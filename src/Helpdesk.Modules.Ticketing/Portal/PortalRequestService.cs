using System.Globalization;
using System.Text.Json;
using Helpdesk.Modules.Attachments.Contracts;
using Helpdesk.Modules.Organisation.Contracts;
using Helpdesk.Modules.Ticketing.Automation;
using Helpdesk.Modules.Ticketing.Contracts;
using Helpdesk.Modules.Ticketing.Domain;
using Helpdesk.Modules.Ticketing.Fields;
using Helpdesk.Modules.Ticketing.Persistence;
using Helpdesk.Modules.Ticketing.Services;
using Helpdesk.Modules.Ticketing.Sla;
using Helpdesk.SharedKernel.Errors;
using Helpdesk.SharedKernel.Paging;
using Helpdesk.SharedKernel.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Modules.Ticketing.Portal;

/// <summary>Requester-facing wording. The ticket status stays the single source of truth; this only chooses words a non-specialist understands.</summary>
internal static class PortalStatus
{
    public static string Code(TicketStatus s) => s switch
    {
        TicketStatus.Open => "InProgress",
        TicketStatus.Pending => "Waiting",
        TicketStatus.Resolved => "Resolved",
        _ => "Closed",
    };

    public static string Label(TicketStatus s) => s switch
    {
        TicketStatus.Open => "In progress",
        TicketStatus.Pending => "Waiting",
        TicketStatus.Resolved => "Resolved",
        _ => "Closed",
    };
}

// Purpose-built requester shapes: nothing here is derived from the agent DTOs, so a new agent-side field can never leak into the portal.
internal sealed record PortalRequestItemDto(string Reference, string Subject, string Status, string StatusLabel, string Department, DateTimeOffset CreatedAt, DateTimeOffset LastActivityAt);

internal sealed record PortalAttachmentDto(Guid Id, string FileName, long Size, bool Downloadable);

internal sealed record PortalMessageDto(Guid Id, string Author, bool FromYou, string Body, DateTimeOffset At, IReadOnlyList<PortalAttachmentDto> Attachments);

internal sealed record PortalFieldValueDto(string Label, string Value);

internal sealed record PortalEditableFieldDto(string Key, string Label, string? HelpText, string Type, bool Required, IReadOnlyList<PortalOptionDto> Options, JsonElement? Value);

internal sealed record PortalRequestDto(
    string Reference, string Subject, string Status, string StatusLabel, string Department, string? Form, DateTimeOffset CreatedAt, DateTimeOffset LastActivityAt,
    IReadOnlyList<PortalFieldValueDto> Fields, IReadOnlyList<PortalMessageDto> Messages, IReadOnlyList<PortalEditableFieldDto> EditableFields,
    string? MergedInto = null, bool CanReply = true);

/// <param name="AttachmentsNotSaved">How many of the attached files could not be stored. The reply itself IS saved; the requester is told to attach those again.</param>
internal sealed record PortalReplyResultDto(PortalMessageDto Message, string Status, string StatusLabel, int AttachmentsNotSaved = 0);

internal sealed record PortalCreateRequest(Guid? FormId, string? Subject, string? Description, Dictionary<string, JsonElement>? Fields, Guid? SubmissionId);

internal sealed record PortalCreatedDto(string Reference, string Status, string StatusLabel);

/// <summary>
/// Requester self-service over the same ticket model as agents and email. Every query is constrained to requesters linked to the signed-in user
/// BEFORE any row, snippet or count is produced; an unknown or foreign reference is a plain 404. The caller's agent permissions play no part.
/// </summary>
internal sealed class PortalRequestService(
    TicketingDbContext db,
    PortalFormAccess formAccess,
    RequesterIdentityService identity,
    IOrganisationDirectory organisation,
    IAttachmentStore attachments,
    ITicketIngress ingress,
    ITicketEvents events,
    TicketLifecycle lifecycle,
    TicketingAudit audit,
    ICurrentUser current,
    FieldCatalog catalog,
    TicketTypesService types,
    Helpdesk.Modules.Mail.Contracts.IMailboxDirectory mailboxes,
    Helpdesk.Modules.Mail.Contracts.IMailOutbox outbox,
    AutoReplyService autoReplies,
    TimeProvider clock)
{
    public const int DefaultLimit = 20;
    public const int MaxLimit = 50;
    public const int MaxFilesPerReply = 5;
    public const int MaxMessages = 200;

    // ----- list -----

    public async Task<PageDto<PortalRequestItemDto>> ListAsync(string? filter, string? cursor, int? limit, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        var take = limit ?? DefaultLimit;
        if (take is < 1 or > MaxLimit)
        {
            errors["limit"] = [$"limit must be between 1 and {MaxLimit}."];
        }

        var f = (filter ?? "active").Trim().ToLowerInvariant();
        if (f is not ("active" or "closed" or "all"))
        {
            errors["filter"] = ["filter must be active, closed or all."];
        }

        TicketValidation.ThrowIfAny(errors);
        DateTimeOffset? cursorAt = null;
        Guid cursorId = default;
        if (cursor is not null)
        {
            if (!Cursor.TryDecode(cursor, out var payload) || payload.Split('|') is not [var ticksText, var idText]
                || !long.TryParse(ticksText, NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) || ticks < 0 || ticks > DateTime.MaxValue.Ticks || !Guid.TryParse(idText, out cursorId))
            {
                throw new RequestValidationException("cursor", "Invalid cursor.");
            }

            cursorAt = new DateTimeOffset(ticks, TimeSpan.Zero);
        }

        var ids = await identity.RequesterIdsAsync(ct);
        if (ids.Count == 0)
        {
            return new PageDto<PortalRequestItemDto>([], null);
        }

        var tickets = ViewableTickets(ids).AsNoTracking().Where(t => t.MergedIntoTicketId == null);
        tickets = f switch
        {
            "active" => tickets.Where(t => t.Status == TicketStatus.Open || t.Status == TicketStatus.Pending),
            "closed" => tickets.Where(t => t.Status == TicketStatus.Resolved || t.Status == TicketStatus.Closed),
            _ => tickets,
        };
        if (cursorAt is { } at)
        {
            tickets = tickets.Where(t => t.LastPublicActivityAt < at || (t.LastPublicActivityAt == at && t.Id.CompareTo(cursorId) < 0));
        }

        var rows = await tickets.OrderByDescending(t => t.LastPublicActivityAt).ThenByDescending(t => t.Id).Take(take + 1)
            .Select(t => new { t.Id, t.Reference, t.Subject, t.Status, t.DepartmentId, t.CreatedAt, t.LastPublicActivityAt })
            .ToListAsync(ct);
        string? next = null;
        if (rows.Count > take)
        {
            rows.RemoveAt(rows.Count - 1);
            next = Cursor.Encode(string.Create(CultureInfo.InvariantCulture, $"{rows[^1].LastPublicActivityAt.UtcTicks}|{rows[^1].Id}"));
        }

        var departments = await organisation.GetDepartmentsAsync(rows.Select(r => r.DepartmentId).Distinct().ToArray(), ct);
        return new PageDto<PortalRequestItemDto>(
            [.. rows.Select(r => new PortalRequestItemDto(r.Reference, r.Subject, PortalStatus.Code(r.Status), PortalStatus.Label(r.Status),
                departments.TryGetValue(r.DepartmentId, out var d) ? d.Name : "", r.CreatedAt, r.LastPublicActivityAt))],
            next);
    }

    // ----- detail -----

    /// <summary>
    /// Tickets the signed-in user may SEE: their own requests, plus a request they were kept in by a merge (D9: the requester of a merged ticket is a protected participant of
    /// the canonical ticket). Ownership stays by requester link, never by address. Seeing is not owning: replying and editing fields stay with the ticket's own requester.
    /// </summary>
    private IQueryable<Ticket> ViewableTickets(IReadOnlyCollection<Guid> requesterIds) =>
        db.Tickets.IgnoreDepartmentScope().Where(t => requesterIds.Contains(t.RequesterId)
            || db.Participants.Any(p => p.TicketId == t.Id && p.IsProtected && p.RemovedAt == null && p.RequesterId != null && requesterIds.Contains(p.RequesterId.Value)));

    private async Task<Ticket> OwnTicketAsync(string reference, bool track, CancellationToken ct, bool allowMerged = false, bool includeProtected = false)
    {
        var ids = await identity.RequesterIdsAsync(ct);
        var normalised = reference.Trim().ToUpperInvariant();
        if (ids.Count == 0 || !TicketValidation.IsReference(normalised))
        {
            throw new NotFoundException();
        }

        var query = (includeProtected ? ViewableTickets(ids) : db.Tickets.IgnoreDepartmentScope().Where(t => ids.Contains(t.RequesterId))).Where(t => t.Reference == normalised);
        var ticket = await (track ? query : query.AsNoTracking()).SingleOrDefaultAsync(ct) ?? throw new NotFoundException();
        if (ticket.MergedIntoTicketId is { } canonicalId && !allowMerged)
        {
            // A merged request is read-only. The requester is pointed at the canonical ticket only when it is also THEIR ticket; otherwise it fails closed.
            _ = await CanonicalReferenceAsync(ticket, ids, ct) ?? throw new NotFoundException();
            throw new ConflictException("This request was merged into another request. Open that one instead.", "ticket_merged");
        }

        return ticket;
    }

    private async Task<string?> CanonicalReferenceAsync(Ticket merged, IReadOnlyCollection<Guid> requesterIds, CancellationToken ct) =>
        await ViewableTickets(requesterIds).AsNoTracking()
            .Where(t => t.Id == merged.MergedIntoTicketId && t.DepartmentId == merged.DepartmentId)
            .Select(t => t.Reference).SingleOrDefaultAsync(ct);

    public async Task<PortalRequestDto> GetAsync(string reference, CancellationToken ct)
    {
        var mine = await identity.RequesterIdsAsync(ct);
        var ticket = await OwnTicketAsync(reference, track: false, ct, allowMerged: true, includeProtected: true);
        if (ticket.IsMerged)
        {
            var canonical = await CanonicalReferenceAsync(ticket, mine, ct) ?? throw new NotFoundException();
            return new PortalRequestDto(ticket.Reference, ticket.Subject, "merged", "Merged", "", null, ticket.CreatedAt, ticket.LastPublicActivityAt, [], [], [], canonical);
        }

        var department = (await organisation.GetDepartmentsAsync([ticket.DepartmentId], ct)).GetValueOrDefault(ticket.DepartmentId);
        var formName = ticket.RequestFormId is { } formId
            ? await db.RequestForms.AsNoTracking().Where(f => f.Id == formId && f.DepartmentId == ticket.DepartmentId).Select(f => f.Name).SingleOrDefaultAsync(ct)
            : null;

        // A value is shown only while BOTH its snapshot and the field definition are requester visible, so hiding a field later hides its old values too.
        var held = await db.TicketFieldValues.AsNoTracking().IgnoreDepartmentScope()
            .Where(v => v.TicketId == ticket.Id && v.DepartmentId == ticket.DepartmentId && v.RequesterVisible)
            .OrderBy(v => v.SortOrder).ToListAsync(ct);
        var definitions = await db.TicketFields.AsNoTracking().Where(f => f.DepartmentId == ticket.DepartmentId && f.RequesterVisible).ToDictionaryAsync(f => f.Id, ct);
        held = [.. held.Where(v => v.FieldId is null || definitions.ContainsKey(v.FieldId.Value))];
        var fields = held.Select(v => new PortalFieldValueDto(v.Label, v.Display)).ToList();
        var isOwn = mine.Contains(ticket.RequesterId);
        var editable = (isOwn ? await EditableBindingsAsync(ticket, ct) : []).Select(b => new PortalEditableFieldDto(
            b.Field.Key, b.Field.Label, b.Field.HelpText, b.Field.FieldType, b.Required,
            [.. FieldValueValidator.Options(b.Field).Where(o => o.IsActive).Select(o => new PortalOptionDto(o.Value, o.Label))],
            held.SingleOrDefault(v => v.Key == b.Field.Key) is { } hv ? JsonDocument.Parse(hv.ValueJson).RootElement.Clone() : null)).ToList();

        // Public messages only. Internal notes are not filtered after the fact: they are never selected, so nothing about them (not even a count) exists here.
        var messages = await db.Messages.AsNoTracking().IgnoreDepartmentScope()
            .Where(m => m.TicketId == ticket.Id && m.DepartmentId == ticket.DepartmentId && m.Kind == MessageKind.Public)
            .OrderByDescending(m => m.CreatedAt).ThenByDescending(m => m.Id).Take(MaxMessages)
            .ToListAsync(ct);
        messages.Reverse();
        var files = await attachments.ListForMessagesAsync(ticket.DepartmentId, messages.Select(m => m.Id).ToArray(), ct);

        return new PortalRequestDto(
            ticket.Reference, ticket.Subject, PortalStatus.Code(ticket.Status), PortalStatus.Label(ticket.Status), department?.Name ?? "", formName,
            ticket.CreatedAt, ticket.LastPublicActivityAt, fields,
            [.. messages.Select(m => ToMessage(m, mine, files))], editable, CanReply: isOwn);
    }

    private static PortalMessageDto ToMessage(Message m, IReadOnlyCollection<Guid> mine, IReadOnlyDictionary<Guid, IReadOnlyList<AttachmentSummary>> files)
    {
        var fromRequester = m.AuthorType == AuthorType.Requester;
        var isMine = fromRequester && m.AuthorRequesterId is { } author && mine.Contains(author);
        return new PortalMessageDto(
            m.Id,
            !fromRequester ? "Support" : isMine ? "You" : "Another participant",
            isMine,
            m.Body,
            m.CreatedAt,
            files.TryGetValue(m.Id, out var list) ? [.. list.Where(a => !a.IsInline).Select(a => new PortalAttachmentDto(a.Id, a.FileName, a.Size, a.Downloadable))] : []);
    }

    // ----- reply -----

    public async Task<PortalReplyResultDto> ReplyAsync(string reference, string? body, IReadOnlyList<IFormFile> files, CancellationToken ct, string? clientMessageId = null)
    {
        var errors = new Dictionary<string, string[]>();
        TicketValidation.Body(errors, "body", body);
        if (files.Count > MaxFilesPerReply)
        {
            errors["files"] = [$"At most {MaxFilesPerReply} files can be attached to one reply."];
        }

        TicketValidation.ThrowIfAny(errors);
        var ticket = await OwnTicketAsync(reference, track: false, ct);

        // Same domain behaviour as an inbound requester email: public message, reopening (D-15), SLA, RequesterMessageAdded automation, activity timestamps.
        var result = await ingress.AddRequesterMessageAsync(ticket.DepartmentId, ticket.Id, ticket.RequesterId, body!, MessageSources.Portal, clientMessageId, ct);

        // A repeated post (the first response was lost) returns the saved reply: its files were stored by the first call and nothing is announced twice.
        var notSaved = 0;
        if (!result.Replayed)
        {
            // The reply is committed; each file then goes through the attachment pipeline on its own. A file that cannot be stored is counted and reported with the saved reply
            // (never an error for a reply that exists, which made the requester resend it and lose the files).
            for (var i = 0; i < files.Count; i++)
            {
                try
                {
                    var file = files[i];
                    var name = Path.GetFileName(file.FileName);
                    var content = file.Length <= 25 * 1024 * 1024 ? await ReadAsync(file, ct) : [];
                    await attachments.StoreAsync(new StoreAttachmentRequest(
                        ticket.DepartmentId, ticket.Id, result.MessageId, string.IsNullOrWhiteSpace(name) ? "attachment" : name, file.ContentType, content, false, null,
                        $"portal:{result.MessageId:N}:{i}", file.Length), ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    notSaved++;
                }
            }

            await events.PublishAsync(ticket.DepartmentId, ticket.Id, ticket.Reference, TicketEventKinds.Message, current.UserId, ct);
            if (result.Reopened)
            {
                await events.PublishAsync(ticket.DepartmentId, ticket.Id, ticket.Reference, TicketEventKinds.Status, current.UserId, ct);
            }

            if (files.Count > notSaved)
            {
                await events.PublishAsync(ticket.DepartmentId, ticket.Id, ticket.Reference, TicketEventKinds.Attachment, current.UserId, ct);
            }
        }

        var message = await db.Messages.AsNoTracking().IgnoreDepartmentScope().SingleAsync(m => m.Id == result.MessageId && m.DepartmentId == ticket.DepartmentId, ct);
        var stored = await attachments.ListForMessagesAsync(ticket.DepartmentId, [message.Id], ct);
        var status = await db.Tickets.AsNoTracking().IgnoreDepartmentScope().Where(t => t.Id == ticket.Id && t.DepartmentId == ticket.DepartmentId).Select(t => t.Status).SingleAsync(ct);
        return new PortalReplyResultDto(ToMessage(message, [ticket.RequesterId], stored), PortalStatus.Code(status), PortalStatus.Label(status), notSaved);
    }

    private static async Task<byte[]> ReadAsync(IFormFile file, CancellationToken ct)
    {
        await using var stream = file.OpenReadStream();
        using var buffer = new MemoryStream((int)Math.Min(file.Length, int.MaxValue));
        await stream.CopyToAsync(buffer, ct);
        return buffer.ToArray();
    }

    // ----- attachments -----

    public async Task<AttachmentOpenResult> OpenAttachmentAsync(string reference, Guid attachmentId, CancellationToken ct)
    {
        var ticket = await OwnTicketAsync(reference, track: false, ct, includeProtected: true);
        var publicMessageIds = await db.Messages.AsNoTracking().IgnoreDepartmentScope()
            .Where(m => m.TicketId == ticket.Id && m.DepartmentId == ticket.DepartmentId && m.Kind == MessageKind.Public).Select(m => m.Id).ToArrayAsync(ct);

        // Only attachments of public messages exist for a requester; anything else (including a guessed id) is "not found".
        var listed = await attachments.ListForMessagesAsync(ticket.DepartmentId, publicMessageIds, ct);
        if (!listed.Values.Any(l => l.Any(a => a.Id == attachmentId)))
        {
            throw new NotFoundException();
        }

        var result = await attachments.OpenAsync(ticket.DepartmentId, ticket.Id, attachmentId, ct);
        return result.NotFound ? throw new NotFoundException() : result;
    }

    // ----- create -----

    public async Task<PortalCreatedDto> CreateAsync(PortalCreateRequest request, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        if (request.FormId is null)
        {
            errors["formId"] = ["Choose a request form."];
        }

        TicketValidation.Subject(errors, "subject", request.Subject);
        TicketValidation.Body(errors, "description", request.Description);
        TicketValidation.ThrowIfAny(errors);

        // The form decides the department and the defaults. Hidden, inactive or out-of-audience forms do not exist for requesters; audience is re-checked on every submission.
        var formId = request.FormId!.Value;
        var form = await formAccess.Usable().AsNoTracking().SingleOrDefaultAsync(f => f.Id == formId, ct) ?? throw new NotFoundException();
        var department = await organisation.FindDepartmentAsync(form.DepartmentId, ct);
        if (department is null || !department.IsActive || (await formAccess.DepartmentsWithSenderAsync([form.DepartmentId], ct)).Count == 0)
        {
            throw new NotFoundException(); // D22-A: no usable default mailbox, so the form cannot be submitted
        }

        var asked = (await catalog.ForFormAsync(form.Id, form.DepartmentId, ct)).Where(b => b.Field.RequesterVisible).ToList();
        var values = FieldValueValidator.Validate(asked, request.Fields ?? [], ValidationMode.Create, errors);
        TicketValidation.ThrowIfAny(errors);
        var typeId = await types.ResolveForCreateAsync(form.DepartmentId, null, form.TicketTypeId, ct);

        var requesterId = await identity.EnsureRequesterAsync(form.DepartmentId, ct);
        var requester = await db.Requesters.AsNoTracking().IgnoreDepartmentScope().SingleAsync(r => r.Id == requesterId && r.DepartmentId == form.DepartmentId, ct);
        var submissionId = request.SubmissionId ?? Guid.CreateVersion7();
        var sourceRef = string.Create(CultureInfo.InvariantCulture, $"portal:{requesterId:N}:{submissionId:N}");

        var now = clock.GetUtcNowMicroseconds();
        Ticket ticket;
        Guid? acknowledgement = null;
        TicketChange[] createdChange = [new TicketChange(ChangeKind.Created)];
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            var number = await TicketNumbering.AllocateAsync(db, form.DepartmentId, ct);
            ticket = new Ticket
            {
                Id = Guid.CreateVersion7(),
                DepartmentId = form.DepartmentId,
                Number = number,
                Reference = string.Create(CultureInfo.InvariantCulture, $"{department.Key}-{number}"),
                RequesterId = requesterId,
                Subject = request.Subject!.Trim(),
                Status = TicketStatus.Open,
                Priority = form.DefaultPriority ?? TicketPriority.Normal,
                CreatedByUserId = current.UserId,
                CreatedAt = now,
                UpdatedAt = now,
                LastPublicActivityAt = now,
                Source = TicketSources.Portal,
                RequestFormId = form.Id,
                TicketTypeId = typeId,
                MailboxId = (await mailboxes.FindReplyMailboxAsync(form.DepartmentId, null, ct))?.MailboxId, // the department sender identity is fixed on the ticket now
            };
            var first = new Message
            {
                Id = Guid.CreateVersion7(),
                TicketId = ticket.Id,
                DepartmentId = form.DepartmentId,
                Kind = MessageKind.Public,
                AuthorType = AuthorType.Requester,
                AuthorRequesterId = requesterId,
                Body = request.Description!,
                Source = MessageSources.Portal,
                SourceRef = sourceRef,
                DeliveryState = DeliveryState.None,
                CreatedAt = now,
            };
            db.Tickets.Add(ticket);
            db.Messages.Add(first);
            await FieldValueStore.ApplyAsync(db, ticket, values, form.Id, now, ct);

            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (Helpdesk.SharedKernel.Database.PostgresErrors.IsUniqueViolation(ex, out var constraint) && constraint == "ux_messages_source_ref")
            {
                // The same submission arrived twice: return the ticket the first one created.
                db.ChangeTracker.Clear();
                await tx.RollbackAsync(ct);
                var existing = await db.Messages.AsNoTracking().IgnoreDepartmentScope().Where(m => m.DepartmentId == form.DepartmentId && m.Source == MessageSources.Portal && m.SourceRef == sourceRef)
                    .Join(db.Tickets.IgnoreDepartmentScope(), m => m.TicketId, t => t.Id, (m, t) => new { t.Reference, t.Status }).SingleAsync(ct);
                return new PortalCreatedDto(existing.Reference, PortalStatus.Code(existing.Status), PortalStatus.Label(existing.Status));
            }

            await audit.WriteAsync(TicketAuditActions.Created, TicketingAudit.TicketObject, ticket.Id, form.DepartmentId, null,
                TicketingAudit.CreatedPayload(ticket, TicketSources.Portal), ct);
            await audit.WriteAsync(TicketAuditActions.MessageAdded, TicketingAudit.TicketObject, ticket.Id, form.DepartmentId, null,
                new { messageId = first.Id, kind = first.Kind.ToString(), authorType = first.AuthorType.ToString() }, ct);
            await lifecycle.BeforeCommitAsync(ticket, requester.EmailNormalised, createdChange, ct, Provenances.Portal);
            acknowledgement = await autoReplies.QueueForNewTicketAsync(ticket, requester, department.Name, TicketSources.Portal, ct);
            await tx.CommitAsync(ct);
        }

        if (acknowledgement is { } queued)
        {
            await outbox.NotifyQueuedAsync(queued, ct);
        }

        await events.PublishAsync(form.DepartmentId, ticket.Id, ticket.Reference, TicketEventKinds.Created, current.UserId, ct);
        await lifecycle.AfterCommitAsync(ticket, createdChange, Provenances.Portal, ct);
        var status = await db.Tickets.AsNoTracking().IgnoreDepartmentScope().Where(t => t.Id == ticket.Id && t.DepartmentId == ticket.DepartmentId).Select(t => t.Status).SingleAsync(ct);
        return new PortalCreatedDto(ticket.Reference, PortalStatus.Code(status), PortalStatus.Label(status));
    }

    // ----- requester-editable details -----

    /// <summary>Fields the requester may change after submitting: active, requester visible AND requester editable, and part of this ticket (its type, or the form it came from).</summary>
    private async Task<List<FieldBinding>> EditableBindingsAsync(Ticket ticket, CancellationToken ct)
    {
        if (ticket.Status == TicketStatus.Closed)
        {
            return [];
        }

        var bindings = await catalog.ForTypeAsync(ticket.DepartmentId, ticket.TicketTypeId, ct);
        if (ticket.RequestFormId is { } formId)
        {
            foreach (var b in await catalog.ForFormAsync(formId, ticket.DepartmentId, ct))
            {
                if (bindings.All(x => x.Field.Id != b.Field.Id))
                {
                    bindings.Add(b);
                }
            }
        }

        return [.. bindings.Where(b => b.Field.RequesterVisible && b.Field.RequesterEditable)];
    }

    public async Task UpdateFieldsAsync(string reference, Dictionary<string, JsonElement>? submitted, CancellationToken ct)
    {
        if (submitted is null || submitted.Count == 0)
        {
            throw new RequestValidationException("fields", "Provide at least one field.");
        }

        var ticket = await OwnTicketAsync(reference, track: true, ct);
        var allowed = await EditableBindingsAsync(ticket, ct);
        var errors = new Dictionary<string, string[]>();
        var result = FieldValueValidator.Validate(allowed, submitted, ValidationMode.Patch, errors);
        TicketValidation.ThrowIfAny(errors);

        var now = clock.GetUtcNowMicroseconds();
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            var changes = await FieldValueStore.ApplyAsync(db, ticket, result, ticket.RequestFormId, now, ct);
            if (changes.Count == 0)
            {
                return;
            }

            ticket.UpdatedAt = now;
            await db.SaveChangesAsync(ct);
            await FieldValueStore.AuditAsync(audit, ticket, changes, ct);
            await tx.CommitAsync(ct);
        }

        await events.PublishAsync(ticket.DepartmentId, ticket.Id, ticket.Reference, TicketEventKinds.Updated, current.UserId, ct);
    }
}
