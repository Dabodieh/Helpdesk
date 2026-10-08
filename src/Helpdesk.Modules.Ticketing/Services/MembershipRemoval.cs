using Helpdesk.Modules.Ticketing.Contracts;
using Helpdesk.Modules.Ticketing.Domain;
using Helpdesk.Modules.Ticketing.Persistence;
using Helpdesk.SharedKernel.Database;
using Helpdesk.SharedKernel.Security;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Modules.Ticketing.Services;

/// <summary>
/// What removing a department member does to the tickets assigned to them. The database clears the assignee when the membership disappears (composite foreign key,
/// TICKETING.md invariant 4); this makes that visible: one <c>ticket.assignee_changed</c> audit entry per affected ticket (previous assignee, the removing administrator as
/// actor, reason <c>department_membership_removed</c>, which also feeds the ticket activity and the content-free lifecycle record), a realtime refresh, and an in-app
/// notification to everyone who can reassign tickets in the department (<c>tickets.assign.any</c>: team leads and administrators), never to the administrator who acted.
/// Notifications are limited to <see cref="MaxNotifiedTickets"/> open tickets per removal so a member with a long queue does not flood the leads; the others are in the
/// ticket activity and the Unassigned queue (the notification list holds one entry per ticket, so a grouped summary would need a schema change).
/// Capture happens inside the removal transaction (<see cref="BeforeMembershipRemovedAsync"/>), recording after it committed (<see cref="MembershipRemovedAsync"/>): a removal that
/// fails records nothing, and a removal cannot be repeated (the membership is gone), so nothing is recorded twice. Tickets that were not assigned to the member are untouched.
/// Known limit: a crash between the commit and the recording loses the activity entries (the tickets are unassigned either way).
/// </summary>
internal sealed class MembershipRemovalHandler(TicketingDbContext db, TicketingAudit audit, ITicketEvents events, ICurrentUser current, TimeProvider clock) : IMembershipRemovalListener
{
    public const string Reason = "department_membership_removed";
    public const int MaxNotifiedTickets = 5;

    private sealed record Captured(Guid TicketId, string Reference, bool Open);

    private List<Captured>? _captured;

    public async Task BeforeMembershipRemovedAsync(Guid departmentId, Guid userId, CancellationToken cancellationToken = default) =>
        _captured = await db.Tickets.AsNoTracking().IgnoreDepartmentScope()
            .Where(t => t.DepartmentId == departmentId && t.AssigneeUserId == userId)
            .OrderBy(t => t.CreatedAt).ThenBy(t => t.Id)
            .Select(t => new Captured(t.Id, t.Reference, t.MergedIntoTicketId == null && (t.Status == TicketStatus.Open || t.Status == TicketStatus.Pending)))
            .ToListAsync(cancellationToken);

    public async Task MembershipRemovedAsync(Guid departmentId, Guid userId, CancellationToken cancellationToken = default)
    {
        var captured = _captured;
        _captured = null; // consumed: a second call records nothing
        if (captured is not { Count: > 0 })
        {
            return;
        }

        // Only tickets that really lost the assignee (and still belong to this department) are recorded.
        var ids = captured.Select(c => c.TicketId).ToArray();
        var cleared = (await db.Tickets.AsNoTracking().IgnoreDepartmentScope()
            .Where(t => ids.Contains(t.Id) && t.DepartmentId == departmentId && t.AssigneeUserId == null).Select(t => t.Id).ToListAsync(cancellationToken)).ToHashSet();
        var affected = captured.Where(c => cleared.Contains(c.TicketId)).ToList();
        if (affected.Count == 0)
        {
            return;
        }

        var now = ClockExtensions.GetUtcNowMicroseconds(clock);
        await using (var tx = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            foreach (var ticket in affected)
            {
                await audit.WriteAsync(TicketAuditActions.AssigneeChanged, TicketingAudit.TicketObject, ticket.TicketId, departmentId,
                    new { assigneeUserId = userId }, new { assigneeUserId = (Guid?)null, reason = Reason }, cancellationToken);
            }

            // The "assigned to me" bookkeeping must forget the member, so a later assignment to them notifies as a change.
            var affectedIds = affected.Select(a => a.TicketId).ToArray();
            await db.TicketNotifyStates.IgnoreDepartmentScope().Where(s => affectedIds.Contains(s.TicketId) && s.AssigneeUserId == userId)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.AssigneeUserId, (Guid?)null), cancellationToken);

            foreach (var ticket in affected.Where(a => a.Open).Take(MaxNotifiedTickets))
            {
                db.NotificationOutbox.Add(new NotificationOutboxItem
                {
                    Id = Guid.CreateVersion7(), DepartmentId = departmentId, TicketId = ticket.TicketId, Kind = NotificationTypes.Unassigned,
                    ActorUserId = current.IsAuthenticated ? current.UserId : null, SubjectUserId = userId, CreatedAt = now,
                });
            }

            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
        }

        // Realtime refresh (best-effort); the first publish also drains the notification outbox.
        foreach (var ticket in affected)
        {
            await events.PublishAsync(departmentId, ticket.TicketId, ticket.Reference, TicketEventKinds.Assignment, current.IsAuthenticated ? current.UserId : null, cancellationToken);
        }
    }
}
