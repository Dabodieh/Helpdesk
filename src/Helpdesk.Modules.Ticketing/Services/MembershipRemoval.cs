using System.Data.Common;
using Helpdesk.Modules.Ticketing.Contracts;
using Helpdesk.Modules.Ticketing.Domain;
using Helpdesk.Modules.Ticketing.Persistence;
using Helpdesk.SharedKernel.Database;
using Helpdesk.SharedKernel.Security;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Modules.Ticketing.Services;

/// <summary>
/// What removing a department member does to the tickets assigned to them. It runs INSIDE the removal transaction (it joins Organisation's connection and transaction),
/// after the membership row is locked, so it is all-or-nothing with the removal: no crash window can leave the member removed without the records, a failure aborts the
/// removal, and a retry cannot record twice (the first attempt either committed everything or nothing). Assignments in flight when the removal starts are waited for (they
/// hold a key-share lock on the membership through the foreign key) and included; none can start afterwards.
/// For every ticket assigned to the member it unassigns the ticket explicitly (the foreign key would otherwise do it silently), writes one <c>ticket.assignee_changed</c>
/// audit entry (previous assignee, the removing administrator as actor, reason <c>department_membership_removed</c>; this also feeds the ticket activity and the
/// content-free lifecycle record), resets the "assigned to me" bookkeeping, and stages notification-outbox items for at most <see cref="MaxNotifiedTickets"/> open tickets.
/// The durable outbox drain turns those into in-app notifications for everyone who can reassign tickets (<c>tickets.assign.any</c>), never the acting administrator.
/// The ticket's team, status and department are untouched; its concurrency version and updated time change like any assignment.
/// Only the realtime refresh happens after commit and is best-effort (clients also poll).
/// </summary>
internal sealed class MembershipRemovalHandler(TicketingDbContext db, TicketingAudit audit, ITicketEvents events, ICurrentUser current, TimeProvider clock) : IMembershipRemovalListener
{
    public const string Reason = "department_membership_removed";
    public const int MaxNotifiedTickets = 5;

    private sealed record UnassignedTicket(Guid Id, string Reference, bool IsOpen);

    private List<UnassignedTicket> _done = [];

    public async Task MembershipRemovingAsync(Guid departmentId, Guid userId, DbTransaction transaction, CancellationToken cancellationToken = default)
    {
        // Join the removal transaction: same connection, same transaction. Nothing here commits it.
        db.Database.SetDbConnection(transaction.Connection!);
        await db.Database.UseTransactionAsync(transaction, cancellationToken);

        var now = ClockExtensions.GetUtcNowMicroseconds(clock);
        // One atomic statement: it locks and clears exactly the tickets assigned to the member right now and returns them (a concurrent edit of the same row is waited for).
        var rows = await db.Database.SqlQuery<UnassignedTicket>(
            $"""
            UPDATE ticketing.tickets SET assignee_user_id = NULL, updated_at = {now}
            WHERE department_id = {departmentId} AND assignee_user_id = {userId}
            RETURNING id AS "Id", reference AS "Reference", (merged_into_ticket_id IS NULL AND status IN ('Open', 'Pending')) AS "IsOpen"
            """).ToListAsync(cancellationToken);
        if (rows.Count == 0)
        {
            return;
        }

        foreach (var ticket in rows)
        {
            await audit.WriteAsync(TicketAuditActions.AssigneeChanged, TicketingAudit.TicketObject, ticket.Id, departmentId,
                new { assigneeUserId = userId }, new { assigneeUserId = (Guid?)null, reason = Reason }, cancellationToken);
        }

        // The "assigned to me" bookkeeping must forget the member, so a later assignment to them notifies as a change.
        var ids = rows.Select(r => r.Id).ToArray();
        await db.TicketNotifyStates.IgnoreDepartmentScope().Where(s => ids.Contains(s.TicketId) && s.AssigneeUserId == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.AssigneeUserId, (Guid?)null), cancellationToken);

        foreach (var ticket in rows.Where(r => r.IsOpen).OrderBy(r => r.Id).Take(MaxNotifiedTickets))
        {
            db.NotificationOutbox.Add(new NotificationOutboxItem
            {
                Id = Guid.CreateVersion7(), DepartmentId = departmentId, TicketId = ticket.Id, Kind = NotificationTypes.Unassigned,
                ActorUserId = current.IsAuthenticated ? current.UserId : null, SubjectUserId = userId, CreatedAt = now,
            });
        }

        await db.SaveChangesAsync(cancellationToken);
        _done = rows;
    }

    public async Task MembershipRemovedAsync(Guid departmentId, Guid userId, CancellationToken cancellationToken = default)
    {
        var done = _done;
        _done = [];
        // Realtime refresh only (best-effort); the first publish also nudges the notification outbox, which a recurring job drains regardless.
        foreach (var ticket in done)
        {
            await events.PublishAsync(departmentId, ticket.Id, ticket.Reference, TicketEventKinds.Assignment, current.IsAuthenticated ? current.UserId : null, cancellationToken);
        }
    }
}
