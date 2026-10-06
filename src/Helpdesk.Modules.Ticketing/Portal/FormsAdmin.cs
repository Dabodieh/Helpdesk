using Helpdesk.Modules.Organisation.Contracts;
using Helpdesk.Modules.Ticketing.Domain;
using Helpdesk.Modules.Ticketing.Fields;
using Helpdesk.Modules.Ticketing.Persistence;
using Helpdesk.Modules.Ticketing.Services;
using Helpdesk.Modules.Ticketing.Sla;
using Helpdesk.SharedKernel.Database;
using Helpdesk.SharedKernel.Errors;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Helpdesk.Modules.Ticketing.Portal;

internal sealed record FormFieldLinkDto(
    Guid FieldId, string Key, string Label, string Type, bool Required, bool? RequiredOverride, int Order, bool FieldIsActive, bool RequesterVisible);

internal sealed record FormAdminDto(
    Guid Id, Guid DepartmentId, string Name, string? Description, bool IsActive, bool VisibleToRequesters, int Order, Guid? DefaultTeamId, string? DefaultPriority,
    Guid? TicketTypeId, IReadOnlyList<FormFieldLinkDto> Fields, string Version);

internal sealed record FormRequest(
    string? Name, string? Description, bool? IsActive, bool? VisibleToRequesters, Guid? DefaultTeamId, bool? ClearDefaultTeam, string? DefaultPriority,
    bool? ClearDefaultPriority, Guid? TicketTypeId, bool? ClearTicketType, string? Version);

/// <summary>
/// Request form administration (permission department.forms.manage, enforced by the endpoint filter; every query also names the department). A form is
/// HOW a requester submits: it picks a ticket type of its department and selects, orders and (optionally) overrides the requirement of department ticket fields.
/// </summary>
internal sealed class FormsAdminService(TicketingDbContext db, TicketingAudit audit, IOrganisationDirectory organisation, TicketTypesService types, TimeProvider clock)
{
    public const int MaxForms = 50;
    public const int MaxFieldsPerForm = 30;
    private const string FormObject = "request_form";

    public async Task<IReadOnlyList<FormAdminDto>> ListAsync(Guid departmentId, CancellationToken ct)
    {
        var forms = await db.RequestForms.AsNoTracking().Where(f => f.DepartmentId == departmentId).OrderBy(f => f.SortOrder).ThenBy(f => f.Name).ToListAsync(ct);
        var links = await db.RequestFormFields.AsNoTracking().Where(l => l.DepartmentId == departmentId)
            .Join(db.TicketFields.AsNoTracking(), l => l.FieldId, f => f.Id, (l, f) => new { Link = l, Field = f }).ToListAsync(ct);
        return [.. forms.Select(f => ToDto(f, links.Where(x => x.Link.FormId == f.Id).OrderBy(x => x.Link.SortOrder)
            .Select(x => new FormFieldLinkDto(x.Field.Id, x.Field.Key, x.Field.Label, x.Field.FieldType, x.Link.Required ?? x.Field.Required, x.Link.Required, x.Link.SortOrder, x.Field.IsActive, x.Field.RequesterVisible))))];
    }

    public async Task<FormAdminDto> CreateFormAsync(Guid departmentId, FormRequest request, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        var (name, description, priority) = ValidateForm(errors, request, creating: true);
        TicketValidation.ThrowIfAny(errors);
        await CheckTeamAsync(departmentId, request.DefaultTeamId, ct);
        if (request.TicketTypeId is { } typeId)
        {
            await types.RequireActiveAsync(departmentId, typeId, "ticketTypeId", ct);
        }

        if (await db.RequestForms.CountAsync(f => f.DepartmentId == departmentId, ct) >= MaxForms)
        {
            throw new ConflictException($"A department can keep at most {MaxForms} request forms.", ProblemCodes.InvalidState);
        }

        var now = ClockExtensions.GetUtcNowMicroseconds(clock);
        var last = await db.RequestForms.Where(f => f.DepartmentId == departmentId).MaxAsync(f => (int?)f.SortOrder, ct) ?? 0;
        var form = new RequestForm
        {
            Id = Guid.CreateVersion7(),
            DepartmentId = departmentId,
            Name = name!,
            Description = description,
            IsActive = request.IsActive ?? true,
            VisibleToRequesters = request.VisibleToRequesters ?? true,
            SortOrder = last + 10,
            DefaultTeamId = request.DefaultTeamId,
            DefaultPriority = priority,
            TicketTypeId = request.TicketTypeId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.RequestForms.Add(form);
        await SaveAsync("A form with this name already exists.", ct);
        await audit.WriteAsync(TicketAuditActions.RequestFormCreated, FormObject, form.Id, departmentId, null, new { name = form.Name, isActive = form.IsActive, ticketTypeId = form.TicketTypeId }, ct);
        await tx.CommitAsync(ct);
        return ToDto(form, []);
    }

    public async Task<FormAdminDto> UpdateFormAsync(Guid departmentId, Guid formId, FormRequest request, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        var version = TicketValidation.Version(errors, request.Version);
        var (name, description, priority) = ValidateForm(errors, request, creating: false);
        TicketValidation.ThrowIfAny(errors);
        await CheckTeamAsync(departmentId, request.DefaultTeamId, ct);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var form = await db.RequestForms.SingleOrDefaultAsync(f => f.Id == formId && f.DepartmentId == departmentId, ct) ?? throw new NotFoundException();
        if (form.Version != version)
        {
            throw new ConflictException("The form was changed elsewhere. Reload and retry.", ProblemCodes.VersionConflict);
        }

        if (request.TicketTypeId is { } typeId && typeId != form.TicketTypeId)
        {
            await types.RequireActiveAsync(departmentId, typeId, "ticketTypeId", ct);
        }

        db.Entry(form).Property(f => f.Version).OriginalValue = version;
        var previousType = form.TicketTypeId;
        form.Name = name ?? form.Name;
        if (request.Description is not null)
        {
            form.Description = description;
        }

        form.IsActive = request.IsActive ?? form.IsActive;
        form.VisibleToRequesters = request.VisibleToRequesters ?? form.VisibleToRequesters;
        form.DefaultTeamId = request.ClearDefaultTeam == true ? null : request.DefaultTeamId ?? form.DefaultTeamId;
        form.DefaultPriority = request.ClearDefaultPriority == true ? null : priority ?? form.DefaultPriority;
        form.TicketTypeId = request.ClearTicketType == true ? null : request.TicketTypeId ?? form.TicketTypeId;
        form.UpdatedAt = ClockExtensions.GetUtcNowMicroseconds(clock);
        await SaveAsync("A form with this name already exists.", ct);
        await audit.WriteAsync(TicketAuditActions.RequestFormUpdated, FormObject, form.Id, departmentId, previousType == form.TicketTypeId ? null : new { ticketTypeId = previousType },
            new { name = form.Name, isActive = form.IsActive, visible = form.VisibleToRequesters, ticketTypeId = form.TicketTypeId }, ct);
        await tx.CommitAsync(ct);
        return (await ListAsync(departmentId, ct)).Single(f => f.Id == form.Id);
    }

    public async Task<IReadOnlyList<FormAdminDto>> ReorderFormsAsync(Guid departmentId, ReorderRequest request, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var rows = await db.RequestForms.Where(f => f.DepartmentId == departmentId).ToListAsync(ct);
        var ids = request.Ids ?? [];
        if (ids.Count != rows.Count || ids.Distinct().Count() != ids.Count || !ids.All(i => rows.Any(r => r.Id == i)))
        {
            throw new RequestValidationException(new Dictionary<string, string[]> { ["ids"] = ["List every form of the department exactly once."] });
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
        await audit.WriteAsync(TicketAuditActions.RequestFormUpdated, FormObject, departmentId, departmentId, null, new { reordered = ids.Count }, ct);
        await tx.CommitAsync(ct);
        return await ListAsync(departmentId, ct);
    }

    /// <summary>
    /// Replaces the form's field list (order = list order). A new link must point to an active field of THIS department (the database also
    /// enforces the same department through the composite key); links that already exist may stay even if the field was since deactivated. A field that is not requester visible may be linked; requesters are simply never asked it.
    /// </summary>
    public async Task<FormAdminDto> SetFieldsAsync(Guid departmentId, Guid formId, FieldLinksRequest request, CancellationToken ct)
    {
        var input = request.Fields ?? throw new RequestValidationException("fields", "Provide the list of fields.");
        if (input.Count > MaxFieldsPerForm || input.Any(i => i.FieldId is null) || input.Select(i => i.FieldId).Distinct().Count() != input.Count)
        {
            throw new RequestValidationException("fields", $"Provide at most {MaxFieldsPerForm} distinct fields.");
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var form = await db.RequestForms.SingleOrDefaultAsync(f => f.Id == formId && f.DepartmentId == departmentId, ct) ?? throw new NotFoundException();
        var wanted = input.Select(i => i.FieldId!.Value).ToArray();
        var known = await db.TicketFields.AsNoTracking().Where(f => f.DepartmentId == departmentId && wanted.Contains(f.Id)).ToDictionaryAsync(f => f.Id, ct);
        var existing = await db.RequestFormFields.Where(l => l.FormId == formId && l.DepartmentId == departmentId).ToListAsync(ct);
        foreach (var id in wanted)
        {
            var isNew = existing.All(e => e.FieldId != id);
            if (!known.TryGetValue(id, out var field) || (isNew && !field.IsActive))
            {
                throw new RequestValidationException("fields", "Choose active fields of this department.");
            }
        }

        db.RequestFormFields.RemoveRange(existing.Where(e => !wanted.Contains(e.FieldId)));
        for (var i = 0; i < input.Count; i++)
        {
            var id = wanted[i];
            var row = existing.SingleOrDefault(e => e.FieldId == id);
            if (row is null)
            {
                db.RequestFormFields.Add(new RequestFormField { FormId = formId, FieldId = id, DepartmentId = departmentId, SortOrder = (i + 1) * 10, Required = input[i].Required });
            }
            else
            {
                row.SortOrder = (i + 1) * 10;
                row.Required = input[i].Required;
            }
        }

        form.UpdatedAt = ClockExtensions.GetUtcNowMicroseconds(clock);
        await SaveAsync("Conflict.", ct);
        await audit.WriteAsync(TicketAuditActions.RequestFormFieldsChanged, FormObject, formId, departmentId, null, new { fieldIds = wanted }, ct);
        await tx.CommitAsync(ct);
        return (await ListAsync(departmentId, ct)).Single(f => f.Id == formId);
    }

    // ----- helpers -----

    private static (string? Name, string? Description, TicketPriority? Priority) ValidateForm(Dictionary<string, string[]> errors, FormRequest r, bool creating)
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
        if (description is { Length: > 1000 })
        {
            errors["description"] = ["Description is at most 1000 characters."];
        }

        TicketPriority? priority = null;
        if (r.DefaultPriority is not null)
        {
            priority = TicketValidation.ParseEnum<TicketPriority>(errors, "defaultPriority", r.DefaultPriority);
        }

        return (name, string.IsNullOrEmpty(description) ? null : description, priority);
    }

    private async Task CheckTeamAsync(Guid departmentId, Guid? teamId, CancellationToken ct)
    {
        if (teamId is { } id && await organisation.FindTeamAsync(departmentId, id, ct) is not { IsActive: true })
        {
            throw new RequestValidationException("defaultTeamId", "Choose an active team of this department.");
        }
    }

    private static FormAdminDto ToDto(RequestForm f, IEnumerable<FormFieldLinkDto> fields) =>
        new(f.Id, f.DepartmentId, f.Name, f.Description, f.IsActive, f.VisibleToRequesters, f.SortOrder, f.DefaultTeamId, f.DefaultPriority?.ToString(), f.TicketTypeId,
            [.. fields], ConcurrencyVersion.ToToken(f.Version));

    private async Task SaveAsync(string duplicateMessage, CancellationToken ct)
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
            throw new ConflictException(duplicateMessage, ProblemCodes.Duplicate);
        }
    }
}
