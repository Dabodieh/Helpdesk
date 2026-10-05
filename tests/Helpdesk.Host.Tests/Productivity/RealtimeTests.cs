using System.Net;
using System.Text.Json;
using Helpdesk.Host.Tests.Infrastructure;
using Helpdesk.Host.Tests.Ticketing;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;

namespace Helpdesk.Host.Tests.Productivity;

/// <summary>
/// Realtime hub security: the server alone decides subscriptions (department groups), events carry no content, other departments and
/// users without access receive nothing, removed members stop receiving immediately, and clients cannot join groups.
/// </summary>
public sealed class RealtimeTests(HelpdeskFixture fixture) : IClassFixture<HelpdeskFixture>, IAsyncLifetime
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

    private sealed record Session(string Cookie, string CsrfToken);

    /// <summary>Signs in through the real dev-login endpoint with no cookie jar and collects the cookies and CSRF token for the hub.</summary>
    private async Task<Session> SessionAsync(string subject)
    {
        using var http = fixture.Factory.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = false });
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

    private async Task<(HubConnection Connection, List<JsonElement> Events)> ConnectAsync(TestUser user, bool authenticated = true)
    {
        var session = authenticated ? await SessionAsync(user.Subject) : new Session("", "");
        var events = new List<JsonElement>();
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(fixture.Factory.Server.BaseAddress, "hubs/tickets"), o =>
            {
                o.Transports = HttpTransportType.LongPolling;
                o.HttpMessageHandlerFactory = _ => fixture.Factory.Server.CreateHandler();
                if (authenticated)
                {
                    o.Headers["Cookie"] = session.Cookie;
                    o.Headers["X-CSRF-TOKEN"] = session.CsrfToken;
                }
            })
            .Build();
        connection.On<JsonElement>("ticketChanged", e =>
        {
            lock (events)
            {
                events.Add(e.Clone());
            }
        });
        _connections.Add(connection);
        await connection.StartAsync();
        return (connection, events);
    }

    private static async Task<bool> WaitForAsync(List<JsonElement> events, Func<JsonElement, bool> match, int milliseconds = 6000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (DateTime.UtcNow < until)
        {
            lock (events)
            {
                if (events.Any(match))
                {
                    return true;
                }
            }

            await Task.Delay(50);
        }

        return false;
    }

    private static async Task<bool> StaysSilentAsync(List<JsonElement> events, int milliseconds = 1200)
    {
        await Task.Delay(milliseconds);
        lock (events)
        {
            return events.Count == 0;
        }
    }

    [Fact]
    public async Task Anonymous_clients_cannot_connect()
    {
        var d = await fixture.NewDeptAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => ConnectAsync(d.Agent, authenticated: false));
    }

    [Fact]
    public async Task Members_receive_content_free_events_for_their_departments_tickets()
    {
        var d = await fixture.NewDeptAsync();
        var (_, agentEvents) = await ConnectAsync(d.Agent);
        var (_, viewerEvents) = await ConnectAsync(d.Viewer);
        var ticket = await d.Agent.CreateTicketAsync(d.Department, new { subject = "Confidential subject", description = "Confidential body", requester = new { email = "secret@customer.test", displayName = "Secret Person" } });
        var id = ticket.Id;

        Assert.True(await WaitForAsync(agentEvents, e => e.GetProperty("ticketId").GetGuid() == id && e.GetProperty("kind").GetString() == "created"));
        (await d.Agent.PatchTicketAsync(ticket, new { Priority = "Urgent" })).Expect(HttpStatusCode.OK);
        (await d.Lead.PostMessageAsync(id, "Public", "A visible reply with secret words")).Expect(HttpStatusCode.Created);
        (await d.Lead.PostMessageAsync(id, "Internal", "A private note with secret words")).Expect(HttpStatusCode.Created);

        foreach (var events in new[] { agentEvents, viewerEvents })
        {
            Assert.True(await WaitForAsync(events, e => e.GetProperty("kind").GetString() == "priority"));
            Assert.True(await WaitForAsync(events, e => e.GetProperty("kind").GetString() == "message"));
            Assert.True(await WaitForAsync(events, e => e.GetProperty("kind").GetString() == "note")); // all four roles may read notes
        }

        // Payload: ids and a kind only. No subject, body, names or addresses ever travel through the hub.
        string[] allowed = ["ticketId", "departmentId", "reference", "kind", "actorUserId", "at"];
        lock (agentEvents)
        {
            Assert.All(agentEvents, e => Assert.All(e.EnumerateObject(), p => Assert.Contains(p.Name, allowed)));
            Assert.DoesNotContain(agentEvents, e => e.GetRawText().Contains("secret", StringComparison.OrdinalIgnoreCase) || e.GetRawText().Contains("Confidential", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public async Task Members_of_other_departments_and_users_without_access_receive_nothing()
    {
        var a = await fixture.NewDeptAsync();
        var b = await fixture.NewDeptAsync();
        var outsider = await fixture.NewUserAsync();
        var platform = await fixture.PlatformAdminAsync();
        var (_, inB) = await ConnectAsync(b.Agent);
        var (_, inB2) = await ConnectAsync(b.Admin);
        var (_, outsiderEvents) = await ConnectAsync(outsider);
        var (_, platformEvents) = await ConnectAsync(platform); // administration is not content access
        var (_, inA) = await ConnectAsync(a.Agent);

        var ticket = await a.Agent.CreateTicketAsync(a.Department);
        await a.Agent.PostMessageAsync(ticket.Id, "Internal", "private");
        (await a.Agent.PatchTicketAsync(await a.Agent.GetTicketAsync(ticket.Id), new { Status = "Pending" })).Expect(HttpStatusCode.OK);

        Assert.True(await WaitForAsync(inA, e => e.GetProperty("kind").GetString() == "note"));
        Assert.True(await StaysSilentAsync(inB));
        Assert.True(await StaysSilentAsync(inB2, 200));
        Assert.True(await StaysSilentAsync(outsiderEvents, 200));
        Assert.True(await StaysSilentAsync(platformEvents, 200));
    }

    [Fact]
    public async Task A_member_removed_from_the_department_stops_receiving_events_immediately()
    {
        var d = await fixture.NewDeptAsync();
        var ticket = await d.Lead.CreateTicketAsync(d.Department);
        var (_, events) = await ConnectAsync(d.Agent);
        (await d.Lead.PostMessageAsync(ticket.Id, "Public", "before removal")).Expect(HttpStatusCode.Created);
        Assert.True(await WaitForAsync(events, e => e.GetProperty("kind").GetString() == "message"));

        (await d.Admin.Client.DeleteAsync($"{d.Department.Url}/members/{d.Agent.Id}")).Expect(HttpStatusCode.NoContent);
        lock (events)
        {
            events.Clear();
        }

        (await d.Lead.PostMessageAsync(ticket.Id, "Public", "after removal")).Expect(HttpStatusCode.Created);
        (await d.Lead.PostMessageAsync(ticket.Id, "Internal", "private after removal")).Expect(HttpStatusCode.Created);
        (await d.Lead.PatchTicketAsync(await d.Lead.GetTicketAsync(ticket.Id), new { Priority = "High" })).Expect(HttpStatusCode.OK);

        Assert.True(await StaysSilentAsync(events));
    }

    [Fact]
    public async Task A_member_added_later_starts_receiving_without_reconnecting()
    {
        var d = await fixture.NewDeptAsync();
        var newcomer = await fixture.NewUserAsync();
        var (_, events) = await ConnectAsync(newcomer);
        var ticket = await d.Agent.CreateTicketAsync(d.Department);
        Assert.True(await StaysSilentAsync(events, 600));

        await fixture.AddMemberAsync(d.Department, newcomer, "Agent");
        (await d.Agent.PostMessageAsync(ticket.Id, "Public", "now visible")).Expect(HttpStatusCode.Created);

        Assert.True(await WaitForAsync(events, e => e.GetProperty("kind").GetString() == "message"));
    }

    [Fact]
    public async Task Clients_cannot_join_groups_or_call_anything_on_the_hub()
    {
        var a = await fixture.NewDeptAsync();
        var b = await fixture.NewDeptAsync();
        var (connection, events) = await ConnectAsync(b.Agent);
        var ticket = await a.Agent.CreateTicketAsync(a.Department);

        foreach (var method in new[] { "JoinGroup", "AddToGroup", "Subscribe", "SubscribeDepartment" })
        {
            await Assert.ThrowsAsync<HubException>(() => connection.InvokeAsync(method, "d:" + a.Id.ToString("N")));
        }

        (await a.Agent.PostMessageAsync(ticket.Id, "Public", "must not reach B")).Expect(HttpStatusCode.Created);
        Assert.True(await StaysSilentAsync(events));
    }

    [Fact]
    public async Task Mail_and_delivery_changes_are_published_to_the_departments_group()
    {
        var d = await fixture.NewDeptAsync();
        var (_, events) = await ConnectAsync(d.Agent);
        var created = (await d.Agent.Client.PostAsync($"{d.Department.Url}/tickets", new { subject = "Realtime mail", description = "x", requester = new { email = $"r{Unique.Id()}@customer.test" } })).Expect(HttpStatusCode.Created);

        // A reply with no mailbox is recorded (NotDelivered); the bulk endpoint publishes per ticket.
        (await d.Agent.Client.PostAsync("/api/tickets/bulk", new { ticketIds = new[] { created.Id }, priority = "High" })).Expect(HttpStatusCode.OK);
        Assert.True(await WaitForAsync(events, e => e.GetProperty("kind").GetString() == "priority" && e.GetProperty("ticketId").GetGuid() == created.Id));
    }
}
