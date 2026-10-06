namespace Helpdesk.Modules.Ticketing.Contracts;

/// <summary>Used by the Host's recurring job. Selects only rows whose indexed <c>next_event_at</c> has passed (never one job per ticket, never a full scan).</summary>
public interface ISlaMonitor
{
    /// <summary>Records due warnings and breaches and runs the SLA automation triggers. Returns the number of tickets processed.</summary>
    Task<int> RunAsync(CancellationToken cancellationToken);
}
