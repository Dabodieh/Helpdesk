using Helpdesk.Modules.Organisation.Contracts;
using Helpdesk.Modules.Ticketing.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Modules.Ticketing.Services;

/// <summary>
/// Makes "may this member own tickets" and "assign this ticket to them" one atomic decision. The check (<see cref="IOrganisationDirectory.IsEligibleAssigneeAsync"/>:
/// role holds tickets.update, user active) reads the CURRENT role, and the foreign key on the ticket only checks that a membership exists, so without a lock a role change
/// to Viewer can commit between the check and the ticket update and leave the ticket assigned to someone who cannot work it. Here the membership row is locked
/// <c>FOR KEY SHARE</c> first and held until the caller's transaction ends: a role change or removal (which lock the row <c>FOR UPDATE</c> before changing it) either waits
/// for this transaction and then unassigns the ticket it just assigned, or has already committed, in which case the check below reads the new role and refuses.
/// Many assignments can hold the share lock at once; only the rare role change or removal of that very member waits.
/// </summary>
internal static class AssigneeLock
{
    /// <summary>True when the member may own tickets in the department. Must run inside the transaction that will save the assignment.</summary>
    public static async Task<bool> HoldAndCheckAsync(TicketingDbContext db, IOrganisationDirectory organisation, Guid departmentId, Guid userId, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("An assignee can only be locked inside the transaction that saves the assignment.");
        }

        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM organisation.department_memberships WHERE department_id = {departmentId} AND user_id = {userId} FOR KEY SHARE", ct);
        return await organisation.IsEligibleAssigneeAsync(departmentId, userId, ct);
    }
}
