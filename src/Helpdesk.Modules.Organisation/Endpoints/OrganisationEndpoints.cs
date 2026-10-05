using Helpdesk.Modules.Organisation.Services;
using Helpdesk.SharedKernel.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Helpdesk.Modules.Organisation.Endpoints;

internal static class OrganisationEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/me", async (MeService me, CancellationToken ct) => TypedResults.Ok(await me.GetAsync(ct)))
            .RequireAuthenticatedOnly()
            .WithTags("Auth");

        MapDepartments(app);
        MapTeams(app);
        MapMembers(app);

        // Temporary Phase 1 probe for the CONTENT permission tickets.read; replaced by real ticket endpoints in Phase 2.
        app.MapGet("/api/departments/{departmentId:guid}/ticket-access-probe", () => TypedResults.NoContent())
            .RequirePermission(PermissionCodes.TicketsRead)
            .WithTags("Probe");
    }

    private static void MapDepartments(IEndpointRouteBuilder app)
    {
        // Visibility is decided by the query: members see their departments; platform admins see all (administrative metadata only).
        app.MapGet("/api/departments", async (DepartmentService departments, CancellationToken ct) => TypedResults.Ok(await departments.ListAsync(ct)))
            .RequireAuthenticatedOnly()
            .WithTags("Departments");

        app.MapPost("/api/departments", async (CreateDepartmentRequest body, DepartmentService departments, CancellationToken ct) =>
            {
                var created = await departments.CreateAsync(body, ct);
                return TypedResults.Created($"/api/departments/{created.Id}", created);
            })
            .RequirePermission(PermissionCodes.PlatformDepartmentsManage, departmentRouteParameter: null)
            .WithTags("Departments");

        app.MapGet("/api/departments/{departmentId:guid}",
                async (Guid departmentId, DepartmentService departments, CancellationToken ct) => TypedResults.Ok(await departments.GetAsync(departmentId, ct)))
            .RequirePermission(PermissionCodes.DepartmentRead)
            .WithTags("Departments");

        // Baseline department.read (404 for outsiders); the service then requires department.manage for name/description and
        // platform.departments.manage for isActive.
        app.MapPatch("/api/departments/{departmentId:guid}",
                async (Guid departmentId, UpdateDepartmentRequest body, DepartmentService departments, CancellationToken ct) =>
                    TypedResults.Ok(await departments.UpdateAsync(departmentId, body, ct)))
            .RequirePermission(PermissionCodes.DepartmentRead)
            .WithTags("Departments");
    }

    private static void MapTeams(IEndpointRouteBuilder app)
    {
        var teams = app.MapGroup("/api/departments/{departmentId:guid}/teams").WithTags("Teams");

        teams.MapGet("", async (Guid departmentId, TeamService service, CancellationToken ct) => TypedResults.Ok(await service.ListAsync(departmentId, ct)))
            .RequirePermission(PermissionCodes.DepartmentRead);

        teams.MapPost("", async (Guid departmentId, CreateTeamRequest body, TeamService service, CancellationToken ct) =>
            {
                var created = await service.CreateAsync(departmentId, body, ct);
                return TypedResults.Created($"/api/departments/{departmentId}/teams/{created.Id}", created);
            })
            .RequirePermission(PermissionCodes.TeamsManage);

        teams.MapGet("/{teamId:guid}",
                async (Guid departmentId, Guid teamId, TeamService service, CancellationToken ct) => TypedResults.Ok(await service.GetAsync(departmentId, teamId, ct)))
            .RequirePermission(PermissionCodes.DepartmentRead);

        teams.MapPatch("/{teamId:guid}",
                async (Guid departmentId, Guid teamId, UpdateTeamRequest body, TeamService service, CancellationToken ct) =>
                    TypedResults.Ok(await service.UpdateAsync(departmentId, teamId, body, ct)))
            .RequirePermission(PermissionCodes.TeamsManage);

        teams.MapGet("/{teamId:guid}/members",
                async (Guid departmentId, Guid teamId, TeamService service, CancellationToken ct) => TypedResults.Ok(await service.MembersAsync(departmentId, teamId, ct)))
            .RequirePermission(PermissionCodes.DepartmentRead);

        teams.MapPut("/{teamId:guid}/members/{userId:guid}", async (Guid departmentId, Guid teamId, Guid userId, TeamService service, CancellationToken ct) =>
            {
                await service.AddMemberAsync(departmentId, teamId, userId, ct);
                return TypedResults.NoContent();
            })
            .RequirePermission(PermissionCodes.TeamsManage);

        teams.MapDelete("/{teamId:guid}/members/{userId:guid}", async (Guid departmentId, Guid teamId, Guid userId, TeamService service, CancellationToken ct) =>
            {
                await service.RemoveMemberAsync(departmentId, teamId, userId, ct);
                return TypedResults.NoContent();
            })
            .RequirePermission(PermissionCodes.TeamsManage);
    }

    private static void MapMembers(IEndpointRouteBuilder app)
    {
        var members = app.MapGroup("/api/departments/{departmentId:guid}/members").WithTags("Members");

        members.MapGet("", async (Guid departmentId, MembershipService service, CancellationToken ct) => TypedResults.Ok(await service.ListAsync(departmentId, ct)))
            .RequirePermission(PermissionCodes.DepartmentRead);

        members.MapPut("/{userId:guid}", async (Guid departmentId, Guid userId, SetRoleRequest body, MembershipService service, CancellationToken ct) =>
                TypedResults.Ok(await service.SetRoleAsync(departmentId, userId, body, ct)))
            .RequirePermission(PermissionCodes.DepartmentMembersManage);

        members.MapDelete("/{userId:guid}", async (Guid departmentId, Guid userId, MembershipService service, CancellationToken ct) =>
            {
                await service.RemoveAsync(departmentId, userId, ct);
                return TypedResults.NoContent();
            })
            .RequirePermission(PermissionCodes.DepartmentMembersManage);
    }
}
