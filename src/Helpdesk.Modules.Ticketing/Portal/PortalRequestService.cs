using System.Globalization;
using System.Text.Json;
using Helpdesk.Modules.Attachments.Contracts;
using Helpdesk.Modules.Organisation.Contracts;
using Helpdesk.Modules.Ticketing.Automation;
using Helpdesk.Modules.Ticketing.Contracts;
using Helpdesk.Modules.Ticketing.Domain;
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

internal sealed record PortalRequestDto(
    string Reference, string Subject, string Status, string StatusLabel, string Department, string? Form, DateTimeOffset CreatedAt, DateTimeOffset LastActivityAt,
    IReadOnlyList<PortalFieldValueDto> Fields, IReadOnlyList<PortalMessageDto> Messages);

internal sealed record PortalReplyResultDto(PortalMessageDto Message, string Status, string StatusLabel);

internal sealed record PortalCreateRequest(Guid? FormId, string? Subject, string? Description, Dictionary<string, JsonElement>? Fields, Guid? SubmissionId);

internal sealed record PortalCreatedDto(string Reference, string Status, string StatusLabel);

/// <summary>
/// Requester self-service over the same ticket model as agents and email. Every query is constrained to requesters linked to the signed-in user
/// BEFORE any row, snippet or count is produced; an unknown or foreign reference is a plain 404. The caller's agent permissions play no part.
/// </summary>
internal sealed class PortalRequestService(
    TicketingDbContext db,
    RequesterIdentityService identity,
    IOrganisationDirectory organisation,
    IAttachmentStore attachments,
    ITicketIngress ingress,
    ITicketEvents events,
    TicketLifecycle lifecycle,
    TicketingAudit audit,
    ICurrentUser current,
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

        var tickets = db.Tickets.AsNoTracking().IgnoreDepartmentScope().Where(t => ids.Contains(t.RequesterId));
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

    private async Task<Ticket> OwnTicketAsync(string reference, bool track, CancellationToken ct)
    {
        var ids = await identity.RequesterIdsAsync(ct);
        var normalised = reference.Trim().ToUpperInvariant();
        if (ids.Count == 0 || !TicketValidation.IsReference(normalised))
        {
            throw new NotFoundException();
        }

        var query = db.Tickets.IgnoreDepartmentScope().Where(t => t.Reference == normalised && ids.Contains(t.RequesterId));
        return await (track ? query : query.AsNoTracking()).SingleOrDefaultAsync(ct) ?? throw new NotFoundException();
    }

    public async Task<PortalRequestDto> GetAsync(string reference, CancellationToken ct)
    {
        var ticket = await OwnTicketAsync(reference, track: false, ct);
        var department = (await organisation.GetDepartmentsAsync([ticket.DepartmentId], ct)).GetValueOrDefault(ticket.DepartmentId);
        var formName = ticket.RequestFormId is { } formId
            ? await db.RequestForms.AsNoTracking().Where(f => f.Id == formId && f.DepartmentId == ticket.DepartmentId).Select(f => f.Name).SingleOrDefaultAsync(ct)
            : null;

        var fields = await db.TicketFieldValues.AsNoTracking().IgnoreDepartmentScope()
            .Where(v => v.TicketId == ticket.Id && v.DepartmentId == ticket.DepartmentId && v.RequesterVisible)
            .OrderBy(v => v.SortOrder).Select(v => new PortalFieldValueDto(v.Label, v.Display)).ToListAsync(ct);

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
            [.. messages.Select(m => ToMessage(m, ticket, files))]);
    }

    private static PortalMessageDto ToMessage(Message m, Ticket ticket, IReadOnlyDictionary<Guid, IReadOnlyList<AttachmentSummary>> files)
    {
        var fromRequester = m.AuthorType == AuthorType.Requester;
        return new PortalMessageDto(
            m.Id,
            !fromRequester ? "Support" : m.AuthorRequesterId == ticket.RequesterId ? "You" : "Another participant",
            fromRequester && m.AuthorRequesterId == ticket.RequesterId,
            m.Body,
            m.CreatedAt,
            files.TryGetValue(m.Id, out var list) ? [.. list.Where(a => !a.IsInline).Select(a => new PortalAttachmentDto(a.Id, a.FileName, a.Size, a.Downloadable))] : []);
    }

    // ----- reply -----

    public async Task<PortalReplyResultDto> ReplyAsync(string reference, string? body, IReadOnlyList<IFormFile> files, CancellationToken ct)
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
        var result = await ingress.AddRequesterMessageAsync(ticket.DepartmentId, ticket.Id, ticket.RequesterId, body!, MessageSources.Portal, ct);
        for (var i = 0; i < files.Count; i++)
        {
            var file = files[i];
            var name = Path.GetFileName(file.FileName);
            var content = file.Length <= 25 * 1024 * 1024 ? await ReadAsync(file, ct) : [];
            await attachments.StoreAsync(new StoreAttachmentRequest(
                ticket.DepartmentId, ticket.Id, result.MessageId, string.IsNullOrWhiteSpace(name) ? "attachment" : name, file.ContentType, content, false, null,
                $"portal:{result.MessageId:N}:{i}", file.Length), ct);
        }

        await events.PublishAsync(ticket.DepartmentId, ticket.Id, ticket.Reference, TicketEventKinds.Message, current.UserId, ct);
        if (result.Reopened)
        {
            await events.PublishAsync(ticket.DepartmentId, ticket.Id, ticket.Reference, TicketEventKinds.Status, current.UserId, ct);
        }

        if (files.Count > 0)
        {
            await events.PublishAsync(ticket.DepartmentId, ticket.Id, ticket.Reference, TicketEventKinds.Attachment, current.UserId, ct);
        }

        var message = await db.Messages.AsNoTracking().IgnoreDepartmentScope().SingleAsync(m => m.Id == result.MessageId && m.DepartmentId == ticket.DepartmentId, ct);
        var stored = await attachments.ListForMessagesAsync(ticket.DepartmentId, [message.Id], ct);
        var status = await db.Tickets.AsNoTracking().IgnoreDepartmentScope().Where(t => t.Id == ticket.Id && t.DepartmentId == ticket.DepartmentId).Select(t => t.Status).SingleAsync(ct);
        return new PortalReplyResultDto(ToMessage(message, ticket, stored), PortalStatus.Code(status), PortalStatus.Label(status));
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
        var ticket = await OwnTicketAsync(reference, track: false, ct);
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

        // The form decides the department and the defaults. Hidden or inactive forms do not exist for requesters.
        var form = await db.RequestForms.AsNoTracking().SingleOrDefaultAsync(f => f.Id == request.FormId!.Value && f.IsActive && f.VisibleToRequesters, ct) ?? throw new NotFoundException();
        var department = await organisation.FindDepartmentAsync(form.DepartmentId, ct);
        if (department is null || !department.IsActive)
        {
            throw new NotFoundException();
        }

        var definitions = await db.FormFields.AsNoTracking().Where(f => f.FormId == form.Id && f.DepartmentId == form.DepartmentId).OrderBy(f => f.SortOrder).ThenBy(f => f.Key).ToListAsync(ct);
        var values = FormSubmissionValidator.Validate(definitions, request.Fields ?? [], errors);
        TicketValidation.ThrowIfAny(errors);

        Guid? teamId = null;
        if (form.DefaultTeamId is { } defaultTeam && await organisation.FindTeamAsync(form.DepartmentId, defaultTeam, ct) is { IsActive: true })
        {
            teamId = defaultTeam;
        }

        var requesterId = await identity.EnsureRequesterAsync(form.DepartmentId, ct);
        var requester = await db.Requesters.AsNoTracking().IgnoreDepartmentScope().SingleAsync(r => r.Id == requesterId && r.DepartmentId == form.DepartmentId, ct);
        var submissionId = request.SubmissionId ?? Guid.CreateVersion7();
        var sourceRef = string.Create(CultureInfo.InvariantCulture, $"portal:{requesterId:N}:{submissionId:N}");

        var now = clock.GetUtcNowMicroseconds();
        Ticket ticket;
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
                TeamId = teamId,
                CreatedByUserId = current.UserId,
                CreatedAt = now,
                UpdatedAt = now,
                LastPublicActivityAt = now,
                Source = TicketSources.Portal,
                RequestFormId = form.Id,
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
            foreach (var value in values)
            {
                db.TicketFieldValues.Add(new TicketFieldValue
                {
                    Id = Guid.CreateVersion7(),
                    TicketId = ticket.Id,
                    DepartmentId = form.DepartmentId,
                    FormId = form.Id,
                    FieldId = value.FieldId,
                    Key = value.Key,
                    Label = value.Label,
                    FieldType = value.FieldType,
                    ValueJson = value.ValueJson,
                    Display = value.Display,
                    RequesterVisible = value.RequesterVisible,
                    AgentVisible = value.AgentVisible,
                    SortOrder = value.SortOrder,
                    CreatedAt = now,
                });
            }

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
                new { reference = ticket.Reference, status = ticket.Status.ToString(), priority = ticket.Priority.ToString(), teamId = ticket.TeamId, source = TicketSources.Portal, formId = form.Id }, ct);
            await audit.WriteAsync(TicketAuditActions.MessageAdded, TicketingAudit.TicketObject, ticket.Id, form.DepartmentId, null,
                new { messageId = first.Id, kind = first.Kind.ToString(), authorType = first.AuthorType.ToString() }, ct);
            await lifecycle.BeforeCommitAsync(ticket, requester.EmailNormalised, createdChange, ct);
            await tx.CommitAsync(ct);
        }

        await events.PublishAsync(form.DepartmentId, ticket.Id, ticket.Reference, TicketEventKinds.Created, current.UserId, ct);
        await lifecycle.AfterCommitAsync(ticket, createdChange, Provenances.Portal, ct);
        var status = await db.Tickets.AsNoTracking().IgnoreDepartmentScope().Where(t => t.Id == ticket.Id && t.DepartmentId == ticket.DepartmentId).Select(t => t.Status).SingleAsync(ct);
        return new PortalCreatedDto(ticket.Reference, PortalStatus.Code(status), PortalStatus.Label(status));
    }
}
