namespace Helpdesk.Modules.Identity.Contracts;

/// <summary>An external (OIDC or development) identity as presented at sign-in. Email/name are mutable attributes, never keys.</summary>
public sealed record ExternalIdentityClaims(string Provider, string IssuerTenant, string Subject, string DisplayName, string? Email);

public sealed record ProvisionedUser(Guid UserId, string DisplayName, string? Email, bool IsActive, bool IsPlatformAdmin, bool IsNewUser, int SessionVersion = 0);

public sealed record UserSummary(Guid Id, string DisplayName, string? Email, bool IsActive, bool IsPlatformAdmin);

/// <summary>Claim types of a signed-in session (the cookie principal), for long-lived connections that must re-check their session.</summary>
public static class SessionClaimTypes
{
    public const string SessionVersion = "helpdesk:sv";
    public const string IssuedAt = "helpdesk:iat";
}

/// <summary>
/// Is a session that was issued at sign-in still valid NOW: the account exists and is active, its session version is the one stamped at sign-in (deactivation and
/// reactivation increment it) and the session is younger than the absolute lifetime. The same rule the cookie middleware applies to every HTTP request; used by realtime
/// connections, which authenticate once and would otherwise outlive their session.
/// </summary>
public interface IUserSessionValidator
{
    Task<bool> IsSessionValidAsync(Guid userId, int sessionVersion, DateTimeOffset issuedAt, CancellationToken cancellationToken = default);
}

public static class ExternalIdentityProviders
{
    public const string Entra = "entra";
    public const string Dev = "dev";
}

/// <summary>
/// Maps an external identity to an internal user, creating the user on first sign-in with no access. Both the Entra OIDC
/// handler and the development sign-in call this; nothing downstream knows which was used.
/// </summary>
public interface IUserProvisioner
{
    Task<ProvisionedUser> ProvisionAsync(ExternalIdentityClaims identity, CancellationToken cancellationToken = default);
}

/// <summary>Read access to users for other modules (no table access across modules).</summary>
public interface IUserDirectory
{
    Task<UserSummary?> FindAsync(Guid userId, CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<Guid, UserSummary>> GetManyAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Active accounts whose current address equals one of the given lower-cased addresses, keyed by that address. Only ever a SUGGESTION for an administrator
    /// (an address is mutable and reusable, never proof of who somebody is). When several accounts share an address none is suggested.
    /// </summary>
    Task<IReadOnlyDictionary<string, UserSummary>> FindActiveByEmailsAsync(IReadOnlyCollection<string> normalisedEmails, CancellationToken cancellationToken = default);
}
