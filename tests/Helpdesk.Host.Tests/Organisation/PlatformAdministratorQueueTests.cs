using System.Net;
using Helpdesk.Host.Tests.Infrastructure;
using Helpdesk.Host.Tests.Mail;
using Helpdesk.Host.Tests.Ticketing;
using Helpdesk.Modules.Organisation.Contracts;

namespace Helpdesk.Host.Tests.Organisation;

/// <summary>
/// S-02 finalisation: the everyday queue, search, saved views and department list of a Platform Administrator cover EVERY department without a membership, and that global
/// access never turns into participation (no membership, assignment, follow or notification). Agents and Department Administrators stay restricted.
/// </summary>
public sealed class PlatformAdministratorQueueTests(HelpdeskFixture fixture) : IClassFixture<HelpdeskFixture>
{
    private const string AllStatuses = "status=Open&status=Pending&status=Resolved&status=Closed";

    private static List<Guid> Ids(ApiResponse page) => [.. page.Json.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid())];

    private static async Task<List<Guid>> QueueAsync(TestUser user, string extra = "") =>
        Ids((await user.Client.GetAsync($"/api/tickets?queue=all&{AllStatuses}{extra}")).Expect(HttpStatusCode.OK));

    [Fact]
    public async Task The_queue_search_and_department_filter_of_a_platform_admin_cover_every_department_including_internal_notes()
    {
        var a = await fixture.NewDeptAsync();
        var b = await fixture.NewDeptAsync();
        var root = await fixture.PlatformAdminAsync();
        var token = $"s02q{Unique.Id()}";
        var ta = await a.Agent.CreateTicketAsync(a.Department, TicketHelpers.NewTicketBody($"A {token}", $"a-{Unique.Id()}@example.test"));
        var tb = await b.Agent.CreateTicketAsync(b.Department, TicketHelpers.NewTicketBody($"B {Unique.Id()}", $"b-{Unique.Id()}@example.test"));
        (await b.Agent.PostMessageAsync(tb.Id, "Internal", $"private {token}-note")).Expect(HttpStatusCode.Created);

        // The queue: both departments, or either one through the department filter.
        var all = await QueueAsync(root);
        Assert.Contains(ta.Id, all);
        Assert.Contains(tb.Id, all);
        Assert.Equal([ta.Id], await QueueAsync(root, $"&departmentId={a.Id}&q={token}"));
        Assert.Contains(tb.Id, await QueueAsync(root, $"&departmentId={b.Id}"));
        Assert.DoesNotContain(ta.Id, await QueueAsync(root, $"&departmentId={b.Id}"));

        // Search finds the ticket subject in A and B's Internal Note, in both departments at once.
        var found = await QueueAsync(root, $"&q={token}");
        Assert.Contains(ta.Id, found);
        Assert.Contains(tb.Id, found);

        // Restricted callers: each sees only its own department, and B's Internal Note is invisible to A's staff.
        Assert.DoesNotContain(tb.Id, await QueueAsync(a.Agent));
        Assert.DoesNotContain(tb.Id, await QueueAsync(a.Admin, $"&q={token}"));
        Assert.Equal([ta.Id], await QueueAsync(a.Admin, $"&q={token}"));
        Assert.DoesNotContain(ta.Id, await QueueAsync(b.Admin));
    }

    [Fact]
    public async Task A_saved_view_of_a_platform_admin_may_target_a_department_it_does_not_belong_to_and_nobody_else_may_use_it()
    {
        var a = await fixture.NewDeptAsync();
        var b = await fixture.NewDeptAsync();
        var root = await fixture.PlatformAdminAsync();
        var tb = await b.Agent.CreateTicketAsync(b.Department, TicketHelpers.NewTicketBody($"View {Unique.Id()}", $"v-{Unique.Id()}@example.test"));

        var view = (await root.Client.PostAsync("/api/views", new { name = $"B only {Unique.Id()}", filter = new { queue = "all", departmentId = b.Id }, isPinned = false })).Expect(HttpStatusCode.Created);
        Assert.Contains(tb.Id, Ids((await root.Client.GetAsync($"/api/tickets?view={view.Id}")).Expect(HttpStatusCode.OK)));

        // A view is private to its owner; a member of A cannot open it, and a view an Agent of A aims at B returns nothing.
        (await a.Agent.Client.GetAsync($"/api/tickets?view={view.Id}")).Expect(HttpStatusCode.NotFound);
        var spy = (await a.Agent.Client.PostAsync("/api/views", new { name = $"Spy {Unique.Id()}", filter = new { queue = "all", departmentId = b.Id }, isPinned = false })).Expect(HttpStatusCode.Created);
        Assert.DoesNotContain(tb.Id, Ids((await a.Agent.Client.GetAsync($"/api/tickets?view={spy.Id}")).Expect(HttpStatusCode.OK)));
    }

    [Fact]
    public async Task Me_lists_every_department_for_a_platform_admin_and_none_for_anyone_else_and_never_a_membership()
    {
        var d = await fixture.NewDeptAsync();
        var root = await fixture.PlatformAdminAsync();

        var adminMe = (await root.Client.GetAsync("/api/me")).Expect(HttpStatusCode.OK).Json;
        Assert.Contains(adminMe.GetProperty("departments").EnumerateArray(), x => x.GetProperty("departmentId").GetGuid() == d.Id);
        Assert.DoesNotContain(adminMe.GetProperty("memberships").EnumerateArray(), m => m.GetProperty("departmentId").GetGuid() == d.Id);

        foreach (var user in new[] { d.Admin, d.Lead, d.Agent, d.Viewer })
        {
            Assert.Empty((await user.Client.GetAsync("/api/me")).Expect(HttpStatusCode.OK).Json.GetProperty("departments").EnumerateArray());
        }
    }

    [Fact]
    public async Task Global_access_never_creates_a_membership_assignment_follow_or_notification()
    {
        var d = await fixture.NewDeptAsync();
        var root = await fixture.PlatformAdminAsync();
        var before = (await root.Client.GetAsync("/api/notifications")).Expect(HttpStatusCode.OK).Json.GetProperty("unreadCount").GetInt32();
        var ticket = await d.Agent.CreateTicketAsync(d.Department, TicketHelpers.NewTicketBody($"Quiet {Unique.Id()}", $"q-{Unique.Id()}@example.test"));

        // Read, annotate and change the ticket as the platform admin.
        var read = (await root.GetTicketAsync(ticket.Id)).Expect(HttpStatusCode.OK);
        (await root.PostMessageAsync(ticket.Id, "Internal", "platform admin was here")).Expect(HttpStatusCode.Created);
        (await root.PatchTicketAsync(read, new { Priority = "High" })).Expect(HttpStatusCode.OK);

        Assert.Equal(0, await fixture.CountAsync("SELECT count(*) FROM organisation.department_memberships WHERE user_id = @u", TicketKit.P("u", root.Id)));
        Assert.Equal(0, await fixture.CountAsync("SELECT count(*) FROM ticketing.ticket_followers WHERE ticket_id = @t AND user_id = @u", TicketKit.P("t", ticket.Id), TicketKit.P("u", root.Id)));
        Assert.Null(await fixture.ScalarAsync<Guid?>("SELECT assignee_user_id FROM ticketing.tickets WHERE id = @t", TicketKit.P("t", ticket.Id)));
        Assert.Equal(before, (await root.Client.GetAsync("/api/notifications")).Expect(HttpStatusCode.OK).Json.GetProperty("unreadCount").GetInt32());

        // Participation stays membership-based: an assignment to a non-member platform admin is refused until it joins the department.
        var lead = d.Lead;
        var current = (await lead.GetTicketAsync(ticket.Id)).Expect(HttpStatusCode.OK);
        Assert.Equal(HttpStatusCode.Conflict, (await lead.AssignAsync(ticket.Id, current.Version, root.Id)).Status);
        (await root.Client.PutAsync($"{d.Department.Url}/members/{root.Id}", new { role = DepartmentRoles.Agent })).Expect(HttpStatusCode.OK);
        current = (await lead.GetTicketAsync(ticket.Id)).Expect(HttpStatusCode.OK);
        (await lead.AssignAsync(ticket.Id, current.Version, root.Id)).Expect(HttpStatusCode.OK);
    }
}
