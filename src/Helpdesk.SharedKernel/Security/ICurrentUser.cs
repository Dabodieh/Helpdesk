namespace Helpdesk.SharedKernel.Security;

/// <summary>The signed-in internal user for the current request (loaded and active-checked on every request by the Identity module).</summary>
public interface ICurrentUser
{
    bool IsAuthenticated { get; }

    /// <summary>Internal user id. Throws when the request is not authenticated.</summary>
    Guid UserId { get; }

    string DisplayName { get; }

    string? Email { get; }

    bool IsPlatformAdmin { get; }
}
