using System.Reflection;
using Helpdesk.Modules.Organisation.Authorization;
using Helpdesk.Modules.Organisation.Contracts;
using Helpdesk.SharedKernel.Authorization;

namespace Helpdesk.Host.Tests.Organisation;

/// <summary>Pure tests of the permission catalogue, role mapping and decision function (no database).</summary>
public sealed class PermissionModelTests
{
    private static readonly string[] AllCodes = typeof(PermissionCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.IsLiteral)
        .Select(f => (string)f.GetRawConstantValue()!)
        .ToArray();

    [Fact]
    public void Every_permission_code_is_classified_exactly_once()
    {
        Assert.Equal(AllCodes.Order(), PermissionCatalogue.All.Select(p => p.Code).Order());
        Assert.Equal(AllCodes.Length, PermissionCatalogue.All.Select(p => p.Code).Distinct().Count());
    }

    [Fact]
    public void Classification_matches_the_documented_catalogue()
    {
        Assert.Equal(PermissionKind.Content, PermissionCatalogue.Get(PermissionCodes.TicketsRead).Kind);
        Assert.All(
            [PermissionCodes.PlatformAuditRead, PermissionCodes.PlatformDepartmentsManage, PermissionCodes.PlatformUsersManage],
            c => Assert.Equal(PermissionKind.Platform, PermissionCatalogue.Get(c).Kind));
        Assert.All(
            [PermissionCodes.DepartmentRead, PermissionCodes.DepartmentManage, PermissionCodes.DepartmentMembersManage, PermissionCodes.TeamsManage, PermissionCodes.DepartmentAuditRead],
            c => Assert.Equal(PermissionKind.Administrative, PermissionCatalogue.Get(c).Kind));
    }

    [Fact]
    public void Platform_admin_never_satisfies_a_content_permission()
    {
        foreach (var permission in PermissionCatalogue.All.Where(p => p.Kind == PermissionKind.Content))
        {
            foreach (var exists in new[] { true, false })
            {
                Assert.NotEqual(EvaluationOutcome.Allowed, PermissionEvaluator.Evaluate(permission, isPlatformAdmin: true, role: null, departmentExists: exists));
            }
        }
    }

    [Fact]
    public void Platform_admin_without_membership_gets_404_for_content_like_any_outsider()
    {
        var tickets = PermissionCatalogue.Get(PermissionCodes.TicketsRead);
        Assert.Equal(EvaluationOutcome.NotFound, PermissionEvaluator.Evaluate(tickets, true, null, true));
        Assert.Equal(EvaluationOutcome.NotFound, PermissionEvaluator.Evaluate(tickets, false, null, true));
    }

    [Fact]
    public void Platform_admin_satisfies_administrative_permissions_when_the_department_exists()
    {
        foreach (var permission in PermissionCatalogue.All.Where(p => p.Kind == PermissionKind.Administrative))
        {
            Assert.Equal(EvaluationOutcome.Allowed, PermissionEvaluator.Evaluate(permission, true, null, true));
            Assert.Equal(EvaluationOutcome.NotFound, PermissionEvaluator.Evaluate(permission, true, null, false));
        }
    }

    [Fact]
    public void Platform_permissions_need_the_flag_and_answer_403_otherwise()
    {
        foreach (var permission in PermissionCatalogue.All.Where(p => p.Kind == PermissionKind.Platform))
        {
            Assert.Equal(EvaluationOutcome.Allowed, PermissionEvaluator.Evaluate(permission, true, null, false));
            Assert.Equal(EvaluationOutcome.Forbidden, PermissionEvaluator.Evaluate(permission, false, null, false));
            Assert.Equal(EvaluationOutcome.Forbidden, PermissionEvaluator.Evaluate(permission, false, DepartmentRoles.DepartmentAdmin, true));
        }
    }

    [Fact]
    public void No_role_grants_a_platform_permission()
    {
        foreach (var role in RoleCatalogue.Roles)
        {
            Assert.DoesNotContain(RoleCatalogue.PermissionsOf(role), p => PermissionCatalogue.Get(p).Kind == PermissionKind.Platform);
        }
    }

    [Fact]
    public void Roles_are_cumulative_and_every_role_reads_tickets()
    {
        for (var i = 1; i < RoleCatalogue.Roles.Count; i++)
        {
            var lower = RoleCatalogue.PermissionsOf(RoleCatalogue.Roles[i - 1]);
            var higher = RoleCatalogue.PermissionsOf(RoleCatalogue.Roles[i]);
            Assert.True(lower.All(higher.Contains), $"{RoleCatalogue.Roles[i]} must include everything {RoleCatalogue.Roles[i - 1]} has");
        }

        Assert.All(RoleCatalogue.Roles, r => Assert.True(RoleCatalogue.RoleGrants(r, PermissionCodes.TicketsRead)));
    }

    [Theory]
    [InlineData(DepartmentRoles.Viewer, PermissionCodes.DepartmentManage, "Forbidden")]
    [InlineData(DepartmentRoles.Agent, PermissionCodes.TeamsManage, "Forbidden")]
    [InlineData(DepartmentRoles.Agent, PermissionCodes.DepartmentAuditRead, "Forbidden")]
    [InlineData(DepartmentRoles.TeamLead, PermissionCodes.DepartmentAuditRead, "Allowed")]
    [InlineData(DepartmentRoles.TeamLead, PermissionCodes.TeamsManage, "Forbidden")]
    [InlineData(DepartmentRoles.DepartmentAdmin, PermissionCodes.DepartmentMembersManage, "Allowed")]
    [InlineData(DepartmentRoles.Viewer, PermissionCodes.TicketsRead, "Allowed")]
    public void Member_decisions_follow_the_role_mapping(string role, string permission, string expected)
    {
        Assert.Equal(Enum.Parse<EvaluationOutcome>(expected), PermissionEvaluator.Evaluate(PermissionCatalogue.Get(permission), false, role, true));
    }

    [Fact]
    public void Unknown_permission_is_a_programming_error()
    {
        Assert.Throws<ArgumentException>(() => PermissionCatalogue.Get("tickets.nonexistent"));
    }

    [Fact]
    public void Role_validation_accepts_only_the_four_system_roles()
    {
        Assert.All(RoleCatalogue.Roles, r => Assert.True(RoleCatalogue.IsValid(r)));
        Assert.False(RoleCatalogue.IsValid("Owner"));
        Assert.False(RoleCatalogue.IsValid("agent"));
        Assert.False(RoleCatalogue.IsValid(null));
    }
}
