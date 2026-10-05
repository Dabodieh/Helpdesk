using System.Net;
using System.Text.Json;
using Helpdesk.Host.Tests.Infrastructure;
using Helpdesk.Modules.Organisation.Contracts;

namespace Helpdesk.Host.Tests.Organisation;

public sealed class MembershipApiTests(HelpdeskFixture fixture) : IClassFixture<HelpdeskFixture>
{
    [Fact]
    public async Task Admin_adds_changes_and_removes_members_and_each_step_is_audited()
    {
        var department = await fixture.CreateDepartmentAsync();
        var admin = await fixture.PlatformAdminAsync();
        var user = await fixture.NewUserAsync();
        var url = $"{department.Url}/members/{user.Id}";

        var added = (await admin.Client.PutAsync(url, new { role = DepartmentRoles.Viewer })).Expect(HttpStatusCode.OK);
        Assert.Equal(DepartmentRoles.Viewer, added.Json.GetProperty("role").GetString());
        var changed = (await admin.Client.PutAsync(url, new { role = DepartmentRoles.Agent })).Expect(HttpStatusCode.OK);
        Assert.Equal(DepartmentRoles.Agent, changed.Json.GetProperty("role").GetString());
        (await admin.Client.PutAsync(url, new { role = DepartmentRoles.Agent })).Expect(HttpStatusCode.OK); // no-op, no extra audit
        (await admin.Client.DeleteAsync(url)).Expect(HttpStatusCode.NoContent);
        (await admin.Client.DeleteAsync(url)).Expect(HttpStatusCode.NotFound);

        Assert.Equal(1, await fixture.CountAuditAsync("membership.added", user.Id));
        Assert.Equal(1, await fixture.CountAuditAsync("membership.role_changed", user.Id));
        Assert.Equal(1, await fixture.CountAuditAsync("membership.removed", user.Id));
    }

    [Fact]
    public async Task Member_list_includes_roles_and_team_ids()
    {
        var department = await fixture.CreateDepartmentAsync();
        var team = await fixture.CreateTeamAsync(department);
        var admin = await fixture.PlatformAdminAsync();
        var agent = await fixture.NewMemberAsync(department, DepartmentRoles.Agent);
        await admin.Client.PutAsync($"{department.Url}/teams/{team.Id}/members/{agent.Id}");

        var members = (await agent.Client.GetAsync($"{department.Url}/members")).Expect(HttpStatusCode.OK).Json.EnumerateArray().ToList();
        var me = Assert.Single(members, m => m.GetProperty("userId").GetGuid() == agent.Id);
        Assert.Equal(DepartmentRoles.Agent, me.GetProperty("role").GetString());
        Assert.Equal(team.Id, Assert.Single(me.GetProperty("teamIds").EnumerateArray()).GetGuid());
        Assert.False(string.IsNullOrEmpty(me.GetProperty("displayName").GetString()));
    }

    [Theory]
    [InlineData("Owner")]
    [InlineData("agent")]
    [InlineData("")]
    public async Task Invalid_roles_are_400(string role)
    {
        var department = await fixture.CreateDepartmentAsync();
        var admin = await fixture.PlatformAdminAsync();
        var user = await fixture.NewUserAsync();
        (await admin.Client.PutAsync($"{department.Url}/members/{user.Id}", new { role })).Expect(HttpStatusCode.BadRequest);
        (await admin.Client.PutAsync($"{department.Url}/members/{user.Id}", new { })).Expect(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Unknown_user_is_404_and_roles_need_department_members_manage()
    {
        var department = await fixture.CreateDepartmentAsync();
        var admin = await fixture.PlatformAdminAsync();
        var lead = await fixture.NewMemberAsync(department, DepartmentRoles.TeamLead);
        var outsider = await fixture.NewUserAsync();
        var target = await fixture.NewUserAsync();

        (await admin.Client.PutAsync($"{department.Url}/members/{Guid.NewGuid()}", new { role = DepartmentRoles.Agent })).Expect(HttpStatusCode.NotFound);
        (await lead.Client.PutAsync($"{department.Url}/members/{target.Id}", new { role = DepartmentRoles.Agent })).Expect(HttpStatusCode.Forbidden);
        (await lead.Client.DeleteAsync($"{department.Url}/members/{lead.Id}")).Expect(HttpStatusCode.Forbidden);
        (await outsider.Client.PutAsync($"{department.Url}/members/{target.Id}", new { role = DepartmentRoles.Agent })).Expect(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Department_admin_cannot_touch_another_department()
    {
        var mine = await fixture.CreateDepartmentAsync();
        var other = await fixture.CreateDepartmentAsync();
        var deptAdmin = await fixture.NewMemberAsync(mine, DepartmentRoles.DepartmentAdmin);
        var target = await fixture.NewUserAsync();

        (await deptAdmin.Client.PutAsync($"{other.Url}/members/{target.Id}", new { role = DepartmentRoles.Agent })).Expect(HttpStatusCode.NotFound);
        (await deptAdmin.Client.PutAsync($"{mine.Url}/members/{target.Id}", new { role = DepartmentRoles.Agent })).Expect(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Last_department_admin_cannot_be_demoted_or_removed()
    {
        var department = await fixture.CreateDepartmentAsync();
        var admin = await fixture.PlatformAdminAsync();
        var onlyAdmin = await fixture.NewMemberAsync(department, DepartmentRoles.DepartmentAdmin);
        var url = $"{department.Url}/members/{onlyAdmin.Id}";

        (await admin.Client.PutAsync(url, new { role = DepartmentRoles.Agent })).Expect(HttpStatusCode.Conflict);
        (await admin.Client.DeleteAsync(url)).Expect(HttpStatusCode.Conflict);
        (await onlyAdmin.Client.DeleteAsync(url)).Expect(HttpStatusCode.Conflict);

        var second = await fixture.NewMemberAsync(department, DepartmentRoles.DepartmentAdmin);
        (await admin.Client.PutAsync(url, new { role = DepartmentRoles.Agent })).Expect(HttpStatusCode.OK);
        (await admin.Client.DeleteAsync($"{department.Url}/members/{second.Id}")).Expect(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Two_admins_demoting_each_other_concurrently_cannot_leave_zero_admins()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var department = await fixture.CreateDepartmentAsync();
            var one = await fixture.NewMemberAsync(department, DepartmentRoles.DepartmentAdmin);
            var two = await fixture.NewMemberAsync(department, DepartmentRoles.DepartmentAdmin);

            var results = await Task.WhenAll(
                one.Client.PutAsync($"{department.Url}/members/{two.Id}", new { role = DepartmentRoles.Viewer }),
                two.Client.PutAsync($"{department.Url}/members/{one.Id}", new { role = DepartmentRoles.Viewer }));

            // The loser is refused by the last-admin rule (409) or, if it started after the winner committed, is no longer an admin (403).
            Assert.Equal(1, results.Count(r => r.Status == HttpStatusCode.OK));
            Assert.Single(results, r => r.Status is HttpStatusCode.Conflict or HttpStatusCode.Forbidden);
            var admins = await fixture.ScalarAsync<long>(
                "SELECT count(*) FROM organisation.department_memberships WHERE department_id = @d AND role = 'DepartmentAdmin'",
                new Npgsql.NpgsqlParameter("d", department.Id));
            Assert.Equal(1, admins);
        }
    }

    [Fact]
    public async Task Self_grant_by_a_platform_admin_is_flagged_in_the_audit_event()
    {
        var department = await fixture.CreateDepartmentAsync();
        var admin = await fixture.PlatformAdminAsync();
        var other = await fixture.NewUserAsync();

        (await admin.Client.PutAsync($"{department.Url}/members/{admin.Id}", new { role = DepartmentRoles.Agent })).Expect(HttpStatusCode.OK);
        (await admin.Client.PutAsync($"{department.Url}/members/{other.Id}", new { role = DepartmentRoles.Agent })).Expect(HttpStatusCode.OK);

        // Platform admin is now a member, so the department audit is readable through the API only if the role allows; read the log through the platform view.
        var events = (await admin.Client.GetAsync("/api/platform/audit?limit=200")).Expect(HttpStatusCode.OK).Json.GetProperty("items").EnumerateArray()
            .Where(e => e.GetProperty("action").GetString() == "membership.added" && e.GetProperty("departmentId").GetGuid() == department.Id)
            .ToList();
        var selfEvent = Assert.Single(events, e => e.GetProperty("objectId").GetGuid() == admin.Id);
        var otherEvent = Assert.Single(events, e => e.GetProperty("objectId").GetGuid() == other.Id);
        Assert.True(selfEvent.GetProperty("next").GetProperty("selfGrant").GetBoolean());
        Assert.False(otherEvent.GetProperty("next").GetProperty("selfGrant").GetBoolean());
    }

    [Fact]
    public async Task Inactive_users_cannot_be_added_and_new_members_need_an_active_department()
    {
        var department = await fixture.CreateDepartmentAsync();
        var admin = await fixture.PlatformAdminAsync();
        var inactive = await fixture.NewUserAsync();
        await fixture.ExecuteAsync("UPDATE identity.users SET is_active = false WHERE id = @id", new Npgsql.NpgsqlParameter("id", inactive.Id));
        (await admin.Client.PutAsync($"{department.Url}/members/{inactive.Id}", new { role = DepartmentRoles.Agent })).Expect(HttpStatusCode.Conflict);

        var active = await fixture.NewUserAsync();
        (await admin.Client.PatchAsync(department.Url, new { version = department.Version, isActive = false })).Expect(HttpStatusCode.OK);
        (await admin.Client.PutAsync($"{department.Url}/members/{active.Id}", new { role = DepartmentRoles.Agent })).Expect(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Me_lists_memberships_with_informational_permissions()
    {
        var department = await fixture.CreateDepartmentAsync();
        var lead = await fixture.NewMemberAsync(department, DepartmentRoles.TeamLead);

        var me = (await lead.Client.GetAsync("/api/me")).Expect(HttpStatusCode.OK).Json;
        Assert.False(me.GetProperty("isPlatformAdmin").GetBoolean());
        Assert.Empty(me.GetProperty("platformPermissions").EnumerateArray());
        var membership = Assert.Single(me.GetProperty("memberships").EnumerateArray());
        Assert.Equal(department.Key, membership.GetProperty("departmentKey").GetString());
        var permissions = membership.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()).ToList();
        Assert.Contains("tickets.read", permissions);
        Assert.Contains("department.audit.read", permissions);
        Assert.DoesNotContain("department.manage", permissions);

        var platform = (await (await fixture.PlatformAdminAsync()).Client.GetAsync("/api/me")).Json;
        Assert.Contains("platform.audit.read", platform.GetProperty("platformPermissions").EnumerateArray().Select(p => p.GetString()));
        Assert.Equal(JsonValueKind.Array, platform.GetProperty("memberships").ValueKind);
    }
}
