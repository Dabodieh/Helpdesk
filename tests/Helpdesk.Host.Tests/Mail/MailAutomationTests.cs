#pragma warning disable CA1861 // one-off request payloads
using System.Net;
using Helpdesk.Host.Tests.Infrastructure;
using Helpdesk.Host.Tests.Ticketing;
using Helpdesk.Modules.Mail.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Helpdesk.Host.Tests.Mail;

/// <summary>The bridge between Phase 3 (inbound email) and Phase 5 (automation): mail creates one ticket, the rule runs once, replays change nothing.</summary>
public sealed class MailAutomationTests(HelpdeskFixture fixture) : IClassFixture<HelpdeskFixture>
{
    [Fact]
    public async Task Inbound_email_creates_one_ticket_runs_the_routing_rule_once_and_replays_change_nothing()
    {
        var d = await fixture.NewDeptAsync();
        var other = await fixture.NewDeptAsync();
        var box = await fixture.CreateMailboxAsync(d);
        var otherBox = await fixture.CreateMailboxAsync(other);
        var team = await fixture.CreateTeamAsync(d.Department);
        (await d.Admin.Client.PostAsync($"{d.Department.Url}/automations", new
        {
            name = "Mail routing",
            trigger = "TicketCreated",
            conditions = new object[]
            {
                new { field = "mailbox", @operator = "in", values = new[] { box.Id.ToString() } },
                new { field = "subjectContains", @operator = "contains", values = new[] { "network" } },
            },
            actions = new object[]
            {
                new { type = "assignTeam", value = team.Id.ToString() },
                new { type = "setPriority", value = "High" },
                new { type = "addInternalNote", text = "Routed from the IT mailbox" },
            },
        })).Expect(HttpStatusCode.Created);

        var sender = MailKit.NewSender();
        var subject = $"Network down {Unique.Id()}";
        var inboundId = await fixture.ReceiveAsync(box, MailKit.Mail(sender, subject, "Nothing connects.", "Pat Sender"));

        var ticket = Assert.Single(await d.Agent.TicketsAsync(d, subject));
        var ticketId = ticket.GetProperty("id").GetGuid();
        Assert.Equal("High", ticket.Str("priority"));
        Assert.Equal(team.Id, ticket.GetProperty("team").GetProperty("id").GetGuid());

        // The real queue query finds it by the routed team and priority, and only in the receiving department.
        var routed = (await d.Agent.Client.GetAsync($"/api/tickets?queue=all&teamId={team.Id}&priority=High&limit=100")).Expect(HttpStatusCode.OK).Json.GetProperty("items");
        Assert.Contains(routed.EnumerateArray(), i => i.GetProperty("id").GetGuid() == ticketId);
        Assert.Empty(await other.Agent.TicketsAsync(other, subject));

        long Executions() => fixture.CountAsync("SELECT count(*) FROM ticketing.automation_executions WHERE ticket_id = @t", TicketKit.P("t", ticketId)).GetAwaiter().GetResult();
        long Notes() => fixture.CountAsync("SELECT count(*) FROM ticketing.messages WHERE ticket_id = @t AND author_type = 'System'", TicketKit.P("t", ticketId)).GetAwaiter().GetResult();
        Assert.Equal(1, Executions());
        Assert.Equal(1, Notes());

        // Replays: the same notification processed again and a delta sync of both mailboxes.
        await fixture.WithScopeAsync(async sp =>
        {
            var processor = sp.GetRequiredService<InboundProcessor>();
            await processor.ProcessAsync(box.Id, inboundId, CancellationToken.None);
            await processor.ProcessAsync(box.Id, inboundId, CancellationToken.None);
        });
        await fixture.SyncAsync(box);
        await fixture.SyncAsync(otherBox);

        Assert.Single(await d.Agent.TicketsAsync(d, subject));
        Assert.Equal(1, await fixture.CountAsync("SELECT count(*) FROM ticketing.tickets WHERE department_id = @d AND subject = @s", TicketKit.P("d", d.Id), TicketKit.P("s", subject)));
        Assert.Equal(1, Executions());
        Assert.Equal(1, Notes());
        Assert.Equal(1, await fixture.CountAsync("SELECT count(*) FROM audit.audit_events WHERE object_id = @t AND action = 'ticket.team_changed'", TicketKit.P("t", ticketId)));
        Assert.Equal(1, await fixture.CountAsync("SELECT count(*) FROM audit.audit_events WHERE object_id = @t AND action = 'ticket.priority_changed'", TicketKit.P("t", ticketId)));
    }
}
