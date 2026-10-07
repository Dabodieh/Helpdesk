using Helpdesk.Modules.Identity.Contracts;
using Helpdesk.Modules.Ticketing.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Helpdesk.Modules.Ticketing.Services;

/// <summary>What an agent is doing on a ticket right now. A state only: never keystrokes, draft text, cursor position or any history.</summary>
internal static class PresenceStates
{
    public const string Viewing = "viewing";
    public const string Reply = "reply";
    public const string Note = "note";

    public static bool IsValid(string? state) => state is Viewing or Reply or Note;

    public static int Rank(string state) => state switch { Note => 3, Reply => 2, _ => 1 };
}

internal sealed record PresenceUser(Guid UserId, string DisplayName, string State);

/// <summary>Payload of the hub message <c>presence</c>: the OTHER people on one ticket, as the receiving connection is allowed to know them. Nothing else.</summary>
internal sealed record PresenceMessage(Guid TicketId, IReadOnlyList<PresenceUser> Users);

/// <summary>
/// Ephemeral, advisory "who is on this ticket" for a single host. Held in memory only (never stored, no history, no durations, no Redis): a heartbeat from the
/// client refreshes a connection's entry, an entry expires after <see cref="Expiry"/> without one, and a disconnect or lost access removes it at once.
/// Authorisation is the connection's own server-decided group membership: only a connection in the ticket department's group can declare presence or learn
/// of it, and a "writing an internal note" state is only reported to connections that may read notes (others see "viewing"). Presence grants nothing and
/// does NOT replace the version checks of the ticket API; it is a courtesy so two people notice each other. Multi-host coordination is deliberately absent.
/// </summary>
internal sealed partial class PresenceService : IDisposable
{
    public static readonly TimeSpan Expiry = TimeSpan.FromSeconds(60);

    private sealed class Entry
    {
        public required string ConnectionId { get; init; }

        public required Guid UserId { get; init; }

        public required string DisplayName { get; init; }

        public required Guid DepartmentId { get; init; }

        public required Guid TicketId { get; init; }

        public required string State { get; set; }

        public DateTimeOffset SeenAt { get; set; }
    }

    private readonly IHubContext<TicketHub> _hub;
    private readonly HubAccess _access;
    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _clock;
    private readonly ILogger<PresenceService> _log;
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _byConnection = new(StringComparer.Ordinal);

    public PresenceService(IHubContext<TicketHub> hub, HubAccess access, IServiceScopeFactory scopes, TimeProvider clock, ILogger<PresenceService> log)
    {
        _hub = hub;
        _access = access;
        _scopes = scopes;
        _clock = clock;
        _log = log;
        _access.GroupsChanged += OnGroupsChanged;
        _access.ConnectionClosed += OnConnectionClosed;
    }

    public int EntryCount
    {
        get
        {
            lock (_gate)
            {
                return _byConnection.Count;
            }
        }
    }

    /// <summary>
    /// Declares (or refreshes) what this connection is doing on a ticket; a null or unknown state, or a ticket the connection may not read, leaves. One ticket
    /// per connection. Unauthorised declarations are dropped silently (nothing says whether the ticket exists).
    /// </summary>
    public async Task SetAsync(string connectionId, Guid userId, Guid ticketId, string? state)
    {
        if (!PresenceStates.IsValid(state))
        {
            await LeaveAsync(connectionId);
            return;
        }

        Entry? existing;
        lock (_gate)
        {
            _byConnection.TryGetValue(connectionId, out existing);
        }

        var departmentId = existing is not null && existing.TicketId == ticketId ? existing.DepartmentId : await DepartmentOfAsync(ticketId);
        if (departmentId is not { } department || !_access.InGroup(connectionId, HubGroups.Department(department)))
        {
            await LeaveAsync(connectionId);
            return;
        }

        // Writing an internal note is only ever declared (and later shown) for someone who may read notes; otherwise it is plain viewing.
        var effective = state == PresenceStates.Note && !_access.InGroup(connectionId, HubGroups.Notes(department)) ? PresenceStates.Viewing : state!;
        var name = existing is not null && existing.UserId == userId ? existing.DisplayName : await NameOfAsync(userId);
        Guid? previousTicket = null;
        var changed = false;
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            if (existing is not null && existing.TicketId == ticketId && _byConnection.TryGetValue(connectionId, out var current) && ReferenceEquals(current, existing))
            {
                changed = current.State != effective;
                current.State = effective;
                current.SeenAt = now;
            }
            else
            {
                if (_byConnection.TryGetValue(connectionId, out var old))
                {
                    previousTicket = old.TicketId;
                }

                _byConnection[connectionId] = new Entry { ConnectionId = connectionId, UserId = userId, DisplayName = name, DepartmentId = department, TicketId = ticketId, State = effective, SeenAt = now };
                changed = true;
            }
        }

        if (changed)
        {
            if (previousTicket is { } before)
            {
                await BroadcastAsync(before);
            }

            await BroadcastAsync(ticketId);
        }
    }

    public Task LeaveAsync(string connectionId)
    {
        Guid? ticket = null;
        lock (_gate)
        {
            if (_byConnection.Remove(connectionId, out var entry))
            {
                ticket = entry.TicketId;
            }
        }

        return ticket is { } t ? BroadcastAsync(t) : Task.CompletedTask;
    }

    /// <summary>Removes stale entries (no heartbeat within <see cref="Expiry"/>) and entries whose connection lost the department; returns how many were removed.</summary>
    public async Task<int> SweepAsync(DateTimeOffset now)
    {
        var affected = new HashSet<Guid>();
        var removed = 0;
        List<Entry> all;
        lock (_gate)
        {
            all = [.. _byConnection.Values];
        }

        foreach (var entry in all)
        {
            var stale = now - entry.SeenAt > Expiry;
            var allowed = _access.InGroup(entry.ConnectionId, HubGroups.Department(entry.DepartmentId));
            lock (_gate)
            {
                if (stale || !allowed)
                {
                    if (_byConnection.Remove(entry.ConnectionId))
                    {
                        removed++;
                        affected.Add(entry.TicketId);
                    }
                }
                else if (entry.State == PresenceStates.Note && !_access.InGroup(entry.ConnectionId, HubGroups.Notes(entry.DepartmentId)))
                {
                    entry.State = PresenceStates.Viewing;
                    affected.Add(entry.TicketId);
                }
            }
        }

        foreach (var ticket in affected)
        {
            await BroadcastAsync(ticket);
        }

        return removed;
    }

    public Task SweepAsync() => SweepAsync(_clock.GetUtcNow());

    private void OnGroupsChanged(string connectionId) => _ = SweepSafelyAsync();

    private void OnConnectionClosed(string connectionId) => _ = LeaveSafelyAsync(connectionId);

    private async Task SweepSafelyAsync()
    {
        try
        {
            await SweepAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Failed(_log, ex.GetType().Name);
        }
    }

    private async Task LeaveSafelyAsync(string connectionId)
    {
        try
        {
            await LeaveAsync(connectionId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Failed(_log, ex.GetType().Name);
        }
    }

    /// <summary>Each connection on the ticket gets the OTHER users, strongest state per user; a note state is downgraded to viewing for connections that cannot read notes.</summary>
    private async Task BroadcastAsync(Guid ticketId)
    {
        List<Entry> onTicket;
        lock (_gate)
        {
            onTicket = [.. _byConnection.Values.Where(e => e.TicketId == ticketId).Select(e => new Entry
            {
                ConnectionId = e.ConnectionId, UserId = e.UserId, DisplayName = e.DisplayName, DepartmentId = e.DepartmentId, TicketId = e.TicketId, State = e.State, SeenAt = e.SeenAt,
            })];
        }

        foreach (var recipient in onTicket)
        {
            var canNotes = _access.InGroup(recipient.ConnectionId, HubGroups.Notes(recipient.DepartmentId));
            var users = onTicket.Where(o => o.UserId != recipient.UserId)
                .GroupBy(o => o.UserId)
                .Select(g =>
                {
                    var best = g.OrderByDescending(o => PresenceStates.Rank(o.State)).First();
                    return new PresenceUser(best.UserId, best.DisplayName, best.State == PresenceStates.Note && !canNotes ? PresenceStates.Viewing : best.State);
                })
                .OrderBy(u => u.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
            try
            {
                await _hub.Clients.Client(recipient.ConnectionId).SendAsync("presence", new PresenceMessage(ticketId, users));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Failed(_log, ex.GetType().Name);
            }
        }
    }

    private async Task<Guid?> DepartmentOfAsync(Guid ticketId)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TicketingDbContext>();
        return await DepartmentScopeBypass.IgnoreDepartmentScope(db.Tickets.AsNoTracking()).Where(t => t.Id == ticketId).Select(t => (Guid?)t.DepartmentId).SingleOrDefaultAsync();
    }

    private async Task<string> NameOfAsync(Guid userId)
    {
        await using var scope = _scopes.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<IUserDirectory>().FindAsync(userId))?.DisplayName ?? "A colleague";
    }

    public void Dispose()
    {
        _access.GroupsChanged -= OnGroupsChanged;
        _access.ConnectionClosed -= OnConnectionClosed;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Presence update failed ({Error}); presence is advisory and expires on its own")]
    private static partial void Failed(ILogger logger, string error);
}

/// <summary>Expires presence that stopped heartbeating and drops presence of connections that lost access, every few seconds.</summary>
internal sealed class PresenceSweeper(PresenceService presence) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await presence.SweepAsync();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _ = ex; // advisory data: the next tick tries again
            }
        }
    }
}
