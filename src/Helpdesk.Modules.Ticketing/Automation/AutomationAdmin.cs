using System.Globalization;
using Helpdesk.Modules.Ticketing.Domain;
using Helpdesk.Modules.Ticketing.Persistence;
using Helpdesk.Modules.Ticketing.Services;
using Helpdesk.Modules.Ticketing.Sla;
using Helpdesk.SharedKernel.Authorization;
using Helpdesk.SharedKernel.Database;
using Helpdesk.SharedKernel.Errors;
using Helpdesk.SharedKernel.Paging;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Modules.Ticketing.Automation;

internal sealed record RuleRequest(
    string? Name, string? Trigger, bool? IsActive, bool? StopProcessing,
    IReadOnlyList<RuleCondition>? Conditions, IReadOnlyList<RuleAction>? Actions, string? Version);

internal sealed record RuleDto(
    Guid Id, Guid DepartmentId, string Name, bool IsActive, string Trigger, int Order, bool StopProcessing,
    IReadOnlyList<RuleCondition> Conditions, IReadOnlyList<RuleAction> Actions, DateTimeOffset UpdatedAt, string Version);

internal sealed record ExecutionDto(
    Guid Id, Guid RuleId, string RuleName, Guid TicketId, string TicketReference, string Trigger, Guid ChainId, int Depth, string Provenance,
    string State, bool Matched, IReadOnlyList<ConditionTrace> Conditions, IReadOnlyList<ActionResult> Actions, string? Error, string? SkipReason,
    DateTimeOffset StartedAt, DateTimeOffset? FinishedAt);

internal sealed record RuleTestRequest(string? Ticket, string? Trigger, Guid? RuleId);

internal sealed record PlannedAction(int Index, string Type, string? Value, string Result, string? Detail);

internal sealed record RuleTestEntry(
    Guid RuleId, string Name, string Trigger, bool Evaluated, bool Matched, bool StopsProcessing,
    IReadOnlyList<ConditionTrace> Conditions, IReadOnlyList<PlannedAction> Actions, string? Note);

internal sealed record RuleTestResult(string Ticket, string Trigger, bool WouldChangeAnything, IReadOnlyList<RuleTestEntry> Rules, string Notice);

/// <summary>
/// Rule administration, execution history and the "Test against ticket" dry run. Permission department.automation.manage is enforced by
/// the endpoint filter; every query filters by the department itself and every referenced team, user or mailbox is checked against it.
/// </summary>
internal sealed class AutomationAdminService(
    TicketingDbContext db,
    IDepartmentScope scope,
    TicketResolver resolver,
    TicketingAudit audit,
    RuleReferenceChecker references,
    IAuthorizer authorizer,
    TimeProvider clock)
{
    public const int MaxRules = 100;
    public const int DefaultHistoryLimit = 50;
    private const string Object = "automation_rule";

    public async Task<IReadOnlyList<RuleDto>> ListAsync(Guid departmentId, CancellationToken ct) =>
        [.. (await db.AutomationRules.AsNoTracking().Where(r => r.DepartmentId == departmentId)
            .OrderBy(r => r.Trigger).ThenBy(r => r.SortOrder).ThenBy(r => r.CreatedAt).ThenBy(r => r.Id).ToListAsync(ct)).Select(ToDto)];

    public async Task<RuleDto> CreateAsync(Guid departmentId, RuleRequest request, CancellationToken ct)
    {
        var errors = RuleShape.Validate(request.Name, request.Trigger, request.Conditions ?? [], request.Actions);
        TicketValidation.ThrowIfAny(errors);
        var conditions = ConditionCatalogue.Normalise(request.Conditions ?? []);
        var actions = ActionCatalogue.Normalise(request.Actions!);
        TicketValidation.ThrowIfAny(await references.CheckAsync(departmentId, conditions, actions, ct));
        if (await db.AutomationRules.CountAsync(r => r.DepartmentId == departmentId, ct) >= MaxRules)
        {
            throw new ConflictException($"A department can keep at most {MaxRules} automation rules.", ProblemCodes.InvalidState);
        }

        var now = ClockExtensions.GetUtcNowMicroseconds(clock);
        var last = await db.AutomationRules.Where(r => r.DepartmentId == departmentId && r.Trigger == request.Trigger).MaxAsync(r => (int?)r.SortOrder, ct) ?? 0;
        var row = new AutomationRule
        {
            Id = Guid.CreateVersion7(),
            DepartmentId = departmentId,
            Name = request.Name!.Trim(),
            IsActive = request.IsActive ?? true,
            Trigger = request.Trigger!,
            SortOrder = last + 10,
            StopProcessing = request.StopProcessing ?? false,
            Conditions = StoredJson.Write(conditions),
            Actions = StoredJson.Write(actions),
            CreatedAt = now,
            UpdatedAt = now,
        };
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.AutomationRules.Add(row);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(TicketAuditActions.AutomationRuleCreated, Object, row.Id, departmentId, null, Summary(row), ct);
        await tx.CommitAsync(ct);
        return ToDto(row);
    }

    public async Task<RuleDto> UpdateAsync(Guid departmentId, Guid id, RuleRequest request, CancellationToken ct)
    {
        var early = new Dictionary<string, string[]>();
        var version = TicketValidation.Version(early, request.Version);
        TicketValidation.ThrowIfAny(early);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var row = await db.AutomationRules.SingleOrDefaultAsync(r => r.Id == id && r.DepartmentId == departmentId, ct) ?? throw new NotFoundException();
        if (row.Version != version)
        {
            throw new ConflictException("The rule was changed elsewhere. Reload and retry.", ProblemCodes.VersionConflict);
        }

        var name = request.Name ?? row.Name;
        var trigger = request.Trigger ?? row.Trigger;
        var conditions = request.Conditions ?? StoredJson.Conditions(row.Conditions);
        var actions = request.Actions ?? StoredJson.Read<List<RuleAction>>(row.Actions);
        TicketValidation.ThrowIfAny(RuleShape.Validate(name, trigger, conditions, actions));
        var normalisedConditions = ConditionCatalogue.Normalise(conditions);
        var normalisedActions = ActionCatalogue.Normalise(actions);
        TicketValidation.ThrowIfAny(await references.CheckAsync(departmentId, normalisedConditions, normalisedActions, ct));

        db.Entry(row).Property(r => r.Version).OriginalValue = version;
        row.Name = name.Trim();
        row.IsActive = request.IsActive ?? row.IsActive;
        row.StopProcessing = request.StopProcessing ?? row.StopProcessing;
        row.Trigger = trigger;
        row.Conditions = StoredJson.Write(normalisedConditions);
        row.Actions = StoredJson.Write(normalisedActions);
        row.UpdatedAt = ClockExtensions.GetUtcNowMicroseconds(clock);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictException("The rule was changed elsewhere. Reload and retry.", ProblemCodes.VersionConflict);
        }

        await audit.WriteAsync(TicketAuditActions.AutomationRuleUpdated, Object, row.Id, departmentId, null, Summary(row), ct);
        await tx.CommitAsync(ct);
        return ToDto(row);
    }

    /// <summary>Renumbers the rules of one trigger 10, 20, 30... in the given order (all rules of that trigger, each once).</summary>
    public async Task<IReadOnlyList<RuleDto>> ReorderAsync(Guid departmentId, string? trigger, ReorderRequest request, CancellationToken ct)
    {
        if (trigger is null || !Triggers.All.Contains(trigger, StringComparer.Ordinal))
        {
            throw new RequestValidationException(new Dictionary<string, string[]> { ["trigger"] = ["Unknown trigger."] });
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var rows = await db.AutomationRules.Where(r => r.DepartmentId == departmentId && r.Trigger == trigger).ToListAsync(ct);
        var ids = request.Ids ?? [];
        if (ids.Count != rows.Count || ids.Distinct().Count() != ids.Count || !ids.All(i => rows.Any(r => r.Id == i)))
        {
            throw new RequestValidationException(new Dictionary<string, string[]> { ["ids"] = ["List every rule of this trigger exactly once."] });
        }

        var now = ClockExtensions.GetUtcNowMicroseconds(clock);
        for (var i = 0; i < ids.Count; i++)
        {
            var row = rows.Single(r => r.Id == ids[i]);
            if (row.SortOrder != (i + 1) * 10)
            {
                row.SortOrder = (i + 1) * 10;
                row.UpdatedAt = now;
            }
        }

        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(TicketAuditActions.AutomationRuleUpdated, Object, departmentId, departmentId, null, new { reordered = ids.Count, trigger }, ct);
        await tx.CommitAsync(ct);
        return await ListAsync(departmentId, ct);
    }

    // ----- history -----

    public async Task<PageDto<ExecutionDto>> HistoryAsync(Guid departmentId, Guid? ruleId, string? ticketReference, string? state, bool? matchedOnly, string? cursor, int? limit, CancellationToken ct)
    {
        // Executions name tickets, requester domains and condition traces: Department content. The administrative permission on the route also lets through a Platform
        // Admin without a membership, so ticket read by membership is required here as well (a non-member answers 404, like the department dashboard).
        await authorizer.RequireAsync(PermissionCodes.TicketsRead, departmentId, ct);

        var take = limit ?? DefaultHistoryLimit;
        if (take is < 1 or > 100)
        {
            throw new RequestValidationException("limit", "limit must be between 1 and 100.");
        }

        var query = db.AutomationExecutions.AsNoTracking().Where(e => e.DepartmentId == departmentId);
        if (ruleId is { } rule)
        {
            query = query.Where(e => e.RuleId == rule);
        }

        if (!string.IsNullOrWhiteSpace(ticketReference))
        {
            var reference = ticketReference.Trim().ToUpperInvariant();
            query = query.Where(e => e.TicketReference == reference);
        }

        if (!string.IsNullOrWhiteSpace(state))
        {
            query = query.Where(e => e.State == state);
        }

        if (matchedOnly == true)
        {
            query = query.Where(e => e.Matched);
        }

        if (cursor is not null)
        {
            var parts = cursor.Split('|');
            if (parts.Length != 2 || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) || !Guid.TryParse(parts[1], out var lastId)
                || ticks < 0 || ticks > DateTime.MaxValue.Ticks)
            {
                throw new RequestValidationException("cursor", "Invalid cursor.");
            }

            var at = new DateTimeOffset(ticks, TimeSpan.Zero);
            query = query.Where(e => e.StartedAt < at || (e.StartedAt == at && e.Id.CompareTo(lastId) < 0));
        }

        var rows = await query.OrderByDescending(e => e.StartedAt).ThenByDescending(e => e.Id).Take(take + 1).ToListAsync(ct);
        string? next = null;
        if (rows.Count > take)
        {
            rows.RemoveAt(rows.Count - 1);
            next = string.Create(CultureInfo.InvariantCulture, $"{rows[^1].StartedAt.UtcTicks}|{rows[^1].Id}");
        }

        return new PageDto<ExecutionDto>([.. rows.Select(ToDto)], next);
    }

    // ----- dry run -----

    /// <summary>
    /// Explains what a trigger would do to a ticket without changing anything: nothing is written, no execution row is recorded, no
    /// event is published. The ticket is looked up through the caller's own ticket scope, so a ticket the caller cannot read is not found.
    /// </summary>
    public async Task<RuleTestResult> TestAsync(Guid departmentId, RuleTestRequest request, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(request.Ticket))
        {
            errors["ticket"] = ["Enter a ticket reference such as IT-1042."];
        }

        TicketValidation.ThrowIfAny(errors);
        await scope.EnsureLoadedAsync(ct);
        var key = await resolver.FindAsync(request.Ticket!.Trim(), ct);
        if (key is null || key.DepartmentId != departmentId)
        {
            throw new NotFoundException();
        }

        var ticket = await db.Tickets.AsNoTracking().Include(t => t.Requester).SingleOrDefaultAsync(t => t.Id == key.Id, ct) ?? throw new NotFoundException();
        var sla = await db.TicketSlas.AsNoTracking().SingleOrDefaultAsync(s => s.TicketId == ticket.Id, ct);

        AutomationRule? single = null;
        if (request.RuleId is { } ruleId)
        {
            single = await db.AutomationRules.AsNoTracking().SingleOrDefaultAsync(r => r.Id == ruleId && r.DepartmentId == departmentId, ct) ?? throw new NotFoundException();
        }

        var trigger = request.Trigger ?? single?.Trigger;
        if (trigger is null || !Triggers.All.Contains(trigger, StringComparer.Ordinal) || (single is not null && single.Trigger != trigger))
        {
            throw new RequestValidationException("trigger", "Choose the trigger to simulate (it must match the rule's trigger).");
        }

        var rules = single is not null
            ? [single]
            : await db.AutomationRules.AsNoTracking().Where(r => r.DepartmentId == departmentId && r.IsActive && r.Trigger == trigger)
                .OrderBy(r => r.SortOrder).ThenBy(r => r.CreatedAt).ThenBy(r => r.Id).ToListAsync(ct);

        var facts = AutomationEngine.FactsOf(ticket, sla, clock.GetUtcNow());
        var entries = new List<RuleTestEntry>();
        var stopped = false;
        var anything = false;
        foreach (var rule in rules)
        {
            if (stopped)
            {
                entries.Add(new RuleTestEntry(rule.Id, rule.Name, rule.Trigger, false, false, rule.StopProcessing, [], [], "Not evaluated: an earlier rule stops further processing."));
                continue;
            }

            var trace = ConditionEvaluator.Evaluate(StoredJson.Conditions(rule.Conditions), facts);
            var matched = ConditionEvaluator.AllMatch(trace);
            var planned = new List<PlannedAction>();
            if (matched)
            {
                var actions = StoredJson.Read<List<RuleAction>>(rule.Actions);
                for (var i = 0; i < actions.Count; i++)
                {
                    var (result, detail, after) = await PlanAsync(departmentId, actions[i], facts, ct);
                    planned.Add(new PlannedAction(i, actions[i].Type, actions[i].Type == ActionTypes.AddInternalNote ? null : actions[i].Value, result, detail));
                    facts = after; // later rules see the ticket as earlier rules would leave it
                    anything |= result == "WouldChange";
                }
            }

            entries.Add(new RuleTestEntry(rule.Id, rule.Name, rule.Trigger, true, matched, rule.StopProcessing, trace, planned, matched && rule.StopProcessing ? "Matched: stops further processing." : null));
            stopped = matched && rule.StopProcessing;
        }

        return new RuleTestResult(ticket.Reference, trigger, anything, entries, "Nothing was changed. Effects of one rule are applied to the next rule's evaluation here, but changes made by actions are not re-run as new triggers.");
    }

    private async Task<(string Result, string? Detail, TicketFacts After)> PlanAsync(Guid departmentId, RuleAction action, TicketFacts facts, CancellationToken ct)
    {
        switch (action.Type)
        {
            case ActionTypes.SetStatus:
            {
                var target = Enum.Parse<TicketStatus>(action.Value!);
                return target == facts.Status ? ("WouldNotChange", "Already " + target, facts) : ("WouldChange", $"{facts.Status} to {target}", facts with { Status = target });
            }

            case ActionTypes.SetPriority:
            {
                var target = Enum.Parse<TicketPriority>(action.Value!);
                return target == facts.Priority ? ("WouldNotChange", "Already " + target, facts) : ("WouldChange", $"{facts.Priority} to {target}", facts with { Priority = target });
            }

            case ActionTypes.AssignTeam:
            {
                var problem = await references.CheckTeamAsync(departmentId, action.Value!, requireActive: true, ct);
                if (problem is not null)
                {
                    return ("WouldFail", problem, facts);
                }

                Guid? target = action.Value == NoneValue.Value ? null : Guid.Parse(action.Value!);
                return target == facts.TeamId ? ("WouldNotChange", "Team already set", facts) : ("WouldChange", "Team would change", facts with { TeamId = target });
            }

            case ActionTypes.AssignAgent:
            {
                var problem = await references.CheckAssigneeAsync(departmentId, action.Value!, ct);
                if (problem is not null)
                {
                    return ("WouldFail", problem, facts);
                }

                Guid? target = action.Value == NoneValue.Value ? null : Guid.Parse(action.Value!);
                return target == facts.AssigneeUserId ? ("WouldNotChange", "Assignee already set", facts) : ("WouldChange", "Assignee would change", facts with { AssigneeUserId = target });
            }

            default:
                return ("WouldChange", "An internal note (staff only, never emailed) would be added", facts);
        }
    }

    private static object Summary(AutomationRule r) => new { name = r.Name, trigger = r.Trigger, isActive = r.IsActive, order = r.SortOrder, stopProcessing = r.StopProcessing };

    private static RuleDto ToDto(AutomationRule r) =>
        new(r.Id, r.DepartmentId, r.Name, r.IsActive, r.Trigger, r.SortOrder, r.StopProcessing, StoredJson.Conditions(r.Conditions),
            StoredJson.Read<List<RuleAction>>(r.Actions), r.UpdatedAt, ConcurrencyVersion.ToToken(r.Version));

    private static ExecutionDto ToDto(AutomationExecution e) =>
        new(e.Id, e.RuleId, e.RuleName, e.TicketId, e.TicketReference, e.Trigger, e.ChainId, e.Depth, e.Provenance, e.State, e.Matched,
            StoredJson.Read<List<ConditionTrace>>(e.Conditions), StoredJson.Read<List<ActionResult>>(e.Actions), e.Error, e.SkipReason, e.StartedAt, e.FinishedAt);
}
