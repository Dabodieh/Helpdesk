#pragma warning disable CA1861 // one-off request payloads
using System.Net;
using System.Text;
using System.Text.Json;
using Helpdesk.Host.Tests.Infrastructure;
using Helpdesk.Host.Tests.Mail;
using Helpdesk.Host.Tests.Ticketing;
using Microsoft.AspNetCore.SignalR.Client;

namespace Helpdesk.Host.Tests.Portal;

/// <summary>Requester authorization (a boundary separate from agent permissions), requester-safe shapes, replies, privacy of internal notes and portal realtime.</summary>
public sealed class PortalTests(HelpdeskFixture fixture) : IClassFixture<HelpdeskFixture>, IAsyncLifetime
{
    private readonly List<HubConnection> _connections = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var c in _connections)
        {
            await c.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_requester_sees_their_own_tickets_in_requester_wording_and_nobody_elses()
    {
        var d = await fixture.NewDeptAsync();
        var alice = await fixture.EmployeeAsync("alice");
        var bob = await fixture.EmployeeAsync("bob");
        var mine = await d.TicketForAsync(alice.Email, "Printer jam");
        var theirs = await d.TicketForAsync(bob.Email, "VPN down");

        var list = await alice.PortalListAsync();
        var item = Assert.Single(list.GetProperty("items").EnumerateArray());
        Assert.Equal(mine.Reference(), item.GetProperty("reference").GetString());
        Assert.Equal("In progress", item.GetProperty("statusLabel").GetString());
        Assert.Equal(d.Department.Name, item.GetProperty("department").GetString());

        // Purpose-built shape: no ids, no team, assignee, priority, mailbox, version or SLA.
        Assert.Equal("createdAt department lastActivityAt reference status statusLabel subject", string.Join(" ", item.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal)));
        Assert.DoesNotContain(theirs.Reference(), list.GetRawText(), StringComparison.Ordinal);

        // The wording follows the one real status.
        var current = (await d.Agent.GetTicketAsync(mine.Id)).Expect(HttpStatusCode.OK);
        (await d.Agent.PatchTicketAsync(current, new { Status = "Pending" })).Expect(HttpStatusCode.OK);
        Assert.Equal("Waiting", (await alice.PortalListAsync()).GetProperty("items")[0].GetProperty("statusLabel").GetString());
        Assert.Empty((await alice.PortalListAsync("?filter=closed")).GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task A_guessed_or_foreign_reference_reveals_nothing()
    {
        var d = await fixture.NewDeptAsync();
        var alice = await fixture.EmployeeAsync("alice");
        var bob = await fixture.EmployeeAsync("bob");
        var other = await fixture.NewDeptAsync();
        var aliceTicket = await d.TicketForAsync(alice.Email);
        var bobTicket = await other.TicketForAsync(bob.Email);

        var missing = (await alice.User.Client.GetAsync("/api/portal/requests/ZZZZ-999999")).Expect(HttpStatusCode.NotFound);
        var foreign = (await alice.User.Client.GetAsync($"/api/portal/requests/{bobTicket.Reference()}")).Expect(HttpStatusCode.NotFound);
        Assert.Equal(missing.Json.GetProperty("title").GetString(), foreign.Json.GetProperty("title").GetString());
        Assert.DoesNotContain(bobTicket.Reference(), foreign.Body, StringComparison.Ordinal);

        // Same department, same company, knowing the reference, or using a ticket id: nothing opens it for another requester.
        (await bob.User.Client.GetAsync($"/api/portal/requests/{aliceTicket.Reference()}")).Expect(HttpStatusCode.NotFound);
        (await bob.User.Client.GetAsync($"/api/portal/requests/{aliceTicket.Id}")).Expect(HttpStatusCode.NotFound);
        (await bob.User.Client.PostAsync($"/api/portal/requests/{aliceTicket.Reference()}/replies", new { body = "hello" })).Expect(HttpStatusCode.NotFound);
        (await bob.User.Client.GetAsync($"/api/portal/requests/{aliceTicket.Reference()}/attachments/{Guid.NewGuid()}")).Expect(HttpStatusCode.NotFound);

        // Portal access is not agent access in either direction.
        (await alice.User.Client.GetAsync($"/api/tickets/{aliceTicket.Id}")).Expect(HttpStatusCode.NotFound);
        Assert.Empty((await alice.User.Client.GetAsync("/api/tickets")).Expect(HttpStatusCode.OK).Json.GetProperty("items").EnumerateArray());
        Assert.Empty((await d.Agent.Client.GetAsync("/api/portal/requests?filter=all")).Expect(HttpStatusCode.OK).Json.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task The_requester_conversation_has_public_messages_only_and_no_trace_of_internal_notes()
    {
        var d = await fixture.NewDeptAsync();
        var alice = await fixture.EmployeeAsync("alice");
        var ticket = await d.TicketForAsync(alice.Email, "Laptop slow");
        (await d.Agent.PostMessageAsync(ticket.Id, "Public", "We are looking into it.")).Expect(HttpStatusCode.Created);
        (await d.Agent.PostMessageAsync(ticket.Id, "Internal", "NOTESECRET reorganise the disks")).Expect(HttpStatusCode.Created);

        var detail = (await alice.User.Client.GetAsync($"/api/portal/requests/{ticket.Reference()}")).Expect(HttpStatusCode.OK).Json;
        var messages = detail.GetProperty("messages").EnumerateArray().ToArray();
        Assert.Equal(["You", "Support"], messages.Select(m => m.GetProperty("author").GetString()!).ToArray());
        Assert.DoesNotContain("NOTESECRET", detail.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("nternal", detail.GetRawText(), StringComparison.Ordinal);
        Assert.Equal("at attachments author body fromYou id", string.Join(" ", messages[0].EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal)));
    }

    [Fact]
    public async Task Adding_an_internal_note_changes_nothing_a_requester_can_see_or_receive()
    {
        var d = await fixture.NewDeptAsync();
        var alice = await fixture.EmployeeAsync("alice");
        var older = await d.TicketForAsync(alice.Email, "Older request");
        await Task.Delay(30);
        var newer = await d.TicketForAsync(alice.Email, "Newer request");
        await alice.PortalListAsync(); // claims the requester so the portal connection is placed in its group
        var (_, events) = await fixture.ConnectPortalAsync(alice, _connections);
        await Task.Delay(300);
        lock (events)
        {
            events.Clear();
        }

        async Task<string> ObserveAsync() =>
            string.Join(
                "\n",
                (await alice.User.Client.GetAsync("/api/portal/requests?filter=all")).Expect(HttpStatusCode.OK).Body,
                (await alice.User.Client.GetAsync($"/api/portal/requests/{older.Reference()}")).Expect(HttpStatusCode.OK).Body,
                (await alice.User.Client.GetAsync($"/api/portal/requests/{newer.Reference()}")).Expect(HttpStatusCode.OK).Body);

        var before = await ObserveAsync();
        await Task.Delay(50);
        (await d.Agent.PostMessageAsync(older.Id, "Internal", "private thought")).Expect(HttpStatusCode.Created);

        // Assignment and priority are agent business too: they must not move the requester's view either.
        var current = (await d.Agent.GetTicketAsync(older.Id)).Expect(HttpStatusCode.OK);
        (await d.Agent.PatchTicketAsync(current, new { Priority = "Urgent" })).Expect(HttpStatusCode.OK);
        (await d.Lead.AssignAsync(older.Id, (await d.Lead.GetTicketAsync(older.Id)).Version, null, d.Agent.Id)).Expect(HttpStatusCode.OK);

        Assert.Equal(before, await ObserveAsync());
        Assert.True(await PortalKit.SilentAsync(events), "the requester received an event for private activity");

        // Visible activity does change it, in order, and is announced.
        (await d.Agent.PostMessageAsync(older.Id, "Public", "Fixed on our side.")).Expect(HttpStatusCode.Created);
        Assert.NotEqual(before, await ObserveAsync());
        Assert.True(await PortalKit.WaitForAsync(events, e => e.GetProperty("reference").GetString() == older.Reference() && e.GetProperty("kind").GetString() == "reply"));
        var order = (await alice.PortalListAsync("?filter=all")).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("reference").GetString()).ToArray();
        Assert.Equal([older.Reference(), newer.Reference()], order.Select(o => o!).ToArray());
    }

    [Fact]
    public async Task Portal_realtime_carries_only_a_reference_and_a_coarse_kind_to_the_owning_requester()
    {
        var d = await fixture.NewDeptAsync();
        var alice = await fixture.EmployeeAsync("alice");
        var bob = await fixture.EmployeeAsync("bob");
        var ticket = await d.TicketForAsync(alice.Email);
        await d.TicketForAsync(bob.Email);
        await alice.PortalListAsync();
        await bob.PortalListAsync();
        var (_, aliceEvents) = await fixture.ConnectPortalAsync(alice, _connections);
        var (_, bobEvents) = await fixture.ConnectPortalAsync(bob, _connections);
        await Task.Delay(300);

        (await d.Agent.PostMessageAsync(ticket.Id, "Public", "REPLYSECRET hello")).Expect(HttpStatusCode.Created);
        var current = (await d.Agent.GetTicketAsync(ticket.Id)).Expect(HttpStatusCode.OK);
        (await d.Agent.PatchTicketAsync(current, new { Status = "Resolved" })).Expect(HttpStatusCode.OK);

        Assert.True(await PortalKit.WaitForAsync(aliceEvents, e => e.GetProperty("kind").GetString() == "reply"));
        Assert.True(await PortalKit.WaitForAsync(aliceEvents, e => e.GetProperty("kind").GetString() == "status"));
        lock (aliceEvents)
        {
            Assert.All(aliceEvents, e => Assert.Equal("at kind reference", string.Join(" ", e.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal))));
            Assert.DoesNotContain(aliceEvents, e => e.GetRawText().Contains("REPLYSECRET", StringComparison.Ordinal));
        }

        Assert.True(await PortalKit.SilentAsync(bobEvents), "another requester received the event");
    }

    [Fact]
    public async Task A_portal_reply_is_a_real_requester_message_that_reopens_triggers_automation_and_reaches_agents()
    {
        var d = await fixture.NewDeptAsync();
        var alice = await fixture.EmployeeAsync("alice");
        (await d.Admin.Client.PostAsync($"{d.Department.Url}/automations", new
        {
            name = "Customer replied",
            trigger = "RequesterMessageAdded",
            conditions = Array.Empty<object>(),
            actions = new object[] { new { type = "setPriority", value = "High" } },
        })).Expect(HttpStatusCode.Created);
        var ticket = await d.TicketForAsync(alice.Email, "Reopen me");
        var current = (await d.Agent.GetTicketAsync(ticket.Id)).Expect(HttpStatusCode.OK);
        (await d.Agent.PatchTicketAsync(current, new { Status = "Closed" })).Expect(HttpStatusCode.OK);
        var (_, agentEvents) = await fixture.ConnectPortalAsync(new PortalKit.Employee(d.Agent, d.Agent.Subject), _connections);
        await Task.Delay(300);

        var reply = (await alice.User.Client.PostAsync($"/api/portal/requests/{ticket.Reference()}/replies", new { body = "Still broken, please reopen." })).Expect(HttpStatusCode.Created).Json;
        Assert.Equal("You", reply.GetProperty("message").GetProperty("author").GetString());
        Assert.Equal("In progress", reply.GetProperty("statusLabel").GetString());

        var agentView = (await d.Agent.GetTicketAsync(ticket.Id)).Expect(HttpStatusCode.OK).Json;
        Assert.Equal("Open", agentView.GetProperty("status").GetString()); // D-15: a requester reply reopens the same ticket
        Assert.Equal("High", agentView.GetProperty("priority").GetString()); // RequesterMessageAdded automation ran
        var messages = (await d.Agent.Client.GetAsync($"/api/tickets/{ticket.Id}/messages")).Expect(HttpStatusCode.OK).Json.GetProperty("items").EnumerateArray().ToArray();
        var portalMessage = Assert.Single(messages, m => m.GetProperty("source").GetString() == "portal");
        Assert.Equal("Requester", portalMessage.GetProperty("authorType").GetString());
        Assert.Equal("Public", portalMessage.GetProperty("kind").GetString());
        Assert.True(await PortalKit.WaitForAsync(agentEvents, e => e.GetProperty("kind").GetString() == "message"));
        Assert.True(await PortalKit.WaitForAsync(agentEvents, e => e.GetProperty("kind").GetString() == "status"));
        Assert.Equal(1, await fixture.CountAsync("SELECT count(*) FROM ticketing.messages WHERE ticket_id = @t AND author_type = 'Requester' AND source = 'portal'", TicketKit.P("t", ticket.Id)));
    }

    [Fact]
    public async Task Portal_reply_input_is_validated_and_a_crafted_body_cannot_post_an_internal_note()
    {
        var d = await fixture.NewDeptAsync();
        var alice = await fixture.EmployeeAsync("alice");
        var ticket = await d.TicketForAsync(alice.Email);
        var url = $"/api/portal/requests/{ticket.Reference()}/replies";
        (await alice.User.Client.PostAsync(url, new { body = "" })).Expect(HttpStatusCode.BadRequest);
        (await alice.User.Client.PostAsync(url, new { body = new string('x', 20001) })).Expect(HttpStatusCode.BadRequest);

        // Unknown properties are ignored: the message is a plain requester message, the status is not touched.
        (await alice.User.Client.PostAsync(url, new { body = "crafted", kind = "Internal", authorType = "Agent", status = "Resolved", internalNote = true })).Expect(HttpStatusCode.Created);
        Assert.Equal(1, await fixture.CountAsync("SELECT count(*) FROM ticketing.messages WHERE ticket_id = @t AND body = 'crafted' AND kind = 'Public' AND author_type = 'Requester' AND source = 'portal'", TicketKit.P("t", ticket.Id)));
        Assert.Equal("Open", (await d.Agent.GetTicketAsync(ticket.Id)).Expect(HttpStatusCode.OK).Json.GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_requester_cannot_claim_a_requester_that_belongs_to_another_account()
    {
        var d = await fixture.NewDeptAsync();
        var form = await d.FormAsync(fixture, "General");
        var alice = await fixture.EmployeeAsync("alice");
        var ticket = await d.TicketForAsync(alice.Email, "Mine");
        Assert.Single((await alice.PortalListAsync()).GetProperty("items").EnumerateArray()); // alice claims her requester

        // Somebody else presents the same address (dev sign-in lets a test do that): the requester is already taken.
        var impostor = await fixture.SignInAsync($"imp-{Unique.Id()}", "Impostor", alice.Email);
        var asImpostor = new PortalKit.Employee(impostor, alice.Email);
        Assert.Empty((await asImpostor.PortalListAsync()).GetProperty("items").EnumerateArray());
        (await impostor.Client.GetAsync($"/api/portal/requests/{ticket.Reference()}")).Expect(HttpStatusCode.NotFound);
        (await impostor.Client.PostAsync("/api/portal/requests", new { formId = form.FormId, subject = "I am Alice", description = "x" })).Expect(HttpStatusCode.Conflict);
        Assert.Equal(1, await fixture.CountAsync("SELECT count(*) FROM ticketing.requester_links WHERE requester_id = (SELECT requester_id FROM ticketing.tickets WHERE id = @t)", TicketKit.P("t", ticket.Id)));
    }

    [Fact]
    public async Task Ownership_follows_the_link_not_the_address()
    {
        var d = await fixture.NewDeptAsync();
        var alice = await fixture.EmployeeAsync("alice");
        var ticket = await d.TicketForAsync(alice.Email, "Mine");
        await alice.PortalListAsync();

        // Alice's account signs in again with a different address (a rename): the request is still hers.
        var renamed = await fixture.SignInAsync(alice.User.Subject, "Alice Renamed", $"renamed-{Unique.Id()}@example.test");
        var list = (await renamed.Client.GetAsync("/api/portal/requests")).Expect(HttpStatusCode.OK).Json;
        Assert.Equal(ticket.Reference(), Assert.Single(list.GetProperty("items").EnumerateArray()).GetProperty("reference").GetString());
    }

    [Fact]
    public async Task Portal_lists_are_paginated_and_attachments_follow_ticket_authorization()
    {
        var d = await fixture.NewDeptAsync();
        var alice = await fixture.EmployeeAsync("alice");
        var bob = await fixture.EmployeeAsync("bob");
        var refs = new List<string>();
        for (var i = 0; i < 5; i++)
        {
            refs.Add((await d.TicketForAsync(alice.Email, $"Request {i}")).Reference());
            await Task.Delay(10);
        }

        var seen = new List<string>();
        string? cursor = null;
        for (var page = 0; page < 5; page++)
        {
            var json = await alice.PortalListAsync($"?filter=all&limit=2{(cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor))}");
            seen.AddRange(json.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("reference").GetString()!));
            cursor = json.GetProperty("nextCursor").GetString();
            if (cursor is null)
            {
                break;
            }
        }

        Assert.Equal(refs.AsEnumerable().Reverse(), seen); // most recent public activity first, no gaps, no repeats
        (await alice.User.Client.GetAsync("/api/portal/requests?limit=500")).Expect(HttpStatusCode.BadRequest);
        (await alice.User.Client.GetAsync("/api/portal/requests?filter=everything")).Expect(HttpStatusCode.BadRequest);
        (await bob.User.Client.GetAsync("/api/portal/requests?cursor=garbage")).Expect(HttpStatusCode.BadRequest);

        // A file attached to a portal reply goes through the one attachment pipeline; only its owner can open it.
        var ticketRef = refs[0];
        var content = new MultipartFormDataContent { { new StringContent("see attached"), "body" } };
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes("hello attachment"));
        file.Headers.ContentType = new("text/plain");
        content.Add(file, "files", "notes.txt");
        var reply = await alice.User.Client.PostMultipartAsync($"/api/portal/requests/{ticketRef}/replies", content);
        Assert.Equal(HttpStatusCode.Created, reply.Status);
        var attachment = reply.Json.GetProperty("message").GetProperty("attachments")[0];
        Assert.Equal("notes.txt", attachment.GetProperty("fileName").GetString());
        var id = attachment.GetProperty("id").GetGuid();
        Assert.Equal("hello attachment", (await alice.User.Client.GetAsync($"/api/portal/requests/{ticketRef}/attachments/{id}")).Expect(HttpStatusCode.OK).Body);
        (await bob.User.Client.GetAsync($"/api/portal/requests/{ticketRef}/attachments/{id}")).Expect(HttpStatusCode.NotFound);
        (await alice.User.Client.GetAsync($"/api/portal/requests/{refs[1]}/attachments/{id}")).Expect(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task An_attachment_of_an_internal_message_does_not_exist_for_the_requester()
    {
        var d = await fixture.NewDeptAsync();
        var alice = await fixture.EmployeeAsync("alice");
        var ticket = await d.TicketForAsync(alice.Email, "With hidden file");
        var note = (await d.Agent.PostMessageAsync(ticket.Id, "Internal", "see the attached log")).Expect(HttpStatusCode.Created).Json.GetProperty("message").GetProperty("id").GetGuid();
        Guid attachmentId = default;
        await fixture.WithScopeAsync(async sp =>
        {
            var store = (Helpdesk.Modules.Attachments.Contracts.IAttachmentStore)sp.GetService(typeof(Helpdesk.Modules.Attachments.Contracts.IAttachmentStore))!;
            var stored = await store.StoreAsync(new Helpdesk.Modules.Attachments.Contracts.StoreAttachmentRequest(d.Id, ticket.Id, note, "internal.txt", "text/plain", Encoding.UTF8.GetBytes("internal bytes"), false, null, "test-internal"));
            attachmentId = stored.Id;
        });

        // The agent can open it; the requester gets the same "not found" as for any other id and the conversation lists no file.
        (await d.Agent.Client.GetAsync($"/api/tickets/{ticket.Id}/attachments/{attachmentId}")).Expect(HttpStatusCode.OK);
        (await alice.User.Client.GetAsync($"/api/portal/requests/{ticket.Reference()}/attachments/{attachmentId}")).Expect(HttpStatusCode.NotFound);
        var detail = (await alice.User.Client.GetAsync($"/api/portal/requests/{ticket.Reference()}")).Expect(HttpStatusCode.OK).Body;
        Assert.DoesNotContain("internal.txt", detail, StringComparison.Ordinal);
        Assert.DoesNotContain(attachmentId.ToString(), detail, StringComparison.Ordinal);
    }
}
