using Helpdesk.Modules.Ticketing.Domain;
using Helpdesk.Modules.Ticketing.Persistence;
using Helpdesk.Modules.Ticketing.Services;
using Helpdesk.Modules.Ticketing.Sla;
using Helpdesk.SharedKernel.Database;
using Helpdesk.SharedKernel.Errors;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Helpdesk.Modules.Ticketing.Fields;

internal sealed record TypeFieldLinkDto(Guid FieldId, string Key, string Label, string Type, bool Required, bool? RequiredOverride, int Order, bool FieldIsActive);

internal sealed record TicketTypeAdminDto(
    Guid Id, Guid DepartmentId, string Name, string? Description, bool IsActive, bool IsDefault, int Order, IReadOnlyList<TypeFieldLinkDto> Fields, string Version);

internal sealed record TicketTypeRequest(string? Name, string? Description, bool? IsActive, bool? IsDefault, string? Version);

internal sealed record FieldLinkInput(Guid? FieldId, bool? Required);

internal sealed record FieldLinksRequest(IReadOnlyList<FieldLinkInput>? Fields);

/// <summary>Ticket type administration (permission department.forms.manage, enforced by the endpoint filter; every query also names the department) and type resolution for ticket creation.</summary>
internal sealed class TicketTypesService(TicketingDbContext db, TicketingAudit audit, TimeProvider clock)
{
    public const int MaxTypes = 50;
    public const int MaxFieldsPerType = 30;
    private const string TypeObject = "ticket_type";

    public async Task<IReadOnlyList<TicketTypeAdminDto>> ListAsync(Guid departmentId, CancellationToken ct)
    {
        var types = await db.TicketTypes.AsNoTracking().Where(t => t.DepartmentId == departmentId).OrderBy(t => t.SortOrder).ThenBy(t => t.Name).ToListAsync(ct);
        var links = await db.TicketTypeFields.AsNoTracking().Where(l => l.DepartmentId == departmentId)
            .Join(db.TicketFields.AsNoTracking(), l => l.FieldId, f => f.Id, (l, f) => new { Link = l, Field = f }).ToListAsync(ct);
        return [.. types.Select(t => ToDto(t, links.Where(x => x.Link.TypeId == t.Id).OrderBy(x => x.Link.SortOrder)
            .Select(x => new TypeFieldLinkDto(x.Field.Id, x.Field.Key, x.Field.Label, x.Field.FieldType, x.Link.Required ?? x.Field.Required, x.Link.Required, x.Link.SortOrder, x.Field.IsActive))))];
    }

    public async Task<TicketTypeAdminDto> CreateAsync(Guid departmentId, TicketTypeRequest request, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        var (name, description) = Validate(errors, request, creating: true);
        TicketValidation.ThrowIfAny(errors);
        if (request.IsActive == false && request.IsDefault == true)
        {
            throw new RequestValidationException("isDefault", "The default type must be active.");
        }

        if (await db.TicketTypes.CountAsync(t => t.DepartmentId == departmentId, ct) >= MaxTypes)
        {
            throw new ConflictException($"A department can keep at most {MaxTypes} ticket types.", ProblemCodes.InvalidState);
        }

        var now = ClockExtensions.GetUtcNowMicroseconds(clock);
        var last = await db.TicketTypes.Where(t => t.DepartmentId == departmentId).MaxAsync(t => (int?)t.SortOrder, ct) ?? 0;
        var type = new TicketType
        {
            Id = Guid.CreateVersion7(),
            DepartmentId = departmentId,
            Name = name!,
            Description = description,
            IsActive = request.IsActive ?? true,
            SortOrder = last + 10,
            CreatedAt = now,
            UpdatedAt = now,
        };
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.TicketTypes.Add(type);
        await SaveAsync(ct);
        if (request.IsDefault == true)
        {
            await MakeDefaultAsync(departmentId, type, ct);
        }

        await audit.WriteAsync(TicketAuditActions.TicketTypeCreated, TypeObject, type.Id, departmentId, null, new { name = type.Name, isActive = type.IsActive, isDefault = type.IsDefault }, ct);
        await tx.CommitAsync(ct);
        return (await ListAsync(departmentId, ct)).Single(t => t.Id == type.Id);
    }

    public async Task<TicketTypeAdminDto> UpdateAsync(Guid departmentId, Guid typeId, TicketTypeRequest request, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        var version = TicketValidation.Version(errors, request.Version);
        var (name, description) = Validate(errors, request, creating: false);
        TicketValidation.ThrowIfAny(errors);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var type = await db.TicketTypes.SingleOrDefaultAsync(t => t.Id == typeId && t.DepartmentId == departmentId, ct) ?? throw new NotFoundException();
        if (type.Version != version)
        {
            throw new ConflictException("The ticket type was changed elsewhere. Reload and retry.", ProblemCodes.VersionConflict);
        }

        db.Entry(type).Property(t => t.Version).OriginalValue = version;
        var before = new { name = type.Name, isActive = type.IsActive, isDefault = type.IsDefault };
        type.Name = name ?? type.Name;
        if (request.Description is not null)
        {
            type.Description = description;
        }

        type.IsActive = request.IsActive ?? type.IsActive;
        if (!type.IsActive)
        {
            type.IsDefault = false; // an inactive type can never be the default
        }

        if (request.IsDefault == true && !type.IsActive)
        {
            throw new RequestValidationException("isDefault", "The default type must be active.");
        }

        type.UpdatedAt = ClockExtensions.GetUtcNowMicroseconds(clock);
        await SaveAsync(ct);
        if (request.IsDefault == true && !type.IsDefault)
        {
            await MakeDefaultAsync(departmentId, type, ct);
        }
        else if (request.IsDefault == false && type.IsDefault)
        {
            type.IsDefault = false;
            await SaveAsync(ct);
        }

        await audit.WriteAsync(TicketAuditActions.TicketTypeUpdated, TypeObject, type.Id, departmentId, before, new { name = type.Name, isActive = type.IsActive, isDefault = type.IsDefault }, ct);
        await tx.CommitAsync(ct);
        return (await ListAsync(departmentId, ct)).Single(t => t.Id == type.Id);
    }

    public async Task<IReadOnlyList<TicketTypeAdminDto>> ReorderAsync(Guid departmentId, ReorderRequest request, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var rows = await db.TicketTypes.Where(t => t.DepartmentId == departmentId).ToListAsync(ct);
        var ids = request.Ids ?? [];
        if (ids.Count != rows.Count || ids.Distinct().Count() != ids.Count || !ids.All(i => rows.Any(r => r.Id == i)))
        {
            throw new RequestValidationException(new Dictionary<string, string[]> { ["ids"] = ["List every ticket type of the department exactly once."] });
        }

        for (var i = 0; i < ids.Count; i++)
        {
            var row = rows.Single(r => r.Id == ids[i]);
            if (row.SortOrder != (i + 1) * 10)
            {
                row.SortOrder = (i + 1) * 10;
                row.UpdatedAt = ClockExtensions.GetUtcNowMicroseconds(clock);
            }
        }

        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(TicketAuditActions.TicketTypeUpdated, TypeObject, departmentId, departmentId, null, new { reordered = ids.Count }, ct);
        await tx.CommitAsync(ct);
        return await ListAsync(departmentId, ct);
    }

    /// <summary>Replaces the type's field list (order = list order). A new link must point to an active field of THIS department; existing links to a since-deactivated field may stay.</summary>
    public async Task<TicketTypeAdminDto> SetFieldsAsync(Guid departmentId, Guid typeId, FieldLinksRequest request, CancellationToken ct)
    {
        var input = request.Fields ?? throw new RequestValidationException("fields", "Provide the list of fields.");
        if (input.Count > MaxFieldsPerType || input.Any(i => i.FieldId is null) || input.Select(i => i.FieldId).Distinct().Count() != input.Count)
        {
            throw new RequestValidationException("fields", $"Provide at most {MaxFieldsPerType} distinct fields.");
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        _ = await db.TicketTypes.AnyAsync(t => t.Id == typeId && t.DepartmentId == departmentId, ct) ? 0 : throw new NotFoundException();
        var wanted = input.Select(i => i.FieldId!.Value).ToArray();
        var known = await db.TicketFields.AsNoTracking().Where(f => f.DepartmentId == departmentId && wanted.Contains(f.Id)).ToDictionaryAsync(f => f.Id, ct);
        var existing = await db.TicketTypeFields.Where(l => l.TypeId == typeId && l.DepartmentId == departmentId).ToListAsync(ct);
        foreach (var id in wanted)
        {
            if (!known.TryGetValue(id, out var field) || (!field.IsActive && existing.All(e => e.FieldId != id)))
            {
                throw new RequestValidationException("fields", "Choose active fields of this department.");
            }
        }

        db.TicketTypeFields.RemoveRange(existing.Where(e => !wanted.Contains(e.FieldId)));
        for (var i = 0; i < input.Count; i++)
        {
            var id = wanted[i];
            var row = existing.SingleOrDefault(e => e.FieldId == id);
            if (row is null)
            {
                db.TicketTypeFields.Add(new TicketTypeField { TypeId = typeId, FieldId = id, DepartmentId = departmentId, SortOrder = (i + 1) * 10, Required = input[i].Required });
            }
            else
            {
                row.SortOrder = (i + 1) * 10;
                row.Required = input[i].Required;
            }
        }

        var type = await db.TicketTypes.SingleAsync(t => t.Id == typeId, ct);
        type.UpdatedAt = ClockExtensions.GetUtcNowMicroseconds(clock);
        await SaveAsync(ct);
        await audit.WriteAsync(TicketAuditActions.TicketTypeFieldsChanged, TypeObject, typeId, departmentId, null, new { fieldIds = wanted }, ct);
        await tx.CommitAsync(ct);
        return (await ListAsync(departmentId, ct)).Single(t => t.Id == typeId);
    }

    // ----- resolution for creating tickets -----

    /// <summary>The type to store on a new ticket: an explicit choice (must be active and of the department), else the fallback (a form's type) when still usable, else the department default, else none.</summary>
    public async Task<Guid?> ResolveForCreateAsync(Guid departmentId, Guid? requested, Guid? fallback, CancellationToken ct)
    {
        if (requested is { } id)
        {
            return await RequireActiveAsync(departmentId, id, "ticketTypeId", ct);
        }

        if (fallback is { } fb && await db.TicketTypes.AsNoTracking().AnyAsync(t => t.Id == fb && t.DepartmentId == departmentId && t.IsActive, ct))
        {
            return fb;
        }

        return await DefaultIdAsync(departmentId, ct);
    }

    public Task<Guid?> DefaultIdAsync(Guid departmentId, CancellationToken ct) =>
        db.TicketTypes.AsNoTracking().Where(t => t.DepartmentId == departmentId && t.IsDefault && t.IsActive).Select(t => (Guid?)t.Id).SingleOrDefaultAsync(ct);

    /// <summary>A foreign, missing or inactive type all answer the same way, so nothing about other departments is revealed.</summary>
    public async Task<Guid> RequireActiveAsync(Guid departmentId, Guid typeId, string field, CancellationToken ct) =>
        await db.TicketTypes.AsNoTracking().AnyAsync(t => t.Id == typeId && t.DepartmentId == departmentId && t.IsActive, ct)
            ? typeId
            : throw new RequestValidationException(field, "Choose an active ticket type of this department.");

    // ----- helpers -----

    private async Task MakeDefaultAsync(Guid departmentId, TicketType type, CancellationToken ct)
    {
        foreach (var other in await db.TicketTypes.Where(t => t.DepartmentId == departmentId && t.IsDefault && t.Id != type.Id).ToListAsync(ct))
        {
            other.IsDefault = false;
        }

        await SaveAsync(ct); // the partial unique index allows one default: clear before setting
        type.IsDefault = true;
        await SaveAsync(ct);
    }

    private static (string? Name, string? Description) Validate(Dictionary<string, string[]> errors, TicketTypeRequest r, bool creating)
    {
        string? name = null;
        if (r.Name is not null || creating)
        {
            name = r.Name?.Trim();
            if (string.IsNullOrEmpty(name) || name.Length > 100)
            {
                errors["name"] = ["Name must be 1 to 100 characters."];
                name = null;
            }
        }

        var description = r.Description?.Trim();
        if (description is { Length: > 500 })
        {
            errors["description"] = ["Description is at most 500 characters."];
        }

        return (name, string.IsNullOrEmpty(description) ? null : description);
    }

    private static TicketTypeAdminDto ToDto(TicketType t, IEnumerable<TypeFieldLinkDto> fields) =>
        new(t.Id, t.DepartmentId, t.Name, t.Description, t.IsActive, t.IsDefault, t.SortOrder, [.. fields], ConcurrencyVersion.ToToken(t.Version));

    private async Task SaveAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictException("The item was changed elsewhere. Reload and retry.", ProblemCodes.VersionConflict);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw new ConflictException("A ticket type with this name already exists.", ProblemCodes.Duplicate);
        }
    }
}
