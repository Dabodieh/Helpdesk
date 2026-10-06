using System.Globalization;
using Helpdesk.Modules.Mail.Contracts;
using Helpdesk.Modules.Organisation.Contracts;
using Helpdesk.Modules.Ticketing.Automation;
using Helpdesk.Modules.Ticketing.Domain;
using Helpdesk.Modules.Ticketing.Persistence;
using Helpdesk.Modules.Ticketing.Sla;
using Helpdesk.SharedKernel.Database;
using Helpdesk.SharedKernel.Errors;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Modules.Ticketing.Services;

/// <summary>Requester and participant upserts shared by the web flow and the mail sink (ON CONFLICT DO NOTHING: concurrent inserts never abort the transaction).</summary>
internal static class PeopleUpsert
{
    public static async Task<(Requester Requester, bool Created)> RequesterAsync(
        TicketingDbContext db, Guid departmentId, string email, string? displayName, DateTimeOffset now, CancellationToken ct)
    {
        var trimmed = email.Trim();
        var normalised = Requester.Normalise(trimmed);
        var name = string.IsNullOrWhiteSpace(displayName) ? trimmed : displayName.Trim();
        if (name.Length > TicketValidation.MaxDisplayName)
        {
            name = name[..TicketValidation.MaxDisplayName];
        }

        var inserted = await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO ticketing.requesters (id, department_id, email, email_normalised, display_name, created_at)
            VALUES ({Guid.CreateVersion7()}, {departmentId}, {trimmed}, {normalised}, {name}, {now})
            ON CONFLICT (department_id, email_normalised) DO NOTHING
            """,
            ct);
        var requester = await db.Requesters.AsNoTracking().IgnoreDepartmentScope().SingleAsync(r => r.DepartmentId == departmentId && r.EmailNormalised == normalised, ct);
        return (requester, inserted == 1);
    }

    public static async Task ParticipantAsync(
        TicketingDbContext db, Guid departmentId, Guid ticketId, MailParticipant person, DateTimeOffset now, CancellationToken ct)
    {
        var email = person.Address.Trim();
        if (email.Length is 0 or > TicketValidation.MaxEmail)
        {
            return;
        }

        var name = person.Name is { Length: > 0 } n ? (n.Length > TicketValidation.MaxDisplayName ? n[..TicketValidation.MaxDisplayName] : n) : null;
        await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO ticketing.ticket_participants (ticket_id, department_id, email_normalised, email, display_name, created_at)
            VALUES ({ticketId}, {departmentId}, {Requester.Normalise(email)}, {email}, {name}, {now})
            ON CONFLICT (ticket_id, email_normalised) DO NOTHING
            """,
            ct);
    }
}

/// <summary>
/// <see cref="IInboundTicketSink"/>: Mail's door into Ticketing. System principal with an explicit department (the receiving mailbox);
/// idempotent on <c>SourceRef</c> through the unique message index, so a retry after a crash returns the earlier result instead of
/// creating a second ticket or message. Uses the named scope bypass and always filters by department itself.
/// </summary>
internal sealed class InboundTicketSink(TicketingDbContext db, TicketingAudit audit, IOrganisationDirectory organisation, ITicketEvents events, TicketLifecycle lifecycle, TimeProvider clock) : IInboundTicketSink
{
    public async Task<InboundTicketResult> CreateTicketAsync(InboundTicketRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var errors = new Dictionary<string, string[]>();
        TicketValidation.Email(errors, "from", request.From.Address);
        TicketValidation.Subject(errors, "subject", request.Subject);
        TicketValidation.Body(errors, "body", request.BodyText);
        TicketValidation.ThrowIfAny(errors);

        var existing = await FindExistingAsync(request.DepartmentId, request.SourceRef, cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        var department = await organisation.FindDepartmentAsync(request.DepartmentId, cancellationToken) ?? throw new NotFoundException();
        if (!department.IsActive)
        {
            throw new ConflictException("The department is inactive.", ProblemCodes.Inactive);
        }

        var now = clock.GetUtcNowMicroseconds();
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        var (requester, requesterCreated) = await PeopleUpsert.RequesterAsync(db, request.DepartmentId, request.From.Address, request.From.Name, now, cancellationToken);
        var number = await TicketNumbering.AllocateAsync(db, request.DepartmentId, cancellationToken);
        // Email cannot choose a type: the department default applies when one is configured, otherwise the ticket is untyped. Never a reason to refuse mail.
        var defaultType = await db.TicketTypes.AsNoTracking().Where(t => t.DepartmentId == request.DepartmentId && t.IsDefault && t.IsActive).Select(t => (Guid?)t.Id).SingleOrDefaultAsync(cancellationToken);
        var ticket = new Ticket
        {
            Id = Guid.CreateVersion7(),
            DepartmentId = request.DepartmentId,
            Number = number,
            Reference = string.Create(CultureInfo.InvariantCulture, $"{department.Key}-{number}"),
            RequesterId = requester.Id,
            Subject = request.Subject.Trim(),
            Status = TicketStatus.Open,
            Priority = TicketPriority.Normal,
            CreatedByUserId = null,
            MailboxId = request.MailboxId,
            CreatedAt = now,
            UpdatedAt = now,
            LastPublicActivityAt = now,
            Source = TicketSources.Email,
            TicketTypeId = defaultType,
        };
        var first = new Message
        {
            Id = Guid.CreateVersion7(),
            TicketId = ticket.Id,
            DepartmentId = request.DepartmentId,
            Kind = MessageKind.Public,
            AuthorType = AuthorType.Requester,
            AuthorRequesterId = requester.Id,
            Body = request.BodyText,
            BodyHtml = request.BodyHtmlSanitised,
            Source = MessageSources.Email,
            SourceRef = request.SourceRef,
            DeliveryState = DeliveryState.None,
            CreatedAt = now,
        };
        db.Tickets.Add(ticket);
        db.Messages.Add(first);
        Message? note = null;
        if (!string.IsNullOrWhiteSpace(request.SystemNote))
        {
            note = new Message
            {
                Id = Guid.CreateVersion7(),
                TicketId = ticket.Id,
                DepartmentId = request.DepartmentId,
                Kind = MessageKind.Internal,
                AuthorType = AuthorType.System,
                Body = request.SystemNote,
                Source = MessageSources.Email,
                SourceRef = request.SourceRef + ":note",
                DeliveryState = DeliveryState.None,
                CreatedAt = now.AddTicks(10),
            };
            db.Messages.Add(note);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (PostgresErrors.IsUniqueViolation(ex, out var constraint) && constraint == "ux_messages_source_ref")
        {
            // A concurrent run created it first: roll back and return the winner.
            db.ChangeTracker.Clear();
            await tx.RollbackAsync(cancellationToken);
            var winner = await FindExistingAsync(request.DepartmentId, request.SourceRef, cancellationToken);
            if (winner is null)
            {
                throw;
            }

            return winner;
        }

        foreach (var person in request.Cc)
        {
            await PeopleUpsert.ParticipantAsync(db, request.DepartmentId, ticket.Id, person, now, cancellationToken);
        }

        if (requesterCreated)
        {
            await audit.WriteSystemAsync(TicketAuditActions.RequesterCreated, TicketingAudit.RequesterObject, requester.Id, request.DepartmentId, null, new { departmentId = request.DepartmentId }, cancellationToken);
        }

        await audit.WriteSystemAsync(
            TicketAuditActions.Created, TicketingAudit.TicketObject, ticket.Id, request.DepartmentId, null,
            new { reference = ticket.Reference, status = ticket.Status.ToString(), priority = ticket.Priority.ToString(), teamId = (Guid?)null, assigneeUserId = (Guid?)null, source = MessageSources.Email, mailboxId = request.MailboxId },
            cancellationToken);
        await audit.WriteSystemAsync(
            TicketAuditActions.MessageAdded, TicketingAudit.TicketObject, ticket.Id, request.DepartmentId, null,
            new { messageId = first.Id, kind = first.Kind.ToString(), authorType = first.AuthorType.ToString() }, cancellationToken);
        if (note is not null)
        {
            await audit.WriteSystemAsync(
                TicketAuditActions.MessageAdded, TicketingAudit.TicketObject, ticket.Id, request.DepartmentId, null,
                new { messageId = note.Id, kind = note.Kind.ToString(), authorType = note.AuthorType.ToString() }, cancellationToken);
        }

        TicketChange[] createdChange = [new TicketChange(ChangeKind.Created)];
        await lifecycle.BeforeCommitAsync(ticket, requester.EmailNormalised, createdChange, cancellationToken);
        await tx.CommitAsync(cancellationToken);
        await events.PublishAsync(request.DepartmentId, ticket.Id, ticket.Reference, TicketEventKinds.Created, null, cancellationToken);
        await lifecycle.AfterCommitAsync(ticket, createdChange, Provenances.Email, cancellationToken);
        return new InboundTicketResult(ticket.Id, ticket.Reference, first.Id, Created: true, Reopened: false);
    }

    public async Task<InboundTicketResult?> AddMessageAsync(InboundMessageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var errors = new Dictionary<string, string[]>();
        TicketValidation.Email(errors, "from", request.From.Address);
        TicketValidation.Body(errors, "body", request.BodyText);
        TicketValidation.ThrowIfAny(errors);

        var existing = await FindExistingAsync(request.DepartmentId, request.SourceRef, cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        var now = clock.GetUtcNowMicroseconds();

        // Strong correlation to a MERGED ticket's thread (headers/token matched upstream) continues on the canonical ticket instead of resurrecting
        // the merged one. Merging flattens (a source always points at the final canonical ticket), so one hop is enough; the loop is only a bound.
        var targetId = request.TicketId;
        for (var hop = 0; hop < 3; hop++)
        {
            var mergedInto = await db.Tickets.AsNoTracking().IgnoreDepartmentScope()
                .Where(t => t.Id == targetId && t.DepartmentId == request.DepartmentId).Select(t => t.MergedIntoTicketId).SingleOrDefaultAsync(cancellationToken);
            if (mergedInto is not { } next)
            {
                break;
            }

            targetId = next;
        }

        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlAsync(
            $"SELECT 1 FROM ticketing.tickets WHERE id = {targetId} AND department_id = {request.DepartmentId} FOR UPDATE", cancellationToken);
        var ticket = await db.Tickets.IgnoreDepartmentScope().SingleOrDefaultAsync(t => t.Id == targetId && t.DepartmentId == request.DepartmentId, cancellationToken);
        if (ticket is null)
        {
            await tx.RollbackAsync(cancellationToken);
            return null; // not a ticket of THIS department: the caller creates a new ticket instead
        }

        if (ticket.IsMerged)
        {
            // The canonical ticket was merged away between the lookup and the lock: fail so the mail job retries and resolves the new target.
            await tx.RollbackAsync(cancellationToken);
            throw new ConflictException("The ticket was merged while the message was processed.", TicketMergeCodes.Merged);
        }

        var (author, _) = await PeopleUpsert.RequesterAsync(db, request.DepartmentId, request.From.Address, request.From.Name, now, cancellationToken);
        var message = new Message
        {
            Id = Guid.CreateVersion7(),
            TicketId = ticket.Id,
            DepartmentId = request.DepartmentId,
            Kind = MessageKind.Public,
            AuthorType = AuthorType.Requester,
            AuthorRequesterId = author.Id,
            Body = request.BodyText,
            BodyHtml = request.BodyHtmlSanitised,
            Source = MessageSources.Email,
            SourceRef = request.SourceRef,
            DeliveryState = DeliveryState.None,
            CreatedAt = now,
        };
        db.Messages.Add(message);

        // D-15: a late requester reply reopens the same ticket (team and assignee are retained).
        var previous = ticket.Status;
        var reopened = ticket.ApplyStatus(TicketStatus.Open, now);
        ticket.RecordPublicActivity(now);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (PostgresErrors.IsUniqueViolation(ex, out var constraint) && constraint == "ux_messages_source_ref")
        {
            db.ChangeTracker.Clear();
            await tx.RollbackAsync(cancellationToken);
            var winner = await FindExistingAsync(request.DepartmentId, request.SourceRef, cancellationToken);
            if (winner is null)
            {
                throw;
            }

            return winner;
        }

        // Someone other than the requester answering is a participant of the conversation from now on.
        if (author.Id != ticket.RequesterId)
        {
            await PeopleUpsert.ParticipantAsync(db, request.DepartmentId, ticket.Id, request.From, now, cancellationToken);
        }

        foreach (var person in request.Cc)
        {
            await PeopleUpsert.ParticipantAsync(db, request.DepartmentId, ticket.Id, person, now, cancellationToken);
        }

        await audit.WriteSystemAsync(
            TicketAuditActions.MessageAdded, TicketingAudit.TicketObject, ticket.Id, request.DepartmentId, null,
            new { messageId = message.Id, kind = message.Kind.ToString(), authorType = message.AuthorType.ToString(), receivedViaMergedTicketId = targetId == request.TicketId ? (Guid?)null : request.TicketId },
            cancellationToken);
        if (reopened)
        {
            await audit.WriteSystemAsync(
                TicketAuditActions.StatusChanged, TicketingAudit.TicketObject, ticket.Id, request.DepartmentId, new { status = previous.ToString() },
                new { status = ticket.Status.ToString(), reason = TicketAuditActions.ReasonRequesterReply }, cancellationToken);
        }

        var changes = new List<TicketChange> { new(ChangeKind.RequesterMessage, MessageId: message.Id) };
        if (reopened)
        {
            changes.Add(new TicketChange(ChangeKind.Status, previous.ToString(), ticket.Status.ToString()));
        }

        await lifecycle.BeforeCommitAsync(ticket, "", changes, cancellationToken);
        await tx.CommitAsync(cancellationToken);
        await events.PublishAsync(request.DepartmentId, ticket.Id, ticket.Reference, TicketEventKinds.Message, null, cancellationToken);
        if (reopened)
        {
            await events.PublishAsync(request.DepartmentId, ticket.Id, ticket.Reference, TicketEventKinds.Status, null, cancellationToken);
        }

        await lifecycle.AfterCommitAsync(ticket, changes, Provenances.Email, cancellationToken);

        return new InboundTicketResult(ticket.Id, ticket.Reference, message.Id, Created: false, Reopened: reopened);
    }

    public async Task<TicketLookup?> FindByReferenceAsync(Guid departmentId, string reference, CancellationToken cancellationToken = default)
    {
        var normalised = reference.Trim().ToUpperInvariant();
        return await db.Tickets.AsNoTracking().IgnoreDepartmentScope()
            .Where(t => t.DepartmentId == departmentId && t.Reference == normalised)
            .Select(t => new TicketLookup(t.Id, t.Reference, t.Status.ToString()))
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task NotifyTicketChangedAsync(Guid departmentId, Guid ticketId, string kind, CancellationToken cancellationToken = default)
    {
        var reference = await db.Tickets.AsNoTracking().IgnoreDepartmentScope().Where(t => t.Id == ticketId && t.DepartmentId == departmentId).Select(t => t.Reference).SingleOrDefaultAsync(cancellationToken);
        if (reference is not null)
        {
            await events.PublishAsync(departmentId, ticketId, reference, kind == "attachment" ? TicketEventKinds.Attachment : TicketEventKinds.Updated, null, cancellationToken);
        }
    }

    private async Task<InboundTicketResult?> FindExistingAsync(Guid departmentId, string sourceRef, CancellationToken ct)
    {
        var existing = await db.Messages.AsNoTracking().IgnoreDepartmentScope()
            .Where(m => m.DepartmentId == departmentId && m.Source == MessageSources.Email && m.SourceRef == sourceRef)
            .Select(m => new { m.Id, m.TicketId }).SingleOrDefaultAsync(ct);
        if (existing is null)
        {
            return null;
        }

        var ticket = await db.Tickets.AsNoTracking().IgnoreDepartmentScope().Where(t => t.Id == existing.TicketId && t.DepartmentId == departmentId)
            .Select(t => new { t.Id, t.Reference, t.Number }).SingleAsync(ct);

        // Created is true only when this message is the ticket's very first one (the creating call), so an idempotent repeat of a
        // creation still reports it and a repeat of an attach does not.
        var firstMessageId = await db.Messages.AsNoTracking().IgnoreDepartmentScope().Where(m => m.TicketId == ticket.Id)
            .OrderBy(m => m.CreatedAt).ThenBy(m => m.Id).Select(m => m.Id).FirstAsync(ct);
        return new InboundTicketResult(ticket.Id, ticket.Reference, existing.Id, Created: firstMessageId == existing.Id, Reopened: false);
    }
}

/// <summary><see cref="IDeliveryStateSink"/>: Mail reports how an agent's public reply ended. Only the delivery columns of that message change.</summary>
internal sealed class DeliveryStateSink(TicketingDbContext db, TicketingAudit audit, ITicketEvents events) : IDeliveryStateSink
{
    public async Task SetAsync(Guid departmentId, Guid ticketMessageId, string state, string? failureCode, CancellationToken cancellationToken = default)
    {
        var target = state switch
        {
            MailDeliveryStates.Pending => DeliveryState.Pending,
            MailDeliveryStates.Sent => DeliveryState.Sent,
            MailDeliveryStates.Failed => DeliveryState.Failed,
            _ => throw new ArgumentOutOfRangeException(nameof(state)),
        };

        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        var message = await db.Messages.IgnoreDepartmentScope().SingleOrDefaultAsync(
            m => m.Id == ticketMessageId && m.DepartmentId == departmentId && m.Kind == MessageKind.Public && m.AuthorType == AuthorType.Agent, cancellationToken);
        if (message is null || message.DeliveryState == target)
        {
            await tx.RollbackAsync(cancellationToken);
            return;
        }

        var previous = message.DeliveryState;
        var error = target == DeliveryState.Failed ? (failureCode is { Length: > 200 } ? failureCode[..200] : failureCode) : null;
        await db.Messages.IgnoreDepartmentScope().Where(m => m.Id == ticketMessageId && m.DepartmentId == departmentId)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.DeliveryState, target).SetProperty(m => m.DeliveryError, error), cancellationToken);
        await audit.WriteSystemAsync(
            TicketAuditActions.MessageDeliveryChanged, TicketingAudit.TicketObject, message.TicketId, departmentId,
            new { messageId = ticketMessageId, delivery = previous.ToString() }, new { messageId = ticketMessageId, delivery = target.ToString(), error }, cancellationToken);
        await tx.CommitAsync(cancellationToken);
        var reference = await db.Tickets.AsNoTracking().IgnoreDepartmentScope().Where(t => t.Id == message.TicketId).Select(t => t.Reference).SingleOrDefaultAsync(cancellationToken);
        await events.PublishAsync(departmentId, message.TicketId, reference ?? "", TicketEventKinds.Delivery, null, cancellationToken);
    }
}
