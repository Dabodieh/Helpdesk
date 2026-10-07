using System.Net;
using Helpdesk.Host.Tests.Infrastructure;
using Helpdesk.Host.Tests.Ticketing;
using Helpdesk.Modules.Mail.Providers;

namespace Helpdesk.Host.Tests.Mail;

/// <summary>Outbound delivery: truthful states, retries without duplicate sends, visible failures, sender identity.</summary>
public sealed class MailOutboundTests(HelpdeskFixture fixture) : IClassFixture<HelpdeskFixture>
{
    private async Task<(Dept D, Mailbox Box, Guid TicketId, string Requester)> StartAsync(string? signature = "Regards")
    {
        var d = await fixture.NewDeptAsync();
        var box = await fixture.CreateMailboxAsync(d, signature: signature);
        var requester = MailKit.NewSender();
        var subject = $"Outbound {Unique.Id()}";
        await fixture.ReceiveAsync(box, MailKit.Mail(requester, subject));
        var ticket = Assert.Single(await d.Agent.TicketsAsync(d, subject));
        return (d, box, ticket.GetProperty("id").GetGuid(), requester);
    }

    private static async Task<string> DeliveryAsync(Dept d, Guid ticketId, string messageText)
    {
        var messages = await d.Agent.MessagesAsync(ticketId);
        return messages.Single(m => m.Str("body") == messageText).Str("deliveryState");
    }

    [Fact]
    public async Task A_transient_failure_is_retried_and_the_message_is_sent_exactly_once()
    {
        var s = await StartAsync();
        fixture.Fake().FailNextSends(1);

        (await s.D.Agent.PostMessageAsync(s.TicketId, "Public", "First attempt fails")).Expect(HttpStatusCode.Created);
        await fixture.DispatchAsync();
        Assert.Empty(fixture.Fake().SentMail(s.Box.Address));
        Assert.Equal("Pending", await DeliveryAsync(s.D, s.TicketId, "First attempt fails")); // still honest: not sent yet

        await fixture.ExecuteAsync("UPDATE mail.messages SET next_attempt_at = now() - interval '1 minute' WHERE ticket_id = @t AND direction = 'Outbound'", TicketKit.P("t", s.TicketId));
        await fixture.DispatchAsync();
        await fixture.DispatchAsync(); // a second dispatch must not send it again

        Assert.Single(fixture.Fake().SentMail(s.Box.Address));
        Assert.Equal("Sent", await DeliveryAsync(s.D, s.TicketId, "First attempt fails"));
    }

    [Fact]
    public async Task A_permanent_failure_is_visible_on_the_message_and_can_be_retried()
    {
        var s = await StartAsync();
        fixture.Fake().FailNextSends(1, permanently: true);

        (await s.D.Agent.PostMessageAsync(s.TicketId, "Public", "Rejected by the provider")).Expect(HttpStatusCode.Created);
        await fixture.DispatchAsync();

        var failed = (await s.D.Agent.MessagesAsync(s.TicketId)).Single(m => m.Str("body") == "Rejected by the provider");
        Assert.Equal("Failed", failed.Str("deliveryState"));
        Assert.Equal("rejected_by_provider", failed.Str("deliveryError")); // a safe category; the raw provider code stays in the mail attention list
        Assert.Empty(fixture.Fake().SentMail(s.Box.Address));
        var attention = (await s.D.Admin.Client.GetAsync($"{s.D.Department.Url}/mail/attention")).Expect(HttpStatusCode.OK);
        Assert.Contains(attention.Json.GetProperty("items").EnumerateArray(), i => i.Str("state") == "Failed" && i.Str("direction") == "Outbound");

        // Only agents who may reply can retry; the retry goes back through the outbox and is then sent.
        var retryUrl = $"/api/tickets/{s.TicketId}/messages/{failed.GetProperty("id").GetGuid()}/retry-delivery";
        (await s.D.Viewer.Client.PostAsync(retryUrl)).Expect(HttpStatusCode.Forbidden);
        var retried = (await s.D.Agent.Client.PostAsync(retryUrl)).Expect(HttpStatusCode.OK);
        Assert.Equal("Pending", retried.Json.Str("deliveryState"));
        await fixture.DispatchAsync();

        Assert.Single(fixture.Fake().SentMail(s.Box.Address));
        Assert.Equal("Sent", await DeliveryAsync(s.D, s.TicketId, "Rejected by the provider"));
        (await s.D.Agent.Client.PostAsync(retryUrl)).ExpectCode(HttpStatusCode.Conflict, "invalid_state");
    }

    [Fact]
    public async Task A_crash_after_sending_does_not_send_again()
    {
        var s = await StartAsync();
        (await s.D.Agent.PostMessageAsync(s.TicketId, "Public", "Sent once")).Expect(HttpStatusCode.Created);
        await fixture.DispatchAsync();
        Assert.Single(fixture.Fake().SentMail(s.Box.Address));

        // Simulate a worker that died after the provider accepted the send but before the state was saved.
        await fixture.ExecuteAsync(
            "UPDATE mail.messages SET state = 'Queued', next_attempt_at = now() - interval '1 minute' WHERE ticket_id = @t AND direction = 'Outbound'", TicketKit.P("t", s.TicketId));
        await fixture.DispatchAsync();

        Assert.Single(fixture.Fake().SentMail(s.Box.Address));
        Assert.Equal("Sent", await DeliveryAsync(s.D, s.TicketId, "Sent once"));
    }

    [Fact]
    public async Task An_unreported_outcome_is_reported_by_the_dispatcher()
    {
        var s = await StartAsync();
        (await s.D.Agent.PostMessageAsync(s.TicketId, "Public", "Report me")).Expect(HttpStatusCode.Created);
        await fixture.DispatchAsync();

        // Pretend telling Ticketing failed: the row is Sent but unreported and the ticket still says Pending.
        await fixture.ExecuteAsync("UPDATE mail.messages SET delivery_reported_at = NULL WHERE ticket_id = @t AND direction = 'Outbound'", TicketKit.P("t", s.TicketId));
        await fixture.ExecuteAsync("UPDATE ticketing.messages SET delivery_state = 'Pending' WHERE ticket_id = @t AND author_type = 'Agent'", TicketKit.P("t", s.TicketId));
        await fixture.DispatchAsync();

        Assert.Equal("Sent", await DeliveryAsync(s.D, s.TicketId, "Report me"));
    }

    [Fact]
    public async Task Internal_notes_never_produce_email()
    {
        var s = await StartAsync();

        (await s.D.Agent.PostMessageAsync(s.TicketId, "Internal", "Private remark")).Expect(HttpStatusCode.Created);
        await fixture.DispatchAsync();

        Assert.Empty(fixture.Fake().SentMail(s.Box.Address));
        Assert.Equal(0, await fixture.CountAsync("SELECT count(*) FROM mail.messages WHERE ticket_id = @t AND direction = 'Outbound'", TicketKit.P("t", s.TicketId)));
    }

    [Fact]
    public async Task A_deactivated_ticket_mailbox_never_switches_the_sender()
    {
        var s = await StartAsync();
        var second = await fixture.CreateMailboxAsync(s.D);
        var list = (await s.D.Admin.Client.GetAsync($"{s.D.Department.Url}/mailboxes/{s.Box.Id}")).Expect(HttpStatusCode.OK);
        (await s.D.Admin.Client.PatchAsync($"{s.D.Department.Url}/mailboxes/{s.Box.Id}", new { version = list.Version, isActive = false })).Expect(HttpStatusCode.OK);

        // The ticket belongs to the first mailbox, which is now inactive: the reply is recorded but NOT sent from the other mailbox.
        var unsent = (await s.D.Agent.PostMessageAsync(s.TicketId, "Public", "Not via the other desk")).Expect(HttpStatusCode.Created);
        Assert.Equal("NotDelivered", unsent.Json.GetProperty("message").Str("deliveryState"));
        Assert.Equal("mailbox_unavailable", unsent.Json.GetProperty("message").Str("deliveryError"));
        await fixture.DispatchAsync();
        Assert.Empty(fixture.Fake().SentMail(s.Box.Address));
        Assert.Empty(fixture.Fake().SentMail(second.Address));
    }

    [Fact]
    public async Task A_ticket_with_no_stored_mailbox_blocks_the_send_and_never_uses_the_department_default()
    {
        var s = await StartAsync();
        var defaultBox = await fixture.CreateMailboxAsync(s.D, isDefault: true); // an active department default exists
        await fixture.ExecuteAsync("UPDATE ticketing.tickets SET mailbox_id = NULL WHERE id = @t", TicketKit.P("t", s.TicketId)); // legacy / mailbox-less ticket

        var blocked = (await s.D.Agent.PostMessageAsync(s.TicketId, "Public", "No sender on this ticket")).Expect(HttpStatusCode.Created);
        Assert.Equal("NotDelivered", blocked.Json.GetProperty("message").Str("deliveryState"));
        Assert.Equal("no_ticket_mailbox", blocked.Json.GetProperty("message").Str("deliveryError"));
        await fixture.DispatchAsync();
        Assert.Empty(fixture.Fake().SentMail(defaultBox.Address));
        Assert.Empty(fixture.Fake().SentMail(s.Box.Address));
        Assert.Equal(0, await fixture.CountAsync("SELECT count(*) FROM mail.messages WHERE ticket_id = @t AND direction = 'Outbound'", TicketKit.P("t", s.TicketId)));

        // With a valid active stored mailbox the same ticket sends normally, from that mailbox only.
        await fixture.ExecuteAsync("UPDATE ticketing.tickets SET mailbox_id = @m WHERE id = @t", TicketKit.P("t", s.TicketId), TicketKit.P("m", s.Box.Id));
        (await s.D.Agent.PostMessageAsync(s.TicketId, "Public", "Now it has one")).Expect(HttpStatusCode.Created);
        await fixture.DispatchAsync();
        Assert.Single(fixture.Fake().SentMail(s.Box.Address));
        Assert.Empty(fixture.Fake().SentMail(defaultBox.Address));
    }

    [Fact]
    public async Task Replies_never_leave_from_another_departments_mailbox()
    {
        var a = await StartAsync();
        var b = await fixture.NewDeptAsync();
        var boxB = await fixture.CreateMailboxAsync(b);

        (await a.D.Agent.PostMessageAsync(a.TicketId, "Public", "From IT")).Expect(HttpStatusCode.Created);
        await fixture.DispatchAsync();

        Assert.Single(fixture.Fake().SentMail(a.Box.Address));
        Assert.Empty(fixture.Fake().SentMail(boxB.Address));
        // And the database refuses a ticket pointing at a mailbox of another department.
        var ex = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => fixture.ExecuteAsync(
            "UPDATE ticketing.tickets SET mailbox_id = @m WHERE id = @t", TicketKit.P("m", boxB.Id), TicketKit.P("t", a.TicketId)));
        Assert.Equal("fk_tickets_mailbox_department", ex.ConstraintName);
    }

    [Fact]
    public async Task The_ticket_reply_without_a_reply_target_goes_out_as_a_new_message_with_the_reference_in_the_subject()
    {
        var d = await fixture.NewDeptAsync();
        var box = await fixture.CreateMailboxAsync(d);
        var created = (await d.Agent.Client.PostAsync(
            $"{d.Department.Url}/tickets", new { subject = "Walk-in", description = "At the desk", requester = new { email = MailKit.NewSender(), displayName = "Walker" } })).Expect(HttpStatusCode.Created);

        (await d.Agent.PostMessageAsync(created.Id, "Public", "Hello Walker")).Expect(HttpStatusCode.Created);
        await fixture.DispatchAsync();

        var sent = Assert.Single(fixture.Fake().SentMail(box.Address));
        Assert.Contains($"[{created.Json.Str("reference")}]", sent.Subject, StringComparison.Ordinal);
        Assert.Null(sent.InReplyTo);
    }
}
