using System.Data.Common;
using Helpdesk.Modules.Ticketing.Contracts;
using Helpdesk.Modules.Ticketing.Domain;
using Helpdesk.Modules.Ticketing.Persistence;
using Helpdesk.SharedKernel.Database;
using Helpdesk.SharedKernel.Security;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Modules.Ticketing.Services;

/// <summary>
/// What happens to the tickets assigned to an account that is being deactivated: they become unassigned, in every department, in the SAME transaction as the
/// deactivation (it joins Identity's connection and transaction, like <see cref="MembershipRemovalHandler"/>), so nothing can stay assigned to an account that can no
/// longer act and a failure aborts the deactivation. One <c>ticket.assignee_changed</c> audit entry per ticket (previous assignee, the administrator as actor, reason
/// <see cref="UserAssignmentEndReasons.UserDeactivated"/>), the "assigned to me" bookkeeping is reset, and open tickets stage a notification for those who can reassign
/// (at most <see cref="MaxNotifiedPerDepartment"/> per department). Status, department and history are untouched; the department memberships stay (history, and needed
/// again on reactivation). Only the realtime refresh happens after commit and is best-effort.
/// </summary>
internal sealed class UserDeactivationHandler(TicketingDbContext db, TicketingAudit audit, ITicketEvents events, ICurrentUser current, TimeProvider clock) : IUserDeactivationListener
{
    public const int MaxNotifiedPerDepartment = 5;

    private sealed record Released(Guid Id, string Reference, Guid DepartmentId, bool IsOpen);

    private List<Released> _done = [];

    public async Task UserDeactivatingAsync(Guid userId, string reason, DbTransaction transaction, CancellationToken cancellationToken = default)
    {
        db.Database.SetDbConnection(transaction.Connection!);
        await db.Database.UseTransactionAsync(transaction, cancellationToken);

        var now = ClockExtensions.GetUtcNowMicroseconds(clock);
        var rows = await db.Database.SqlQuery<Released>(
            $"""
            UPDATE ticketing.tickets SET assignee_user_id = NULL, updated_at = {now}
            WHERE assignee_user_id = {userId}
            RETURNING id AS "Id", reference AS "Reference", department_id AS "DepartmentId", (merged_into_ticket_id IS NULL AND status IN ('Open', 'Pending')) AS "IsOpen"
            """).ToListAsync(cancellationToken);
        if (rows.Count == 0)
        {
            return;
        }

        foreach (var ticket in rows)
        {
            await audit.WriteAsync(TicketAuditActions.AssigneeChanged, TicketingAudit.TicketObject, ticket.Id, ticket.DepartmentId,
                new { assigneeUserId = userId }, new { assigneeUserId = (Guid?)null, reason }, cancellationToken);
        }

        var ids = rows.Select(r => r.Id).ToArray();
        await db.TicketNotifyStates.IgnoreDepartmentScope().Where(s => ids.Contains(s.TicketId) && s.AssigneeUserId == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.AssigneeUserId, (Guid?)null), cancellationToken);

        foreach (var group in rows.Where(r => r.IsOpen).GroupBy(r => r.DepartmentId))
        {
            foreach (var ticket in group.OrderBy(r => r.Id).Take(MaxNotifiedPerDepartment))
            {
                db.NotificationOutbox.Add(new NotificationOutboxItem
                {
                    Id = Guid.CreateVersion7(), DepartmentId = ticket.DepartmentId, TicketId = ticket.Id, Kind = NotificationTypes.Unassigned,
                    ActorUserId = current.IsAuthenticated ? current.UserId : null, SubjectUserId = userId, CreatedAt = now,
                });
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        _done = rows;
    }

    public async Task UserDeactivatedAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var done = _done;
        _done = [];
        foreach (var ticket in done)
        {
            await events.PublishAsync(ticket.DepartmentId, ticket.Id, ticket.Reference, TicketEventKinds.Assignment, current.IsAuthenticated ? current.UserId : null, cancellationToken);
        }
    }
}
