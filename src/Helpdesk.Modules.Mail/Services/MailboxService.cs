using System.Globalization;
using System.Text.RegularExpressions;
using Helpdesk.Modules.Mail.Contracts;
using Helpdesk.Modules.Mail.Domain;
using Helpdesk.Modules.Mail.Persistence;
using Helpdesk.Modules.Mail.Providers;
using Helpdesk.Modules.Organisation.Contracts;
using Helpdesk.SharedKernel.Authorization;
using Helpdesk.SharedKernel.Database;
using Helpdesk.SharedKernel.Errors;
using Helpdesk.SharedKernel.Paging;
using Helpdesk.SharedKernel.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Helpdesk.Modules.Mail.Services;

internal sealed record SubscriptionDto(string State, DateTimeOffset? ExpiresAt);

internal sealed record MailboxDto(
    Guid Id,
    Guid DepartmentId,
    string Address,
    string DisplayName,
    string Provider,
    bool IsActive,
    bool IsDefault,
    string Version,
    SubscriptionDto Subscription,
    DateTimeOffset? LastSyncAt,
    string? LastSyncError,
    int AttentionCount,
    MailboxHealthDto Health,
    DateTimeOffset? LastSendAt,
    int OutboundFailedCount,
    int OutboundUncertainCount);

/// <summary>
/// Derived from persisted state only (no probes). <see cref="Status"/>: Healthy, Attention, Failed, NotConfigured, Disabled. <see cref="Reasons"/> are stable codes
/// (subscription_failed, subscription_expired, subscription_expiring, sync_failing, sync_stale, outbound_failed, outbound_uncertain, inbound_failed, ...).
/// Having no traffic is never a reason: the sync job runs every few minutes whether or not mail arrives, so a stale LastSyncAt means sync itself stopped.
/// </summary>
internal sealed record MailboxHealthDto(string Status, IReadOnlyList<string> Reasons);

internal sealed record CreateMailboxRequest(string? Address, string? DisplayName, string? ProviderRef, bool? IsDefault);

internal sealed record UpdateMailboxRequest(string? Version, string? DisplayName, bool? IsActive, bool? IsDefault);

internal sealed record AttentionItemDto(
    Guid Id,
    Guid MailboxId,
    string Direction,
    string State,
    string? IgnoreReason,
    DateTimeOffset OccurredAt,
    string Address,
    string Subject,
    string? LastError,
    int Attempts,
    Guid? TicketId,
    string? Category = null,
    string? Nature = null);

/// <summary>An outbound email that has not left yet and is late: <see cref="Reason"/> is <c>retrying</c> (a send failed and will be tried again), <c>overdue</c> (queued longer than expected) or <c>sending_stalled</c> (claimed by a worker that has not finished).</summary>
internal sealed record WaitingItemDto(
    Guid Id,
    Guid MailboxId,
    string State,
    string Reason,
    DateTimeOffset QueuedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? NextAttemptAt,
    int Attempts,
    string Address,
    string Subject,
    string? LastError,
    string? Category,
    string? Nature,
    Guid? TicketId);

internal sealed record WaitingPage(IReadOnlyList<WaitingItemDto> Items, int Total, int WaitingAfterMinutes);

internal sealed record RetryOutcomeDto(string Outcome);

internal sealed record ReleaseOutcomeDto(string State, Guid? TicketId);

internal sealed record AttentionPage(IReadOnlyList<AttentionItemDto> Items, string? NextCursor);

/// <summary>Department mailbox administration (permission <c>department.mailboxes.manage</c> on the department route, enforced by the endpoint filter).</summary>
internal sealed partial class MailboxService(
    MailDbContext db,
    IOrganisationDirectory organisation,
    MailAudit audit,
    SubscriptionService subscriptions,
    InboundProcessor processor,
    IMailJobScheduler scheduler,
    IOptions<MailOptions> options,
    OutboundSender sender,
    IAuthorizer authorizer,
    ICurrentUser current,
    IMailProvider provider,
    TimeProvider clock)
{
    private const int MaxLimit = 100;
    private static readonly TimeSpan StaleSyncAfter = TimeSpan.FromMinutes(30); // the sync-all job runs every 5 minutes, traffic or not
    private static readonly TimeSpan ExpiringSoon = TimeSpan.FromHours(12); // renewal starts 36 h before expiry and runs every 6 h: this close means renewal is failing

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex EmailPattern();

    public async Task<IReadOnlyList<MailboxDto>> ListAsync(Guid departmentId, CancellationToken ct)
    {
        var mailboxes = await db.Mailboxes.AsNoTracking().Include(m => m.Sync)
            .Where(m => m.DepartmentId == departmentId).OrderByDescending(m => m.IsDefault).ThenBy(m => m.Address).ToListAsync(ct);
        var stats = await StatsAsync(departmentId, ct);
        return [.. mailboxes.Select(m => ToDto(m, stats.GetValueOrDefault(m.Id) ?? new MailboxStats()))];
    }

    /// <summary>Every department's mailboxes, for the platform-level Microsoft 365 page. The endpoint requires platform administration; no ticket data is involved.</summary>
    public async Task<IReadOnlyList<MailboxDto>> ListAllAsync(CancellationToken ct)
    {
        var all = await db.Mailboxes.AsNoTracking().Include(m => m.Sync).OrderBy(m => m.DepartmentId).ThenByDescending(m => m.IsDefault).ThenBy(m => m.Address).ToListAsync(ct);
        var stats = new Dictionary<Guid, MailboxStats>();
        foreach (var departmentId in all.Select(m => m.DepartmentId).Distinct())
        {
            foreach (var (id, s) in await StatsAsync(departmentId, ct))
            {
                stats[id] = s;
            }
        }

        return [.. all.Select(m => ToDto(m, stats.GetValueOrDefault(m.Id) ?? new MailboxStats()))];
    }

    public async Task<MailboxDto> GetAsync(Guid departmentId, Guid mailboxId, CancellationToken ct)
    {
        var mailbox = await FindAsync(departmentId, mailboxId, track: false, ct);
        var stats = await StatsAsync(departmentId, ct);
        return ToDto(mailbox, stats.GetValueOrDefault(mailbox.Id) ?? new MailboxStats());
    }

    public async Task<MailboxDto> CreateAsync(Guid departmentId, CreateMailboxRequest request, CancellationToken ct)
    {
        // D10: only a Platform Admin introduces a mailbox address; Department Admins keep managing the mailboxes already registered.
        if (!current.IsPlatformAdmin)
        {
            throw new ForbiddenException();
        }

        var errors = new Dictionary<string, string[]>();
        var address = request.Address?.Trim();
        if (string.IsNullOrWhiteSpace(address) || address.Length > 320 || !EmailPattern().IsMatch(address) || address.Contains('\0', StringComparison.Ordinal))
        {
            errors["address"] = ["address must be a valid email address of at most 320 characters."];
        }

        ValidateDisplayName(errors, request.DisplayName);
        if (request.ProviderRef is { Length: > 128 })
        {
            errors["providerRef"] = ["providerRef must be at most 128 characters."];
        }

        ThrowIfAny(errors);

        var department = await organisation.FindDepartmentAsync(departmentId, ct) ?? throw new NotFoundException();
        if (!department.IsActive)
        {
            throw new ConflictException("The department is inactive.", ProblemCodes.Inactive);
        }

        // D10: the application's own credentials must reach the mailbox before anything is registered. A failed probe creates nothing, whoever asks.
        var providerRef = string.IsNullOrWhiteSpace(request.ProviderRef) ? null : request.ProviderRef.Trim();
        try
        {
            await provider.ProbeMailboxAsync(new MailboxRef(Guid.Empty, address!, providerRef ?? address!), ct);
        }
        catch (PermanentProviderException)
        {
            throw new RequestValidationException("address", "The application cannot access this mailbox. Check that the address is correct and that the mailbox is inside the application's approved Exchange scope.");
        }
        catch (TransientProviderException)
        {
            throw new ConflictException("Access to the mailbox could not be verified right now. Nothing was registered; try again shortly.");
        }

        var now = clock.GetUtcNowMicroseconds();
        var mailbox = new Mailbox
        {
            Id = Guid.CreateVersion7(),
            DepartmentId = departmentId,
            Address = address!,
            AddressNormalised = Mailbox.Normalise(address!),
            DisplayName = request.DisplayName!.Trim(),
            Provider = options.Value.Provider.ToLowerInvariant(),
            ProviderRef = providerRef,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
            Sync = new MailboxSync { SubscriptionState = SubscriptionStates.None },
        };

        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            var hasDefault = await db.Mailboxes.AnyAsync(m => m.DepartmentId == departmentId && m.IsDefault, ct);
            mailbox.IsDefault = request.IsDefault == true || !hasDefault;
            if (mailbox.IsDefault && hasDefault)
            {
                await db.Database.ExecuteSqlAsync($"UPDATE mail.mailboxes SET is_default = false, updated_at = {now} WHERE department_id = {departmentId} AND is_default", ct);
            }

            db.Mailboxes.Add(mailbox);
            await SaveAsync(ct);
            await audit.WriteAsync(
                MailAuditActions.MailboxCreated, MailAudit.MailboxObject, mailbox.Id, departmentId, null,
                new { address = mailbox.Address, displayName = mailbox.DisplayName, provider = mailbox.Provider, isDefault = mailbox.IsDefault }, ct);
            await tx.CommitAsync(ct);
        }

        // Best effort: the webhook subscription (state is visible on the mailbox) and a first delta pass.
        await subscriptions.EnsureAsync(mailbox.Id, ct);
        scheduler.EnqueueSync(mailbox.Id);
        return await GetAsync(departmentId, mailbox.Id, ct);
    }

    /// <summary>
    /// Manual retry of the inbound subscription (the same <see cref="SubscriptionService.EnsureAsync"/> the automatic job uses). The mailbox is resolved by department first, so
    /// another department's mailbox is simply not found. A healthy subscription is left alone and a held subscription id is renewed before anything is created (no duplicates).
    /// The result is the mailbox as it now stands: its subscription state and last error are what the administrator sees.
    /// </summary>
    public async Task<MailboxDto> RetrySubscriptionAsync(Guid departmentId, Guid mailboxId, CancellationToken ct)
    {
        var mailbox = await FindAsync(departmentId, mailboxId, track: false, ct);
        if (!mailbox.IsActive)
        {
            throw new RequestValidationException("mailbox", "Activate the mailbox before retrying its inbound subscription.");
        }

        var before = mailbox.Sync?.SubscriptionState ?? SubscriptionStates.None;
        await subscriptions.EnsureAsync(mailboxId, ct);
        db.ChangeTracker.Clear();
        var result = await GetAsync(departmentId, mailboxId, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await audit.WriteAsync(
            MailAuditActions.SubscriptionRetried, MailAudit.MailboxObject, mailboxId, departmentId,
            new { state = before }, new { state = result.Subscription.State, error = result.LastSyncError }, ct);
        await tx.CommitAsync(ct);
        return result;
    }

    public async Task<MailboxDto> UpdateAsync(Guid departmentId, Guid mailboxId, UpdateMailboxRequest request, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        if (!ConcurrencyVersion.TryParse(request.Version, out var version))
        {
            errors["version"] = ["version is required."];
        }

        if (request.DisplayName is not null)
        {
            ValidateDisplayName(errors, request.DisplayName);
        }

        if (request.DisplayName is null && request.IsActive is null && request.IsDefault is null)
        {
            errors["body"] = ["Provide at least one of displayName, isActive, isDefault."];
        }

        ThrowIfAny(errors);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var mailbox = await FindAsync(departmentId, mailboxId, track: true, ct);
        if (mailbox.Version != version)
        {
            throw new ConflictException("The mailbox was changed by someone else. Reload and retry.", ProblemCodes.VersionConflict);
        }

        db.Entry(mailbox).Property(m => m.Version).OriginalValue = version;

        var actions = new List<(string Action, object Previous, object Next)>();
        var now = clock.GetUtcNowMicroseconds();
        if (request.DisplayName is not null && request.DisplayName.Trim() != mailbox.DisplayName)
        {
            actions.Add((MailAuditActions.MailboxUpdated, new { displayName = mailbox.DisplayName }, new { displayName = request.DisplayName.Trim() }));
            mailbox.DisplayName = request.DisplayName.Trim();
        }

        if (request.IsActive is { } active && active != mailbox.IsActive)
        {
            actions.Add((active ? MailAuditActions.MailboxActivated : MailAuditActions.MailboxDeactivated, new { isActive = mailbox.IsActive }, new { isActive = active }));
            mailbox.IsActive = active;
            if (!active)
            {
                mailbox.IsDefault = false;
            }
        }

        if (request.IsDefault is { } makeDefault && makeDefault != mailbox.IsDefault)
        {
            if (makeDefault && !mailbox.IsActive)
            {
                throw new ConflictException("Only an active mailbox can be the default.", ProblemCodes.Inactive);
            }

            if (makeDefault)
            {
                await db.Database.ExecuteSqlAsync($"UPDATE mail.mailboxes SET is_default = false, updated_at = {now} WHERE department_id = {departmentId} AND is_default AND id <> {mailboxId}", ct);
            }

            actions.Add((MailAuditActions.MailboxUpdated, new { isDefault = mailbox.IsDefault }, new { isDefault = makeDefault }));
            mailbox.IsDefault = makeDefault;
        }

        if (actions.Count > 0)
        {
            mailbox.UpdatedAt = now;
            await SaveAsync(ct);
            foreach (var (action, previous, next) in actions)
            {
                await audit.WriteAsync(action, MailAudit.MailboxObject, mailbox.Id, departmentId, previous, next, ct);
            }
        }

        await tx.CommitAsync(ct);
        if (mailbox.IsActive)
        {
            await subscriptions.EnsureAsync(mailbox.Id, ct);
        }

        db.ChangeTracker.Clear();
        return await GetAsync(departmentId, mailboxId, ct);
    }

    /// <summary>
    /// Failed and ignored mail of the department, newest first (nothing is dropped silently; this is the visible list). The sender address, the subject and the ticket link are
    /// Department content: they are returned only to a caller who also holds tickets.read in this department by membership. Any other caller (for example a Platform Admin
    /// without a membership, whom the administrative permission lets through) gets the operational fields only, with those three left empty.
    /// </summary>
    public async Task<AttentionPage> AttentionAsync(Guid departmentId, string? cursor, int? limit, CancellationToken ct)
    {
        var take = limit ?? 50;
        if (take is < 1 or > MaxLimit)
        {
            throw new RequestValidationException("limit", $"limit must be between 1 and {MaxLimit}.");
        }

        var query = db.Messages.AsNoTracking().Where(m => m.DepartmentId == departmentId && (m.State == MessageStates.Failed || m.State == MessageStates.Uncertain || m.State == MessageStates.Ignored));
        if (cursor is not null)
        {
            if (!Cursor.TryDecode(cursor, out var payload) || payload.Split('|') is not { Length: 2 } parts
                || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) || ticks > DateTime.MaxValue.Ticks || !Guid.TryParse(parts[1], out var id))
            {
                throw new RequestValidationException("cursor", "Invalid cursor.");
            }

            var at = new DateTimeOffset(ticks, TimeSpan.Zero);
            query = query.Where(m => m.CreatedAt < at || (m.CreatedAt == at && m.Id.CompareTo(id) < 0));
        }

        var rows = await query.OrderByDescending(m => m.CreatedAt).ThenByDescending(m => m.Id).Take(take + 1).ToListAsync(ct);
        var more = rows.Count > take;
        if (more)
        {
            rows.RemoveAt(take);
        }

        var content = await authorizer.CheckAsync(PermissionCodes.TicketsRead, departmentId, ct) == AccessOutcome.Allowed;
        var items = rows.Select(m => new AttentionItemDto(
            m.Id, m.MailboxId, m.Direction, m.State, m.IgnoreReason, m.OccurredAt, content ? m.FromAddress : "", content ? (m.Subject.Length > 200 ? m.Subject[..200] : m.Subject) : "",
            m.LastError, m.Attempts, content ? m.TicketId : null, m.Direction == MessageDirections.Outbound ? MailErrors.Category(m.LastError) : null,
            m.Direction == MessageDirections.Outbound ? MailProblemNature.Of(MailErrors.Category(m.LastError)) : null)).ToList();
        var next = more ? Cursor.Encode(string.Create(CultureInfo.InvariantCulture, $"{rows[^1].CreatedAt.UtcTicks}|{rows[^1].Id}")) : null;
        return new AttentionPage(items, next);
    }

    public const int WaitingAfterMinutes = 5;

    /// <summary>
    /// Outbound email that has not been sent and is late, so a stuck dispatcher, an unavailable mailbox or a provider outage is visible per department and not only as a global metric:
    /// queued mail that is retrying after a failure, queued longer than <see cref="WaitingAfterMinutes"/> minutes, or claimed by a worker that has not finished in that time (a crashed worker's
    /// claim is reconciled by the maintenance job after ten minutes, never blindly re-sent). Oldest first. Same content rule as <see cref="AttentionAsync"/>: addresses, subjects and ticket links
    /// only for a caller who also holds tickets.read here.
    /// </summary>
    public async Task<WaitingPage> WaitingAsync(Guid departmentId, int? limit, CancellationToken ct)
    {
        var take = limit ?? 50;
        if (take is < 1 or > MaxLimit)
        {
            throw new RequestValidationException("limit", $"limit must be between 1 and {MaxLimit}.");
        }

        var cutoff = clock.GetUtcNowMicroseconds() - TimeSpan.FromMinutes(WaitingAfterMinutes);
        var query = db.Messages.AsNoTracking().Where(m => m.DepartmentId == departmentId && m.Direction == MessageDirections.Outbound
            && ((m.State == MessageStates.Queued && (m.Attempts > 0 || m.CreatedAt < cutoff || (m.NextAttemptAt != null && m.NextAttemptAt < cutoff)))
                || (m.State == MessageStates.Sending && m.UpdatedAt < cutoff)));
        var total = await query.CountAsync(ct);
        var rows = await query.OrderBy(m => m.CreatedAt).ThenBy(m => m.Id).Take(take).ToListAsync(ct);
        var content = await authorizer.CheckAsync(PermissionCodes.TicketsRead, departmentId, ct) == AccessOutcome.Allowed;
        var items = rows.Select(m =>
        {
            var category = MailErrors.Category(m.LastError);
            var reason = m.State == MessageStates.Sending ? "sending_stalled" : m.Attempts > 0 ? "retrying" : "overdue";
            return new WaitingItemDto(
                m.Id, m.MailboxId, m.State, reason, m.CreatedAt, m.UpdatedAt, m.NextAttemptAt, m.Attempts, content ? m.FromAddress : "",
                content ? (m.Subject.Length > 200 ? m.Subject[..200] : m.Subject) : "", m.LastError, category, MailProblemNature.Of(category), content ? m.TicketId : null);
        }).ToList();
        return new WaitingPage(items, total, WaitingAfterMinutes);
    }

    /// <summary>
    /// D12 / MSG-009: turns an IGNORED inbound message into (part of) a ticket by explicit decision. Allowed for the ordinary ignore reasons (auto-submitted, bulk, list mail, an
    /// auto-response marker, the sender rate limit); never for delivery reports or loops (the mailbox's own address), duplicates, drafts or mail without a sender. Content
    /// action: needs ticket read by membership as well (404 otherwise). The released message is processed as usual except that it is never acknowledged.
    /// </summary>
    public async Task<ReleaseOutcomeDto> ReleaseIgnoredAsync(Guid departmentId, Guid mailMessageId, CancellationToken ct)
    {
        await authorizer.RequireAsync(PermissionCodes.TicketsRead, departmentId, ct);

        var message = await db.Messages.SingleOrDefaultAsync(
            m => m.Id == mailMessageId && m.DepartmentId == departmentId && m.Direction == MessageDirections.Inbound && m.State == MessageStates.Ignored, ct) ?? throw new NotFoundException();
        string[] releasable = [IgnoreReasons.AutoSubmitted, IgnoreReasons.Bulk, IgnoreReasons.AutoResponseSuppress, IgnoreReasons.MailingList, IgnoreReasons.SenderRateLimited];
        if (message.IgnoreReason is not { } reason || !releasable.Contains(reason) || MessageFlags.Has(message.Flags, "ndr_correlated"))
        {
            throw new ConflictException("This message cannot be turned into a ticket (delivery reports, loops, duplicates and mail without a sender are never released).", ProblemCodes.InvalidState);
        }

        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            var now = clock.GetUtcNowMicroseconds();
            message.State = MessageStates.Received;
            message.IgnoreReason = null;
            message.Flags = MessageFlags.With(message.Flags, MessageFlags.Released);
            message.Attempts = 0;
            message.NextAttemptAt = now;
            message.UpdatedAt = now;
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(MailAuditActions.IgnoredReleased, MailAudit.MessageObject, message.Id, departmentId, new { reason }, new { state = MessageStates.Received }, ct);
            await tx.CommitAsync(ct);
        }

        await processor.ProcessAsync(message.MailboxId, message.ProviderMessageId!, ct);
        var released = await db.Messages.AsNoTracking().Where(m => m.Id == mailMessageId).Select(m => new { m.State, m.TicketId }).SingleAsync(ct);
        return new ReleaseOutcomeDto(released.State, released.TicketId);
    }

    /// <summary>
    /// Re-queues failed mail. An Uncertain outbound message is first reconciled against the provider sent copy (and simply marked Sent when it is found); only
    /// with <paramref name="acceptDuplicateRisk"/> is it re-sent, as a NEW message, knowing the requester may then receive it twice. Returns null for the plain retry.
    /// </summary>
    public async Task<RetryOutcomeDto?> RetryAsync(Guid departmentId, Guid mailMessageId, bool acceptDuplicateRisk, CancellationToken ct)
    {
        // Re-queuing re-sends or re-processes correspondence: Department content. The administrative permission on the route also lets a Platform Admin without a membership
        // through, so ticket read by membership is required as well (404 without one).
        await authorizer.RequireAsync(PermissionCodes.TicketsRead, departmentId, ct);

        var message = await db.Messages.AsNoTracking()
            .Where(m => m.Id == mailMessageId && m.DepartmentId == departmentId && (m.State == MessageStates.Failed || m.State == MessageStates.Uncertain))
            .Select(m => new { m.Direction, m.MailboxId, m.ProviderMessageId, m.State }).SingleOrDefaultAsync(ct) ?? throw new NotFoundException();
        if (message.State == MessageStates.Uncertain)
        {
            if (await sender.ReconcileAsync(mailMessageId, ct))
            {
                return new RetryOutcomeDto("reconciled");
            }

            if (!acceptDuplicateRisk)
            {
                throw new ConflictException(
                    "The provider has no record that this message was sent, but it may have been. Re-sending can deliver it twice. Confirm to re-send anyway.", MailProblemCodes.OutcomeUncertain);
            }

            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var at = clock.GetUtcNowMicroseconds();
            var requeued = await db.Database.ExecuteSqlAsync(
                $"UPDATE mail.messages SET state = 'Queued', attempts = 0, last_error = NULL, provider_draft_id = NULL, next_attempt_at = {at}, delivery_reported_at = NULL, updated_at = {at} WHERE id = {mailMessageId} AND department_id = {departmentId} AND state = 'Uncertain'",
                ct);
            if (requeued != 1)
            {
                throw new ConflictException("The message was resolved by someone else.", ProblemCodes.InvalidState);
            }

            await audit.WriteAsync(MailAuditActions.UncertainRetried, MailAudit.MessageObject, mailMessageId, departmentId, null, new { acceptedDuplicateRisk = true }, ct);
            await tx.CommitAsync(ct);
            scheduler.EnqueueSend(mailMessageId);
            return new RetryOutcomeDto("requeued");
        }

        if (message.Direction == MessageDirections.Outbound)
        {
            // A confirmed non-delivery report means the earlier send reached the provider: clear the draft so the retry is a genuine new send (see MailOutbox.RetryAsync).
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var now = clock.GetUtcNowMicroseconds();
            var ndr = MailErrorCodes.NdrDeliveryFailed;
            var resent = await db.Database.ExecuteSqlAsync(
                $"UPDATE mail.messages SET state = 'Queued', attempts = 0, last_error = NULL, provider_draft_id = CASE WHEN last_error = {ndr} THEN NULL ELSE provider_draft_id END, next_attempt_at = {now}, delivery_reported_at = NULL, updated_at = {now} WHERE id = {mailMessageId} AND department_id = {departmentId} AND state = 'Failed' AND last_error = {ndr}",
                ct);
            if (resent == 1)
            {
                await audit.WriteAsync(
                    MailAuditActions.ResentAfterBounce, MailAudit.MessageObject, mailMessageId, departmentId,
                    new { state = MessageStates.Failed, error = ndr }, new { state = MessageStates.Queued, resend = true }, ct);
            }
            else
            {
                var requeuedPlain = await db.Database.ExecuteSqlAsync(
                    $"UPDATE mail.messages SET state = 'Queued', attempts = 0, last_error = NULL, next_attempt_at = {now}, delivery_reported_at = NULL, updated_at = {now} WHERE id = {mailMessageId} AND department_id = {departmentId} AND state = 'Failed'",
                    ct);
                if (requeuedPlain == 1)
                {
                    await audit.WriteAsync(MailAuditActions.FailedRetried, MailAudit.MessageObject, mailMessageId, departmentId, new { state = MessageStates.Failed }, new { state = MessageStates.Queued }, ct);
                }
            }

            await tx.CommitAsync(ct);
            scheduler.EnqueueSend(mailMessageId);
            return null;
        }

        if (!await processor.RequeueAsync(departmentId, mailMessageId, ct))
        {
            throw new NotFoundException();
        }

        scheduler.EnqueueSync(message.MailboxId);
        return null;
    }

    // ----- helpers -----

    private async Task<Mailbox> FindAsync(Guid departmentId, Guid mailboxId, bool track, CancellationToken ct)
    {
        var query = track ? db.Mailboxes.Include(m => m.Sync) : db.Mailboxes.AsNoTracking().Include(m => m.Sync);

        // Resolved by BOTH ids: a mailbox of another department is simply not found.
        return await query.SingleOrDefaultAsync(m => m.Id == mailboxId && m.DepartmentId == departmentId, ct) ?? throw new NotFoundException();
    }

    private sealed class MailboxStats
    {
        public int InboundFailed { get; set; }

        public int OutboundFailed { get; set; }

        public int OutboundUncertain { get; set; }

        public DateTimeOffset? LastSendAt { get; set; }
    }

    /// <summary>Counts per mailbox of this department only (the department id is part of every query).</summary>
    private async Task<Dictionary<Guid, MailboxStats>> StatsAsync(Guid departmentId, CancellationToken ct)
    {
        var rows = await db.Messages.AsNoTracking()
            .Where(m => m.DepartmentId == departmentId && (m.State == MessageStates.Failed || m.State == MessageStates.Uncertain))
            .GroupBy(m => new { m.MailboxId, m.Direction, m.State }).Select(g => new { g.Key.MailboxId, g.Key.Direction, g.Key.State, Count = g.Count() }).ToListAsync(ct);
        var sends = await db.Messages.AsNoTracking()
            .Where(m => m.DepartmentId == departmentId && m.Direction == MessageDirections.Outbound && m.State == MessageStates.Sent)
            .GroupBy(m => m.MailboxId).Select(g => new { Id = g.Key, At = g.Max(m => m.UpdatedAt) }).ToListAsync(ct);
        var result = new Dictionary<Guid, MailboxStats>();
        MailboxStats Of(Guid id) => result.TryGetValue(id, out var s) ? s : result[id] = new MailboxStats();
        foreach (var r in rows)
        {
            var s = Of(r.MailboxId);
            if (r.State == MessageStates.Uncertain)
            {
                s.OutboundUncertain += r.Count;
            }
            else if (r.Direction == MessageDirections.Outbound)
            {
                s.OutboundFailed += r.Count;
            }
            else
            {
                s.InboundFailed += r.Count;
            }
        }

        foreach (var s in sends)
        {
            Of(s.Id).LastSendAt = s.At;
        }

        return result;
    }

    private MailboxDto ToDto(Mailbox m, MailboxStats stats) =>
        new(
            m.Id, m.DepartmentId, m.Address, m.DisplayName, m.Provider, m.IsActive, m.IsDefault, ConcurrencyVersion.ToToken(m.Version),
            new SubscriptionDto(m.Sync?.SubscriptionState ?? SubscriptionStates.None, m.Sync?.SubscriptionExpiresAt),
            m.Sync?.LastSyncAt, m.Sync?.LastSyncError, stats.InboundFailed + stats.OutboundFailed + stats.OutboundUncertain,
            HealthOf(m, stats), stats.LastSendAt, stats.OutboundFailed, stats.OutboundUncertain);

    private MailboxHealthDto HealthOf(Mailbox m, MailboxStats stats)
    {
        if (!m.IsActive)
        {
            return new MailboxHealthDto("Disabled", ["mailbox_inactive"]);
        }

        var now = clock.GetUtcNowMicroseconds();
        var sync = m.Sync;
        if (sync is null || (sync.SubscriptionState == SubscriptionStates.None && sync.LastSyncAt is null))
        {
            return new MailboxHealthDto("NotConfigured", ["subscription_not_created"]);
        }

        var failed = new List<string>();
        var attention = new List<string>();
        if (sync.SubscriptionState == SubscriptionStates.Failed)
        {
            failed.Add("subscription_failed");
        }
        else if (sync.SubscriptionState == SubscriptionStates.None)
        {
            attention.Add("subscription_missing");
        }
        else if (sync.SubscriptionExpiresAt is { } expires)
        {
            if (expires <= now)
            {
                failed.Add("subscription_expired");
            }
            else if (expires - now < ExpiringSoon)
            {
                attention.Add("subscription_expiring");
            }
        }

        if (sync.ConsecutiveFailures >= 3)
        {
            failed.Add("sync_failing");
        }
        else if (sync.ConsecutiveFailures > 0)
        {
            attention.Add("sync_failing");
        }
        else if (sync.LastSyncAt is { } last && now - last > StaleSyncAfter)
        {
            attention.Add("sync_stale");
        }

        if (stats.OutboundUncertain > 0)
        {
            attention.Add("outbound_uncertain");
        }

        if (stats.OutboundFailed > 0)
        {
            attention.Add("outbound_failed");
        }

        if (stats.InboundFailed > 0)
        {
            attention.Add("inbound_failed");
        }

        return failed.Count > 0 ? new MailboxHealthDto("Failed", [.. failed, .. attention]) : attention.Count > 0 ? new MailboxHealthDto("Attention", attention) : new MailboxHealthDto("Healthy", []);
    }

    private static void ValidateDisplayName(Dictionary<string, string[]> errors, string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > 200 || value.Contains('\0', StringComparison.Ordinal))
        {
            errors["displayName"] = ["displayName is required and at most 200 characters."];
        }
    }

    private static void ThrowIfAny(Dictionary<string, string[]> errors)
    {
        if (errors.Count > 0)
        {
            throw new RequestValidationException(errors);
        }
    }

    private async Task SaveAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictException("The mailbox was changed by someone else. Reload and retry.", ProblemCodes.VersionConflict);
        }
        catch (DbUpdateException ex) when (PostgresErrors.IsUniqueViolation(ex, out var constraint))
        {
            throw new ConflictException(
                constraint == "ux_mailboxes_address" ? "A mailbox with this address already exists." : "A mailbox with these values already exists.", ProblemCodes.Duplicate);
        }
    }
}

internal sealed class MailboxDirectory(MailDbContext db) : IMailboxDirectory
{
    public async Task<ReplyMailbox?> FindReplyMailboxAsync(Guid departmentId, Guid? ticketMailboxId, CancellationToken cancellationToken = default)
    {
        var active = await db.Mailboxes.AsNoTracking().Where(m => m.DepartmentId == departmentId && m.IsActive)
            .Select(m => new { m.Id, m.Address, m.DisplayName, m.IsDefault }).ToListAsync(cancellationToken);

        // A ticket that has its own mailbox is answered from THAT mailbox or not at all: when it is unavailable the reply is recorded as not delivered and an
        // administrator must act; it is never silently sent from another identity. Only a ticket with no mailbox of its own uses the department default
        // (else the only active one). Never another department's, never a global sender.
        var chosen = ticketMailboxId is not null
            ? active.FirstOrDefault(m => m.Id == ticketMailboxId)
            : active.FirstOrDefault(m => m.IsDefault) ?? (active.Count == 1 ? active[0] : null);
        return chosen is null ? null : new ReplyMailbox(chosen.Id, chosen.Address, chosen.DisplayName, chosen.IsDefault);
    }

    public async Task<bool> HasExternalSendAsync(Guid departmentId, Guid ticketId, CancellationToken cancellationToken = default) =>
        await db.Messages.AsNoTracking().AnyAsync(
            m => m.DepartmentId == departmentId && m.TicketId == ticketId && m.Direction == MessageDirections.Outbound
                && (m.State == MessageStates.Queued || m.State == MessageStates.Sending || m.State == MessageStates.Sent || m.State == MessageStates.Uncertain
                    || (m.State == MessageStates.Failed && (m.LastError == MailErrorCodes.NdrDeliveryFailed || (m.Flags != null && m.Flags.Contains(MessageFlags.SubmitStarted))))),
            cancellationToken);

    public async Task<IReadOnlyList<ReplyMailbox>> ListActiveAsync(Guid departmentId, CancellationToken cancellationToken = default) =>
        await db.Mailboxes.AsNoTracking().Where(m => m.DepartmentId == departmentId && m.IsActive).OrderByDescending(m => m.IsDefault).ThenBy(m => m.DisplayName)
            .Select(m => new ReplyMailbox(m.Id, m.Address, m.DisplayName, m.IsDefault)).ToListAsync(cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, ReplyMailbox>> GetManyAsync(Guid departmentId, IReadOnlyCollection<Guid> mailboxIds, CancellationToken cancellationToken = default) =>
        mailboxIds.Count == 0
            ? new Dictionary<Guid, ReplyMailbox>()
            : await db.Mailboxes.AsNoTracking().Where(m => m.DepartmentId == departmentId && mailboxIds.Contains(m.Id))
                .ToDictionaryAsync(m => m.Id, m => new ReplyMailbox(m.Id, m.Address, m.DisplayName, m.IsDefault), cancellationToken);
}

internal sealed class MailboxHealthReader(MailboxService mailboxes) : IMailboxHealthReader
{
    public async Task<IReadOnlyList<MailboxHealthInfo>> ListAsync(Guid departmentId, CancellationToken cancellationToken = default) =>
        [.. (await mailboxes.ListAsync(departmentId, cancellationToken)).Select(m => new MailboxHealthInfo(
            m.Id, m.Address, m.DisplayName, m.Health.Status, m.Health.Reasons, m.OutboundFailedCount, m.OutboundUncertainCount, m.LastSendAt))];
}
