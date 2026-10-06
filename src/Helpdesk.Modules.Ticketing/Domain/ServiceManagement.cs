namespace Helpdesk.Modules.Ticketing.Domain;

/// <summary>A department's working calendar. Weekly hours and exceptions are validated JSON (see <see cref="Sla.CalendarDefinition"/>).</summary>
internal sealed class BusinessHoursCalendar
{
    public Guid Id { get; set; }

    public Guid DepartmentId { get; set; }

    public string Name { get; set; } = "";

    /// <summary>IANA time zone id (for example Europe/London). DST is handled by the framework time zone database, never by hand.</summary>
    public string TimeZoneId { get; set; } = "";

    public bool IsActive { get; set; } = true;

    /// <summary>Normalised JSON of the weekly intervals.</summary>
    public string Weekly { get; set; } = "[]";

    /// <summary>Normalised JSON of the date exceptions (full-day closures and date-specific alternative hours).</summary>
    public string Exceptions { get; set; } = "[]";

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public uint Version { get; set; }
}

/// <summary>
/// A department SLA policy. Policies are evaluated in explicit order (<see cref="SortOrder"/>, then creation) and the first active
/// match wins; policies are never combined.
/// </summary>
internal sealed class SlaPolicy
{
    public Guid Id { get; set; }

    public Guid DepartmentId { get; set; }

    public string Name { get; set; } = "";

    public bool IsActive { get; set; } = true;

    public int SortOrder { get; set; }

    /// <summary>Normalised JSON array of structured conditions (all must match; empty = every ticket).</summary>
    public string Conditions { get; set; } = "[]";

    /// <summary>Null = round the clock (24 x 7, UTC).</summary>
    public Guid? CalendarId { get; set; }

    public int? FirstResponseMinutes { get; set; }

    public int? ResolutionMinutes { get; set; }

    /// <summary>The resolution clock pauses while the ticket is Pending (the only status that can pause it).</summary>
    public bool PauseWhenPending { get; set; }

    /// <summary>How long before a deadline the clock counts as "approaching" (wall-clock minutes).</summary>
    public int ApproachingLeadMinutes { get; set; } = 60;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public uint Version { get; set; }
}

/// <summary>
/// The SLA applied to one ticket. <see cref="Snapshot"/> freezes the policy, targets and calendar that were in force when it was
/// applied: later edits never rewrite it and old deadlines are never recalculated from today's configuration.
/// </summary>
internal sealed class TicketSla
{
    public Guid TicketId { get; set; }

    public Guid DepartmentId { get; set; }

    /// <summary>Informational only (the policy may since have been edited or deleted); the snapshot is authoritative.</summary>
    public Guid? PolicyId { get; set; }

    public string Snapshot { get; set; } = "{}";

    public DateTimeOffset AppliedAt { get; set; }

    /// <summary>Copied from the snapshot so warnings can be computed without parsing it.</summary>
    public int ApproachingLeadMinutes { get; set; } = 60;

    public int? FirstResponseTargetMinutes { get; set; }

    public DateTimeOffset? FirstResponseDueAt { get; set; }

    public DateTimeOffset? FirstResponseMetAt { get; set; }

    public DateTimeOffset? FirstResponseWarnedAt { get; set; }

    public DateTimeOffset? FirstResponseBreachedAt { get; set; }

    public int? ResolutionTargetMinutes { get; set; }

    /// <summary>Null while paused, met, or when the policy has no resolution target.</summary>
    public DateTimeOffset? ResolutionDueAt { get; set; }

    public DateTimeOffset? ResolutionPausedAt { get; set; }

    /// <summary>Business seconds left on the resolution clock while it is paused.</summary>
    public long? ResolutionRemainingSeconds { get; set; }

    public DateTimeOffset? ResolutionMetAt { get; set; }

    public DateTimeOffset? ResolutionWarnedAt { get; set; }

    public DateTimeOffset? ResolutionBreachedAt { get; set; }

    /// <summary>Starts at 1; incremented when a resolved or closed ticket is reopened (a new resolution cycle with the full target).</summary>
    public int ResolutionCycle { get; set; } = 1;

    /// <summary>Earliest due time among clocks that are running and unmet (drives the "breached / due soon" filters and sorting).</summary>
    public DateTimeOffset? NextDueAt { get; set; }

    /// <summary>Earliest warning time among clocks that are running and unmet (drives "approaching").</summary>
    public DateTimeOffset? NextWarnAt { get; set; }

    /// <summary>Next moment something must be recorded (a warning or a breach); the monitor selects on this indexed column only.</summary>
    public DateTimeOffset? NextEventAt { get; set; }
}

/// <summary>An explainable history entry of a ticket's SLA (applied, paused, met, reopened, approaching, breached). Content free.</summary>
internal sealed class SlaEvent
{
    public Guid Id { get; set; }

    public Guid TicketId { get; set; }

    public Guid DepartmentId { get; set; }

    public DateTimeOffset At { get; set; }

    public string Kind { get; set; } = "";

    /// <summary>Which clock (FirstResponse, Resolution) or null for the SLA as a whole.</summary>
    public string? Clock { get; set; }

    public string Detail { get; set; } = "{}";
}

internal sealed class AutomationRule
{
    public Guid Id { get; set; }

    public Guid DepartmentId { get; set; }

    public string Name { get; set; } = "";

    public bool IsActive { get; set; } = true;

    public string Trigger { get; set; } = "";

    public int SortOrder { get; set; }

    /// <summary>Explicit and never implicit: when true and the rule matched, no later rule runs for this event.</summary>
    public bool StopProcessing { get; set; }

    public string Conditions { get; set; } = "[]";

    public string Actions { get; set; } = "[]";

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public uint Version { get; set; }
}

internal static class ExecutionStates
{
    public const string Running = "Running";
    public const string Completed = "Completed";
    public const string Failed = "Failed";
    public const string Skipped = "Skipped";
}

/// <summary>
/// One evaluation of one rule for one triggering event. The unique key (rule, ticket, trigger key) makes retried events idempotent;
/// the chain id, depth and provenance explain why a rule ran and keep automation from triggering itself endlessly.
/// </summary>
internal sealed class AutomationExecution
{
    public Guid Id { get; set; }

    public Guid DepartmentId { get; set; }

    public Guid RuleId { get; set; }

    /// <summary>The rule's name at execution time (the rule may be renamed or removed later).</summary>
    public string RuleName { get; set; } = "";

    public Guid TicketId { get; set; }

    public string TicketReference { get; set; } = "";

    public string Trigger { get; set; } = "";

    public string TriggerKey { get; set; } = "";

    public Guid ChainId { get; set; }

    public int Depth { get; set; }

    /// <summary>What caused the event: user, email, sla, schedule or automation.</summary>
    public string Provenance { get; set; } = "";

    public string State { get; set; } = ExecutionStates.Running;

    public bool Matched { get; set; }

    /// <summary>JSON trace of every evaluated condition (field, expected, actual summary, result).</summary>
    public string Conditions { get; set; } = "[]";

    /// <summary>JSON list of attempted actions and their outcomes.</summary>
    public string Actions { get; set; } = "[]";

    public string? Error { get; set; }

    public string? SkipReason { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }
}
