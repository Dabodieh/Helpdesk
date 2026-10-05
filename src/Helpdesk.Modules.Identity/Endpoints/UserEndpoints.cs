using Helpdesk.Modules.Identity.Services;
using Helpdesk.SharedKernel.Authorization;
using Helpdesk.SharedKernel.Errors;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Helpdesk.Modules.Identity.Endpoints;

internal static class UserEndpoints
{
    internal sealed record SetPlatformAdminRequest(bool? Value);

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/platform/users",
                async (string? search, string? cursor, int? limit, PlatformUserService users, CancellationToken ct) =>
                {
                    var page = await users.ListAsync(search, cursor, limit, ct);
                    return TypedResults.Ok(new { items = page.Items, nextCursor = page.NextCursor });
                })
            .RequirePermission(PermissionCodes.PlatformUsersManage, departmentRouteParameter: null)
            .WithTags("Platform");

        app.MapPut("/api/platform/users/{userId:guid}/platform-admin",
                async (Guid userId, SetPlatformAdminRequest body, PlatformUserService users, CancellationToken ct) =>
                {
                    if (body.Value is null)
                    {
                        throw new RequestValidationException("value", "value is required.");
                    }

                    return TypedResults.Ok(await users.SetPlatformAdminAsync(userId, body.Value.Value, ct));
                })
            .RequirePermission(PermissionCodes.PlatformUsersManage, departmentRouteParameter: null)
            .WithTags("Platform");

        // Authorised in the handler: platform.users.manage, or department.members.manage in at least one department.
        app.MapGet("/api/users/lookup",
                async (string? q, PlatformUserService users, IAuthorizer authorizer, CancellationToken ct) =>
                {
                    var allowed = await authorizer.CheckAsync(PermissionCodes.PlatformUsersManage, null, ct) == AccessOutcome.Allowed
                        || await authorizer.HasInAnyDepartmentAsync(PermissionCodes.DepartmentMembersManage, ct);
                    if (!allowed)
                    {
                        throw new ForbiddenException();
                    }

                    return TypedResults.Ok(await users.LookupAsync(q, ct));
                })
            .RequireAuthenticatedOnly()
            .WithTags("Users");
    }
}
