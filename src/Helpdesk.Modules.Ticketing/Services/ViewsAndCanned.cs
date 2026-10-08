using System.Text.Json;
using Helpdesk.Modules.Organisation.Contracts;
using Helpdesk.Modules.Ticketing.Domain;
using Helpdesk.Modules.Ticketing.Persistence;
using Helpdesk.SharedKernel.Authorization;
using Helpdesk.SharedKernel.Database;
using Helpdesk.SharedKernel.Errors;
using Helpdesk.SharedKernel.Security;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Modules.Ticketing.Services;

/// <summary>The structured filter a saved view stores. The same fields the queue offers; no query language.</summary>
internal sealed record ViewFilter(
    string? Queue,
    Guid? DepartmentId,
    Guid? AssigneeUserId,
    bool? Unassigned,
    string[]? Status,
    string[]? Priority,
    string? Q,
    string? Sort,
    string? Sla = null,
    Guid? TypeId = null,
    string? Send = null);

internal sealed record SavedViewDto(Guid Id, string Name, ViewFilter Filter, bool IsPinned, string Version);

internal sealed record CreateViewRequest(string? Name, ViewFilter? Filter, bool? IsPinned);

internal sealed record UpdateViewRequest(string? Version, string? Name, ViewFilter? Filter, bool? IsPinned);

/// <summary>
/// Personal saved views. They are owner-only (404 for anyone else) and store a validated filter. Running a view means calling the
/// ordinary list endpoint with that filter (or <c>?view=</c>), so the department scope of the query always applies: a view can never
/// return anything the caller could not list anyway, even if the stored department/assignee ids point elsewhere.
/// </summary>
internal sealed class SavedViewService(TicketingDbContext db, ICurrentUser current, TimeProvider clock)
{
    public const int MaxViewsPerUser = 50;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    public async Task<IReadOnlyList<SavedViewDto>> ListAsync(CancellationToken ct)
    {
        var me = current.UserId;
        var rows = await db.SavedViews.AsNoTracking().Where(v => v.OwnerUserId == me).OrderByDescending(v => v.IsPinned).ThenBy(v => v.Name).ToListAsync(ct);
        return [.. rows.Select(ToDto)];
    }

    public async Task<SavedViewDto> CreateAsync(CreateViewRequest request, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        var name = ValidateName(errors, request.Name);
        var filter = Normalise(errors, request.Filter);
        TicketValidation.ThrowIfAny(errors);

        var me = current.UserId;
        if (await db.SavedViews.CountAsync(v => v.OwnerUserId == me, ct) >= MaxViewsPerUser)
        {
            throw new ConflictException($"You can keep at most {MaxViewsPerUser} saved views.", ProblemCodes.InvalidState);
        }

        var now = clock.GetUtcNowMicroseconds();
        var view = new SavedView
        {
            Id = Guid.CreateVersion7(),
            OwnerUserId = me,
            Name = name!,
            Filter = JsonSerializer.Serialize(filter, Json),
            IsPinned = request.IsPinned ?? false,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.SavedViews.Add(view);
        await SaveAsync(ct);
        return ToDto(view);
    }

    public async Task<SavedViewDto> UpdateAsync(Guid id, UpdateViewRequest request, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        var version = TicketValidation.Version(errors, request.Version);
        string? name = null;
        if (request.Name is not null)
        {
            name = ValidateName(errors, request.Name);
        }

        ViewFilter? filter = request.Filter is null ? null : Normalise(errors, request.Filter);
        if (request.Name is null && request.Filter is null && request.IsPinned is null)
        {
            errors["body"] = ["Provide at least one of name, filter, isPinned."];
        }

        TicketValidation.ThrowIfAny(errors);

        var view = await FindAsync(id, ct);
        if (view.Version != version)
        {
            throw new ConflictException("The view was changed elsewhere. Reload and retry.", ProblemCodes.VersionConflict);
        }

        db.Entry(view).Property(v => v.Version).OriginalValue = version;
        if (name is not null)
        {
            view.Name = name;
        }

        if (filter is not null)
        {
            view.Filter = JsonSerializer.Serialize(filter, Json);
        }

        if (request.IsPinned is { } pinned)
        {
            view.IsPinned = pinned;
        }

        view.UpdatedAt = clock.GetUtcNowMicroseconds();
        await SaveAsync(ct);
        return ToDto(view);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var view = await FindAsync(id, ct);
        db.SavedViews.Remove(view);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>The stored filter of one of the CALLER'S views (404 for other users' views).</summary>
    public async Task<ViewFilter> FilterOfAsync(Guid id, CancellationToken ct)
    {
        var view = await db.SavedViews.AsNoTracking().SingleOrDefaultAsync(v => v.Id == id && v.OwnerUserId == current.UserId, ct) ?? throw new NotFoundException();
        return JsonSerializer.Deserialize<ViewFilter>(view.Filter, Json) ?? new ViewFilter(null, null, null, null, null, null, null, null);
    }

    private async Task<SavedView> FindAsync(Guid id, CancellationToken ct) =>
        await db.SavedViews.SingleOrDefaultAsync(v => v.Id == id && v.OwnerUserId == current.UserId, ct) ?? throw new NotFoundException();

    private static string? ValidateName(Dictionary<string, string[]> errors, string? name)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > 80 || TicketValidation.HasNul(trimmed))
        {
            errors["name"] = ["name is required and at most 80 characters."];
            return null;
        }

        return trimmed;
    }

    /// <summary>Validates every field with the same rules as the list endpoint and stores canonical values.</summary>
    internal static ViewFilter Normalise(Dictionary<string, string[]> errors, ViewFilter? filter)
    {
        if (filter is null)
        {
            errors["filter"] = ["filter is required."];
            return new ViewFilter(null, null, null, null, null, null, null, null);
        }

        var queue = (filter.Queue ?? "all").Trim().ToLowerInvariant();
        if (queue is not ("my" or "unassigned" or "all" or "following"))
        {
            errors["filter.queue"] = ["queue must be one of my, unassigned, all, following."];
        }

        string[]? Canon<T>(string[]? raw, string field)
            where T : struct, Enum
        {
            if (raw is null or { Length: 0 })
            {
                return null;
            }

            var result = new List<string>();
            foreach (var value in raw.Take(8))
            {
                if (TicketValidation.ParseEnum<T>(errors, "filter." + field, value) is { } parsed)
                {
                    result.Add(parsed.ToString());
                }
            }

            return [.. result.Distinct()];
        }

        var status = Canon<TicketStatus>(filter.Status, "status");
        var priority = Canon<TicketPriority>(filter.Priority, "priority");
        var q = filter.Q?.Trim();
        if (!string.IsNullOrEmpty(q) && (q.Length is < 2 or > 100 || TicketValidation.HasNul(q)))
        {
            errors["filter.q"] = ["q must be between 2 and 100 characters."];
        }

        var sort = (filter.Sort ?? "updated_desc").Trim().ToLowerInvariant();
        if (sort is not ("updated_desc" or "updated_asc" or "created_desc" or "created_asc" or "priority_desc" or "sla_due_asc"))
        {
            errors["filter.sort"] = ["sort must be one of updated_desc, updated_asc, created_desc, created_asc, priority_desc, sla_due_asc."];
        }

        var sla = filter.Sla?.Trim().ToLowerInvariant();
        if (!string.IsNullOrEmpty(sla) && !TicketQueryService.SlaFilters.Contains(sla))
        {
            errors["filter.sla"] = [$"sla must be one of {string.Join(", ", TicketQueryService.SlaFilters)}."];
        }

        var send = filter.Send?.Trim().ToLowerInvariant();
        if (!string.IsNullOrEmpty(send) && !TicketQueryService.SendFilters.Contains(send))
        {
            errors["filter.send"] = [$"send must be one of {string.Join(", ", TicketQueryService.SendFilters)}."];
        }

        return new ViewFilter(queue, filter.DepartmentId, filter.AssigneeUserId, filter.Unassigned == true ? true : null, status, priority,
            string.IsNullOrEmpty(q) ? null : q, sort, string.IsNullOrEmpty(sla) ? null : sla, filter.TypeId, string.IsNullOrEmpty(send) ? null : send);
    }

    private SavedViewDto ToDto(SavedView v) =>
        new(v.Id, v.Name, JsonSerializer.Deserialize<ViewFilter>(v.Filter, Json) ?? new ViewFilter(null, null, null, null, null, null, null, null), v.IsPinned,
            ConcurrencyVersion.ToToken(v.Version));

    private async Task SaveAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictException("The view was changed elsewhere. Reload and retry.", ProblemCodes.VersionConflict);
        }
        catch (DbUpdateException ex) when (Helpdesk.SharedKernel.Database.PostgresErrors.IsUniqueViolation(ex, out _))
        {
            throw new ConflictException("You already have a view with this name.", ProblemCodes.Duplicate);
        }
    }
}

internal sealed record CannedResponseDto(
    Guid Id, string Scope, Guid? DepartmentId, string? DepartmentName, string Title, string Body, bool IsActive, bool CanManage, DateTimeOffset UpdatedAt, string Version);

internal sealed record CreateCannedRequest(string? Scope, Guid? DepartmentId, string? Title, string? Body);

internal sealed record UpdateCannedRequest(string? Version, string? Title, string? Body, bool? IsActive);

/// <summary>A canned response with its placeholders filled in for one ticket, ready to insert into a draft. <see cref="EmptyPlaceholders"/> names placeholders whose value is empty for this ticket (the client may warn before the agent sends).</summary>
internal sealed record CannedRenderDto(Guid Id, string Title, string Body, IReadOnlyList<string> EmptyPlaceholders);

/// <summary>
/// Canned responses: personal (owner only, usable in every department the owner can reply in) and department (shared; managed by
/// <c>department.canned.manage</c>). Inserting one into the composer is a client action; nothing here sends anything.
/// </summary>
internal sealed class CannedResponseService(
    TicketingDbContext db,
    IDepartmentScope scope,
    IAuthorizer authorizer,
    IOrganisationDirectory organisation,
    TicketingAudit audit,
    ICurrentUser current,
    TimeProvider clock,
    TicketResolver tickets)
{
    public const int MaxPerOwner = 200;

    /// <summary>The only placeholders a canned response may use: the ticket's own facts and the inserting agent's name. Same fixed-list, single-pass mechanism as the email identity templates.</summary>
    public static readonly string[] Placeholders = ["requester.name", "ticket.reference", "ticket.subject", "department.name", "agent.name"];

    /// <summary>
    /// The response filled in for ONE ticket, for insertion into the composer. Needs tickets.reply or tickets.note on the ticket's department (the endpoint filter already
    /// resolved the ticket through the caller's department scope). Usable: the caller's own personal response, or an ACTIVE response of the ticket's own department;
    /// anything else behaves as missing. Read-only: nothing is sent, stored or audited, and no internal-note content is involved.
    /// </summary>
    public async Task<CannedRenderDto> RenderAsync(string ticketRoute, Guid id, CancellationToken ct)
    {
        await scope.EnsureLoadedAsync(ct);
        var key = await tickets.FindAsync(ticketRoute, ct) ?? throw new NotFoundException();
        if (await authorizer.CheckAsync(PermissionCodes.TicketsReply, key.DepartmentId, ct) != AccessOutcome.Allowed
            && await authorizer.CheckAsync(PermissionCodes.TicketsNote, key.DepartmentId, ct) != AccessOutcome.Allowed)
        {
            throw new ForbiddenException();
        }

        var me = current.UserId;
        var response = await db.CannedResponses.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id, ct);
        var usable = response is not null && response.IsActive
            && ((response.Scope == CannedScopes.Personal && response.OwnerUserId == me) || (response.Scope == CannedScopes.Department && response.DepartmentId == key.DepartmentId));
        if (!usable)
        {
            throw new NotFoundException();
        }

        var ticket = await db.Tickets.AsNoTracking().Where(t => t.Id == key.Id && t.DepartmentId == key.DepartmentId)
            .Select(t => new { t.Reference, t.Subject, RequesterName = t.Requester.DisplayName }).SingleOrDefaultAsync(ct) ?? throw new NotFoundException();
        var department = await organisation.FindDepartmentAsync(key.DepartmentId, ct) ?? throw new NotFoundException();
        var values = new Dictionary<string, string>
        {
            ["requester.name"] = TemplateText.OneLine(ticket.RequesterName ?? ""),
            ["ticket.reference"] = ticket.Reference,
            ["ticket.subject"] = TemplateText.OneLine(ticket.Subject ?? ""),
            ["department.name"] = TemplateText.OneLine(department.Name),
            ["agent.name"] = TemplateText.OneLine(current.DisplayName ?? ""),
        };
        var used = TemplateText.PlaceholdersIn(response!.Body);
        return new CannedRenderDto(response.Id, response.Title, TemplateText.Render(response.Body, values, v => v), [.. used.Where(u => values.TryGetValue(u, out var v) && v.Length == 0)]);
    }

    public async Task<IReadOnlyList<CannedResponseDto>> ListAsync(Guid? departmentId, bool includeInactive, CancellationToken ct)
    {
        await scope.EnsureLoadedAsync(ct);
        var me = current.UserId;
        var usable = (await authorizer.AccessibleDepartmentsAsync(PermissionCodes.TicketsReply, ct))
            .Union(await authorizer.AccessibleDepartmentsAsync(PermissionCodes.TicketsNote, ct)).ToHashSet();
        var manage = (await authorizer.AccessibleDepartmentsAsync(PermissionCodes.DepartmentCannedManage, ct)).ToHashSet();
        var visible = usable.Union(manage).ToArray();

        var rows = await db.CannedResponses.AsNoTracking()
            .Where(c => (c.Scope == CannedScopes.Personal && c.OwnerUserId == me) || (c.Scope == CannedScopes.Department && visible.Contains(c.DepartmentId!.Value)))
            .Where(c => departmentId == null || c.Scope == CannedScopes.Personal || c.DepartmentId == departmentId)
            .OrderBy(c => c.Scope).ThenBy(c => c.Title).ThenBy(c => c.Id)
            .ToListAsync(ct);
        rows = [.. rows.Where(c => includeInactive ? (c.IsActive || c.Scope == CannedScopes.Personal || manage.Contains(c.DepartmentId!.Value)) : c.IsActive)];

        var names = await organisation.GetDepartmentsAsync([.. rows.Where(r => r.DepartmentId is not null).Select(r => r.DepartmentId!.Value).Distinct()], ct);
        return [.. rows.Select(c => ToDto(c, c.DepartmentId is { } d && names.TryGetValue(d, out var info) ? info.Name : null, manage))];
    }

    public async Task<CannedResponseDto> CreateAsync(CreateCannedRequest request, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        var scopeName = request.Scope?.Trim();
        if (scopeName is not (CannedScopes.Personal or CannedScopes.Department))
        {
            errors["scope"] = ["scope must be Personal or Department."];
        }

        var (title, body) = Validate(errors, request.Title, request.Body, requireBoth: true);
        if (scopeName == CannedScopes.Department && request.DepartmentId is null)
        {
            errors["departmentId"] = ["departmentId is required for department responses."];
        }

        if (scopeName == CannedScopes.Personal && request.DepartmentId is not null)
        {
            errors["departmentId"] = ["Personal responses have no department."];
        }

        TicketValidation.ThrowIfAny(errors);

        await scope.EnsureLoadedAsync(ct);
        var now = clock.GetUtcNowMicroseconds();
        var response = new CannedResponse { Id = Guid.CreateVersion7(), Scope = scopeName!, Title = title!, Body = body!, IsActive = true, CreatedAt = now, UpdatedAt = now };
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (scopeName == CannedScopes.Personal)
        {
            if (!await authorizer.HasInAnyDepartmentAsync(PermissionCodes.TicketsReply, ct) && !await authorizer.HasInAnyDepartmentAsync(PermissionCodes.TicketsNote, ct))
            {
                throw new ForbiddenException();
            }

            var me = current.UserId;
            if (await db.CannedResponses.CountAsync(c => c.OwnerUserId == me, ct) >= MaxPerOwner)
            {
                throw new ConflictException($"You can keep at most {MaxPerOwner} personal responses.", ProblemCodes.InvalidState);
            }

            response.OwnerUserId = me;
        }
        else
        {
            var departmentId = request.DepartmentId!.Value;
            await authorizer.RequireAsync(PermissionCodes.DepartmentCannedManage, departmentId, ct);
            if (await db.CannedResponses.CountAsync(c => c.DepartmentId == departmentId, ct) >= MaxPerOwner)
            {
                throw new ConflictException($"A department can keep at most {MaxPerOwner} responses.", ProblemCodes.InvalidState);
            }

            response.DepartmentId = departmentId;
        }

        db.CannedResponses.Add(response);
        await db.SaveChangesAsync(ct);
        if (response.DepartmentId is { } auditedDepartment)
        {
            await audit.WriteAsync(TicketAuditActions.CannedCreated, TicketingAudit.CannedObject, response.Id, auditedDepartment, null, new { title = response.Title }, ct);
        }

        await tx.CommitAsync(ct);
        return await ToDtoAsync(response, ct);
    }

    public async Task<CannedResponseDto> UpdateAsync(Guid id, UpdateCannedRequest request, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        var version = TicketValidation.Version(errors, request.Version);
        var (title, body) = Validate(errors, request.Title, request.Body, requireBoth: false);
        if (request.Title is null && request.Body is null && request.IsActive is null)
        {
            errors["body"] = ["Provide at least one of title, body, isActive."];
        }

        TicketValidation.ThrowIfAny(errors);

        await scope.EnsureLoadedAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var response = await db.CannedResponses.SingleOrDefaultAsync(c => c.Id == id, ct) ?? throw new NotFoundException();
        if (response.Scope == CannedScopes.Personal)
        {
            if (response.OwnerUserId != current.UserId)
            {
                throw new NotFoundException(); // someone else's personal response does not exist for the caller
            }
        }
        else
        {
            await authorizer.RequireAsync(PermissionCodes.DepartmentCannedManage, response.DepartmentId!.Value, ct);
        }

        if (response.Version != version)
        {
            throw new ConflictException("The response was changed elsewhere. Reload and retry.", ProblemCodes.VersionConflict);
        }

        db.Entry(response).Property(c => c.Version).OriginalValue = version;
        if (title is not null)
        {
            response.Title = title;
        }

        if (body is not null)
        {
            response.Body = body;
        }

        if (request.IsActive is { } active)
        {
            response.IsActive = active;
        }

        response.UpdatedAt = clock.GetUtcNowMicroseconds();
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictException("The response was changed elsewhere. Reload and retry.", ProblemCodes.VersionConflict);
        }

        if (response.DepartmentId is { } departmentId)
        {
            await audit.WriteAsync(TicketAuditActions.CannedUpdated, TicketingAudit.CannedObject, response.Id, departmentId, null, new { title = response.Title, isActive = response.IsActive }, ct);
        }

        await tx.CommitAsync(ct);
        return await ToDtoAsync(response, ct);
    }

    private static (string? Title, string? Body) Validate(Dictionary<string, string[]> errors, string? title, string? body, bool requireBoth)
    {
        string? t = null;
        string? b = null;
        if (title is not null || requireBoth)
        {
            t = title?.Trim();
            if (string.IsNullOrEmpty(t) || t.Length > 120 || TicketValidation.HasNul(t))
            {
                errors["title"] = ["title is required and at most 120 characters."];
                t = null;
            }
        }

        if (body is not null || requireBoth)
        {
            b = body?.Replace("\r\n", "\n", StringComparison.Ordinal);
            if (string.IsNullOrWhiteSpace(b) || b.Length > 5000 || TicketValidation.HasNul(b))
            {
                errors["body"] = ["body is required and at most 5000 characters."];
                b = null;
            }
            else if (TemplateText.Validate(b, Placeholders) is { } templateError)
            {
                errors["body"] = [templateError];
                b = null;
            }
        }

        return (t, b);
    }

    private async Task<CannedResponseDto> ToDtoAsync(CannedResponse c, CancellationToken ct)
    {
        string? name = null;
        var manage = new HashSet<Guid>();
        if (c.DepartmentId is { } d)
        {
            name = (await organisation.FindDepartmentAsync(d, ct))?.Name;
            manage = (await authorizer.AccessibleDepartmentsAsync(PermissionCodes.DepartmentCannedManage, ct)).ToHashSet();
        }

        return ToDto(c, name, manage);
    }

    private CannedResponseDto ToDto(CannedResponse c, string? departmentName, HashSet<Guid> manage) =>
        new(c.Id, c.Scope, c.DepartmentId, departmentName, c.Title, c.Body, c.IsActive,
            c.Scope == CannedScopes.Personal ? c.OwnerUserId == current.UserId : manage.Contains(c.DepartmentId!.Value), c.UpdatedAt, ConcurrencyVersion.ToToken(c.Version));
}
