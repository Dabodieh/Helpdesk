using Helpdesk.Modules.Ticketing.Contracts;
using Helpdesk.Modules.Ticketing.Persistence;
using Helpdesk.SharedKernel.Database;
using Helpdesk.SharedKernel.Operations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Helpdesk.Modules.Ticketing.Automation;

/// <summary>
/// "Automation has to react to this change": staged in the SAME transaction as the ticket change or the SLA breach/warning row (decision D16, the notification outbox
/// pattern, not an event framework) and run by the fast path right after commit or, if that did not happen, by the recurring drain. <see cref="TriggerKey"/> is the
/// deterministic key the automation engine already uses, so a replay can never repeat a rule's actions. Carries ids and the trigger name only.
/// </summary>
internal sealed class AutomationIntent
{
    public Guid Id { get; set; }

    public Guid DepartmentId { get; set; }

    public Guid TicketId { get; set; }

    public string Trigger { get; set; } = "";

    public string TriggerKey { get; set; } = "";

    public string Provenance { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? ProcessedAt { get; set; }

    public int Attempts { get; set; }

    /// <summary>Exception type name of the last failure (never a message).</summary>
    public string? LastError { get; set; }
}

internal sealed partial class AutomationOutbox(TicketingDbContext db, AutomationEngine automation, TimeProvider clock, ILogger<AutomationOutbox> log) : IAutomationOutbox
{
    /// <summary>After this many failed attempts an intent is parked: it stays visible (and in the age metric) and is not retried until an operator looks.</summary>
    public const int MaxAttempts = 5;

    private const int Batch = 100;

    /// <summary>The drain leaves very recent items to the fast path that is probably still running them.</summary>
    private static readonly TimeSpan FastPathGrace = TimeSpan.FromSeconds(30);

    /// <summary>Adds the intent to the caller's unit of work; it is written by the caller's SaveChanges, in the caller's transaction.</summary>
    public void Stage(Guid departmentId, Guid ticketId, string trigger, string triggerKey, string provenance) =>
        db.AutomationIntents.Add(new AutomationIntent
        {
            Id = Guid.CreateVersion7(), DepartmentId = departmentId, TicketId = ticketId, Trigger = trigger, TriggerKey = triggerKey, Provenance = provenance, CreatedAt = clock.GetUtcNowMicroseconds(),
        });

    /// <summary>Fast path: runs the intents of this ticket that were staged for these keys. A failure leaves them for the drain.</summary>
    public async Task RunStagedAsync(Guid ticketId, IReadOnlyCollection<string> triggerKeys, CancellationToken cancellationToken)
    {
        if (triggerKeys.Count == 0)
        {
            return;
        }

        var ids = await db.AutomationIntents.AsNoTracking()
            .Where(i => i.TicketId == ticketId && i.ProcessedAt == null && triggerKeys.Contains(i.TriggerKey)).OrderBy(i => i.CreatedAt).ThenBy(i => i.Id).Select(i => i.Id).ToListAsync(cancellationToken);
        foreach (var id in ids)
        {
            await RunAsync(id, cancellationToken);
        }
    }

    public async Task<int> DrainAsync(CancellationToken cancellationToken)
    {
        var before = clock.GetUtcNow() - FastPathGrace;
        var ids = await db.AutomationIntents.AsNoTracking()
            .Where(i => i.ProcessedAt == null && i.Attempts < MaxAttempts && i.CreatedAt < before)
            .OrderBy(i => i.Attempts).ThenBy(i => i.CreatedAt).ThenBy(i => i.Id).Take(Batch).Select(i => i.Id).ToListAsync(cancellationToken);
        var processed = 0;
        foreach (var id in ids)
        {
            if (await RunAsync(id, cancellationToken))
            {
                processed++;
            }
        }

        return processed;
    }

    /// <summary>Claims one intent by attempt (so a second worker skips it), runs the engine and marks it processed. False when it failed or was taken.</summary>
    private async Task<bool> RunAsync(Guid id, CancellationToken ct)
    {
        var now = clock.GetUtcNowMicroseconds();
        var item = await db.AutomationIntents.AsNoTracking().SingleOrDefaultAsync(i => i.Id == id && i.ProcessedAt == null, ct);
        if (item is null)
        {
            return false;
        }

        try
        {
            await automation.RunDurableAsync(item.DepartmentId, item.TicketId, item.Trigger, item.TriggerKey, AutomationChain.Start(item.Provenance), ct);
            await db.AutomationIntents.Where(i => i.Id == id && i.ProcessedAt == null).ExecuteUpdateAsync(s => s.SetProperty(i => i.ProcessedAt, now), ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            db.ChangeTracker.Clear();
            var error = ex.GetType().Name;
            await db.AutomationIntents.Where(i => i.Id == id && i.ProcessedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(i => i.Attempts, i => i.Attempts + 1).SetProperty(i => i.LastError, error), ct);
            IntentFailed(log, id, error);
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Automation intent {IntentId} failed ({Error}); it will be retried until it is parked")]
    private static partial void IntentFailed(ILogger logger, Guid intentId, string error);
}

/// <summary>Age of the oldest automation intent still waiting (including parked ones). Growing means the drain is stuck or an intent keeps failing.</summary>
internal sealed class AutomationOutboxAgeSignal(IServiceScopeFactory scopes, TimeProvider clock) : IOperationalSignal
{
    public string Name => "helpdesk_automation_outbox_oldest_age_seconds";

    public string Description => "Age of the oldest unprocessed automation intent.";

    public string Unit => "s";

    public async Task<double?> ReadAsync(CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TicketingDbContext>();
        var oldest = await db.AutomationIntents.AsNoTracking().Where(i => i.ProcessedAt == null).MinAsync(i => (DateTimeOffset?)i.CreatedAt, cancellationToken);
        return oldest is null ? 0 : Math.Max(0, (clock.GetUtcNow() - oldest.Value).TotalSeconds);
    }
}
