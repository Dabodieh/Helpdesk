using Helpdesk.Modules.Ticketing.Contracts;
using Helpdesk.Modules.Ticketing.Domain;
using Helpdesk.Modules.Ticketing.Persistence;
using Helpdesk.SharedKernel.Database;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Modules.Ticketing.Sla;

/// <summary>
/// Gives running SLA clocks that predate the 80% alert their alert instant, using the engine's own calculation (business hours, exceptions and pauses are already
/// in the deadline). Only unmet, unpaused, unbreached clocks with a deadline and no alert fields are touched. A clock whose 80% instant has already passed is
/// marked as alerted at that instant and nothing is notified (no retrospective alerts). The write is raw SQL on the alert columns and <c>next_event_at</c> only: deadlines,
/// warnings, breaches, SLA events, automation and notifications are not touched, and the notification outbox interceptor (SaveChanges only) never sees it.
/// </summary>
internal sealed class SlaAlertBackfill(TicketingDbContext db, TimeProvider clock) : ISlaAlertBackfill
{
    private const int BatchSize = 200;

    public async Task<SlaAlertBackfillResult> RunAsync(bool apply, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNowMicroseconds();
        Guid? after = null;
        int tickets = 0, scheduled = 0, past = 0;
        while (true)
        {
            var batch = await db.TicketSlas.AsNoTracking().IgnoreDepartmentScope()
                .Where(s => (after == null || s.TicketId.CompareTo(after.Value) > 0)
                    && ((s.FirstResponseDueAt != null && s.FirstResponseMetAt == null && s.FirstResponseTargetMinutes != null && s.FirstResponseBreachedAt == null
                            && s.FirstResponseAlertAt == null && s.FirstResponseAlertedAt == null)
                        || (s.ResolutionDueAt != null && s.ResolutionMetAt == null && s.ResolutionPausedAt == null && s.ResolutionTargetMinutes != null && s.ResolutionBreachedAt == null
                            && s.ResolutionAlertAt == null && s.ResolutionAlertedAt == null)))
                .OrderBy(s => s.TicketId).Take(BatchSize).ToListAsync(cancellationToken);
            if (batch.Count == 0)
            {
                break;
            }

            foreach (var sla in batch)
            {
                var calendar = SlaEngine.Calendar(StoredJson.Read<SlaSnapshot>(sla.Snapshot));
                // AlertInstant itself ignores met, paused, breached and already-alerted clocks; an existing alert time is left alone here.
                var first = sla.FirstResponseAlertAt is null
                    ? SlaEngine.AlertInstant(sla, calendar, sla.FirstResponseDueAt, sla.FirstResponseMetAt, false, sla.FirstResponseTargetMinutes, sla.FirstResponseAlertedAt, sla.FirstResponseBreachedAt)
                    : null;
                var resolution = sla.ResolutionAlertAt is null
                    ? SlaEngine.AlertInstant(sla, calendar, sla.ResolutionDueAt, sla.ResolutionMetAt, sla.ResolutionPausedAt is not null, sla.ResolutionTargetMinutes, sla.ResolutionAlertedAt, sla.ResolutionBreachedAt)
                    : null;
                if (first is null && resolution is null)
                {
                    continue;
                }

                var (frAt, frDone) = Split(first, now);
                var (resAt, resDone) = Split(resolution, now);
                var changed = 1;
                if (apply)
                {
                    DateTimeOffset? frAlert = frAt, frAlerted = frDone, resAlert = resAt, resAlerted = resDone;
                    var frChange = first is not null;
                    var resChange = resolution is not null;
                    changed = await db.Database.ExecuteSqlAsync(
                        $"""
                        UPDATE ticketing.ticket_sla SET
                            fr_alert_at = COALESCE(CAST({frAlert} AS timestamptz), fr_alert_at), fr_alerted_at = COALESCE(CAST({frAlerted} AS timestamptz), fr_alerted_at),
                            res_alert_at = COALESCE(CAST({resAlert} AS timestamptz), res_alert_at), res_alerted_at = COALESCE(CAST({resAlerted} AS timestamptz), res_alerted_at),
                            next_event_at = LEAST(next_event_at, CAST({frAlert} AS timestamptz), CAST({resAlert} AS timestamptz))
                        WHERE ticket_id = {sla.TicketId}
                          AND (NOT {frChange} OR (fr_alert_at IS NULL AND fr_alerted_at IS NULL AND fr_met_at IS NULL AND fr_breached_at IS NULL))
                          AND (NOT {resChange} OR (res_alert_at IS NULL AND res_alerted_at IS NULL AND res_met_at IS NULL AND res_breached_at IS NULL AND res_paused_at IS NULL))
                        """,
                        cancellationToken);
                }

                if (changed > 0)
                {
                    tickets++;
                    scheduled += (frAt is null ? 0 : 1) + (resAt is null ? 0 : 1);
                    past += (frDone is null ? 0 : 1) + (resDone is null ? 0 : 1);
                }
            }

            after = batch[^1].TicketId;
        }

        return new SlaAlertBackfillResult(apply, tickets, scheduled, past);
    }

    /// <summary>A future instant is scheduled; a past one is recorded as already alerted (silently).</summary>
    private static (DateTimeOffset? Alert, DateTimeOffset? Alerted) Split(DateTimeOffset? instant, DateTimeOffset now) =>
        instant is not { } at ? (null, null) : at > now ? (at, null) : (null, at);
}
