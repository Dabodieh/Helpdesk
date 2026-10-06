namespace Helpdesk.Modules.Ticketing.Domain;

internal static class FieldTypes
{
    public const string ShortText = "ShortText";
    public const string LongText = "LongText";
    public const string SingleSelect = "SingleSelect";
    public const string MultiSelect = "MultiSelect";
    public const string Checkbox = "Checkbox";
    public const string Date = "Date";
    public const string Number = "Number";

    public static readonly string[] All = [ShortText, LongText, SingleSelect, MultiSelect, Checkbox, Date, Number];

    public static bool HasOptions(string type) => type is SingleSelect or MultiSelect;
}

/// <summary>A department's structured way of creating a normal ticket (not a service catalogue item: no approvals, no fulfilment workflow).</summary>
internal sealed class RequestForm
{
    public Guid Id { get; set; }

    public Guid DepartmentId { get; set; }

    public string Name { get; set; } = "";

    public string? Description { get; set; }

    public bool IsActive { get; set; } = true;

    /// <summary>Shown in the requester portal. Inactive or hidden forms accept no submissions.</summary>
    public bool VisibleToRequesters { get; set; } = true;

    public int SortOrder { get; set; }

    /// <summary>Server-side defaults applied to every ticket created from the form. The requester never chooses them.</summary>
    public Guid? DefaultTeamId { get; set; }

    public TicketPriority? DefaultPriority { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public uint Version { get; set; }
}

/// <summary>
/// One custom field of a form. <see cref="Key"/> is the stable identity (never the label); fields are deactivated, never deleted, so tickets
/// that hold values keep their meaning. The type cannot change after creation.
/// </summary>
internal sealed class FormField
{
    public Guid Id { get; set; }

    public Guid FormId { get; set; }

    public Guid DepartmentId { get; set; }

    public string Key { get; set; } = "";

    public string Label { get; set; } = "";

    public string? HelpText { get; set; }

    public string FieldType { get; set; } = Domain.FieldTypes.ShortText;

    public bool Required { get; set; }

    /// <summary>JSON array of <c>{ value, label, isActive }</c> for select types; option values are stable, labels may change.</summary>
    public string Options { get; set; } = "[]";

    public int SortOrder { get; set; }

    public bool IsActive { get; set; } = true;

    /// <summary>Asked on the portal form and shown on the requester's ticket. Fields that are not requester visible can never be submitted by a requester.</summary>
    public bool RequesterVisible { get; set; } = true;

    /// <summary>Shown to agents on the ticket.</summary>
    public bool AgentVisible { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public uint Version { get; set; }
}

/// <summary>
/// A validated, typed value submitted on a ticket. The definition is snapshotted (label, type, visibility, rendered display text), so renaming or
/// deactivating a field later never changes what an existing ticket shows. Values are immutable.
/// </summary>
internal sealed class TicketFieldValue
{
    public Guid Id { get; set; }

    public Guid TicketId { get; set; }

    public Guid DepartmentId { get; set; }

    public Guid? FormId { get; set; }

    public Guid? FieldId { get; set; }

    public string Key { get; set; } = "";

    public string Label { get; set; } = "";

    public string FieldType { get; set; } = "";

    /// <summary>Typed JSON: string, number, boolean or array of option values.</summary>
    public string ValueJson { get; set; } = "null";

    /// <summary>Human text as rendered at submission (select labels resolved), also the searchable text.</summary>
    public string Display { get; set; } = "";

    public bool RequesterVisible { get; set; }

    public bool AgentVisible { get; set; }

    public int SortOrder { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
