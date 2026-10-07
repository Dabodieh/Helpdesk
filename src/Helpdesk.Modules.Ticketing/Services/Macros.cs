using System.Text.Json;
using Helpdesk.Modules.Identity.Contracts;
using Helpdesk.Modules.Organisation.Contracts;
using Helpdesk.Modules.Ticketing.Automation;
using Helpdesk.Modules.Ticketing.Domain;
using Helpdesk.Modules.Ticketing.Fields;
using Helpdesk.Modules.Ticketing.Persistence;
using Helpdesk.Modules.Ticketing.Sla;
using Helpdesk.SharedKernel.Authorization;
using Helpdesk.SharedKernel.Database;
using Helpdesk.SharedKernel.Errors;
using Helpdesk.SharedKernel.Security;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Modules.Ticketing.Services;

internal static class MacroActionTypes
{
    public const string SetStatus = "setStatus";
    public const string SetPriority = "setPriority";
    public const string SetTeam = "setTeam";
    public const string SetAssignee = "setAssignee";
    public const string ClearAssignee = "clearAssignee";
    public const string AssignToMe = "assignToMe";
    public const string SetType = "setType";
    public const string SetField = "setField";
    public const string InternalNote = "internalNote";
    public const string PublicReply = "publicReply";

    public static readonly string[] All =
        [SetStatus, SetPriority, SetTeam, SetAssignee, ClearAssignee, AssignToMe, SetType, SetField, InternalNote, PublicReply];
}

/// <summary>
/// One structured step of a macro (never code). <c>Value</c>: enum name, id (team, user, type, canned response). <c>Text</c>: note or reply text.
/// <c>Key</c> + <c>Data</c>: a ticket field key and its typed value.
/// </summary>
internal sealed record MacroAction(string? Type, string? Value, string? Text, string? Key = null, JsonElement? Data = null);

internal sealed record MacroRequest(string? Name, string? Description, bool? IsActive, IReadOnlyList<MacroAction>? Actions, string? Version);

internal sealed record MacroDto(
    Guid Id, Guid DepartmentId, string Name, string? Description, bool IsActive, int Order, IReadOnlyList<MacroAction> Actions,
    IReadOnlyList<string> Summary, DateTimeOffset UpdatedAt, string Version);

/// <summary>An active macro the caller may run on this ticket, with the plain-language list of what it will do (the preview).</summary>
internal sealed record MacroPickDto(Guid Id, string Name, string? Description, IReadOnlyList<string> Summary);

internal sealed record RunMacroRequest(string? Version);

/// <summary>Text for the Public Reply composer. Nothing is sent: the agent edits it and presses Send.</summary>
internal sealed record MacroDraftDto(string Body, Guid? CannedResponseId, string? Title);

internal sealed record MacroRunResultDto(TicketDetailDto Ticket, MacroDraftDto? Draft, bool NoteAdded);

internal static class MacroCodes
{
    public const string CannedUnavailable = "canned_unavailable";
}

/// <summary>Resolves names for the plain-language summary of macro actions (one lookup per kind per call, never per macro).</summary>
internal sealed class MacroSummariser(TicketingDbContext db, IDepartmentScope scope, IOrganisationDirectory organisation, IUserDirectory users)
{
    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<string>>> SummariseAsync(Guid departmentId, IReadOnlyList<(Guid MacroId, IReadOnlyList<MacroAction> Actions)> macros, CancellationToken ct)
    {
        await scope.EnsureLoadedAsync(ct); // canned responses are read through the caller's department scope
        var all = macros.SelectMany(m => m.Actions).ToList();
        Guid[] Ids(string type) => [.. all.Where(a => a.Type == type && Guid.TryParse(a.Value, out _)).Select(a => Guid.Parse(a.Value!)).Distinct()];
        var teams = await organisation.GetTeamsAsync(Ids(MacroActionTypes.SetTeam), ct);
        var people = await users.GetManyAsync(Ids(MacroActionTypes.SetAssignee), ct);
        var typeIds = Ids(MacroActionTypes.SetType);
        var typeNames = typeIds.Length == 0 ? [] : await db.TicketTypes.AsNoTracking().Where(t => t.DepartmentId == departmentId && typeIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.Name, ct);
        var cannedIds = Ids(MacroActionTypes.PublicReply);
        var canned = cannedIds.Length == 0 ? [] : await db.CannedResponses.AsNoTracking().Where(c => c.DepartmentId == departmentId && cannedIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Title, ct);
        var keys = all.Where(a => a.Type == MacroActionTypes.SetField && a.Key is not null).Select(a => a.Key!).Distinct().ToArray();
        var labels = keys.Length == 0 ? [] : await db.TicketFields.AsNoTracking().Where(f => f.DepartmentId == departmentId && keys.Contains(f.Key)).ToDictionaryAsync(f => f.Key, f => f.Label, ct);

        string Name<T>(IReadOnlyDictionary<Guid, T> map, string? value, Func<T, string> pick) =>
            Guid.TryParse(value, out var id) && map.TryGetValue(id, out var item) ? pick(item) : "(unavailable)";

        var result = new Dictionary<Guid, IReadOnlyList<string>>();
        foreach (var (macroId, actions) in macros)
        {
            result[macroId] =
            [
                .. actions.Select(a => a.Type switch
                {
                    MacroActionTypes.SetStatus => $"Status → {a.Value}",
                    MacroActionTypes.SetPriority => $"Priority → {a.Value}",
                    MacroActionTypes.SetTeam => $"Team → {Name(teams, a.Value, t => t.Name)}",
                    MacroActionTypes.SetAssignee => $"Assignee → {Name(people, a.Value, p => p.DisplayName)}",
                    MacroActionTypes.ClearAssignee => "Clear assignee",
                    MacroActionTypes.AssignToMe => "Assign to me",
                    MacroActionTypes.SetType => $"Type → {Name(typeNames, a.Value, n => n)}",
                    MacroActionTypes.SetField => $"{(a.Key is not null && labels.TryGetValue(a.Key, out var label) ? label : "(unavailable field)")} → {(a.Data is { } d ? Show(d) : "")}",
                    MacroActionTypes.InternalNote => "Add an internal note (not visible to the requester)",
                    MacroActionTypes.PublicReply => a.Value is not null
                        ? $"Insert \"{Name(canned, a.Value, n => n)}\" into the Public Reply (draft, not sent)"
                        : "Insert text into the Public Reply (draft, not sent)",
                    _ => a.Type ?? "",
                }),
            ];
        }

        return result;
    }

    private static string Show(JsonElement d) => d.ValueKind switch
    {
        JsonValueKind.String => d.GetString() ?? "",
        JsonValueKind.Array => string.Join(", ", d.EnumerateArray().Select(Show)),
        _ => d.ToString(),
    };
}

/// <summary>
/// Macro administration (department.canned.manage, enforced by the endpoint filter; every query filters by the department itself). A macro is a stored,
/// ordered bundle of structured actions that an agent runs by hand. It is not an automation rule: nothing here reacts to events. Every reference (team,
/// person, type, field, canned response) must belong to the macro's department, and foreign, missing and unusable ids are refused alike.
/// </summary>
internal sealed class MacroAdminService(
    TicketingDbContext db,
    IDepartmentScope scope,
    TicketingAudit audit,
    RuleReferenceChecker references,
    TicketTypesService types,
    MacroSummariser summariser,
    TimeProvider clock)
{
    public const int MaxMacros = 100;
    public const int MaxActions = 15;
    private const string Object = "macro";

    public async Task<IReadOnlyList<MacroDto>> ListAsync(Guid departmentId, CancellationToken ct)
    {
        await scope.EnsureLoadedAsync(ct);
        var rows = await db.Macros.AsNoTracking().Where(m => m.DepartmentId == departmentId).OrderBy(m => m.SortOrder).ThenBy(m => m.Name).ThenBy(m => m.Id).ToListAsync(ct);
        return await ToDtosAsync(departmentId, rows, ct);
    }

    public async Task<MacroDto> CreateAsync(Guid departmentId, MacroRequest request, CancellationToken ct)
    {
        await scope.EnsureLoadedAsync(ct);
        var errors = new Dictionary<string, string[]>();
        var (name, description) = ValidateText(errors, request.Name, request.Description, creating: true);
        var actions = await ValidateActionsAsync(errors, departmentId, request.Actions, ct);
        TicketValidation.ThrowIfAny(errors);
        if (await db.Macros.CountAsync(m => m.DepartmentId == departmentId, ct) >= MaxMacros)
        {
            throw new ConflictException($"A department can keep at most {MaxMacros} macros.", ProblemCodes.InvalidState);
        }

        var now = clock.GetUtcNowMicroseconds();
        var last = await db.Macros.Where(m => m.DepartmentId == departmentId).MaxAsync(m => (int?)m.SortOrder, ct) ?? 0;
        var row = new Macro
        {
            Id = Guid.CreateVersion7(),
            DepartmentId = departmentId,
            Name = name!,
            Description = description,
            IsActive = request.IsActive ?? true,
            SortOrder = last + 10,
            Actions = StoredJson.Write(actions),
            CreatedAt = now,
            UpdatedAt = now,
        };
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.Macros.Add(row);
        await SaveAsync(ct);
        await audit.WriteAsync(TicketAuditActions.MacroCreated, Object, row.Id, departmentId, null, new { name = row.Name, actions = actions.Count }, ct);
        await tx.CommitAsync(ct);
        return (await ToDtosAsync(departmentId, [row], ct))[0];
    }

    public async Task<MacroDto> UpdateAsync(Guid departmentId, Guid id, MacroRequest request, CancellationToken ct)
    {
        await scope.EnsureLoadedAsync(ct);
        var errors = new Dictionary<string, string[]>();
        var version = TicketValidation.Version(errors, request.Version);
        TicketValidation.ThrowIfAny(errors);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var row = await db.Macros.SingleOrDefaultAsync(m => m.Id == id && m.DepartmentId == departmentId, ct) ?? throw new NotFoundException();
        if (row.Version != version)
        {
            throw new ConflictException("The macro was changed elsewhere. Reload and retry.", ProblemCodes.VersionConflict);
        }

        var (name, description) = ValidateText(errors, request.Name ?? row.Name, request.Description ?? row.Description, creating: false);
        List<MacroAction>? actions = null;
        if (request.Actions is not null)
        {
            actions = await ValidateActionsAsync(errors, departmentId, request.Actions, ct);
        }

        TicketValidation.ThrowIfAny(errors);
        db.Entry(row).Property(m => m.Version).OriginalValue = version;
        row.Name = name!;
        row.Description = description;
        row.IsActive = request.IsActive ?? row.IsActive;
        if (actions is not null)
        {
            row.Actions = StoredJson.Write(actions);
        }

        row.UpdatedAt = clock.GetUtcNowMicroseconds();
        await SaveAsync(ct);
        await audit.WriteAsync(TicketAuditActions.MacroUpdated, Object, row.Id, departmentId, null, new { name = row.Name, isActive = row.IsActive }, ct);
        await tx.CommitAsync(ct);
        return (await ToDtosAsync(departmentId, [row], ct))[0];
    }

    public async Task<IReadOnlyList<MacroDto>> ReorderAsync(Guid departmentId, ReorderRequest request, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var rows = await db.Macros.Where(m => m.DepartmentId == departmentId).ToListAsync(ct);
        var ids = request.Ids ?? [];
        if (ids.Count != rows.Count || ids.Distinct().Count() != ids.Count || !ids.All(i => rows.Any(r => r.Id == i)))
        {
            throw new RequestValidationException("ids", "List every macro of this department exactly once.");
        }

        var now = clock.GetUtcNowMicroseconds();
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
        await audit.WriteAsync(TicketAuditActions.MacroUpdated, Object, departmentId, departmentId, null, new { reordered = ids.Count }, ct);
        await tx.CommitAsync(ct);
        return await ListAsync(departmentId, ct);
    }

    private async Task<IReadOnlyList<MacroDto>> ToDtosAsync(Guid departmentId, List<Macro> rows, CancellationToken ct)
    {
        var parsed = rows.Select(r => (r.Id, Actions: (IReadOnlyList<MacroAction>)StoredJson.Read<List<MacroAction>>(r.Actions))).ToList();
        var summaries = await summariser.SummariseAsync(departmentId, parsed, ct);
        return [.. rows.Select((r, i) => new MacroDto(
            r.Id, r.DepartmentId, r.Name, r.Description, r.IsActive, r.SortOrder, parsed[i].Actions, summaries[r.Id], r.UpdatedAt, ConcurrencyVersion.ToToken(r.Version)))];
    }

    private static (string? Name, string? Description) ValidateText(Dictionary<string, string[]> errors, string? rawName, string? rawDescription, bool creating)
    {
        var name = rawName?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 100 || TicketValidation.HasNul(name))
        {
            errors["name"] = ["name is required and at most 100 characters."];
        }

        var description = string.IsNullOrWhiteSpace(rawDescription) ? null : rawDescription.Trim();
        if (description is { Length: > 300 } || TicketValidation.HasNul(description))
        {
            errors["description"] = ["description must be at most 300 characters."];
        }

        _ = creating;
        return (name, description);
    }

    /// <summary>Shape, uniqueness and every reference of the actions, normalised for storage. Foreign ids answer exactly like missing ones.</summary>
    private async Task<List<MacroAction>> ValidateActionsAsync(Dictionary<string, string[]> errors, Guid departmentId, IReadOnlyList<MacroAction>? input, CancellationToken ct)
    {
        var result = new List<MacroAction>();
        if (input is null || input.Count == 0 || input.Count > MaxActions)
        {
            errors["actions"] = [$"Provide between 1 and {MaxActions} actions."];
            return result;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < input.Count; i++)
        {
            var a = input[i];
            var field = $"actions[{i}]";
            var type = MacroActionTypes.All.FirstOrDefault(t => string.Equals(t, a.Type?.Trim(), StringComparison.OrdinalIgnoreCase));
            if (type is null)
            {
                errors[field] = ["Unknown action."];
                continue;
            }

            var group = type is MacroActionTypes.SetAssignee or MacroActionTypes.ClearAssignee or MacroActionTypes.AssignToMe ? "assignee" : type;
            if (type != MacroActionTypes.SetField && !seen.Add(group))
            {
                errors[field] = ["Each kind of action can be used once per macro."];
                continue;
            }

            var value = a.Value?.Trim();
            string? message = null;
            switch (type)
            {
                case MacroActionTypes.SetStatus:
                    message = Enum.TryParse<TicketStatus>(value, true, out var status) && Enum.IsDefined(status) ? null : "Choose a valid status.";
                    value = message is null ? status.ToString() : value;
                    break;
                case MacroActionTypes.SetPriority:
                    message = Enum.TryParse<TicketPriority>(value, true, out var priority) && Enum.IsDefined(priority) ? null : "Choose a valid priority.";
                    value = message is null ? priority.ToString() : value;
                    break;
                case MacroActionTypes.SetTeam:
                    message = await references.CheckTeamAsync(departmentId, value ?? "", requireActive: true, ct);
                    break;
                case MacroActionTypes.SetAssignee:
                    message = await references.CheckAssigneeAsync(departmentId, value ?? "", ct);
                    break;
                case MacroActionTypes.SetType:
                    message = Guid.TryParse(value, out var typeId) && await types.IsActiveAsync(departmentId, typeId, ct) ? null : "Choose an active ticket type of this department.";
                    break;
                case MacroActionTypes.SetField:
                    message = await CheckFieldAsync(departmentId, a, keys, ct);
                    break;
                case MacroActionTypes.InternalNote:
                    message = string.IsNullOrWhiteSpace(a.Text) || a.Text.Length > 2000 || TicketValidation.HasNul(a.Text) ? "The note is required and at most 2000 characters." : null;
                    break;
                case MacroActionTypes.PublicReply:
                    message = await CheckReplyAsync(departmentId, a, value, ct);
                    break;
            }

            if (message is not null)
            {
                errors[field] = [message];
                continue;
            }

            result.Add(type switch
            {
                MacroActionTypes.InternalNote => new MacroAction(type, null, a.Text!.Trim()),
                MacroActionTypes.PublicReply => value is { Length: > 0 } ? new MacroAction(type, value, null) : new MacroAction(type, null, a.Text!.Trim()),
                MacroActionTypes.SetField => new MacroAction(type, null, null, a.Key!.Trim(), a.Data!.Value.Clone()),
                MacroActionTypes.ClearAssignee or MacroActionTypes.AssignToMe => new MacroAction(type, null, null),
                _ => new MacroAction(type, value, null),
            });
        }

        return result;
    }

    private async Task<string?> CheckFieldAsync(Guid departmentId, MacroAction a, HashSet<string> keys, CancellationToken ct)
    {
        var key = a.Key?.Trim();
        if (string.IsNullOrEmpty(key) || a.Data is not { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } data)
        {
            return "Choose a field and give it a value.";
        }

        if (!keys.Add(key))
        {
            return "A field can be set once per macro.";
        }

        var definition = await db.TicketFields.AsNoTracking().SingleOrDefaultAsync(f => f.DepartmentId == departmentId && f.Key == key && f.IsActive && f.AgentVisible, ct);
        if (definition is null)
        {
            return "Choose an active field of this department.";
        }

        // Shape check of the value against the field itself; whether the field applies to a ticket's type is decided when the macro runs (final state).
        var fieldErrors = new Dictionary<string, string[]>();
        FieldValueValidator.Validate([new FieldBinding(definition, false, 0)], new Dictionary<string, JsonElement> { [key] = data }, ValidationMode.Patch, fieldErrors);
        return fieldErrors.Count > 0 ? "The value is not valid for this field." : null;
    }

    private async Task<string?> CheckReplyAsync(Guid departmentId, MacroAction a, string? value, CancellationToken ct)
    {
        var hasText = !string.IsNullOrWhiteSpace(a.Text);
        if (hasText == !string.IsNullOrEmpty(value))
        {
            return "Give either the reply text or a canned response.";
        }

        if (hasText)
        {
            return a.Text!.Length > 5000 || TicketValidation.HasNul(a.Text) ? "The reply text is at most 5000 characters." : null;
        }

        return Guid.TryParse(value, out var id) && await db.CannedResponses.AsNoTracking().AnyAsync(c => c.Id == id && c.Scope == CannedScopes.Department && c.DepartmentId == departmentId && c.IsActive, ct)
            ? null
            : "Choose an active canned response of this department.";
    }

    private async Task SaveAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictException("The macro was changed elsewhere. Reload and retry.", ProblemCodes.VersionConflict);
        }
        catch (DbUpdateException ex) when (PostgresErrors.IsUniqueViolation(ex, out _))
        {
            throw new ConflictException("A macro with this name already exists.", ProblemCodes.Duplicate);
        }
    }
}

/// <summary>
/// Running macros on a ticket. The caller needs exactly the permissions the equivalent manual actions need (tickets.update for status, priority, type and fields;
/// the assignment rules for team and assignee; tickets.note for a note; tickets.reply for a reply draft); nothing is added. Everything is resolved and validated
/// against the ticket's FINAL intended state before anything is written, then applied in one transaction through the same audit, SLA and automation hooks as manual
/// changes. A Public Reply action only returns text for the composer: this service never queues or sends mail.
/// </summary>
internal sealed class MacroRunService(
    TicketingDbContext db,
    TicketResolver resolver,
    TicketService tickets,
    TicketPropertiesService properties,
    TicketTypesService types,
    TicketProjection projection,
    TicketLifecycle lifecycle,
    MacroSummariser summariser,
    TicketingAudit audit,
    IAuthorizer authorizer,
    ICurrentUser current,
    ITicketEvents events,
    TimeProvider clock)
{
    public const int PickLimit = 50;

    /// <summary>Active macros of the ticket's department that the caller is allowed to run, optionally filtered by name or description.</summary>
    public async Task<IReadOnlyList<MacroPickDto>> PickAsync(string route, string? q, CancellationToken ct)
    {
        var search = q?.Trim() ?? "";
        if (search.Length > 100 || TicketValidation.HasNul(search))
        {
            throw new RequestValidationException("q", "q must be at most 100 characters.");
        }

        var key = await resolver.FindAsync(route, ct) ?? throw new NotFoundException();
        var query = db.Macros.AsNoTracking().Where(m => m.DepartmentId == key.DepartmentId && m.IsActive);
        if (search.Length > 0)
        {
            var like = "%" + TicketValidation.EscapeLike(search) + "%";
            query = query.Where(m => EF.Functions.ILike(m.Name, like, "\\") || (m.Description != null && EF.Functions.ILike(m.Description, like, "\\")));
        }

        var rows = await query.OrderBy(m => m.SortOrder).ThenBy(m => m.Name).Take(PickLimit).ToListAsync(ct);
        var allowed = await AllowedAsync(key.DepartmentId, ct);
        var parsed = rows.Select(r => (r.Id, Actions: (IReadOnlyList<MacroAction>)StoredJson.Read<List<MacroAction>>(r.Actions))).Where(p => Needs(p.Actions).All(allowed.Contains)).ToList();
        var summaries = await summariser.SummariseAsync(key.DepartmentId, parsed, ct);
        return [.. parsed.Select(p => { var r = rows.Single(x => x.Id == p.Id); return new MacroPickDto(r.Id, r.Name, r.Description, summaries[r.Id]); })];
    }

    private async Task<HashSet<string>> AllowedAsync(Guid departmentId, CancellationToken ct)
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var permission in new[] { PermissionCodes.TicketsUpdate, PermissionCodes.TicketsNote, PermissionCodes.TicketsReply, PermissionCodes.TicketsAssignAny, PermissionCodes.TicketsAssignSelf })
        {
            if (await authorizer.CheckAsync(permission, departmentId, ct) == AccessOutcome.Allowed)
            {
                allowed.Add(permission);
            }
        }

        // Assignment: assign.any, or assign.self which suffices for "assign to me" only.
        if (allowed.Contains(PermissionCodes.TicketsAssignAny))
        {
            allowed.Add("assign");
            allowed.Add("assign.self");
        }

        if (allowed.Contains(PermissionCodes.TicketsAssignSelf))
        {
            allowed.Add("assign.self");
        }

        return allowed;
    }

    private static IEnumerable<string> Needs(IReadOnlyList<MacroAction> actions)
    {
        foreach (var a in actions)
        {
            switch (a.Type)
            {
                case MacroActionTypes.SetStatus or MacroActionTypes.SetPriority or MacroActionTypes.SetType or MacroActionTypes.SetField:
                    yield return PermissionCodes.TicketsUpdate;
                    break;
                case MacroActionTypes.SetTeam or MacroActionTypes.SetAssignee or MacroActionTypes.ClearAssignee:
                    yield return "assign";
                    break;
                case MacroActionTypes.AssignToMe:
                    yield return "assign.self";
                    break;
                case MacroActionTypes.InternalNote:
                    yield return PermissionCodes.TicketsNote;
                    break;
                case MacroActionTypes.PublicReply:
                    yield return PermissionCodes.TicketsReply;
                    break;
            }
        }
    }

    public async Task<MacroRunResultDto> RunAsync(string route, Guid macroId, RunMacroRequest request, CancellationToken ct)
    {
        var early = new Dictionary<string, string[]>();
        var version = TicketValidation.Version(early, request.Version);
        TicketValidation.ThrowIfAny(early);

        var key = await resolver.FindAsync(route, ct) ?? throw new NotFoundException();
        var macro = await db.Macros.AsNoTracking().SingleOrDefaultAsync(m => m.Id == macroId && m.DepartmentId == key.DepartmentId, ct) ?? throw new NotFoundException();
        if (!macro.IsActive)
        {
            throw new ConflictException("The macro is inactive.", ProblemCodes.Inactive);
        }

        var actions = StoredJson.Read<List<MacroAction>>(macro.Actions);

        // Permissions first, for every action, before anything is read further or written: a macro never does more than the caller could do by hand.
        var allowed = await AllowedAsync(key.DepartmentId, ct);
        if (!Needs(actions).All(allowed.Contains))
        {
            throw new ForbiddenException();
        }

        MacroDraftDto? draft = null;
        if (actions.SingleOrDefault(a => a.Type == MacroActionTypes.PublicReply) is { } reply)
        {
            if (reply.Value is null)
            {
                draft = new MacroDraftDto(reply.Text!, null, null);
            }
            else
            {
                // Resolved now, not when the macro was saved. Unavailable means the macro cannot run: nothing is substituted.
                var canned = Guid.TryParse(reply.Value, out var cannedId)
                    ? await db.CannedResponses.AsNoTracking().SingleOrDefaultAsync(c => c.Id == cannedId && c.Scope == CannedScopes.Department && c.DepartmentId == key.DepartmentId && c.IsActive, ct)
                    : null;
                draft = canned is null
                    ? throw new ConflictException("The canned response of this macro is no longer available.", MacroCodes.CannedUnavailable)
                    : new MacroDraftDto(canned.Body, canned.Id, canned.Title);
            }
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var ticket = await tickets.LockAndLoadAsync(key.Id, version, ct); // merged: 409 ticket_merged; stale version: version_conflict
        var now = clock.GetUtcNowMicroseconds();

        // ----- validate the final intended state (no writes yet) -----
        TicketStatus? status = null;
        TicketPriority? priority = null;
        Guid? typeId = null;
        var typeSet = false;
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        Guid? team = ticket.TeamId;
        Guid? assignee = ticket.AssigneeUserId;
        string? noteText = null;
        foreach (var a in actions)
        {
            switch (a.Type)
            {
                case MacroActionTypes.SetStatus:
                    status = Enum.Parse<TicketStatus>(a.Value!);
                    break;
                case MacroActionTypes.SetPriority:
                    priority = Enum.Parse<TicketPriority>(a.Value!);
                    break;
                case MacroActionTypes.SetTeam:
                    team = Guid.Parse(a.Value!);
                    break;
                case MacroActionTypes.SetAssignee:
                    assignee = Guid.Parse(a.Value!);
                    break;
                case MacroActionTypes.ClearAssignee:
                    assignee = null;
                    break;
                case MacroActionTypes.AssignToMe:
                    assignee = current.UserId;
                    break;
                case MacroActionTypes.SetType:
                    typeId = await types.RequireActiveAsync(ticket.DepartmentId, Guid.Parse(a.Value!), "actions", ct);
                    typeSet = true;
                    break;
                case MacroActionTypes.SetField:
                    fields[a.Key!] = a.Data!.Value;
                    break;
                case MacroActionTypes.InternalNote:
                    noteText = a.Text;
                    break;
            }
        }

        var finalType = typeSet ? typeId : ticket.TicketTypeId;
        ValidationResult? fieldResult = null;
        if (fields.Count > 0)
        {
            var bindings = await properties.EditableBindingsAsync(ticket, finalType, ct);
            var fieldErrors = new Dictionary<string, string[]>();
            fieldResult = FieldValueValidator.Validate(bindings, fields, ValidationMode.Patch, fieldErrors);
            TicketValidation.ThrowIfAny(fieldErrors);
        }

        var teamChanged = team != ticket.TeamId;
        var assigneeChanged = assignee != ticket.AssigneeUserId;
        if (teamChanged || assigneeChanged)
        {
            await tickets.RequireAssignmentRightsAsync(ticket.DepartmentId, team, assignee, ticket, ct);
            if (teamChanged)
            {
                await tickets.ValidateTeamAsync(ticket.DepartmentId, team, ct);
            }

            if (assigneeChanged)
            {
                await tickets.ValidateAssigneeAsync(ticket.DepartmentId, assignee, ct);
            }
        }

        // ----- apply -----
        var via = new { macroId = macro.Id };
        var lc = new List<TicketChange>();
        var kinds = new HashSet<string>(StringComparer.Ordinal);
        var stateChanged = false;
        if (priority is { } p && p != ticket.Priority)
        {
            await audit.WriteAsync(TicketAuditActions.PriorityChanged, TicketingAudit.TicketObject, ticket.Id, ticket.DepartmentId, new { priority = ticket.Priority.ToString() }, new { priority = p.ToString(), via }, ct);
            lc.Add(new TicketChange(ChangeKind.Priority, ticket.Priority.ToString(), p.ToString()));
            ticket.Priority = p;
            kinds.Add(TicketEventKinds.Priority);
            stateChanged = true;
        }

        if (status is { } s && s != ticket.Status)
        {
            var previous = ticket.Status;
            ticket.ApplyStatus(s, now);
            await audit.WriteAsync(TicketAuditActions.StatusChanged, TicketingAudit.TicketObject, ticket.Id, ticket.DepartmentId, new { status = previous.ToString() }, new { status = s.ToString(), via }, ct);
            lc.Add(new TicketChange(ChangeKind.Status, previous.ToString(), s.ToString()));
            kinds.Add(TicketEventKinds.Status);
            stateChanged = true;
        }

        if (teamChanged)
        {
            await audit.WriteAsync(TicketAuditActions.TeamChanged, TicketingAudit.TicketObject, ticket.Id, ticket.DepartmentId, new { teamId = ticket.TeamId }, new { teamId = team, via }, ct);
            lc.Add(new TicketChange(ChangeKind.Team, ticket.TeamId?.ToString(), team?.ToString()));
            ticket.TeamId = team;
            kinds.Add(TicketEventKinds.Assignment);
            stateChanged = true;
        }

        if (assigneeChanged)
        {
            await audit.WriteAsync(TicketAuditActions.AssigneeChanged, TicketingAudit.TicketObject, ticket.Id, ticket.DepartmentId, new { assigneeUserId = ticket.AssigneeUserId }, new { assigneeUserId = assignee, via }, ct);
            lc.Add(new TicketChange(ChangeKind.Assignee, ticket.AssigneeUserId?.ToString(), assignee?.ToString()));
            ticket.AssigneeUserId = assignee;
            kinds.Add(TicketEventKinds.Assignment);
            stateChanged = true;
        }

        if (typeSet && typeId != ticket.TicketTypeId)
        {
            var names = await db.TicketTypes.AsNoTracking().Where(t => t.DepartmentId == ticket.DepartmentId && (t.Id == ticket.TicketTypeId || t.Id == typeId)).ToDictionaryAsync(t => t.Id, t => t.Name, ct);
            await audit.WriteAsync(TicketAuditActions.TypeChanged, TicketingAudit.TicketObject, ticket.Id, ticket.DepartmentId,
                new { typeId = ticket.TicketTypeId, name = ticket.TicketTypeId is { } o && names.TryGetValue(o, out var on) ? on : null },
                new { typeId, name = typeId is { } n && names.TryGetValue(n, out var nn) ? nn : null, via }, ct);
            ticket.TicketTypeId = typeId;
            kinds.Add(TicketEventKinds.Updated);
            stateChanged = true;
        }

        if (fieldResult is not null)
        {
            var changes = await FieldValueStore.ApplyAsync(db, ticket, fieldResult, ticket.RequestFormId, now, ct);
            if (changes.Count > 0)
            {
                await FieldValueStore.AuditAsync(audit, ticket, changes, ct);
                kinds.Add(TicketEventKinds.Updated);
                stateChanged = true;
            }
        }

        Message? note = null;
        if (noteText is not null)
        {
            // An internal note alone never moves the ticket's visible timestamps (same rule as a manual note).
            note = new Message
            {
                Id = Guid.CreateVersion7(),
                TicketId = ticket.Id,
                DepartmentId = ticket.DepartmentId,
                Kind = MessageKind.Internal,
                AuthorType = AuthorType.Agent,
                AuthorUserId = current.UserId,
                Body = noteText,
                Source = MessageSources.Web,
                DeliveryState = DeliveryState.None,
                CreatedAt = now,
            };
            db.Messages.Add(note);
        }

        if (stateChanged)
        {
            ticket.UpdatedAt = now;
        }

        await tickets.SaveAsync(ct);
        if (note is not null)
        {
            await audit.WriteAsync(TicketAuditActions.MessageAdded, TicketingAudit.TicketObject, ticket.Id, ticket.DepartmentId, null,
                new { messageId = note.Id, kind = note.Kind.ToString(), authorType = note.AuthorType.ToString(), via }, ct);
        }

        // One record of the run (name as it was then). It lists neither action kinds nor text, so it reveals nothing about a note or reply.
        await audit.WriteAsync(TicketAuditActions.MacroRun, TicketingAudit.TicketObject, ticket.Id, ticket.DepartmentId, null, new { macroId = macro.Id, name = macro.Name }, ct);
        if (lc.Count > 0)
        {
            await lifecycle.BeforeCommitAsync(ticket, ticket.Requester.EmailNormalised, lc, ct);
        }

        await tx.CommitAsync(ct);

        foreach (var kind in kinds)
        {
            await events.PublishAsync(ticket.DepartmentId, ticket.Id, ticket.Reference, kind, current.UserId, ct);
        }

        if (note is not null)
        {
            await events.PublishAsync(ticket.DepartmentId, ticket.Id, ticket.Reference, TicketEventKinds.Note, current.UserId, ct);
        }

        await lifecycle.AfterCommitAsync(ticket, lc, Provenances.User, ct);
        return new MacroRunResultDto(await projection.DetailAsync(ticket, ticket.Requester, ct), draft, note is not null);
    }
}
