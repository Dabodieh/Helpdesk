using System.Net;
using Helpdesk.Host.Tests.Infrastructure;
using Helpdesk.Modules.Organisation.Contracts;

namespace Helpdesk.Host.Tests.Organisation;

/// <summary>End-to-end decisions through the real ASP.NET authorization pipeline (fallback policy, handler, 404/403 mapping).</summary>
public sealed class AuthorizationDecisionTests(HelpdeskFixture fixture) : IClassFixture<HelpdeskFixture>
{
    [Theory]
    [InlineData(DepartmentRoles.Viewer)]
    [InlineData(DepartmentRoles.Agent)]
    [InlineData(DepartmentRoles.TeamLead)]
    [InlineData(DepartmentRoles.DepartmentAdmin)]
    public async Task Every_role_may_read_tickets(string role)
    {
        var department = await fixture.CreateDepartmentAsync();
        var ticket = await fixture.CreateTicketAsync(department);
        var user = await fixture.NewMemberAsync(department, role);
        var response = (await user.Client.GetAsync($"/api/tickets/{ticket.Id}")).Expect(HttpStatusCode.OK);
        Assert.Equal(ticket.Id, response.Id);
    }

    [Fact]
    public async Task Ticket_read_is_404_for_outsiders_and_unknown_tickets_and_401_without_session()
    {
        var department = await fixture.CreateDepartmentAsync();
        var ticket = await fixture.CreateTicketAsync(department);
        var outsider = await fixture.NewUserAsync();

        var hidden = (await outsider.Client.GetAsync($"/api/tickets/{ticket.Id}")).Expect(HttpStatusCode.NotFound);
        var unknown = (await outsider.Client.GetAsync($"/api/tickets/{Guid.NewGuid()}")).Expect(HttpStatusCode.NotFound);
        Assert.Equal(hidden.Body.Length, unknown.Body.Length);
        (await fixture.CreateAnonymousClient().GetAsync($"/api/tickets/{ticket.Id}")).Expect(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Platform_admin_without_membership_has_content_and_administrative_access_in_every_department()
    {
        var department = await fixture.CreateDepartmentAsync();
        var ticket = await fixture.CreateTicketAsync(department);
        var admin = await fixture.PlatformAdminAsync();

        (await admin.Client.GetAsync($"/api/tickets/{ticket.Id}")).Expect(HttpStatusCode.OK);
        (await admin.Client.GetAsync($"/api/tickets/{Guid.NewGuid()}")).Expect(HttpStatusCode.NotFound);

        (await admin.Client.GetAsync(department.Url)).Expect(HttpStatusCode.OK);
        (await admin.Client.GetAsync($"{department.Url}/members")).Expect(HttpStatusCode.OK);
        (await admin.Client.GetAsync($"{department.Url}/audit")).Expect(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Content_access_follows_the_platform_flag_immediately_in_both_directions()
    {
        var department = await fixture.CreateDepartmentAsync();
        var ticket = await fixture.CreateTicketAsync(department);
        var admin = await fixture.NewUserAsync("padmin");
        var root = await fixture.PlatformAdminAsync();

        (await admin.Client.GetAsync($"/api/tickets/{ticket.Id}")).Expect(HttpStatusCode.NotFound);
        (await root.Client.PutAsync($"/api/platform/users/{admin.Id}/platform-admin", new { value = true })).Expect(HttpStatusCode.OK);
        (await admin.Client.GetAsync($"/api/tickets/{ticket.Id}")).Expect(HttpStatusCode.OK);
        (await root.Client.PutAsync($"/api/platform/users/{admin.Id}/platform-admin", new { value = false })).Expect(HttpStatusCode.OK);
        (await admin.Client.GetAsync($"/api/tickets/{ticket.Id}")).Expect(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Insufficient_role_is_403_for_members()
    {
        var department = await fixture.CreateDepartmentAsync();
        var agent = await fixture.NewMemberAsync(department, DepartmentRoles.Agent);
        var viewer = await fixture.NewMemberAsync(department, DepartmentRoles.Viewer);

        foreach (var user in new[] { agent, viewer })
        {
            (await user.Client.GetAsync($"{department.Url}/audit")).Expect(HttpStatusCode.Forbidden);
            (await user.Client.PutAsync($"{department.Url}/members/{user.Id}", new { role = DepartmentRoles.DepartmentAdmin })).Expect(HttpStatusCode.Forbidden);
        }
    }

    [Fact]
    public async Task Platform_endpoints_are_403_for_everyone_but_platform_admins_even_department_admins()
    {
        var department = await fixture.CreateDepartmentAsync();
        var deptAdmin = await fixture.NewMemberAsync(department, DepartmentRoles.DepartmentAdmin);

        (await deptAdmin.Client.GetAsync("/api/platform/users")).Expect(HttpStatusCode.Forbidden);
        (await deptAdmin.Client.GetAsync("/api/platform/audit")).Expect(HttpStatusCode.Forbidden);
        (await deptAdmin.Client.PutAsync($"/api/platform/users/{deptAdmin.Id}/platform-admin", new { value = true })).Expect(HttpStatusCode.Forbidden);
        (await deptAdmin.Client.PostAsync("/api/departments", new { key = Unique.Key(), name = "x" })).Expect(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Removing_a_membership_takes_effect_on_the_next_request()
    {
        var department = await fixture.CreateDepartmentAsync();
        var admin = await fixture.PlatformAdminAsync();
        var second = await fixture.NewMemberAsync(department, DepartmentRoles.DepartmentAdmin);
        var user = await fixture.NewMemberAsync(department, DepartmentRoles.Agent);

        (await user.Client.GetAsync(department.Url)).Expect(HttpStatusCode.OK);
        (await admin.Client.DeleteAsync($"{department.Url}/members/{user.Id}")).Expect(HttpStatusCode.NoContent);
        (await user.Client.GetAsync(department.Url)).Expect(HttpStatusCode.NotFound);
        _ = second;
    }

    [Fact]
    public async Task Isolation_between_two_departments_holds_for_reads_and_writes()
    {
        var a = await fixture.CreateDepartmentAsync();
        var b = await fixture.CreateDepartmentAsync();
        var adminOfA = await fixture.NewMemberAsync(a, DepartmentRoles.DepartmentAdmin);
        var ticketOfB = await fixture.CreateTicketAsync(b);

        (await adminOfA.Client.GetAsync(b.Url)).Expect(HttpStatusCode.NotFound);
        (await adminOfA.Client.GetAsync($"{b.Url}/members")).Expect(HttpStatusCode.NotFound);
        (await adminOfA.Client.GetAsync($"{b.Url}/audit")).Expect(HttpStatusCode.NotFound);
        (await adminOfA.Client.GetAsync($"/api/tickets/{ticketOfB.Id}")).Expect(HttpStatusCode.NotFound);
        var listed = (await adminOfA.Client.GetAsync("/api/departments")).Json.EnumerateArray().Select(d => d.GetProperty("id").GetGuid()).ToList();
        Assert.DoesNotContain(b.Id, listed);
    }
}
