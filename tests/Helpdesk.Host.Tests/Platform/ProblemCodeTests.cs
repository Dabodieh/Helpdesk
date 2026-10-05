using System.Net;
using Helpdesk.Host.Tests.Infrastructure;
using Helpdesk.Modules.Organisation.Contracts;

namespace Helpdesk.Host.Tests.Platform;

/// <summary>Clients branch on the structured <c>code</c> problem extension, never on message text.</summary>
public sealed class ProblemCodeTests(HelpdeskFixture fixture) : IClassFixture<HelpdeskFixture>
{
    private static string Code(ApiResponse response) => response.Json.GetProperty("code").GetString()!;

    [Fact]
    public async Task Stale_version_is_version_conflict()
    {
        var admin = await fixture.PlatformAdminAsync();
        var department = await fixture.CreateDepartmentAsync();
        (await admin.Client.PatchAsync(department.Url, new { version = department.Version, name = $"Renamed {Unique.Id()}" })).Expect(HttpStatusCode.OK);
        var stale = (await admin.Client.PatchAsync(department.Url, new { version = department.Version, name = $"Again {Unique.Id()}" })).Expect(HttpStatusCode.Conflict);
        Assert.Equal("version_conflict", Code(stale));
    }

    [Fact]
    public async Task Duplicate_key_is_duplicate()
    {
        var admin = await fixture.PlatformAdminAsync();
        var department = await fixture.CreateDepartmentAsync();
        var response = (await admin.Client.PostAsync("/api/departments", new { key = department.Key, name = $"Other {Unique.Id()}" })).Expect(HttpStatusCode.Conflict);
        Assert.Equal("duplicate", Code(response));
    }

    [Fact]
    public async Task Last_department_admin_is_last_admin()
    {
        var department = await fixture.CreateDepartmentAsync();
        var admin = await fixture.NewMemberAsync(department, DepartmentRoles.DepartmentAdmin);
        var response = (await admin.Client.DeleteAsync($"{department.Url}/members/{admin.Id}")).Expect(HttpStatusCode.Conflict);
        Assert.Equal("last_admin", Code(response));
    }

    [Fact]
    public async Task Missing_csrf_token_is_csrf_invalid()
    {
        var user = await fixture.NewUserAsync();
        var response = (await user.Client.SendAsync(HttpMethod.Post, "/api/departments", new { key = Unique.Key(), name = "x" }, includeCsrf: false)).Expect(HttpStatusCode.BadRequest);
        Assert.Equal("csrf_invalid", Code(response));
    }
}
