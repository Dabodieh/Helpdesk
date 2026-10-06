using System.Collections.Concurrent;
using System.Security.Claims;
using Helpdesk.Modules.Organisation.Contracts;
using Helpdesk.SharedKernel.Authorization;
using Helpdesk.SharedKernel.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
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
}

/// <summary>Payload of the <c>ticketChanged</c> hub message. No subject, body, names or addresses: ids and a kind only.</summary>
internal sealed record TicketChangedMessage(Guid TicketId, Guid DepartmentId, string Reference, string Kind, Guid? ActorUserId, DateTimeOffset At);

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
}

internal sealed partial class TicketEventPublisher(IHubContext<TicketHub> hub, TimeProvider clock, ILogger<TicketEventPublisher> log) : ITicketEvents
{
    public async Task PublishAsync(Guid departmentId, Guid ticketId, string reference, string kind, Guid? actorUserId, CancellationToken cancellationToken = default)
    {
        try
        {
            var message = new TicketChangedMessage(ticketId, departmentId, reference, kind, actorUserId, clock.GetUtcNow());
            var group = kind == TicketEventKinds.Note ? HubGroups.Notes(departmentId) : HubGroups.Department(departmentId);
            await hub.Clients.Group(group).SendAsync("ticketChanged", message, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            PublishFailed(log, ex.GetType().Name);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Realtime publish failed ({Error}); clients reconcile through polling")]
    private static partial void PublishFailed(ILogger logger, string error);
}

/// <summary>
/// The realtime hub. Clients cannot name groups or call anything that changes state: the server places each connection into the
/// groups of the departments where the signed-in user holds tickets.read (and tickets.notes.read), at connect time, again whenever
/// the user's memberships change (<see cref="IAccessChangeListener"/>) and on a periodic re-check.
/// </summary>
[Authorize]
internal sealed class TicketHub(HubAccess access) : Hub
{
    public override async Task OnConnectedAsync()
    {
        await access.ConnectedAsync(Context.ConnectionId, UserId(Context.User));
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        access.Disconnected(Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }

    internal static Guid UserId(ClaimsPrincipal? user) =>
        Guid.TryParse(user?.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id) ? id : throw new HubException("Not authenticated.");
}

/// <summary>Tracks connections and keeps their group membership equal to the user's current access (no request context needed).</summary>
internal sealed partial class HubAccess(IHubContext<TicketHub> hub, IServiceScopeFactory scopes, ILogger<HubAccess> log) : IAccessChangeListener, IDisposable
{
    private sealed class Connection(Guid userId)
    {
        public Guid UserId { get; } = userId;

        public HashSet<string> Groups { get; } = [];
    }

    private readonly ConcurrentDictionary<string, Connection> _connections = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public int ConnectionCount => _connections.Count;

    public async Task ConnectedAsync(string connectionId, Guid userId)
    {
        _connections[connectionId] = new Connection(userId);
        await RefreshConnectionAsync(connectionId, CancellationToken.None);
    }

    public void Disconnected(string connectionId) => _connections.TryRemove(connectionId, out _);

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
            var organisation = scope.ServiceProvider.GetRequiredService<IOrganisationDirectory>();
            var readers = await organisation.GetDepartmentsWithPermissionAsync(connection.UserId, PermissionCodes.TicketsRead, ct);
            var noteReaders = await organisation.GetDepartmentsWithPermissionAsync(connection.UserId, PermissionCodes.TicketsNotesRead, ct);
            var wanted = new HashSet<string>(readers.Select(HubGroups.Department).Concat(noteReaders.Intersect(readers).Select(HubGroups.Notes)), StringComparer.Ordinal);

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
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RefreshFailed(log, ex.GetType().Name);
        }
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
