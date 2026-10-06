using Helpdesk.Modules.Ticketing.Domain;
using Helpdesk.Modules.Ticketing.Sla;

namespace Helpdesk.Host.Tests.Sla;

/// <summary>Pure condition evaluation: every field, both set operators, and that traces never carry ticket text.</summary>
public sealed class ConditionTests
{
    private static readonly Guid Team = Guid.NewGuid();
    private static readonly Guid Mailbox = Guid.NewGuid();
    private static readonly Guid Agent = Guid.NewGuid();

    private static TicketFacts Facts(Guid? team = null, Guid? assignee = null, Guid? mailbox = null) =>
        new(TicketStatus.Open, TicketPriority.High, team, assignee, mailbox, "Network down in Building 4", "Pat@Example.COM".ToLowerInvariant(), SlaStates.Running, SlaStates.Breached);

    private static bool Match(TicketFacts facts, string field, string op, params string[] values) =>
        ConditionEvaluator.AllMatch(ConditionEvaluator.Evaluate([new RuleCondition(field, op, values)], facts));

    [Fact]
    public void Set_conditions_match_in_and_not_in()
    {
        var f = Facts(Team, null, Mailbox);
        Assert.True(Match(f, "status", "in", "Open", "Pending"));
        Assert.False(Match(f, "status", "notIn", "Open"));
        Assert.True(Match(f, "priority", "in", "High"));
        Assert.True(Match(f, "team", "in", Team.ToString()));
        Assert.False(Match(f, "team", "in", "none"));
        Assert.True(Match(f, "assignee", "in", "none"));
        Assert.True(Match(Facts(assignee: Agent), "assignee", "notIn", "none"));
        Assert.True(Match(f, "mailbox", "in", Mailbox.ToString()));
        Assert.True(Match(Facts(), "mailbox", "in", "none"));
        Assert.True(Match(f, "slaResolution", "in", "Breached"));
        Assert.True(Match(f, "slaFirstResponse", "notIn", "Breached", "Paused"));
    }

    [Fact]
    public void Text_conditions_are_literal_and_case_insensitive()
    {
        var f = Facts();
        Assert.True(Match(f, "subjectContains", "contains", "NETWORK"));
        Assert.False(Match(f, "subjectContains", "contains", "net.*down")); // no regular expressions: the text is literal
        Assert.True(Match(f, "requesterDomain", "equals", "example.com"));
        Assert.False(Match(f, "requesterDomain", "equals", "example"));
        Assert.True(Match(f, "requesterEmail", "equals", "pat@example.com"));
    }

    [Fact]
    public void All_conditions_are_evaluated_and_must_all_match_and_the_trace_holds_no_ticket_text()
    {
        var trace = ConditionEvaluator.Evaluate(
            [new RuleCondition("status", "in", ["Closed"]), new RuleCondition("subjectContains", "contains", ["Building"]), new RuleCondition("requesterEmail", "equals", ["pat@example.com"])],
            Facts());
        Assert.Equal([false, true, true], trace.Select(t => t.Result).ToArray());
        Assert.False(ConditionEvaluator.AllMatch(trace));
        Assert.Null(trace[1].Actual);
        Assert.Null(trace[2].Actual);
        Assert.Equal("Open", trace[0].Actual);
    }

    [Fact]
    public void An_empty_condition_list_matches_every_ticket()
    {
        Assert.True(ConditionEvaluator.AllMatch(ConditionEvaluator.Evaluate([], Facts())));
    }
}
