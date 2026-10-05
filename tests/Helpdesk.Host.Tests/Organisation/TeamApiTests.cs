using System.Net;
using Helpdesk.Host.Tests.Infrastructure;
using Helpdesk.Modules.Organisation.Contracts;

namespace Helpdesk.Host.Tests.Organisation;

public sealed class TeamApiTests(HelpdeskFixture fixture) : IClassFixture<HelpdeskFixture>
{
    [Fact]
    public async Task Department_admin_creates_teams_but_agents_cannot()
    {
        var department = await fixture.CreateDepartmentAsync();
        var deptAdmin = await fixture.NewMemberAsync(department, DepartmentRoles.DepartmentAdmin);
        var agent = await fixture.NewMemberAsync(department, DepartmentRoles.Agent);
        var lead = await fixture.NewMemberAsync(department, DepartmentRoles.TeamLead);

        (await agent.Client.PostAsync($"{department.Url}/teams", new { name = "Nope" })).Expect(HttpStatusCode.Forbidden);
        (await lead.Client.PostAsync($"{department.Url}/teams", new { name = "Nope" })).Expect(HttpStatusCode.Forbidden); // TeamLead scoped management is deferred
        var created = (await deptAdmin.Client.PostAsync($"{department.Url}/teams", new { name = $"Support {Unique.Id()}", description = "front line" })).Expect(HttpStatusCode.Created);
        Assert.Equal(0, created.Json.GetProperty("memberCount").GetInt32());
        Assert.Equal(1, await fixture.CountAuditAsync("team.created", created.Id));
    }

    [Fact]
    public async Task Outsiders_get_404_on_every_team_endpoint()
    {
        var department = await fixture.CreateDepartmentAsync();
        var team = await fixture.CreateTeamAsync(department);
        var outsider = await fixture.NewUserAsync();
        var url = $"{department.Url}/teams";

        (await outsider.Client.GetAsync(url)).Expect(HttpStatusCode.NotFound);
        (await outsider.Client.PostAsync(url, new { name = "x" })).Expect(HttpStatusCode.NotFound);
        (await outsider.Client.GetAsync($"{url}/{team.Id}")).Expect(HttpStatusCode.NotFound);
        (await outsider.Client.PatchAsync($"{url}/{team.Id}", new { version = team.Version, name = "y" })).Expect(HttpStatusCode.NotFound);
        (await outsider.Client.GetAsync($"{url}/{team.Id}/members")).Expect(HttpStatusCode.NotFound);
        (await outsider.Client.PutAsync($"{url}/{team.Id}/members/{outsider.Id}")).Expect(HttpStatusCode.NotFound);
        (await outsider.Client.DeleteAsync($"{url}/{team.Id}/members/{outsider.Id}")).Expect(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Team_name_is_unique_per_department_case_insensitively_but_reusable_across_departments()
    {
        var first = await fixture.CreateDepartmentAsync();
        var second = await fixture.CreateDepartmentAsync();
        var admin = await fixture.PlatformAdminAsync();
        var name = $"Network {Unique.Id()}";

        (await admin.Client.PostAsync($"{first.Url}/teams", new { name })).Expect(HttpStatusCode.Created);
        (await admin.Client.PostAsync($"{first.Url}/teams", new { name = name.ToUpperInvariant() })).Expect(HttpStatusCode.Conflict);
        (await admin.Client.PostAsync($"{second.Url}/teams", new { name })).Expect(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Teams_are_resolved_by_both_ids()
    {
        var first = await fixture.CreateDepartmentAsync();
        var second = await fixture.CreateDepartmentAsync();
        var team = await fixture.CreateTeamAsync(first);
        var admin = await fixture.PlatformAdminAsync();

        (await admin.Client.GetAsync($"{first.Url}/teams/{team.Id}")).Expect(HttpStatusCode.OK);
        (await admin.Client.GetAsync($"{second.Url}/teams/{team.Id}")).Expect(HttpStatusCode.NotFound);
        (await admin.Client.PatchAsync($"{second.Url}/teams/{team.Id}", new { version = team.Version, name = "hijack" })).Expect(HttpStatusCode.NotFound);
        (await admin.Client.GetAsync($"{second.Url}/teams/{team.Id}/members")).Expect(HttpStatusCode.NotFound);

        // Even a DepartmentAdmin of the second department cannot reach the first department's team through their own department.
        var otherAdmin = await fixture.NewMemberAsync(second, DepartmentRoles.DepartmentAdmin);
        (await otherAdmin.Client.GetAsync($"{second.Url}/teams/{team.Id}")).Expect(HttpStatusCode.NotFound);
        (await otherAdmin.Client.GetAsync($"{first.Url}/teams/{team.Id}")).Expect(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Patch_renames_deactivates_and_reactivates_with_audit()
    {
        var department = await fixture.CreateDepartmentAsync();
        var team = await fixture.CreateTeamAsync(department);
        var admin = await fixture.PlatformAdminAsync();
        var url = $"{department.Url}/teams/{team.Id}";

        var renamed = (await admin.Client.PatchAsync(url, new { version = team.Version, name = $"Renamed {Unique.Id()}" })).Expect(HttpStatusCode.OK);
        var off = (await admin.Client.PatchAsync(url, new { version = renamed.Version, isActive = false })).Expect(HttpStatusCode.OK);
        Assert.False(off.Json.GetProperty("isActive").GetBoolean());
        (await admin.Client.PatchAsync(url, new { version = off.Version, isActive = true })).Expect(HttpStatusCode.OK);

        Assert.Equal(1, await fixture.CountAuditAsync("team.updated", team.Id));
        Assert.Equal(1, await fixture.CountAuditAsync("team.deactivated", team.Id));
        Assert.Equal(1, await fixture.CountAuditAsync("team.activated", team.Id));
    }

    [Fact]
    public async Task Patch_with_stale_version_is_409()
    {
        var department = await fixture.CreateDepartmentAsync();
        var team = await fixture.CreateTeamAsync(department);
        var admin = await fixture.PlatformAdminAsync();
        var url = $"{department.Url}/teams/{team.Id}";

        (await admin.Client.PatchAsync(url, new { version = team.Version, name = $"A {Unique.Id()}" })).Expect(HttpStatusCode.OK);
        (await admin.Client.PatchAsync(url, new { version = team.Version, name = $"B {Unique.Id()}" })).Expect(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Team_membership_requires_department_membership_and_is_audited()
    {
        var department = await fixture.CreateDepartmentAsync();
        var team = await fixture.CreateTeamAsync(department);
        var admin = await fixture.PlatformAdminAsync();
        var agent = await fixture.NewMemberAsync(department, DepartmentRoles.Agent);
        var stranger = await fixture.NewUserAsync();
        var membersUrl = $"{department.Url}/teams/{team.Id}/members";

        (await admin.Client.PutAsync($"{membersUrl}/{stranger.Id}")).Expect(HttpStatusCode.Conflict);
        (await admin.Client.PutAsync($"{membersUrl}/{agent.Id}")).Expect(HttpStatusCode.NoContent);
        (await admin.Client.PutAsync($"{membersUrl}/{agent.Id}")).Expect(HttpStatusCode.NoContent); // idempotent

        var members = (await agent.Client.GetAsync(membersUrl)).Expect(HttpStatusCode.OK).Json.EnumerateArray().ToList();
        Assert.Single(members);
        Assert.Equal(agent.Id, members[0].GetProperty("userId").GetGuid());
        Assert.Equal(1, (await admin.Client.GetAsync($"{department.Url}/teams/{team.Id}")).Json.GetProperty("memberCount").GetInt32());
        Assert.Equal(1, await fixture.CountAuditAsync("team_membership.added", team.Id));

        (await admin.Client.DeleteAsync($"{membersUrl}/{agent.Id}")).Expect(HttpStatusCode.NoContent);
        (await admin.Client.DeleteAsync($"{membersUrl}/{agent.Id}")).Expect(HttpStatusCode.NotFound);
        Assert.Equal(1, await fixture.CountAuditAsync("team_membership.removed", team.Id));
    }

    [Fact]
    public async Task Team_membership_of_another_departments_member_is_rejected()
    {
        var first = await fixture.CreateDepartmentAsync();
        var second = await fixture.CreateDepartmentAsync();
        var team = await fixture.CreateTeamAsync(first);
        var admin = await fixture.PlatformAdminAsync();
        var memberOfSecondOnly = await fixture.NewMemberAsync(second, DepartmentRoles.Agent);

        (await admin.Client.PutAsync($"{first.Url}/teams/{team.Id}/members/{memberOfSecondOnly.Id}")).Expect(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Removing_a_department_member_removes_and_audits_their_team_memberships()
    {
        var department = await fixture.CreateDepartmentAsync();
        var team = await fixture.CreateTeamAsync(department);
        var admin = await fixture.PlatformAdminAsync();
        var agent = await fixture.NewMemberAsync(department, DepartmentRoles.Agent);
        await admin.Client.PutAsync($"{department.Url}/teams/{team.Id}/members/{agent.Id}");

        (await admin.Client.DeleteAsync($"{department.Url}/members/{agent.Id}")).Expect(HttpStatusCode.NoContent);

        Assert.Empty((await admin.Client.GetAsync($"{department.Url}/teams/{team.Id}/members")).Json.EnumerateArray());
        Assert.Equal(1, await fixture.CountAuditAsync("team_membership.removed", team.Id));
        Assert.Equal(1, await fixture.CountAuditAsync("membership.removed", agent.Id));
    }

    [Fact]
    public async Task Inactive_department_or_team_cannot_gain_new_teams_or_members()
    {
        var department = await fixture.CreateDepartmentAsync();
        var team = await fixture.CreateTeamAsync(department);
        var admin = await fixture.PlatformAdminAsync();
        var agent = await fixture.NewMemberAsync(department, DepartmentRoles.Agent);

        var off = (await admin.Client.PatchAsync($"{department.Url}/teams/{team.Id}", new { version = team.Version, isActive = false })).Expect(HttpStatusCode.OK);
        (await admin.Client.PutAsync($"{department.Url}/teams/{team.Id}/members/{agent.Id}")).Expect(HttpStatusCode.Conflict);

        var current = (await admin.Client.GetAsync(department.Url)).Version;
        (await admin.Client.PatchAsync(department.Url, new { version = current, isActive = false })).Expect(HttpStatusCode.OK);
        (await admin.Client.PostAsync($"{department.Url}/teams", new { name = "late" })).Expect(HttpStatusCode.Conflict);
        _ = off;
    }
}
