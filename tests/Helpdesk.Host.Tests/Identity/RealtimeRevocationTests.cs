using System.Net;
using System.Text.Json;
using Helpdesk.Host.Tests.Infrastructure;
using Helpdesk.Modules.Organisation.Contracts;
using Helpdesk.Modules.Ticketing.Services;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace Helpdesk.Host.Tests.Identity;

/// <summary>
/// S-01, realtime: SignalR authenticates only when a connection opens, so a deactivated, reactivated or expired session must lose its CONNECTION too: every group
/// (department, notes, user, requester) and the socket itself, with no way back for that connection.
/// </summary>
public sealed class RealtimeRevocationTests(HelpdeskFixture fixture) : IClassFixture<HelpdeskFixture>, IAsyncLifetime
{
    private readonly List<HubConnection> _connections = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var connection in _connections)
        {
            await connection.DisposeAsync();
        }
    }

    private sealed class AdjustableClock : TimeProvider
    {
        private TimeSpan _offset;

        public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + _offset;

        public void Advance(TimeSpan by) => _offset += by;
    }

    private sealed record Session(string Cookie, string CsrfToken);

    private static async Task<Session> SessionAsync(string subject, WebApplicationFactory<Program> factory)
    {
        using var http = factory.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = false });
        var login = await http.GetAsync($"/api/auth/dev-login?subject={Uri.EscapeDataString(subject)}&name={Uri.EscapeDataString(subject)}&email={Uri.EscapeDataString(subject)}@example.test&returnUrl=/");
        var cookies = Pairs(login);
        using var csrfRequest = new HttpRequestMessage(HttpMethod.Get, "/api/csrf");
        csrfRequest.Headers.Add("Cookie", string.Join("; ", cookies));
        var csrf = await http.SendAsync(csrfRequest);
        cookies.AddRange(Pairs(csrf));
        var token = JsonDocument.Parse(await csrf.Content.ReadAsStringAsync()).RootElement.GetProperty("token").GetString()!;
        return new Session(string.Join("; ", cookies), token);
    }

    private static List<string> Pairs(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values) ? [.. values.Select(v => v.Split(';')[0])] : [];

    private sealed class Live
    {
        public required HubConnection Connection { get; init; }

        public required List<JsonElement> Events { get; init; }

        public required TaskCompletionSource Closed { get; init; }
    }

    private async Task<Live> ConnectAsync(Session session, WebApplicationFactory<Program> factory)
    {
        var events = new List<JsonElement>();
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, "hubs/tickets"), o =>
            {
                o.Transports = HttpTransportType.LongPolling;
                o.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                o.Headers["Cookie"] = session.Cookie;
                o.Headers["X-CSRF-TOKEN"] = session.CsrfToken;
            })
            .Build();
        connection.On<JsonElement>("ticketChanged", e =>
        {
            lock (events)
            {
                events.Add(e.Clone());
            }
        });
        connection.Closed += _ =>
        {
            closed.TrySetResult();
            return Task.CompletedTask;
        };
        _connections.Add(connection);
        await connection.StartAsync();
        return new Live { Connection = connection, Events = events, Closed = closed };
    }

    private static async Task<bool> WaitAsync(Func<bool> condition, int milliseconds = 20000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (DateTime.UtcNow < until)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(50);
        }

        return condition();
    }

    private static string UserGroup(Guid id) => HubGroups.User(id);

    [Fact]
    public async Task Deactivation_removes_every_group_and_closes_the_connection_and_reactivation_does_not_restore_it()
    {
        var root = await fixture.PlatformAdminAsync();
        var department = await fixture.CreateDepartmentAsync();
        var lead = await fixture.NewMemberAsync(department, DepartmentRoles.TeamLead);
        var agentSubject = $"rt-agent-{Unique.Id()}";
        var agent = await fixture.SignInAsync(agentSubject);
        await fixture.AddMemberAsync(department, agent, DepartmentRoles.Agent);

        var access = fixture.Factory.Services.GetRequiredService<HubAccess>();
        var session = await SessionAsync(agentSubject, fixture.Factory);
        var live = await ConnectAsync(session, fixture.Factory);
        var connectionId = live.Connection.ConnectionId!;

        // Subscribed: the department's and the user's own groups are held, and an event arrives.
        Assert.True(await WaitAsync(() => access.InGroup(connectionId, HubGroups.Department(department.Id)) && access.InGroup(connectionId, UserGroup(agent.Id))));
        await lead.CreateTicketAsync(department);
        Assert.True(await WaitAsync(() => { lock (live.Events) { return live.Events.Count > 0; } }));

        (await root.Client.PutAsync($"/api/platform/users/{agent.Id}/active", new { value = false })).Expect(HttpStatusCode.OK);

        // Immediately: no group of any kind is left (including the user and requester groups that department removal never touched) and the server closed the socket.
        Assert.True(await WaitAsync(() => live.Closed.Task.IsCompleted), "the connection was not closed by the server");
        Assert.False(access.InGroup(connectionId, HubGroups.Department(department.Id)));
        Assert.False(access.InGroup(connectionId, HubGroups.Notes(department.Id)));
        Assert.False(access.InGroup(connectionId, UserGroup(agent.Id)));
        int before;
        lock (live.Events)
        {
            before = live.Events.Count;
        }

        await lead.CreateTicketAsync(department);
        await Task.Delay(500);
        lock (live.Events)
        {
            Assert.Equal(before, live.Events.Count);
        }

        // Reactivation: the old connection stays dead and the old cookie cannot open a new one; a fresh sign-in works and subscribes again.
        (await root.Client.PutAsync($"/api/platform/users/{agent.Id}/active", new { value = true })).Expect(HttpStatusCode.OK);
        await Task.Delay(300);
        Assert.False(access.InGroup(connectionId, HubGroups.Department(department.Id)));
        await Assert.ThrowsAnyAsync<Exception>(() => ConnectAsync(session, fixture.Factory));

        var fresh = await ConnectAsync(await SessionAsync(agentSubject, fixture.Factory), fixture.Factory);
        Assert.True(await WaitAsync(() => access.InGroup(fresh.Connection.ConnectionId!, HubGroups.Department(department.Id))));
    }

    [Fact]
    public async Task A_connection_whose_session_passes_the_absolute_lifetime_is_closed_even_though_it_is_in_use()
    {
        var clock = new AdjustableClock();
        var factory = fixture.Factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Authentication:Session:AbsoluteLifetimeHours", "1");
            b.UseSetting("Authentication:Session:IdleTimeoutMinutes", "30");
            b.ConfigureServices(s => s.AddSingleton<TimeProvider>(clock));
        });
        var subject = $"rt-abs-{Unique.Id()}";
        var live = await ConnectAsync(await SessionAsync(subject, factory), factory);
        var access = factory.Services.GetRequiredService<HubAccess>();
        var connectionId = live.Connection.ConnectionId!;
        Assert.True(await WaitAsync(() => access.ConnectionCount > 0));

        clock.Advance(TimeSpan.FromMinutes(61));
        await access.RefreshAllAsync(CancellationToken.None);

        Assert.True(await WaitAsync(() => live.Closed.Task.IsCompleted), "the expired session's connection stayed open");
    }

    [Fact]
    public async Task A_healthy_connection_keeps_its_groups_across_a_refresh()
    {
        // Sanity for the positive path: a healthy connection keeps its groups across a refresh (the revocation check does not drop live sessions).
        var subject = $"rt-ok-{Unique.Id()}";
        var user = await fixture.SignInAsync(subject);
        var access = fixture.Factory.Services.GetRequiredService<HubAccess>();
        var live = await ConnectAsync(await SessionAsync(subject, fixture.Factory), fixture.Factory);
        var connectionId = live.Connection.ConnectionId!;
        Assert.True(await WaitAsync(() => access.InGroup(connectionId, UserGroup(user.Id))));

        await access.RefreshAllAsync(CancellationToken.None);

        Assert.True(access.InGroup(connectionId, UserGroup(user.Id)));
        Assert.False(live.Closed.Task.IsCompleted);
    }
}
