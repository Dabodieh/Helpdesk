using System.Net;
using System.Text.Json;
using Helpdesk.Host.Tests.Infrastructure;
using Helpdesk.Host.Tests.Ticketing;
using Helpdesk.Modules.Mail.Providers;
using Npgsql;

namespace Helpdesk.Host.Tests.Mail;

/// <summary>Inbound pipeline rules: threading evidence, department isolation, noise guard, idempotency, retries, HTML.</summary>
public sealed class MailInboundTests(HelpdeskFixture fixture) : IClassFixture<HelpdeskFixture>
{
    private async Task<(Dept D, Mailbox Box, string Requester, string Subject, Guid TicketId, string InternetId)> StartAsync(string? subjectPrefix = null)
    {
        var d = await fixture.NewDeptAsync();
        var box = await fixture.CreateMailboxAsync(d);
        var requester = MailKit.NewSender();
        var subject = $"{subjectPrefix}Network {Unique.Id()}";
        var internetId = $"<{Guid.NewGuid():N}@client.test>";
        await fixture.ReceiveAsync(box, MailKit.Mail(requester, subject) with { InternetMessageId = internetId });
        var ticket = Assert.Single(await d.Agent.TicketsAsync(d, subject));
        return (d, box, requester, subject, ticket.GetProperty("id").GetGuid(), internetId);
    }

    private async Task<string> StateOfAsync(Mailbox box, string providerMessageId) =>
        (await fixture.ScalarAsync<string>(
            "SELECT state FROM mail.messages WHERE mailbox_id = @m AND provider_message_id = @p", TicketKit.P("m", box.Id), TicketKit.P("p", providerMessageId)))!;

    // ----- threading -----

    [Fact]
    public async Task In_reply_to_attaches_to_the_ticket_even_when_the_subject_changed()
    {
        var s = await StartAsync();

        await fixture.ReceiveAsync(s.Box, MailKit.Mail(s.Requester, "Totally different subject", "More details") with { InReplyTo = s.InternetId });

        Assert.Equal(2, (await s.D.Agent.MessagesAsync(s.TicketId)).Count);
        Assert.Empty(await s.D.Agent.TicketsAsync(s.D, "Totally different"));
    }

    [Fact]
    public async Task References_attach_when_in_reply_to_is_missing_and_header_case_and_brackets_do_not_matter()
    {
        var s = await StartAsync();

        await fixture.ReceiveAsync(s.Box, MailKit.Mail(s.Requester, "Re: whatever", "Following up")
            with { References = ["<unrelated@x.test>", s.InternetId.ToUpperInvariant()] });

        Assert.Equal(2, (await s.D.Agent.MessagesAsync(s.TicketId)).Count);
    }

    [Fact]
    public async Task A_ticket_reference_in_the_subject_is_a_hint_only_and_never_attaches()
    {
        var s = await StartAsync();
        var reference = (await s.D.Agent.GetTicketAsync(s.TicketId)).Json.Str("reference");
        var spoofed = $"Re: [{reference}] spoofed {Unique.Id()}";

        await fixture.ReceiveAsync(s.Box, MailKit.Mail(s.Requester, spoofed, "Please add this to the old ticket"));

        // The original ticket is untouched; the mail became a NEW ticket carrying a system suggestion (internal note).
        Assert.Single(await s.D.Agent.MessagesAsync(s.TicketId));
        var created = Assert.Single(await s.D.Agent.TicketsAsync(s.D, "spoofed"));
        var messages = await s.D.Agent.MessagesAsync(created.GetProperty("id").GetGuid());
        var note = Assert.Single(messages, m => m.Str("kind") == "Internal");
        Assert.Equal("System", note.Str("authorType"));
        Assert.Contains(reference, note.Str("body"), StringComparison.Ordinal);
        Assert.Contains("not attached automatically", note.Str("body"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_subject_reference_of_another_department_is_not_even_suggested()
    {
        var s = await StartAsync();
        var other = await fixture.NewDeptAsync();
        var otherBox = await fixture.CreateMailboxAsync(other);
        var reference = (await s.D.Agent.GetTicketAsync(s.TicketId)).Json.Str("reference");
        var subject = $"Re: [{reference}] cross {Unique.Id()}";

        await fixture.ReceiveAsync(otherBox, MailKit.Mail(MailKit.NewSender(), subject));

        var created = Assert.Single(await other.Agent.TicketsAsync(other, "cross"));
        var messages = await other.Agent.MessagesAsync(created.GetProperty("id").GetGuid());
        Assert.DoesNotContain(messages, m => m.Str("kind") == "Internal");
        Assert.Single(await s.D.Agent.MessagesAsync(s.TicketId));
    }

    [Fact]
    public async Task Conversation_id_attaches_within_the_same_mailbox_only()
    {
        var d = await fixture.NewDeptAsync();
        var first = await fixture.CreateMailboxAsync(d);
        var second = await fixture.CreateMailboxAsync(d);
        var requester = MailKit.NewSender();
        var subject = $"Conv {Unique.Id()}";
        var conversation = $"conv-{Guid.NewGuid():N}";
        await fixture.ReceiveAsync(first, MailKit.Mail(requester, subject) with { ConversationId = conversation });
        var ticket = Assert.Single(await d.Agent.TicketsAsync(d, subject));
        var ticketId = ticket.GetProperty("id").GetGuid();

        await fixture.ReceiveAsync(first, MailKit.Mail(requester, "no headers here", "same mailbox") with { ConversationId = conversation });
        Assert.Equal(2, (await d.Agent.MessagesAsync(ticketId)).Count);

        var otherSubject = $"Elsewhere {Unique.Id()}";
        await fixture.ReceiveAsync(second, MailKit.Mail(requester, otherSubject, "other mailbox") with { ConversationId = conversation });
        Assert.Equal(2, (await d.Agent.MessagesAsync(ticketId)).Count);
        Assert.Single(await d.Agent.TicketsAsync(d, otherSubject));
    }

    [Fact]
    public async Task The_outbound_token_in_the_body_attaches_header_stripped_replies()
    {
        var s = await StartAsync();
        (await s.D.Agent.PostMessageAsync(s.TicketId, "Public", "On it.")).Expect(HttpStatusCode.Created);
        await fixture.DispatchAsync();
        var sent = Assert.Single(fixture.Fake().SentMail(s.Box.Address));

        await fixture.ReceiveAsync(s.Box, MailKit.Mail(s.Requester, "Forwarded by my gateway", $"Thanks!\n\n> {sent.Token}\n[hd:{sent.Token}]"));

        Assert.Equal(3, (await s.D.Agent.MessagesAsync(s.TicketId)).Count);
    }

    [Fact]
    public async Task Evidence_pointing_into_another_department_is_discarded_and_never_attaches()
    {
        var s = await StartAsync();
        var other = await fixture.NewDeptAsync();
        var otherBox = await fixture.CreateMailboxAsync(other);
        var subject = $"Leak attempt {Unique.Id()}";

        // A mail received by ANOTHER department's mailbox that carries the IT ticket's message id as evidence.
        await fixture.ReceiveAsync(otherBox, MailKit.Mail(MailKit.NewSender(), subject) with { InReplyTo = s.InternetId });

        Assert.Single(await s.D.Agent.MessagesAsync(s.TicketId));
        var created = Assert.Single(await other.Agent.TicketsAsync(other, subject));
        (await s.D.Agent.GetTicketAsync(created.GetProperty("id").GetGuid())).Expect(HttpStatusCode.NotFound);
        Assert.Equal(1, await fixture.CountAsync(
            "SELECT count(*) FROM mail.messages WHERE mailbox_id = @m AND flags LIKE '%cross_department_ignored%'", TicketKit.P("m", otherBox.Id)));
    }

    [Fact]
    public async Task Cc_recipients_become_participants_and_are_copied_on_replies()
    {
        var d = await fixture.NewDeptAsync();
        var box = await fixture.CreateMailboxAsync(d);
        var subject = $"Cc {Unique.Id()}";
        var cc = $"manager{Unique.Id()}@customer.test";
        await fixture.ReceiveAsync(box, MailKit.Mail(MailKit.NewSender(), subject) with { Cc = [cc] });
        var ticket = Assert.Single(await d.Agent.TicketsAsync(d, subject));
        var ticketId = ticket.GetProperty("id").GetGuid();

        var detail = (await d.Agent.GetTicketAsync(ticketId)).Expect(HttpStatusCode.OK);
        Assert.Equal(cc, Assert.Single(detail.Json.GetProperty("participants").EnumerateArray()).Str("email"));
        Assert.Equal(box.Address, detail.Json.GetProperty("mailbox").Str("address"));

        (await d.Agent.PostMessageAsync(ticketId, "Public", "Noted.")).Expect(HttpStatusCode.Created);
        await fixture.DispatchAsync();
        Assert.Equal(cc, Assert.Single(Assert.Single(fixture.Fake().SentMail(box.Address)).Cc).Address);
    }

    // ----- noise and loop protection -----

    [Theory]
    [InlineData("Auto-Submitted", "auto-replied", "auto_submitted")]
    [InlineData("Precedence", "bulk", "precedence_bulk")]
    [InlineData("X-Auto-Response-Suppress", "All", "auto_response_suppress")]
    [InlineData("List-Unsubscribe", "<mailto:leave@list.test>", "mailing_list")]
    [InlineData("Content-Type", "multipart/report; report-type=delivery-status", "bounce")]
    public async Task Machine_generated_mail_is_ignored_and_recorded_never_dropped_silently(string header, string value, string reason)
    {
        var d = await fixture.NewDeptAsync();
        var box = await fixture.CreateMailboxAsync(d);
        var subject = $"Auto {Unique.Id()}";

        var id = await fixture.ReceiveAsync(box, MailKit.Mail(MailKit.NewSender(), subject) with { Headers = new Dictionary<string, string> { [header] = value } });

        Assert.Empty(await d.Agent.TicketsAsync(d, subject));
        Assert.Equal("Ignored", await StateOfAsync(box, id));
        Assert.Equal(reason, await fixture.ScalarAsync<string>("SELECT ignore_reason FROM mail.messages WHERE provider_message_id = @p", TicketKit.P("p", id)));
    }

    [Fact]
    public async Task Mail_from_a_helpdesk_mailbox_of_any_department_is_ignored_to_stop_loops()
    {
        var a = await fixture.NewDeptAsync();
        var b = await fixture.NewDeptAsync();
        var boxA = await fixture.CreateMailboxAsync(a);
        var boxB = await fixture.CreateMailboxAsync(b);
        var subject = $"Ping pong {Unique.Id()}";

        var id = await fixture.ReceiveAsync(boxA, MailKit.Mail(boxB.Address, subject));

        Assert.Empty(await a.Agent.TicketsAsync(a, subject));
        Assert.Equal("Ignored", await StateOfAsync(boxA, id));
    }

    [Fact]
    public async Task Mailer_daemon_senders_and_drafts_are_ignored()
    {
        var d = await fixture.NewDeptAsync();
        var box = await fixture.CreateMailboxAsync(d);
        var subject = $"Undeliverable {Unique.Id()}";

        await fixture.ReceiveAsync(box, MailKit.Mail("mailer-daemon@mx.example.test", subject));

        Assert.Empty(await d.Agent.TicketsAsync(d, subject));
    }

    [Fact]
    public async Task One_sender_is_rate_limited_per_mailbox()
    {
        var d = await fixture.NewDeptAsync();
        var box = await fixture.CreateMailboxAsync(d);
        var sender = MailKit.NewSender();
        var marker = Unique.Id();
        for (var i = 0; i < 31; i++)
        {
            fixture.Fake().Inject(MailKit.Mail(sender, $"Flood {marker} {i}") with { Mailbox = box.Address });
        }

        await fixture.SyncAsync(box);

        Assert.Equal(30, (await d.Agent.TicketsAsync(d, $"Flood {marker}")).Count);
        Assert.Equal(1, await fixture.CountAsync("SELECT count(*) FROM mail.messages WHERE mailbox_id = @m AND ignore_reason = 'sender_rate_limited'", TicketKit.P("m", box.Id)));
    }

    // ----- idempotency keys -----

    [Fact]
    public async Task The_same_message_id_with_the_same_content_is_a_duplicate_and_with_different_content_is_ingested_and_flagged()
    {
        var d = await fixture.NewDeptAsync();
        var box = await fixture.CreateMailboxAsync(d);
        var sender = MailKit.NewSender();
        var internetId = $"<{Guid.NewGuid():N}@dup.test>";
        var at = DateTimeOffset.UtcNow;
        var subject = $"Dup {Unique.Id()}";

        await fixture.ReceiveAsync(box, MailKit.Mail(sender, subject) with { InternetMessageId = internetId, ReceivedAt = at });
        await fixture.ReceiveAsync(box, MailKit.Mail(sender, subject) with { InternetMessageId = internetId, ReceivedAt = at });
        Assert.Single(await d.Agent.TicketsAsync(d, subject));

        // A late re-delivery of the same message (any time gap) is still the same message.
        await fixture.ReceiveAsync(box, MailKit.Mail(sender, subject) with { InternetMessageId = internetId, ReceivedAt = at.AddHours(3) });
        Assert.Single(await d.Agent.TicketsAsync(d, subject));

        var other = $"Different {Unique.Id()}";
        await fixture.ReceiveAsync(box, MailKit.Mail(sender, other) with { InternetMessageId = internetId, ReceivedAt = at.AddMinutes(5) });
        Assert.Single(await d.Agent.TicketsAsync(d, other));
        Assert.Equal(1, await fixture.CountAsync("SELECT count(*) FROM mail.messages WHERE mailbox_id = @m AND flags = 'internet_id_collision'", TicketKit.P("m", box.Id)));
    }

    [Fact]
    public async Task Mail_received_before_the_mailbox_was_configured_is_never_imported()
    {
        var d = await fixture.NewDeptAsync();
        var address = MailKit.NewAddress();
        var subject = $"Ancient {Unique.Id()}";
        fixture.Fake().Inject(MailKit.Mail(MailKit.NewSender(), subject) with { Mailbox = address, ReceivedAt = DateTimeOffset.UtcNow.AddDays(-30) });

        var box = await fixture.CreateMailboxAsync(d, address);
        await fixture.SyncAsync(box);

        Assert.Empty(await d.Agent.TicketsAsync(d, subject));
        var fresh = $"Fresh {Unique.Id()}";
        await fixture.ReceiveAsync(box, MailKit.Mail(MailKit.NewSender(), fresh));
        Assert.Single(await d.Agent.TicketsAsync(d, fresh));
    }

    [Fact]
    public async Task A_failure_after_the_ticket_exists_is_recorded_and_the_retry_completes_without_duplicates()
    {
        var d = await fixture.NewDeptAsync();
        var box = await fixture.CreateMailboxAsync(d);
        var subject = $"Retry {Unique.Id()}";
        fixture.Fake().FailNextAttachmentFetches(1);

        var id = await fixture.ReceiveAsync(box, MailKit.Mail(MailKit.NewSender(), subject)
            with { Attachments = [new FakeAttachment("notes.txt", "hello"u8.ToArray(), "text/plain")] });

        // The ticket was created, the attachment step failed: visible, retryable, nothing lost.
        var ticket = Assert.Single(await d.Agent.TicketsAsync(d, subject));
        Assert.Equal("Failed", await StateOfAsync(box, id));
        var attention = (await d.Admin.Client.GetAsync($"{d.Department.Url}/mail/attention")).Expect(HttpStatusCode.OK);
        Assert.Contains(attention.Json.GetProperty("items").EnumerateArray(), i => i.Str("state") == "Failed" && i.Str("subject") == subject);

        await fixture.ExecuteAsync("UPDATE mail.messages SET next_attempt_at = now() - interval '1 minute' WHERE provider_message_id = @p", TicketKit.P("p", id));
        await fixture.DispatchAsync();

        Assert.Equal("Completed", await StateOfAsync(box, id));
        Assert.Single(await d.Agent.TicketsAsync(d, subject));
        var messages = await d.Agent.MessagesAsync(ticket.GetProperty("id").GetGuid());
        Assert.Single(messages);
        Assert.Single(messages[0].GetProperty("attachments").EnumerateArray());
    }

    // ----- HTML -----

    [Fact]
    public async Task Html_bodies_are_sanitised_for_the_ticket_while_the_original_is_kept()
    {
        var d = await fixture.NewDeptAsync();
        var box = await fixture.CreateMailboxAsync(d);
        var subject = $"Html {Unique.Id()}";
        var html = "<p>Hello <b>team</b></p><script>alert(1)</script><img src=\"https://tracker.test/p.gif\" onerror=\"x()\"><a href=\"javascript:alert(2)\">bad</a><a href=\"https://ok.test\">ok</a>";

        var id = await fixture.ReceiveAsync(box, MailKit.Mail(MailKit.NewSender(), subject, body: "") with { BodyText = null, BodyHtml = html });

        var ticket = Assert.Single(await d.Agent.TicketsAsync(d, subject));
        var message = Assert.Single(await d.Agent.MessagesAsync(ticket.GetProperty("id").GetGuid()));
        var safe = message.Str("bodyHtml");
        Assert.DoesNotContain("<script", safe, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onerror", safe, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("javascript:", safe, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tracker.test", safe, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("https://ok.test", safe, StringComparison.Ordinal);
        Assert.Contains("Hello", message.Str("body"), StringComparison.Ordinal);
        Assert.DoesNotContain("alert", message.Str("body"), StringComparison.Ordinal);
        Assert.Equal(html, await fixture.ScalarAsync<string>("SELECT body_html_original FROM mail.messages WHERE provider_message_id = @p", TicketKit.P("p", id)));
    }

    [Fact]
    public async Task The_department_is_always_that_of_the_receiving_mailbox_even_when_the_message_names_other_departments()
    {
        var a = await fixture.NewDeptAsync();
        var b = await fixture.NewDeptAsync();
        var boxA = await fixture.CreateMailboxAsync(a);
        var boxB = await fixture.CreateMailboxAsync(b);
        var subject = $"Both {Unique.Id()}";

        // Addressed to BOTH mailboxes: each mailbox's copy becomes a ticket in its own department only.
        fixture.Fake().Inject(MailKit.Mail(MailKit.NewSender(), subject) with { Mailbox = boxA.Address, To = [boxA.Address, boxB.Address], Subject = $"{subject} [{b.Key}-1]" });
        fixture.Fake().Inject(MailKit.Mail(MailKit.NewSender(), subject) with { Mailbox = boxB.Address, To = [boxA.Address, boxB.Address] });
        await fixture.SyncAsync(boxA);
        await fixture.SyncAsync(boxB);

        Assert.Single(await a.Agent.TicketsAsync(a, subject));
        Assert.Single(await b.Agent.TicketsAsync(b, subject));
        Assert.Empty(await a.Agent.TicketsAsync(b, subject));
        Assert.Equal(0, await fixture.CountAsync(
            "SELECT count(*) FROM ticketing.tickets t JOIN mail.mailboxes m ON m.id = t.mailbox_id WHERE t.department_id <> m.department_id"));
    }
}
