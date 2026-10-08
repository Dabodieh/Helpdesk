using Helpdesk.Modules.Ticketing.Contracts;
using Helpdesk.Modules.Ticketing.Domain;
using Helpdesk.Modules.Ticketing.Persistence;
using Helpdesk.SharedKernel.Database;
using Helpdesk.SharedKernel.Security;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Helpdesk.Modules.Ticketing.Services;

/// <summary>
/// "A change that can notify somebody happened" for one ticket. Written in the SAME SaveChanges transaction as the ticket or message change
/// (<see cref="NotificationOutboxInterceptor"/>), so the change cannot commit without it, and drained idempotently by <see cref="NotificationOutboxDrainer"/>.
/// <see cref="Kind"/> is a <see cref="TicketEventKinds"/> value (assignment, status, priority or message). It carries ids only.
/// </summary>
internal sealed class NotificationOutboxItem
{
    public Guid Id { get; set; }

    public Guid DepartmentId { get; set; }

    public Guid TicketId { get; set; }

    public string Kind { get; set; } = "";

    /// <summary>The signed-in user whose request made the change (never notified about it); null for ingestion and system work.</summary>
    public Guid? ActorUserId { get; set; }

    /// <summary>For a mention: the one user to notify; the internal note it is in is <see cref="MessageId"/>. Null for every other kind.</summary>
    public Guid? SubjectUserId { get; set; }

    public Guid? MessageId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? ProcessedAt { get; set; }

    /// <summary>Failed processing attempts. After <see cref="NotificationOutboxDrainer.MaxAttempts"/> the item is parked (stays unprocessed and visible, no longer retried).</summary>
    public int Attempts { get; set; }

    /// <summary>Exception type name of the last failure (never a message).</summary>
    public string? LastError { get; set; }
}

/// <summary>
/// Stages <see cref="NotificationOutboxItem"/> rows inside the same SaveChanges (hence the same transaction) as the mutation that qualifies: an assignee change
/// (including the initial assignee of a new ticket), a status change, a priority change, a new PUBLIC message (requester or agent), or a new @mention row of an internal
/// note (the mention, never the note itself, is the intent). Other internal-note content, team-only
/// changes and every other edit stage nothing.
///
/// IMPORTANT LIMIT: this only sees EF-TRACKED mutations. A relevant path that uses ExecuteUpdate or raw SQL (none does today for these fields) would be invisible
/// here and MUST explicitly insert an outbox row in its own transaction, or be redesigned to use tracked entities. Do not widen this into a general event framework.
/// </summary>
internal sealed class NotificationOutboxInterceptor(IServiceProvider services) : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Stage(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Stage(eventData.Context);
        return ValueTask.FromResult(result);
    }

    private void Stage(DbContext? context)
    {
        if (context is not TicketingDbContext db)
        {
            return;
        }

        var user = services.GetService(typeof(ICurrentUser)) as ICurrentUser;
        Guid? actor = user is { IsAuthenticated: true } ? user.UserId : null;
        var now = ClockExtensions.GetUtcNowMicroseconds((TimeProvider?)services.GetService(typeof(TimeProvider)) ?? TimeProvider.System);
        var staged = new HashSet<(Guid Ticket, string Kind, Guid? Subject)>();
        void Add(Guid departmentId, Guid ticketId, string kind, Guid? subject = null, Guid? messageId = null)
        {
            if (staged.Add((ticketId, kind, subject)))
            {
                db.NotificationOutbox.Add(new NotificationOutboxItem
                {
                    Id = Guid.CreateVersion7(), DepartmentId = departmentId, TicketId = ticketId, Kind = kind, ActorUserId = actor, SubjectUserId = subject, MessageId = messageId, CreatedAt = now,
                });
            }
        }

        foreach (var entry in db.ChangeTracker.Entries().ToList())
        {
            switch (entry.Entity)
            {
                case Ticket ticket when entry.State == EntityState.Added:
                    if (ticket.AssigneeUserId is not null)
                    {
                        Add(ticket.DepartmentId, ticket.Id, TicketEventKinds.Assignment);
                    }

                    break;
                case Ticket ticket when entry.State == EntityState.Modified:
                    if (Changed(entry, nameof(Ticket.AssigneeUserId)))
                    {
                        Add(ticket.DepartmentId, ticket.Id, TicketEventKinds.Assignment);
                    }

                    if (Changed(entry, nameof(Ticket.Status)))
                    {
                        Add(ticket.DepartmentId, ticket.Id, TicketEventKinds.Status);
                    }

                    if (Changed(entry, nameof(Ticket.Priority)))
                    {
                        Add(ticket.DepartmentId, ticket.Id, TicketEventKinds.Priority);
                    }

                    break;
                case Message message when entry.State == EntityState.Added && message.Kind == MessageKind.Public:
                    // The message id is kept so a late drain classifies THIS message (requester reply or agent reply), not whatever is latest by then (BE-015).
                    Add(message.DepartmentId, message.TicketId, TicketEventKinds.Message, messageId: message.Id);
                    break;
                case MessageMention mention when entry.State == EntityState.Added:
                    // One intent per mentioned user (a duplicate is impossible: the key is note + user).
                    Add(mention.DepartmentId, mention.TicketId, TicketEventKinds.Mention, mention.UserId, mention.MessageId);
                    break;
            }
        }
    }

    private static bool Changed(Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry entry, string property)
    {
        var p = entry.Property(property);
        return p.IsModified && !Equals(p.OriginalValue, p.CurrentValue);
    }
}

/// <summary>
/// Drains the outbox into notifications. Each item is claimed, turned into notifications (the existing <see cref="NotificationRecorder"/> rules: dedupe, actor and
/// permission suppression) and marked processed in ONE transaction, so a replay or a second worker can never duplicate; a failure rolls the item back to be
/// retried. SignalR only gets an empty refresh prompt afterwards. Called right after a change (fast path) and by a recurring Hangfire job (the durable path).
/// </summary>
internal sealed partial class NotificationOutboxDrainer(TicketingDbContext db, NotificationRecorder recorder, IHubContext<TicketHub> hub, TimeProvider clock, ILogger<NotificationOutboxDrainer> log)
    : INotificationOutbox
{
    private const int Batch = 100;

    /// <summary>Failed attempts after which an item is parked: it stays unprocessed and visible (and in the age metric) but no longer blocks or slows newer items (BE-005).</summary>
    public const int MaxAttempts = 5;

    public async Task<int> DrainAsync(CancellationToken cancellationToken)
    {
        var ids = await db.NotificationOutbox.AsNoTracking().Where(i => i.ProcessedAt == null && i.Attempts < MaxAttempts).OrderBy(i => i.Attempts).ThenBy(i => i.CreatedAt).ThenBy(i => i.Id).Take(Batch).Select(i => i.Id).ToListAsync(cancellationToken);
        var processed = 0;
        foreach (var id in ids)
        {
            try
            {
                var notified = await ProcessAsync(id, cancellationToken);
                if (notified is null)
                {
                    continue;
                }

                processed++;
                foreach (var user in notified)
                {
                    await PromptAsync(user, cancellationToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                db.ChangeTracker.Clear();
                var error = ex.GetType().Name;
                await db.NotificationOutbox.Where(i => i.Id == id && i.ProcessedAt == null)
                    .ExecuteUpdateAsync(s => s.SetProperty(i => i.Attempts, i => i.Attempts + 1).SetProperty(i => i.LastError, error), cancellationToken);
                DrainFailed(log, id, error);
            }
        }

        return processed;
    }

    /// <summary>Null when another worker already handled the item.</summary>
    private async Task<IReadOnlyCollection<Guid>?> ProcessAsync(Guid id, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var now = ClockExtensions.GetUtcNowMicroseconds(clock);
        var claimed = await db.Database.ExecuteSqlAsync($"UPDATE ticketing.notification_outbox SET processed_at = {now} WHERE id = {id} AND processed_at IS NULL", ct);
        if (claimed == 0)
        {
            await tx.RollbackAsync(ct);
            return null;
        }

        var item = await db.NotificationOutbox.AsNoTracking().SingleAsync(i => i.Id == id, ct);
        var notified = await recorder.RecordAsync(item.DepartmentId, item.TicketId, item.Kind, item.ActorUserId, ct, item.SubjectUserId, item.MessageId);
        await tx.CommitAsync(ct);
        return notified;
    }

    private async Task PromptAsync(Guid user, CancellationToken ct)
    {
        try
        {
            await hub.Clients.Group(HubGroups.User(user)).SendAsync("notificationsChanged", ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            PromptFailed(log, ex.GetType().Name);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Notification outbox item {ItemId} failed ({Error}); it will be retried")]
    private static partial void DrainFailed(ILogger logger, Guid itemId, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Notification refresh prompt failed ({Error}); clients reconcile through polling")]
    private static partial void PromptFailed(ILogger logger, string error);
}
