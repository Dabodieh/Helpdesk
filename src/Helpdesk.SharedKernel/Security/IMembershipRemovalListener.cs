namespace Helpdesk.SharedKernel.Security;

/// <summary>
/// Told when a user's department membership is removed, so a module that holds work assigned to that user can record what the removal does to it
/// (the database clears the assignee when the membership disappears). Implemented by Ticketing; called by Organisation.
/// The two calls belong together and use the same listener instance within one request.
/// </summary>
public interface IMembershipRemovalListener
{
    /// <summary>Inside the removal transaction, before the membership is deleted. Read-only: capture what the removal is about to change.</summary>
    Task BeforeMembershipRemovedAsync(Guid departmentId, Guid userId, CancellationToken cancellationToken = default);

    /// <summary>After the removal committed. Best-effort: a failure here must not undo or fail the removal.</summary>
    Task MembershipRemovedAsync(Guid departmentId, Guid userId, CancellationToken cancellationToken = default);
}
