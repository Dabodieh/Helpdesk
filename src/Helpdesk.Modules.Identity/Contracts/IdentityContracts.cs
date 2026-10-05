namespace Helpdesk.Modules.Identity.Contracts;

/// <summary>An external (OIDC or development) identity as presented at sign-in. Email/name are mutable attributes, never keys.</summary>
public sealed record ExternalIdentityClaims(string Provider, string IssuerTenant, string Subject, string DisplayName, string? Email);

public sealed record ProvisionedUser(Guid UserId, string DisplayName, string? Email, bool IsActive, bool IsPlatformAdmin, bool IsNewUser);

public sealed record UserSummary(Guid Id, string DisplayName, string? Email, bool IsActive, bool IsPlatformAdmin);

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
}
