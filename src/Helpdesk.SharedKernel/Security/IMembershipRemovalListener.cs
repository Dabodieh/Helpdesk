using System.Data.Common;

namespace Helpdesk.SharedKernel.Security;

/// <summary>
/// Told when a user's department membership is removed, so a module that holds work assigned to that user can deal with it in the SAME database transaction as the
/// removal (the database would otherwise clear the assignee silently when the membership disappears). Implemented by Ticketing; called by Organisation.
/// </summary>
public interface IMembershipRemovalListener
{
    /// <summary>
    /// Inside the removal transaction, after the membership row has been locked and before it is deleted. The listener joins <paramref name="transaction"/> (same connection),
    /// so everything it writes commits or rolls back together with the removal: nothing can be lost between the two, and a failure here aborts the removal.
    /// </summary>
    Task MembershipRemovingAsync(Guid departmentId, Guid userId, DbTransaction transaction, CancellationToken cancellationToken = default);

    /// <summary>After the removal committed. Best-effort conveniences only (realtime refresh); nothing durable may depend on this call.</summary>
    Task MembershipRemovedAsync(Guid departmentId, Guid userId, CancellationToken cancellationToken = default);
}
