namespace Helpdesk.SharedKernel.Authorization;

/// <summary>
/// Permission code constants. The catalogue (kind, role mapping) lives in the Organisation module; only the codes are
/// shared so modules that cannot reference Organisation (Audit, Identity) can still declare what an endpoint requires.
/// </summary>
public static class PermissionCodes
{
    public const string PlatformDepartmentsManage = "platform.departments.manage";
    public const string PlatformUsersManage = "platform.users.manage";
    public const string PlatformAuditRead = "platform.audit.read";
    public const string DepartmentRead = "department.read";
    public const string DepartmentManage = "department.manage";
    public const string DepartmentMembersManage = "department.members.manage";
    public const string TeamsManage = "teams.manage";
    public const string DepartmentAuditRead = "department.audit.read";
    public const string TicketsRead = "tickets.read";
}
