using System.Collections.Concurrent;
using System.Security.Claims;
using Helpdesk.Modules.Identity.Contracts;
using Helpdesk.Modules.Organisation.Contracts;
using Helpdesk.SharedKernel.Authorization;
using Helpdesk.SharedKernel.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Helpdesk.Modules.Ticketing.Services;

/// <summary>What happened to a ticket. Deliberately content-free: clients react by re-reading through the authorised REST API.</summary>
internal static class TicketEventKinds
{
    public const string Created = "created";
    public const string Updated = "updated";
    public const string Assignment = "assignment";
    public const string Status = "status";
    public const string Priority = "priority";
    public const string Message = "message";
    public const string Note = "note";
    public const string Delivery = "delivery";
    public const string Attachment = "attachment";
    public const string Sla = "sla";

    /// <summary>Outbox-only: a user was @mentioned in an internal note. Never published to clients (note events use <see cref="Note"/>).</summary>
    public const string Mention = "mention";
}

/// <summary>Payload of the <c>ticketChanged</c> hub message. No subject, body, names or addresses: ids and a kind only.</summary>
internal sealed record TicketChangedMessage(Guid TicketId, Guid DepartmentId, string Reference, string Kind, Guid? ActorUserId, DateTimeOffset At);

/// <summary>Payload of the requester portal event <c>requestChanged</c>: the request reference and a coarse kind (reply, status, attachment). Nothing else.</summary>
internal sealed record PortalRequestChanged(string Reference, string Kind, DateTimeOffset At);

internal interface ITicketEvents
{
    /// <summary>Publishes after the change committed. Never throws into the request: realtime is best-effort, REST/database state is the truth.</summary>
    Task PublishAsync(Guid departmentId, Guid ticketId, string reference, string kind, Guid? actorUserId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Group names are decided only by the server. <c>d:</c> carries everything a department member with tickets.read may know about;
/// <c>n:</c> carries internal-note events and is joined only with tickets.notes.read (so a note event never reaches anyone who could
/// not read the note).
/// </summary>
internal static class HubGroups
{
    public static string Department(Guid departmentId) => "d:" + departmentId.ToString("N");

    public static string Notes(Guid departmentId) => "n:" + departmentId.ToString("N");

    /// <summary>The requester portal group of one Requester: only requester-visible events are ever sent to it.</summary>
    public static string Requester(Guid requesterId) => "r:" + requesterId.ToString("N");

    /// <summary>One user's own connections: only an empty "notifications changed" prompt is ever sent here.</summary>
    public static string User(Guid userId) => "u:" + userId.ToString("N");
}

internal sealed partial class TicketEventPublisher(IHubContext<TicketHub> hub, IServiceScopeFactory scopes, TimeProvider clock, ILogger<TicketEventPublisher> log) : ITicketEvents
{
    public async Task PublishAsync(Guid departmentId, Guid ticketId, string reference, string kind, Guid? actorUserId, CancellationToken cancellationToken = default)
    {
        // Notifications were committed with the change itself (NotificationOutbox); this is only the fast path that drains them now. The recurring job drains
        // whatever a crash left behind, so nothing here is required for durability.
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<NotificationOutboxDrainer>().DrainAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            NotificationFailed(log, ex.GetType().Name);
        }

        try
        {
            var message = new TicketChangedMessage(ticketId, departmentId, reference, kind, actorUserId, clock.GetUtcNow());
            var group = kind == TicketEventKinds.Note ? HubGroups.Notes(departmentId) : HubGroups.Department(departmentId);
            await hub.Clients.Group(group).SendAsync("ticketChanged", message, cancellationToken);
            await PublishToRequesterAsync(departmentId, ticketId, reference, kind, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            PublishFailed(log, ex.GetType().Name);
        }
    }

    /// <summary>
    /// Requester-safe fan-out. Only events a requester can see (a public message, a status change, an attachment) are forwarded, and the payload is a
    /// reference and a coarse kind: no ids, no department, no actor, no internal-note or assignment information. The target group is chosen by the server
    /// from the ticket's requester; clients can never pick it.
    /// </summary>
    private async Task PublishToRequesterAsync(Guid departmentId, Guid ticketId, string reference, string kind, CancellationToken ct)
    {
        var portalKind = kind switch
        {
            TicketEventKinds.Message => "reply",
            TicketEventKinds.Status => "status",
            TicketEventKinds.Attachment => "attachment",
            _ => null,
        };
        if (portalKind is null)
        {
            return;
        }

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Persistence.TicketingDbContext>();
        var requesterId = await Persistence.DepartmentScopeBypass.IgnoreDepartmentScope(db.Tickets.AsNoTracking())
            .Where(t => t.Id == ticketId && t.DepartmentId == departmentId).Select(t => (Guid?)t.RequesterId).SingleOrDefaultAsync(ct);
        if (requesterId is { } id)
        {
            await hub.Clients.Group(HubGroups.Requester(id)).SendAsync("requestChanged", new PortalRequestChanged(reference, portalKind, clock.GetUtcNow()), ct);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Draining notifications failed ({Error}); the recurring job retries")]
    private static partial void NotificationFailed(ILogger logger, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Realtime publish failed ({Error}); clients reconcile through polling")]
    private static partial void PublishFailed(ILogger logger, string error);
}

/// <summary>
/// The realtime hub. Clients cannot name groups or call anything that changes state: the server places each connection into the
/// groups of the departments where the signed-in user holds tickets.read (and tickets.notes.read), at connect time, again whenever
/// the user's memberships change (<see cref="IAccessChangeListener"/>) and on a periodic re-check.
/// </summary>
[Authorize]
internal sealed class TicketHub(HubAccess access, PresenceService presence) : Hub
{
    /// <summary>
    /// The ONLY client-callable method. Declares what this connection is doing on one ticket ("viewing", "reply", "note"; anything else leaves). It carries no
    /// text and changes no ticket data; the server decides whether the connection may be on that ticket at all. See <see cref="PresenceService"/>.
    /// </summary>
    public Task SetPresence(Guid ticketId, string? state) => presence.SetAsync(Context.ConnectionId, UserId(Context.User), ticketId, state);

    public override async Task OnConnectedAsync()
    {
        await access.ConnectedAsync(Context.ConnectionId, UserId(Context.User), Context);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        access.Disconnected(Context.ConnectionId);
        await presence.LeaveAsync(Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }

    internal static Guid UserId(ClaimsPrincipal? user) =>
        Guid.TryParse(user?.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id) ? id : throw new HubException("Not authenticated.");
}

/// <summary>Tracks connections and keeps their group membership equal to the user's current access (no request context needed).</summary>
internal sealed partial class HubAccess(IHubContext<TicketHub> hub, IServiceScopeFactory scopes, ILogger<HubAccess> log) : IAccessChangeListener, IDisposable
{
    private sealed class Connection(Guid userId, HubCallerContext? context, int? sessionVersion, DateTimeOffset? issuedAt)
    {
        public Guid UserId { get; } = userId;

        /// <summary>Lets the server close the connection itself when its session is revoked or expires (SignalR authenticates only when the connection opens).</summary>
        public HubCallerContext? Context { get; } = context;

        /// <summary>Session stamp of the cookie the connection was opened with; a connection without one cannot prove its session and is treated as revoked.</summary>
        public int? SessionVersion { get; } = sessionVersion;

        public DateTimeOffset? IssuedAt { get; } = issuedAt;

        public HashSet<string> Groups { get; } = [];
    }

    private readonly ConcurrentDictionary<string, Connection> _connections = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Raised after a connection's group membership was re-evaluated (access may have been removed).</summary>
    public event Action<string>? GroupsChanged;

    /// <summary>Raised when a connection closed.</summary>
    public event Action<string>? ConnectionClosed;

    /// <summary>Is this connection currently in the server-decided group? (The authority for presence: the same groups that gate every realtime event.)</summary>
    public bool InGroup(string connectionId, string group)
    {
        if (!_connections.TryGetValue(connectionId, out var connection))
        {
            return false;
        }

        _gate.Wait();
        try
        {
            return connection.Groups.Contains(group);
        }
        finally
        {
            _gate.Release();
        }
    }

    public int ConnectionCount => _connections.Count;

    public async Task ConnectedAsync(string connectionId, Guid userId, HubCallerContext? context = null)
    {
        int? version = int.TryParse(context?.User?.FindFirst(SessionClaimTypes.SessionVersion)?.Value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;
        DateTimeOffset? issued = long.TryParse(context?.User?.FindFirst(SessionClaimTypes.IssuedAt)?.Value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var s)
            ? DateTimeOffset.FromUnixTimeSeconds(s) : null;
        _connections[connectionId] = new Connection(userId, context, version, issued);
        await RefreshConnectionAsync(connectionId, CancellationToken.None);
    }

    public void Disconnected(string connectionId)
    {
        _connections.TryRemove(connectionId, out _);
        ConnectionClosed?.Invoke(connectionId);
    }

    public async Task AccessChangedAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        foreach (var (connectionId, connection) in _connections)
        {
            if (connection.UserId == userId)
            {
                await RefreshConnectionAsync(connectionId, cancellationToken);
            }
        }
    }

    /// <summary>Re-evaluates every connected user (safety net for access changes that did not notify, e.g. user deactivation).</summary>
    public async Task RefreshAllAsync(CancellationToken cancellationToken)
    {
        foreach (var connectionId in _connections.Keys)
        {
            await RefreshConnectionAsync(connectionId, cancellationToken);
        }
    }

    private async Task RefreshConnectionAsync(string connectionId, CancellationToken ct)
    {
        if (!_connections.TryGetValue(connectionId, out var connection))
        {
            return;
        }

        try
        {
            await using var scope = scopes.CreateAsyncScope();

            // The session behind this connection must still be valid (account active, same session version, inside the absolute lifetime). If not, the connection loses
            // EVERY group (department, notes, user, requester) and is closed by the server; nothing ever re-adds a revoked connection, so reactivation does not restore it.
            if (connection.Context is not null
                && !(connection.SessionVersion is { } version && connection.IssuedAt is { } issuedAt
                     && await scope.ServiceProvider.GetRequiredService<IUserSessionValidator>().IsSessionValidAsync(connection.UserId, version, issuedAt, ct)))
            {
                await RevokeAsync(connectionId, connection, ct);
                return;
            }

            var organisation = scope.ServiceProvider.GetRequiredService<IOrganisationDirectory>();
            var readers = await organisation.GetDepartmentsWithPermissionAsync(connection.UserId, PermissionCodes.TicketsRead, ct);
            var noteReaders = await organisation.GetDepartmentsWithPermissionAsync(connection.UserId, PermissionCodes.TicketsNotesRead, ct);
            var requesterIds = await scope.ServiceProvider.GetRequiredService<Persistence.TicketingDbContext>().RequesterLinks.AsNoTracking()
                .Where(l => l.UserId == connection.UserId).Select(l => l.RequesterId).ToListAsync(ct);
            var wanted = new HashSet<string>(
                readers.Select(HubGroups.Department).Append(HubGroups.User(connection.UserId)).Concat(noteReaders.Intersect(readers).Select(HubGroups.Notes)).Concat(requesterIds.Select(HubGroups.Requester)),
                StringComparer.Ordinal);

            await _gate.WaitAsync(ct);
            try
            {
                foreach (var group in connection.Groups.Where(g => !wanted.Contains(g)).ToList())
                {
                    await hub.Groups.RemoveFromGroupAsync(connectionId, group, ct);
                    connection.Groups.Remove(group);
                }

                foreach (var group in wanted.Where(g => !connection.Groups.Contains(g)))
                {
                    await hub.Groups.AddToGroupAsync(connectionId, group, ct);
                    connection.Groups.Add(group);
                }
            }
            finally
            {
                _gate.Release();
            }

            GroupsChanged?.Invoke(connectionId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RefreshFailed(log, ex.GetType().Name);
        }
    }

    private async Task RevokeAsync(string connectionId, Connection connection, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            foreach (var group in connection.Groups.ToList())
            {
                await hub.Groups.RemoveFromGroupAsync(connectionId, group, ct);
                connection.Groups.Remove(group);
            }
        }
        finally
        {
            _gate.Release();
        }

        _connections.TryRemove(connectionId, out _);
        connection.Context?.Abort();
        GroupsChanged?.Invoke(connectionId);
        ConnectionClosed?.Invoke(connectionId);
    }

    public void Dispose() => _gate.Dispose();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Realtime access refresh failed ({Error})")]
    private static partial void RefreshFailed(ILogger logger, string error);
}

/// <summary>Periodic re-check so access removed without a notification (deactivation, role changes elsewhere) is dropped within a minute.</summary>
internal sealed class HubAccessRefresher(HubAccess access) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(60));
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            await access.RefreshAllAsync(stoppingToken);
        }
    }
}
