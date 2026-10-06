using Helpdesk.Modules.Ticketing.Contracts;
using Helpdesk.Modules.Ticketing.Domain;
using Helpdesk.Modules.Ticketing.Persistence;
using Helpdesk.Modules.Ticketing.Services;
using Helpdesk.Modules.Ticketing.Sla;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Helpdesk.Modules.Ticketing.Automation;

internal static class Provenances
{
    public const string User = "user";
    public const string Email = "email";
    public const string Sla = "sla";
    public const string Schedule = "schedule";
}

/// <summary>
/// The two hooks every ticket-changing path calls: <see cref="BeforeCommitAsync"/> inside the transaction (SLA clocks change atomically
/// with the ticket) and <see cref="AfterCommitAsync"/> once it committed (automation reacts; SLA changes reach the open workspaces).
/// Keeping them here means no controller or service decides SLA or automation behaviour on its own.
/// </summary>
internal sealed class TicketLifecycle(TicketingDbContext db, SlaEngine sla, AutomationEngine automation, ITicketEvents events, TimeProvider clock)
{
    public async Task BeforeCommitAsync(Ticket ticket, string requesterEmailNormalised, IReadOnlyList<TicketChange> changes, CancellationToken ct)
    {
        var now = clock.GetUtcNowMicroseconds();
        if (changes.Any(c => c.Kind == ChangeKind.Created))
        {
            await sla.ApplyOnCreateAsync(ticket, requesterEmailNormalised, now, ct);
            return;
        }

        await sla.OnChangesAsync(ticket, changes, now, ct);
    }

    public async Task AfterCommitAsync(Ticket ticket, IReadOnlyList<TicketChange> changes, string provenance, CancellationToken ct)
    {
        if (changes.Count == 0)
        {
            return;
        }

        await automation.DispatchAsync(ticket.DepartmentId, ticket.Id, changes, null, provenance, ct);
        if (changes.Any(c => c.Kind is ChangeKind.Status or ChangeKind.AgentPublicReply or ChangeKind.Created)
            && await db.TicketSlas.AsNoTracking().IgnoreDepartmentScope().AnyAsync(s => s.TicketId == ticket.Id && s.DepartmentId == ticket.DepartmentId, ct))
        {
            await events.PublishAsync(ticket.DepartmentId, ticket.Id, ticket.Reference, TicketEventKinds.Sla, null, ct);
        }
    }
}

internal sealed class SlaMonitor(TicketingDbContext db, SlaEngine sla, AutomationEngine automation, ITicketEvents events, TimeProvider clock) : ISlaMonitor
{
    private const int BatchSize = 200;

    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var processed = 0;
        for (var round = 0; round < 50; round++)
        {
            var batch = await ProcessBatchAsync(cancellationToken);
            processed += batch;
            if (batch < BatchSize)
            {
                return processed;
            }
        }

        return processed;
    }

    private async Task<int> ProcessBatchAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNowMicroseconds();
        var notifications = new List<SlaNotification>();
        int count;
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            // Competing monitors never process the same ticket: rows are claimed with SKIP LOCKED.
            var ids = new List<Guid>();
            var connection = db.Database.GetDbConnection();
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = db.Database.CurrentTransaction!.GetDbTransaction();
                command.CommandText = "SELECT ticket_id FROM ticketing.ticket_sla WHERE next_event_at <= @now ORDER BY next_event_at LIMIT @n FOR UPDATE SKIP LOCKED";
                var p1 = command.CreateParameter();
                p1.ParameterName = "now";
                p1.Value = now;
                command.Parameters.Add(p1);
                var p2 = command.CreateParameter();
                p2.ParameterName = "n";
                p2.Value = BatchSize;
                command.Parameters.Add(p2);
                await using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    ids.Add(reader.GetGuid(0));
                }
            }

            var rows = await db.TicketSlas.IgnoreDepartmentScope().Where(s => ids.Contains(s.TicketId)).ToListAsync(ct);
            foreach (var row in rows)
            {
                notifications.AddRange(sla.RecordDue(row, now));
            }

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            count = rows.Count;
        }

        foreach (var n in notifications)
        {
            var reference = await db.Tickets.AsNoTracking().IgnoreDepartmentScope().Where(t => t.Id == n.TicketId && t.DepartmentId == n.DepartmentId).Select(t => t.Reference).SingleOrDefaultAsync(ct);
            if (reference is null)
            {
                continue;
            }

            await events.PublishAsync(n.DepartmentId, n.TicketId, reference, TicketEventKinds.Sla, null, ct);
            var trigger = n.Kind == SlaEventKinds.Breached ? Triggers.SlaBreached : Triggers.SlaApproaching;
            // Deterministic key: a retried monitor run can never repeat the automation of the same warning or breach.
            await automation.RunAsync(n.DepartmentId, n.TicketId, trigger, $"sla:{n.Clock}:{n.Cycle}:{n.Kind}", AutomationChain.Start(Provenances.Sla), ct);
        }

        return count;
    }
}
