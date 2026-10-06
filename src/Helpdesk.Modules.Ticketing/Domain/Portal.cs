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

    /// <summary>The kind of ticket this form creates (same department). The form is HOW it is submitted; the type is WHAT it is.</summary>
    public Guid? TicketTypeId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public uint Version { get; set; }
}

/// <summary>
/// A department's reusable ticket field definition. <see cref="Key"/> is the stable identity (never the label); fields are deactivated, never deleted,
/// so tickets that hold values keep their meaning. Key and type cannot change after creation. Where a field appears is configuration:
/// <see cref="AppliesToAllTypes"/> (common fields), <see cref="TicketTypeField"/> (per type) and <see cref="RequestFormField"/> (per form).
/// </summary>
internal sealed class TicketField
{
    public Guid Id { get; set; }

    public Guid DepartmentId { get; set; }

    public string Key { get; set; } = "";

    public string Label { get; set; } = "";

    public string? HelpText { get; set; }

    public string FieldType { get; set; } = Domain.FieldTypes.ShortText;

    /// <summary>Default requirement wherever the field is asked; a type or form link may override it.</summary>
    public bool Required { get; set; }

    /// <summary>JSON array of <c>{ value, label, isActive }</c> for select types; option values are stable, labels may change.</summary>
    public string Options { get; set; } = "[]";

    /// <summary>Order among the department field definitions (the order on a ticket comes from the type or form link).</summary>
    public int SortOrder { get; set; }

    public bool IsActive { get; set; } = true;

    /// <summary>Common field: applies to every ticket of the department whatever its type.</summary>
    public bool AppliesToAllTypes { get; set; }

    /// <summary>Asked on portal forms and shown on the requester ticket. A field that is not requester visible can never be submitted or read by a requester.</summary>
    public bool RequesterVisible { get; set; } = true;

    /// <summary>The requester may change the value after submitting (portal only; implies requester visible).</summary>
    public bool RequesterEditable { get; set; }

    /// <summary>Shown to and editable by agents on the ticket.</summary>
    public bool AgentVisible { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public uint Version { get; set; }
}

/// <summary>"What kind of ticket is this?" A department classification; deactivated, never deleted, so historical tickets keep their type.</summary>
internal sealed class TicketType
{
    public Guid Id { get; set; }

    public Guid DepartmentId { get; set; }

    public string Name { get; set; } = "";

    public string? Description { get; set; }

    public bool IsActive { get; set; } = true;

    /// <summary>At most one per department. Applied when a channel supplies no type (email, forms without a type, agent create).</summary>
    public bool IsDefault { get; set; }

    public int SortOrder { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public uint Version { get; set; }
}

/// <summary>A field included for a ticket type, with its order and an optional requirement override (null = the field own default).</summary>
internal sealed class TicketTypeField
{
    public Guid TypeId { get; set; }

    public Guid FieldId { get; set; }

    public Guid DepartmentId { get; set; }

    public int SortOrder { get; set; }

    public bool? Required { get; set; }
}

/// <summary>A field asked on a request form, with its order and an optional requirement override (null = the field own default).</summary>
internal sealed class RequestFormField
{
    public Guid FormId { get; set; }

    public Guid FieldId { get; set; }

    public Guid DepartmentId { get; set; }

    public int SortOrder { get; set; }

    public bool? Required { get; set; }
}

/// <summary>
/// A validated, typed value submitted on a ticket. The definition is snapshotted (label, type, visibility, rendered display text), so renaming or
/// deactivating a field later never changes what an existing ticket shows. One row per ticket and key; rewritten (snapshot refreshed) when an agent or an editable requester changes the value.
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

    public DateTimeOffset UpdatedAt { get; set; }
}
