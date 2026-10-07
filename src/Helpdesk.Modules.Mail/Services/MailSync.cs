using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Helpdesk.Modules.Mail.Contracts;
using Helpdesk.Modules.Mail.Domain;
using Helpdesk.Modules.Mail.Persistence;
using Helpdesk.Modules.Mail.Providers;
using Helpdesk.SharedKernel.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Helpdesk.Modules.Mail.Services;

/// <summary>
/// Delta synchronisation of one mailbox. Webhooks are only a nudge: this is the source of truth, so lost, duplicated or reordered
/// notifications are harmless. Mail received before the mailbox was configured is never imported (<see cref="MailboxSync"/> starts
/// at the configuration time), and a per-mailbox advisory lock keeps concurrent runs from overlapping.
/// </summary>
internal sealed class MailSyncService(MailDbContext db, IMailProvider provider, InboundProcessor processor, TimeProvider clock, ILogger<MailSyncService> log)
{
    private const int MaxRounds = 5;

    public async Task SyncAsync(Guid mailboxId, CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            var locked = await db.Database.SqlQuery<bool>($"SELECT pg_try_advisory_lock(hashtextextended({mailboxId.ToString()}, 0)) AS \"Value\"").SingleAsync(ct);
            if (!locked)
            {
                return; // another run is working on this mailbox; it loops again if a notification arrived meanwhile
            }

            try
            {
                for (var round = 0; round < MaxRounds; round++)
                {
                    if (!await RunOnceAsync(mailboxId, ct))
                    {
                        break;
                    }
                }
            }
            finally
            {
                await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_unlock(hashtextextended({mailboxId.ToString()}, 0))", CancellationToken.None);
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    /// <summary>One delta pass. Returns true when another pass is needed because a notification arrived meanwhile.</summary>
    private async Task<bool> RunOnceAsync(Guid mailboxId, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        var mailbox = await db.Mailboxes.Include(m => m.Sync).SingleOrDefaultAsync(m => m.Id == mailboxId, ct);
        if (mailbox is not { IsActive: true })
        {
            return false;
        }

        var sync = mailbox.Sync ?? new MailboxSync { MailboxId = mailbox.Id };
        if (mailbox.Sync is null)
        {
            db.MailboxSyncs.Add(sync);
        }

        sync.Dirty = false;
        await db.SaveChangesAsync(ct);

        var mref = new MailboxRef(mailbox.Id, mailbox.Address, mailbox.ProviderRef ?? mailbox.Address);
        var notBefore = mailbox.CreatedAt - TimeSpan.FromMinutes(1);
        try
        {
            DeltaResult delta;
            try
            {
                delta = await provider.FetchChangesAsync(mref, sync.DeltaToken, notBefore, ct);
            }
            catch (DeltaResetException)
            {
                // The provider forgot our position. Start over from the configuration time: already processed mail is skipped by the
                // idempotency key, anything that arrived during the gap is picked up.
                log.DeltaReset(mailbox.Id);
                delta = await provider.FetchChangesAsync(mref, null, notBefore, ct);
            }

            foreach (var change in delta.Changes.Where(c => !c.Removed).DistinctBy(c => c.ProviderMessageId))
            {
                await processor.ProcessAsync(mailbox.Id, change.ProviderMessageId, ct);
                db.ChangeTracker.Clear();
            }

            var tracked = await db.MailboxSyncs.SingleAsync(s => s.MailboxId == mailboxId, ct);
            tracked.DeltaToken = delta.DeltaToken;
            tracked.LastSyncAt = clock.GetUtcNowMicroseconds();
            tracked.LastSyncError = null;
            tracked.ConsecutiveFailures = 0;
            await db.SaveChangesAsync(ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var code = MailText.ShortError(ex);
            log.SyncFailed(mailbox.Id, code);
            db.ChangeTracker.Clear();
            var tracked = await db.MailboxSyncs.SingleAsync(s => s.MailboxId == mailboxId, ct);
            tracked.ConsecutiveFailures++;
            tracked.LastSyncError = code;
            tracked.Dirty = true;
            await db.SaveChangesAsync(ct);
            throw;
        }

        db.ChangeTracker.Clear();
        return await db.MailboxSyncs.AsNoTracking().Where(s => s.MailboxId == mailboxId).Select(s => s.Dirty).SingleAsync(ct);
    }
}

/// <summary>Creates, renews and re-creates Graph webhook subscriptions (without resource data; lifecycle URL always set; ADR-011).</summary>
internal sealed class SubscriptionService(MailDbContext db, IMailProvider provider, IOptions<MailOptions> options, TimeProvider clock, ILogger<SubscriptionService> log)
{
    public async Task EnsureAsync(Guid mailboxId, CancellationToken ct)
    {
        var settings = options.Value;
        var mailbox = await db.Mailboxes.Include(m => m.Sync).SingleOrDefaultAsync(m => m.Id == mailboxId, ct);
        if (mailbox is not { IsActive: true })
        {
            return;
        }

        var sync = mailbox.Sync ?? new MailboxSync { MailboxId = mailbox.Id };
        if (mailbox.Sync is null)
        {
            db.MailboxSyncs.Add(sync);
        }

        var now = clock.GetUtcNowMicroseconds();
        var healthy = sync.SubscriptionState == SubscriptionStates.Active && sync.SubscriptionExpiresAt is { } expires
            && expires - now > TimeSpan.FromHours(settings.RenewWithinHours);
        if (healthy)
        {
            return;
        }

        var mref = new MailboxRef(mailbox.Id, mailbox.Address, mailbox.ProviderRef ?? mailbox.Address);
        var expiry = now + TimeSpan.FromDays(settings.SubscriptionLifetimeDays);
        try
        {
            if (sync.SubscriptionId is { } existing && sync.SubscriptionState == SubscriptionStates.Active)
            {
                try
                {
                    // Renewal is a PATCH of the expiry only (it also reauthorises, ADR-011).
                    sync.SubscriptionExpiresAt = await provider.RenewSubscriptionAsync(mref, existing, expiry, ct);
                    sync.LastSyncError = null;
                    await db.SaveChangesAsync(ct);
                    return;
                }
                catch (PermanentProviderException)
                {
                    // The subscription is gone or invalid: replace it (changes in between are recovered by delta).
                    sync.SubscriptionId = null;
                }
            }

            var baseUrl = (settings.NotificationBaseUrl ?? "https://localhost").TrimEnd('/');
            var clientState = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            var created = await provider.CreateSubscriptionAsync(
                mref, new SubscriptionRequest($"{baseUrl}/api/mail/graph/notifications", $"{baseUrl}/api/mail/graph/lifecycle", clientState, expiry), ct);
            sync.SubscriptionId = created.Id;
            sync.SubscriptionExpiresAt = created.ExpiresAt;
            sync.SubscriptionState = SubscriptionStates.Active;
            sync.ClientStateHash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(clientState));
            sync.LastSyncError = null;
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Visible on the mailbox status; delta reconciliation keeps working without webhooks.
            sync.SubscriptionState = SubscriptionStates.Failed;
            sync.LastSyncError = MailText.ShortError(ex);
            log.SubscriptionFailed(mailbox.Id, sync.LastSyncError);
            await db.SaveChangesAsync(CancellationToken.None);
        }
    }

    public async Task MarkRemovedAsync(Guid mailboxId, CancellationToken ct)
    {
        var sync = await db.MailboxSyncs.SingleOrDefaultAsync(s => s.MailboxId == mailboxId, ct);
        if (sync is null)
        {
            return;
        }

        sync.SubscriptionState = SubscriptionStates.None;
        sync.SubscriptionId = null;
        sync.SubscriptionExpiresAt = null;
        sync.ClientStateHash = null;
        await db.SaveChangesAsync(ct);
    }
}

/// <summary>
/// Webhook intake (anonymous by necessity): a notification is accepted only when its subscription id is one of OUR subscriptions and
/// its client state matches (constant-time compare of hashes). The mailbox comes from our subscription record, never from the payload;
/// only identifiers are stored; processing is a nudge to the delta sync.
/// </summary>
internal sealed class NotificationService(MailDbContext db, SubscriptionService subscriptions, IMailJobScheduler scheduler, TimeProvider clock, ILogger<NotificationService> log)
{
    public const int MaxItems = 200;

    /// <returns>The number of accepted notifications (rejected ones are logged without content).</returns>
    public async Task<int> HandleAsync(JsonNode payload, bool lifecycle, CancellationToken ct)
    {
        var accepted = 0;
        var rejected = 0;
        var mailboxesToSync = new HashSet<Guid>();
        foreach (var item in (payload["value"]?.AsArray() ?? []).Take(MaxItems))
        {
            var subscriptionId = item?["subscriptionId"]?.GetValue<string>();
            var clientState = item?["clientState"]?.GetValue<string>();
            if (subscriptionId is null || clientState is null)
            {
                rejected++;
                continue;
            }

            var sync = await db.MailboxSyncs.SingleOrDefaultAsync(s => s.SubscriptionId == subscriptionId, ct);
            if (sync?.ClientStateHash is null
                || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(clientState)), sync.ClientStateHash))
            {
                rejected++;
                continue;
            }

            accepted++;
            if (lifecycle)
            {
                switch (item!["lifecycleEvent"]?.GetValue<string>())
                {
                    case "reauthorizationRequired":
                        sync.SubscriptionExpiresAt = clock.GetUtcNow(); // forces the renewal path now
                        await db.SaveChangesAsync(ct);
                        await subscriptions.EnsureAsync(sync.MailboxId, ct);
                        break;
                    case "subscriptionRemoved":
                        await subscriptions.MarkRemovedAsync(sync.MailboxId, ct);
                        await subscriptions.EnsureAsync(sync.MailboxId, ct);
                        mailboxesToSync.Add(sync.MailboxId);
                        break;
                    default: // "missed" and anything unknown: reconcile through delta
                        mailboxesToSync.Add(sync.MailboxId);
                        break;
                }

                continue;
            }

            db.Notifications.Add(new MailNotification
            {
                Id = Guid.CreateVersion7(),
                MailboxId = sync.MailboxId,
                ChangeType = (item!["changeType"]?.GetValue<string>() ?? "unknown") is { Length: <= 32 } ct32 ? ct32 : "unknown",
                ResourceId = item["resourceData"]?["id"]?.GetValue<string>() is { Length: <= 512 } rid ? rid : null,
                ReceivedAt = clock.GetUtcNowMicroseconds(),
            });
            sync.Dirty = true;
            await db.SaveChangesAsync(ct);
            mailboxesToSync.Add(sync.MailboxId);
        }

        if (rejected > 0)
        {
            log.WebhookRejected(rejected);
        }

        foreach (var mailboxId in mailboxesToSync)
        {
            scheduler.EnqueueSync(mailboxId);
        }

        return accepted;
    }
}

internal sealed class MailJobRunner(
    MailDbContext db,
    MailSyncService sync,
    SubscriptionService subscriptions,
    OutboundSender sender,
    InboundProcessor processor,
    TimeProvider clock,
    ILogger<MailJobRunner> log) : IMailJobRunner
{
    private const int BatchSize = 50;

    public Task RunSyncAsync(Guid mailboxId, CancellationToken cancellationToken = default) => sync.SyncAsync(mailboxId, cancellationToken);

    public async Task RunSyncAllAsync(CancellationToken cancellationToken = default)
    {
        var ids = await db.Mailboxes.AsNoTracking().Where(m => m.IsActive).Select(m => m.Id).ToListAsync(cancellationToken);
        foreach (var id in ids)
        {
            try
            {
                await sync.SyncAsync(id, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.ReconciliationFailed(id, MailText.ShortError(ex));
            }
        }
    }

    public Task RunSendAsync(Guid outboundId, CancellationToken cancellationToken = default) => sender.SendAsync(outboundId, cancellationToken);

    public async Task RunDispatchAsync(CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNowMicroseconds();

        // Sends that were claimed but never finished (crashed worker). Before the provider send call they simply go back to the queue. Once the
        // send call may have happened (submit_started) they are reconciled against the provider sent copy instead and, without one, become Uncertain:
        // a blind resend could mail the requester twice.
        var stuckBefore = now - TimeSpan.FromMinutes(10);
        await db.Database.ExecuteSqlAsync(
            $"UPDATE mail.messages SET state = 'Queued', next_attempt_at = {now}, updated_at = {now} WHERE direction = 'Outbound' AND state = 'Sending' AND updated_at < {stuckBefore} AND (flags IS NULL OR flags NOT LIKE '%submit_started%')",
            cancellationToken);
        var stuckSubmitted = await db.Messages.AsNoTracking()
            .Where(m => m.Direction == MessageDirections.Outbound && m.State == MessageStates.Sending && m.UpdatedAt < stuckBefore)
            .Select(m => m.Id).Take(BatchSize).ToListAsync(cancellationToken);
        foreach (var stuckId in stuckSubmitted)
        {
            await sender.ReconcileAsync(stuckId, cancellationToken);
            db.ChangeTracker.Clear();
        }

        var due = await db.Messages.AsNoTracking()
            .Where(m => m.Direction == MessageDirections.Outbound && m.State == MessageStates.Queued && m.NextAttemptAt <= now)
            .OrderBy(m => m.NextAttemptAt).Select(m => m.Id).Take(BatchSize).ToListAsync(cancellationToken);
        foreach (var id in due)
        {
            await sender.SendAsync(id, cancellationToken);
            db.ChangeTracker.Clear();
        }

        var unreported = await db.Messages
            .Where(m => m.Direction == MessageDirections.Outbound && (m.State == MessageStates.Sent || m.State == MessageStates.Failed || m.State == MessageStates.Uncertain) && m.DeliveryReportedAt == null)
            .OrderBy(m => m.UpdatedAt).Take(BatchSize).ToListAsync(cancellationToken);
        foreach (var message in unreported)
        {
            await sender.ReportAsync(message, cancellationToken);
        }

        db.ChangeTracker.Clear();
        var retryInbound = await db.Messages.AsNoTracking()
            .Where(m => m.Direction == MessageDirections.Inbound && m.State == MessageStates.Failed && m.NextAttemptAt != null && m.NextAttemptAt <= now && m.Attempts < InboundProcessor.MaxAttempts)
            .OrderBy(m => m.NextAttemptAt).Select(m => new { m.MailboxId, m.ProviderMessageId }).Take(BatchSize).ToListAsync(cancellationToken);
        foreach (var m in retryInbound)
        {
            await processor.ProcessAsync(m.MailboxId, m.ProviderMessageId!, cancellationToken);
            db.ChangeTracker.Clear();
        }
    }

    public async Task RunSubscriptionMaintenanceAsync(CancellationToken cancellationToken = default)
    {
        var ids = await db.Mailboxes.AsNoTracking().Where(m => m.IsActive).Select(m => m.Id).ToListAsync(cancellationToken);
        foreach (var id in ids)
        {
            await subscriptions.EnsureAsync(id, cancellationToken);
            db.ChangeTracker.Clear();
        }
    }
}
