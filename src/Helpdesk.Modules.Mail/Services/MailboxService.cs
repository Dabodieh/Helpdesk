using System.Globalization;
using System.Text.RegularExpressions;
using Helpdesk.Modules.Mail.Contracts;
using Helpdesk.Modules.Mail.Domain;
using Helpdesk.Modules.Mail.Persistence;
using Helpdesk.Modules.Organisation.Contracts;
using Helpdesk.SharedKernel.Database;
using Helpdesk.SharedKernel.Errors;
using Helpdesk.SharedKernel.Paging;
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
    int AttentionCount);

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
    Guid? TicketId);

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
    TimeProvider clock)
{
    private const int MaxLimit = 100;

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex EmailPattern();

    public async Task<IReadOnlyList<MailboxDto>> ListAsync(Guid departmentId, CancellationToken ct)
    {
        var mailboxes = await db.Mailboxes.AsNoTracking().Include(m => m.Sync)
            .Where(m => m.DepartmentId == departmentId).OrderByDescending(m => m.IsDefault).ThenBy(m => m.Address).ToListAsync(ct);
        var attention = await AttentionCountsAsync(departmentId, ct);
        return [.. mailboxes.Select(m => ToDto(m, attention.GetValueOrDefault(m.Id)))];
    }

    public async Task<MailboxDto> GetAsync(Guid departmentId, Guid mailboxId, CancellationToken ct)
    {
        var mailbox = await FindAsync(departmentId, mailboxId, track: false, ct);
        var attention = await AttentionCountsAsync(departmentId, ct);
        return ToDto(mailbox, attention.GetValueOrDefault(mailbox.Id));
    }

    public async Task<MailboxDto> CreateAsync(Guid departmentId, CreateMailboxRequest request, CancellationToken ct)
    {
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

        var now = clock.GetUtcNowMicroseconds();
        var mailbox = new Mailbox
        {
            Id = Guid.CreateVersion7(),
            DepartmentId = departmentId,
            Address = address!,
            AddressNormalised = Mailbox.Normalise(address!),
            DisplayName = request.DisplayName!.Trim(),
            Provider = options.Value.Provider.ToLowerInvariant(),
            ProviderRef = string.IsNullOrWhiteSpace(request.ProviderRef) ? null : request.ProviderRef.Trim(),
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

    /// <summary>Failed and ignored mail of the department, newest first (nothing is dropped silently; this is the visible list).</summary>
    public async Task<AttentionPage> AttentionAsync(Guid departmentId, string? cursor, int? limit, CancellationToken ct)
    {
        var take = limit ?? 50;
        if (take is < 1 or > MaxLimit)
        {
            throw new RequestValidationException("limit", $"limit must be between 1 and {MaxLimit}.");
        }

        var query = db.Messages.AsNoTracking().Where(m => m.DepartmentId == departmentId && (m.State == MessageStates.Failed || m.State == MessageStates.Ignored));
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

        var items = rows.Select(m => new AttentionItemDto(
            m.Id, m.MailboxId, m.Direction, m.State, m.IgnoreReason, m.OccurredAt, m.FromAddress, m.Subject.Length > 200 ? m.Subject[..200] : m.Subject,
            m.LastError, m.Attempts, m.TicketId)).ToList();
        var next = more ? Cursor.Encode(string.Create(CultureInfo.InvariantCulture, $"{rows[^1].CreatedAt.UtcTicks}|{rows[^1].Id}")) : null;
        return new AttentionPage(items, next);
    }

    public async Task RetryAsync(Guid departmentId, Guid mailMessageId, CancellationToken ct)
    {
        var message = await db.Messages.AsNoTracking().Where(m => m.Id == mailMessageId && m.DepartmentId == departmentId && m.State == MessageStates.Failed)
            .Select(m => new { m.Direction, m.MailboxId, m.ProviderMessageId }).SingleOrDefaultAsync(ct) ?? throw new NotFoundException();
        if (message.Direction == MessageDirections.Outbound)
        {
            var now = clock.GetUtcNowMicroseconds();
            await db.Database.ExecuteSqlAsync(
                $"UPDATE mail.messages SET state = 'Queued', attempts = 0, last_error = NULL, next_attempt_at = {now}, delivery_reported_at = NULL, updated_at = {now} WHERE id = {mailMessageId} AND department_id = {departmentId} AND state = 'Failed'",
                ct);
            scheduler.EnqueueSend(mailMessageId);
            return;
        }

        if (!await processor.RequeueAsync(departmentId, mailMessageId, ct))
        {
            throw new NotFoundException();
        }

        scheduler.EnqueueSync(message.MailboxId);
    }

    // ----- helpers -----

    private async Task<Mailbox> FindAsync(Guid departmentId, Guid mailboxId, bool track, CancellationToken ct)
    {
        var query = track ? db.Mailboxes.Include(m => m.Sync) : db.Mailboxes.AsNoTracking().Include(m => m.Sync);

        // Resolved by BOTH ids: a mailbox of another department is simply not found.
        return await query.SingleOrDefaultAsync(m => m.Id == mailboxId && m.DepartmentId == departmentId, ct) ?? throw new NotFoundException();
    }

    private async Task<Dictionary<Guid, int>> AttentionCountsAsync(Guid departmentId, CancellationToken ct) =>
        await db.Messages.AsNoTracking()
            .Where(m => m.DepartmentId == departmentId && m.State == MessageStates.Failed)
            .GroupBy(m => m.MailboxId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);

    private static MailboxDto ToDto(Mailbox m, int attention) =>
        new(
            m.Id, m.DepartmentId, m.Address, m.DisplayName, m.Provider, m.IsActive, m.IsDefault, ConcurrencyVersion.ToToken(m.Version),
            new SubscriptionDto(m.Sync?.SubscriptionState ?? SubscriptionStates.None, m.Sync?.SubscriptionExpiresAt),
            m.Sync?.LastSyncAt, m.Sync?.LastSyncError, attention);

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

        // The ticket's own mailbox; else the department's default; else the only active one. Never another department's, never a global sender.
        var chosen = active.FirstOrDefault(m => m.Id == ticketMailboxId)
            ?? active.FirstOrDefault(m => m.IsDefault)
            ?? (active.Count == 1 ? active[0] : null);
        return chosen is null ? null : new ReplyMailbox(chosen.Id, chosen.Address, chosen.DisplayName, chosen.IsDefault);
    }

    public async Task<IReadOnlyList<ReplyMailbox>> ListActiveAsync(Guid departmentId, CancellationToken cancellationToken = default) =>
        await db.Mailboxes.AsNoTracking().Where(m => m.DepartmentId == departmentId && m.IsActive).OrderByDescending(m => m.IsDefault).ThenBy(m => m.DisplayName)
            .Select(m => new ReplyMailbox(m.Id, m.Address, m.DisplayName, m.IsDefault)).ToListAsync(cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, ReplyMailbox>> GetManyAsync(Guid departmentId, IReadOnlyCollection<Guid> mailboxIds, CancellationToken cancellationToken = default) =>
        mailboxIds.Count == 0
            ? new Dictionary<Guid, ReplyMailbox>()
            : await db.Mailboxes.AsNoTracking().Where(m => m.DepartmentId == departmentId && mailboxIds.Contains(m.Id))
                .ToDictionaryAsync(m => m.Id, m => new ReplyMailbox(m.Id, m.Address, m.DisplayName, m.IsDefault), cancellationToken);
}
