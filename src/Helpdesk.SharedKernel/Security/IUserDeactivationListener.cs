using System.Data.Common;

namespace Helpdesk.SharedKernel.Security;

/// <summary>Why a user's work is being released (recorded as the audit reason).</summary>
public static class UserAssignmentEndReasons
{
    /// <summary>The account was deactivated by an administrator.</summary>
    public const string UserDeactivated = "user_deactivated";
}

/// <summary>
/// Told when a user account is being deactivated, so a module that holds work owned by that user (assigned tickets) can release it in the SAME database transaction:
/// nothing can be left assigned to an account that can no longer act, and a failure here aborts the deactivation. Implemented by Ticketing; called by Identity.
/// Memberships, messages, audit history and requester records are never touched: they are history and are needed again if the account is reactivated.
/// </summary>
public interface IUserDeactivationListener
{
    /// <summary>
    /// Inside the Identity transaction, after the user row has been locked and changed but before commit. The listener joins <paramref name="transaction"/> (same
    /// connection), so everything it writes commits or rolls back together with the deactivation.
    /// </summary>
    Task UserDeactivatingAsync(Guid userId, string reason, DbTransaction transaction, CancellationToken cancellationToken = default);

    /// <summary>After the deactivation committed. Best-effort conveniences only (realtime refresh); nothing durable may depend on this call.</summary>
    Task UserDeactivatedAsync(Guid userId, CancellationToken cancellationToken = default);
}
