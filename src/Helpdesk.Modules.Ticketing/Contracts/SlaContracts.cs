namespace Helpdesk.Modules.Ticketing.Contracts;

/// <summary>Used by the Host's recurring job. Selects only rows whose indexed <c>next_event_at</c> has passed (never one job per ticket, never a full scan).</summary>
public interface ISlaMonitor
{
    /// <summary>Records due warnings and breaches and runs the SLA automation triggers. Returns the number of tickets processed.</summary>
    Task<int> RunAsync(CancellationToken cancellationToken);
}

/// <summary>The result of <see cref="ISlaAlertBackfill"/>. <see cref="ClocksAlreadyPast"/> counts clocks whose 80% instant had already passed: they are marked as alerted WITHOUT a notification.</summary>
public sealed record SlaAlertBackfillResult(bool Applied, int Tickets, int ClocksScheduled, int ClocksAlreadyPast);

/// <summary>
/// One-off, operator-started backfill of the 80% alert instants for SLA rows that existed before they were introduced. Never scheduled and never run on startup;
/// only the Host's explicit <c>sla-alert-backfill</c> command calls it. Idempotent: a second run finds nothing to do.
/// </summary>
public interface ISlaAlertBackfill
{
    /// <summary>With <paramref name="apply"/> false (the default of the command) nothing is written and the counts show what would change.</summary>
    Task<SlaAlertBackfillResult> RunAsync(bool apply, CancellationToken cancellationToken);
}
