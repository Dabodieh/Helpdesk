namespace Helpdesk.Modules.Identity.Domain;

internal sealed class User
{
    public Guid Id { get; set; }

    public string DisplayName { get; set; } = "";

    public string? Email { get; set; }

    public bool IsActive { get; set; } = true;

    public bool IsPlatformAdmin { get; set; }

    /// <summary>
    /// Stamped into every session cookie at sign-in and compared on every request. Deactivation and reactivation increment it, so every session issued before is dead
    /// for good (a reactivated account must sign in again), whatever the cookie's own expiry says.
    /// </summary>
    public int SessionVersion { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public DateTimeOffset? LastSignInAt { get; set; }

    public List<ExternalIdentity> ExternalIdentities { get; } = [];
}

/// <summary>One persisted ASP.NET Data Protection key (see DataProtectionKeyStore).</summary>
internal sealed class DataProtectionKeyRow
{
    public string FriendlyName { get; set; } = "";

    public string Xml { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }
}

internal sealed class ExternalIdentity
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public User User { get; set; } = null!;

    public string Provider { get; set; } = "";

    public string IssuerTenant { get; set; } = "";

    public string Subject { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// The Microsoft Entra sign-in application created by the setup wizard (single row, id 1). The client secret is stored only protected by ASP.NET Data Protection; the keys
/// live in this same database, so the database and its backups are the security boundary (an open production gate: key ring encryption at rest).
/// </summary>
internal sealed class EntraSettings
{
    public short Id { get; set; } = 1;

    public string TenantId { get; set; } = "";

    public string ClientId { get; set; } = "";

    public string ClientSecretProtected { get; set; } = "";

    public DateTimeOffset SecretExpiresAt { get; set; }

    /// <summary>Object id of the application registration in the tenant (for reference and rotation); not the client id.</summary>
    public string ApplicationObjectId { get; set; } = "";

    /// <summary>Origins (scheme + host + port) whose <c>/api/auth/signin-oidc</c> is registered as a redirect URI, separated by a space.</summary>
    public string RedirectOrigins { get; set; } = "";

    /// <summary>The Entra object id of the administrator who ran the setup: the first sign-in with this identity becomes a platform administrator (audited), like the bootstrap setting.</summary>
    public string BootstrapAdminSubject { get; set; } = "";

    public Guid ConfiguredBy { get; set; }

    public DateTimeOffset ConfiguredAt { get; set; }
}
