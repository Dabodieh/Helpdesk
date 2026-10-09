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
        MapInvitations(app);
    }

    /// <summary>
    /// Account invitations. Creating, listing, resending and revoking are platform administration (<c>platform.users.manage</c>). Previewing and accepting need only a signed-in user:
    /// the link's secret and the signed-in address decide, and every unusable link is a plain 404.
    /// </summary>
    private static void MapInvitations(IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/admin/invitations").WithTags("Invitations");

        admin.MapGet("", async (InvitationService service, CancellationToken ct) => TypedResults.Ok(await service.ListAsync(ct)))
            .RequirePermission(PermissionCodes.PlatformUsersManage, departmentRouteParameter: null);

        admin.MapPost("", async (CreateInvitationRequest body, InvitationService service, CancellationToken ct) => TypedResults.Created("/api/admin/invitations", await service.CreateAsync(body, ct)))
            .RequirePermission(PermissionCodes.PlatformUsersManage, departmentRouteParameter: null);

        admin.MapPost("{id:guid}/resend", async (Guid id, InvitationService service, CancellationToken ct) => TypedResults.Ok(await service.ResendAsync(id, ct)))
            .RequirePermission(PermissionCodes.PlatformUsersManage, departmentRouteParameter: null);

        admin.MapPost("{id:guid}/revoke", async (Guid id, InvitationService service, CancellationToken ct) =>
            {
                await service.RevokeAsync(id, ct);
                return TypedResults.NoContent();
            })
            .RequirePermission(PermissionCodes.PlatformUsersManage, departmentRouteParameter: null);

        app.MapPost("/api/invitations/preview", async (InvitationTokenRequest body, InvitationService service, CancellationToken ct) => TypedResults.Ok(await service.PreviewAsync(body.Token, ct)))
            .RequireAuthenticatedOnly()
            .WithTags("Invitations");

        app.MapPost("/api/invitations/accept", async (InvitationTokenRequest body, InvitationService service, CancellationToken ct) => TypedResults.Ok(await service.AcceptAsync(body.Token, ct)))
            .RequireAuthenticatedOnly()
            .WithTags("Invitations");
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
