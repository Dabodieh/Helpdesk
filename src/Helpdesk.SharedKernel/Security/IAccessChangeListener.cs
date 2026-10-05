namespace Helpdesk.SharedKernel.Security;

/// <summary>
/// Told (after commit) when a user's access to departments or teams may have changed, so long-lived connections (realtime) can drop what
/// the user may no longer see immediately instead of waiting for the periodic re-check. Implemented by Ticketing; called by Organisation.
/// </summary>
public interface IAccessChangeListener
{
    Task AccessChangedAsync(Guid userId, CancellationToken cancellationToken = default);
}
