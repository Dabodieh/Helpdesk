using System.Net;
using System.Text.Json;
using Helpdesk.Host.Tests.Infrastructure;
using Helpdesk.Modules.Organisation.Contracts;

namespace Helpdesk.Host.Tests.Platform;

public sealed class PlatformApiTests(HelpdeskFixture fixture) : IClassFixture<HelpdeskFixture>
{
    [Fact]
    public async Task Platform_admin_lists_users_with_flags_search_and_cursor_paging()
    {
        var admin = await fixture.PlatformAdminAsync();
        var token = Unique.Id();
        var users = new List<TestUser>();
        for (var i = 0; i < 5; i++)
        {
            users.Add(await fixture.SignInAsync($"srch-{token}-{i}", $"Searchable {token} {i}", $"srch-{token}-{i}@example.test"));
        }

        var seen = new List<Guid>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var url = $"/api/platform/users?search={token}&limit=2" + (cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}");
            var page = (await admin.Client.GetAsync(url)).Expect(HttpStatusCode.OK).Json;
            foreach (var item in page.GetProperty("items").EnumerateArray())
            {
                seen.Add(item.GetProperty("id").GetGuid());
                Assert.False(item.GetProperty("isPlatformAdmin").GetBoolean());
                Assert.True(item.GetProperty("isActive").GetBoolean());
            }

            var next = page.GetProperty("nextCursor");
            cursor = next.ValueKind == JsonValueKind.Null ? null : next.GetString();
            pages++;
        }
        while (cursor is not null && pages < 10);

        Assert.Equal(users.Select(u => u.Id).Order(), seen.Order());
        Assert.Equal(3, pages);
        (await admin.Client.GetAsync("/api/platform/users?cursor=garbage")).Expect(HttpStatusCode.BadRequest);
        (await admin.Client.GetAsync("/api/platform/users?limit=0")).Expect(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Granting_and_revoking_platform_admin_is_audited_and_validated()
    {
        var admin = await fixture.PlatformAdminAsync();
        var user = await fixture.NewUserAsync();
        var url = $"/api/platform/users/{user.Id}/platform-admin";

        (await admin.Client.PutAsync(url, new { })).Expect(HttpStatusCode.BadRequest);
        (await admin.Client.PutAsync($"/api/platform/users/{Guid.NewGuid()}/platform-admin", new { value = true })).Expect(HttpStatusCode.NotFound);

        var granted = (await admin.Client.PutAsync(url, new { value = true })).Expect(HttpStatusCode.OK);
        Assert.True(granted.Json.GetProperty("isPlatformAdmin").GetBoolean());
        (await admin.Client.PutAsync(url, new { value = true })).Expect(HttpStatusCode.OK); // no-op, not audited again
        (await admin.Client.PutAsync(url, new { value = false })).Expect(HttpStatusCode.OK);

        Assert.Equal(2, await fixture.CountAuditAsync("user.platform_admin_changed", user.Id));
    }

    [Fact]
    public async Task Only_platform_admins_can_change_platform_admin_including_department_admins_and_self()
    {
        var department = await fixture.CreateDepartmentAsync();
        var deptAdmin = await fixture.NewMemberAsync(department, DepartmentRoles.DepartmentAdmin);
        var other = await fixture.NewUserAsync();

        (await deptAdmin.Client.PutAsync($"/api/platform/users/{other.Id}/platform-admin", new { value = true })).Expect(HttpStatusCode.Forbidden);
        (await deptAdmin.Client.PutAsync($"/api/platform/users/{deptAdmin.Id}/platform-admin", new { value = true })).Expect(HttpStatusCode.Forbidden);
        (await other.Client.PutAsync($"/api/platform/users/{other.Id}/platform-admin", new { value = true })).Expect(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Lookup_is_for_member_managers_and_platform_admins_only()
    {
        var department = await fixture.CreateDepartmentAsync();
        var token = Unique.Id();
        var target = await fixture.SignInAsync($"look-{token}", $"Lookup Target {token}", $"look-{token}@example.test");
        var deptAdmin = await fixture.NewMemberAsync(department, DepartmentRoles.DepartmentAdmin);
        var agent = await fixture.NewMemberAsync(department, DepartmentRoles.Agent);
        var outsider = await fixture.NewUserAsync();
        var admin = await fixture.PlatformAdminAsync();

        foreach (var caller in new[] { deptAdmin, admin })
        {
            var results = (await caller.Client.GetAsync($"/api/users/lookup?q={token}")).Expect(HttpStatusCode.OK).Json.EnumerateArray().ToList();
            var hit = Assert.Single(results);
            Assert.Equal(target.Id, hit.GetProperty("id").GetGuid());
            Assert.Equal($"look-{token}@example.test", hit.GetProperty("email").GetString());
        }

        (await agent.Client.GetAsync($"/api/users/lookup?q={token}")).Expect(HttpStatusCode.Forbidden);
        (await outsider.Client.GetAsync($"/api/users/lookup?q={token}")).Expect(HttpStatusCode.Forbidden);
        (await fixture.CreateAnonymousClient().GetAsync($"/api/users/lookup?q={token}")).Expect(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Lookup_validates_length_matches_case_insensitively_escapes_wildcards_and_caps_results()
    {
        var admin = await fixture.PlatformAdminAsync();
        var token = Unique.Id();
        for (var i = 0; i < 22; i++)
        {
            await fixture.SignInAsync($"cap-{token}-{i}", $"Capped {token.ToUpperInvariant()} {i}");
        }

        (await admin.Client.GetAsync("/api/users/lookup?q=ab")).Expect(HttpStatusCode.BadRequest);
        (await admin.Client.GetAsync("/api/users/lookup")).Expect(HttpStatusCode.BadRequest);
        Assert.Equal(20, (await admin.Client.GetAsync($"/api/users/lookup?q={token}")).Json.GetArrayLength());
        Assert.Equal(0, (await admin.Client.GetAsync("/api/users/lookup?q=%25%25%25")).Json.GetArrayLength()); // literal "%%%", not a wildcard
        Assert.Equal(0, (await admin.Client.GetAsync("/api/users/lookup?q=___")).Json.GetArrayLength());
    }

    [Fact]
    public async Task Lookup_returns_active_users_only()
    {
        var admin = await fixture.PlatformAdminAsync();
        var token = Unique.Id();
        var gone = await fixture.SignInAsync($"inactive-{token}", $"Gone {token}");
        await fixture.ExecuteAsync("UPDATE identity.users SET is_active = false WHERE id = @id", new Npgsql.NpgsqlParameter("id", gone.Id));

        Assert.Equal(0, (await admin.Client.GetAsync($"/api/users/lookup?q={token}")).Expect(HttpStatusCode.OK).Json.GetArrayLength());
        var listed = (await admin.Client.GetAsync($"/api/platform/users?search={token}")).Json.GetProperty("items").EnumerateArray().Single();
        Assert.False(listed.GetProperty("isActive").GetBoolean());
    }
}

/// <summary>Own database: the "last platform admin" rule needs a known set of admins.</summary>
public sealed class LastPlatformAdminTests(HelpdeskFixture fixture) : IClassFixture<HelpdeskFixture>
{
    [Fact]
    public async Task Last_platform_admin_cannot_be_removed_but_can_once_another_exists()
    {
        var first = await fixture.PlatformAdminAsync();
        var second = await fixture.NewUserAsync("second-admin");

        (await first.Client.PutAsync($"/api/platform/users/{first.Id}/platform-admin", new { value = false })).Expect(HttpStatusCode.Conflict);

        (await first.Client.PutAsync($"/api/platform/users/{second.Id}/platform-admin", new { value = true })).Expect(HttpStatusCode.OK);
        (await second.Client.PutAsync($"/api/platform/users/{first.Id}/platform-admin", new { value = false })).Expect(HttpStatusCode.OK);
        (await second.Client.PutAsync($"/api/platform/users/{second.Id}/platform-admin", new { value = false })).Expect(HttpStatusCode.Conflict);
        (await first.Client.GetAsync("/api/platform/users")).Expect(HttpStatusCode.Forbidden); // first is no longer an admin
    }

}

/// <summary>Own database again: the previous class leaves the bootstrap admin demoted.</summary>
public sealed class PlatformAdminRaceTests(HelpdeskFixture fixture) : IClassFixture<HelpdeskFixture>
{
    [Fact]
    public async Task Two_admins_removing_each_other_concurrently_cannot_leave_none()
    {
        var admin = await fixture.PlatformAdminAsync();
        var one = await fixture.NewUserAsync("race-a");
        var two = await fixture.NewUserAsync("race-b");
        await admin.Client.PutAsync($"/api/platform/users/{one.Id}/platform-admin", new { value = true });
        await admin.Client.PutAsync($"/api/platform/users/{two.Id}/platform-admin", new { value = true });
        // Leave exactly one and two as admins plus the bootstrap admin, then remove bootstrap so only one and two remain.
        await admin.Client.PutAsync($"/api/platform/users/{admin.Id}/platform-admin", new { value = false });

        var results = await Task.WhenAll(
            one.Client.PutAsync($"/api/platform/users/{two.Id}/platform-admin", new { value = false }),
            two.Client.PutAsync($"/api/platform/users/{one.Id}/platform-admin", new { value = false }));

        Assert.Equal(1, results.Count(r => r.Status == HttpStatusCode.OK));
        // The loser is either refused by the last-admin rule (409) or, if it started after the winner committed, no longer an admin (403).
        Assert.Single(results, r => r.Status is HttpStatusCode.Conflict or HttpStatusCode.Forbidden);
        var active = await fixture.ScalarAsync<long>("SELECT count(*) FROM identity.users WHERE is_platform_admin AND is_active");
        Assert.Equal(1, active);
    }
}
