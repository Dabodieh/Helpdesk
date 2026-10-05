using Helpdesk.Modules.Organisation.Persistence;
using Helpdesk.SharedKernel.Authorization;
using Helpdesk.SharedKernel.Errors;
using Helpdesk.SharedKernel.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Modules.Organisation.Authorization;

/// <summary><see cref="IAuthorizer"/> backed by the real ASP.NET authorization pipeline (<see cref="IAuthorizationService"/>).</summary>
internal sealed class Authorizer(
    IAuthorizationService authorization,
    IHttpContextAccessor http,
    ICurrentUser current,
    MembershipCache memberships,
    OrganisationDbContext db) : IAuthorizer
{
    public async Task<AccessOutcome> CheckAsync(string permission, Guid? departmentId, CancellationToken cancellationToken = default)
    {
        var definition = PermissionCatalogue.Get(permission);
        if (definition.Kind == PermissionKind.Platform && departmentId is not null)
        {
            throw new ArgumentException("Platform permissions are not department-scoped.", nameof(departmentId));
        }

        if (definition.Kind != PermissionKind.Platform && departmentId is null)
        {
            throw new ArgumentException("Department permissions need a department id.", nameof(departmentId));
        }

        var user = http.HttpContext?.User ?? throw new InvalidOperationException("Authorization requires an HTTP request context.");
        object? resource = departmentId is { } id ? new DepartmentResource(id) : null;
        var result = await authorization.AuthorizeAsync(user, resource, new PermissionRequirement(permission));
        if (result.Succeeded)
        {
            return AccessOutcome.Allowed;
        }

        var notFound = result.Failure?.FailureReasons.Any(r => r.Message == AuthorizationFailureReasons.NotFound) == true;
        return notFound ? AccessOutcome.NotFound : AccessOutcome.Forbidden;
    }

    public async Task RequireAsync(string permission, Guid? departmentId, CancellationToken cancellationToken = default)
    {
        switch (await CheckAsync(permission, departmentId, cancellationToken))
        {
            case AccessOutcome.Allowed:
                return;
            case AccessOutcome.NotFound:
                throw new NotFoundException();
            default:
                throw new ForbiddenException();
        }
    }

    public async Task<bool> HasInAnyDepartmentAsync(string permission, CancellationToken cancellationToken = default)
    {
        var definition = PermissionCatalogue.Get(permission);
        if (!current.IsAuthenticated || definition.Kind == PermissionKind.Platform)
        {
            return current.IsAuthenticated && current.IsPlatformAdmin;
        }

        if (definition.Kind == PermissionKind.Administrative && current.IsPlatformAdmin)
        {
            return true;
        }

        var roles = await memberships.GetRolesAsync(cancellationToken);
        return roles.Values.Any(r => RoleCatalogue.RoleGrants(r, permission));
    }

    public async Task<IReadOnlyCollection<Guid>> AccessibleDepartmentsAsync(string permission, CancellationToken cancellationToken = default)
    {
        var definition = PermissionCatalogue.Get(permission);
        if (!current.IsAuthenticated || definition.Kind == PermissionKind.Platform)
        {
            return [];
        }

        if (definition.Kind == PermissionKind.Administrative && current.IsPlatformAdmin)
        {
            return await db.Departments.AsNoTracking().Select(d => d.Id).ToListAsync(cancellationToken);
        }

        var roles = await memberships.GetRolesAsync(cancellationToken);
        return roles.Where(kv => RoleCatalogue.RoleGrants(kv.Value, permission)).Select(kv => kv.Key).ToList();
    }
}
