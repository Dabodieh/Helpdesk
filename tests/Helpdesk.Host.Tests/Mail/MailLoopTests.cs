using System.Net;
using Helpdesk.Host.Tests.Infrastructure;
using Helpdesk.Host.Tests.Ticketing;
using Helpdesk.Modules.Mail.Providers;
using Microsoft.Extensions.DependencyInjection;

namespace Helpdesk.Host.Tests.Mail;

/// <summary>
/// The MVP acceptance scenario (docs/product/MVP.md) end to end through the real pipeline with the mailbox simulator behind the
/// provider seam: mail arrives, a ticket appears in the right department only (idempotently), an agent replies from that
/// department's mailbox, the requester answers and is threaded onto the same ticket via standard headers, everything is audited.
/// </summary>
public sealed class MailLoopTests(HelpdeskFixture fixture) : IClassFixture<HelpdeskFixture>
{
    [Fact]
    public async Task Mvp_acceptance_scenario_runs_end_to_end_with_the_fake_provider()
    {
        // 1-3: a department with members and a configured mailbox; another department with its own mailbox.
        var it = await fixture.NewDeptAsync();
        var hr = await fixture.NewDeptAsync();
        var itBox = await fixture.CreateMailboxAsync(it, signature: "Regards,\nIT Service Desk");
        var hrBox = await fixture.CreateMailboxAsync(hr);
        var requester = MailKit.NewSender();
        var subject = $"Printer jam {Unique.Id()}";

        // 4: an email arrives; the department comes from the receiving mailbox.
        var inboundId = await fixture.ReceiveAsync(itBox, MailKit.Mail(requester, subject, "The printer on floor 3 keeps jamming.", "Priya Shah"));
        var tickets = await it.Agent.TicketsAsync(it, subject);
        var ticket = Assert.Single(tickets);
        var ticketId = ticket.GetProperty("id").GetGuid();
        Assert.Equal(requester, ticket.GetProperty("requester").Str("email"));
        Assert.Equal("Open", ticket.Str("status"));
        Assert.StartsWith(it.Key + "-", ticket.Str("reference"));

        // Idempotent: the same message again (duplicate notification / delta replay) creates nothing new.
        await fixture.WithScopeAsync(async sp =>
        {
            var processor = sp.GetRequiredService<Modules.Mail.Services.InboundProcessor>();
            await processor.ProcessAsync(itBox.Id, inboundId, CancellationToken.None);
            await processor.ProcessAsync(itBox.Id, inboundId, CancellationToken.None);
        });
        await fixture.SyncAsync(itBox);
        Assert.Single(await it.Agent.TicketsAsync(it, subject));
        Assert.Single(await it.Agent.MessagesAsync(ticketId));

        // 5: an agent of another department cannot see it (404, absent from lists); the department's own agent can.
        (await hr.Agent.GetTicketAsync(ticketId)).Expect(HttpStatusCode.NotFound);
        Assert.Empty(await hr.Agent.TicketsAsync(hr, subject));
        (await it.Agent.GetTicketAsync(ticketId)).Expect(HttpStatusCode.OK);

        // 6: the agent's public reply leaves from THAT department's mailbox, without any department footer (only an agent's own signature is ever appended), and is reported as sent.
        var reply = (await it.Agent.PostMessageAsync(ticketId, "Public", "We have sent an engineer.")).Expect(HttpStatusCode.Created);
        Assert.Equal("Pending", reply.Json.GetProperty("message").Str("deliveryState"));
        await fixture.DispatchAsync();
        var sent = Assert.Single(fixture.Fake().SentMail(itBox.Address));
        Assert.Empty(fixture.Fake().SentMail(hrBox.Address));
        Assert.Equal(requester, Assert.Single(sent.To).Address);
        Assert.Contains("We have sent an engineer.", sent.BodyHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("IT Service Desk", sent.BodyHtml, StringComparison.Ordinal); // there is no department footer feature
        Assert.Contains($"[hd:{sent.Token}]", sent.BodyHtml, StringComparison.Ordinal);
        Assert.NotNull(sent.InReplyTo); // a reply to the inbound mail (Exchange threading assumed, runbook E-23)
        var messages = await it.Agent.MessagesAsync(ticketId);
        var agentMessage = messages.Single(m => m.Str("authorType") == "Agent");
        Assert.Equal("Sent", agentMessage.Str("deliveryState"));

        // 7: the requester answers the mail we sent: threaded onto the same ticket by standard headers, not by subject.
        await fixture.ReceiveAsync(itBox, FakeMailProvider.CustomerReplyTo(sent, requester, "Thanks, that fixed it."));
        Assert.Single(await it.Agent.TicketsAsync(it, subject));
        Assert.Equal(3, (await it.Agent.MessagesAsync(ticketId)).Count);

        // 8: all state changes are in the audit log (system actor for ingestion, delivery change recorded).
        var audit = await fixture.AuditOfAsync(ticketId);
        Assert.Contains(audit, a => a is { Action: "ticket.created", ActorUserId: null, Source: "system" });
        Assert.Contains(audit, a => a is { Action: "ticket.message_delivery_changed" } && a.Next!.Contains("Sent", StringComparison.Ordinal));
        Assert.Equal(3, audit.Count(a => a.Action == "ticket.message_added"));
        Assert.True(await fixture.CountAsync("SELECT count(*) FROM audit.audit_events WHERE category = 'mail' AND department_id = @d", TicketKit.P("d", it.Id)) >= 3);
    }

    [Fact]
    public async Task A_late_requester_reply_on_a_closed_ticket_reopens_the_same_ticket()
    {
        var d = await fixture.NewDeptAsync();
        var box = await fixture.CreateMailboxAsync(d);
        var requester = MailKit.NewSender();
        var subject = $"Late reply {Unique.Id()}";
        await fixture.ReceiveAsync(box, MailKit.Mail(requester, subject));
        var ticket = Assert.Single(await d.Agent.TicketsAsync(d, subject));
        var ticketId = ticket.GetProperty("id").GetGuid();
        (await d.Agent.PostMessageAsync(ticketId, "Public", "Done?")).Expect(HttpStatusCode.Created);
        await fixture.DispatchAsync();
        var sent = Assert.Single(fixture.Fake().SentMail(box.Address));
        var current = (await d.Agent.GetTicketAsync(ticketId)).Expect(HttpStatusCode.OK);
        (await d.Agent.PatchTicketAsync(current, new { Status = "Closed" })).Expect(HttpStatusCode.OK);

        await fixture.ReceiveAsync(box, FakeMailProvider.CustomerReplyTo(sent, requester, "Actually it is still broken."));

        var reopened = (await d.Agent.GetTicketAsync(ticketId)).Expect(HttpStatusCode.OK);
        Assert.Equal("Open", reopened.Json.GetProperty("status").GetString());
        Assert.Single(await d.Agent.TicketsAsync(d, subject)); // the same ticket, no follow-up ticket
        var audit = await fixture.AuditOfAsync(ticketId);
        Assert.Contains(audit, a => a is { Action: "ticket.status_changed", ActorUserId: null } && a.Next!.Contains("requester_reply", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_unconfigured_department_records_the_reply_but_does_not_pretend_to_send_it()
    {
        var d = await fixture.NewDeptAsync();
        var created = (await d.Agent.Client.PostAsync(
            $"{d.Department.Url}/tickets", new { subject = "Web ticket", description = "Created by an agent", requester = new { email = MailKit.NewSender(), displayName = "Web Person" } }))
            .Expect(HttpStatusCode.Created);

        var reply = (await d.Agent.PostMessageAsync(created.Id, "Public", "Hello")).Expect(HttpStatusCode.Created);

        Assert.Equal("NotDelivered", reply.Json.GetProperty("message").Str("deliveryState"));
    }

    [Fact]
    public async Task A_web_created_ticket_replies_from_the_department_default_mailbox()
    {
        var d = await fixture.NewDeptAsync();
        var first = await fixture.CreateMailboxAsync(d);
        var second = await fixture.CreateMailboxAsync(d);
        var created = (await d.Agent.Client.PostAsync(
            $"{d.Department.Url}/tickets", new { subject = "Phone call", description = "Rang the desk", requester = new { email = MailKit.NewSender(), displayName = "Caller" } }))
            .Expect(HttpStatusCode.Created);

        (await d.Agent.PostMessageAsync(created.Id, "Public", "Calling back with details.")).Expect(HttpStatusCode.Created);
        await fixture.DispatchAsync();

        // The first mailbox of a department became its default; there is no reply target, so it goes out as a new message.
        var sent = Assert.Single(fixture.Fake().SentMail(first.Address));
        Assert.Null(sent.InReplyTo);
        Assert.Contains(created.Json.Str("reference"), sent.Subject, StringComparison.Ordinal);
        Assert.Empty(fixture.Fake().SentMail(second.Address));
    }
}
