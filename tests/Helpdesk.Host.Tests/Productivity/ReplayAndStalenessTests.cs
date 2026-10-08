#pragma warning disable CA1861 // one-off request payloads
using System.Net;
using Helpdesk.Host.Tests.Infrastructure;
using Helpdesk.Host.Tests.Mail;
using Helpdesk.Host.Tests.Portal;
using Helpdesk.Host.Tests.Ticketing;

namespace Helpdesk.Host.Tests.Productivity;

/// <summary>Replay, idempotency and staleness at the write edges (WS-10): repeated posts, double-submitted portal replies, bulk edits of changed tickets, racing first saves.</summary>
public sealed class ReplayAndStalenessTests(HelpdeskFixture fixture) : IClassFixture<HelpdeskFixture>
{
    [Fact]
    public async Task A_repeated_agent_post_with_the_same_client_message_id_is_one_message_and_one_email()
    {
        var d = await fixture.NewDeptAsync();
        var box = await fixture.CreateMailboxAsync(d);
        var subject = $"Replay {Unique.Id()}";
        await fixture.ReceiveAsync(box, MailKit.Mail(MailKit.NewSender(), subject));
        var ticketId = Assert.Single(await d.Agent.TicketsAsync(d, subject)).GetProperty("id").GetGuid();
        var clientId = Guid.NewGuid();

        var first = (await d.Agent.Client.PostAsync($"/api/tickets/{ticketId}/messages", new { kind = "Public", body = "On it", clientMessageId = clientId })).Expect(HttpStatusCode.Created);
        var again = (await d.Agent.Client.PostAsync($"/api/tickets/{ticketId}/messages", new { kind = "Public", body = "On it", clientMessageId = clientId })).Expect(HttpStatusCode.Created);

        Assert.Equal(first.Json.GetProperty("message").GetProperty("id").GetGuid(), again.Json.GetProperty("message").GetProperty("id").GetGuid());
        await fixture.DispatchAsync();
        Assert.Single(fixture.Fake().SentMail(box.Address));
        Assert.Equal(1, await fixture.CountAsync("SELECT count(*) FROM ticketing.messages WHERE ticket_id = @t AND body = 'On it'", TicketKit.P("t", ticketId)));

        // A malformed id is a validation error; a different id is a different message.
        (await d.Agent.Client.PostAsync($"/api/tickets/{ticketId}/messages", new { kind = "Public", body = "x", clientMessageId = "not-a-guid" })).Expect(HttpStatusCode.BadRequest);
        (await d.Agent.Client.PostAsync($"/api/tickets/{ticketId}/messages", new { kind = "Public", body = "On it", clientMessageId = Guid.NewGuid() })).Expect(HttpStatusCode.Created);
        Assert.Equal(2, await fixture.CountAsync("SELECT count(*) FROM ticketing.messages WHERE ticket_id = @t AND body = 'On it'", TicketKit.P("t", ticketId)));
    }

    [Fact]
    public async Task A_double_submitted_portal_reply_is_one_reply_one_reopen_and_one_requester_notification()
    {
        var d = await fixture.NewDeptAsync();
        var alice = await fixture.EmployeeAsync("alice");
        var created = await d.TicketForAsync(alice.Email, $"Portal replay {Unique.Id()}");
        var reference = created.Json.GetProperty("reference").GetString()!;
        var clientId = Guid.NewGuid();

        var first = (await alice.User.Client.PostAsync($"/api/portal/requests/{reference}/replies", new { body = "Still broken", clientMessageId = clientId })).Expect(HttpStatusCode.Created);
        var second = (await alice.User.Client.PostAsync($"/api/portal/requests/{reference}/replies", new { body = "Still broken", clientMessageId = clientId })).Expect(HttpStatusCode.Created);

        Assert.Equal(first.Json.GetProperty("message").GetProperty("id").GetGuid(), second.Json.GetProperty("message").GetProperty("id").GetGuid());
        Assert.Equal(1, await fixture.CountAsync("SELECT count(*) FROM ticketing.messages WHERE ticket_id = @t AND body = 'Still broken'", TicketKit.P("t", created.Id)));
        Assert.Equal(1, await fixture.CountAsync("SELECT count(*) FROM ticketing.automation_outbox WHERE ticket_id = @t AND trigger = 'RequesterMessageAdded'", TicketKit.P("t", created.Id)));
    }

    [Fact]
    public async Task A_bulk_edit_leaves_a_ticket_that_changed_since_it_was_selected_and_applies_to_the_rest()
    {
        var d = await fixture.NewDeptAsync();
        var stale = await d.Lead.CreateTicketAsync(d.Department);
        var fresh = await d.Lead.CreateTicketAsync(d.Department);
        var listed = (await d.Lead.Client.GetAsync($"/api/tickets?queue=all&departmentId={d.Department.Id}&limit=50")).Expect(HttpStatusCode.OK).Json.GetProperty("items").EnumerateArray().ToList();
        string VersionOf(Guid id) => listed.Single(t => t.GetProperty("id").GetGuid() == id).GetProperty("version").GetString()!;
        var versions = new Dictionary<Guid, string> { [stale.Id] = VersionOf(stale.Id), [fresh.Id] = VersionOf(fresh.Id) };

        // Somebody else changes one of them after the list was read.
        var current = await d.Agent.GetTicketAsync(stale.Id);
        (await d.Agent.Client.PatchAsync($"/api/tickets/{stale.Id}", new { version = current.Version, priority = "Urgent" })).Expect(HttpStatusCode.OK);

        var result = (await d.Lead.Client.PostAsync("/api/tickets/bulk", new { ticketIds = new[] { stale.Id, fresh.Id }, priority = "Low", versions })).Expect(HttpStatusCode.OK).Json;
        string Outcome(Guid id) => result.GetProperty("results").EnumerateArray().Single(r => r.GetProperty("ticketId").GetGuid() == id).GetProperty("outcome").GetString()!;
        Assert.Equal("changed", Outcome(stale.Id));
        Assert.Equal("updated", Outcome(fresh.Id));
        Assert.Equal("Urgent", (await d.Agent.GetTicketAsync(stale.Id)).Json.GetProperty("priority").GetString()); // the intermediate decision survives
        Assert.Equal("Low", (await d.Agent.GetTicketAsync(fresh.Id)).Json.GetProperty("priority").GetString());

        // Without versions the behaviour is unchanged (last write wins), so existing clients keep working.
        (await d.Lead.Client.PostAsync("/api/tickets/bulk", new { ticketIds = new[] { stale.Id }, priority = "Low" })).Expect(HttpStatusCode.OK);
        Assert.Equal("Low", (await d.Agent.GetTicketAsync(stale.Id)).Json.GetProperty("priority").GetString());
    }

    [Fact]
    public async Task Two_first_saves_of_queue_preferences_at_the_same_time_both_succeed()
    {
        var d = await fixture.NewDeptAsync();
        var user = d.Agent;
        var saves = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => user.Client.PutAsync("/api/me/queue-preferences", new { columns = new[] { "status", "assignee" } })));
        Assert.All(saves, r => Assert.Equal(HttpStatusCode.OK, r.Status));
    }
}
