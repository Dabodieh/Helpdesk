using Helpdesk.Modules.Organisation.Contracts;
using Helpdesk.Modules.Ticketing.Domain;
using Helpdesk.Modules.Ticketing.Persistence;
using Helpdesk.SharedKernel.Authorization;
using Helpdesk.SharedKernel.Database;
using Helpdesk.SharedKernel.Errors;
using Helpdesk.SharedKernel.Security;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Modules.Ticketing.Services;

/// <summary>Safe categories of an in-app notification. Never message text: the row stores ids and a type only.</summary>
internal static class NotificationTypes
{
    public const string Assigned = "assigned";
    public const string RequesterReply = "requester_reply";
    public const string FollowedUpdate = "followed_update";
    public const string Mentioned = "mentioned";

    public static bool IsValid(string value) => value is Assigned or RequesterReply or FollowedUpdate or Mentioned;
}

/// <summary>
/// Turns committed ticket events into durable notifications (called by the event publisher BEFORE any realtime push, so SignalR failing never loses one).
/// A recipient gets at most ONE unread notification per ticket (a database unique index): further qualifying events update it, keeping the strongest reason
/// (assigned over requester reply over followed update). The acting user is never notified, a recipient without tickets.read in the department is skipped,
/// internal notes never notify, and nothing here carries message content. Known limit: the notification is written after the ticket change committed (the
/// existing committed-event pattern), so a process crash in between loses it; the ticket itself is the truth.
/// </summary>
internal sealed class NotificationRecorder(TicketingDbContext db, IOrganisationDirectory organisation, TimeProvider clock)
{
    /// <summary>Returns the users whose notification list changed (so the caller can prompt them to refresh).</summary>
    public async Task<IReadOnlyCollection<Guid>> RecordAsync(
        Guid departmentId, Guid ticketId, string kind, Guid? actorUserId, CancellationToken ct, Guid? subjectUserId = null, Guid? messageId = null)
    {
        if (kind is not (TicketEventKinds.Created or TicketEventKinds.Assignment or TicketEventKinds.Message or TicketEventKinds.Status or TicketEventKinds.Priority or TicketEventKinds.Mention))
        {
            return [];
        }

        var ticket = await db.Tickets.AsNoTracking().IgnoreDepartmentScope().Where(t => t.Id == ticketId && t.DepartmentId == departmentId)
            .Select(t => new { t.AssigneeUserId, t.MergedIntoTicketId }).SingleOrDefaultAsync(ct);
        if (ticket is null || ticket.MergedIntoTicketId is not null)
        {
            return [];
        }

        var recipients = new Dictionary<Guid, string>();
        void Add(Guid user, string type)
        {
            if (!recipients.TryGetValue(user, out var existing) || Rank(type) > Rank(existing))
            {
                recipients[user] = type;
            }
        }

        var followers = kind is TicketEventKinds.Created or TicketEventKinds.Mention // following never comes from a mention
            ? []
            : await db.TicketFollowers.AsNoTracking().IgnoreDepartmentScope().Where(f => f.TicketId == ticketId && f.DepartmentId == departmentId).Select(f => f.UserId).ToListAsync(ct);

        switch (kind)
        {
            case TicketEventKinds.Created or TicketEventKinds.Assignment:
                // "Assigned to me" only when the assignee actually changed since we last looked (a team-only change is also an assignment event).
                var state = await db.TicketNotifyStates.IgnoreDepartmentScope().SingleOrDefaultAsync(s => s.TicketId == ticketId, ct);
                if (ticket.AssigneeUserId is { } assignee && assignee != state?.AssigneeUserId)
                {
                    Add(assignee, NotificationTypes.Assigned);
                }

                if (state is null)
                {
                    db.TicketNotifyStates.Add(new TicketNotifyState { TicketId = ticketId, DepartmentId = departmentId, AssigneeUserId = ticket.AssigneeUserId });
                }
                else
                {
                    state.AssigneeUserId = ticket.AssigneeUserId;
                }

                await db.SaveChangesAsync(ct);
                foreach (var f in followers)
                {
                    Add(f, NotificationTypes.FollowedUpdate);
                }

                break;
            case TicketEventKinds.Mention:
                if (subjectUserId is { } mentioned)
                {
                    Add(mentioned, NotificationTypes.Mentioned);
                }

                break;
            case TicketEventKinds.Message:
                // Who wrote the latest public message decides: a requester reply reaches the assignee and followers; an agent reply is only an update for followers.
                var author = await db.Messages.AsNoTracking().IgnoreDepartmentScope().Where(m => m.TicketId == ticketId && m.Kind == MessageKind.Public)
                    .OrderByDescending(m => m.CreatedAt).ThenByDescending(m => m.Id).Select(m => (AuthorType?)m.AuthorType).FirstOrDefaultAsync(ct);
                if (author == AuthorType.Requester)
                {
                    if (ticket.AssigneeUserId is { } a)
                    {
                        Add(a, NotificationTypes.RequesterReply);
                    }

                    foreach (var f in followers)
                    {
                        Add(f, NotificationTypes.RequesterReply);
                    }
                }
                else if (author == AuthorType.Agent)
                {
                    foreach (var f in followers)
                    {
                        Add(f, NotificationTypes.FollowedUpdate);
                    }
                }

                break;
            default:
                foreach (var f in followers)
                {
                    Add(f, NotificationTypes.FollowedUpdate);
                }

                break;
        }

        if (actorUserId is { } actor)
        {
            recipients.Remove(actor); // never about one's own action
        }

        var now = ClockExtensions.GetUtcNowMicroseconds(clock);
        var notified = new List<Guid>();
        foreach (var (user, type) in recipients)
        {
            // Authorisation at creation: only someone who may read this department's tickets is notified (and a notification never grants anything later).
            if (!(await organisation.GetDepartmentsWithPermissionAsync(user, PermissionCodes.TicketsRead, ct)).Contains(departmentId))
            {
                continue;
            }

            // A mention points into an internal note: only someone who may read internal notes is told.
            if (type == NotificationTypes.Mentioned && !(await organisation.GetDepartmentsWithPermissionAsync(user, PermissionCodes.TicketsNotesRead, ct)).Contains(departmentId))
            {
                continue;
            }

            var linkMessage = type == NotificationTypes.Mentioned ? messageId : null;
            await db.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO ticketing.notifications (id, user_id, department_id, ticket_id, type, message_id, created_at)
                VALUES ({Guid.CreateVersion7()}, {user}, {departmentId}, {ticketId}, {type}, {linkMessage}, {now})
                ON CONFLICT (user_id, ticket_id) WHERE read_at IS NULL DO UPDATE SET
                    type = CASE WHEN (CASE EXCLUDED.type WHEN 'mentioned' THEN 4 WHEN 'assigned' THEN 3 WHEN 'requester_reply' THEN 2 ELSE 1 END)
                                   > (CASE notifications.type WHEN 'mentioned' THEN 4 WHEN 'assigned' THEN 3 WHEN 'requester_reply' THEN 2 ELSE 1 END)
                                THEN EXCLUDED.type ELSE notifications.type END,
                    message_id = CASE WHEN EXCLUDED.type = 'mentioned' THEN EXCLUDED.message_id ELSE notifications.message_id END,
                    created_at = EXCLUDED.created_at
                """,
                ct);
            notified.Add(user);
        }

        return notified;
    }

    private static int Rank(string type) => type switch
    {
        NotificationTypes.Mentioned => 4,
        NotificationTypes.Assigned => 3,
        NotificationTypes.RequesterReply => 2,
        _ => 1,
    };
}

internal sealed record NotificationDto(Guid Id, string Type, DateTimeOffset CreatedAt, bool Read, string Reference, string Subject, Guid? MessageId = null);

internal sealed record NotificationListDto(int UnreadCount, IReadOnlyList<NotificationDto> Items);

/// <summary>
/// The caller's own notifications. Every read joins the ticket through the department-scope filter (tickets.read by membership; a platform administrator has
/// none), so a notification whose ticket the caller can no longer read is not listed, not counted and reveals nothing. Opening the link goes through the
/// normal ticket authorisation: a notification is a pointer, never access.
/// </summary>
internal sealed class NotificationService(TicketingDbContext db, IDepartmentScope scope, IAuthorizer authorizer, ICurrentUser current, TimeProvider clock)
{
    public const int DefaultLimit = 20;
    public const int MaxLimit = 50;

    public async Task<NotificationListDto> ListAsync(int? limit, CancellationToken ct)
    {
        var take = limit ?? DefaultLimit;
        if (take is < 1 or > MaxLimit)
        {
            throw new RequestValidationException("limit", $"limit must be between 1 and {MaxLimit}.");
        }

        await scope.EnsureLoadedAsync(ct);
        var me = current.UserId;
        var visible = await VisibleAsync(me, ct);
        var unread = await visible.CountAsync(x => x.N.ReadAt == null, ct);
        var rows = await visible.OrderByDescending(x => x.N.CreatedAt).ThenByDescending(x => x.N.Id).Take(take)
            .Select(x => new NotificationDto(x.N.Id, x.N.Type, x.N.CreatedAt, x.N.ReadAt != null, x.T.Reference, x.T.Subject, x.N.Type == NotificationTypes.Mentioned ? x.N.MessageId : null)).ToListAsync(ct);
        return new NotificationListDto(unread, rows);
    }

    public async Task<int> UnreadCountAsync(CancellationToken ct)
    {
        await scope.EnsureLoadedAsync(ct);
        return await (await VisibleAsync(current.UserId, ct)).CountAsync(x => x.N.ReadAt == null, ct);
    }

    public async Task MarkReadAsync(Guid id, CancellationToken ct)
    {
        var now = ClockExtensions.GetUtcNowMicroseconds(clock);
        var me = current.UserId;
        var updated = await db.Database.ExecuteSqlAsync($"UPDATE ticketing.notifications SET read_at = {now} WHERE id = {id} AND user_id = {me} AND read_at IS NULL", ct);
        if (updated == 0 && !await db.Notifications.AsNoTracking().AnyAsync(n => n.Id == id && n.UserId == me, ct))
        {
            throw new NotFoundException();
        }
    }

    public async Task MarkAllReadAsync(CancellationToken ct)
    {
        var now = ClockExtensions.GetUtcNowMicroseconds(clock);
        var me = current.UserId;
        await db.Database.ExecuteSqlAsync($"UPDATE ticketing.notifications SET read_at = {now} WHERE user_id = {me} AND read_at IS NULL", ct);
    }

    /// <summary>
    /// Ticket access through the department scope; a MENTION additionally needs internal-note access in that department NOW (it points into a note), so a
    /// revoked note permission hides it and its count even while the ticket itself stays readable.
    /// </summary>
    private async Task<IQueryable<NotificationRow>> VisibleAsync(Guid userId, CancellationToken ct)
    {
        var noteDepartments = (await authorizer.AccessibleDepartmentsAsync(PermissionCodes.TicketsNotesRead, ct)).ToArray();
        return db.Notifications.AsNoTracking().Where(n => n.UserId == userId && (n.Type != NotificationTypes.Mentioned || noteDepartments.Contains(n.DepartmentId)))
            .Join(db.Tickets.AsNoTracking(), n => n.TicketId, t => t.Id, (n, t) => new NotificationRow { N = n, T = t })
            .Where(x => x.T.DepartmentId == x.N.DepartmentId);
    }

    private sealed class NotificationRow
    {
        public required Notification N { get; init; }

        public required Ticket T { get; init; }
    }
}
