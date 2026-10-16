using System.Net;
using Helpdesk.Host.Tests.Infrastructure;
using Helpdesk.Host.Tests.Mail;
using Helpdesk.Host.Tests.Ticketing;
using Helpdesk.Modules.Organisation.Contracts;

namespace Helpdesk.Host.Tests.Organisation;

/// <summary>
/// Policy S-02: the Platform Administrator is the global super-administrator. It works tickets (including Internal Notes), administers memberships and configuration in
/// EVERY Department without being a member, and every membership change it makes is audited and marked. Department Administrators, Agents and Viewers stay inside their
/// own Departments and roles.
/// </summary>
public sealed class PlatformAdministratorPolicyTests(HelpdeskFixture fixture) : IClassFixture<HelpdeskFixture>
{
    private static async Task<System.Text.Json.JsonElement> MembershipEventAsync(TestUser root, string action, Guid departmentId, Guid userId)
    {
        var items = (await root.Client.GetAsync("/api/platform/audit?limit=200")).Expect(HttpStatusCode.OK).Json.GetProperty("items").EnumerateArray();
        return items.First(e => e.GetProperty("action").GetString() == action
            && e.GetProperty("departmentId").GetGuid() == departmentId
            && e.GetProperty("objectId").GetGuid() == userId);
    }

    [Fact]
    public async Task Platform_admin_works_tickets_and_internal_notes_in_a_department_it_does_not_belong_to()
    {
        var d = await fixture.NewDeptAsync();
        var root = await fixture.PlatformAdminAsync();
        var subject = $"S02-{Unique.Id()}";
        var ticket = await d.Agent.CreateTicketAsync(d.Department, TicketHelpers.NewTicketBody(subject, $"req-{Unique.Id()}@example.test"));
        (await d.Agent.PostMessageAsync(ticket.Id, "Internal", "secret agent note")).Expect(HttpStatusCode.Created);

        // Reads: the ticket, its Internal Notes, the department queue and the department dashboard.
        var read = (await root.GetTicketAsync(ticket.Id)).Expect(HttpStatusCode.OK);
        Assert.Contains("secret agent note", (await root.Client.GetAsync(TicketKit.Url(ticket.Id, "/messages"))).Expect(HttpStatusCode.OK).Body, StringComparison.Ordinal);
        Assert.Contains(subject, (await root.Client.GetAsync($"/api/tickets?departmentId={d.Id}")).Expect(HttpStatusCode.OK).Body, StringComparison.Ordinal);
        (await root.Client.GetAsync($"{d.Department.Url}/dashboard")).Expect(HttpStatusCode.OK);

        // Writes: a note, a change, a ticket of its own.
        (await root.PostMessageAsync(ticket.Id, "Internal", "platform admin note")).Expect(HttpStatusCode.Created);
        (await root.PatchTicketAsync(read, new { Priority = "Urgent" })).Expect(HttpStatusCode.OK);
        (await root.CreateTicketAsync(d.Department, TicketHelpers.NewTicketBody($"S02-own-{Unique.Id()}", $"req-{Unique.Id()}@example.test"))).Expect(HttpStatusCode.Created);

        // Still not a member: the department is not in its own memberships.
        Assert.DoesNotContain(
            (await root.Client.GetAsync("/api/me")).Expect(HttpStatusCode.OK).Json.GetProperty("memberships").EnumerateArray(),
            m => m.GetProperty("departmentId").GetGuid() == d.Id);
    }

    [Fact]
    public async Task Platform_admin_manages_memberships_and_roles_of_any_department_and_each_change_is_audited_and_marked()
    {
        var d = await fixture.NewDeptAsync();
        var root = await fixture.PlatformAdminAsync();
        var person = await fixture.NewUserAsync();

        (await root.Client.PutAsync($"{d.Department.Url}/members/{person.Id}", new { role = DepartmentRoles.Viewer })).Expect(HttpStatusCode.OK);
        (await root.Client.PutAsync($"{d.Department.Url}/members/{person.Id}", new { role = DepartmentRoles.TeamLead })).Expect(HttpStatusCode.OK);
        (await root.Client.PutAsync($"{d.Department.Url}/members/{root.Id}", new { role = DepartmentRoles.Agent })).Expect(HttpStatusCode.OK); // self-assignment: allowed
        (await root.Client.DeleteAsync($"{d.Department.Url}/members/{person.Id}")).Expect(HttpStatusCode.NoContent);

        var added = await MembershipEventAsync(root, "membership.added", d.Id, person.Id);
        Assert.True(added.GetProperty("next").GetProperty("viaPlatformAdmin").GetBoolean());
        Assert.False(added.GetProperty("next").GetProperty("selfGrant").GetBoolean());
        var changed = await MembershipEventAsync(root, "membership.role_changed", d.Id, person.Id);
        Assert.True(changed.GetProperty("next").GetProperty("viaPlatformAdmin").GetBoolean());
        var removed = await MembershipEventAsync(root, "membership.removed", d.Id, person.Id);
        Assert.True(removed.GetProperty("next").GetProperty("viaPlatformAdmin").GetBoolean());
        var self = await MembershipEventAsync(root, "membership.added", d.Id, root.Id);
        Assert.True(self.GetProperty("next").GetProperty("selfGrant").GetBoolean());
        Assert.True(self.GetProperty("next").GetProperty("viaPlatformAdmin").GetBoolean());
    }

    [Fact]
    public async Task A_department_admin_changing_its_own_department_is_not_marked_as_platform_admin_action()
    {
        var d = await fixture.NewDeptAsync();
        var root = await fixture.PlatformAdminAsync();
        var person = await fixture.NewUserAsync();

        (await d.Admin.Client.PutAsync($"{d.Department.Url}/members/{person.Id}", new { role = DepartmentRoles.Agent })).Expect(HttpStatusCode.OK);

        var added = await MembershipEventAsync(root, "membership.added", d.Id, person.Id);
        Assert.False(added.GetProperty("next").GetProperty("viaPlatformAdmin").GetBoolean());
    }

    [Fact]
    public async Task Department_roles_stay_confined_to_their_own_department_and_permissions()
    {
        var a = await fixture.NewDeptAsync();
        var b = await fixture.NewDeptAsync();
        var ticketB = await b.Agent.CreateTicketAsync(b.Department, TicketHelpers.NewTicketBody($"S02-B-{Unique.Id()}", $"req-{Unique.Id()}@example.test"));
        var person = await fixture.NewUserAsync();

        // Another department's administrator, lead, agent and viewer: B is invisible (404) for content and for administration.
        foreach (var user in new[] { a.Admin, a.Lead, a.Agent, a.Viewer })
        {
            (await user.GetTicketAsync(ticketB.Id)).Expect(HttpStatusCode.NotFound);
            (await user.Client.GetAsync($"{b.Department.Url}/members")).Expect(HttpStatusCode.NotFound);
        }

        (await a.Admin.Client.PutAsync($"{b.Department.Url}/members/{person.Id}", new { role = DepartmentRoles.Agent })).Expect(HttpStatusCode.NotFound);
        (await a.Admin.Client.DeleteAsync($"{b.Department.Url}/members/{b.Agent.Id}")).Expect(HttpStatusCode.NotFound);

        // Inside its own department a role keeps its limits: an agent and a viewer cannot administer, a viewer cannot work tickets.
        var ticketA = await a.Agent.CreateTicketAsync(a.Department, TicketHelpers.NewTicketBody($"S02-A-{Unique.Id()}", $"req-{Unique.Id()}@example.test"));
        (await a.Agent.Client.PutAsync($"{a.Department.Url}/members/{person.Id}", new { role = DepartmentRoles.Agent })).Expect(HttpStatusCode.Forbidden);
        (await a.Viewer.Client.PutAsync($"{a.Department.Url}/members/{person.Id}", new { role = DepartmentRoles.Agent })).Expect(HttpStatusCode.Forbidden);
        (await a.Viewer.PostMessageAsync(ticketA.Id, "Internal", "viewer note")).Expect(HttpStatusCode.Forbidden);

        // Platform-level powers still need the flag: no department role, however high, carries them.
        (await a.Admin.Client.PutAsync($"/api/platform/users/{a.Admin.Id}/platform-admin", new { value = true })).Expect(HttpStatusCode.Forbidden);
        (await a.Admin.Client.GetAsync("/api/platform/audit")).Expect(HttpStatusCode.Forbidden);
    }
}
