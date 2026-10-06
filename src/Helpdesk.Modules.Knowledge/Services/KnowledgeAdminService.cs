using System.Globalization;
using System.Text.RegularExpressions;
using Helpdesk.Modules.Audit.Contracts;
using Helpdesk.Modules.Knowledge.Domain;
using Helpdesk.Modules.Knowledge.Persistence;
using Helpdesk.SharedKernel.Database;
using Helpdesk.SharedKernel.Errors;
using Helpdesk.SharedKernel.Security;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Helpdesk.Modules.Knowledge.Services;

internal sealed class KnowledgeAudit(IAuditWriter writer, KnowledgeDbContext db, ICurrentUser current)
{
    public const string CategoryObject = "knowledge_category";
    public const string ArticleObject = "knowledge_article";

    public Task WriteAsync(string action, string objectType, Guid objectId, Guid departmentId, object? next, CancellationToken ct) =>
        writer.WriteAsync(new AuditEntry(AuditCategories.Knowledge, action, objectType, objectId, departmentId, current.UserId, current.DisplayName, null, next), db, ct);
}

internal sealed record CategoryRequest(string? Name, string? Description, bool? IsActive, string? Version);

internal sealed record CategoryAdminDto(Guid Id, Guid DepartmentId, string Name, string? Description, int Order, bool IsActive, int ArticleCount, string Version);

internal sealed record ArticleRequest(string? Title, string? Summary, string? Body, Guid? CategoryId, bool? ClearCategory, string? Visibility, string? Version);

internal sealed record ArticleSlot(string Title, string Summary, string Body);

internal sealed record ArticleListItemDto(
    Guid Id, string Slug, string Title, string Status, string Visibility, Guid? CategoryId, string? Category, bool HasUnpublishedChanges, DateTimeOffset UpdatedAt, DateTimeOffset? PublishedAt);

internal sealed record ArticleAdminDto(
    Guid Id, Guid DepartmentId, string Slug, string Status, string Visibility, Guid? CategoryId, ArticleSlot Draft, DateTimeOffset DraftUpdatedAt, ArticleSlot? Published,
    DateTimeOffset? PublishedAt, bool HasUnpublishedChanges, int Helpful, int NotHelpful, string Version);

internal sealed record IdsRequest(IReadOnlyList<Guid>? Ids);

internal sealed record VersionRequest(string? Version);

/// <summary>
/// Knowledge administration (permission department.knowledge.manage, enforced by the endpoint filter; every query also filters by the department).
/// Editing writes the draft slot only; publishing is an explicit step that copies the draft to the published slot, so public content never changes half-edited.
/// </summary>
internal sealed partial class KnowledgeAdminService(KnowledgeDbContext db, KnowledgeAudit audit, ICurrentUser current, TimeProvider clock)
{
    public const int MaxCategories = 50;
    public const int MaxBody = 50_000;
    public const int PageSize = 25;

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonSlug();

    // ----- categories -----

    public async Task<IReadOnlyList<CategoryAdminDto>> ListCategoriesAsync(Guid departmentId, CancellationToken ct)
    {
        var rows = await db.Categories.AsNoTracking().Where(c => c.DepartmentId == departmentId).OrderBy(c => c.SortOrder).ThenBy(c => c.Name).ToListAsync(ct);
        var counts = await db.Articles.AsNoTracking().Where(a => a.DepartmentId == departmentId && a.CategoryId != null).GroupBy(a => a.CategoryId!.Value).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        return [.. rows.Select(c => new CategoryAdminDto(c.Id, c.DepartmentId, c.Name, c.Description, c.SortOrder, c.IsActive, counts.GetValueOrDefault(c.Id), ConcurrencyVersion.ToToken(c.Version)))];
    }

    public async Task<CategoryAdminDto> CreateCategoryAsync(Guid departmentId, CategoryRequest request, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        var (name, description) = ValidateCategory(errors, request, creating: true);
        ThrowIfAny(errors);
        if (await db.Categories.CountAsync(c => c.DepartmentId == departmentId, ct) >= MaxCategories)
        {
            throw new ConflictException($"A department can keep at most {MaxCategories} categories.", ProblemCodes.InvalidState);
        }

        var now = clock.GetUtcNow();
        var last = await db.Categories.Where(c => c.DepartmentId == departmentId).MaxAsync(c => (int?)c.SortOrder, ct) ?? 0;
        var row = new KnowledgeCategory { Id = Guid.CreateVersion7(), DepartmentId = departmentId, Name = name!, Description = description, SortOrder = last + 10, IsActive = request.IsActive ?? true, CreatedAt = now, UpdatedAt = now };
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.Categories.Add(row);
        await SaveAsync("A category with this name already exists.", ct);
        await audit.WriteAsync("knowledge.category_created", KnowledgeAudit.CategoryObject, row.Id, departmentId, new { name = row.Name }, ct);
        await tx.CommitAsync(ct);
        return (await ListCategoriesAsync(departmentId, ct)).Single(c => c.Id == row.Id);
    }

    public async Task<CategoryAdminDto> UpdateCategoryAsync(Guid departmentId, Guid id, CategoryRequest request, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        var version = ParseVersion(errors, request.Version);
        var (name, description) = ValidateCategory(errors, request, creating: false);
        ThrowIfAny(errors);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var row = await db.Categories.SingleOrDefaultAsync(c => c.Id == id && c.DepartmentId == departmentId, ct) ?? throw new NotFoundException();
        CheckVersion(row.Version, version, "category");
        db.Entry(row).Property(c => c.Version).OriginalValue = version;
        row.Name = name ?? row.Name;
        if (request.Description is not null)
        {
            row.Description = description;
        }

        row.IsActive = request.IsActive ?? row.IsActive;
        row.UpdatedAt = clock.GetUtcNow();
        await SaveAsync("A category with this name already exists.", ct);
        await audit.WriteAsync("knowledge.category_updated", KnowledgeAudit.CategoryObject, row.Id, departmentId, new { name = row.Name, isActive = row.IsActive }, ct);
        await tx.CommitAsync(ct);
        return (await ListCategoriesAsync(departmentId, ct)).Single(c => c.Id == id);
    }

    public async Task<IReadOnlyList<CategoryAdminDto>> ReorderCategoriesAsync(Guid departmentId, IdsRequest request, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var rows = await db.Categories.Where(c => c.DepartmentId == departmentId).ToListAsync(ct);
        var ids = request.Ids ?? [];
        if (ids.Count != rows.Count || ids.Distinct().Count() != ids.Count || !ids.All(i => rows.Any(r => r.Id == i)))
        {
            throw new RequestValidationException(new Dictionary<string, string[]> { ["ids"] = ["List every category of the department exactly once."] });
        }

        for (var i = 0; i < ids.Count; i++)
        {
            var row = rows.Single(r => r.Id == ids[i]);
            if (row.SortOrder != (i + 1) * 10)
            {
                row.SortOrder = (i + 1) * 10;
                row.UpdatedAt = clock.GetUtcNow();
            }
        }

        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("knowledge.category_updated", KnowledgeAudit.CategoryObject, departmentId, departmentId, new { reordered = ids.Count }, ct);
        await tx.CommitAsync(ct);
        return await ListCategoriesAsync(departmentId, ct);
    }

    // ----- articles -----

    public async Task<IReadOnlyList<ArticleListItemDto>> ListArticlesAsync(Guid departmentId, string? status, Guid? categoryId, string? q, int? page, CancellationToken ct)
    {
        var query = db.Articles.AsNoTracking().Where(a => a.DepartmentId == departmentId);
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (status is not (ArticleStatuses.Draft or ArticleStatuses.Published or ArticleStatuses.Archived))
            {
                throw new RequestValidationException("status", "status must be Draft, Published or Archived.");
            }

            query = query.Where(a => a.Status == status);
        }

        if (categoryId is { } category)
        {
            query = query.Where(a => a.CategoryId == category);
        }

        if (!string.IsNullOrWhiteSpace(q))
        {
            var like = "%" + EscapeLike(q.Trim()) + "%";
            query = query.Where(a => EF.Functions.ILike(a.DraftTitle, like, "\\") || EF.Functions.ILike(a.DraftSummary, like, "\\"));
        }

        var skip = Math.Max(0, (page ?? 0)) * PageSize;
        var rows = await query.OrderByDescending(a => a.UpdatedAt).ThenBy(a => a.Id).Skip(skip).Take(PageSize).ToListAsync(ct);
        var names = await db.Categories.AsNoTracking().Where(c => c.DepartmentId == departmentId).ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        return [.. rows.Select(a => new ArticleListItemDto(a.Id, a.Slug, a.DraftTitle, a.Status, a.Visibility, a.CategoryId, a.CategoryId is { } c ? names.GetValueOrDefault(c) : null, Unpublished(a), a.UpdatedAt, a.PublishedAt))];
    }

    public async Task<ArticleAdminDto> GetArticleAsync(Guid departmentId, Guid id, CancellationToken ct) =>
        await ToAdminAsync(await db.Articles.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id && a.DepartmentId == departmentId, ct) ?? throw new NotFoundException(), ct);

    public async Task<ArticleAdminDto> CreateArticleAsync(Guid departmentId, ArticleRequest request, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        var slot = ValidateSlot(errors, request, creating: true);
        var visibility = ValidateVisibility(errors, request.Visibility) ?? ArticleVisibility.Everyone;
        ThrowIfAny(errors);
        await CheckCategoryAsync(departmentId, request.CategoryId, ct);

        var now = clock.GetUtcNow();
        var article = new KnowledgeArticle
        {
            Id = Guid.CreateVersion7(),
            DepartmentId = departmentId,
            CategoryId = request.CategoryId,
            Slug = await UniqueSlugAsync(slot!.Title, ct),
            Status = ArticleStatuses.Draft,
            Visibility = visibility,
            DraftTitle = slot.Title,
            DraftSummary = slot.Summary,
            DraftBody = slot.Body,
            DraftUpdatedAt = now,
            DraftUpdatedByUserId = current.UserId,
            AuthorUserId = current.UserId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.Articles.Add(article);
        await SaveAsync("An article with this address already exists.", ct);
        await audit.WriteAsync("knowledge.article_created", KnowledgeAudit.ArticleObject, article.Id, departmentId, new { title = article.DraftTitle, slug = article.Slug }, ct);
        await tx.CommitAsync(ct);
        return await ToAdminAsync(article, ct);
    }

    /// <summary>Saves the DRAFT. A published article keeps serving its published content until Publish is called.</summary>
    public async Task<ArticleAdminDto> SaveDraftAsync(Guid departmentId, Guid id, ArticleRequest request, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        var version = ParseVersion(errors, request.Version);
        var slot = ValidateSlot(errors, request, creating: false);
        var visibility = ValidateVisibility(errors, request.Visibility);
        ThrowIfAny(errors);
        await CheckCategoryAsync(departmentId, request.CategoryId, ct);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var article = await db.Articles.SingleOrDefaultAsync(a => a.Id == id && a.DepartmentId == departmentId, ct) ?? throw new NotFoundException();
        CheckVersion(article.Version, version, "article");
        db.Entry(article).Property(a => a.Version).OriginalValue = version;
        if (request.Title is not null)
        {
            article.DraftTitle = slot!.Title;
        }

        if (request.Summary is not null)
        {
            article.DraftSummary = slot!.Summary;
        }

        if (request.Body is not null)
        {
            article.DraftBody = slot!.Body;
        }

        article.Visibility = visibility ?? article.Visibility;
        article.CategoryId = request.ClearCategory == true ? null : request.CategoryId ?? article.CategoryId;
        var now = clock.GetUtcNow();
        article.DraftUpdatedAt = now;
        article.DraftUpdatedByUserId = current.UserId;
        article.UpdatedAt = now;
        await SaveAsync("Conflict.", ct);
        await audit.WriteAsync("knowledge.article_updated", KnowledgeAudit.ArticleObject, article.Id, departmentId, new { title = article.DraftTitle, status = article.Status }, ct);
        await tx.CommitAsync(ct);
        return await ToAdminAsync(article, ct);
    }

    public Task<ArticleAdminDto> PublishAsync(Guid departmentId, Guid id, VersionRequest request, CancellationToken ct) =>
        ChangeStateAsync(departmentId, id, request, publish: true, ct);

    public Task<ArticleAdminDto> ArchiveAsync(Guid departmentId, Guid id, VersionRequest request, CancellationToken ct) =>
        ChangeStateAsync(departmentId, id, request, publish: false, ct);

    private async Task<ArticleAdminDto> ChangeStateAsync(Guid departmentId, Guid id, VersionRequest request, bool publish, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        var version = ParseVersion(errors, request.Version);
        ThrowIfAny(errors);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var article = await db.Articles.SingleOrDefaultAsync(a => a.Id == id && a.DepartmentId == departmentId, ct) ?? throw new NotFoundException();
        CheckVersion(article.Version, version, "article");
        db.Entry(article).Property(a => a.Version).OriginalValue = version;
        var now = clock.GetUtcNow();
        if (publish)
        {
            if (string.IsNullOrWhiteSpace(article.DraftTitle) || string.IsNullOrWhiteSpace(article.DraftBody))
            {
                throw new RequestValidationException(new Dictionary<string, string[]> { ["body"] = ["An article needs a title and a body before it can be published."] });
            }

            article.PublishedTitle = article.DraftTitle;
            article.PublishedSummary = article.DraftSummary;
            article.PublishedBody = article.DraftBody;
            article.PublishedAt = now;
            article.PublishedByUserId = current.UserId;
            article.Status = ArticleStatuses.Published;
        }
        else
        {
            if (article.Status != ArticleStatuses.Published)
            {
                throw new ConflictException("Only a published article can be archived.", ProblemCodes.InvalidState);
            }

            article.Status = ArticleStatuses.Archived;
        }

        article.UpdatedAt = now;
        await SaveAsync("Conflict.", ct);
        await audit.WriteAsync(publish ? "knowledge.article_published" : "knowledge.article_archived", KnowledgeAudit.ArticleObject, article.Id, departmentId, new { title = article.DraftTitle, slug = article.Slug }, ct);
        await tx.CommitAsync(ct);
        return await ToAdminAsync(article, ct);
    }

    // ----- helpers -----

    private static bool Unpublished(KnowledgeArticle a) =>
        a.Status != ArticleStatuses.Draft && (a.DraftTitle != a.PublishedTitle || a.DraftSummary != a.PublishedSummary || a.DraftBody != a.PublishedBody);

    private async Task<ArticleAdminDto> ToAdminAsync(KnowledgeArticle a, CancellationToken ct)
    {
        var votes = await db.Feedback.AsNoTracking().Where(f => f.ArticleId == a.Id).GroupBy(f => f.Helpful).Select(g => new { g.Key, N = g.Count() }).ToListAsync(ct);
        return new ArticleAdminDto(
            a.Id, a.DepartmentId, a.Slug, a.Status, a.Visibility, a.CategoryId, new ArticleSlot(a.DraftTitle, a.DraftSummary, a.DraftBody), a.DraftUpdatedAt,
            a.PublishedTitle is null ? null : new ArticleSlot(a.PublishedTitle, a.PublishedSummary ?? "", a.PublishedBody ?? ""), a.PublishedAt, Unpublished(a),
            votes.FirstOrDefault(v => v.Key)?.N ?? 0, votes.FirstOrDefault(v => !v.Key)?.N ?? 0, ConcurrencyVersion.ToToken(a.Version));
    }

    private static ArticleSlot? ValidateSlot(Dictionary<string, string[]> errors, ArticleRequest r, bool creating)
    {
        string? title = null;
        if (r.Title is not null || creating)
        {
            title = r.Title?.Trim();
            if (string.IsNullOrEmpty(title) || title.Length > 200)
            {
                errors["title"] = ["Title must be 1 to 200 characters."];
            }
        }

        var summary = r.Summary?.Trim();
        if (summary is { Length: > 500 })
        {
            errors["summary"] = ["Summary is at most 500 characters."];
        }

        var body = r.Body?.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (body is { Length: > MaxBody } || body?.Contains('\0', StringComparison.Ordinal) == true)
        {
            errors["body"] = [$"Body is at most {MaxBody} characters."];
        }

        return errors.Count > 0 ? null : new ArticleSlot(title ?? "", summary ?? "", body ?? "");
    }

    private static string? ValidateVisibility(Dictionary<string, string[]> errors, string? visibility)
    {
        if (visibility is null)
        {
            return null;
        }

        if (!ArticleVisibility.All.Contains(visibility, StringComparer.Ordinal))
        {
            errors["visibility"] = ["visibility must be Everyone or Requesters."];
            return null;
        }

        return visibility;
    }

    private static (string? Name, string? Description) ValidateCategory(Dictionary<string, string[]> errors, CategoryRequest r, bool creating)
    {
        string? name = null;
        if (r.Name is not null || creating)
        {
            name = r.Name?.Trim();
            if (string.IsNullOrEmpty(name) || name.Length > 100)
            {
                errors["name"] = ["Name must be 1 to 100 characters."];
                name = null;
            }
        }

        var description = r.Description?.Trim();
        if (description is { Length: > 500 })
        {
            errors["description"] = ["Description is at most 500 characters."];
        }

        return (name, string.IsNullOrEmpty(description) ? null : description);
    }

    private async Task CheckCategoryAsync(Guid departmentId, Guid? categoryId, CancellationToken ct)
    {
        if (categoryId is { } id && !await db.Categories.AnyAsync(c => c.Id == id && c.DepartmentId == departmentId, ct))
        {
            throw new RequestValidationException("categoryId", "Choose a category of this department.");
        }
    }

    private async Task<string> UniqueSlugAsync(string title, CancellationToken ct)
    {
        var baseSlug = NonSlug().Replace(title.ToLowerInvariant(), "-").Trim('-');
        if (baseSlug.Length == 0)
        {
            baseSlug = "article";
        }

        baseSlug = baseSlug.Length > 70 ? baseSlug[..70].TrimEnd('-') : baseSlug;
        var slug = baseSlug;
        for (var n = 2; await db.Articles.AnyAsync(a => a.Slug == slug, ct); n++)
        {
            slug = string.Create(CultureInfo.InvariantCulture, $"{baseSlug}-{n}");
        }

        return slug;
    }

    private static string EscapeLike(string text) => text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);

    private static uint ParseVersion(Dictionary<string, string[]> errors, string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            errors["version"] = ["version is required."];
            return 0;
        }

        if (!ConcurrencyVersion.TryParse(version, out var parsed))
        {
            errors["version"] = ["version is not valid."];
            return 0;
        }

        return parsed;
    }

    private static void CheckVersion(uint actual, uint expected, string what)
    {
        if (actual != expected)
        {
            throw new ConflictException($"The {what} was changed elsewhere. Reload and retry.", ProblemCodes.VersionConflict);
        }
    }

    private static void ThrowIfAny(Dictionary<string, string[]> errors)
    {
        if (errors.Count > 0)
        {
            throw new RequestValidationException(errors);
        }
    }

    private async Task SaveAsync(string duplicateMessage, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictException("The item was changed elsewhere. Reload and retry.", ProblemCodes.VersionConflict);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw new ConflictException(duplicateMessage, ProblemCodes.Duplicate);
        }
    }
}
