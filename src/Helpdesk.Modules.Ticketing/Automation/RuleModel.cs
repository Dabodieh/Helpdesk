using Helpdesk.Modules.Ticketing.Domain;
using Helpdesk.Modules.Ticketing.Sla;

namespace Helpdesk.Modules.Ticketing.Automation;

internal static class Triggers
{
    public const string TicketCreated = "TicketCreated";
    public const string RequesterMessageAdded = "RequesterMessageAdded";
    public const string StatusChanged = "StatusChanged";
    public const string PriorityChanged = "PriorityChanged";
    public const string AssigneeChanged = "AssigneeChanged";
    public const string SlaApproaching = "SlaApproaching";
    public const string SlaBreached = "SlaBreached";

    public static readonly string[] All =
    [
        TicketCreated, RequesterMessageAdded, StatusChanged, PriorityChanged, AssigneeChanged, SlaApproaching, SlaBreached,
    ];

    /// <summary>The trigger a ticket change raises, or null when the change is not an automation trigger.</summary>
    public static string? For(ChangeKind kind) => kind switch
    {
        ChangeKind.Created => TicketCreated,
        ChangeKind.RequesterMessage => RequesterMessageAdded,
        ChangeKind.Status => StatusChanged,
        ChangeKind.Priority => PriorityChanged,
        ChangeKind.Assignee => AssigneeChanged,
        _ => null,
    };
}

internal static class ActionTypes
{
    public const string SetStatus = "setStatus";
    public const string SetPriority = "setPriority";
    public const string AssignAgent = "assignAgent";
    public const string AddInternalNote = "addInternalNote";

    public static readonly string[] All = [SetStatus, SetPriority, AssignAgent, AddInternalNote];
}

/// <summary>One structured action. <c>value</c> carries the status, priority or id (or "none"); <c>text</c> only the internal note text.</summary>
internal sealed record RuleAction(string Type, string? Value, string? Text);

internal static class ActionOutcomes
{
    public const string Pending = "Pending";
    public const string Done = "Done";
    public const string NoChange = "NoChange";
    public const string Failed = "Failed";
    public const string Skipped = "Skipped";
}

/// <summary>Outcome of one action, stored in the execution history (never contains note text).</summary>
internal sealed record ActionResult(int Index, string Type, string? Value, string Outcome, string? Detail);

internal static class ActionCatalogue
{
    public const int MaxActions = 10;
    public const int MaxNoteLength = 1000;

    public static Dictionary<string, string[]> Validate(IReadOnlyList<RuleAction>? actions)
    {
        var errors = new Dictionary<string, string[]>();
        if (actions is null || actions.Count == 0)
        {
            errors["actions"] = ["Add at least one action."];
            return errors;
        }

        if (actions.Count > MaxActions)
        {
            errors["actions"] = [$"At most {MaxActions} actions are allowed."];
            return errors;
        }

        for (var i = 0; i < actions.Count; i++)
        {
            var a = actions[i];
            var at = $"actions[{i}]";
            if (a is null || a.Type is null || !ActionTypes.All.Contains(a.Type, StringComparer.Ordinal))
            {
                errors[at] = ["Unknown action type."];
                continue;
            }

            var problem = a.Type switch
            {
                ActionTypes.SetStatus => Enum.TryParse<TicketStatus>(a.Value, out var s) && Enum.IsDefined(s) ? null : "Choose a status.",
                ActionTypes.SetPriority => Enum.TryParse<TicketPriority>(a.Value, out var p) && Enum.IsDefined(p) ? null : "Choose a priority.",
                ActionTypes.AssignAgent => a.Value == NoneValue.Value || Guid.TryParse(a.Value, out _) ? null : "Choose a target, or 'none'.",
                ActionTypes.AddInternalNote => string.IsNullOrWhiteSpace(a.Text) || a.Text.Length > MaxNoteLength ? $"Note text must be 1 to {MaxNoteLength} characters." : null,
                _ => "Unknown action type.",
            };
            if (problem is not null)
            {
                errors[at] = [problem];
            }
        }

        return errors;
    }

    public static IReadOnlyList<RuleAction> Normalise(IReadOnlyList<RuleAction> actions) =>
        [.. actions.Select(a => a.Type == ActionTypes.AddInternalNote ? new RuleAction(a.Type, null, a.Text!.Trim()) : new RuleAction(a.Type, a.Value!.Trim(), null))];
}

/// <summary>
/// The causal chain of one automation run. <see cref="Seen"/> (rule, ticket) pairs make a rule run at most once per chain, and
/// <see cref="MaxDepth"/> / <see cref="MaxExecutions"/> bound everything else: automation can never trigger itself endlessly.
/// </summary>
internal sealed class AutomationChain
{
    public const int MaxDepth = 4;
    public const int MaxExecutions = 25;

    private readonly HashSet<(Guid Rule, Guid Ticket)> _seen;
    private readonly ChainCounter _counter;

    private AutomationChain(Guid id, int depth, string provenance, HashSet<(Guid, Guid)> seen, ChainCounter counter)
    {
        Id = id;
        Depth = depth;
        Provenance = provenance;
        _seen = seen;
        _counter = counter;
    }

    public Guid Id { get; }

    public int Depth { get; }

    /// <summary>What started the chain: user, email, sla, schedule.</summary>
    public string Provenance { get; }

    public static AutomationChain Start(string provenance) => new(Guid.CreateVersion7(), 0, provenance, [], new ChainCounter());

    public AutomationChain Child() => new(Id, Depth + 1, Provenance, _seen, _counter);

    public bool DepthExceeded => Depth > MaxDepth;

    public bool BudgetExceeded => _counter.Value >= MaxExecutions;

    public bool AlreadyRan(Guid rule, Guid ticket) => _seen.Contains((rule, ticket));

    public void MarkRan(Guid rule, Guid ticket)
    {
        _seen.Add((rule, ticket));
        _counter.Value++;
    }

    private sealed class ChainCounter
    {
        public int Value;
    }
}

/// <summary>Shared rule validation (shape only; references to users and mailboxes are checked against the department separately).</summary>
internal static class RuleShape
{
    public const int MaxNameLength = 100;

    public static Dictionary<string, string[]> Validate(string? name, string? trigger, IReadOnlyList<RuleCondition>? conditions, IReadOnlyList<RuleAction>? actions)
    {
        var errors = ConditionCatalogue.Validate(conditions, ConditionCatalogue.RuleFields, "conditions");
        foreach (var (key, value) in ActionCatalogue.Validate(actions))
        {
            errors[key] = value;
        }

        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > MaxNameLength)
        {
            errors["name"] = [$"Name must be 1 to {MaxNameLength} characters."];
        }

        if (trigger is null || !Triggers.All.Contains(trigger, StringComparer.Ordinal))
        {
            errors["trigger"] = ["Unknown trigger."];
        }

        return errors;
    }
}
