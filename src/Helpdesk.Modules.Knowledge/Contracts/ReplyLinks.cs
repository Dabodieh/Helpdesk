namespace Helpdesk.Modules.Knowledge.Contracts;

/// <summary>An article offered for a reply link: only its published title and category, plus the portal path the requester opens.</summary>
public sealed record ReplyLinkArticle(string Slug, string Title, string? Category, string Path);

/// <summary>
/// Articles a department's <b>linked, authenticated requester</b> can open in the portal, for the public-reply link picker. The caller (Ticketing) has already
/// established that the ticket's requester is linked to a signed-in account of this department; this applies the normal requester audience rules for that
/// requester and never returns drafts, archived articles or other departments' articles.
/// </summary>
public interface IReplyLinkArticles
{
    Task<IReadOnlyList<ReplyLinkArticle>> SearchAsync(Guid departmentId, string? query, CancellationToken cancellationToken = default);
}
