namespace Helpdesk.Modules.Knowledge.Contracts;

/// <summary>An article offered for a reply link: only its published title and category, plus the portal path the requester opens.</summary>
public sealed record ReplyLinkArticle(string Slug, string Title, string? Category, string Path);

/// <summary>
/// Articles a department's <b>linked, authenticated requester</b> can open in the portal, for the public-reply link picker. The precondition is part of the call:
/// <paramref name="linkedUserId"/> must be the account that a Ticketing requester link of this department points at (Ticketing establishes that; an empty id yields
/// nothing). The normal requester audience rules apply, and drafts, archived articles and other departments' articles are never returned.
/// </summary>
public interface IReplyLinkArticles
{
    Task<IReadOnlyList<ReplyLinkArticle>> SearchAsync(Guid departmentId, Guid linkedUserId, string? query, CancellationToken cancellationToken = default);
}
