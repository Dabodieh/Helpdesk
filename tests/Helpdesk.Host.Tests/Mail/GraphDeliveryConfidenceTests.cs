#pragma warning disable CA1861 // one-off request payloads
using System.Net;
using Helpdesk.Host.Tests.Infrastructure;
using Helpdesk.Host.Tests.Ticketing;
using Helpdesk.Modules.Mail.Contracts;
using Helpdesk.Modules.Mail.Providers;
using Microsoft.Extensions.DependencyInjection;

namespace Helpdesk.Host.Tests.Mail;

/// <summary>
/// Outbound confidence rules: accepted is not delivered, an uncertain send is never retried blindly, a correlated NDR marks the message failed without ever becoming
/// a ticket, and mailbox health reflects persisted state. SIMULATOR evidence only (FakeMailProvider): none of this proves Microsoft Graph behaviour (D-16).
/// </summary>
public sealed class GraphDeliveryConfidenceTests(HelpdeskFixture fixture) : IClassFixture<HelpdeskFixture>
{
    private async Task<(Dept D, Mailbox Box, Guid TicketId, string Requester, string Subject)> StartAsync()
    {
        var d = await fixture.NewDeptAsync();
        var box = await fixture.CreateMailboxAsync(d);
        var requester = MailKit.NewSender();
        var subject = $"Confidence {Unique.Id()}";
        await fixture.ReceiveAsync(box, MailKit.Mail(requester, subject));
        var ticket = Assert.Single(await d.Agent.TicketsAsync(d, subject));
        return (d, box, ticket.GetProperty("id").GetGuid(), requester, subject);
    }

    private async Task<(string State, string? Error)> OutboundAsync(Guid ticketId, string body)
    {
        var state = await fixture.ScalarAsync<string>("SELECT state FROM mail.messages WHERE ticket_id = @t AND direction = 'Outbound' AND body_text = @b", TicketKit.P("t", ticketId), TicketKit.P("b", body));
        var error = await fixture.ScalarAsync<string>("SELECT last_error FROM mail.messages WHERE ticket_id = @t AND direction = 'Outbound' AND body_text = @b", TicketKit.P("t", ticketId), TicketKit.P("b", body));
        return (state!, error);
    }

    private static async Task<JsonElementLike> TicketMessageAsync(Dept d, Guid ticketId, string body) =>
        new((await d.Agent.MessagesAsync(ticketId)).Single(m => m.Str("body") == body));

    private sealed record JsonElementLike(System.Text.Json.JsonElement E)
    {
        public string Delivery => E.Str("deliveryState");

        public string? Error => E.TryGetProperty("deliveryError", out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() : null;
    }

    [Fact]
    public async Task A_throttled_send_is_certainly_not_submitted_and_is_retried_once_then_sent_once()
    {
        var s = await StartAsync();
        fixture.Fake().ThrottleNextSends(1);

        (await s.D.Agent.PostMessageAsync(s.TicketId, "Public", "Throttled once")).Expect(HttpStatusCode.Created);
        await fixture.DispatchAsync();
        Assert.Empty(fixture.Fake().SentMail(s.Box.Address));
        Assert.Equal("Queued", (await OutboundAsync(s.TicketId, "Throttled once")).State); // safe pre-submission retry

        await fixture.ExecuteAsync("UPDATE mail.messages SET next_attempt_at = now() - interval '1 minute' WHERE ticket_id = @t AND direction = 'Outbound'", TicketKit.P("t", s.TicketId));
        await fixture.DispatchAsync();
        Assert.Single(fixture.Fake().SentMail(s.Box.Address));
        Assert.Equal("Sent", (await OutboundAsync(s.TicketId, "Throttled once")).State);
    }

    [Fact]
    public async Task An_uncertain_send_is_not_retried_surfaces_for_an_administrator_and_a_resend_needs_an_explicit_duplicate_risk_decision()
    {
        var s = await StartAsync();
        fixture.Fake().TimeoutNextSends(1, reachedProvider: false);

        (await s.D.Agent.PostMessageAsync(s.TicketId, "Public", "Lost in transit")).Expect(HttpStatusCode.Created);
        await fixture.DispatchAsync();

        // Neither Sent nor a plain failure: uncertain, reported to the ticket with a safe category, and never queued again by itself.
        Assert.Equal(("Uncertain", "outcome_uncertain"), await OutboundAsync(s.TicketId, "Lost in transit"));
        var shown = await TicketMessageAsync(s.D, s.TicketId, "Lost in transit");
        Assert.Equal("Failed", shown.Delivery);
        Assert.Equal("outcome_uncertain", shown.Error);
        await fixture.ExecuteAsync("UPDATE mail.messages SET next_attempt_at = now() - interval '1 minute' WHERE ticket_id = @t AND direction = 'Outbound'", TicketKit.P("t", s.TicketId));
        await fixture.DispatchAsync();
        await fixture.DispatchAsync();
        Assert.Empty(fixture.Fake().SentMail(s.Box.Address));
        Assert.Equal("Uncertain", (await OutboundAsync(s.TicketId, "Lost in transit")).State);

        // The ticket-level retry (a normal "Retry sending") is not offered for it, and the administrator sees it with its category.
        var messageId = (await s.D.Agent.MessagesAsync(s.TicketId)).Single(m => m.Str("body") == "Lost in transit").GetProperty("id").GetGuid();
        Assert.NotEqual(HttpStatusCode.OK, (await s.D.Agent.Client.PostAsync($"/api/tickets/{s.TicketId}/messages/{messageId}/retry-delivery")).Status);
        var item = (await s.D.Admin.Client.GetAsync($"{s.D.Department.Url}/mail/attention")).Expect(HttpStatusCode.OK).Json.GetProperty("items").EnumerateArray()
            .Single(i => i.Str("state") == "Uncertain");
        Assert.Equal("outcome_uncertain", item.Str("category"));
        var mailId = item.GetProperty("id").GetGuid();

        // Resolving needs mailbox administration; first the provider is asked, then the duplicate risk must be accepted explicitly.
        Assert.Equal(HttpStatusCode.Forbidden, (await s.D.Agent.Client.PostAsync($"{s.D.Department.Url}/mail/{mailId}/retry")).Status);
        (await s.D.Admin.Client.PostAsync($"{s.D.Department.Url}/mail/{mailId}/retry")).ExpectCode(HttpStatusCode.Conflict, "outcome_uncertain");
        Assert.Equal("requeued", (await s.D.Admin.Client.PostAsync($"{s.D.Department.Url}/mail/{mailId}/retry", new { acceptDuplicateRisk = true })).Expect(HttpStatusCode.OK).Json.Str("outcome"));
        await fixture.DispatchAsync();
        Assert.Single(fixture.Fake().SentMail(s.Box.Address));
        Assert.Equal("Sent", (await OutboundAsync(s.TicketId, "Lost in transit")).State);
        Assert.Equal(1, await fixture.CountAuditAsync("mail.uncertain_retried", mailId));
    }

    [Fact]
    public async Task A_timeout_after_the_provider_accepted_the_message_reconciles_to_sent_without_a_second_send()
    {
        var s = await StartAsync();
        fixture.Fake().TimeoutNextSends(1, reachedProvider: true);

        (await s.D.Agent.PostMessageAsync(s.TicketId, "Public", "Accepted but the reply was lost")).Expect(HttpStatusCode.Created);
        await fixture.DispatchAsync();
        await fixture.DispatchAsync();

        Assert.Single(fixture.Fake().SentMail(s.Box.Address));
        Assert.Equal("Sent", (await OutboundAsync(s.TicketId, "Accepted but the reply was lost")).State);
    }

    [Fact]
    public async Task A_correlated_delivery_report_fails_that_message_and_is_never_a_ticket_a_reply_or_an_acknowledgement()
    {
        var s = await StartAsync();
        (await s.D.Agent.PostMessageAsync(s.TicketId, "Public", "Will bounce")).Expect(HttpStatusCode.Created);
        await fixture.DispatchAsync();
        var sent = Assert.Single(fixture.Fake().SentMail(s.Box.Address));
        Assert.Equal("Sent", (await TicketMessageAsync(s.D, s.TicketId, "Will bounce")).Delivery); // accepted, and labelled as sent, never as delivered
        var ticketsBefore = (await s.D.Agent.TicketsAsync(s.D, "Confidence")).Count;
        var messagesBefore = (await s.D.Agent.MessagesAsync(s.TicketId)).Count;
        var sentBefore = fixture.Fake().SentMail(s.Box.Address).Count;

        // The postmaster's report quotes our footer token (evidence by token).
        var report = new FakeInbound(
            s.Box.Address, $"postmaster@{MailKit.NewSender().Split('@')[1]}", "Undeliverable: " + sent.Subject, "Postmaster", $"Your message could not be delivered.\n\n[hd:{sent.Token}]",
            Headers: new Dictionary<string, string> { ["Content-Type"] = "multipart/report; report-type=delivery-status", ["Auto-Submitted"] = "auto-replied" });
        await fixture.ReceiveAsync(s.Box, report);
        await fixture.DispatchAsync();

        Assert.Equal(("Failed", "ndr_delivery_failed"), await OutboundAsync(s.TicketId, "Will bounce"));
        var shown = await TicketMessageAsync(s.D, s.TicketId, "Will bounce");
        Assert.Equal("Failed", shown.Delivery);
        Assert.Equal("delivery_failed", shown.Error);
        Assert.Equal(ticketsBefore, (await s.D.Agent.TicketsAsync(s.D, "Confidence")).Count);
        Assert.Equal(messagesBefore, (await s.D.Agent.MessagesAsync(s.TicketId)).Count); // not a requester message
        Assert.Equal(sentBefore, fixture.Fake().SentMail(s.Box.Address).Count); // no acknowledgement or other mail was triggered
        Assert.Equal(1, await fixture.CountAsync("SELECT count(*) FROM mail.messages WHERE mailbox_id = @m AND direction = 'Inbound' AND state = 'Ignored' AND ignore_reason IS NOT NULL AND flags LIKE '%ndr_correlated%'", TicketKit.P("m", s.Box.Id)));
    }

    private Task<long> TicketSequenceAsync(Guid ticketMessageId) =>
        fixture.ScalarAsync<long>("SELECT delivery_report_seq FROM ticketing.messages WHERE id = @m", TicketKit.P("m", ticketMessageId));

    private Task ReportAsync(Guid departmentId, Guid ticketMessageId, string state, string? code, long sequence) =>
        fixture.WithScopeAsync(sp => sp.GetRequiredService<IDeliveryStateSink>().SetAsync(departmentId, ticketMessageId, state, code, sequence));

    [Fact]
    public async Task A_delivery_report_is_applied_only_when_its_sequence_is_higher_than_the_highest_accepted()
    {
        var s = await StartAsync();
        (await s.D.Agent.PostMessageAsync(s.TicketId, "Public", "Ordered reports")).Expect(HttpStatusCode.Created);
        await fixture.DispatchAsync();
        var messageId = (await s.D.Agent.MessagesAsync(s.TicketId)).Single(m => m.Str("body") == "Ordered reports").GetProperty("id").GetGuid();
        var departmentId = s.D.Department.Id;
        var seq = await TicketSequenceAsync(messageId);
        Assert.True(seq > 0);
        Assert.Equal("Sent", (await TicketMessageAsync(s.D, s.TicketId, "Ordered reports")).Delivery);

        // Newer: accepted, state and sequence move.
        await ReportAsync(departmentId, messageId, MailDeliveryStates.Failed, "delivery_failed", seq + 1);
        Assert.Equal("Failed", (await TicketMessageAsync(s.D, s.TicketId, "Ordered reports")).Delivery);
        Assert.Equal(seq + 1, await TicketSequenceAsync(messageId));
        var audits = await fixture.CountAuditAsync("ticket.message_delivery_changed", s.TicketId);

        // Older (arriving afterwards): ignored, nothing regresses.
        await ReportAsync(departmentId, messageId, MailDeliveryStates.Sent, null, seq);
        Assert.Equal("Failed", (await TicketMessageAsync(s.D, s.TicketId, "Ordered reports")).Delivery);
        Assert.Equal(seq + 1, await TicketSequenceAsync(messageId));

        // Equal (a repeat, even carrying a different state): idempotent, no regression and no repeated work.
        await ReportAsync(departmentId, messageId, MailDeliveryStates.Failed, "delivery_failed", seq + 1);
        await ReportAsync(departmentId, messageId, MailDeliveryStates.Sent, null, seq + 1);
        Assert.Equal("Failed", (await TicketMessageAsync(s.D, s.TicketId, "Ordered reports")).Delivery);
        Assert.Equal(audits, await fixture.CountAuditAsync("ticket.message_delivery_changed", s.TicketId));

        // A still newer report is accepted again.
        await ReportAsync(departmentId, messageId, MailDeliveryStates.Sent, null, seq + 2);
        Assert.Equal("Sent", (await TicketMessageAsync(s.D, s.TicketId, "Ordered reports")).Delivery);
        Assert.Equal(seq + 2, await TicketSequenceAsync(messageId));
    }

    private async Task<Guid> BounceAsync(Dept d, Mailbox box, Guid ticketId, string body)
    {
        (await d.Agent.PostMessageAsync(ticketId, "Public", body)).Expect(HttpStatusCode.Created);
        await fixture.DispatchAsync();
        var sent = Assert.Single(fixture.Fake().SentMail(box.Address));
        var report = new FakeInbound(
            box.Address, $"postmaster@{MailKit.NewSender().Split('@')[1]}", "Undeliverable: " + sent.Subject, "Postmaster", $"Your message could not be delivered.\n\n[hd:{sent.Token}]",
            Headers: new Dictionary<string, string> { ["Content-Type"] = "multipart/report; report-type=delivery-status", ["Auto-Submitted"] = "auto-replied" });
        await fixture.ReceiveAsync(box, report);
        await fixture.DispatchAsync();
        Assert.Equal(("Failed", "ndr_delivery_failed"), await OutboundAsync(ticketId, body));
        return (await d.Agent.MessagesAsync(ticketId)).Single(m => m.Str("body") == body).GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task Retrying_a_reply_after_a_confirmed_bounce_is_a_genuine_second_send_and_is_audited_as_a_resend()
    {
        var s = await StartAsync();
        var messageId = await BounceAsync(s.D, s.Box, s.TicketId, "Retry after bounce");
        var mailId = await fixture.ScalarAsync<Guid>("SELECT id FROM mail.messages WHERE ticket_id = @t AND direction = 'Outbound'", TicketKit.P("t", s.TicketId));
        var bounceSeq = await TicketSequenceAsync(messageId);
        Assert.Equal(bounceSeq, await fixture.ScalarAsync<long>("SELECT delivery_seq FROM mail.messages WHERE id = @m", TicketKit.P("m", mailId)));

        var retried = (await s.D.Agent.Client.PostAsync($"/api/tickets/{s.TicketId}/messages/{messageId}/retry-delivery")).Expect(HttpStatusCode.OK);
        Assert.Equal("Pending", retried.Json.Str("deliveryState"));
        await fixture.DispatchAsync();

        Assert.Equal(2, fixture.Fake().SentMail(s.Box.Address).Count); // the bounced copy in Sent Items was not mistaken for the retry
        Assert.Equal("Sent", (await OutboundAsync(s.TicketId, "Retry after bounce")).State);
        Assert.Equal("Sent", (await TicketMessageAsync(s.D, s.TicketId, "Retry after bounce")).Delivery);
        Assert.Equal(1, await fixture.CountAuditAsync("mail.resent_after_bounce", mailId));

        // The newer Sent report was accepted after the bounce report: the sequence moved forward and both sides agree.
        var sentSeq = await TicketSequenceAsync(messageId);
        Assert.True(sentSeq > bounceSeq);
        Assert.Equal(sentSeq, await fixture.ScalarAsync<long>("SELECT delivery_seq FROM mail.messages WHERE id = @m", TicketKit.P("m", mailId)));
    }

    [Fact]
    public async Task The_administrator_retry_of_a_bounced_reply_is_also_a_genuine_second_send_and_is_audited_as_a_resend()
    {
        var s = await StartAsync();
        await BounceAsync(s.D, s.Box, s.TicketId, "Admin retry after bounce");
        var mailId = (await s.D.Admin.Client.GetAsync($"{s.D.Department.Url}/mail/attention")).Expect(HttpStatusCode.OK).Json.GetProperty("items").EnumerateArray()
            .Single(i => i.Str("state") == "Failed" && i.Str("direction") == "Outbound").GetProperty("id").GetGuid();

        (await s.D.Admin.Client.PostAsync($"{s.D.Department.Url}/mail/{mailId}/retry")).Expect(HttpStatusCode.NoContent);
        await fixture.DispatchAsync();

        Assert.Equal(2, fixture.Fake().SentMail(s.Box.Address).Count);
        Assert.Equal("Sent", (await OutboundAsync(s.TicketId, "Admin retry after bounce")).State);
        Assert.Equal(1, await fixture.CountAuditAsync("mail.resent_after_bounce", mailId));
    }

    [Fact]
    public async Task A_report_matched_by_message_id_works_and_an_uncorrelated_report_is_only_suppressed()
    {
        var s = await StartAsync();
        (await s.D.Agent.PostMessageAsync(s.TicketId, "Public", "Bounce by id")).Expect(HttpStatusCode.Created);
        await fixture.DispatchAsync();
        var sent = Assert.Single(fixture.Fake().SentMail(s.Box.Address));
        var domain = MailKit.NewSender().Split('@')[1];

        // Wrong token and a subject that names the ticket: subject alone is never evidence.
        var stray = new FakeInbound(
            s.Box.Address, $"mailer-daemon@{domain}", "Undeliverable: " + sent.Subject, null, "Could not deliver [hd:" + new string('a', 32) + "]",
            Headers: new Dictionary<string, string> { ["Content-Type"] = "multipart/report; report-type=delivery-status" });
        await fixture.ReceiveAsync(s.Box, stray);
        Assert.Equal("Sent", (await OutboundAsync(s.TicketId, "Bounce by id")).State);
        Assert.Single(await s.D.Agent.TicketsAsync(s.D, s.Subject));

        var byId = new FakeInbound(
            s.Box.Address, $"mailer-daemon@{domain}", "Delivery Status Notification (Failure)", null, "The address was not found.", InReplyTo: sent.InternetMessageId,
            References: [sent.InternetMessageId], Headers: new Dictionary<string, string> { ["Content-Type"] = "multipart/report; report-type=delivery-status" });
        await fixture.ReceiveAsync(s.Box, byId);
        Assert.Equal(("Failed", "ndr_delivery_failed"), await OutboundAsync(s.TicketId, "Bounce by id"));
        Assert.Single(await s.D.Agent.TicketsAsync(s.D, s.Subject));
    }

    [Fact]
    public async Task Mailbox_health_comes_from_persisted_state_and_stays_inside_the_department()
    {
        var s = await StartAsync();
        var other = await fixture.NewDeptAsync();

        async Task<(string Status, string[] Reasons)> ReadAsync()
        {
            var json = (await s.D.Admin.Client.GetAsync($"{s.D.Department.Url}/mailboxes/{s.Box.Id}")).Expect(HttpStatusCode.OK).Json.GetProperty("health");
            return (json.Str("status"), [.. json.GetProperty("reasons").EnumerateArray().Select(r => r.GetString()!)]);
        }

        Assert.Equal("Healthy", (await ReadAsync()).Status); // created, subscribed and synced

        await fixture.ExecuteAsync("UPDATE mail.mailbox_sync SET consecutive_failures = 1 WHERE mailbox_id = @m", TicketKit.P("m", s.Box.Id));
        var one = await ReadAsync();
        Assert.Equal("Attention:sync_failing", $"{one.Status}:{string.Join(",", one.Reasons)}");
        await fixture.ExecuteAsync("UPDATE mail.mailbox_sync SET consecutive_failures = 3 WHERE mailbox_id = @m", TicketKit.P("m", s.Box.Id));
        Assert.Equal("Failed", (await ReadAsync()).Status);
        await fixture.ExecuteAsync("UPDATE mail.mailbox_sync SET consecutive_failures = 0, subscription_expires_at = now() - interval '1 hour' WHERE mailbox_id = @m", TicketKit.P("m", s.Box.Id));
        Assert.Contains("subscription_expired", (await ReadAsync()).Reasons);
        await fixture.ExecuteAsync("UPDATE mail.mailbox_sync SET subscription_expires_at = now() + interval '2 days', last_sync_at = now() - interval '2 hours' WHERE mailbox_id = @m", TicketKit.P("m", s.Box.Id));
        var stale = await ReadAsync(); // no traffic is fine; no sync run is not
        Assert.Equal("Attention:sync_stale", $"{stale.Status}:{string.Join(",", stale.Reasons)}");
        await fixture.SyncAsync(s.Box);
        Assert.Equal("Healthy", (await ReadAsync()).Status); // a successful sync restores it

        fixture.Fake().TimeoutNextSends(1, reachedProvider: false);
        (await s.D.Agent.PostMessageAsync(s.TicketId, "Public", "Health probe")).Expect(HttpStatusCode.Created);
        await fixture.DispatchAsync();
        Assert.Contains("outbound_uncertain", (await ReadAsync()).Reasons);

        // Operational data of this department only; and only for people who manage mailboxes.
        (await other.Admin.Client.GetAsync($"{s.D.Department.Url}/mailboxes/{s.Box.Id}")).Expect(HttpStatusCode.NotFound);
        (await other.Admin.Client.GetAsync($"{other.Department.Url}/mailboxes/{s.Box.Id}")).Expect(HttpStatusCode.NotFound);
        Assert.DoesNotContain(s.Box.Address, (await other.Admin.Client.GetAsync($"{other.Department.Url}/mailboxes")).Expect(HttpStatusCode.OK).Body, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.D.Agent.Client.GetAsync($"{s.D.Department.Url}/mailboxes")).Status);
        var attentionOther = (await other.Admin.Client.GetAsync($"{other.Department.Url}/mail/attention")).Expect(HttpStatusCode.OK).Body;
        Assert.DoesNotContain("Uncertain", attentionOther, StringComparison.Ordinal);
    }
}
