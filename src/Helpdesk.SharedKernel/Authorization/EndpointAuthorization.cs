using Helpdesk.SharedKernel.Errors;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Helpdesk.SharedKernel.Authorization;

/// <summary>Endpoint metadata: the permission this endpoint requires (read by the architecture tests and the OpenAPI/QA tooling).</summary>
public sealed record PermissionMetadata(string Permission, string? DepartmentRouteParameter);

/// <summary>Endpoint metadata: explicitly declared as "any authenticated user" (no permission or department).</summary>
public sealed record AuthenticatedOnlyMetadata;

public static class EndpointAuthorizationExtensions
{
    /// <summary>
    /// Requires an authenticated user and the permission, evaluated by <see cref="IAuthorizer"/>. When
    /// <paramref name="departmentRouteParameter"/> is set, the department id is read from that route value; inaccessible
    /// departments yield 404, insufficient permission 403. Pass null for platform permissions.
    /// </summary>
    public static TBuilder RequirePermission<TBuilder>(this TBuilder builder, string permission, string? departmentRouteParameter = "departmentId")
        where TBuilder : IEndpointConventionBuilder
    {
        builder.RequireAuthorization();
        builder.WithMetadata(new PermissionMetadata(permission, departmentRouteParameter));
        builder.AddEndpointFilter(async (context, next) =>
        {
            Guid? departmentId = null;
            if (departmentRouteParameter is not null)
            {
                var raw = context.HttpContext.Request.RouteValues[departmentRouteParameter]?.ToString();
                if (!Guid.TryParse(raw, out var parsed))
                {
                    throw new NotFoundException();
                }

                departmentId = parsed;
            }

            var authorizer = context.HttpContext.RequestServices.GetRequiredService<IAuthorizer>();
            await authorizer.RequireAsync(permission, departmentId, context.HttpContext.RequestAborted);
            return await next(context);
        });
        return builder;
    }

    /// <summary>Marks an endpoint as authenticated-only on purpose (own data, or authorization decided in the service).</summary>
    public static TBuilder RequireAuthenticatedOnly<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.RequireAuthorization();
        builder.WithMetadata(new AuthenticatedOnlyMetadata());
        return builder;
    }
}
