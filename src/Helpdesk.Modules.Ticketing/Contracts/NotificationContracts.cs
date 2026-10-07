namespace Helpdesk.Modules.Ticketing.Contracts;

/// <summary>Used by the Host's recurring job: turns committed notification outbox items into notifications (idempotent; safe to run concurrently).</summary>
public interface INotificationOutbox
{
    /// <summary>Returns the number of outbox items processed.</summary>
    Task<int> DrainAsync(CancellationToken cancellationToken);
}
