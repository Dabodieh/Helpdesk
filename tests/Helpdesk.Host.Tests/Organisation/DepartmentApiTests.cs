using System.Net;
using System.Text.Json;
using Helpdesk.Host.Tests.Infrastructure;
using Helpdesk.Modules.Organisation.Contracts;

namespace Helpdesk.Host.Tests.Organisation;

public sealed class DepartmentApiTests(HelpdeskFixture fixture) : IClassFixture<HelpdeskFixture>
{
    [Theory]
    [InlineData("a")]
    [InlineData("ab")]
    [InlineData("1AB")]
    [InlineData("TOOLONGKEY1")]
    [InlineData("AB-C")]
    [InlineData("")]
    public async Task Create_rejects_invalid_keys(string key)
    {
        var admin = await fixture.PlatformAdminAsync();
        var response = (await admin.Client.PostAsync("/api/departments", new { key, name = $"N {Unique.Id()}" })).Expect(HttpStatusCode.BadRequest);
        Assert.True(response.Json.GetProperty("errors").TryGetProperty("key", out _));
    }

    [Fact]
    public async Task Create_requires_a_name()
    {
        var admin = await fixture.PlatformAdminAsync();
        var response = (await admin.Client.PostAsync("/api/departments", new { key = Unique.Key(), name = "  " })).Expect(HttpStatusCode.BadRequest);
        Assert.True(response.Json.GetProperty("errors").TryGetProperty("name", out _));
    }

    [Fact]
    public async Task Create_returns_201_with_location_and_defaults()
    {
        var admin = await fixture.PlatformAdminAsync();
        var key = Unique.Key();
        var response = (await admin.Client.PostAsync("/api/departments", new { key, name = $"IT {Unique.Id()}", description = "  helpdesk  " })).Expect(HttpStatusCode.Created);
        Assert.Equal($"/api/departments/{response.Id}", response.Message.Headers.Location?.ToString());
        Assert.Equal(key, response.Json.GetProperty("key").GetString());
        Assert.True(response.Json.GetProperty("isActive").GetBoolean());
        Assert.Equal("helpdesk", response.Json.GetProperty("description").GetString());
        Assert.Equal(JsonValueKind.Null, response.Json.GetProperty("myRole").ValueKind);
    }

    [Fact]
    public async Task Create_rejects_duplicate_key_and_name_case_insensitively()
    {
        var admin = await fixture.PlatformAdminAsync();
        var department = await fixture.CreateDepartmentAsync();
        (await admin.Client.PostAsync("/api/departments", new { key = department.Key.ToLowerInvariant(), name = $"Other {Unique.Id()}" }))
            .Expect(HttpStatusCode.BadRequest); // lower-case key fails the format rule
        (await admin.Client.PostAsync("/api/departments", new { key = department.Key, name = $"Other {Unique.Id()}" })).Expect(HttpStatusCode.Conflict);
        (await admin.Client.PostAsync("/api/departments", new { key = Unique.Key(), name = department.Name.ToUpperInvariant() })).Expect(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Create_needs_platform_departments_manage()
    {
        var department = await fixture.CreateDepartmentAsync();
        var deptAdmin = await fixture.NewMemberAsync(department, DepartmentRoles.DepartmentAdmin);
        (await deptAdmin.Client.PostAsync("/api/departments", new { key = Unique.Key(), name = $"X {Unique.Id()}" })).Expect(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Unauthenticated_requests_get_401()
    {
        var anonymous = fixture.CreateAnonymousClient();
        await anonymous.RefreshCsrfAsync();
        (await anonymous.GetAsync("/api/departments")).Expect(HttpStatusCode.Unauthorized);
        (await anonymous.GetAsync("/api/me")).Expect(HttpStatusCode.Unauthorized);
        (await anonymous.PostAsync("/api/departments", new { key = Unique.Key(), name = "x" })).Expect(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task List_shows_only_own_departments_to_members_and_all_to_platform_admins()
    {
        var mine = await fixture.CreateDepartmentAsync();
        var other = await fixture.CreateDepartmentAsync();
        var agent = await fixture.NewMemberAsync(mine, DepartmentRoles.Agent);

        var memberList = (await agent.Client.GetAsync("/api/departments")).Expect(HttpStatusCode.OK).Json.EnumerateArray().ToList();
        Assert.Single(memberList);
        Assert.Equal(mine.Id, memberList[0].GetProperty("id").GetGuid());
        Assert.Equal(DepartmentRoles.Agent, memberList[0].GetProperty("myRole").GetString());

        var admin = await fixture.PlatformAdminAsync();
        var all = (await admin.Client.GetAsync("/api/departments")).Expect(HttpStatusCode.OK).Json.EnumerateArray().Select(d => d.GetProperty("id").GetGuid()).ToList();
        Assert.Contains(mine.Id, all);
        Assert.Contains(other.Id, all);
    }

    [Fact]
    public async Task Get_is_404_for_outsiders_and_for_unknown_ids_with_identical_responses()
    {
        var department = await fixture.CreateDepartmentAsync();
        var outsider = await fixture.NewUserAsync();
        var member = await fixture.NewMemberAsync(department, DepartmentRoles.Viewer);

        (await member.Client.GetAsync(department.Url)).Expect(HttpStatusCode.OK);
        var hidden = (await outsider.Client.GetAsync(department.Url)).Expect(HttpStatusCode.NotFound);
        var unknown = (await outsider.Client.GetAsync($"/api/departments/{Guid.NewGuid()}")).Expect(HttpStatusCode.NotFound);
        Assert.Equal(hidden.Json.GetProperty("title").GetString(), unknown.Json.GetProperty("title").GetString());
        Assert.Equal(hidden.Body.Length, unknown.Body.Length); // same shape: no existence oracle
    }

    [Fact]
    public async Task Patch_name_and_description_need_department_manage()
    {
        var department = await fixture.CreateDepartmentAsync();
        var deptAdmin = await fixture.NewMemberAsync(department, DepartmentRoles.DepartmentAdmin);
        var agent = await fixture.NewMemberAsync(department, DepartmentRoles.Agent);
        var outsider = await fixture.NewUserAsync();
        var newName = $"Renamed {Unique.Id()}";

        (await agent.Client.PatchAsync(department.Url, new { version = department.Version, name = newName })).Expect(HttpStatusCode.Forbidden);
        (await outsider.Client.PatchAsync(department.Url, new { version = department.Version, name = newName })).Expect(HttpStatusCode.NotFound);
        var ok = (await deptAdmin.Client.PatchAsync(department.Url, new { version = department.Version, name = newName, description = "d" })).Expect(HttpStatusCode.OK);
        Assert.Equal(newName, ok.Json.GetProperty("name").GetString());
        Assert.NotEqual(department.Version, ok.Version);
    }

    [Fact]
    public async Task Patch_is_active_needs_platform_departments_manage_and_is_audited()
    {
        var department = await fixture.CreateDepartmentAsync();
        var deptAdmin = await fixture.NewMemberAsync(department, DepartmentRoles.DepartmentAdmin);
        var admin = await fixture.PlatformAdminAsync();

        var current = (await deptAdmin.Client.GetAsync(department.Url)).Expect(HttpStatusCode.OK).Version;
        (await deptAdmin.Client.PatchAsync(department.Url, new { version = current, isActive = false })).Expect(HttpStatusCode.Forbidden);

        var deactivated = (await admin.Client.PatchAsync(department.Url, new { version = current, isActive = false })).Expect(HttpStatusCode.OK);
        Assert.False(deactivated.Json.GetProperty("isActive").GetBoolean());
        var reactivated = (await admin.Client.PatchAsync(department.Url, new { version = deactivated.Version, isActive = true })).Expect(HttpStatusCode.OK);
        Assert.True(reactivated.Json.GetProperty("isActive").GetBoolean());

        Assert.Equal(1, await fixture.CountAuditAsync("department.deactivated", department.Id));
        Assert.Equal(1, await fixture.CountAuditAsync("department.activated", department.Id));
    }

    [Fact]
    public async Task Patch_validates_version_and_body()
    {
        var department = await fixture.CreateDepartmentAsync();
        var admin = await fixture.PlatformAdminAsync();
        (await admin.Client.PatchAsync(department.Url, new { name = "x" })).Expect(HttpStatusCode.BadRequest);
        (await admin.Client.PatchAsync(department.Url, new { version = "abc", name = "x" })).Expect(HttpStatusCode.BadRequest);
        (await admin.Client.PatchAsync(department.Url, new { version = department.Version })).Expect(HttpStatusCode.BadRequest);
        (await admin.Client.PatchAsync(department.Url, new { version = department.Version, name = new string('x', 101) })).Expect(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Patch_with_stale_version_returns_409_and_first_writer_wins()
    {
        var department = await fixture.CreateDepartmentAsync();
        var admin = await fixture.PlatformAdminAsync();

        (await admin.Client.PatchAsync(department.Url, new { version = department.Version, name = $"First {Unique.Id()}" })).Expect(HttpStatusCode.OK);
        (await admin.Client.PatchAsync(department.Url, new { version = department.Version, name = $"Second {Unique.Id()}" })).Expect(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Concurrent_patches_with_the_same_version_yield_exactly_one_success()
    {
        var department = await fixture.CreateDepartmentAsync();
        var admin = await fixture.PlatformAdminAsync();

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(i =>
            admin.Client.PatchAsync(department.Url, new { version = department.Version, name = $"Race {i} {Unique.Id()}" })));

        Assert.Equal(1, results.Count(r => r.Status == HttpStatusCode.OK));
        Assert.Equal(5, results.Count(r => r.Status == HttpStatusCode.Conflict));
    }

    [Fact]
    public async Task Patch_to_a_taken_name_is_409()
    {
        var first = await fixture.CreateDepartmentAsync();
        var second = await fixture.CreateDepartmentAsync();
        var admin = await fixture.PlatformAdminAsync();
        (await admin.Client.PatchAsync(second.Url, new { version = second.Version, name = first.Name.ToLowerInvariant() })).Expect(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Patch_description_can_be_cleared_with_an_empty_string()
    {
        var department = await fixture.CreateDepartmentAsync();
        var admin = await fixture.PlatformAdminAsync();
        var set = (await admin.Client.PatchAsync(department.Url, new { version = department.Version, description = "hello" })).Expect(HttpStatusCode.OK);
        var cleared = (await admin.Client.PatchAsync(department.Url, new { version = set.Version, description = "" })).Expect(HttpStatusCode.OK);
        Assert.Equal(JsonValueKind.Null, cleared.Json.GetProperty("description").ValueKind);
    }

    [Fact]
    public async Task Mutations_without_csrf_token_are_rejected()
    {
        var admin = await fixture.PlatformAdminAsync();
        var response = await admin.Client.SendAsync(HttpMethod.Post, "/api/departments", new { key = Unique.Key(), name = "x" }, includeCsrf: false);
        response.Expect(HttpStatusCode.BadRequest);
    }
}
