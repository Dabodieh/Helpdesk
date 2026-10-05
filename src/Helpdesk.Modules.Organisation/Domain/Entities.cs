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
