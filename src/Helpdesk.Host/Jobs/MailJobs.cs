using Hangfire;
using Helpdesk.Modules.Mail.Contracts;

namespace Helpdesk.Host.Jobs;

/// <summary>
/// Hangfire entry points (ADR-006). They only call the module's own <see cref="IMailJobRunner"/>; all logic and idempotency live in
/// the Mail module, so Hangfire stays replaceable. Jobs run as the system principal.
/// </summary>
public sealed class MailJobs(IMailJobRunner runner)
{
    [AutomaticRetry(Attempts = 3, DelaysInSeconds = [30, 120, 600])]
    public Task Sync(Guid mailboxId, CancellationToken cancellationToken) => runner.RunSyncAsync(mailboxId, cancellationToken);

    [AutomaticRetry(Attempts = 3, DelaysInSeconds = [30, 120, 600])]
    public Task Send(Guid outboundId, CancellationToken cancellationToken) => runner.RunSendAsync(outboundId, cancellationToken);

    [DisableConcurrentExecution(120)]
    [AutomaticRetry(Attempts = 0)]
    public Task SyncAll(CancellationToken cancellationToken) => runner.RunSyncAllAsync(cancellationToken);

    [DisableConcurrentExecution(120)]
    [AutomaticRetry(Attempts = 0)]
    public Task Dispatch(CancellationToken cancellationToken) => runner.RunDispatchAsync(cancellationToken);

    [DisableConcurrentExecution(300)]
    [AutomaticRetry(Attempts = 0)]
    public Task MaintainSubscriptions(CancellationToken cancellationToken) => runner.RunSubscriptionMaintenanceAsync(cancellationToken);
}

internal sealed class HangfireMailJobScheduler(IBackgroundJobClient client) : IMailJobScheduler
{
    public void EnqueueSync(Guid mailboxId) => client.Enqueue<MailJobs>(j => j.Sync(mailboxId, CancellationToken.None));

    public void EnqueueSend(Guid outboundId) => client.Enqueue<MailJobs>(j => j.Send(outboundId, CancellationToken.None));
}

/// <summary>Records SLA warnings and breaches that are due and runs the SLA automation triggers (an indexed selection; no per-ticket jobs).</summary>
public sealed class SlaJobs(Helpdesk.Modules.Ticketing.Contracts.ISlaMonitor monitor)
{
    [DisableConcurrentExecution(120)]
    [AutomaticRetry(Attempts = 0)]
    public Task Monitor(CancellationToken cancellationToken) => monitor.RunAsync(cancellationToken);
}
