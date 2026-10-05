using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Helpdesk.Modules.Audit.Contracts;
using Helpdesk.Modules.Identity.Contracts;
using Helpdesk.Modules.Organisation.Contracts;
using Helpdesk.Modules.Ticketing.Domain;
using Helpdesk.Modules.Ticketing.Persistence;
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

        var now = clock.GetUtcNowMicroseconds();
        Ticket ticket;
        Requester requester;
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
            await SaveAsync(ct);

            await audit.WriteAsync(TicketAuditActions.Created, TicketingAudit.TicketObject, ticket.Id, departmentId, null,
                new
                {
                    reference = ticket.Reference,
                    status = ticket.Status.ToString(),
                    priority = ticket.Priority.ToString(),
                    teamId = ticket.TeamId,
                    assigneeUserId = ticket.AssigneeUserId,
                }, ct);
            await audit.WriteAsync(TicketAuditActions.MessageAdded, TicketingAudit.TicketObject, ticket.Id, departmentId, null,
                new { messageId = first.Id, kind = first.Kind.ToString(), authorType = first.AuthorType.ToString() }, ct);
            await tx.CommitAsync(ct);
        }

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
            ticket.Priority = newPriority;
        }

        if (status is { } newStatus && newStatus != ticket.Status)
        {
            var previous = ticket.Status;
            ticket.ApplyStatus(newStatus, now);
            changes.Add((TicketAuditActions.StatusChanged, new { status = previous.ToString() }, new { status = newStatus.ToString() }));
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

        await tx.CommitAsync(ct);
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
        if (request.TeamId != ticket.TeamId)
        {
            await ValidateTeamAsync(ticket.DepartmentId, request.TeamId, ct);
            changes.Add((TicketAuditActions.TeamChanged, new { teamId = ticket.TeamId }, new { teamId = request.TeamId }));
        }

        if (request.AssigneeUserId != ticket.AssigneeUserId)
        {
            await ValidateAssigneeAsync(ticket.DepartmentId, request.AssigneeUserId, ct);
            changes.Add((TicketAuditActions.AssigneeChanged, new { assigneeUserId = ticket.AssigneeUserId }, new { assigneeUserId = request.AssigneeUserId }));
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

            // Phase 2 has no mail transport: a public reply is recorded, never sent, and the state says so.
            DeliveryState = kind == MessageKind.Public ? DeliveryState.NotDelivered : DeliveryState.None,
            CreatedAt = now,
        };
        db.Messages.Add(message);

        var previousStatus = ticket.Status;
        var statusChanged = status is { } requested && ticket.ApplyStatus(requested, now);
        ticket.UpdatedAt = now;
        await SaveAsync(ct);

        await audit.WriteAsync(TicketAuditActions.MessageAdded, TicketingAudit.TicketObject, ticket.Id, ticket.DepartmentId, null,
            new { messageId = message.Id, kind = message.Kind.ToString(), authorType = message.AuthorType.ToString() }, ct);
        if (statusChanged)
        {
            await audit.WriteAsync(TicketAuditActions.StatusChanged, TicketingAudit.TicketObject, ticket.Id, ticket.DepartmentId,
                new { status = previousStatus.ToString() }, new { status = ticket.Status.ToString() }, ct);
        }

        await tx.CommitAsync(ct);
        var dto = new MessageDto(message.Id, message.TicketId, message.Kind.ToString(), message.AuthorType.ToString(),
            new MessageAuthorDto(current.UserId, current.DisplayName), message.Body, message.Source, message.DeliveryState.ToString(), message.CreatedAt);
        return new PostedMessageDto(dto, await projection.DetailAsync(ticket, ticket.Requester, ct));
    }

    public async Task<PageDto<MessageDto>> ListMessagesAsync(string route, string? cursor, int? limit, CancellationToken ct)
    {
        var take = limit ?? DefaultMessageLimit;
        if (take is < 1 or > MaxLimit)
        {
            throw new RequestValidationException("limit", $"limit must be between 1 and {MaxLimit}.");
        }

        var key = await resolver.FindAsync(route, ct) ?? throw new NotFoundException();
        var query = db.Messages.AsNoTracking().Where(m => m.TicketId == key.Id);

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

        var items = rows.Select(m => new MessageDto(
            m.Id,
            m.TicketId,
            m.Kind.ToString(),
            m.AuthorType.ToString(),
            AuthorOf(m, people, requesters),
            m.Body,
            m.Source,
            m.DeliveryState.ToString(),
            m.CreatedAt)).ToList();
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
        var page = await auditReader.ReadObjectEventsAsync(AuditCategories.Ticketing, TicketingAudit.TicketObject, key.Id, key.DepartmentId, cursor, take, ct);
        return new PageDto<ActivityDto>(page.Items.Select(ToActivity).ToList(), page.NextCursor);
    }

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

    // ----- shared helpers -----

    /// <summary>
    /// Serialises writers of one ticket (row lock for the rest of the transaction), then loads it tracked. With
    /// <paramref name="expectedVersion"/> a mismatch is a <c>version_conflict</c>; the lock makes the comparison race-free.
    /// </summary>
    private async Task<Ticket> LockAndLoadAsync(Guid ticketId, uint? expectedVersion, CancellationToken ct)
    {
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM ticketing.tickets WHERE id = {ticketId} FOR UPDATE", ct);
        var ticket = await db.Tickets.Include(t => t.Requester).SingleOrDefaultAsync(t => t.Id == ticketId, ct) ?? throw new NotFoundException();
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
    /// Team/assignee changes need <c>tickets.assign.any</c>. With only <c>tickets.assign.self</c> a caller may take a ticket for
    /// themself or release it when they are its assignee, leaving the team unchanged (on create: no team, assignee = themself).
    /// </summary>
    private async Task RequireAssignmentRightsAsync(Guid departmentId, Guid? teamId, Guid? assigneeUserId, Ticket? ticket, CancellationToken ct)
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
        var selfAssignment = assigneeUserId == current.UserId
            || (assigneeUserId is null && ticket is not null && ticket.AssigneeUserId == current.UserId);
        if (!(self && teamUnchanged && selfAssignment))
        {
            throw new ForbiddenException();
        }
    }

    private async Task ValidateTeamAsync(Guid departmentId, Guid? teamId, CancellationToken ct)
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

    private async Task ValidateAssigneeAsync(Guid departmentId, Guid? assigneeUserId, CancellationToken ct)
    {
        if (assigneeUserId is { } id && !await organisation.IsEligibleAssigneeAsync(departmentId, id, ct))
        {
            throw new ConflictException("The user cannot be assigned tickets in this department.", ProblemCodes.InvalidState);
        }
    }

    private async Task SaveAsync(CancellationToken ct)
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

    internal static ConflictException VersionConflict() =>
        new("The ticket was changed by someone else. Reload and retry.", ProblemCodes.VersionConflict);
}
