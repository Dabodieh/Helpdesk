using System.Globalization;
using System.Text.Json;
using Helpdesk.Modules.Ticketing.Domain;
using Helpdesk.Modules.Ticketing.Persistence;
using Helpdesk.Modules.Ticketing.Services;
using Helpdesk.Modules.Ticketing.Sla;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Helpdesk.Modules.Ticketing.Automation;

/// <summary>
/// Evaluates a department's rules in explicit order for one triggering event. Event driven only (no scanning). Every evaluation is
/// recorded in the execution history; (rule, ticket, trigger key) is unique, so a retried event never repeats a rule's actions, and a
/// failed or interrupted execution resumes at the first action that did not complete.
/// </summary>
internal sealed partial class AutomationEngine(
    TicketingDbContext db,
    SystemTicketOps ops,
    TimeProvider clock,
    ILogger<AutomationEngine> log)
{
    /// <summary>An execution still marked Running after this long is treated as abandoned (the worker died) and may be resumed.</summary>
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(2);

    /// <summary>Runs automation for the changes of a ticket. Never throws: automation problems are recorded, they never fail the caller's request.</summary>
    public async Task DispatchAsync(Guid departmentId, Guid ticketId, IReadOnlyList<TicketChange> changes, AutomationChain? chain, string provenance, CancellationToken ct)
    {
        chain ??= AutomationChain.Start(provenance);
        foreach (var change in changes)
        {
            if (Triggers.For(change.Kind) is not { } trigger)
            {
                continue;
            }

            await RunAsync(departmentId, ticketId, trigger, $"{change.Kind}:{change.EventId:N}", chain, ct);
        }
    }

    public async Task RunAsync(Guid departmentId, Guid ticketId, string trigger, string triggerKey, AutomationChain chain, CancellationToken ct)
    {
        try
        {
            await RunCoreAsync(departmentId, ticketId, trigger, triggerKey, chain, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RunFailed(log, trigger, ex.GetType().Name);
        }
    }

    private async Task RunCoreAsync(Guid departmentId, Guid ticketId, string trigger, string triggerKey, AutomationChain chain, CancellationToken ct)
    {
        var rules = await db.AutomationRules.AsNoTracking()
            .Where(r => r.DepartmentId == departmentId && r.IsActive && r.Trigger == trigger)
            .OrderBy(r => r.SortOrder).ThenBy(r => r.CreatedAt).ThenBy(r => r.Id)
            .ToListAsync(ct);
        foreach (var rule in rules)
        {
            var facts = await LoadFactsAsync(departmentId, ticketId, ct);
            if (facts is null)
            {
                return;
            }

            var execution = await ClaimAsync(rule, facts.Value.Reference, ticketId, trigger, triggerKey, chain, ct);
            if (execution is null)
            {
                // Duplicate delivery of an event already handled. A matched rule that stops processing still stops it.
                if (rule.StopProcessing && await PreviouslyMatchedAsync(rule.Id, ticketId, triggerKey, ct))
                {
                    break;
                }

                continue;
            }

            if (execution.State == ExecutionStates.Skipped)
            {
                continue;
            }

            var stop = await ExecuteAsync(rule, execution, facts.Value.Facts, chain, ct);
            if (stop)
            {
                break;
            }
        }
    }

    private async Task<bool> ExecuteAsync(AutomationRule rule, AutomationExecution execution, TicketFacts facts, AutomationChain chain, CancellationToken ct)
    {
        List<ActionResult> results;
        if (execution.Actions != "[]" && execution.Matched)
        {
            results = StoredJson.Read<List<ActionResult>>(execution.Actions); // resuming
        }
        else
        {
            var conditions = StoredJson.Conditions(rule.Conditions);
            var trace = ConditionEvaluator.Evaluate(conditions, facts);
            execution.Matched = ConditionEvaluator.AllMatch(trace);
            execution.Conditions = StoredJson.Write(trace);
            var actions = StoredJson.Read<List<RuleAction>>(rule.Actions);
            results = execution.Matched
                ? [.. actions.Select((a, i) => new ActionResult(i, a.Type, a.Type == ActionTypes.AddInternalNote ? null : a.Value, ActionOutcomes.Pending, null))]
                : [];
            execution.Actions = StoredJson.Write(results);
            await SaveExecutionAsync(execution, ct);
        }

        if (!execution.Matched)
        {
            await FinishAsync(execution, ExecutionStates.Completed, null, ct);
            return false;
        }

        var ruleActions = StoredJson.Read<List<RuleAction>>(rule.Actions);
        var failed = false;
        var nested = new List<TicketChange>();
        for (var i = 0; i < results.Count; i++)
        {
            if (results[i].Outcome is ActionOutcomes.Done or ActionOutcomes.NoChange)
            {
                continue;
            }

            OpOutcome outcome;
            try
            {
                outcome = await ops.ApplyAsync(execution.DepartmentId, execution.TicketId, ruleActions[i], execution.Id, i, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                ActionFailed(log, ruleActions[i].Type, ex.GetType().Name);
                outcome = OpOutcome.Failed("The action could not be applied.");
            }

            results[i] = results[i] with { Outcome = outcome.Outcome, Detail = outcome.Detail };
            failed |= outcome.Outcome == ActionOutcomes.Failed;
            nested.AddRange(outcome.Changes);
            execution.Actions = StoredJson.Write(results);
            await SaveExecutionAsync(execution, ct);
        }

        await FinishAsync(execution, failed ? ExecutionStates.Failed : ExecutionStates.Completed, failed ? "One or more actions failed." : null, ct);

        // Changes made by automation raise their own triggers, one level deeper in the same chain (each (rule, ticket) runs once).
        if (nested.Count > 0)
        {
            var child = chain.Child();
            foreach (var change in nested)
            {
                if (Triggers.For(change.Kind) is { } trigger)
                {
                    await RunAsync(execution.DepartmentId, execution.TicketId, trigger, $"{execution.Id:N}:{change.Kind}:{change.EventId:N}", child, ct);
                }
            }
        }

        return rule.StopProcessing;
    }

    private async Task<(TicketFacts Facts, string Reference)?> LoadFactsAsync(Guid departmentId, Guid ticketId, CancellationToken ct)
    {
        var ticket = await db.Tickets.AsNoTracking().IgnoreDepartmentScope().Include(t => t.Requester)
            .SingleOrDefaultAsync(t => t.Id == ticketId && t.DepartmentId == departmentId, ct);
        if (ticket is null)
        {
            return null;
        }

        var sla = await db.TicketSlas.AsNoTracking().IgnoreDepartmentScope().SingleOrDefaultAsync(s => s.TicketId == ticketId && s.DepartmentId == departmentId, ct);
        return (FactsOf(ticket, sla, clock.GetUtcNow()), ticket.Reference);
    }

    public static TicketFacts FactsOf(Ticket ticket, TicketSla? sla, DateTimeOffset now) =>
        new(ticket.Status, ticket.Priority, ticket.TeamId, ticket.AssigneeUserId, ticket.MailboxId, ticket.Subject, ticket.Requester.EmailNormalised,
            SlaEngine.FirstResponseState(sla, now), SlaEngine.ResolutionState(sla, now), ticket.TicketTypeId);

    /// <summary>
    /// Records the start of an evaluation. Returns null for an event this rule already handled (duplicate), the existing row when it
    /// must be resumed, a Skipped row when loop protection stopped it, or a new Running row.
    /// </summary>
    private async Task<AutomationExecution?> ClaimAsync(AutomationRule rule, string reference, Guid ticketId, string trigger, string triggerKey, AutomationChain chain, CancellationToken ct)
    {
        var now = ClockExtensions.GetUtcNowMicroseconds(clock);
        var skipReason = chain.DepthExceeded ? "max_depth" : chain.BudgetExceeded ? "max_executions" : chain.AlreadyRan(rule.Id, ticketId) ? "loop_detected" : null;
        var id = Guid.CreateVersion7();
        var state = skipReason is null ? ExecutionStates.Running : ExecutionStates.Skipped;
        var inserted = await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO ticketing.automation_executions
                (id, department_id, rule_id, rule_name, ticket_id, ticket_reference, trigger, trigger_key, chain_id, depth, provenance, state, matched, conditions, actions, skip_reason, started_at, finished_at)
            VALUES
                ({id}, {rule.DepartmentId}, {rule.Id}, {rule.Name}, {ticketId}, {reference}, {trigger}, {triggerKey}, {chain.Id}, {chain.Depth}, {chain.Provenance}, {state}, false, '[]'::jsonb, '[]'::jsonb, {skipReason}, {now}, {(skipReason is null ? null : now)})
            ON CONFLICT (rule_id, ticket_id, trigger_key) DO NOTHING
            """,
            ct);
        if (inserted == 1)
        {
            if (skipReason is null)
            {
                chain.MarkRan(rule.Id, ticketId);
            }

            return await db.AutomationExecutions.SingleAsync(e => e.Id == id, ct);
        }

        var existing = await db.AutomationExecutions.SingleAsync(e => e.RuleId == rule.Id && e.TicketId == ticketId && e.TriggerKey == triggerKey, ct);
        var resumable = existing.State == ExecutionStates.Failed || (existing.State == ExecutionStates.Running && existing.StartedAt < clock.GetUtcNow() - Lease);
        if (!resumable)
        {
            return null;
        }

        existing.State = ExecutionStates.Running;
        existing.Error = null;
        existing.FinishedAt = null;
        await db.SaveChangesAsync(ct);
        chain.MarkRan(rule.Id, ticketId);
        return existing;
    }

    private Task<bool> PreviouslyMatchedAsync(Guid ruleId, Guid ticketId, string triggerKey, CancellationToken ct) =>
        db.AutomationExecutions.AsNoTracking().AnyAsync(e => e.RuleId == ruleId && e.TicketId == ticketId && e.TriggerKey == triggerKey && e.Matched, ct);

    private Task<int> SaveExecutionAsync(AutomationExecution execution, CancellationToken ct) => db.SaveChangesAsync(ct);

    private Task<int> FinishAsync(AutomationExecution execution, string state, string? error, CancellationToken ct)
    {
        execution.State = state;
        execution.Error = error;
        execution.FinishedAt = ClockExtensions.GetUtcNowMicroseconds(clock);
        return db.SaveChangesAsync(ct);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Automation run for trigger {Trigger} failed ({Error}); the request is unaffected")]
    private static partial void RunFailed(ILogger logger, string trigger, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Automation action {Action} failed ({Error})")]
    private static partial void ActionFailed(ILogger logger, string action, string error);
}
