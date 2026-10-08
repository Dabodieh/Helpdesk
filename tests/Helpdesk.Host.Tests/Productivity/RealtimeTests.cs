using System.Net;
using System.Text.Json;
using Helpdesk.Host.Tests.Infrastructure;
using Helpdesk.Host.Tests.Ticketing;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

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

    private async Task<(HubConnection Connection, List<JsonElement> Events)> ConnectAsync(TestUser user, bool authenticated = true, string eventName = "ticketChanged")
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
        connection.On<JsonElement>(eventName, e =>
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

    /// <summary>
    /// Waits until the connection is really subscribed (QA-005): a probe action that the listener may see is repeated until it arrives. SignalR joins the groups after the
    /// handshake, so a fixed sleep after StartAsync was a guess. The events are cleared afterwards.
    /// </summary>
    private static async Task WaitSubscribedAsync(List<JsonElement> events, Func<Task> probe)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            await probe();
            if (await WaitForAsync(events, _ => true, 250))
            {
                await Task.Delay(100); // let a queued probe event land before clearing
                lock (events)
                {
                    events.Clear();
                }

                return;
            }
        }

        throw new Xunit.Sdk.XunitException("the realtime subscription never became active");
    }

    /// <summary>
    /// A negative check that cannot pass by being early: after the suspect event, an event the listener IS allowed to see is produced and awaited. Events reach one connection
    /// in the order they were sent, so anything leaked before the sentinel is already in the list when the sentinel arrives.
    /// </summary>
    private static async Task<bool> NoneBeforeSentinelAsync(List<JsonElement> events, Func<Task> produceSentinel, Func<JsonElement, bool> isSentinel, Func<JsonElement, bool> isForbidden)
    {
        await produceSentinel();
        if (!await WaitForAsync(events, isSentinel))
        {
            return false;
        }

        lock (events)
        {
            return !events.Any(isForbidden);
        }
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

    /// <summary>
    /// Test-only role: reads tickets and replies publicly but lacks <c>tickets.notes.read</c>. It exists only in the test process
    /// (<c>RoleCatalogue.RegisterTestRole</c>) and in this fixture's throw-away database (the role check constraint is widened here);
    /// the four production roles are untouched.
    /// </summary>
    private const string RestrictedRole = "TestRestrictedAgent";

    [Fact]
    public async Task A_member_without_notes_read_cannot_retrieve_or_learn_about_internal_notes_through_rest_or_realtime()
    {
        Helpdesk.Modules.Organisation.Authorization.RoleCatalogue.RegisterTestRole(RestrictedRole,
            ["department.read", "tickets.read", "tickets.create", "tickets.reply", "tickets.update"]);
        await fixture.ExecuteAsync("ALTER TABLE organisation.department_memberships DROP CONSTRAINT IF EXISTS ck_department_memberships_role");
        await fixture.ExecuteAsync($"ALTER TABLE organisation.department_memberships ADD CONSTRAINT ck_department_memberships_role CHECK (role IN ('DepartmentAdmin','TeamLead','Agent','Viewer','{RestrictedRole}'))");

        var d = await fixture.NewDeptAsync();
        var restricted = await fixture.NewMemberAsync(d.Department, RestrictedRole);
        var (_, restrictedEvents) = await ConnectAsync(restricted);
        var (_, agentEvents) = await ConnectAsync(d.Agent);

        var ticket = await d.Agent.CreateTicketAsync(d.Department, new { subject = "Notes boundary", description = "plain description", requester = new { email = $"r{Unique.Id()}@customer.test" } });
        Assert.True(await WaitForAsync(restrictedEvents, e => e.GetProperty("kind").GetString() == "created"));
        lock (restrictedEvents)
        {
            restrictedEvents.Clear();
        }

        var note = $"NOTESECRET-{Unique.Id()}";
        var activityBefore = (await restricted.Client.GetAsync($"/api/tickets/{ticket.Id}/activity?limit=200")).Expect(HttpStatusCode.OK).Json.GetProperty("items").GetArrayLength();
        var listBefore = (await restricted.Client.GetAsync("/api/tickets?status=Open&limit=100")).Expect(HttpStatusCode.OK).Body;

        // An internal note is posted by a normal agent. The agent (with notes.read) is told; the restricted member hears nothing at all.
        (await d.Agent.PostMessageAsync(ticket.Id, "Internal", note)).Expect(HttpStatusCode.Created);
        Assert.True(await WaitForAsync(agentEvents, e => e.GetProperty("kind").GetString() == "note"));
        Assert.True(
            await NoneBeforeSentinelAsync(
                restrictedEvents, async () => { (await d.Agent.PostMessageAsync(ticket.Id, "Public", "sentinel reply")).Expect(HttpStatusCode.Created); },
                e => e.GetProperty("kind").GetString() == "message", e => e.GetProperty("kind").GetString() == "note"),
            "restricted member received a realtime event for an internal note");

        // REST: messages, detail, activity, search, list and counts reveal neither the text nor the existence of the note.
        var messages = (await restricted.Client.GetAsync($"/api/tickets/{ticket.Id}/messages")).Expect(HttpStatusCode.OK);
        Assert.DoesNotContain(note, messages.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("Internal", messages.Body, StringComparison.Ordinal);
        Assert.All(messages.Json.GetProperty("items").EnumerateArray(), m => Assert.Equal("Public", m.GetProperty("kind").GetString()));

        var activity = (await restricted.Client.GetAsync($"/api/tickets/{ticket.Id}/activity?limit=200")).Expect(HttpStatusCode.OK);
        Assert.Equal(activityBefore + 1, activity.Json.GetProperty("items").GetArrayLength()); // +1: only the visible sentinel reply, never the note
        Assert.DoesNotContain("Internal", activity.Body, StringComparison.Ordinal);

        foreach (var url in new[] { $"/api/tickets/{ticket.Id}", $"/api/tickets?q={note}", "/api/tickets/queue-counts" })
        {
            Assert.DoesNotContain(note, (await restricted.Client.GetAsync(url)).Expect(HttpStatusCode.OK).Body, StringComparison.Ordinal);
        }

        Assert.True(IsoKit.EmptyPage((await restricted.Client.GetAsync($"/api/tickets?q={note}")).Expect(HttpStatusCode.OK)), "search matched an internal note");
        var found = Assert.Single((await d.Agent.Client.GetAsync($"/api/tickets?q={note}")).Expect(HttpStatusCode.OK).Json.GetProperty("items").EnumerateArray());
        Assert.Equal("note", found.GetProperty("match").GetProperty("field").GetString()); // sanity: an entitled member does find it

        // The restricted member cannot write notes either, and the connection still works: a public reply is delivered.
        (await restricted.PostMessageAsync(ticket.Id, "Internal", "nope")).Expect(HttpStatusCode.Forbidden);
        (await d.Lead.PostMessageAsync(ticket.Id, "Public", "visible to everyone")).Expect(HttpStatusCode.Created);
        Assert.True(await WaitForAsync(restrictedEvents, e => e.GetProperty("kind").GetString() == "message"));
        lock (restrictedEvents)
        {
            Assert.DoesNotContain(restrictedEvents, e => e.GetProperty("kind").GetString() == "note");
        }
    }

    [Fact]
    public async Task Automation_changes_reach_connected_members_as_content_free_events()
    {
        var d = await fixture.NewDeptAsync();
        var team = await fixture.CreateTeamAsync(d.Department);
        (await d.Admin.Client.PostAsync($"{d.Department.Url}/automations", new
        {
            name = "Route",
            trigger = "TicketCreated",
            conditions = Array.Empty<object>(),
            actions = new object[] { new { type = "assignTeam", value = team.Id.ToString() }, new { type = "addInternalNote", text = "AUTONOTE secret" } },
        })).Expect(HttpStatusCode.Created);
        var (_, events) = await ConnectAsync(d.Viewer);
        await d.Agent.CreateTicketAsync(d.Department, new { subject = "Routed", description = "x", requester = new { email = $"r{Unique.Id()}@customer.test" } });

        Assert.True(await WaitForAsync(events, e => e.GetProperty("kind").GetString() == "assignment"));
        Assert.True(await WaitForAsync(events, e => e.GetProperty("kind").GetString() == "note"));
        string[] allowed = ["ticketId", "departmentId", "reference", "kind", "actorUserId", "at"];
        lock (events)
        {
            Assert.All(events, e => Assert.All(e.EnumerateObject(), p => Assert.Contains(p.Name, allowed)));
            Assert.DoesNotContain(events, e => e.GetRawText().Contains("AUTONOTE", StringComparison.Ordinal));
        }
    }

    private static readonly string[] RestrictedPermissions = ["department.read", "tickets.read", "tickets.create", "tickets.reply", "tickets.update"];

    private async Task RegisterRestrictedRoleAsync()
    {
        Helpdesk.Modules.Organisation.Authorization.RoleCatalogue.RegisterTestRole(RestrictedRole, RestrictedPermissions);
        await fixture.ExecuteAsync("ALTER TABLE organisation.department_memberships DROP CONSTRAINT IF EXISTS ck_department_memberships_role");
        await fixture.ExecuteAsync($"ALTER TABLE organisation.department_memberships ADD CONSTRAINT ck_department_memberships_role CHECK (role IN ('DepartmentAdmin','TeamLead','Agent','Viewer','{RestrictedRole}'))");
    }

    /// <summary>Everything a restricted caller can observe about two tickets: detail, list order and timestamps, search, activity, counts.</summary>
    private static async Task<string> ObservationAsync(TestUser user, Guid older, Guid newer, string term)
    {
        var parts = new List<string>
        {
            (await user.Client.GetAsync($"/api/tickets/{older}")).Expect(HttpStatusCode.OK).Body,
            (await user.Client.GetAsync($"/api/tickets/{newer}")).Expect(HttpStatusCode.OK).Body,
            (await user.Client.GetAsync("/api/tickets?status=Open&limit=100")).Expect(HttpStatusCode.OK).Body,
            (await user.Client.GetAsync($"/api/tickets?q={term}&limit=100")).Expect(HttpStatusCode.OK).Body,
            (await user.Client.GetAsync("/api/tickets?sort=updated_asc&limit=100")).Expect(HttpStatusCode.OK).Body,
            (await user.Client.GetAsync($"/api/tickets/{older}/activity?limit=200")).Expect(HttpStatusCode.OK).Body,
            (await user.Client.GetAsync($"/api/tickets/{older}/messages?limit=200")).Expect(HttpStatusCode.OK).Body,
            (await user.Client.GetAsync("/api/tickets/queue-counts")).Expect(HttpStatusCode.OK).Body,
        };
        return string.Join("\n", parts);
    }

    [Fact]
    public async Task An_internal_note_changes_nothing_a_caller_without_notes_read_can_observe()
    {
        await RegisterRestrictedRoleAsync();
        var d = await fixture.NewDeptAsync();
        var restricted = await fixture.NewMemberAsync(d.Department, RestrictedRole);
        var tag = Unique.Id();
        var older = await d.Agent.CreateTicketAsync(d.Department, new { subject = $"Older {tag}", description = "first", requester = new { email = $"a{tag}@customer.test" } });
        await Task.Delay(20);
        var newer = await d.Agent.CreateTicketAsync(d.Department, new { subject = $"Newer {tag}", description = "second", requester = new { email = $"b{tag}@customer.test" } });
        var (_, restrictedEvents) = await ConnectAsync(restricted);
        var (_, agentEvents) = await ConnectAsync(d.Agent);
        await WaitSubscribedAsync(restrictedEvents, async () => { (await d.Agent.CreateTicketAsync(d.Department)).Expect(HttpStatusCode.Created); });
        await WaitSubscribedAsync(agentEvents, async () => { (await d.Agent.CreateTicketAsync(d.Department)).Expect(HttpStatusCode.Created); });

        var before = await ObservationAsync(restricted, older.Id, newer.Id, tag);
        var versionBefore = await fixture.ScalarAsync<long>("SELECT xmin::text::bigint FROM ticketing.tickets WHERE id = @t", new NpgsqlParameter("t", older.Id));
        var updatedBefore = await fixture.ScalarAsync<DateTime>("SELECT updated_at FROM ticketing.tickets WHERE id = @t", new NpgsqlParameter("t", older.Id));

        // A person adds a private note, then automation adds one: neither may be observable.
        await Task.Delay(50);
        (await d.Agent.PostMessageAsync(older.Id, "Internal", $"private {tag}")).Expect(HttpStatusCode.Created);
        (await d.Admin.Client.PostAsync($"{d.Department.Url}/automations", new
        {
            name = "Note",
            trigger = "TicketCreated",
            conditions = Array.Empty<object>(),
            actions = new object[] { new { type = "addInternalNote", text = $"auto {tag}" } },
        })).Expect(HttpStatusCode.Created);
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var engine = scope.ServiceProvider.GetRequiredService<Helpdesk.Modules.Ticketing.Automation.AutomationEngine>();
            await engine.RunAsync(d.Id, older.Id, "TicketCreated", $"replay-{tag}", Helpdesk.Modules.Ticketing.Automation.AutomationChain.Start("test"), CancellationToken.None);
        }

        // Authorised readers do get the note (events and content), proving the notes really were written.
        Assert.True(await WaitForAsync(agentEvents, e => e.GetProperty("kind").GetString() == "note"));
        Assert.Equal(2, (await d.Agent.Client.GetAsync($"/api/tickets/{older.Id}/messages?limit=200")).Expect(HttpStatusCode.OK).Json.GetProperty("items").EnumerateArray().Count(m => m.GetProperty("kind").GetString() == "Internal"));

        // The restricted caller sees byte-identical detail (including updatedAt and version), list order, search, activity, messages and counts.
        Assert.Equal(before, await ObservationAsync(restricted, older.Id, newer.Id, tag));
        Assert.Equal(versionBefore, await fixture.ScalarAsync<long>("SELECT xmin::text::bigint FROM ticketing.tickets WHERE id = @t", new NpgsqlParameter("t", older.Id)));
        Assert.Equal(updatedBefore, await fixture.ScalarAsync<DateTime>("SELECT updated_at FROM ticketing.tickets WHERE id = @t", new NpgsqlParameter("t", older.Id)));

        // The note did not reorder the restricted caller's queue: the older ticket is still the older one.
        var order = (await restricted.Client.GetAsync("/api/tickets?limit=100")).Expect(HttpStatusCode.OK).Json.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToArray();
        Assert.True(Array.IndexOf(order, newer.Id) < Array.IndexOf(order, older.Id));

        // Visible activity still moves the timestamp normally: a public reply from the same agent.
        (await d.Agent.PostMessageAsync(older.Id, "Public", "visible reply")).Expect(HttpStatusCode.Created);
        Assert.NotEqual(before, await ObservationAsync(restricted, older.Id, newer.Id, tag));
        Assert.True(await WaitForAsync(restrictedEvents, e => e.GetProperty("kind").GetString() == "message"));

        // That visible reply is the sentinel: every event sent before it has arrived, and none of them was about an internal note.
        lock (restrictedEvents)
        {
            Assert.DoesNotContain(restrictedEvents, e => e.GetProperty("kind").GetString() == "note");
        }

        Assert.True(await fixture.ScalarAsync<DateTime>("SELECT updated_at FROM ticketing.tickets WHERE id = @t", new NpgsqlParameter("t", older.Id)) > updatedBefore);
    }

    [Fact]
    public async Task A_note_posted_together_with_a_status_change_is_visible_only_as_the_status_change()
    {
        await RegisterRestrictedRoleAsync();
        var d = await fixture.NewDeptAsync();
        var restricted = await fixture.NewMemberAsync(d.Department, RestrictedRole);
        var ticket = await d.Agent.CreateTicketAsync(d.Department);
        var (_, restrictedEvents) = await ConnectAsync(restricted);
        await WaitSubscribedAsync(restrictedEvents, async () => { (await d.Agent.CreateTicketAsync(d.Department)).Expect(HttpStatusCode.Created); });

        (await d.Agent.PostMessageAsync(ticket.Id, "Internal", "private reasoning", status: "Pending")).Expect(HttpStatusCode.Created);
        Assert.True(await WaitForAsync(restrictedEvents, e => e.GetProperty("kind").GetString() == "status"));
        lock (restrictedEvents)
        {
            Assert.DoesNotContain(restrictedEvents, e => e.GetProperty("kind").GetString() == "note");
        }

        var activity = (await restricted.Client.GetAsync($"/api/tickets/{ticket.Id}/activity?limit=200")).Expect(HttpStatusCode.OK).Body;
        Assert.DoesNotContain("Internal", activity, StringComparison.Ordinal);
        Assert.Contains("ticket.status_changed", activity, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_sla_event_reaches_only_members_who_can_read_the_ticket_and_carries_no_configuration()
    {
        Helpdesk.Modules.Organisation.Authorization.RoleCatalogue.RegisterTestRole("TestNoTicketRead", ["department.read"]);
        await fixture.ExecuteAsync("ALTER TABLE organisation.department_memberships DROP CONSTRAINT IF EXISTS ck_department_memberships_role");
        await fixture.ExecuteAsync($"ALTER TABLE organisation.department_memberships ADD CONSTRAINT ck_department_memberships_role CHECK (role IN ('DepartmentAdmin','TeamLead','Agent','Viewer','{RestrictedRole}','TestNoTicketRead'))");

        var a = await fixture.NewDeptAsync();
        var b = await fixture.NewDeptAsync();
        var noRead = await fixture.NewMemberAsync(a.Department, "TestNoTicketRead");
        const string secretPolicy = "SECRETPOLICY-Platinum";
        const string secretRule = "SECRETRULE-Escalate";
        var calendar = (await a.Admin.Client.PostAsync($"{a.Department.Url}/business-hours", new { name = "SECRETCAL", timeZone = "Europe/London", weekly = new[] { new { day = "Monday", intervals = new[] { new { start = "09:00", end = "17:00" } } } } })).Expect(HttpStatusCode.Created);
        (await a.Admin.Client.PostAsync($"{a.Department.Url}/sla-policies", new { name = secretPolicy, calendarId = calendar.Id, firstResponseMinutes = 60, resolutionMinutes = 480 })).Expect(HttpStatusCode.Created);
        (await a.Admin.Client.PostAsync($"{a.Department.Url}/automations", new { name = secretRule, trigger = "SlaBreached", conditions = Array.Empty<object>(), actions = new object[] { new { type = "setPriority", value = "Urgent" } } })).Expect(HttpStatusCode.Created);

        var (_, agentEvents) = await ConnectAsync(a.Agent);
        var (_, viewerEvents) = await ConnectAsync(a.Viewer);
        var (_, outsiderEvents) = await ConnectAsync(b.Agent);
        var (_, noReadEvents) = await ConnectAsync(noRead);
        await WaitSubscribedAsync(outsiderEvents, async () => { (await b.Agent.CreateTicketAsync(b.Department)).Expect(HttpStatusCode.Created); });
        await WaitSubscribedAsync(viewerEvents, async () => { (await a.Agent.CreateTicketAsync(a.Department)).Expect(HttpStatusCode.Created); });
        await WaitSubscribedAsync(agentEvents, async () => { (await a.Agent.CreateTicketAsync(a.Department)).Expect(HttpStatusCode.Created); });

        var ticket = await a.Agent.CreateTicketAsync(a.Department, new { subject = "UNRELATEDSUBJECT-xyz", description = "UNRELATEDBODY-xyz", requester = new { email = $"r{Unique.Id()}@customer.test" } });
        foreach (var events in new[] { agentEvents, viewerEvents })
        {
            Assert.True(await WaitForAsync(events, e => e.GetProperty("kind").GetString() == "sla" && e.GetProperty("ticketId").GetGuid() == ticket.Id));
        }

        // Resolving completes the SLA: another sla event, same shape.
        (await a.Lead.PatchTicketAsync(ticket, new { Status = "Resolved" })).Expect(HttpStatusCode.OK);
        Assert.True(await WaitForAsync(agentEvents, e => e.GetProperty("kind").GetString() == "status"));
        Assert.True(
            await NoneBeforeSentinelAsync(
                outsiderEvents, async () => { (await b.Agent.CreateTicketAsync(b.Department)).Expect(HttpStatusCode.Created); },
                e => e.GetProperty("kind").GetString() == "created" && e.GetProperty("departmentId").GetGuid() == b.Id, e => e.GetProperty("departmentId").GetGuid() == a.Id),
            "a member of another department received an event");
        Assert.True(await StaysSilentAsync(noReadEvents), "a member without tickets.read received an event");

        string[] allowed = ["ticketId", "departmentId", "reference", "kind", "actorUserId", "at"];
        foreach (var events in new[] { agentEvents, viewerEvents })
        {
            lock (events)
            {
                var sla = events.Where(e => e.GetProperty("kind").GetString() == "sla").ToArray();
                Assert.NotEmpty(sla);
                Assert.All(events, e => Assert.All(e.EnumerateObject(), p => Assert.Contains(p.Name, allowed)));
                Assert.All(sla, e => Assert.Equal(a.Id, e.GetProperty("departmentId").GetGuid()));
                var raw = string.Concat(events.Select(e => e.GetRawText()));
                foreach (var forbidden in new[] { secretPolicy, secretRule, "SECRETCAL", "UNRELATEDSUBJECT", "UNRELATEDBODY", "Urgent", "firstResponse", "dueAt" })
                {
                    Assert.DoesNotContain(forbidden, raw, StringComparison.Ordinal);
                }
            }
        }

        // Re-subscription does not bypass authorization: a member removed from the department and reconnecting hears nothing.
        var (_, removedBefore) = await ConnectAsync(a.Viewer);
        (await a.Admin.Client.DeleteAsync($"{a.Department.Url}/members/{a.Viewer.Id}")).Expect(HttpStatusCode.NoContent);
        await Task.Delay(300);
        var (_, removedAfter) = await ConnectAsync(a.Viewer);
        lock (removedAfter)
        {
            removedAfter.Clear();
        }

        await a.Agent.CreateTicketAsync(a.Department);
        Assert.True(await WaitForAsync(agentEvents, e => e.GetProperty("kind").GetString() == "sla" && e.GetProperty("ticketId").GetGuid() != ticket.Id));
        Assert.True(await StaysSilentAsync(removedAfter), "a removed member received an event after reconnecting");
        Assert.NotNull(removedBefore);
    }

    // ----- presence (ephemeral, advisory) -----

    private static JsonElement? LastPresence(List<JsonElement> events, Guid ticketId)
    {
        lock (events)
        {
            var last = events.LastOrDefault(e => e.GetProperty("ticketId").GetGuid() == ticketId);
            return last.ValueKind == JsonValueKind.Object ? last : null;
        }
    }

    private static async Task<bool> PresenceAsync(List<JsonElement> events, Guid ticketId, Func<JsonElement, bool> users, int milliseconds = 6000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (DateTime.UtcNow < until)
        {
            if (LastPresence(events, ticketId) is { } last && users(last.GetProperty("users")))
            {
                return true;
            }

            await Task.Delay(50);
        }

        return false;
    }

    private static bool Sees(JsonElement users, Guid userId, string state) => users.EnumerateArray().Any(u => u.GetProperty("userId").GetGuid() == userId && u.GetProperty("state").GetString() == state);

    private static bool Empty(JsonElement users) => users.GetArrayLength() == 0;

    private async Task<(Dept D, Guid TicketId)> PresenceTicketAsync()
    {
        var d = await fixture.NewDeptAsync();
        var ticket = await d.Agent.CreateTicketAsync(d.Department, new { subject = "Presence", description = "plain", requester = new { email = $"r{Unique.Id()}@customer.test" } });
        return (d, ticket.Id);
    }

    [Fact]
    public async Task Two_authorised_agents_see_each_other_viewing_and_composing_but_never_themselves_and_the_payload_has_no_content()
    {
        var (d, id) = await PresenceTicketAsync();
        var (a, aSeen) = await ConnectAsync(d.Agent, eventName: "presence");
        var (b, bSeen) = await ConnectAsync(d.Lead, eventName: "presence");

        await a.InvokeAsync("SetPresence", id, "viewing");
        await b.InvokeAsync("SetPresence", id, "viewing");
        Assert.True(await PresenceAsync(aSeen, id, u => Sees(u, d.Lead.Id, "viewing")));
        Assert.True(await PresenceAsync(bSeen, id, u => Sees(u, d.Agent.Id, "viewing")));
        Assert.DoesNotContain(LastPresence(aSeen, id)!.Value.GetProperty("users").EnumerateArray(), u => u.GetProperty("userId").GetGuid() == d.Agent.Id); // own presence is not echoed

        await b.InvokeAsync("SetPresence", id, "reply");
        Assert.True(await PresenceAsync(aSeen, id, u => Sees(u, d.Lead.Id, "reply")));
        await b.InvokeAsync("SetPresence", id, "note");
        Assert.True(await PresenceAsync(aSeen, id, u => Sees(u, d.Lead.Id, "note"))); // an agent who may read notes sees note composing

        // Only ids, a name and a state ever travel: no draft, keystroke or ticket content.
        var payload = LastPresence(aSeen, id)!.Value;
        Assert.Equal(["ticketId", "users"], payload.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.All(payload.GetProperty("users").EnumerateArray(), u => Assert.Equal(["displayName", "state", "userId"], u.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray()));
        Assert.DoesNotContain("Presence", payload.GetRawText(), StringComparison.Ordinal); // the ticket subject
    }

    [Fact]
    public async Task Note_composing_is_shown_as_plain_viewing_to_someone_who_cannot_read_notes()
    {
        Helpdesk.Modules.Organisation.Authorization.RoleCatalogue.RegisterTestRole(RestrictedRole,
            ["department.read", "tickets.read", "tickets.create", "tickets.reply", "tickets.update"]);
        await fixture.ExecuteAsync("ALTER TABLE organisation.department_memberships DROP CONSTRAINT IF EXISTS ck_department_memberships_role");
        await fixture.ExecuteAsync($"ALTER TABLE organisation.department_memberships ADD CONSTRAINT ck_department_memberships_role CHECK (role IN ('DepartmentAdmin','TeamLead','Agent','Viewer','{RestrictedRole}'))");
        var (d, id) = await PresenceTicketAsync();
        var restricted = await fixture.NewMemberAsync(d.Department, RestrictedRole);
        var (agent, agentSeen) = await ConnectAsync(d.Agent, eventName: "presence");
        var (rest, restSeen) = await ConnectAsync(restricted, eventName: "presence");

        await rest.InvokeAsync("SetPresence", id, "viewing");
        await agent.InvokeAsync("SetPresence", id, "note");
        Assert.True(await PresenceAsync(restSeen, id, u => Sees(u, d.Agent.Id, "viewing")));
        Assert.False(await PresenceAsync(restSeen, id, u => Sees(u, d.Agent.Id, "note"), milliseconds: 600));

        // The restricted member cannot even declare note composing: it is recorded (and shown) as viewing.
        await rest.InvokeAsync("SetPresence", id, "note");
        Assert.True(await PresenceAsync(agentSeen, id, u => Sees(u, restricted.Id, "viewing")));
        Assert.False(await PresenceAsync(agentSeen, id, u => Sees(u, restricted.Id, "note"), milliseconds: 600));
    }

    [Fact]
    public async Task Presence_expires_without_a_heartbeat_and_disappears_on_disconnect_or_lost_access()
    {
        var (d, id) = await PresenceTicketAsync();
        var (a, aSeen) = await ConnectAsync(d.Agent, eventName: "presence");
        var (b, _) = await ConnectAsync(d.Lead, eventName: "presence");
        var (c, _) = await ConnectAsync(d.Admin, eventName: "presence");
        await b.InvokeAsync("SetPresence", id, "viewing");
        await c.InvokeAsync("SetPresence", id, "reply");
        var declared = DateTimeOffset.UtcNow;
        await Task.Delay(1500);
        await a.InvokeAsync("SetPresence", id, "viewing"); // A keeps heartbeating; B and C went quiet
        Assert.True(await PresenceAsync(aSeen, id, u => u.GetArrayLength() == 2));

        // Stale: B and C last spoke before the expiry window ended, A did not; the sweep removes only them.
        var presence = fixture.Factory.Services.GetRequiredService<Helpdesk.Modules.Ticketing.Services.PresenceService>();
        Assert.Equal(2, await presence.SweepAsync(declared + Helpdesk.Modules.Ticketing.Services.PresenceService.Expiry + TimeSpan.FromMilliseconds(700)));
        Assert.True(await PresenceAsync(aSeen, id, Empty));

        // Disconnect cleans up at once.
        await b.InvokeAsync("SetPresence", id, "viewing");
        Assert.True(await PresenceAsync(aSeen, id, u => Sees(u, d.Lead.Id, "viewing")));
        await b.StopAsync();
        Assert.True(await PresenceAsync(aSeen, id, Empty));

        // Lost access: the member is removed from the department and stops being observable immediately.
        var leaver = await fixture.NewMemberAsync(d.Department, "Agent");
        var (l, _) = await ConnectAsync(leaver, eventName: "presence");
        await l.InvokeAsync("SetPresence", id, "reply");
        Assert.True(await PresenceAsync(aSeen, id, u => Sees(u, leaver.Id, "reply")));
        var admin = await fixture.PlatformAdminAsync();
        (await admin.Client.DeleteAsync($"{d.Department.Url}/members/{leaver.Id}")).Expect(HttpStatusCode.NoContent);
        Assert.True(await PresenceAsync(aSeen, id, Empty));
    }

    [Fact]
    public async Task Other_departments_and_requesters_can_neither_declare_nor_observe_presence()
    {
        var (d, id) = await PresenceTicketAsync();
        var other = await fixture.NewDeptAsync();
        var presence = fixture.Factory.Services.GetRequiredService<Helpdesk.Modules.Ticketing.Services.PresenceService>();
        var (a, aSeen) = await ConnectAsync(d.Agent, eventName: "presence");
        var (foreign, foreignSeen) = await ConnectAsync(other.Agent, eventName: "presence");
        var requester = await fixture.NewUserAsync("requester");
        var (asRequester, requesterSeen) = await ConnectAsync(requester, eventName: "presence");
        var before = presence.EntryCount;

        await foreign.InvokeAsync("SetPresence", id, "viewing"); // not a member of this department: dropped silently
        await asRequester.InvokeAsync("SetPresence", id, "reply"); // no membership at all
        await foreign.InvokeAsync("SetPresence", Guid.NewGuid(), "viewing"); // unknown ticket: indistinguishable
        Assert.Equal(before, presence.EntryCount);

        await a.InvokeAsync("SetPresence", id, "viewing");
        await Task.Delay(400);
        Assert.Empty(foreignSeen);
        Assert.Empty(requesterSeen);
        Assert.DoesNotContain(aSeen, e => e.GetProperty("users").GetArrayLength() > 0);
    }
}
