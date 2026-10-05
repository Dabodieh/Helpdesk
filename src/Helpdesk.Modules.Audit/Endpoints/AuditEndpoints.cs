using Helpdesk.Modules.Audit.Services;
using Helpdesk.SharedKernel.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Helpdesk.Modules.Audit.Endpoints;

internal static class AuditEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/departments/{departmentId:guid}/audit",
                async (Guid departmentId, string? cursor, int? limit, AuditQueries queries, CancellationToken ct) =>
                {
                    var page = await queries.ForDepartmentAsync(departmentId, cursor, limit, ct);
                    return TypedResults.Ok(new { items = page.Items, nextCursor = page.NextCursor });
                })
            .RequirePermission(PermissionCodes.DepartmentAuditRead)
            .WithTags("Audit");

        app.MapGet("/api/platform/audit",
                async (string? cursor, int? limit, AuditQueries queries, CancellationToken ct) =>
                {
                    var page = await queries.ForPlatformAsync(cursor, limit, ct);
                    return TypedResults.Ok(new { items = page.Items, nextCursor = page.NextCursor });
                })
            .RequirePermission(PermissionCodes.PlatformAuditRead, departmentRouteParameter: null)
            .WithTags("Audit");
    }
}
