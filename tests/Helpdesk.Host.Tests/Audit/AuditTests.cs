using System.Net;
using Helpdesk.Host.Tests.Infrastructure;
using Helpdesk.Modules.Audit.Contracts;
using Helpdesk.Modules.Audit.Persistence;
using Helpdesk.Modules.Audit.Services;
using Helpdesk.Modules.Organisation.Contracts;
using Helpdesk.Modules.Organisation.Domain;
using Helpdesk.Modules.Organisation.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Helpdesk.Host.Tests.Audit;

public sealed class AuditTests(HelpdeskFixture fixture) : IClassFixture<HelpdeskFixture>
{
    private static readonly string[] PlatformCategories = ["organisation", "identity"];

    private static AuditEntry Entry(string action = "department.updated") =>
        new(AuditCategories.Organisation, action, "department", Guid.NewGuid(), null, null, null);

    [Fact]
    public async Task Writer_requires_an_explicit_transaction()
    {
        await fixture.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<OrganisationDbContext>();
            var writer = sp.GetRequiredService<IAuditWriter>();
            await Assert.ThrowsAsync<InvalidOperationException>(() => writer.WriteAsync(Entry(), db));
        });
    }

    [Fact]
    public async Task State_change_and_audit_event_roll_back_together()
    {
        var departmentId = Guid.NewGuid();
        var marker = $"rollback-{Unique.Id()}";
        await fixture.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<OrganisationDbContext>();
            var writer = sp.GetRequiredService<IAuditWriter>();
            await using var tx = await db.Database.BeginTransactionAsync();
            db.Departments.Add(new Department { Id = departmentId, Key = Unique.Key(), Name = marker, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
            await writer.WriteAsync(new AuditEntry(AuditCategories.Organisation, "department.created", "department", departmentId, departmentId, null, null), db);
            await tx.RollbackAsync();
        });

        Assert.Equal(0, await fixture.ScalarAsync<long>("SELECT count(*) FROM organisation.departments WHERE id = @id", new NpgsqlParameter("id", departmentId)));
        Assert.Equal(0, await fixture.CountAuditAsync("department.created", departmentId));
    }

    [Fact]
    public async Task State_change_and_audit_event_commit_together()
    {
        var departmentId = Guid.NewGuid();
        await fixture.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<OrganisationDbContext>();
            var writer = sp.GetRequiredService<IAuditWriter>();
            await using var tx = await db.Database.BeginTransactionAsync();
            db.Departments.Add(new Department { Id = departmentId, Key = Unique.Key(), Name = $"commit-{Unique.Id()}", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
            await writer.WriteAsync(new AuditEntry(AuditCategories.Organisation, "department.created", "department", departmentId, departmentId, null, null, Next: new { key = "K" }), db);
            await tx.CommitAsync();
        });

        Assert.Equal(1, await fixture.CountAuditAsync("department.created", departmentId));
    }

    [Fact]
    public async Task A_failing_audit_write_rolls_back_the_api_mutation()
    {
        var factory = fixture.Factory.WithWebHostBuilder(b => b.ConfigureServices(services =>
        {
            services.AddScoped<IAuditWriter>(_ => new FailingFor("department.created", new AuditWriter(TimeProvider.System)));
        }));
        var admin = await fixture.SignInAsync("platform-admin", "Platform Admin", factory: factory);
        var key = Unique.Key();

        var response = await admin.Client.PostAsync("/api/departments", new { key, name = $"Atomic {Unique.Id()}" });

        Assert.Equal(HttpStatusCode.InternalServerError, response.Status);
        Assert.Equal(0, await fixture.ScalarAsync<long>("SELECT count(*) FROM organisation.departments WHERE key = @k", new NpgsqlParameter("k", key)));
    }

    [Fact]
    public async Task A_failing_audit_write_rolls_back_membership_changes()
    {
        var department = await fixture.CreateDepartmentAsync();
        var user = await fixture.NewUserAsync();
        var factory = fixture.Factory.WithWebHostBuilder(b => b.ConfigureServices(services =>
        {
            services.AddScoped<IAuditWriter>(_ => new FailingFor("membership.added", new AuditWriter(TimeProvider.System)));
        }));
        var admin = await fixture.SignInAsync("platform-admin", "Platform Admin", factory: factory);

        var response = await admin.Client.PutAsync($"{department.Url}/members/{user.Id}", new { role = DepartmentRoles.Agent });

        Assert.Equal(HttpStatusCode.InternalServerError, response.Status);
        Assert.Equal(0, await fixture.ScalarAsync<long>("SELECT count(*) FROM organisation.department_memberships WHERE user_id = @u", new NpgsqlParameter("u", user.Id)));
    }

    [Fact]
    public async Task Audit_table_rejects_update_delete_and_truncate_at_the_database()
    {
        await fixture.CreateDepartmentAsync();
        foreach (var sql in new[]
                 {
                     "UPDATE audit.audit_events SET action = 'tampered'",
                     "DELETE FROM audit.audit_events",
                     "TRUNCATE audit.audit_events",
                 })
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(() => fixture.ExecuteAsync(sql));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
        }
    }

    [Fact]
    public async Task Audit_context_cannot_save_changes()
    {
        await fixture.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<AuditDbContext>();
            await Assert.ThrowsAsync<NotSupportedException>(() => db.SaveChangesAsync());
        });
    }

    [Fact]
    public async Task There_is_no_write_api_for_audit()
    {
        var department = await fixture.CreateDepartmentAsync();
        var admin = await fixture.PlatformAdminAsync();
        foreach (var method in new[] { HttpMethod.Post, HttpMethod.Put, HttpMethod.Patch, HttpMethod.Delete })
        {
            (await admin.Client.SendAsync(method, $"{department.Url}/audit", new { })).Expect(HttpStatusCode.MethodNotAllowed);
            (await admin.Client.SendAsync(method, "/api/platform/audit", new { })).Expect(HttpStatusCode.MethodNotAllowed);
        }
    }

    [Fact]
    public async Task Department_audit_is_limited_to_team_leads_and_admins_and_hidden_from_outsiders()
    {
        var department = await fixture.CreateDepartmentAsync();
        var lead = await fixture.NewMemberAsync(department, DepartmentRoles.TeamLead);
        var agent = await fixture.NewMemberAsync(department, DepartmentRoles.Agent);
        var viewer = await fixture.NewMemberAsync(department, DepartmentRoles.Viewer);
        var outsider = await fixture.NewUserAsync();
        var deptAdmin = await fixture.NewMemberAsync(department, DepartmentRoles.DepartmentAdmin);

        (await lead.Client.GetAsync($"{department.Url}/audit")).Expect(HttpStatusCode.OK);
        (await deptAdmin.Client.GetAsync($"{department.Url}/audit")).Expect(HttpStatusCode.OK);
        (await agent.Client.GetAsync($"{department.Url}/audit")).Expect(HttpStatusCode.Forbidden);
        (await viewer.Client.GetAsync($"{department.Url}/audit")).Expect(HttpStatusCode.Forbidden);
        (await outsider.Client.GetAsync($"{department.Url}/audit")).Expect(HttpStatusCode.NotFound);
        (await outsider.Client.GetAsync($"/api/departments/{Guid.NewGuid()}/audit")).Expect(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Department_audit_has_the_contract_shape_and_never_shows_other_departments()
    {
        var department = await fixture.CreateDepartmentAsync();
        var other = await fixture.CreateDepartmentAsync();
        var admin = await fixture.PlatformAdminAsync();
        (await admin.Client.PatchAsync(department.Url, new { version = department.Version, name = $"Audited {Unique.Id()}" })).Expect(HttpStatusCode.OK);

        var page = (await admin.Client.GetAsync($"{department.Url}/audit")).Expect(HttpStatusCode.OK).Json;
        var items = page.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(["department.updated", "department.created"], items.Select(i => i.GetProperty("action").GetString()));
        Assert.All(items, i => Assert.Equal(department.Id, i.GetProperty("objectId").GetGuid()));

        var updated = items[0];
        Assert.Equal(admin.Id, updated.GetProperty("actor").GetProperty("id").GetGuid());
        Assert.Equal("Platform Admin", updated.GetProperty("actor").GetProperty("displayName").GetString());
        Assert.Equal("department", updated.GetProperty("objectType").GetString());
        Assert.Equal("web", updated.GetProperty("source").GetString());
        Assert.Equal(department.Name, updated.GetProperty("previous").GetProperty("name").GetString());
        Assert.NotEqual(department.Name, updated.GetProperty("next").GetProperty("name").GetString());
        Assert.True(DateTimeOffset.TryParse(updated.GetProperty("occurredAt").GetString(), out var at) && at.Offset == TimeSpan.Zero);
        Assert.DoesNotContain(other.Id.ToString(), page.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cursor_paging_walks_all_events_newest_first_without_duplicates()
    {
        var department = await fixture.CreateDepartmentAsync();
        var admin = await fixture.PlatformAdminAsync();
        for (var i = 0; i < 6; i++)
        {
            await fixture.CreateTeamAsync(department);
        }

        var seen = new List<Guid>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var url = $"{department.Url}/audit?limit=2" + (cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}");
            var page = (await admin.Client.GetAsync(url)).Expect(HttpStatusCode.OK).Json;
            seen.AddRange(page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()));
            var next = page.GetProperty("nextCursor");
            cursor = next.ValueKind == System.Text.Json.JsonValueKind.Null ? null : next.GetString();
            pages++;
        }
        while (cursor is not null && pages < 20);

        Assert.Equal(7, seen.Count); // department.created + 6 team.created
        Assert.Equal(seen.Count, seen.Distinct().Count());
        Assert.Equal(4, pages);
        var all = (await admin.Client.GetAsync($"{department.Url}/audit?limit=200")).Json.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToList();
        Assert.Equal(all, seen);
    }

    [Theory]
    [InlineData("limit=0")]
    [InlineData("limit=201")]
    [InlineData("cursor=not-a-cursor")]
    [InlineData("cursor=MXwyMw")]
    public async Task Paging_parameters_are_validated(string query)
    {
        var department = await fixture.CreateDepartmentAsync();
        var admin = await fixture.PlatformAdminAsync();
        (await admin.Client.GetAsync($"{department.Url}/audit?{query}")).Expect(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Platform_audit_shows_organisation_and_identity_events_only_to_platform_admins()
    {
        var department = await fixture.CreateDepartmentAsync();
        var user = await fixture.NewUserAsync("audited");
        var admin = await fixture.PlatformAdminAsync();

        var items = (await admin.Client.GetAsync("/api/platform/audit?limit=200")).Expect(HttpStatusCode.OK).Json.GetProperty("items").EnumerateArray().ToList();
        var categories = items.Select(i => i.GetProperty("category").GetString()).Distinct().ToList();
        Assert.All(categories, c => Assert.Contains(c, PlatformCategories));
        Assert.Contains(items, i => i.GetProperty("action").GetString() == "department.created" && i.GetProperty("objectId").GetGuid() == department.Id);
        var provisioned = Assert.Single(items, i => i.GetProperty("action").GetString() == "user.provisioned" && i.GetProperty("objectId").GetGuid() == user.Id);
        Assert.DoesNotContain("@", provisioned.GetRawText(), StringComparison.Ordinal); // no email/PII in audit values

        (await user.Client.GetAsync("/api/platform/audit")).Expect(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Every_audit_action_in_the_contract_is_emitted_by_its_operation()
    {
        var admin = await fixture.PlatformAdminAsync();
        var department = await fixture.CreateDepartmentAsync();                                                   // department.created
        var renamed = (await admin.Client.PatchAsync(department.Url, new { version = department.Version, name = $"R {Unique.Id()}" })).Expect(HttpStatusCode.OK); // department.updated
        var off = (await admin.Client.PatchAsync(department.Url, new { version = renamed.Version, isActive = false })).Expect(HttpStatusCode.OK);              // department.deactivated
        (await admin.Client.PatchAsync(department.Url, new { version = off.Version, isActive = true })).Expect(HttpStatusCode.OK);                           // department.activated
        var team = await fixture.CreateTeamAsync(department);                                                      // team.created
        var teamRenamed = (await admin.Client.PatchAsync($"{department.Url}/teams/{team.Id}", new { version = team.Version, name = $"T {Unique.Id()}" })).Expect(HttpStatusCode.OK); // team.updated
        var teamOff = (await admin.Client.PatchAsync($"{department.Url}/teams/{team.Id}", new { version = teamRenamed.Version, isActive = false })).Expect(HttpStatusCode.OK); // team.deactivated
        (await admin.Client.PatchAsync($"{department.Url}/teams/{team.Id}", new { version = teamOff.Version, isActive = true })).Expect(HttpStatusCode.OK);               // team.activated
        var user = await fixture.NewUserAsync();                                                                   // user.provisioned
        await fixture.AddMemberAsync(department, user, DepartmentRoles.Agent);                                     // membership.added
        (await admin.Client.PutAsync($"{department.Url}/members/{user.Id}", new { role = DepartmentRoles.TeamLead })).Expect(HttpStatusCode.OK); // membership.role_changed
        (await admin.Client.PutAsync($"{department.Url}/teams/{team.Id}/members/{user.Id}")).Expect(HttpStatusCode.NoContent);                // team_membership.added
        (await admin.Client.DeleteAsync($"{department.Url}/teams/{team.Id}/members/{user.Id}")).Expect(HttpStatusCode.NoContent);             // team_membership.removed
        (await admin.Client.DeleteAsync($"{department.Url}/members/{user.Id}")).Expect(HttpStatusCode.NoContent);                             // membership.removed
        (await admin.Client.PutAsync($"/api/platform/users/{user.Id}/platform-admin", new { value = true })).Expect(HttpStatusCode.OK);        // user.platform_admin_changed
        (await admin.Client.PutAsync($"/api/platform/users/{user.Id}/platform-admin", new { value = false })).Expect(HttpStatusCode.OK);

        var actions = (await admin.Client.GetAsync($"{department.Url}/audit?limit=200")).Json.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("action").GetString()).ToHashSet();
        string[] departmentActions =
        [
            "department.created", "department.updated", "department.deactivated", "department.activated",
            "team.created", "team.updated", "team.deactivated", "team.activated",
            "membership.added", "membership.role_changed", "membership.removed",
            "team_membership.added", "team_membership.removed",
        ];
        Assert.All(departmentActions, a => Assert.Contains(a, actions));
        Assert.Equal(1, await fixture.CountAuditAsync("user.provisioned", user.Id));
        Assert.Equal(2, await fixture.CountAuditAsync("user.platform_admin_changed", user.Id));
    }

    private sealed class FailingFor(string action, IAuditWriter inner) : IAuditWriter
    {
        public Task WriteAsync(AuditEntry entry, DbContext caller, CancellationToken cancellationToken = default) =>
            entry.Action == action ? throw new InvalidOperationException("simulated audit failure") : inner.WriteAsync(entry, caller, cancellationToken);
    }
}
