namespace Helpdesk.Modules.Identity.Domain;

internal sealed class User
{
    public Guid Id { get; set; }

    public string DisplayName { get; set; } = "";

    public string? Email { get; set; }

    public bool IsActive { get; set; } = true;

    public bool IsPlatformAdmin { get; set; }

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
