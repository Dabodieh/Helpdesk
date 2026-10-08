using System.Net;
using Helpdesk.Host.Tests.Infrastructure;
using Helpdesk.Host.Tests.Mail;
using Helpdesk.Host.Tests.Portal;

namespace Helpdesk.Host.Tests.Ticketing;

/// <summary>One set of definitions for the headline figures (WS-14, D25 to D27) and the content-free lifecycle record (D7b).</summary>
public sealed class ReportingFoundationTests(HelpdeskFixture fixture) : IClassFixture<HelpdeskFixture>
{
    private static async Task<int> BadgeAsync(Dept d, string name) =>
        (await d.Agent.Client.GetAsync("/api/tickets/queue-counts")).Expect(HttpStatusCode.OK).Json.GetProperty(name).GetInt32();

    private static async Task<int> ListCountAsync(Dept d, string query) =>
        (await d.Agent.Client.GetAsync($"/api/tickets?departmentId={d.Department.Id}&limit=100&{query}")).Expect(HttpStatusCode.OK).Json.GetProperty("items").GetArrayLength();

    [Fact]
    public async Task Each_sidebar_count_equals_the_list_it_opens_and_counts_open_plus_pending_without_resolved_tickets()
    {
        var d = await fixture.NewDeptAsync();
        await d.Agent.CreateTicketAsync(d.Department);
        var pending = await d.Agent.CreateTicketAsync(d.Department);
        var resolved = await d.Agent.CreateTicketAsync(d.Department);
        (await d.Agent.PatchTicketAsync(pending, new { Status = "Pending" })).Expect(HttpStatusCode.OK);
        (await d.Agent.PatchTicketAsync(resolved, new { Status = "Resolved" })).Expect(HttpStatusCode.OK);

        Assert.Equal(2, await BadgeAsync(d, "all"));
        Assert.Equal(2, await BadgeAsync(d, "unassigned"));
        Assert.Equal(await ListCountAsync(d, "queue=all&status=Open&status=Pending"), await BadgeAsync(d, "all"));
    }

    [Fact]
    public async Task Awaiting_first_response_means_no_human_public_reply_yet_whatever_the_sla_setup()
    {
        var d = await fixture.NewDeptAsync(); // no SLA policy at all
        await d.Agent.CreateTicketAsync(d.Department);
        var answered = await d.Agent.CreateTicketAsync(d.Department);
        (await d.Agent.PostMessageAsync(answered.Id, "Internal", "a note is not an answer")).Expect(HttpStatusCode.Created);
        Assert.Equal(2, await ListCountAsync(d, "sla=awaitingresponse"));

        (await d.Agent.PostMessageAsync(answered.Id, "Public", "Hello")).Expect(HttpStatusCode.Created);
        Assert.Equal(1, await ListCountAsync(d, "sla=awaitingresponse"));
        var dashboard = (await d.Lead.Client.GetAsync($"/api/departments/{d.Department.Id}/dashboard")).Expect(HttpStatusCode.OK).Json;
        Assert.Equal(1, dashboard.GetProperty("backlog").GetProperty("awaitingFirstResponse").GetInt32());
        Assert.Equal(0, await ListCountAsync(d, "sla=slafirstresponse")); // the SLA-specific view stays separate: no policy, nothing pending
    }

    [Fact]
    public async Task A_merged_duplicate_was_created_but_is_not_current_work()
    {
        var d = await fixture.NewDeptAsync();
        var email = $"dana-{Unique.Id()}@example.test";
        var main = (await d.TicketForAsync(email, "Main")).Expect(HttpStatusCode.Created);
        var duplicate = (await d.TicketForAsync(email, "Duplicate")).Expect(HttpStatusCode.Created);
        var mainDetail = (await d.Agent.GetTicketAsync(main.Id)).Expect(HttpStatusCode.OK);
        var duplicateDetail = (await d.Agent.GetTicketAsync(duplicate.Id)).Expect(HttpStatusCode.OK);
        (await d.Agent.Client.PostAsync(TicketKit.Url(main.Id, "/merge"), new { version = mainDetail.Version, sources = new[] { new { ticketId = duplicate.Id, version = duplicateDetail.Version } } })).Expect(HttpStatusCode.OK);

        Assert.Equal(2, await fixture.CountAsync("SELECT count(*) FROM ticketing.ticket_lifecycle_events WHERE department_id = @d AND kind = 'created'", TicketKit.P("d", d.Id))); // both were raised
        Assert.Equal(1, await BadgeAsync(d, "all")); // only the canonical ticket is current work
        Assert.Equal(1, await fixture.CountAsync("SELECT count(*) FROM ticketing.ticket_lifecycle_events WHERE department_id = @d AND kind = 'merged'", TicketKit.P("d", d.Id))); // merged volume is its own figure
    }

    [Fact]
    public async Task The_lifecycle_record_holds_content_free_facts_and_the_three_creation_events_share_one_shape()
    {
        var d = await fixture.NewDeptAsync();
        var box = await fixture.CreateMailboxAsync(d);
        var secret = $"SECRETSUBJECT{Unique.Id()}";
        var agent = await d.Agent.CreateTicketAsync(d.Department, TicketHelpers.NewTicketBody(secret));
        (await d.Agent.PatchTicketAsync(agent, new { Priority = "High" })).Expect(HttpStatusCode.OK);
        var subject = $"Emailed {Unique.Id()}";
        await fixture.ReceiveAsync(box, MailKit.Mail(MailKit.NewSender(), subject));
        var emailed = Assert.Single(await d.Agent.TicketsAsync(d, subject)).GetProperty("id").GetGuid();
        var alice = await fixture.EmployeeAsync("alice");
        var (formId, _) = await d.FormAsync(fixture, "General");
        var portal = (await alice.User.Client.PostAsync("/api/portal/requests", new { formId, subject = "Portal", description = "x", submissionId = Guid.NewGuid() })).Expect(HttpStatusCode.Created);
        var portalId = await fixture.ScalarAsync<Guid>("SELECT id FROM ticketing.tickets WHERE reference = @r", TicketKit.P("r", portal.Json.GetProperty("reference").GetString()!));

        Assert.Equal(1, await fixture.CountAsync("SELECT count(*) FROM ticketing.ticket_lifecycle_events WHERE ticket_id = @t AND kind = 'created'", TicketKit.P("t", agent.Id)));
        Assert.Equal("High", await fixture.ScalarAsync<string>("SELECT to_value FROM ticketing.ticket_lifecycle_events WHERE ticket_id = @t AND kind = 'priority'", TicketKit.P("t", agent.Id)));
        Assert.Equal(0, await fixture.CountAsync("SELECT count(*) FROM ticketing.ticket_lifecycle_events WHERE to_value ILIKE @s OR from_value ILIKE @s", TicketKit.P("s", "%" + secret + "%")));

        foreach (var (id, source) in new[] { (agent.Id, "agent"), (emailed, "email"), (portalId, "portal") })
        {
            var keys = await fixture.ScalarAsync<string>(
                "SELECT string_agg(k, ',' ORDER BY k) FROM (SELECT jsonb_object_keys(next) AS k FROM audit.audit_events WHERE object_id = @t AND action = 'ticket.created') x", TicketKit.P("t", id));
            Assert.Equal("assigneeUserId,formId,mailboxId,priority,reference,source,status,teamId,ticketTypeId", keys);
            Assert.Equal(source, await fixture.ScalarAsync<string>("SELECT next->>'source' FROM audit.audit_events WHERE object_id = @t AND action = 'ticket.created'", TicketKit.P("t", id)));
        }
    }
}
