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
        MapMembers(app);
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
