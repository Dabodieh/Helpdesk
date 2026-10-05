namespace Helpdesk.Modules.Organisation.Contracts;

/// <summary>Stable role codes stored in <c>department_memberships.role</c> (fixed system roles in v1).</summary>
public static class DepartmentRoles
{
    public const string Viewer = "Viewer";
    public const string Agent = "Agent";
    public const string TeamLead = "TeamLead";
    public const string DepartmentAdmin = "DepartmentAdmin";
}
