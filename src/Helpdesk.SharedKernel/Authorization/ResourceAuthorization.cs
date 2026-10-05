using Helpdesk.SharedKernel.Errors;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Helpdesk.SharedKernel.Authorization;

/// <summary>
/// Resolves the department that owns a resource addressed by a route value (e.g. a ticket id or reference), through the
/// caller's department-scoped data. Returns null when the resource does not exist <b>or</b> is not visible to the caller:
/// the two cases must stay indistinguishable (404).
/// </summary>
public interface IResourceDepartmentResolver
{
    Task<Guid?> ResolveDepartmentAsync(string routeValue, CancellationToken cancellationToken = default);
}

/// <summary>
/// Endpoint metadata: the endpoint authorises a <b>content</b> permission against the department that owns the resource named
/// by <see cref="ResourceRouteParameter"/> (the department is never taken from the request). Read by the architecture tests.
/// </summary>
public sealed record ResourcePermissionMetadata(string Permission, string ResourceRouteParameter, Type ResolverType);

public static class ResourceAuthorizationExtensions
{
    /// <summary>
    /// Requires an authenticated user and <paramref name="permission"/> on the resource's persisted department. Unknown or
    /// invisible resources are 404 (via <typeparamref name="TResolver"/>), members lacking the permission 403.
    /// </summary>
    public static IEndpointConventionBuilder RequireResourcePermission<TResolver>(this IEndpointConventionBuilder builder, string permission, string resourceRouteParameter)
        where TResolver : class, IResourceDepartmentResolver
    {
        builder.RequireAuthorization();
        builder.WithMetadata(new ResourcePermissionMetadata(permission, resourceRouteParameter, typeof(TResolver)));
        builder.AddEndpointFilter(async (context, next) =>
        {
            var raw = context.HttpContext.Request.RouteValues[resourceRouteParameter]?.ToString();
            if (string.IsNullOrEmpty(raw))
            {
                throw new NotFoundException();
            }

            var services = context.HttpContext.RequestServices;
            var ct = context.HttpContext.RequestAborted;
            var departmentId = await services.GetRequiredService<TResolver>().ResolveDepartmentAsync(raw, ct) ?? throw new NotFoundException();
            await services.GetRequiredService<IAuthorizer>().RequireAsync(permission, departmentId, ct);
            return await next(context);
        });
        return builder;
    }
}
