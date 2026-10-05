namespace Helpdesk.Modules.Organisation.Contracts;

/// <summary>Stable role codes stored in <c>department_memberships.role</c> (fixed system roles in v1).</summary>
public static class DepartmentRoles
{
    public const string Viewer = "Viewer";
    public const string Agent = "Agent";
    public const string TeamLead = "TeamLead";
    public const string DepartmentAdmin = "DepartmentAdmin";
}

public sealed record DepartmentInfo(Guid Id, string Key, string Name, bool IsActive);

public sealed record TeamInfo(Guid Id, Guid DepartmentId, string Name, bool IsActive);

/// <summary>
/// Read-only organisation questions other modules (Ticketing) need, answered without table access across modules.
/// Lookups are not caller-scoped: callers decide who may see what; ids come from already authorised objects.
/// </summary>
public interface IOrganisationDirectory
{
    Task<DepartmentInfo?> FindDepartmentAsync(Guid departmentId, CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<Guid, DepartmentInfo>> GetDepartmentsAsync(IReadOnlyCollection<Guid> departmentIds, CancellationToken cancellationToken = default);

    /// <summary>The team only when it belongs to <paramref name="departmentId"/>; null otherwise (a team of another department is simply not found).</summary>
    Task<TeamInfo?> FindTeamAsync(Guid departmentId, Guid teamId, CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<Guid, TeamInfo>> GetTeamsAsync(IReadOnlyCollection<Guid> teamIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// True when the user holds a membership in the department whose role grants <c>tickets.update</c> (can work tickets) and
    /// the user is active.
    /// </summary>
    Task<bool> IsEligibleAssigneeAsync(Guid departmentId, Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Departments in which <paramref name="userId"/> holds a CONTENT permission through a membership role (never through the platform-admin
    /// flag), for an active user. Used by long-lived connections that have no request context.
    /// </summary>
    Task<IReadOnlyCollection<Guid>> GetDepartmentsWithPermissionAsync(Guid userId, string permission, CancellationToken cancellationToken = default);

    /// <summary>Ids of the teams the user belongs to, restricted to the given departments (team membership, never inferred from role).</summary>
    Task<IReadOnlyCollection<Guid>> GetTeamIdsOfUserAsync(Guid userId, IReadOnlyCollection<Guid> departmentIds, CancellationToken cancellationToken = default);
}
