using System.Text.Json;
using Helpdesk.Modules.Ticketing.Domain;

namespace Helpdesk.Modules.Ticketing.Sla;

/// <summary>One structured condition. Fields, operators and value shapes come from a closed set (<see cref="ConditionCatalogue"/>): no expressions, no regular expressions.</summary>
internal sealed record RuleCondition(string Field, string Operator, IReadOnlyList<string> Values);

internal static class ConditionFields
{
    public const string Status = "status";
    public const string Priority = "priority";
    public const string Team = "team";
    public const string Assignee = "assignee";
    public const string Mailbox = "mailbox";
    public const string SubjectContains = "subjectContains";
    public const string RequesterEmail = "requesterEmail";
    public const string RequesterDomain = "requesterDomain";
    public const string SlaFirstResponse = "slaFirstResponse";
    public const string SlaResolution = "slaResolution";
}

internal static class ConditionOperators
{
    public const string In = "in";
    public const string NotIn = "notIn";
    public const string Contains = "contains";
    public const string IsEqual = "equals";
}

/// <summary>Value used for "no team", "no assignee" and "no mailbox" in id conditions.</summary>
internal static class NoneValue
{
    public const string Value = "none";
}

internal static class SlaStates
{
    public const string None = "None";
    public const string Running = "Running";
    public const string Approaching = "Approaching";
    public const string Breached = "Breached";
    public const string Paused = "Paused";
    public const string Met = "Met";
    public const string MetLate = "MetLate";

    public static readonly string[] All = [None, Running, Approaching, Breached, Paused, Met, MetLate];
}

/// <summary>The ticket facts conditions are evaluated against (a plain snapshot, so evaluation is pure and testable).</summary>
internal sealed record TicketFacts(
    TicketStatus Status,
    TicketPriority Priority,
    Guid? TeamId,
    Guid? AssigneeUserId,
    Guid? MailboxId,
    string Subject,
    string RequesterEmailNormalised,
    string SlaFirstResponse,
    string SlaResolution)
{
    public string RequesterDomain => RequesterEmailNormalised[(RequesterEmailNormalised.LastIndexOf('@') + 1)..];
}

internal sealed record ConditionTrace(string Field, string Operator, IReadOnlyList<string> Values, string? Actual, bool Result);

internal static class ConditionCatalogue
{
    public const int MaxConditions = 10;
    public const int MaxValues = 20;
    public const int MaxTextLength = 200;

    private static readonly Dictionary<string, string[]> Operators = new(StringComparer.Ordinal)
    {
        [ConditionFields.Status] = [ConditionOperators.In, ConditionOperators.NotIn],
        [ConditionFields.Priority] = [ConditionOperators.In, ConditionOperators.NotIn],
        [ConditionFields.Team] = [ConditionOperators.In, ConditionOperators.NotIn],
        [ConditionFields.Assignee] = [ConditionOperators.In, ConditionOperators.NotIn],
        [ConditionFields.Mailbox] = [ConditionOperators.In, ConditionOperators.NotIn],
        [ConditionFields.SubjectContains] = [ConditionOperators.Contains],
        [ConditionFields.RequesterEmail] = [ConditionOperators.IsEqual],
        [ConditionFields.RequesterDomain] = [ConditionOperators.IsEqual],
        [ConditionFields.SlaFirstResponse] = [ConditionOperators.In, ConditionOperators.NotIn],
        [ConditionFields.SlaResolution] = [ConditionOperators.In, ConditionOperators.NotIn],
    };

    /// <summary>Fields usable for SLA policy matching (a policy is chosen when the ticket is created, so state-like fields do not apply).</summary>
    public static readonly string[] PolicyFields =
    [
        ConditionFields.Priority, ConditionFields.Team, ConditionFields.Mailbox,
        ConditionFields.SubjectContains, ConditionFields.RequesterEmail, ConditionFields.RequesterDomain,
    ];

    public static readonly string[] RuleFields = [.. Operators.Keys];

    public static bool IsIdField(string field) => field is ConditionFields.Team or ConditionFields.Assignee or ConditionFields.Mailbox;

    /// <summary>Shape validation only (no database): known field and operator, value count and form. Returns field-path errors.</summary>
    public static Dictionary<string, string[]> Validate(IReadOnlyList<RuleCondition>? conditions, string[] allowedFields, string path)
    {
        var errors = new Dictionary<string, string[]>();
        if (conditions is null)
        {
            return errors;
        }

        if (conditions.Count > MaxConditions)
        {
            errors[path] = [$"At most {MaxConditions} conditions are allowed."];
            return errors;
        }

        for (var i = 0; i < conditions.Count; i++)
        {
            var c = conditions[i];
            var at = $"{path}[{i}]";
            if (c is null || c.Field is null || !allowedFields.Contains(c.Field, StringComparer.Ordinal))
            {
                errors[at] = ["Unknown or unsupported condition field."];
                continue;
            }

            if (c.Operator is null || !Operators[c.Field].Contains(c.Operator, StringComparer.Ordinal))
            {
                errors[at] = [$"Operator must be one of {string.Join(", ", Operators[c.Field])} for {c.Field}."];
                continue;
            }

            var values = c.Values ?? [];
            var (min, max) = c.Operator is ConditionOperators.Contains or ConditionOperators.IsEqual ? (1, 1) : (1, MaxValues);
            if (values.Count < min || values.Count > max)
            {
                errors[at] = [min == max ? "Exactly one value is required." : $"Provide between 1 and {MaxValues} values."];
                continue;
            }

            var problem = values.Select(v => ValueProblem(c.Field, v)).FirstOrDefault(p => p is not null);
            if (problem is not null)
            {
                errors[at] = [problem];
            }
        }

        return errors;
    }

    private static string? ValueProblem(string field, string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaxTextLength)
        {
            return $"Values must be 1 to {MaxTextLength} characters.";
        }

        switch (field)
        {
            case ConditionFields.Status:
                return Enum.TryParse<TicketStatus>(value, out _) && Enum.IsDefined(Enum.Parse<TicketStatus>(value)) ? null : "Unknown status.";
            case ConditionFields.Priority:
                return Enum.TryParse<TicketPriority>(value, out _) && Enum.IsDefined(Enum.Parse<TicketPriority>(value)) ? null : "Unknown priority.";
            case ConditionFields.Team or ConditionFields.Assignee or ConditionFields.Mailbox:
                return value == NoneValue.Value || Guid.TryParse(value, out _) ? null : "Use an id, or 'none'.";
            case ConditionFields.SlaFirstResponse or ConditionFields.SlaResolution:
                return SlaStates.All.Contains(value) ? null : "Unknown SLA state.";
            case ConditionFields.RequesterDomain:
                return value.Contains('@') || value.Contains(' ') ? "Enter a domain such as example.com (no @, no spaces)." : null;
            case ConditionFields.RequesterEmail:
                return value.Contains('@') && !value.Contains(' ') ? null : "Enter a full email address.";
            default:
                return null;
        }
    }

    /// <summary>Canonical form stored in the database: trimmed, lower-cased where the match is case-insensitive.</summary>
    public static IReadOnlyList<RuleCondition> Normalise(IReadOnlyList<RuleCondition> conditions) =>
        [.. conditions.Select(c => new RuleCondition(
            c.Field,
            c.Operator,
            [.. c.Values.Select(v => c.Field is ConditionFields.RequesterEmail or ConditionFields.RequesterDomain ? v.Trim().ToLowerInvariant() : v.Trim())]))];
}

internal static class ConditionEvaluator
{
    /// <summary>Evaluates every condition (no short-circuit, so the trace explains the whole rule). The rule matches when all results are true.</summary>
    public static IReadOnlyList<ConditionTrace> Evaluate(IReadOnlyList<RuleCondition> conditions, TicketFacts facts) =>
        [.. conditions.Select(c => EvaluateOne(c, facts))];

    public static bool AllMatch(IReadOnlyList<ConditionTrace> trace) => trace.All(t => t.Result);

    private static ConditionTrace EvaluateOne(RuleCondition c, TicketFacts f)
    {
        var (actual, result) = c.Field switch
        {
            ConditionFields.Status => InSet(f.Status.ToString(), c),
            ConditionFields.Priority => InSet(f.Priority.ToString(), c),
            ConditionFields.Team => InSet(f.TeamId?.ToString() ?? NoneValue.Value, c),
            ConditionFields.Assignee => InSet(f.AssigneeUserId?.ToString() ?? NoneValue.Value, c),
            ConditionFields.Mailbox => InSet(f.MailboxId?.ToString() ?? NoneValue.Value, c),
            ConditionFields.SlaFirstResponse => InSet(f.SlaFirstResponse, c),
            ConditionFields.SlaResolution => InSet(f.SlaResolution, c),

            // The subject and the full address are never copied into the trace (it is stored and shown to administrators).
            ConditionFields.SubjectContains => ((string?)null, f.Subject.Contains(c.Values[0], StringComparison.OrdinalIgnoreCase)),
            ConditionFields.RequesterEmail => ((string?)null, string.Equals(f.RequesterEmailNormalised, c.Values[0], StringComparison.Ordinal)),
            ConditionFields.RequesterDomain => (f.RequesterDomain, string.Equals(f.RequesterDomain, c.Values[0], StringComparison.Ordinal)),
            _ => ((string?)null, false),
        };
        return new ConditionTrace(c.Field, c.Operator, c.Values, actual, result);
    }

    private static (string?, bool) InSet(string actual, RuleCondition c)
    {
        var contained = c.Values.Contains(actual, StringComparer.OrdinalIgnoreCase);
        return (actual, c.Operator == ConditionOperators.NotIn ? !contained : contained);
    }
}

internal static class StoredJson
{
    public static string Write<T>(T value) => JsonSerializer.Serialize(value, CalendarJson.Options);

    public static T Read<T>(string json) where T : class =>
        JsonSerializer.Deserialize<T>(json, CalendarJson.Options) ?? throw new InvalidOperationException("Stored JSON is empty.");

    public static IReadOnlyList<RuleCondition> Conditions(string json) => Read<List<RuleCondition>>(json);
}
