using Helpdesk.Modules.Identity.Services;
using Helpdesk.SharedKernel.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Helpdesk.Modules.Identity.Endpoints;

/// <summary>Platform administration of Microsoft Entra staff sign-in (status, guided setup, disconnect). Platform administrators only.</summary>
internal static class EntraEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin/entra").WithTags("Entra sign-in");

        group.MapGet("", (EntraSetupService service) => TypedResults.Ok(service.GetStatus()))
            .RequirePermission(PermissionCodes.PlatformUsersManage, departmentRouteParameter: null);

        group.MapPost("setup/start", async (EntraSetupService service, CancellationToken ct) => TypedResults.Ok(await service.StartAsync(ct)))
            .RequirePermission(PermissionCodes.PlatformUsersManage, departmentRouteParameter: null);

        group.MapPost("setup/poll", async (EntraSetupSessionRequest body, EntraSetupService service, CancellationToken ct) => TypedResults.Ok(await service.PollAsync(body.SessionId, ct)))
            .RequirePermission(PermissionCodes.PlatformUsersManage, departmentRouteParameter: null);

        group.MapPost("setup/create", async (EntraSetupCreateRequest body, EntraSetupService service, CancellationToken ct) => TypedResults.Ok(await service.CreateAsync(body, ct)))
            .RequirePermission(PermissionCodes.PlatformUsersManage, departmentRouteParameter: null);

        group.MapPost("disconnect", async (EntraSetupService service, CancellationToken ct) =>
            {
                await service.DisconnectAsync(ct);
                return TypedResults.NoContent();
            })
            .RequirePermission(PermissionCodes.PlatformUsersManage, departmentRouteParameter: null);
    }
}
