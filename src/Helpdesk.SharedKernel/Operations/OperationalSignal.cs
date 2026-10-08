namespace Helpdesk.SharedKernel.Operations;

/// <summary>
/// One operational measurement a module publishes so that stuck or failing work can be alerted on (SRE-005). The host samples every registered signal periodically and
/// exposes it as a metric; a signal never carries ticket content, addresses or identifiers, only a number. Registered as singletons; implementations open their own scope.
/// </summary>
public interface IOperationalSignal
{
    /// <summary>Metric name, lower-case with underscores, e.g. <c>helpdesk_notification_outbox_oldest_age_seconds</c>.</summary>
    string Name { get; }

    string Description { get; }

    /// <summary>Unit, e.g. <c>s</c> for seconds or <c>{item}</c> for a count.</summary>
    string Unit { get; }

    /// <summary>The current value; 0 when there is nothing outstanding. Null when it cannot be determined right now.</summary>
    Task<double?> ReadAsync(CancellationToken cancellationToken);
}
