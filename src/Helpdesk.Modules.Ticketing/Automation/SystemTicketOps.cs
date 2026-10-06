using System.Globalization;
using Helpdesk.Modules.Mail.Contracts;
using Helpdesk.Modules.Organisation.Contracts;
using Helpdesk.Modules.Ticketing.Domain;
using Helpdesk.Modules.Ticketing.Persistence;
using Helpdesk.Modules.Ticketing.Services;
using Helpdesk.Modules.Ticketing.Sla;
using Helpdesk.SharedKernel.Errors;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Modules.Ticketing.Automation;

internal sealed record OpOutcome(string Outcome, string? Detail, IReadOnlyList<TicketChange> Changes)
{
    public static OpOutcome Failed(string detail) => new(ActionOutcomes.Failed, detail, []);

    public static OpOutcome NoChange(string? detail = null) => new(ActionOutcomes.NoChange, detail, []);
}

/// <summary>
/// Checks that every team, user and mailbox a rule names belongs to the rule's own department. Used when a rule is saved AND again
/// when it runs, so a crafted payload or a later change can never make automation reach into another department.
/// </summary>
internal sealed class RuleReferenceChecker(IOrganisationDirectory organisation, IMailboxDirectory mailboxes, TicketingDbContext db)
{
    public async Task<string?> CheckTeamAsync(Guid departmentId, string value, bool requireActive, CancellationToken ct)
    {
        if (value == NoneValue.Value)
        {
            return null;
        }

        var team = Guid.TryParse(value, out var id) ? await organisation.FindTeamAsync(departmentId, id, ct) : null;
        return team is null ? "The team does not belong to this department." : requireActive && !team.IsActive ? "The team is inactive." : null;
    }

    public async Task<string?> CheckAssigneeAsync(Guid departmentId, string value, CancellationToken ct)
    {
        if (value == NoneValue.Value)
        {
            return null;
        }

        return Guid.TryParse(value, out var id) && await organisation.IsEligibleAssigneeAsync(departmentId, id, ct)
            ? null
            : "The person is not an active member of this department who can work tickets.";
    }

    /// <summary>A type named by a condition only compares, so it may be inactive; it must be a type of THIS department.</summary>
    public async Task<string?> CheckTicketTypeAsync(Guid departmentId, string value, CancellationToken ct) =>
        value == NoneValue.Value || (Guid.TryParse(value, out var id) && await db.TicketTypes.AsNoTracking().AnyAsync(t => t.Id == id && t.DepartmentId == departmentId, ct))
            ? null
            : "The ticket type does not belong to this department.";

    public async Task<string?> CheckMailboxAsync(Guid departmentId, string value, CancellationToken ct)
    {
        if (value == NoneValue.Value)
        {
            return null;
        }

        return Guid.TryParse(value, out var id) && (await mailboxes.GetManyAsync(departmentId, [id], ct)).ContainsKey(id)
            ? null
            : "The mailbox does not belong to this department.";
    }

    /// <summary>Department checks for every id a set of conditions and actions mention. Returns field-path errors.</summary>
    public async Task<Dictionary<string, string[]>> CheckAsync(Guid departmentId, IReadOnlyList<RuleCondition> conditions, IReadOnlyList<RuleAction> actions, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        for (var i = 0; i < conditions.Count; i++)
        {
            var c = conditions[i];
            foreach (var value in c.Values.Where(_ => ConditionCatalogue.IsIdField(c.Field)))
            {
                var problem = c.Field switch
                {
                    ConditionFields.Team => await CheckTeamAsync(departmentId, value, requireActive: false, ct),
                    ConditionFields.Assignee => await CheckKnownUserAsync(departmentId, value, ct),
                    ConditionFields.TicketType => await CheckTicketTypeAsync(departmentId, value, ct),
                    _ => await CheckMailboxAsync(departmentId, value, ct),
                };
                if (problem is not null)
                {
                    errors[$"conditions[{i}]"] = [problem];
                    break;
                }
            }
        }

        for (var i = 0; i < actions.Count; i++)
        {
            var a = actions[i];
            var problem = a.Type switch
            {
                ActionTypes.AssignTeam => await CheckTeamAsync(departmentId, a.Value!, requireActive: true, ct),
                ActionTypes.AssignAgent => await CheckAssigneeAsync(departmentId, a.Value!, ct),
                _ => null,
            };
            if (problem is not null)
            {
                errors[$"actions[{i}]"] = [problem];
            }
        }

        return errors;
    }

    // A condition may name someone who is no longer eligible (it only compares); it must still be a person of this department.
    private async Task<string?> CheckKnownUserAsync(Guid departmentId, string value, CancellationToken ct) =>
        value == NoneValue.Value || (Guid.TryParse(value, out var id) && await organisation.IsEligibleAssigneeAsync(departmentId, id, ct))
            ? null
            : "The person is not an active member of this department who can work tickets.";
}

/// <summary>
/// The system principal's door into ticket mutations for automation. Each operation takes one ticket by (department, id), runs in its
/// own transaction with the same row lock, invariants, audit and SLA handling as the interactive paths, and never touches mail:
/// an internal note created here is Internal, authored by the system, with no delivery state and never queued to the outbox.
/// </summary>
internal sealed class SystemTicketOps(
    TicketingDbContext db,
    TicketingAudit audit,
    RuleReferenceChecker references,
    SlaEngine sla,
    ITicketEvents events,
    TimeProvider clock)
{
    public async Task<OpOutcome> ApplyAsync(Guid departmentId, Guid ticketId, RuleAction action, Guid executionId, int index, CancellationToken ct)
    {
        // Re-validate references at execution time: the department's teams and people may have changed since the rule was saved.
        var problem = action.Type switch
        {
            ActionTypes.AssignTeam => await references.CheckTeamAsync(departmentId, action.Value!, requireActive: true, ct),
            ActionTypes.AssignAgent => await references.CheckAssigneeAsync(departmentId, action.Value!, ct),
            _ => null,
        };
        if (problem is not null)
        {
            return OpOutcome.Failed(problem);
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM ticketing.tickets WHERE id = {ticketId} AND department_id = {departmentId} FOR UPDATE", ct);
        var ticket = await db.Tickets.IgnoreDepartmentScope().SingleOrDefaultAsync(t => t.Id == ticketId && t.DepartmentId == departmentId, ct);
        if (ticket is null)
        {
            return OpOutcome.Failed("The ticket no longer exists in this department.");
        }

        await db.Entry(ticket).ReloadAsync(ct);
        var now = clock.GetUtcNowMicroseconds();
        var via = new { ruleExecutionId = executionId, action = index };
        var changes = new List<TicketChange>();
        string eventKind;

        switch (action.Type)
        {
            case ActionTypes.SetStatus:
            {
                var target = Enum.Parse<TicketStatus>(action.Value!);
                var previous = ticket.Status;
                if (!ticket.ApplyStatus(target, now))
                {
                    return OpOutcome.NoChange();
                }

                ticket.UpdatedAt = now;
                await db.SaveChangesAsync(ct);
                await audit.WriteSystemAsync(TicketAuditActions.StatusChanged, TicketingAudit.TicketObject, ticket.Id, departmentId,
                    new { status = previous.ToString() }, new { status = target.ToString(), reason = TicketAuditActions.ReasonAutomation, via }, ct);
                var change = new TicketChange(ChangeKind.Status, previous.ToString(), target.ToString());
                changes.Add(change);
                await sla.OnChangesAsync(ticket, [change], now, ct);
                eventKind = TicketEventKinds.Status;
                break;
            }

            case ActionTypes.SetPriority:
            {
                var target = Enum.Parse<TicketPriority>(action.Value!);
                if (target == ticket.Priority)
                {
                    return OpOutcome.NoChange();
                }

                var previous = ticket.Priority;
                ticket.Priority = target;
                ticket.UpdatedAt = now;
                await db.SaveChangesAsync(ct);
                await audit.WriteSystemAsync(TicketAuditActions.PriorityChanged, TicketingAudit.TicketObject, ticket.Id, departmentId,
                    new { priority = previous.ToString() }, new { priority = target.ToString(), reason = TicketAuditActions.ReasonAutomation, via }, ct);
                changes.Add(new TicketChange(ChangeKind.Priority, previous.ToString(), target.ToString()));
                eventKind = TicketEventKinds.Priority;
                break;
            }

            case ActionTypes.AssignTeam:
            {
                Guid? target = action.Value == NoneValue.Value ? null : Guid.Parse(action.Value!);
                if (target == ticket.TeamId)
                {
                    return OpOutcome.NoChange();
                }

                var previous = ticket.TeamId;
                ticket.TeamId = target;
                ticket.UpdatedAt = now;
                await db.SaveChangesAsync(ct);
                await audit.WriteSystemAsync(TicketAuditActions.TeamChanged, TicketingAudit.TicketObject, ticket.Id, departmentId,
                    new { teamId = previous }, new { teamId = target, reason = TicketAuditActions.ReasonAutomation, via }, ct);
                changes.Add(new TicketChange(ChangeKind.Team, previous?.ToString(), target?.ToString()));
                eventKind = TicketEventKinds.Assignment;
                break;
            }

            case ActionTypes.AssignAgent:
            {
                Guid? target = action.Value == NoneValue.Value ? null : Guid.Parse(action.Value!);
                if (target == ticket.AssigneeUserId)
                {
                    return OpOutcome.NoChange();
                }

                var previous = ticket.AssigneeUserId;
                ticket.AssigneeUserId = target;
                ticket.UpdatedAt = now;
                await db.SaveChangesAsync(ct);
                await audit.WriteSystemAsync(TicketAuditActions.AssigneeChanged, TicketingAudit.TicketObject, ticket.Id, departmentId,
                    new { assigneeUserId = previous }, new { assigneeUserId = target, reason = TicketAuditActions.ReasonAutomation, via }, ct);
                changes.Add(new TicketChange(ChangeKind.Assignee, previous?.ToString(), target?.ToString()));
                eventKind = TicketEventKinds.Assignment;
                break;
            }

            case ActionTypes.AddInternalNote:
            {
                // Idempotent: the same execution and action index can never add a second note (unique source reference).
                var sourceRef = string.Create(CultureInfo.InvariantCulture, $"automation:{executionId:N}:{index}");
                if (await db.Messages.IgnoreDepartmentScope().AnyAsync(m => m.DepartmentId == departmentId && m.Source == MessageSources.Automation && m.SourceRef == sourceRef, ct))
                {
                    return OpOutcome.NoChange("already added");
                }

                var message = new Message
                {
                    Id = Guid.CreateVersion7(),
                    TicketId = ticket.Id,
                    DepartmentId = departmentId,
                    Kind = MessageKind.Internal,
                    AuthorType = AuthorType.System,
                    Body = action.Text!,
                    Source = MessageSources.Automation,
                    SourceRef = sourceRef,
                    DeliveryState = DeliveryState.None,
                    CreatedAt = now,
                };
                db.Messages.Add(message); // the ticket row is untouched: a private note must not change UpdatedAt or the row version
                await db.SaveChangesAsync(ct);
                await audit.WriteSystemAsync(TicketAuditActions.MessageAdded, TicketingAudit.TicketObject, ticket.Id, departmentId, null,
                    new { messageId = message.Id, kind = message.Kind.ToString(), authorType = message.AuthorType.ToString(), reason = TicketAuditActions.ReasonAutomation, via }, ct);
                eventKind = TicketEventKinds.Note;
                break;
            }

            default:
                return OpOutcome.Failed("Unknown action type.");
        }

        await tx.CommitAsync(ct);
        await events.PublishAsync(departmentId, ticket.Id, ticket.Reference, eventKind, null, ct);
        return new OpOutcome(ActionOutcomes.Done, null, changes);
    }
}
