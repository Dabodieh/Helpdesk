using System.Data.Common;

namespace Helpdesk.SharedKernel.Security;

/// <summary>Why a member's ticket assignments are ending (recorded as the audit reason).</summary>
public static class MemberAssignmentEndReasons
{
    /// <summary>The department membership is being removed.</summary>
    public const string MembershipRemoved = "department_membership_removed";

    /// <summary>The member's role is being changed to one that cannot work tickets (no <c>tickets.update</c>), so they can no longer own tickets.</summary>
    public const string RoleChanged = "department_role_changed";
}

/// <summary>
/// Told when a department member can no longer own the work assigned to them (membership removed, or role changed to one that cannot work tickets), so a module that
/// holds such work can deal with it in the SAME database transaction (the database would otherwise clear the assignee silently on removal, or leave it stale on a role
/// change). Implemented by Ticketing; called by Organisation.
/// </summary>
public interface IMembershipRemovalListener
{
    /// <summary>
    /// Inside the Organisation transaction, after the membership row has been locked and before it is deleted or changed. The listener joins <paramref name="transaction"/>
    /// (same connection), so everything it writes commits or rolls back together with the change: nothing can be lost between the two, and a failure here aborts it.
    /// </summary>
    Task MembershipRemovingAsync(Guid departmentId, Guid userId, string reason, DbTransaction transaction, CancellationToken cancellationToken = default);

    /// <summary>After the change committed. Best-effort conveniences only (realtime refresh); nothing durable may depend on this call.</summary>
    Task MembershipRemovedAsync(Guid departmentId, Guid userId, CancellationToken cancellationToken = default);
}
