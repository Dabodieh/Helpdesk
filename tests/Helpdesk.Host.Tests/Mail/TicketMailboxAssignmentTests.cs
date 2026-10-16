#pragma warning disable CA1861 // one-off request payloads
using System.Net;
using Helpdesk.Host.Tests.Infrastructure;
using Helpdesk.Host.Tests.Ticketing;

namespace Helpdesk.Host.Tests.Mail;

/// <summary>D22: a ticket with no stored mailbox gets one only through an explicit, audited, department-scoped action; it can be corrected only until something was sent.</summary>
public sealed class TicketMailboxAssignmentTests(HelpdeskFixture fixture) : IClassFixture<HelpdeskFixture>
{
    private static Task<ApiResponse> AssignAsync(TestUser user, Guid ticketId, string version, Guid mailboxId) =>
        user.Client.PostAsync($"/api/tickets/{ticketId}/mailbox", new { version, mailboxId });

    [Fact]
    public async Task Only_a_mailbox_administrator_with_access_to_the_ticket_can_assign_an_eligible_active_mailbox_of_the_same_department()
    {
        var d = await fixture.NewDeptAsync();
        var other = await fixture.NewDeptAsync();
        var ticket = await d.Agent.CreateTicketAsync(d.Department); // the department has no mailbox yet: the ticket has none
        Assert.Equal(System.Text.Json.JsonValueKind.Null, ticket.Json.GetProperty("mailbox").ValueKind);
        var mine = await fixture.CreateMailboxAsync(d);
        var foreign = await fixture.CreateMailboxAsync(other);

        (await AssignAsync(d.Agent, ticket.Id, ticket.Version, mine.Id)).Expect(HttpStatusCode.Forbidden); // tickets.reply style access is not enough
        (await AssignAsync(other.Admin, ticket.Id, ticket.Version, mine.Id)).Expect(HttpStatusCode.NotFound);  // another department's mailbox administrator: the ticket does not exist for them
        (await AssignAsync(d.Admin, ticket.Id, ticket.Version, foreign.Id)).Expect(HttpStatusCode.BadRequest);
        (await AssignAsync(d.Admin, ticket.Id, ticket.Version, Guid.NewGuid())).Expect(HttpStatusCode.BadRequest);

        var assigned = (await AssignAsync(d.Admin, ticket.Id, ticket.Version, mine.Id)).Expect(HttpStatusCode.OK);
        Assert.Equal(mine.Id, assigned.Json.GetProperty("mailbox").GetProperty("id").GetGuid());
        Assert.True(assigned.Json.GetProperty("mailboxAssigned").GetBoolean());
        Assert.Equal(1, await fixture.CountAuditAsync("ticket.mailbox_assigned", ticket.Id));

        // From now on the ticket is answered from that mailbox (and only that one).
        (await d.Agent.PostMessageAsync(ticket.Id, "Public", "Now with a sender")).Expect(HttpStatusCode.Created);
        await fixture.DispatchAsync();
        Assert.Equal(mine.Address, Assert.Single(fixture.Fake().SentMail(mine.Address)).Mailbox);
    }

    [Fact]
    public async Task The_assignment_can_be_corrected_until_a_public_reply_is_submitted_and_is_then_permanently_fixed()
    {
        var d = await fixture.NewDeptAsync();
        var ticket = await d.Agent.CreateTicketAsync(d.Department);
        var first = await fixture.CreateMailboxAsync(d);
        var second = await fixture.CreateMailboxAsync(d);

        var assigned = (await AssignAsync(d.Admin, ticket.Id, ticket.Version, first.Id)).Expect(HttpStatusCode.OK);
        var corrected = (await AssignAsync(d.Admin, ticket.Id, assigned.Version, second.Id)).Expect(HttpStatusCode.OK);
        Assert.Equal(second.Id, corrected.Json.GetProperty("mailbox").GetProperty("id").GetGuid());
        Assert.Equal(1, await fixture.CountAuditAsync("ticket.mailbox_corrected", ticket.Id));

        (await d.Agent.PostMessageAsync(ticket.Id, "Public", "Sent from the second")).Expect(HttpStatusCode.Created); // queued: no race with a change
        var latest = await d.Admin.GetTicketAsync(ticket.Id);
        (await AssignAsync(d.Admin, ticket.Id, latest.Version, first.Id)).ExpectCode(HttpStatusCode.Conflict, "invalid_state");
        await fixture.DispatchAsync();
        latest = await d.Admin.GetTicketAsync(ticket.Id);
        (await AssignAsync(d.Admin, ticket.Id, latest.Version, first.Id)).ExpectCode(HttpStatusCode.Conflict, "invalid_state");
        Assert.Equal(second.Id, await fixture.ScalarAsync<Guid>("SELECT mailbox_id FROM ticketing.tickets WHERE id = @t", TicketKit.P("t", ticket.Id)));
    }

    [Fact]
    public async Task A_mailbox_chosen_when_the_ticket_was_created_is_never_reassigned_through_this_action()
    {
        var d = await fixture.NewDeptAsync();
        var original = await fixture.CreateMailboxAsync(d);
        var spare = await fixture.CreateMailboxAsync(d);
        var ticket = await d.Agent.CreateTicketAsync(d.Department); // stores the default mailbox at creation

        (await AssignAsync(d.Admin, ticket.Id, ticket.Version, spare.Id)).ExpectCode(HttpStatusCode.Conflict, "invalid_state");
        Assert.Equal(original.Id, await fixture.ScalarAsync<Guid>("SELECT mailbox_id FROM ticketing.tickets WHERE id = @t", TicketKit.P("t", ticket.Id)));
    }

    [Fact]
    public async Task Agents_can_take_and_release_their_own_tickets_but_cannot_choose_another_person_as_assignee()
    {
        var d = await fixture.NewDeptAsync();
        var ticket = await d.Agent.CreateTicketAsync(d.Department);

        var taken = (await d.Agent.AssignAsync(ticket.Id, ticket.Version, d.Agent.Id)).Expect(HttpStatusCode.OK);
        Assert.Equal(d.Agent.Id, taken.Json.GetProperty("assignee").GetProperty("id").GetGuid());
        (await d.Agent.AssignAsync(ticket.Id, taken.Version, d.Lead.Id)).Expect(HttpStatusCode.Forbidden);
        (await d.Viewer.AssignAsync(ticket.Id, taken.Version, null)).Expect(HttpStatusCode.Forbidden);
    }
}
