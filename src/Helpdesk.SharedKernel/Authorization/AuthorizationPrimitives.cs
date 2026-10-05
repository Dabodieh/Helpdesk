using Microsoft.AspNetCore.Authorization;

namespace Helpdesk.SharedKernel.Authorization;

/// <summary>Requirement evaluated by the Organisation module's authorization handler.</summary>
public sealed class PermissionRequirement(string permission) : IAuthorizationRequirement
{
    public string Permission { get; } = permission;
}

/// <summary>Resource for department-scoped decisions. Platform permissions are evaluated with no resource.</summary>
public sealed class DepartmentResource(Guid departmentId)
{
    public Guid DepartmentId { get; } = departmentId;
}

public enum AccessOutcome
{
    Allowed,

    /// <summary>No membership (or no such department): the caller must not learn whether it exists.</summary>
    NotFound,

    /// <summary>Member (or authenticated caller for platform permissions) lacking the permission.</summary>
    Forbidden,
}

public static class AuthorizationFailureReasons
{
    public const string NotFound = "helpdesk:not-found";
    public const string Forbidden = "helpdesk:forbidden";
}

/// <summary>
/// Single entry point for authorization decisions. Backed by the ASP.NET authorization pipeline
/// (<see cref="IAuthorizationService"/> + <see cref="PermissionRequirement"/>).
/// </summary>
public interface IAuthorizer
{
    Task<AccessOutcome> CheckAsync(string permission, Guid? departmentId, CancellationToken cancellationToken = default);

    /// <summary>Throws <see cref="Errors.NotFoundException"/> (404) or <see cref="Errors.ForbiddenException"/> (403) when not allowed.</summary>
    Task RequireAsync(string permission, Guid? departmentId, CancellationToken cancellationToken = default);

    /// <summary>True when the caller holds the permission in at least one department (administrative permissions: platform admins in all).</summary>
    Task<bool> HasInAnyDepartmentAsync(string permission, CancellationToken cancellationToken = default);

    /// <summary>Departments where the caller holds the permission through a membership or (administrative only) the platform-admin flag.</summary>
    Task<IReadOnlyCollection<Guid>> AccessibleDepartmentsAsync(string permission, CancellationToken cancellationToken = default);
}
