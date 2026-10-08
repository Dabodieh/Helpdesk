using Helpdesk.Modules.Identity.Contracts;
using Helpdesk.Modules.Knowledge.Contracts;
using Helpdesk.Modules.Ticketing.Persistence;
using Helpdesk.SharedKernel.Authorization;
using Helpdesk.SharedKernel.Errors;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Modules.Ticketing.Services;

/// <summary>
/// Knowledge articles an agent may link in a PUBLIC reply. Needs tickets.reply on the ticket's department. An article is offered only when the ticket's requester is
/// linked to an active signed-in account (the only person who can open the portal route); an unlinked or external requester gets nothing. Which articles that account can
/// open is decided by Knowledge's normal requester audience rules, restricted to the ticket's own department. An agent being able to see an article proves nothing here.
/// </summary>
internal sealed class ReplyLinkService(TicketResolver resolver, IAuthorizer authorizer, TicketingDbContext db, IUserDirectory users, IReplyLinkArticles articles)
{
    public async Task<IReadOnlyList<ReplyLinkArticle>> SearchAsync(string route, string? q, CancellationToken ct)
    {
        var key = await resolver.FindAsync(route, ct) ?? throw new NotFoundException();
        await authorizer.RequireAsync(PermissionCodes.TicketsReply, key.DepartmentId, ct);
        var linkedUser = await (from t in db.Tickets.AsNoTracking()
                                join l in db.RequesterLinks.AsNoTracking() on t.RequesterId equals l.RequesterId
                                where t.Id == key.Id
                                select (Guid?)l.UserId).SingleOrDefaultAsync(ct);
        if (linkedUser is not { } userId || !(await users.FindAsync(userId, ct) is { IsActive: true }))
        {
            return [];
        }

        return await articles.SearchAsync(key.DepartmentId, userId, q, ct);
    }
}
