namespace Helpdesk.Modules.Ticketing.Contracts;

/// <summary>Used by the Host's recurring job: turns committed notification outbox items into notifications (idempotent; safe to run concurrently).</summary>
public interface INotificationOutbox
{
    /// <summary>Returns the number of outbox items processed.</summary>
    Task<int> DrainAsync(CancellationToken cancellationToken);
}

/// <summary>Used by the Host's recurring job: runs automation intents that committed with a ticket or SLA change but were not run by the fast path (idempotent; safe to run concurrently).</summary>
public interface IAutomationOutbox
{
    /// <summary>Returns the number of intents run.</summary>
    Task<int> DrainAsync(CancellationToken cancellationToken);
}
