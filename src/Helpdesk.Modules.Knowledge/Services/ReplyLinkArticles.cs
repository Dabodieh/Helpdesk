using Helpdesk.Modules.Knowledge.Contracts;
using Helpdesk.Modules.Knowledge.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Modules.Knowledge.Services;

/// <summary>
/// The reply-link picker's article source. It reuses the portal's visibility definition for a requester who is a linked requester of the department, restricted to that
/// department, so nothing is offered that the requester could not open. Results carry only the published title and category (never summary or body).
/// </summary>
internal sealed class ReplyLinkArticles(KnowledgeDbContext db) : IReplyLinkArticles
{
    public const int MaxResults = 10;

    public async Task<IReadOnlyList<ReplyLinkArticle>> SearchAsync(Guid departmentId, string? query, CancellationToken cancellationToken = default)
    {
        var text = query?.Trim();
        if (text is { Length: > 100 })
        {
            text = text[..100];
        }

        var articles = KnowledgePortalService.Visible(db, [departmentId]).Where(a => a.DepartmentId == departmentId);
        var tsQuery = KnowledgePortalService.ToPrefixQuery(text);
        if (!string.IsNullOrEmpty(text) && tsQuery is null)
        {
            return [];
        }

        var rows = tsQuery is null
            ? await articles.OrderByDescending(a => a.PublishedAt).ThenByDescending(a => a.Id).Take(MaxResults).ToListAsync(cancellationToken)
            : await articles.Where(a => a.SearchVector.Matches(EF.Functions.ToTsQuery("english", tsQuery)))
                .OrderByDescending(a => a.SearchVector.Rank(EF.Functions.ToTsQuery("english", tsQuery))).ThenByDescending(a => a.PublishedAt).ThenBy(a => a.Id)
                .Take(MaxResults).ToListAsync(cancellationToken);
        var categoryIds = rows.Where(r => r.CategoryId != null).Select(r => r.CategoryId!.Value).Distinct().ToArray();
        var names = await db.Categories.AsNoTracking().Where(c => categoryIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, cancellationToken);
        return [.. rows.Select(r => new ReplyLinkArticle(r.Slug, r.PublishedTitle!, r.CategoryId is { } c ? names.GetValueOrDefault(c) : null, "/portal/knowledge/" + r.Slug))];
    }
}
