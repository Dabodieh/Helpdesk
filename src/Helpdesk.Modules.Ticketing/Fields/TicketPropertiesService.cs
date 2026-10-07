using System.Text.Json;
using Helpdesk.Modules.Ticketing.Domain;
using Helpdesk.Modules.Ticketing.Persistence;
using Helpdesk.Modules.Ticketing.Services;
using Helpdesk.SharedKernel.Database;
using Helpdesk.SharedKernel.Errors;
using Helpdesk.SharedKernel.Security;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Modules.Ticketing.Fields;

internal sealed record TypeOptionDto(Guid Id, string Name, bool IsActive, bool IsDefault);

internal sealed record PropertyOptionDto(string Value, string Label, bool IsActive);

/// <summary>
/// One field of a ticket as an agent sees it. <c>Applicable</c>: the ticket's type or the department asks it. <c>Editable</c>: applicable (or already holding a value),
/// active and agent visible. A value whose field was later deactivated or is no longer applicable stays readable but not editable.
/// </summary>
internal sealed record TicketPropertyDto(
    Guid? FieldId, string Key, string Label, string? HelpText, string Type, bool Required, IReadOnlyList<PropertyOptionDto> Options, JsonElement? Value, string? Display,
    bool Applicable, bool Editable, bool Active, bool MissingRequired);

internal sealed record TicketPropertiesDto(RefDto? Type, IReadOnlyList<TypeOptionDto> Types, IReadOnlyList<TicketPropertyDto> Fields, string Version);

internal sealed record UpdatePropertiesRequest(string? Version, Guid? TicketTypeId, bool? ClearTicketType, Dictionary<string, JsonElement>? Fields);

/// <summary>
/// Agent-side ticket details: the ticket's type and its configured field values. Authorization is the endpoint's resource filter (tickets.read to view, tickets.update to
/// change) against the ticket's persisted department. Only these two things change here, never status, priority, team, assignee or requester.
///
/// Required semantics: "required" is enforced when a ticket is created through a channel that collects fields and when an agent touches a field here (a required field cannot
/// be cleared). Changing a ticket's type or making a field required never invalidates existing tickets; missing required values are only reported (<c>MissingRequired</c>).
/// </summary>
internal sealed class TicketPropertiesService(
    TicketingDbContext db, TicketResolver resolver, FieldCatalog catalog, TicketTypesService types, TicketingAudit audit, ITicketEvents events, ICurrentUser current, TimeProvider clock)
{
    public async Task<TicketPropertiesDto> GetAsync(string route, CancellationToken ct)
    {
        var key = await resolver.FindAsync(route, ct) ?? throw new NotFoundException();
        var ticket = await db.Tickets.AsNoTracking().SingleOrDefaultAsync(t => t.Id == key.Id, ct) ?? throw new NotFoundException();
        return await BuildAsync(ticket, ct);
    }

    public async Task<TicketPropertiesDto> UpdateAsync(string route, UpdatePropertiesRequest request, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        var version = TicketValidation.Version(errors, request.Version);
        if (request.TicketTypeId is null && request.ClearTicketType != true && (request.Fields is null || request.Fields.Count == 0))
        {
            errors["body"] = ["Provide a ticket type or fields."];
        }

        TicketValidation.ThrowIfAny(errors);

        var key = await resolver.FindAsync(route, ct) ?? throw new NotFoundException();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM ticketing.tickets WHERE id = {key.Id} FOR UPDATE", ct);
        var ticket = await db.Tickets.SingleOrDefaultAsync(t => t.Id == key.Id, ct) ?? throw new NotFoundException();
        if (ticket.IsMerged)
        {
            throw new ConflictException("The ticket was merged into another ticket and is read-only.", Services.TicketMergeCodes.Merged);
        }

        if (ticket.Version != version)
        {
            throw new ConflictException("The ticket was changed elsewhere. Reload and retry.", ProblemCodes.VersionConflict);
        }

        db.Entry(ticket).Property(t => t.Version).OriginalValue = version;
        var now = Services.ClockExtensions.GetUtcNowMicroseconds(clock);
        var changed = false;

        var newType = request.ClearTicketType == true ? null : request.TicketTypeId ?? ticket.TicketTypeId;
        if (request.ClearTicketType == true && request.TicketTypeId is not null)
        {
            throw new RequestValidationException("ticketTypeId", "Choose a type or clear it, not both.");
        }

        if (newType != ticket.TicketTypeId)
        {
            // A new choice must be an active type of THIS ticket's department (foreign, missing and inactive answer alike). Clearing is allowed.
            if (newType is { } chosen)
            {
                await types.RequireActiveAsync(ticket.DepartmentId, chosen, "ticketTypeId", ct);
            }

            var names = await db.TicketTypes.AsNoTracking().Where(t => t.DepartmentId == ticket.DepartmentId && (t.Id == ticket.TicketTypeId || t.Id == newType))
                .ToDictionaryAsync(t => t.Id, t => t.Name, ct);
            await audit.WriteAsync(TicketAuditActions.TypeChanged, TicketingAudit.TicketObject, ticket.Id, ticket.DepartmentId,
                new { typeId = ticket.TicketTypeId, name = ticket.TicketTypeId is { } p && names.TryGetValue(p, out var pn) ? pn : null },
                new { typeId = newType, name = newType is { } n && names.TryGetValue(n, out var nn) ? nn : null }, ct);
            ticket.TicketTypeId = newType;
            changed = true;
        }

        if (request.Fields is { Count: > 0 } submitted)
        {
            var allowed = await EditableBindingsAsync(ticket, ticket.TicketTypeId, ct);
            var fieldErrors = new Dictionary<string, string[]>();
            var result = FieldValueValidator.Validate(allowed, submitted, ValidationMode.Patch, fieldErrors);
            TicketValidation.ThrowIfAny(fieldErrors);
            var changes = await FieldValueStore.ApplyAsync(db, ticket, result, ticket.RequestFormId, now, ct);
            if (changes.Count > 0)
            {
                changed = true;
                await FieldValueStore.AuditAsync(audit, ticket, changes, ct);
            }
        }

        if (changed)
        {
            ticket.UpdatedAt = now;
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                throw new ConflictException("The ticket was changed elsewhere. Reload and retry.", ProblemCodes.VersionConflict);
            }
        }

        await tx.CommitAsync(ct);
        if (changed)
        {
            await events.PublishAsync(ticket.DepartmentId, ticket.Id, ticket.Reference, TicketEventKinds.Updated, current.UserId, ct);
        }

        return await BuildAsync(ticket, ct);
    }

    /// <summary>Applicable fields the agent may see, plus fields that already hold a value (active ones stay editable).</summary>
    internal async Task<List<FieldBinding>> EditableBindingsAsync(Ticket ticket, Guid? typeId, CancellationToken ct)
    {
        var bindings = (await catalog.ForTypeAsync(ticket.DepartmentId, typeId, ct)).Where(b => b.Field.AgentVisible).ToList();
        var heldIds = await db.TicketFieldValues.IgnoreDepartmentScope().AsNoTracking()
            .Where(v => v.TicketId == ticket.Id && v.DepartmentId == ticket.DepartmentId && v.FieldId != null).Select(v => v.FieldId!.Value).ToListAsync(ct);
        if (heldIds.Count > 0)
        {
            var held = await db.TicketFields.AsNoTracking().Where(f => f.DepartmentId == ticket.DepartmentId && f.IsActive && f.AgentVisible && heldIds.Contains(f.Id)).ToListAsync(ct);
            bindings.AddRange(held.Where(f => bindings.All(b => b.Field.Id != f.Id)).Select(f => new FieldBinding(f, f.Required, 2_000_000 + f.SortOrder)));
        }

        return bindings;
    }

    private async Task<TicketPropertiesDto> BuildAsync(Ticket ticket, CancellationToken ct)
    {
        var typeRows = await db.TicketTypes.AsNoTracking().Where(t => t.DepartmentId == ticket.DepartmentId && (t.IsActive || t.Id == ticket.TicketTypeId)).OrderBy(t => t.SortOrder).ThenBy(t => t.Name).ToListAsync(ct);
        var current = typeRows.SingleOrDefault(t => t.Id == ticket.TicketTypeId);
        var applicable = (await catalog.ForTypeAsync(ticket.DepartmentId, ticket.TicketTypeId, ct)).Where(b => b.Field.AgentVisible).ToList();
        var values = await db.TicketFieldValues.AsNoTracking().IgnoreDepartmentScope()
            .Where(v => v.TicketId == ticket.Id && v.DepartmentId == ticket.DepartmentId && v.AgentVisible).OrderBy(v => v.SortOrder).ToListAsync(ct);
        var heldIds = values.Where(v => v.FieldId != null).Select(v => v.FieldId!.Value).ToArray();
        var heldFields = heldIds.Length == 0 ? [] : await db.TicketFields.AsNoTracking().Where(f => f.DepartmentId == ticket.DepartmentId && heldIds.Contains(f.Id)).ToDictionaryAsync(f => f.Id, ct);

        var rows = new List<TicketPropertyDto>();
        foreach (var b in applicable)
        {
            var v = values.SingleOrDefault(x => x.Key == b.Field.Key);
            rows.Add(Row(b.Field, b.Required, v, applicable: true, editable: true));
        }

        foreach (var v in values.Where(v => applicable.All(b => b.Field.Key != v.Key)))
        {
            // Historical or no-longer-applicable value: shown with its snapshot, editable only while the field is still active.
            heldFields.TryGetValue(v.FieldId ?? Guid.Empty, out var def);
            rows.Add(def is null
                ? new TicketPropertyDto(null, v.Key, v.Label, null, v.FieldType, false, [], Parse(v.ValueJson), v.Display, false, false, false, false)
                : Row(def, false, v, applicable: false, editable: def.IsActive && def.AgentVisible));
        }

        return new TicketPropertiesDto(
            current is null ? null : new RefDto(current.Id, current.Name),
            [.. typeRows.Select(t => new TypeOptionDto(t.Id, t.Name, t.IsActive, t.IsDefault))],
            rows,
            ConcurrencyVersion.ToToken(ticket.Version));
    }

    private static TicketPropertyDto Row(TicketField f, bool required, TicketFieldValue? v, bool applicable, bool editable) =>
        new(f.Id, f.Key, v?.Label ?? f.Label, f.HelpText, f.FieldType, required, [.. FieldValueValidator.Options(f).Select(o => new PropertyOptionDto(o.Value, o.Label, o.IsActive))],
            v is null ? null : Parse(v.ValueJson), v?.Display, applicable, editable && f.IsActive, f.IsActive, applicable && required && v is null);

    private static JsonElement? Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
