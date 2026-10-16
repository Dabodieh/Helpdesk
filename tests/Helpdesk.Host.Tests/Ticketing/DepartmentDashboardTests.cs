#pragma warning disable CA1861 // one-off request payloads
using System.Net;
using System.Text.Json;
using Helpdesk.Host.Tests.Infrastructure;
using Helpdesk.Host.Tests.Mail;
using Helpdesk.Host.Tests.Sla;

namespace Helpdesk.Host.Tests.Ticketing;

/// <summary>The department dashboard: current operational state of ONE explicit department, built on the same predicates as the queues.</summary>
public sealed class DepartmentDashboardTests(HelpdeskFixture fixture) : IClassFixture<HelpdeskFixture>
{
    private static async Task<JsonElement> DashboardAsync(TestUser user, DepartmentRef d) => (await user.Client.GetAsync($"{d.Url}/dashboard")).Expect(HttpStatusCode.OK).Json;

    private static int Count(JsonElement json, params string[] path) => path.Aggregate(json, (e, p) => e.GetProperty(p)).GetInt32();

    private static async Task<int> QueueCountAsync(TestUser user, Guid departmentId, string extra = "") =>
        (await user.Client.GetAsync($"/api/tickets?queue=all&departmentId={departmentId}&status=Open&status=Pending&limit=100{extra}")).Expect(HttpStatusCode.OK).Json.GetProperty("items").GetArrayLength();

    [Fact]
    public async Task Figures_use_the_queue_definitions_exclude_merged_sources_and_separate_overdue_from_due_soon()
    {
        await using var w = await TimedWorld.CreateAsync(fixture, "2026-01-12T09:00:00Z"); // a Monday, 09:00
        await w.PolicyAsync("Standard", await w.CalendarAsync(), firstResponse: null, resolution: 240); // resolution due 13:00
        var a = await w.NewTicketAsync("A");
        var b = await w.NewTicketAsync("B");
        var c = await w.NewTicketAsync("C");
        var merge = (await w.Lead.Client.PostAsync($"/api/tickets/{a.Id}/merge", new
        {
            version = (await w.TicketAsync(a.Id)).Version,
            sources = new[] { new { ticketId = c.Id, version = (await w.TicketAsync(c.Id)).Version } },
        })).Expect(HttpStatusCode.OK);
        Assert.NotNull(merge);

        var d = w.Dept.Department;
        var morning = await DashboardAsync(w.Agent, d);
        Assert.Equal(2, Count(morning, "backlog", "open")); // C is a merged source: not actionable
        Assert.Equal(2, Count(morning, "backlog", "unassigned"));
        Assert.Equal(0, Count(morning, "sla", "overdue"));
        Assert.Equal(2, Count(morning, "sla", "dueSoon")); // due within four hours
        Assert.Equal(Count(morning, "sla", "dueSoon"), await QueueCountAsync(w.Agent, d.Id, "&sla=duesoon"));
        Assert.Equal(2, await QueueCountAsync(w.Agent, d.Id));

        await w.GoToAsync("2026-01-12T13:30:00Z");
        await w.SetStatusAsync(b.Id, "Pending");
        var late = await DashboardAsync(w.Agent, d);
        Assert.Equal((1, 1), (Count(late, "backlog", "open"), Count(late, "backlog", "pending")));
        Assert.Equal(2, Count(late, "sla", "overdue")); // active overdue work
        Assert.Equal(0, Count(late, "sla", "dueSoon"));
        Assert.Equal(Count(late, "sla", "overdue"), await QueueCountAsync(w.Agent, d.Id, "&sla=breached"));
        Assert.Equal(Count(late, "backlog", "unassigned"), await QueueCountAsync(w.Agent, d.Id, "&unassigned=true"));
        Assert.Equal(2, late.GetProperty("oldest").GetArrayLength());
        Assert.All(late.GetProperty("oldest").EnumerateArray(), o => Assert.True(o.GetProperty("slaOverdue").GetBoolean()));
        Assert.Equal(2, late.GetProperty("ageing").EnumerateArray().Sum(x => x.GetProperty("count").GetInt32()));

        // Resolved work leaves the backlog and the overdue figure with it (history of breaches is not part of this view).
        await w.SetStatusAsync(a.Id, "Resolved");
        var after = await DashboardAsync(w.Agent, d);
        Assert.Equal(1, Count(after, "backlog", "open") + Count(after, "backlog", "pending"));
        Assert.Equal(1, Count(after, "sla", "overdue"));
    }

    [Fact]
    public async Task One_department_at_a_time_other_departments_see_nothing_the_platform_admin_sees_every_department_and_mailbox_health_needs_the_mailbox_permission()
    {
        var it = await fixture.NewDeptAsync();
        var hr = await fixture.NewDeptAsync();
        var box = await fixture.CreateMailboxAsync(it);
        await it.Agent.CreateTicketAsync(it.Department, TicketHelpers.NewTicketBody($"IT only {Unique.Id()}"));
        await hr.Agent.CreateTicketAsync(hr.Department, TicketHelpers.NewTicketBody($"HR only {Unique.Id()}"));
        await hr.Agent.CreateTicketAsync(hr.Department, TicketHelpers.NewTicketBody($"HR second {Unique.Id()}"));

        // A member of both departments gets each department's own figures, never a sum.
        var both = await fixture.NewUserAsync("both");
        await fixture.AddMemberAsync(it.Department, both, "Agent");
        await fixture.AddMemberAsync(hr.Department, both, "Agent");
        Assert.Equal(1, Count(await DashboardAsync(both, it.Department), "backlog", "open"));
        Assert.Equal(2, Count(await DashboardAsync(both, hr.Department), "backlog", "open"));

        // No membership (another department's administrator) means the department does not exist here; the platform administrator (S-02) reaches every department.
        (await hr.Admin.Client.GetAsync($"{it.Department.Url}/dashboard")).Expect(HttpStatusCode.NotFound);
        var platform = await fixture.PlatformAdminAsync();
        (await platform.Client.GetAsync($"{it.Department.Url}/dashboard")).Expect(HttpStatusCode.OK);
        (await (await fixture.NewUserAsync("outsider")).Client.GetAsync($"{it.Department.Url}/dashboard")).Expect(HttpStatusCode.NotFound);

        // Mailbox diagnostics only for people who manage mailboxes; ticket access alone does not reach them.
        Assert.Equal(JsonValueKind.Null, (await DashboardAsync(it.Agent, it.Department)).GetProperty("mailboxes").ValueKind);
        var withMail = await DashboardAsync(it.Admin, it.Department);
        Assert.Equal(box.Address, withMail.GetProperty("mailboxes")[0].GetProperty("address").GetString());
        Assert.DoesNotContain(box.Address, (await DashboardAsync(hr.Admin, hr.Department)).GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_acknowledgement_never_satisfies_first_response_and_an_uncertain_send_surfaces()
    {
        var d = await fixture.NewDeptAsync();
        var box = await fixture.CreateMailboxAsync(d);
        var calendar = (await d.Admin.Client.PostAsync($"{d.Department.Url}/business-hours", new { name = "All week", timeZone = "UTC", exceptions = Array.Empty<object>(), weekly = new[] { "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday" }.Select(day => new { day, intervals = new[] { new { start = "00:00", end = "23:59" } } }).ToArray() })).Expect(HttpStatusCode.Created);
        (await d.Admin.Client.PostAsync($"{d.Department.Url}/sla-policies", new { name = "Respond", calendarId = calendar.Id, firstResponseMinutes = 60, resolutionMinutes = (int?)null, pauseWhenPending = false, approachingLeadMinutes = 30, conditions = Array.Empty<object>() })).Expect(HttpStatusCode.Created);
        (await d.Admin.Client.PutAsync($"{d.Department.Url}/auto-reply", new { enabled = true, forEmail = true, forPortal = false, subject = "Ack {{ticket.reference}}", body = "Thanks {{requester.name}}" })).Expect(HttpStatusCode.OK);

        var requester = MailKit.NewSender();
        var subject = $"Dash {Unique.Id()}";
        await fixture.ReceiveAsync(box, MailKit.Mail(requester, subject));
        await fixture.DispatchAsync();
        Assert.Single(fixture.Fake().SentMail(box.Address)); // the acknowledgement went out
        Assert.Equal(1, Count(await DashboardAsync(d.Agent, d.Department), "backlog", "awaitingFirstResponse")); // ... and is not a human response

        var ticket = Assert.Single(await d.Agent.TicketsAsync(d, subject));
        var ticketId = ticket.GetProperty("id").GetGuid();
        fixture.Fake().TimeoutNextSends(1, reachedProvider: false);
        (await d.Agent.PostMessageAsync(ticketId, "Public", "A human answers")).Expect(HttpStatusCode.Created);
        await fixture.DispatchAsync();
        var after = await DashboardAsync(d.Agent, d.Department);
        Assert.Equal(0, Count(after, "backlog", "awaitingFirstResponse")); // the agent's reply is the first response
        Assert.Equal(1, Count(after, "sends", "uncertainTickets"));
        Assert.Equal(0, Count(after, "sends", "failedTickets"));
        var reasons = (await DashboardAsync(d.Admin, d.Department)).GetProperty("mailboxes")[0].GetProperty("reasons").EnumerateArray().Select(r => r.GetString()).ToArray();
        Assert.Contains("outbound_uncertain", reasons);
    }
}
