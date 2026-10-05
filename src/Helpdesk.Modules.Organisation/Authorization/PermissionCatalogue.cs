using Helpdesk.Modules.Organisation.Contracts;
using Helpdesk.SharedKernel.Authorization;

namespace Helpdesk.Modules.Organisation.Authorization;

internal enum PermissionKind
{
    /// <summary>Installation-level; satisfied only by the platform-admin flag.</summary>
    Platform,

    /// <summary>Department structure/config; satisfied by a role that grants it, or by the platform-admin flag.</summary>
    Administrative,

    /// <summary>Ticket/message/attachment data; satisfied ONLY by a department membership whose role grants it. Never by platform admin.</summary>
    Content,
}

internal sealed record PermissionDefinition(string Code, PermissionKind Kind);

internal enum EvaluationOutcome
{
    Allowed,
    NotFound,
    Forbidden,
}

/// <summary>The permission catalogue and fixed roles, defined in code (PERMISSIONS.md).</summary>
internal static class PermissionCatalogue
{
    public static readonly IReadOnlyList<PermissionDefinition> All =
    [
        new(PermissionCodes.PlatformDepartmentsManage, PermissionKind.Platform),
        new(PermissionCodes.PlatformUsersManage, PermissionKind.Platform),
        new(PermissionCodes.PlatformAuditRead, PermissionKind.Platform),
        new(PermissionCodes.DepartmentRead, PermissionKind.Administrative),
        new(PermissionCodes.DepartmentManage, PermissionKind.Administrative),
        new(PermissionCodes.DepartmentMembersManage, PermissionKind.Administrative),
        new(PermissionCodes.TeamsManage, PermissionKind.Administrative),
        new(PermissionCodes.DepartmentAuditRead, PermissionKind.Administrative),
        new(PermissionCodes.TicketsRead, PermissionKind.Content),
    ];

    private static readonly Dictionary<string, PermissionDefinition> ByCode = All.ToDictionary(p => p.Code, StringComparer.Ordinal);

    public static PermissionDefinition Get(string code) =>
        ByCode.TryGetValue(code, out var definition) ? definition : throw new ArgumentException($"Unknown permission '{code}'.", nameof(code));

    public static IReadOnlyList<string> PlatformPermissions { get; } =
        All.Where(p => p.Kind == PermissionKind.Platform).Select(p => p.Code).ToArray();

    public static IReadOnlyList<string> AdministrativePermissions { get; } =
        All.Where(p => p.Kind == PermissionKind.Administrative).Select(p => p.Code).ToArray();
}

internal static class RoleCatalogue
{
    /// <summary>Ordered from least to most privileged.</summary>
    public static readonly IReadOnlyList<string> Roles =
        [DepartmentRoles.Viewer, DepartmentRoles.Agent, DepartmentRoles.TeamLead, DepartmentRoles.DepartmentAdmin];

    private static readonly string[] Reader = [PermissionCodes.DepartmentRead, PermissionCodes.TicketsRead];

    private static readonly Dictionary<string, HashSet<string>> Grants = new(StringComparer.Ordinal)
    {
        [DepartmentRoles.Viewer] = [.. Reader],
        [DepartmentRoles.Agent] = [.. Reader],
        [DepartmentRoles.TeamLead] = [.. Reader, PermissionCodes.DepartmentAuditRead],
        [DepartmentRoles.DepartmentAdmin] =
        [
            .. Reader,
            PermissionCodes.DepartmentAuditRead,
            PermissionCodes.DepartmentManage,
            PermissionCodes.DepartmentMembersManage,
            PermissionCodes.TeamsManage,
        ],
    };

    public static bool IsValid(string? role) => role is not null && Grants.ContainsKey(role);

    public static bool RoleGrants(string role, string permission) => Grants.TryGetValue(role, out var set) && set.Contains(permission);

    public static IReadOnlyCollection<string> PermissionsOf(string role) => Grants.TryGetValue(role, out var set) ? set : [];
}

/// <summary>Pure decision function used by the authorization handler (unit-testable without a database).</summary>
internal static class PermissionEvaluator
{
    /// <param name="role">The caller's role in the department, or null when there is no membership.</param>
    /// <param name="departmentExists">Whether the department exists (only relevant when the caller has no membership).</param>
    public static EvaluationOutcome Evaluate(PermissionDefinition permission, bool isPlatformAdmin, string? role, bool departmentExists)
    {
        if (permission.Kind == PermissionKind.Platform)
        {
            return isPlatformAdmin ? EvaluationOutcome.Allowed : EvaluationOutcome.Forbidden;
        }

        if (role is not null && RoleCatalogue.RoleGrants(role, permission.Code))
        {
            return EvaluationOutcome.Allowed;
        }

        // Platform admins satisfy administrative permissions only. Content permissions are never satisfied by the flag.
        if (permission.Kind == PermissionKind.Administrative && isPlatformAdmin)
        {
            return role is not null || departmentExists ? EvaluationOutcome.Allowed : EvaluationOutcome.NotFound;
        }

        return role is null ? EvaluationOutcome.NotFound : EvaluationOutcome.Forbidden;
    }
}
