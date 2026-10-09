namespace Helpdesk.Modules.Organisation.Domain;

internal sealed class Department
{
    public Guid Id { get; set; }

    public string Key { get; set; } = "";

    public string Name { get; set; } = "";

    public string? Description { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>PostgreSQL xmin; exposed to clients as the opaque <c>version</c> string.</summary>
    public uint Version { get; set; }
}

internal sealed class Team
{
    public Guid Id { get; set; }

    public Guid DepartmentId { get; set; }

    public string Name { get; set; } = "";

    public string? Description { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public uint Version { get; set; }
}

internal sealed class DepartmentMembership
{
    public Guid UserId { get; set; }

    public Guid DepartmentId { get; set; }

    public string Role { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

internal sealed class TeamMembership
{
    public Guid TeamId { get; set; }

    public Guid UserId { get; set; }

    /// <summary>Denormalised from the team; composite foreign keys make crossing departments impossible.</summary>
    public Guid DepartmentId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

internal static class InvitationStatuses
{
    public const string Pending = "Pending";
    public const string Accepted = "Accepted";
    public const string Revoked = "Revoked";
}

internal static class InvitationEmailStates
{
    public const string NotConfigured = "NotConfigured";
    public const string Accepted = "Accepted";
    public const string Failed = "Failed";
    public const string Uncertain = "Uncertain";
}

/// <summary>
/// A platform administrator's invitation for one person to join one department. The emailed link carries a random token of which only the SHA-256 hash is stored.
/// Activation needs the person to sign in with Microsoft and the signed-in address to match; nothing is granted until then.
/// </summary>
internal sealed class Invitation
{
    public Guid Id { get; set; }

    public string DisplayName { get; set; } = "";

    public string Email { get; set; } = "";

    public string EmailNormalised { get; set; } = "";

    public Guid DepartmentId { get; set; }

    public string Role { get; set; } = "";

    public byte[] TokenHash { get; set; } = [];

    public string Status { get; set; } = InvitationStatuses.Pending;

    public DateTimeOffset ExpiresAt { get; set; }

    public string EmailState { get; set; } = InvitationEmailStates.NotConfigured;

    public Guid CreatedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public DateTimeOffset? AcceptedAt { get; set; }

    public Guid? AcceptedUserId { get; set; }
}
