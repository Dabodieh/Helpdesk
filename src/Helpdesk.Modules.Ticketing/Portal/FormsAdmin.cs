using System.Text.RegularExpressions;
using Helpdesk.Modules.Organisation.Contracts;
using Helpdesk.Modules.Ticketing.Domain;
using Helpdesk.Modules.Ticketing.Persistence;
using Helpdesk.Modules.Ticketing.Services;
using Helpdesk.Modules.Ticketing.Sla;
using Helpdesk.SharedKernel.Database;
using Helpdesk.SharedKernel.Errors;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Helpdesk.Modules.Ticketing.Portal;

internal sealed record OptionInput(string? Value, string? Label, bool? IsActive);

internal sealed record FieldAdminDto(
    Guid Id, string Key, string Label, string? HelpText, string Type, bool Required, IReadOnlyList<FieldOption> Options, int Order, bool IsActive,
    bool RequesterVisible, bool AgentVisible, bool HasValues, string Version);

internal sealed record FormAdminDto(
    Guid Id, Guid DepartmentId, string Name, string? Description, bool IsActive, bool VisibleToRequesters, int Order, Guid? DefaultTeamId, string? DefaultPriority,
    IReadOnlyList<FieldAdminDto> Fields, string Version);

internal sealed record FormRequest(
    string? Name, string? Description, bool? IsActive, bool? VisibleToRequesters, Guid? DefaultTeamId, bool? ClearDefaultTeam, string? DefaultPriority,
    bool? ClearDefaultPriority, string? Version);

internal sealed record FieldRequest(
    string? Key, string? Label, string? HelpText, string? Type, bool? Required, IReadOnlyList<OptionInput>? Options, bool? IsActive, bool? RequesterVisible,
    bool? AgentVisible, string? Version);

/// <summary>Request form and custom field administration (permission department.forms.manage, enforced by the endpoint filter; every query also filters by the department).</summary>
internal sealed partial class FormsAdminService(TicketingDbContext db, TicketingAudit audit, IOrganisationDirectory organisation, TimeProvider clock)
{
    public const int MaxForms = 50;
    public const int MaxFieldsPerForm = 30;
    private const string FormObject = "request_form";
    private const string FieldObject = "form_field";

    [GeneratedRegex("^[a-z][a-z0-9_]{0,39}$")]
    private static partial Regex KeyPattern();

    public async Task<IReadOnlyList<FormAdminDto>> ListAsync(Guid departmentId, CancellationToken ct)
    {
        var forms = await db.RequestForms.AsNoTracking().Where(f => f.DepartmentId == departmentId).OrderBy(f => f.SortOrder).ThenBy(f => f.Name).ToListAsync(ct);
        var fields = await db.FormFields.AsNoTracking().Where(f => f.DepartmentId == departmentId).OrderBy(f => f.SortOrder).ThenBy(f => f.Key).ToListAsync(ct);
        var used = (await db.TicketFieldValues.AsNoTracking().IgnoreDepartmentScope().Where(v => v.DepartmentId == departmentId && v.FieldId != null).Select(v => v.FieldId!.Value).Distinct().ToListAsync(ct)).ToHashSet();
        return [.. forms.Select(f => ToDto(f, fields.Where(x => x.FormId == f.Id), used))];
    }

    public async Task<FormAdminDto> CreateFormAsync(Guid departmentId, FormRequest request, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        var (name, description, priority) = ValidateForm(errors, request, creating: true);
        TicketValidation.ThrowIfAny(errors);
        await CheckTeamAsync(departmentId, request.DefaultTeamId, ct);
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
            CreatedAt = now,
            UpdatedAt = now,
        };
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.RequestForms.Add(form);
        await SaveAsync("A form with this name already exists.", ct);
        await audit.WriteAsync(TicketAuditActions.RequestFormCreated, FormObject, form.Id, departmentId, null, new { name = form.Name, isActive = form.IsActive }, ct);
        await tx.CommitAsync(ct);
        return ToDto(form, [], []);
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

        db.Entry(form).Property(f => f.Version).OriginalValue = version;
        form.Name = name ?? form.Name;
        if (request.Description is not null)
        {
            form.Description = description;
        }

        form.IsActive = request.IsActive ?? form.IsActive;
        form.VisibleToRequesters = request.VisibleToRequesters ?? form.VisibleToRequesters;
        form.DefaultTeamId = request.ClearDefaultTeam == true ? null : request.DefaultTeamId ?? form.DefaultTeamId;
        form.DefaultPriority = request.ClearDefaultPriority == true ? null : priority ?? form.DefaultPriority;
        form.UpdatedAt = ClockExtensions.GetUtcNowMicroseconds(clock);
        await SaveAsync("A form with this name already exists.", ct);
        await audit.WriteAsync(TicketAuditActions.RequestFormUpdated, FormObject, form.Id, departmentId, null, new { name = form.Name, isActive = form.IsActive, visible = form.VisibleToRequesters }, ct);
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

    // ----- fields -----

    public async Task<FormAdminDto> CreateFieldAsync(Guid departmentId, Guid formId, FieldRequest request, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        var label = request.Label?.Trim();
        if (string.IsNullOrEmpty(label) || label.Length > 100)
        {
            errors["label"] = ["Label must be 1 to 100 characters."];
        }

        var type = request.Type;
        if (type is null || !FieldTypes.All.Contains(type, StringComparer.Ordinal))
        {
            errors["type"] = ["Choose a field type."];
        }

        var help = Help(errors, request.HelpText);
        List<FieldOption> options = [];
        if (type is not null && FieldTypes.HasOptions(type))
        {
            options = ParseOptions(errors, request.Options, existing: []);
        }
        else if (request.Options is { Count: > 0 })
        {
            errors["options"] = ["Only select fields have options."];
        }

        var key = request.Key?.Trim();
        if (string.IsNullOrEmpty(key) && label is not null)
        {
            key = Slug(label);
        }

        if (key is null || !KeyPattern().IsMatch(key))
        {
            errors["key"] = ["The key must start with a letter and use lower-case letters, digits and underscores (at most 40)."];
        }

        TicketValidation.ThrowIfAny(errors);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var form = await db.RequestForms.SingleOrDefaultAsync(f => f.Id == formId && f.DepartmentId == departmentId, ct) ?? throw new NotFoundException();
        if (await db.FormFields.CountAsync(f => f.FormId == formId, ct) >= MaxFieldsPerForm)
        {
            throw new ConflictException($"A form can have at most {MaxFieldsPerForm} fields.", ProblemCodes.InvalidState);
        }

        var last = await db.FormFields.Where(f => f.FormId == formId).MaxAsync(f => (int?)f.SortOrder, ct) ?? 0;
        var now = ClockExtensions.GetUtcNowMicroseconds(clock);
        var field = new FormField
        {
            Id = Guid.CreateVersion7(),
            FormId = form.Id,
            DepartmentId = departmentId,
            Key = key!,
            Label = label!,
            HelpText = help,
            FieldType = type!,
            Required = request.Required ?? false,
            Options = StoredJson.Write(options),
            SortOrder = last + 10,
            IsActive = request.IsActive ?? true,
            RequesterVisible = request.RequesterVisible ?? true,
            AgentVisible = request.AgentVisible ?? true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.FormFields.Add(field);
        await SaveAsync("A field with this key already exists on the form.", ct);
        await audit.WriteAsync(TicketAuditActions.FormFieldCreated, FieldObject, field.Id, departmentId, null, new { formId, key = field.Key, label = field.Label, type = field.FieldType }, ct);
        await tx.CommitAsync(ct);
        return (await ListAsync(departmentId, ct)).Single(f => f.Id == formId);
    }

    public async Task<FormAdminDto> UpdateFieldAsync(Guid departmentId, Guid formId, Guid fieldId, FieldRequest request, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        var version = TicketValidation.Version(errors, request.Version);
        string? label = null;
        if (request.Label is not null)
        {
            label = request.Label.Trim();
            if (label.Length is 0 or > 100)
            {
                errors["label"] = ["Label must be 1 to 100 characters."];
            }
        }

        var help = Help(errors, request.HelpText);
        if (request.Key is not null || request.Type is not null)
        {
            errors["key"] = ["The key and the type of a field cannot be changed. Add a new field instead."];
        }

        TicketValidation.ThrowIfAny(errors);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var field = await db.FormFields.SingleOrDefaultAsync(f => f.Id == fieldId && f.FormId == formId && f.DepartmentId == departmentId, ct) ?? throw new NotFoundException();
        if (field.Version != version)
        {
            throw new ConflictException("The field was changed elsewhere. Reload and retry.", ProblemCodes.VersionConflict);
        }

        db.Entry(field).Property(f => f.Version).OriginalValue = version;
        if (request.Options is not null)
        {
            if (!FieldTypes.HasOptions(field.FieldType))
            {
                throw new RequestValidationException("options", "Only select fields have options.");
            }

            var parsed = ParseOptions(errors, request.Options, FormSubmissionValidator.Options(field));
            TicketValidation.ThrowIfAny(errors);
            field.Options = StoredJson.Write(parsed);
        }

        field.Label = label ?? field.Label;
        if (request.HelpText is not null)
        {
            field.HelpText = help;
        }

        field.Required = request.Required ?? field.Required;
        field.IsActive = request.IsActive ?? field.IsActive;
        field.RequesterVisible = request.RequesterVisible ?? field.RequesterVisible;
        field.AgentVisible = request.AgentVisible ?? field.AgentVisible;
        field.UpdatedAt = ClockExtensions.GetUtcNowMicroseconds(clock);
        await SaveAsync("Conflict.", ct);

        // Tickets keep their own snapshot (label, type, display text): this edit never rewrites what an existing ticket shows.
        await audit.WriteAsync(TicketAuditActions.FormFieldUpdated, FieldObject, field.Id, departmentId, null, new { formId, key = field.Key, label = field.Label, isActive = field.IsActive, required = field.Required }, ct);
        await tx.CommitAsync(ct);
        return (await ListAsync(departmentId, ct)).Single(f => f.Id == formId);
    }

    public async Task<FormAdminDto> ReorderFieldsAsync(Guid departmentId, Guid formId, ReorderRequest request, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        _ = await db.RequestForms.AnyAsync(f => f.Id == formId && f.DepartmentId == departmentId, ct) ? 0 : throw new NotFoundException();
        var rows = await db.FormFields.Where(f => f.FormId == formId && f.DepartmentId == departmentId).ToListAsync(ct);
        var ids = request.Ids ?? [];
        if (ids.Count != rows.Count || ids.Distinct().Count() != ids.Count || !ids.All(i => rows.Any(r => r.Id == i)))
        {
            throw new RequestValidationException(new Dictionary<string, string[]> { ["ids"] = ["List every field of the form exactly once."] });
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
        await audit.WriteAsync(TicketAuditActions.FormFieldUpdated, FieldObject, formId, departmentId, null, new { formId, reordered = ids.Count }, ct);
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

    private static string? Help(Dictionary<string, string[]> errors, string? help)
    {
        var text = help?.Trim();
        if (text is { Length: > 500 })
        {
            errors["helpText"] = ["Help text is at most 500 characters."];
        }

        return string.IsNullOrEmpty(text) ? null : text;
    }

    /// <summary>Options are matched by their stable value. Existing options can be relabelled or deactivated but never removed, so stored values stay interpretable.</summary>
    private static List<FieldOption> ParseOptions(Dictionary<string, string[]> errors, IReadOnlyList<OptionInput>? input, IReadOnlyList<FieldOption> existing)
    {
        var result = new List<FieldOption>();
        if (input is null || input.Count is 0 or > FormSubmissionValidator.MaxOptions)
        {
            errors["options"] = [$"Provide between 1 and {FormSubmissionValidator.MaxOptions} options."];
            return result;
        }

        foreach (var (o, i) in input.Select((o, i) => (o, i)))
        {
            var label = o.Label?.Trim();
            if (string.IsNullOrEmpty(label) || label.Length > 100)
            {
                errors[$"options[{i}]"] = ["Each option needs a label of 1 to 100 characters."];
                continue;
            }

            var value = o.Value?.Trim();
            if (string.IsNullOrEmpty(value))
            {
                value = Slug(label);
                var n = 2;
                while (result.Any(r => r.Value == value) || existing.Any(e => e.Value == value && e.Label != label))
                {
                    value = Slug(label) + "_" + n++;
                }
            }

            if (!KeyPattern().IsMatch(value) || result.Any(r => r.Value == value))
            {
                errors[$"options[{i}]"] = ["Option values must be unique (lower-case letters, digits, underscores)."];
                continue;
            }

            result.Add(new FieldOption(value, label, o.IsActive ?? true));
        }

        foreach (var kept in existing.Where(e => result.All(r => r.Value != e.Value)))
        {
            errors["options"] = [$"The option \"{kept.Label}\" cannot be removed (deactivate it instead)."];
        }

        return result;
    }

    private static string Slug(string text)
    {
        var chars = text.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_').ToArray();
        var slug = Regex.Replace(new string(chars), "_+", "_").Trim('_');
        if (slug.Length == 0 || !char.IsAsciiLetter(slug[0]))
        {
            slug = "f_" + slug;
        }

        return slug.Length > 40 ? slug[..40].TrimEnd('_') : slug;
    }

    private static FormAdminDto ToDto(RequestForm f, IEnumerable<FormField> fields, HashSet<Guid> used) =>
        new(f.Id, f.DepartmentId, f.Name, f.Description, f.IsActive, f.VisibleToRequesters, f.SortOrder, f.DefaultTeamId, f.DefaultPriority?.ToString(),
            [.. fields.Select(x => new FieldAdminDto(x.Id, x.Key, x.Label, x.HelpText, x.FieldType, x.Required, FormSubmissionValidator.Options(x), x.SortOrder, x.IsActive,
                x.RequesterVisible, x.AgentVisible, used.Contains(x.Id), ConcurrencyVersion.ToToken(x.Version)))],
            ConcurrencyVersion.ToToken(f.Version));

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
