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
    public const string DepartmentMailboxesManage = "department.mailboxes.manage";
    public const string DepartmentCannedManage = "department.canned.manage";
    public const string DepartmentSlaManage = "department.sla.manage";
    public const string DepartmentAutomationManage = "department.automation.manage";
    public const string DepartmentFormsManage = "department.forms.manage";
    public const string DepartmentKnowledgeManage = "department.knowledge.manage";
    public const string DepartmentAuditRead = "department.audit.read";
    public const string TicketsRead = "tickets.read";
    public const string TicketsCreate = "tickets.create";
    public const string TicketsReply = "tickets.reply";
    public const string TicketsNote = "tickets.note";
    public const string TicketsNotesRead = "tickets.notes.read";
    public const string TicketsUpdate = "tickets.update";
    public const string TicketsAssignSelf = "tickets.assign.self";

    /// <summary>Change a ticket's team (routing and escalation) without being able to choose another person as assignee (D21).</summary>
    public const string TicketsAssignTeam = "tickets.assign.team";
    public const string TicketsAssignAny = "tickets.assign.any";
}
