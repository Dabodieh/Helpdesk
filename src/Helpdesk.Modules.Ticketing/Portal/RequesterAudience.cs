using Helpdesk.Modules.Knowledge.Contracts;
using Helpdesk.Modules.Ticketing.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Modules.Ticketing.Portal;

/// <summary>Knowledge's view of "requesters of a department": the departments of the requesters linked to the signed-in user.</summary>
internal sealed class RequesterAudience(TicketingDbContext db, RequesterIdentityService identity) : IRequesterAudience
{
    public async Task<IReadOnlyCollection<Guid>> GetRequesterDepartmentIdsAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        await identity.RequesterIdsAsync(cancellationToken); // claims unclaimed requesters that match the verified address first
        return await db.RequesterLinks.AsNoTracking().Where(l => l.UserId == userId).Select(l => l.DepartmentId).Distinct().ToListAsync(cancellationToken);
    }
}
