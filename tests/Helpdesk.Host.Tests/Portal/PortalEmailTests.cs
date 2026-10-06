using System.Net;
using Helpdesk.Host.Tests.Infrastructure;
using Helpdesk.Host.Tests.Mail;
using Helpdesk.Host.Tests.Ticketing;
using Helpdesk.Modules.Mail.Providers;

namespace Helpdesk.Host.Tests.Portal;

/// <summary>Email and the portal are two channels over ONE conversation.</summary>
public sealed class PortalEmailTests(HelpdeskFixture fixture) : IClassFixture<HelpdeskFixture>
{
    [Fact]
    public async Task Email_and_portal_share_one_ticket_and_one_public_conversation_while_internal_notes_stay_invisible()
    {
        var d = await fixture.NewDeptAsync();
        var box = await fixture.CreateMailboxAsync(d);
        var alice = await fixture.EmployeeAsync("alice");
        var subject = $"Email first {Unique.Id()}";

        // 1. The requester emails the department mailbox: one ticket, source email.
        await fixture.ReceiveAsync(box, MailKit.Mail(alice.Email, subject, "My monitor is flickering.", "Alice"));
        var ticket = Assert.Single(await d.Agent.TicketsAsync(d, subject));
        var ticketId = ticket.GetProperty("id").GetGuid();
        var reference = ticket.GetProperty("reference").GetString()!;
        Assert.Equal("email", (await d.Agent.GetTicketAsync(ticketId)).Expect(HttpStatusCode.OK).Json.GetProperty("source").GetString());

        // 2. The same person signs in to the portal (verified address = the requester's address): it is in My requests, nothing else needed.
        var list = (await alice.PortalListAsync()).GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(reference, Assert.Single(list).GetProperty("reference").GetString());

        // 3. She replies in the portal: a requester message on the same ticket (not a new one, nothing goes through the mailbox).
        var sentBefore = fixture.Fake().SentMail(box.Address).Count;
        (await alice.User.Client.PostAsync($"/api/portal/requests/{reference}/replies", new { body = "It also makes a buzzing noise." })).Expect(HttpStatusCode.Created);
        Assert.Single(await d.Agent.TicketsAsync(d, subject));
        Assert.Equal(sentBefore, fixture.Fake().SentMail(box.Address).Count);

        // 4. An agent adds a private note and then replies publicly through the existing outbound pipeline.
        (await d.Agent.PostMessageAsync(ticketId, "Internal", "PRIVATE: probably the cable")).Expect(HttpStatusCode.Created);
        (await d.Agent.PostMessageAsync(ticketId, "Public", "Please try another cable.")).Expect(HttpStatusCode.Created);
        await fixture.DispatchAsync();
        var sent = Assert.Single(fixture.Fake().SentMail(box.Address));
        Assert.Contains("Please try another cable.", sent.BodyHtml, StringComparison.Ordinal);

        // 5. The reply by email is visible in the portal; the note is not, in any form.
        var conversation = (await alice.User.Client.GetAsync($"/api/portal/requests/{reference}")).Expect(HttpStatusCode.OK).Json;
        Assert.Equal(["You", "You", "Support"], conversation.GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("author").GetString()!).ToArray());
        Assert.Contains("Please try another cable.", conversation.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE", conversation.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("nternal", conversation.GetRawText(), StringComparison.Ordinal);

        // 6. She answers the email she received: threaded onto the same ticket and shown in the portal as hers.
        await fixture.ReceiveAsync(box, FakeMailProvider.CustomerReplyTo(sent, alice.Email, "Thanks, the new cable works."));
        Assert.Single(await d.Agent.TicketsAsync(d, subject));
        var after = (await alice.User.Client.GetAsync($"/api/portal/requests/{reference}")).Expect(HttpStatusCode.OK).Json;
        Assert.Equal(["You", "You", "Support", "You"], after.GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("author").GetString()!).ToArray());
        Assert.Equal(1, await fixture.CountAsync("SELECT count(*) FROM ticketing.tickets WHERE department_id = @d AND subject = @s", TicketKit.P("d", d.Id), TicketKit.P("s", subject)));

        // 7. One conversation, three sources recorded on the messages.
        var sources = (await d.Agent.Client.GetAsync($"/api/tickets/{ticketId}/messages")).Expect(HttpStatusCode.OK).Json.GetProperty("items").EnumerateArray()
            .Where(m => m.GetProperty("kind").GetString() == "Public").Select(m => m.GetProperty("source").GetString()!).Order().ToArray();
        Assert.Equal(["email", "email", "portal", "web"], sources);
    }
}
