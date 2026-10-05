using Helpdesk.Host.Tests.Infrastructure;
using Helpdesk.Modules.Organisation.Authorization;
using Helpdesk.Modules.Organisation.Contracts;
using Helpdesk.Modules.Organisation.Persistence;
using Helpdesk.SharedKernel.Authorization;
using Helpdesk.SharedKernel.Security;
using Microsoft.Extensions.DependencyInjection;

namespace Helpdesk.Host.Tests.Organisation;

/// <summary>IAuthorizer.AccessibleDepartments / HasInAnyDepartment against real memberships (service level, stubbed current user).</summary>
public sealed class AuthorizerQueryTests(HelpdeskFixture fixture) : IClassFixture<HelpdeskFixture>
{
    private sealed class StubUser(Guid id, bool platformAdmin) : ICurrentUser
    {
        public bool IsAuthenticated => true;

        public Guid UserId { get; } = id;

        public string DisplayName => "stub";

        public string? Email => null;

        public bool IsPlatformAdmin { get; } = platformAdmin;
    }

    private async Task<T> WithAuthorizerAsync<T>(Guid userId, bool platformAdmin, Func<IAuthorizer, Task<T>> action)
    {
        T result = default!;
        await fixture.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<OrganisationDbContext>();
            var user = new StubUser(userId, platformAdmin);
            var authorizer = new Authorizer(null!, null!, user, new MembershipCache(db, user), db);
            result = await action(authorizer);
        });
        return result;
    }

    [Fact]
    public async Task Accessible_departments_follow_roles_and_permission_kind()
    {
        var d1 = await fixture.CreateDepartmentAsync();
        var d2 = await fixture.CreateDepartmentAsync();
        var d3 = await fixture.CreateDepartmentAsync();
        var user = await fixture.NewUserAsync();
        await fixture.AddMemberAsync(d1, user, DepartmentRoles.Agent);
        await fixture.AddMemberAsync(d2, user, DepartmentRoles.TeamLead);

        var audit = await WithAuthorizerAsync(user.Id, false, a => a.AccessibleDepartmentsAsync(PermissionCodes.DepartmentAuditRead));
        var tickets = await WithAuthorizerAsync(user.Id, false, a => a.AccessibleDepartmentsAsync(PermissionCodes.TicketsRead));
        var manage = await WithAuthorizerAsync(user.Id, false, a => a.AccessibleDepartmentsAsync(PermissionCodes.DepartmentManage));

        Assert.Equal([d2.Id], audit);
        Assert.Equal(new[] { d1.Id, d2.Id }.Order(), tickets.Order());
        Assert.Empty(manage);
        Assert.DoesNotContain(d3.Id, tickets);
    }

    [Fact]
    public async Task Platform_admin_flag_widens_administrative_but_never_content_permissions()
    {
        var d1 = await fixture.CreateDepartmentAsync();
        var d2 = await fixture.CreateDepartmentAsync();
        var admin = await fixture.NewUserAsync();
        await fixture.AddMemberAsync(d1, admin, DepartmentRoles.Viewer);

        var administrative = await WithAuthorizerAsync(admin.Id, true, a => a.AccessibleDepartmentsAsync(PermissionCodes.DepartmentManage));
        var content = await WithAuthorizerAsync(admin.Id, true, a => a.AccessibleDepartmentsAsync(PermissionCodes.TicketsRead));

        Assert.Contains(d1.Id, administrative);
        Assert.Contains(d2.Id, administrative);
        Assert.Equal([d1.Id], content); // only the membership
    }

    [Fact]
    public async Task Has_in_any_department_checks_roles_and_the_flag_for_administrative_permissions()
    {
        var d1 = await fixture.CreateDepartmentAsync();
        var lead = await fixture.NewMemberAsync(d1, DepartmentRoles.TeamLead);
        var deptAdmin = await fixture.NewMemberAsync(d1, DepartmentRoles.DepartmentAdmin);
        var nobody = await fixture.NewUserAsync();

        Assert.False(await WithAuthorizerAsync(lead.Id, false, a => a.HasInAnyDepartmentAsync(PermissionCodes.DepartmentMembersManage)));
        Assert.True(await WithAuthorizerAsync(deptAdmin.Id, false, a => a.HasInAnyDepartmentAsync(PermissionCodes.DepartmentMembersManage)));
        Assert.False(await WithAuthorizerAsync(nobody.Id, false, a => a.HasInAnyDepartmentAsync(PermissionCodes.DepartmentMembersManage)));
        Assert.True(await WithAuthorizerAsync(nobody.Id, true, a => a.HasInAnyDepartmentAsync(PermissionCodes.DepartmentMembersManage)));
        Assert.False(await WithAuthorizerAsync(nobody.Id, true, a => a.HasInAnyDepartmentAsync(PermissionCodes.TicketsRead)));
    }
}
