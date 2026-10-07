using System.Globalization;
using Helpdesk.Modules.Ticketing.Fields;
using System.Text.Json;
using System.Text.Json.Nodes;
using Helpdesk.Modules.Attachments.Contracts;
using Helpdesk.Modules.Audit.Contracts;
using Helpdesk.Modules.Identity.Contracts;
using Helpdesk.Modules.Mail.Contracts;
using Helpdesk.Modules.Organisation.Contracts;
using Helpdesk.Modules.Ticketing.Automation;
using Helpdesk.Modules.Ticketing.Domain;
using Helpdesk.Modules.Ticketing.Persistence;
using Helpdesk.Modules.Ticketing.Sla;
using Helpdesk.SharedKernel.Authorization;
using Helpdesk.SharedKernel.Database;
using Helpdesk.SharedKernel.Errors;
using Helpdesk.SharedKernel.Paging;
using Helpdesk.SharedKernel.Security;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Helpdesk.Modules.Ticketing.Services;

/// <summary>
/// Ticket commands and single-ticket reads. Authorization model (PHASE2_API.md): the endpoint filter resolves the ticket through
/// the department-scoped set (404) and checks the baseline permission against its persisted department (403); this service then
/// applies the finer, payload-dependent rules. Every mutation and its audit events commit in one transaction; message bodies are
/// never audited or logged.
/// </summary>
internal sealed class TicketService(
    TicketingDbContext db,
    IDepartmentScope scope,
    TicketingAudit audit,
    TicketProjection projection,
    TicketResolver resolver,
    IAuthorizer authorizer,
    ICurrentUser current,
    IOrganisationDirectory organisation,
    IUserDirectory users,
    IAuditReader auditReader,
    IMailboxDirectory mailboxes,
    IMailOutbox outbox,
    IAttachmentStore attachments,
    ITicketEvents events,
    TicketLifecycle lifecycle,
    TicketTypesService types,
    FieldCatalog catalog,
    TimeProvider clock)
{
    public const int DefaultMessageLimit = 100;
    public const int DefaultActivityLimit = 50;
    public const int MaxLimit = 200;

    // ----- create -----

    public async Task<TicketDetailDto> CreateAsync(Guid departmentId, CreateTicketRequest request, CancellationToken ct)
    {
        await scope.EnsureLoadedAsync(ct);
        await RequireAssignmentRightsAsync(departmentId, request.TeamId, request.AssigneeUserId, ticket: null, ct);

        var errors = new Dictionary<string, string[]>();
        TicketValidation.Subject(errors, "subject", request.Subject);
        TicketValidation.Body(errors, "description", request.Description);
        if ((request.Requester is null) == (request.RequesterId is null))
        {
            errors["requester"] = ["Provide either requester or requesterId."];
        }
        else if (request.Requester is { } input)
        {
            TicketValidation.Email(errors, "requester.email", input.Email);
            TicketValidation.DisplayName(errors, "requester.displayName", input.DisplayName);
        }

        var priority = TicketValidation.ParseEnum<TicketPriority>(errors, "priority", request.Priority) ?? TicketPriority.Normal;
        TicketValidation.ThrowIfAny(errors);

        var department = await organisation.FindDepartmentAsync(departmentId, ct) ?? throw new NotFoundException();
        if (!department.IsActive)
        {
            throw new ConflictException("The department is inactive.", ProblemCodes.Inactive);
        }

        await ValidateTeamAsync(departmentId, request.TeamId, ct);
        await ValidateAssigneeAsync(departmentId, request.AssigneeUserId, ct);

        // Explicit type must be an active type of this department; otherwise the department default (if any). Fields are collected and validated (required enforced)
        // only when the caller submits them; a create that submits none leaves details to be completed on the ticket.
        var typeId = await types.ResolveForCreateAsync(departmentId, request.TicketTypeId, null, ct);
        ValidationResult? fieldValues = null;
        if (request.Fields is not null)
        {
            var fieldErrors = new Dictionary<string, string[]>();
            var allowed = (await catalog.ForTypeAsync(departmentId, typeId, ct)).Where(b => b.Field.AgentVisible).ToList();
            fieldValues = FieldValueValidator.Validate(allowed, request.Fields, ValidationMode.Create, fieldErrors);
            TicketValidation.ThrowIfAny(fieldErrors);
        }

        var now = clock.GetUtcNowMicroseconds();
        Ticket ticket;
        Requester requester;
        TicketChange[] createdChange = [new TicketChange(ChangeKind.Created)];
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            var requesterCreated = false;
            if (request.RequesterId is { } requesterId)
            {
                requester = await db.Requesters.AsNoTracking().SingleOrDefaultAsync(r => r.Id == requesterId && r.DepartmentId == departmentId, ct)
                    ?? throw new RequestValidationException("requesterId", "The requester does not exist in this department.");
            }
            else
            {
                (requester, requesterCreated) = await UpsertRequesterAsync(departmentId, request.Requester!, now, ct);
            }

            if (requesterCreated)
            {
                await audit.WriteAsync(TicketAuditActions.RequesterCreated, TicketingAudit.RequesterObject, requester.Id, departmentId,
                    null, new { departmentId }, ct);
            }

            var number = await TicketNumbering.AllocateAsync(db, departmentId, ct);
            ticket = new Ticket
            {
                Id = Guid.CreateVersion7(),
                DepartmentId = departmentId,
                Number = number,
                Reference = string.Create(CultureInfo.InvariantCulture, $"{department.Key}-{number}"),
                RequesterId = requester.Id,
                Subject = request.Subject!.Trim(),
                Status = TicketStatus.Open,
                Priority = priority,
                TeamId = request.TeamId,
                AssigneeUserId = request.AssigneeUserId,
                CreatedByUserId = current.UserId,
                CreatedAt = now,
                UpdatedAt = now,
                LastPublicActivityAt = now,
                Source = TicketSources.Agent,
                TicketTypeId = typeId,
            };
            var first = new Message
            {
                Id = Guid.CreateVersion7(),
                TicketId = ticket.Id,
                DepartmentId = departmentId,
                Kind = MessageKind.Public,
                AuthorType = AuthorType.Requester,
                AuthorRequesterId = requester.Id,
                Body = request.Description!,
                Source = MessageSources.Manual,
                DeliveryState = DeliveryState.None,
                CreatedAt = now,
            };
            db.Tickets.Add(ticket);
            db.Messages.Add(first);
            if (fieldValues is not null)
            {
                await FieldValueStore.ApplyAsync(db, ticket, fieldValues, null, now, ct);
            }

            await SaveAsync(ct);

            await audit.WriteAsync(TicketAuditActions.Created, TicketingAudit.TicketObject, ticket.Id, departmentId, null,
                new
                {
                    reference = ticket.Reference,
                    status = ticket.Status.ToString(),
                    priority = ticket.Priority.ToString(),
                    teamId = ticket.TeamId,
                    assigneeUserId = ticket.AssigneeUserId,
                    ticketTypeId = ticket.TicketTypeId,
                }, ct);
            await audit.WriteAsync(TicketAuditActions.MessageAdded, TicketingAudit.TicketObject, ticket.Id, departmentId, null,
                new { messageId = first.Id, kind = first.Kind.ToString(), authorType = first.AuthorType.ToString() }, ct);
            await lifecycle.BeforeCommitAsync(ticket, requester.EmailNormalised, createdChange, ct);
            await tx.CommitAsync(ct);
        }

        await events.PublishAsync(departmentId, ticket.Id, ticket.Reference, TicketEventKinds.Created, current.UserId, ct);
        await lifecycle.AfterCommitAsync(ticket, createdChange, Provenances.User, ct);
        return await projection.DetailAsync(ticket, requester, ct);
    }

    private async Task<(Requester Requester, bool Created)> UpsertRequesterAsync(Guid departmentId, RequesterInput input, DateTimeOffset now, CancellationToken ct)
    {
        var email = input.Email!.Trim();
        var normalised = Requester.Normalise(email);
        var displayName = string.IsNullOrWhiteSpace(input.DisplayName) ? email : input.DisplayName.Trim();

        // ON CONFLICT DO NOTHING keeps a concurrent insert of the same address from aborting the transaction.
        var inserted = await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO ticketing.requesters (id, department_id, email, email_normalised, display_name, created_at)
            VALUES ({Guid.CreateVersion7()}, {departmentId}, {email}, {normalised}, {displayName}, {now})
            ON CONFLICT (department_id, email_normalised) DO NOTHING
            """,
            ct);
        var requester = await db.Requesters.AsNoTracking().SingleAsync(r => r.DepartmentId == departmentId && r.EmailNormalised == normalised, ct);
        return (requester, inserted == 1);
    }

    // ----- read -----

    public async Task<TicketDetailDto> GetAsync(string route, CancellationToken ct)
    {
        var key = await resolver.FindAsync(route, ct) ?? throw new NotFoundException();
        var ticket = await db.Tickets.AsNoTracking().Include(t => t.Requester).SingleOrDefaultAsync(t => t.Id == key.Id, ct) ?? throw new NotFoundException();
        return await projection.DetailAsync(ticket, ticket.Requester, ct);
    }

    // ----- update -----

    public async Task<TicketDetailDto> UpdateAsync(string route, UpdateTicketRequest request, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        var version = TicketValidation.Version(errors, request.Version);
        if (request.Subject is not null)
        {
            TicketValidation.Subject(errors, "subject", request.Subject);
        }

        var status = TicketValidation.ParseEnum<TicketStatus>(errors, "status", request.Status);
        var priority = TicketValidation.ParseEnum<TicketPriority>(errors, "priority", request.Priority);
        if (request.Subject is null && request.Status is null && request.Priority is null)
        {
            errors["body"] = ["Provide at least one of subject, status, priority."];
        }

        TicketValidation.ThrowIfAny(errors);

        var key = await resolver.FindAsync(route, ct) ?? throw new NotFoundException();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var ticket = await LockAndLoadAsync(key.Id, version, ct);

        var changes = new List<(string Action, object Previous, object Next)>();
        var lc = new List<TicketChange>();
        var now = clock.GetUtcNowMicroseconds();
        var newSubject = request.Subject?.Trim();
        if (newSubject is not null && newSubject != ticket.Subject)
        {
            changes.Add((TicketAuditActions.SubjectChanged, new { subject = ticket.Subject }, new { subject = newSubject }));
            ticket.Subject = newSubject;
        }

        if (priority is { } newPriority && newPriority != ticket.Priority)
        {
            changes.Add((TicketAuditActions.PriorityChanged, new { priority = ticket.Priority.ToString() }, new { priority = newPriority.ToString() }));
            lc.Add(new TicketChange(ChangeKind.Priority, ticket.Priority.ToString(), newPriority.ToString()));
            ticket.Priority = newPriority;
        }

        if (status is { } newStatus && newStatus != ticket.Status)
        {
            var previous = ticket.Status;
            ticket.ApplyStatus(newStatus, now);
            changes.Add((TicketAuditActions.StatusChanged, new { status = previous.ToString() }, new { status = newStatus.ToString() }));
            lc.Add(new TicketChange(ChangeKind.Status, previous.ToString(), newStatus.ToString()));
        }

        if (changes.Count > 0)
        {
            ticket.UpdatedAt = now;
            await SaveAsync(ct);
            foreach (var (action, previous, next) in changes)
            {
                await audit.WriteAsync(action, TicketingAudit.TicketObject, ticket.Id, ticket.DepartmentId, previous, next, ct);
            }
        }

        if (lc.Count > 0)
        {
            await lifecycle.BeforeCommitAsync(ticket, ticket.Requester.EmailNormalised, lc, ct);
        }

        await tx.CommitAsync(ct);
        foreach (var kind in changes.Select(c => c.Action switch
        {
            TicketAuditActions.StatusChanged => TicketEventKinds.Status,
            TicketAuditActions.PriorityChanged => TicketEventKinds.Priority,
            _ => TicketEventKinds.Updated,
        }).Distinct())
        {
            await events.PublishAsync(ticket.DepartmentId, ticket.Id, ticket.Reference, kind, current.UserId, ct);
        }

        await lifecycle.AfterCommitAsync(ticket, lc, Provenances.User, ct);
        return await projection.DetailAsync(ticket, ticket.Requester, ct);
    }

    // ----- assignment -----

    public async Task<TicketDetailDto> AssignAsync(string route, AssignTicketRequest request, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        var version = TicketValidation.Version(errors, request.Version);
        TicketValidation.ThrowIfAny(errors);

        var key = await resolver.FindAsync(route, ct) ?? throw new NotFoundException();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var ticket = await LockAndLoadAsync(key.Id, version, ct);

        await RequireAssignmentRightsAsync(ticket.DepartmentId, request.TeamId, request.AssigneeUserId, ticket, ct);

        // Validate only what actually changes, so keeping an existing (since deactivated) team or assignee stays possible.
        var changes = new List<(string Action, object Previous, object Next)>();
        var lc = new List<TicketChange>();
        if (request.TeamId != ticket.TeamId)
        {
            await ValidateTeamAsync(ticket.DepartmentId, request.TeamId, ct);
            changes.Add((TicketAuditActions.TeamChanged, new { teamId = ticket.TeamId }, new { teamId = request.TeamId }));
            lc.Add(new TicketChange(ChangeKind.Team, ticket.TeamId?.ToString(), request.TeamId?.ToString()));
        }

        if (request.AssigneeUserId != ticket.AssigneeUserId)
        {
            await ValidateAssigneeAsync(ticket.DepartmentId, request.AssigneeUserId, ct);
            changes.Add((TicketAuditActions.AssigneeChanged, new { assigneeUserId = ticket.AssigneeUserId }, new { assigneeUserId = request.AssigneeUserId }));
            lc.Add(new TicketChange(ChangeKind.Assignee, ticket.AssigneeUserId?.ToString(), request.AssigneeUserId?.ToString()));
        }

        if (changes.Count > 0)
        {
            ticket.TeamId = request.TeamId;
            ticket.AssigneeUserId = request.AssigneeUserId;
            ticket.UpdatedAt = clock.GetUtcNowMicroseconds();
            await SaveAsync(ct);
            foreach (var (action, previous, next) in changes)
            {
                await audit.WriteAsync(action, TicketingAudit.TicketObject, ticket.Id, ticket.DepartmentId, previous, next, ct);
            }
        }

        await tx.CommitAsync(ct);
        if (changes.Count > 0)
        {
            await events.PublishAsync(ticket.DepartmentId, ticket.Id, ticket.Reference, TicketEventKinds.Assignment, current.UserId, ct);
            await lifecycle.AfterCommitAsync(ticket, lc, Provenances.User, ct);
        }

        return await projection.DetailAsync(ticket, ticket.Requester, ct);
    }

    // ----- messages -----

    public async Task<PostedMessageDto> PostMessageAsync(string route, PostMessageRequest request, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        if (request.Kind is null)
        {
            errors["kind"] = ["kind is required."];
        }

        var kind = TicketValidation.ParseEnum<MessageKind>(errors, "kind", request.Kind);
        TicketValidation.Body(errors, "body", request.Body);
        var status = TicketValidation.ParseEnum<TicketStatus>(errors, "status", request.Status);
        TicketValidation.ThrowIfAny(errors);

        var key = await resolver.FindAsync(route, ct) ?? throw new NotFoundException();
        await authorizer.RequireAsync(kind == MessageKind.Public ? PermissionCodes.TicketsReply : PermissionCodes.TicketsNote, key.DepartmentId, ct);
        if (status is not null)
        {
            await authorizer.RequireAsync(PermissionCodes.TicketsUpdate, key.DepartmentId, ct);
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var ticket = await LockAndLoadAsync(key.Id, expectedVersion: null, ct);
        if (kind == MessageKind.Public && ticket.Status == TicketStatus.Closed)
        {
            throw new ConflictException("The ticket is closed. Reopen it before replying to the requester.", ProblemCodes.InvalidState);
        }

        // A public reply leaves from the ticket's mailbox (or the department default); with no active mailbox it is only recorded.
        var replyMailbox = kind == MessageKind.Public ? await mailboxes.FindReplyMailboxAsync(ticket.DepartmentId, ticket.MailboxId, ct) : null;
        var now = clock.GetUtcNowMicroseconds();
        var message = new Message
        {
            Id = Guid.CreateVersion7(),
            TicketId = ticket.Id,
            DepartmentId = ticket.DepartmentId,
            Kind = kind!.Value,
            AuthorType = AuthorType.Agent,
            AuthorUserId = current.UserId,
            Body = request.Body!,
            Source = MessageSources.Web,

            // Truthful states only: Pending once queued in the outbox; NotDelivered when no mailbox exists to send it from.
            DeliveryState = kind != MessageKind.Public ? DeliveryState.None : replyMailbox is null ? DeliveryState.NotDelivered : DeliveryState.Pending,
            CreatedAt = now,
        };
        db.Messages.Add(message);

        var previousStatus = ticket.Status;
        var statusChanged = status is { } requested && ticket.ApplyStatus(requested, now);

        // UpdatedAt means visible activity (public messages, state changes). An internal note alone must not move it: the ticket row is
        // not even written, so UpdatedAt, the row version and queue order give callers without tickets.notes.read nothing to infer from.
        if (kind == MessageKind.Public)
        {
            ticket.RecordPublicActivity(now);
        }
        else if (statusChanged)
        {
            ticket.UpdatedAt = now;
        }

        await SaveAsync(ct);

        await audit.WriteAsync(TicketAuditActions.MessageAdded, TicketingAudit.TicketObject, ticket.Id, ticket.DepartmentId, null,
            new { messageId = message.Id, kind = message.Kind.ToString(), authorType = message.AuthorType.ToString() }, ct);
        if (statusChanged)
        {
            await audit.WriteAsync(TicketAuditActions.StatusChanged, TicketingAudit.TicketObject, ticket.Id, ticket.DepartmentId,
                new { status = previousStatus.ToString() }, new { status = ticket.Status.ToString() }, ct);
        }

        Guid? outboundId = null;
        if (replyMailbox is not null)
        {
            var cc = await db.Participants.AsNoTracking().Where(p => p.TicketId == ticket.Id)
                .Select(p => new MailParticipant(p.Email, p.DisplayName)).ToListAsync(ct);
            outboundId = await outbox.QueueReplyAsync(
                new OutboundReplyRequest(
                    ticket.DepartmentId, replyMailbox.MailboxId, ticket.Id, message.Id, ticket.Reference, ticket.Subject,
                    new MailParticipant(ticket.Requester.Email, ticket.Requester.DisplayName), cc, message.Body, current.UserId), db, ct);
        }

        var messageChanges = new List<TicketChange>();
        if (kind == MessageKind.Public)
        {
            messageChanges.Add(new TicketChange(ChangeKind.AgentPublicReply, MessageId: message.Id));
        }

        if (statusChanged)
        {
            messageChanges.Add(new TicketChange(ChangeKind.Status, previousStatus.ToString(), ticket.Status.ToString()));
        }

        if (messageChanges.Count > 0)
        {
            await lifecycle.BeforeCommitAsync(ticket, ticket.Requester.EmailNormalised, messageChanges, ct);
        }

        await tx.CommitAsync(ct);
        if (outboundId is { } queued)
        {
            await outbox.NotifyQueuedAsync(queued, ct);
        }

        await events.PublishAsync(ticket.DepartmentId, ticket.Id, ticket.Reference, kind == MessageKind.Internal ? TicketEventKinds.Note : TicketEventKinds.Message, current.UserId, ct);
        if (statusChanged)
        {
            await events.PublishAsync(ticket.DepartmentId, ticket.Id, ticket.Reference, TicketEventKinds.Status, current.UserId, ct);
        }

        await lifecycle.AfterCommitAsync(ticket, messageChanges, Provenances.User, ct);
        var dto = new MessageDto(message.Id, message.TicketId, message.Kind.ToString(), message.AuthorType.ToString(),
            new MessageAuthorDto(current.UserId, current.DisplayName), message.Body, null, message.Source, message.DeliveryState.ToString(), message.DeliveryError, [], message.CreatedAt);
        return new PostedMessageDto(dto, await projection.DetailAsync(ticket, ticket.Requester, ct));
    }

    /// <summary>Puts a failed public reply back in the mail outbox. Baseline permission tickets.read (endpoint), tickets.reply here.</summary>
    public async Task<MessageDto> RetryDeliveryAsync(string route, Guid messageId, CancellationToken ct)
    {
        var key = await resolver.FindAsync(route, ct) ?? throw new NotFoundException();
        await authorizer.RequireAsync(PermissionCodes.TicketsReply, key.DepartmentId, ct);

        if (await db.Tickets.AsNoTracking().AnyAsync(t => t.Id == key.Id && t.MergedIntoTicketId != null, ct))
        {
            throw MergedConflict();
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var message = await db.Messages.SingleOrDefaultAsync(
            m => m.Id == messageId && m.TicketId == key.Id && m.DepartmentId == key.DepartmentId && m.Kind == MessageKind.Public && m.AuthorType == AuthorType.Agent, ct)
            ?? throw new NotFoundException();
        if (message.DeliveryState != DeliveryState.Failed)
        {
            throw new ConflictException("Only a failed delivery can be retried.", ProblemCodes.InvalidState);
        }

        var outboundId = await outbox.RetryAsync(key.DepartmentId, messageId, db, ct)
            ?? throw new ConflictException("There is no failed email to retry for this message.", ProblemCodes.InvalidState);
        await db.Messages.Where(m => m.Id == messageId).ExecuteUpdateAsync(
            s => s.SetProperty(m => m.DeliveryState, DeliveryState.Pending).SetProperty(m => m.DeliveryError, (string?)null), ct);
        await audit.WriteAsync(
            TicketAuditActions.MessageDeliveryChanged, TicketingAudit.TicketObject, key.Id, key.DepartmentId,
            new { messageId, delivery = nameof(DeliveryState.Failed) }, new { messageId, delivery = nameof(DeliveryState.Pending), reason = "retry" }, ct);
        await tx.CommitAsync(ct);
        await outbox.NotifyQueuedAsync(outboundId, ct);
        await events.PublishAsync(key.DepartmentId, key.Id, route, TicketEventKinds.Delivery, current.UserId, ct);

        message.DeliveryState = DeliveryState.Pending;
        message.DeliveryError = null;
        var files = await attachments.ListForMessagesAsync(key.DepartmentId, [message.Id], ct);
        return new MessageDto(
            message.Id, message.TicketId, message.Kind.ToString(), message.AuthorType.ToString(), new MessageAuthorDto(message.AuthorUserId, current.DisplayName),
            message.Body, message.BodyHtml, message.Source, message.DeliveryState.ToString(), message.DeliveryError, files.TryGetValue(message.Id, out var list) ? list : [], message.CreatedAt);
    }

    /// <summary>Opens an attachment of this ticket for download (department checked twice: ticket scope, then the attachment's own department filter).</summary>
    public async Task<AttachmentOpenResult> OpenAttachmentAsync(string route, Guid attachmentId, CancellationToken ct)
    {
        var key = await resolver.FindAsync(route, ct) ?? throw new NotFoundException();
        // Attachments of merged tickets stay on their original ticket (no copies): try this ticket, then the tickets merged into it.
        var owners = await ConversationTicketsAsync(key, ct);
        foreach (var owner in owners.Keys)
        {
            var result = await attachments.OpenAsync(key.DepartmentId, owner, attachmentId, ct);
            if (!result.NotFound)
            {
                return result;
            }
        }

        throw new NotFoundException();
    }

    /// <summary>The ticket itself first, then the tickets merged into it (id to reference). Same department by construction (composite foreign key).</summary>
    private async Task<Dictionary<Guid, string>> ConversationTicketsAsync(TicketKey key, CancellationToken ct)
    {
        var map = new Dictionary<Guid, string> { [key.Id] = "" };
        var merged = await db.Tickets.AsNoTracking().Where(t => t.MergedIntoTicketId == key.Id && t.DepartmentId == key.DepartmentId)
            .Select(t => new { t.Id, t.Reference }).ToListAsync(ct);
        foreach (var m in merged)
        {
            map[m.Id] = m.Reference;
        }

        return map;
    }

    public async Task<PageDto<MessageDto>> ListMessagesAsync(string route, string? cursor, int? limit, CancellationToken ct)
    {
        var take = limit ?? DefaultMessageLimit;
        if (take is < 1 or > MaxLimit)
        {
            throw new RequestValidationException("limit", $"limit must be between 1 and {MaxLimit}.");
        }

        var key = await resolver.FindAsync(route, ct) ?? throw new NotFoundException();
        // The conversation of a canonical ticket also shows the messages of the tickets merged into it; they keep their original ticket and provenance.
        var conversation = await ConversationTicketsAsync(key, ct);
        var conversationIds = conversation.Keys.ToArray();
        var query = db.Messages.AsNoTracking().Where(m => conversationIds.Contains(m.TicketId));

        // Internal notes have their own gate, applied in the query itself.
        if (await authorizer.CheckAsync(PermissionCodes.TicketsNotesRead, key.DepartmentId, ct) != AccessOutcome.Allowed)
        {
            query = query.Where(m => m.Kind == MessageKind.Public);
        }

        if (cursor is not null)
        {
            if (!TryDecodeMessageCursor(cursor, out var at, out var id))
            {
                throw new RequestValidationException("cursor", "Invalid cursor.");
            }

            query = query.Where(m => m.CreatedAt < at || (m.CreatedAt == at && m.Id.CompareTo(id) < 0));
        }

        var rows = await query.OrderByDescending(m => m.CreatedAt).ThenByDescending(m => m.Id).Take(take + 1).ToListAsync(ct);
        var hasMore = rows.Count > take;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        var userIds = rows.Where(m => m.AuthorUserId is not null).Select(m => m.AuthorUserId!.Value).Distinct().ToArray();
        var requesterIds = rows.Where(m => m.AuthorRequesterId is not null).Select(m => m.AuthorRequesterId!.Value).Distinct().ToArray();
        var people = await users.GetManyAsync(userIds, ct);
        var requesters = requesterIds.Length == 0
            ? []
            : await db.Requesters.AsNoTracking().Where(r => requesterIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => r.DisplayName, ct);

        var files = await attachments.ListForMessagesAsync(key.DepartmentId, rows.Select(m => m.Id).ToArray(), ct);
        var items = rows.Select(m => new MessageDto(
            m.Id,
            m.TicketId,
            m.Kind.ToString(),
            m.AuthorType.ToString(),
            AuthorOf(m, people, requesters),
            m.Body,
            m.BodyHtml,
            m.Source,
            m.DeliveryState.ToString(),
            m.DeliveryError,
            files.TryGetValue(m.Id, out var list) ? list : [],
            m.CreatedAt,
            m.TicketId == key.Id ? null : conversation[m.TicketId])).ToList();
        var next = hasMore ? Cursor.Encode(string.Create(CultureInfo.InvariantCulture, $"{rows[^1].CreatedAt.UtcTicks}|{rows[^1].Id}")) : null;
        return new PageDto<MessageDto>(items, next);
    }

    private static MessageAuthorDto AuthorOf(Message m, IReadOnlyDictionary<Guid, UserSummary> people, Dictionary<Guid, string> requesters) => m.AuthorType switch
    {
        AuthorType.Agent => new MessageAuthorDto(m.AuthorUserId, m.AuthorUserId is { } u && people.TryGetValue(u, out var person) ? person.DisplayName : "Unknown user"),
        AuthorType.Requester => new MessageAuthorDto(m.AuthorRequesterId, m.AuthorRequesterId is { } r && requesters.TryGetValue(r, out var name) ? name : "Requester"),
        _ => new MessageAuthorDto(null, "System"),
    };

    private static bool TryDecodeMessageCursor(string cursor, out DateTimeOffset at, out Guid id)
    {
        at = default;
        id = default;
        if (!Cursor.TryDecode(cursor, out var payload))
        {
            return false;
        }

        var parts = payload.Split('|');
        if (parts.Length != 2
            || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
            || ticks < 0 || ticks > DateTime.MaxValue.Ticks
            || !Guid.TryParse(parts[1], out id))
        {
            return false;
        }

        at = new DateTimeOffset(ticks, TimeSpan.Zero);
        return true;
    }

    // ----- activity -----

    public async Task<PageDto<ActivityDto>> ActivityAsync(string route, string? cursor, int? limit, CancellationToken ct)
    {
        var take = limit ?? DefaultActivityLimit;
        if (take is < 1 or > MaxLimit)
        {
            throw new RequestValidationException("limit", $"limit must be between 1 and {MaxLimit}.");
        }

        var key = await resolver.FindAsync(route, ct) ?? throw new NotFoundException();
        var canReadNotes = await authorizer.CheckAsync(PermissionCodes.TicketsNotesRead, key.DepartmentId, ct) == AccessOutcome.Allowed;
        var items = new List<ActivityDto>();
        var next = cursor;
        string? nextCursor;
        do
        {
            var page = await auditReader.ReadObjectEventsAsync(AuditCategories.Ticketing, TicketingAudit.TicketObject, key.Id, key.DepartmentId, next, take - items.Count, ct);
            // Callers without tickets.notes.read must not learn that an internal note exists: its audit entry is skipped, and
            // further pages are read until the page is full so the page size cannot reveal it either.
            items.AddRange(page.Items.Where(r => canReadNotes || !IsInternalNoteEvent(r)).Select(ToActivity));
            nextCursor = page.NextCursor;
            next = nextCursor;
        }
        while (items.Count < take && nextCursor is not null);

        return new PageDto<ActivityDto>(items, nextCursor);
    }

    private static bool IsInternalNoteEvent(AuditRecord record) =>
        record.Action == TicketAuditActions.MessageAdded
        && record.Next is { ValueKind: JsonValueKind.Object } next
        && next.TryGetProperty("kind", out var kind)
        && kind.GetString() == nameof(MessageKind.Internal);

    private static ActivityDto ToActivity(AuditRecord record)
    {
        var previous = ToNode(record.Previous);
        var next = ToNode(record.Next);
        string? reason = null;
        if (next is JsonObject nextObject && nextObject.TryGetPropertyValue("reason", out var reasonNode))
        {
            // Surfaced as the top-level `reason`; `next` is returned exactly as stored.
            reason = reasonNode?.GetValue<string>();
        }

        return new ActivityDto(
            record.Id,
            record.OccurredAt,
            record.ActorUserId is { } actor ? new ActorDto(actor, record.ActorDisplayName) : null,
            record.Action,
            previous,
            next,
            reason);
    }

    private static JsonNode? ToNode(JsonElement? element) => element is { } e ? JsonNode.Parse(e.GetRawText()) : null;

    // ----- bulk -----

    public const int MaxBulk = 100;

    /// <summary>
    /// Applies the same change to many tickets. Every ticket is authorised and validated on its own, in its own transaction, against its
    /// own department: ids that are not visible are "not_found" (indistinguishable from missing), a lacking permission is "forbidden",
    /// a department-inconsistent team/assignee is "invalid". One ticket failing never rolls back or blocks the others; the result lists
    /// every outcome. Each applied change is audited like its single-ticket counterpart (with bulk = true).
    /// </summary>
    public async Task<BulkResultDto> BulkAsync(BulkRequest request, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        var ids = (request.TicketIds ?? []).Distinct().ToArray();
        if (ids.Length is 0 or > MaxBulk)
        {
            errors["ticketIds"] = [$"Provide between 1 and {MaxBulk} ticket ids."];
        }

        var status = TicketValidation.ParseEnum<TicketStatus>(errors, "status", request.Status);
        var priority = TicketValidation.ParseEnum<TicketPriority>(errors, "priority", request.Priority);
        if (status is null && priority is null && request.Team is null && request.Assignee is null)
        {
            errors["body"] = ["Provide at least one of status, priority, team, assignee."];
        }

        TicketValidation.ThrowIfAny(errors);
        await scope.EnsureLoadedAsync(ct);

        var results = new List<BulkItemDto>(ids.Length);
        foreach (var id in ids)
        {
            db.ChangeTracker.Clear();
            try
            {
                results.Add(await BulkOneAsync(id, status, priority, request.Team, request.Assignee, ct));
            }
            catch (NotFoundException)
            {
                results.Add(new BulkItemDto(id, null, "not_found", null));
            }
            catch (ForbiddenException)
            {
                results.Add(new BulkItemDto(id, null, "forbidden", null));
            }
            catch (ConflictException ex)
            {
                results.Add(new BulkItemDto(id, null, "invalid", ex.Code));
            }
        }

        db.ChangeTracker.Clear();
        return new BulkResultDto(
            results,
            results.Count(r => r.Outcome == "updated"),
            results.Count(r => r.Outcome == "unchanged"),
            results.Count(r => r.Outcome is "not_found" or "forbidden" or "invalid"));
    }

    private async Task<BulkItemDto> BulkOneAsync(Guid id, TicketStatus? status, TicketPriority? priority, IdChange? team, IdChange? assignee, CancellationToken ct)
    {
        // The scope filter makes an inaccessible ticket null here (same answer as a missing one).
        var key = await db.Tickets.AsNoTracking().Where(t => t.Id == id).Select(t => new { t.Id, t.DepartmentId }).SingleOrDefaultAsync(ct) ?? throw new NotFoundException();
        if (status is not null || priority is not null)
        {
            await authorizer.RequireAsync(PermissionCodes.TicketsUpdate, key.DepartmentId, ct);
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var ticket = await LockAndLoadAsync(key.Id, expectedVersion: null, ct);
        var now = clock.GetUtcNowMicroseconds();
        var changes = new List<(string Action, object Previous, object Next, string Kind)>();
        var (oldStatus, oldPriority, oldTeam, oldAssignee) = (ticket.Status, ticket.Priority, ticket.TeamId, ticket.AssigneeUserId);

        var wantedTeam = team is null ? ticket.TeamId : team.Id;
        var wantedAssignee = assignee is null ? ticket.AssigneeUserId : assignee.Id;
        if (wantedTeam != ticket.TeamId || wantedAssignee != ticket.AssigneeUserId)
        {
            await RequireAssignmentRightsAsync(ticket.DepartmentId, wantedTeam, wantedAssignee, ticket, ct);
            if (wantedTeam != ticket.TeamId)
            {
                await ValidateTeamAsync(ticket.DepartmentId, wantedTeam, ct);
                changes.Add((TicketAuditActions.TeamChanged, new { teamId = ticket.TeamId }, new { teamId = wantedTeam, bulk = true }, TicketEventKinds.Assignment));
                ticket.TeamId = wantedTeam;
            }

            if (wantedAssignee != ticket.AssigneeUserId)
            {
                await ValidateAssigneeAsync(ticket.DepartmentId, wantedAssignee, ct);
                changes.Add((TicketAuditActions.AssigneeChanged, new { assigneeUserId = ticket.AssigneeUserId }, new { assigneeUserId = wantedAssignee, bulk = true }, TicketEventKinds.Assignment));
                ticket.AssigneeUserId = wantedAssignee;
            }
        }

        if (priority is { } newPriority && newPriority != ticket.Priority)
        {
            changes.Add((TicketAuditActions.PriorityChanged, new { priority = ticket.Priority.ToString() }, new { priority = newPriority.ToString(), bulk = true }, TicketEventKinds.Priority));
            ticket.Priority = newPriority;
        }

        if (status is { } newStatus && newStatus != ticket.Status)
        {
            var previous = ticket.Status;
            ticket.ApplyStatus(newStatus, now);
            changes.Add((TicketAuditActions.StatusChanged, new { status = previous.ToString() }, new { status = newStatus.ToString(), bulk = true }, TicketEventKinds.Status));
        }

        if (changes.Count == 0)
        {
            await tx.RollbackAsync(ct);
            return new BulkItemDto(ticket.Id, ticket.Reference, "unchanged", null);
        }

        ticket.UpdatedAt = now;
        await SaveAsync(ct);
        foreach (var (action, previous, next, _) in changes)
        {
            await audit.WriteAsync(action, TicketingAudit.TicketObject, ticket.Id, ticket.DepartmentId, previous, next, ct);
        }

        var lc = new List<TicketChange>();
        if (ticket.Status != oldStatus)
        {
            lc.Add(new TicketChange(ChangeKind.Status, oldStatus.ToString(), ticket.Status.ToString()));
        }

        if (ticket.Priority != oldPriority)
        {
            lc.Add(new TicketChange(ChangeKind.Priority, oldPriority.ToString(), ticket.Priority.ToString()));
        }

        if (ticket.TeamId != oldTeam)
        {
            lc.Add(new TicketChange(ChangeKind.Team, oldTeam?.ToString(), ticket.TeamId?.ToString()));
        }

        if (ticket.AssigneeUserId != oldAssignee)
        {
            lc.Add(new TicketChange(ChangeKind.Assignee, oldAssignee?.ToString(), ticket.AssigneeUserId?.ToString()));
        }

        await lifecycle.BeforeCommitAsync(ticket, ticket.Requester.EmailNormalised, lc, ct);
        await tx.CommitAsync(ct);
        foreach (var kind in changes.Select(c => c.Kind).Distinct())
        {
            await events.PublishAsync(ticket.DepartmentId, ticket.Id, ticket.Reference, kind, current.UserId, ct);
        }

        await lifecycle.AfterCommitAsync(ticket, lc, Provenances.User, ct);
        return new BulkItemDto(ticket.Id, ticket.Reference, "updated", null);
    }

    // ----- shared helpers -----

    /// <summary>
    /// Serialises writers of one ticket (row lock for the rest of the transaction), then loads it tracked. With
    /// <paramref name="expectedVersion"/> a mismatch is a <c>version_conflict</c>; the lock makes the comparison race-free.
    /// </summary>
    internal async Task<Ticket> LockAndLoadAsync(Guid ticketId, uint? expectedVersion, CancellationToken ct)
    {
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM ticketing.tickets WHERE id = {ticketId} FOR UPDATE", ct);
        var ticket = await db.Tickets.Include(t => t.Requester).SingleOrDefaultAsync(t => t.Id == ticketId, ct) ?? throw new NotFoundException();
        if (ticket.IsMerged)
        {
            throw MergedConflict();
        }

        if (expectedVersion is { } version)
        {
            if (ticket.Version != version)
            {
                throw VersionConflict();
            }

            db.Entry(ticket).Property(t => t.Version).OriginalValue = version;
        }

        return ticket;
    }

    /// <summary>
    /// Team/assignee changes need <c>tickets.assign.any</c>. With only <c>tickets.assign.self</c> a caller may CLAIM a ticket that has
    /// no assignee (or keep one they already hold) or release a ticket they hold, leaving the team unchanged (on create: no team,
    /// assignee = themself). Taking a ticket that is assigned to another agent is a reassignment and needs <c>assign.any</c>.
    /// </summary>
    internal async Task RequireAssignmentRightsAsync(Guid departmentId, Guid? teamId, Guid? assigneeUserId, Ticket? ticket, CancellationToken ct)
    {
        if (ticket is null && teamId is null && assigneeUserId is null)
        {
            return;
        }

        if (await authorizer.CheckAsync(PermissionCodes.TicketsAssignAny, departmentId, ct) == AccessOutcome.Allowed)
        {
            return;
        }

        var self = await authorizer.CheckAsync(PermissionCodes.TicketsAssignSelf, departmentId, ct) == AccessOutcome.Allowed;
        var teamUnchanged = teamId == ticket?.TeamId;
        var currentAssignee = ticket?.AssigneeUserId;
        var selfAssignment = (assigneeUserId == current.UserId && (currentAssignee is null || currentAssignee == current.UserId))
            || (assigneeUserId is null && currentAssignee == current.UserId);
        if (!(self && teamUnchanged && selfAssignment))
        {
            throw new ForbiddenException();
        }
    }

    internal async Task ValidateTeamAsync(Guid departmentId, Guid? teamId, CancellationToken ct)
    {
        if (teamId is not { } id)
        {
            return;
        }

        var team = await organisation.FindTeamAsync(departmentId, id, ct);
        if (team is null)
        {
            throw new ConflictException("The team does not belong to this ticket's department.", ProblemCodes.InvalidState);
        }

        if (!team.IsActive)
        {
            throw new ConflictException("The team is inactive.", ProblemCodes.InvalidState);
        }
    }

    internal async Task ValidateAssigneeAsync(Guid departmentId, Guid? assigneeUserId, CancellationToken ct)
    {
        if (assigneeUserId is { } id && !await organisation.IsEligibleAssigneeAsync(departmentId, id, ct))
        {
            throw new ConflictException("The user cannot be assigned tickets in this department.", ProblemCodes.InvalidState);
        }
    }

    internal async Task SaveAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw VersionConflict();
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.ForeignKeyViolation })
        {
            // The team or assignee membership disappeared between validation and the write (composite foreign keys).
            throw new ConflictException("The team or assignee is no longer valid for this department.", ProblemCodes.InvalidState);
        }
    }

    internal static ConflictException MergedConflict() =>
        new("The ticket was merged into another ticket and is read-only.", TicketMergeCodes.Merged);

    internal static ConflictException VersionConflict() =>
        new("The ticket was changed by someone else. Reload and retry.", ProblemCodes.VersionConflict);
}
