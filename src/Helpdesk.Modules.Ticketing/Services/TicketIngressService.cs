using Helpdesk.Modules.Ticketing.Automation;
using Helpdesk.Modules.Ticketing.Contracts;
using Helpdesk.Modules.Ticketing.Domain;
using Helpdesk.Modules.Ticketing.Persistence;
using Helpdesk.Modules.Ticketing.Sla;
using Helpdesk.SharedKernel.Errors;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Modules.Ticketing.Services;

/// <summary>
/// <see cref="ITicketIngress"/>: system-principal writes with an explicit department (the receiving mailbox). Runs without a
/// caller scope, so it uses the named <c>IgnoreDepartmentScope()</c> bypass and always filters by department id itself.
/// </summary>
internal sealed class TicketIngressService(TicketingDbContext db, TicketingAudit audit, TicketLifecycle lifecycle, TimeProvider clock) : ITicketIngress
{
    public async Task<RequesterMessageResult> AddRequesterMessageAsync(
        Guid departmentId, Guid ticketId, Guid requesterId, string body, string source, CancellationToken cancellationToken = default)
    {
        var errors = new Dictionary<string, string[]>();
        TicketValidation.Body(errors, "body", body);
        if (!MessageSources.IsValid(source))
        {
            errors["source"] = [$"source must be one of {MessageSources.Web}, {MessageSources.Manual}."];
        }

        TicketValidation.ThrowIfAny(errors);

        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM ticketing.tickets WHERE id = {ticketId} AND department_id = {departmentId} FOR UPDATE", cancellationToken);
        var ticket = await db.Tickets.IgnoreDepartmentScope()
            .SingleOrDefaultAsync(t => t.Id == ticketId && t.DepartmentId == departmentId, cancellationToken) ?? throw new NotFoundException();
        var requesterExists = await db.Requesters.IgnoreDepartmentScope()
            .AnyAsync(r => r.Id == requesterId && r.DepartmentId == departmentId, cancellationToken);
        if (!requesterExists)
        {
            throw new NotFoundException();
        }

        var now = clock.GetUtcNowMicroseconds();
        var message = new Message
        {
            Id = Guid.CreateVersion7(),
            TicketId = ticket.Id,
            DepartmentId = departmentId,
            Kind = MessageKind.Public,
            AuthorType = AuthorType.Requester,
            AuthorRequesterId = requesterId,
            Body = body,
            Source = source,
            DeliveryState = DeliveryState.None,
            CreatedAt = now,
        };
        db.Messages.Add(message);

        // D-15: a late reply reopens the same ticket (team and assignee are retained); an Open ticket stays Open.
        var previous = ticket.Status;
        var reopened = ticket.ApplyStatus(TicketStatus.Open, now);
        ticket.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);

        await audit.WriteSystemAsync(TicketAuditActions.MessageAdded, TicketingAudit.TicketObject, ticket.Id, departmentId, null,
            new { messageId = message.Id, kind = message.Kind.ToString(), authorType = message.AuthorType.ToString() }, cancellationToken);
        if (reopened)
        {
            await audit.WriteSystemAsync(TicketAuditActions.StatusChanged, TicketingAudit.TicketObject, ticket.Id, departmentId,
                new { status = previous.ToString() },
                new { status = ticket.Status.ToString(), reason = TicketAuditActions.ReasonRequesterReply }, cancellationToken);
        }

        var changes = new List<TicketChange> { new(ChangeKind.RequesterMessage, MessageId: message.Id) };
        if (reopened)
        {
            changes.Add(new TicketChange(ChangeKind.Status, previous.ToString(), ticket.Status.ToString()));
        }

        await lifecycle.BeforeCommitAsync(ticket, "", changes, cancellationToken);
        await tx.CommitAsync(cancellationToken);
        await lifecycle.AfterCommitAsync(ticket, changes, source == MessageSources.Web || source == MessageSources.Manual ? Provenances.User : Provenances.Email, cancellationToken);
        return new RequesterMessageResult(message.Id, reopened);
    }
}
