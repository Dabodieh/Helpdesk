using System.Globalization;
using Helpdesk.Modules.Knowledge.Contracts;
using Helpdesk.Modules.Knowledge.Domain;
using Helpdesk.Modules.Knowledge.Persistence;
using Helpdesk.Modules.Organisation.Contracts;
using Helpdesk.SharedKernel.Errors;
using Helpdesk.SharedKernel.Security;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Modules.Knowledge.Services;

internal sealed record KnowledgeCategoryDto(Guid Id, string Name, string? Description, string Department, int ArticleCount);

internal sealed record ArticleCardDto(string Slug, string Title, string Summary, string Department, string? Category);

internal sealed record ArticlePageDto(IReadOnlyList<ArticleCardDto> Items, string? NextCursor);

internal sealed record PortalArticleDto(
    string Slug, string Title, string Summary, string Body, string Department, string? Category, DateTimeOffset PublishedAt, DateTimeOffset UpdatedAt, bool? MyFeedback);

internal sealed record FeedbackRequest(bool? Helpful);

/// <summary>
/// What a signed-in employee can read. Only the PUBLISHED slot of Published articles is ever selected (drafts, archived articles and unpublished edits do not
/// exist here), restricted to the audience of each article. Search is PostgreSQL full text over the published slot; results carry the summary, never a body fragment.
/// </summary>
internal sealed class KnowledgePortalService(KnowledgeDbContext db, IRequesterAudience audience, IOrganisationDirectory organisation, ICurrentUser current, TimeProvider clock)
{
    public const int DefaultLimit = 10;
    public const int MaxLimit = 25;
    public const int MaxOffset = 500;

    private async Task<IQueryable<KnowledgeArticle>> VisibleAsync(CancellationToken ct)
    {
        var requesterDepartments = (await audience.GetRequesterDepartmentIdsAsync(current.UserId, ct)).ToArray();
        return Visible(db, requesterDepartments);
    }

    /// <summary>The one definition of "a requester can open this article": Published, in an active category, and in the requester's audience (Everyone, or Requesters of the article's department).</summary>
    internal static IQueryable<KnowledgeArticle> Visible(KnowledgeDbContext db, Guid[] requesterDepartments) =>
        db.Articles.AsNoTracking().Where(a =>
            a.Status == ArticleStatuses.Published
            && (a.Visibility == ArticleVisibility.Everyone || requesterDepartments.Contains(a.DepartmentId))
            && (a.CategoryId == null || db.Categories.Any(c => c.Id == a.CategoryId && c.IsActive)));

    public async Task<IReadOnlyList<KnowledgeCategoryDto>> CategoriesAsync(CancellationToken ct)
    {
        var visible = await VisibleAsync(ct);
        var counts = await visible.Where(a => a.CategoryId != null).GroupBy(a => a.CategoryId!.Value).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        var categories = await db.Categories.AsNoTracking().Where(c => c.IsActive && counts.Keys.Contains(c.Id)).OrderBy(c => c.SortOrder).ThenBy(c => c.Name).ToListAsync(ct);
        var departments = await organisation.GetDepartmentsAsync(categories.Select(c => c.DepartmentId).Distinct().ToArray(), ct);
        return [.. categories.Select(c => new KnowledgeCategoryDto(c.Id, c.Name, c.Description, departments.TryGetValue(c.DepartmentId, out var d) ? d.Name : "", counts[c.Id]))
            .OrderBy(c => c.Department, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Search (when <paramref name="q"/> is given) or browse (latest first), optionally within one category or department.</summary>
    public async Task<ArticlePageDto> SearchAsync(string? q, Guid? categoryId, Guid? departmentId, string? cursor, int? limit, CancellationToken ct)
    {
        var take = limit ?? DefaultLimit;
        var errors = new Dictionary<string, string[]>();
        if (take is < 1 or > MaxLimit)
        {
            errors["limit"] = [$"limit must be between 1 and {MaxLimit}."];
        }

        var offset = 0;
        if (cursor is not null && (!int.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out offset) || offset > MaxOffset))
        {
            errors["cursor"] = ["Invalid cursor."];
        }

        var query = q?.Trim();
        if (query is { Length: > 100 })
        {
            errors["q"] = ["q is at most 100 characters."];
        }

        if (errors.Count > 0)
        {
            throw new RequestValidationException(errors);
        }

        var articles = await VisibleAsync(ct);
        if (categoryId is { } category)
        {
            articles = articles.Where(a => a.CategoryId == category);
        }

        if (departmentId is { } department)
        {
            articles = articles.Where(a => a.DepartmentId == department);
        }

        List<KnowledgeArticle> rows;
        var tsQuery = ToPrefixQuery(query);
        if (!string.IsNullOrEmpty(query) && tsQuery is null)
        {
            rows = []; // nothing searchable in the text (only punctuation)
        }
        else if (tsQuery is not null)
        {
            rows = await articles.Where(a => a.SearchVector.Matches(EF.Functions.ToTsQuery("english", tsQuery)))
                .OrderByDescending(a => a.SearchVector.Rank(EF.Functions.ToTsQuery("english", tsQuery))).ThenByDescending(a => a.PublishedAt).ThenBy(a => a.Id)
                .Skip(offset).Take(take + 1).ToListAsync(ct);
        }
        else
        {
            rows = await articles.OrderByDescending(a => a.PublishedAt).ThenByDescending(a => a.Id).Skip(offset).Take(take + 1).ToListAsync(ct);
        }

        string? next = null;
        if (rows.Count > take)
        {
            rows.RemoveAt(rows.Count - 1);
            next = (offset + take).ToString(CultureInfo.InvariantCulture);
        }

        var departments = await organisation.GetDepartmentsAsync(rows.Select(r => r.DepartmentId).Distinct().ToArray(), ct);
        var categoryIds = rows.Where(r => r.CategoryId != null).Select(r => r.CategoryId!.Value).Distinct().ToArray();
        var names = await db.Categories.AsNoTracking().Where(c => categoryIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        return new ArticlePageDto(
            [.. rows.Select(r => new ArticleCardDto(r.Slug, r.PublishedTitle!, r.PublishedSummary ?? "", departments.TryGetValue(r.DepartmentId, out var d) ? d.Name : "", r.CategoryId is { } c ? names.GetValueOrDefault(c) : null))],
            next);
    }

    public async Task<PortalArticleDto> GetAsync(string slug, CancellationToken ct)
    {
        var article = await (await VisibleAsync(ct)).SingleOrDefaultAsync(a => a.Slug == slug, ct) ?? throw new NotFoundException();
        var department = (await organisation.GetDepartmentsAsync([article.DepartmentId], ct)).GetValueOrDefault(article.DepartmentId);
        var category = article.CategoryId is { } c ? await db.Categories.AsNoTracking().Where(x => x.Id == c).Select(x => x.Name).SingleOrDefaultAsync(ct) : null;
        var mine = await db.Feedback.AsNoTracking().Where(f => f.ArticleId == article.Id && f.UserId == current.UserId).Select(f => (bool?)f.Helpful).SingleOrDefaultAsync(ct);
        return new PortalArticleDto(article.Slug, article.PublishedTitle!, article.PublishedSummary ?? "", article.PublishedBody!, department?.Name ?? "", category, article.PublishedAt!.Value, article.UpdatedAt, mine);
    }

    public async Task<PortalArticleDto> SetFeedbackAsync(string slug, FeedbackRequest request, CancellationToken ct)
    {
        if (request.Helpful is not { } helpful)
        {
            throw new RequestValidationException("helpful", "helpful must be true or false.");
        }

        var article = await (await VisibleAsync(ct)).SingleOrDefaultAsync(a => a.Slug == slug, ct) ?? throw new NotFoundException();
        var userId = current.UserId;
        var now = clock.GetUtcNow();
        await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO knowledge.article_feedback (article_id, user_id, helpful, updated_at)
            VALUES ({article.Id}, {userId}, {helpful}, {now})
            ON CONFLICT (article_id, user_id) DO UPDATE SET helpful = EXCLUDED.helpful, updated_at = EXCLUDED.updated_at
            """,
            ct);
        return await GetAsync(slug, ct);
    }

    /// <summary>
    /// Turns free text into a safe prefix query ("reset pass" becomes <c>reset:* &amp; pass:*</c>): only letters and digits survive, so no tsquery syntax can
    /// be injected, and partial words match while the requester is still typing.
    /// </summary>
    internal static string? ToPrefixQuery(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();
        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch))
            {
                current.Append(char.ToLowerInvariant(ch));
            }
            else if (current.Length > 0)
            {
                tokens.Add(current.ToString());
                current.Clear();
            }
        }

        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        var usable = tokens.Where(t => t.Length is >= 1 and <= 40).Take(8).Select(t => t + ":*").ToArray();
        return usable.Length == 0 ? null : string.Join(" & ", usable);
    }
}
