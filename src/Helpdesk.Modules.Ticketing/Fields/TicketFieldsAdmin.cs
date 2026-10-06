using System.Text.RegularExpressions;
using Helpdesk.Modules.Ticketing.Domain;
using Helpdesk.Modules.Ticketing.Persistence;
using Helpdesk.Modules.Ticketing.Services;
using Helpdesk.Modules.Ticketing.Sla;
using Helpdesk.SharedKernel.Database;
using Helpdesk.SharedKernel.Errors;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Helpdesk.Modules.Ticketing.Fields;

internal sealed record OptionInput(string? Value, string? Label, bool? IsActive);

internal sealed record FieldUsageDto(Guid Id, string Name);

internal sealed record TicketFieldAdminDto(
    Guid Id, Guid DepartmentId, string Key, string Label, string? HelpText, string Type, bool Required, IReadOnlyList<FieldOption> Options, int Order, bool IsActive,
    bool AppliesToAllTypes, bool RequesterVisible, bool RequesterEditable, bool AgentVisible, bool HasValues, IReadOnlyList<FieldUsageDto> Types, IReadOnlyList<FieldUsageDto> Forms,
    string Version);

internal sealed record TicketFieldRequest(
    string? Key, string? Label, string? HelpText, string? Type, bool? Required, IReadOnlyList<OptionInput>? Options, bool? IsActive, bool? AppliesToAllTypes,
    bool? RequesterVisible, bool? RequesterEditable, bool? AgentVisible, string? Version);

/// <summary>Ticket field definition administration (permission department.forms.manage, enforced by the endpoint filter; every query also names the department).</summary>
internal sealed partial class TicketFieldsService(TicketingDbContext db, TicketingAudit audit, TimeProvider clock)
{
    public const int MaxFields = 100;
    private const string FieldObject = "ticket_field";

    [GeneratedRegex("^[a-z][a-z0-9_]{0,39}$")]
    private static partial Regex KeyPattern();

    public async Task<IReadOnlyList<TicketFieldAdminDto>> ListAsync(Guid departmentId, CancellationToken ct)
    {
        var fields = await db.TicketFields.AsNoTracking().Where(f => f.DepartmentId == departmentId).OrderBy(f => f.SortOrder).ThenBy(f => f.Key).ToListAsync(ct);
        var used = (await db.TicketFieldValues.AsNoTracking().IgnoreDepartmentScope().Where(v => v.DepartmentId == departmentId && v.FieldId != null).Select(v => v.FieldId!.Value).Distinct().ToListAsync(ct)).ToHashSet();
        var onTypes = await db.TicketTypeFields.AsNoTracking().Where(l => l.DepartmentId == departmentId)
            .Join(db.TicketTypes.AsNoTracking(), l => l.TypeId, t => t.Id, (l, t) => new { l.FieldId, t.Id, t.Name }).ToListAsync(ct);
        var onForms = await db.RequestFormFields.AsNoTracking().Where(l => l.DepartmentId == departmentId)
            .Join(db.RequestForms.AsNoTracking(), l => l.FormId, f => f.Id, (l, f) => new { l.FieldId, f.Id, f.Name }).ToListAsync(ct);
        return [.. fields.Select(f => ToDto(f, used.Contains(f.Id),
            [.. onTypes.Where(x => x.FieldId == f.Id).Select(x => new FieldUsageDto(x.Id, x.Name)).OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)],
            [.. onForms.Where(x => x.FieldId == f.Id).Select(x => new FieldUsageDto(x.Id, x.Name)).OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)]))];
    }

    public async Task<TicketFieldAdminDto> CreateAsync(Guid departmentId, TicketFieldRequest request, CancellationToken ct)
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

        var visible = request.RequesterVisible ?? true;
        var editable = request.RequesterEditable ?? false;
        if (editable && !visible)
        {
            errors["requesterEditable"] = ["A field the requester cannot see cannot be editable by them."];
        }

        TicketValidation.ThrowIfAny(errors);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (await db.TicketFields.CountAsync(f => f.DepartmentId == departmentId, ct) >= MaxFields)
        {
            throw new ConflictException($"A department can have at most {MaxFields} ticket fields.", ProblemCodes.InvalidState);
        }

        var last = await db.TicketFields.Where(f => f.DepartmentId == departmentId).MaxAsync(f => (int?)f.SortOrder, ct) ?? 0;
        var now = ClockExtensions.GetUtcNowMicroseconds(clock);
        var field = new TicketField
        {
            Id = Guid.CreateVersion7(),
            DepartmentId = departmentId,
            Key = key!,
            Label = label!,
            HelpText = help,
            FieldType = type!,
            Required = request.Required ?? false,
            Options = StoredJson.Write(options),
            SortOrder = last + 10,
            IsActive = request.IsActive ?? true,
            AppliesToAllTypes = request.AppliesToAllTypes ?? false,
            RequesterVisible = visible,
            RequesterEditable = editable,
            AgentVisible = request.AgentVisible ?? true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.TicketFields.Add(field);
        await SaveAsync("A field with this key already exists in the department.", ct);
        await audit.WriteAsync(TicketAuditActions.TicketFieldCreated, FieldObject, field.Id, departmentId, null, new { key = field.Key, label = field.Label, type = field.FieldType, common = field.AppliesToAllTypes }, ct);
        await tx.CommitAsync(ct);
        return (await ListAsync(departmentId, ct)).Single(f => f.Id == field.Id);
    }

    public async Task<TicketFieldAdminDto> UpdateAsync(Guid departmentId, Guid fieldId, TicketFieldRequest request, CancellationToken ct)
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
        var field = await db.TicketFields.SingleOrDefaultAsync(f => f.Id == fieldId && f.DepartmentId == departmentId, ct) ?? throw new NotFoundException();
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

            var parsed = ParseOptions(errors, request.Options, FieldValueValidator.Options(field));
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
        field.AppliesToAllTypes = request.AppliesToAllTypes ?? field.AppliesToAllTypes;
        field.RequesterVisible = request.RequesterVisible ?? field.RequesterVisible;
        field.RequesterEditable = request.RequesterEditable ?? field.RequesterEditable;
        field.AgentVisible = request.AgentVisible ?? field.AgentVisible;
        if (field.RequesterEditable && !field.RequesterVisible)
        {
            throw new RequestValidationException("requesterEditable", "A field the requester cannot see cannot be editable by them.");
        }

        field.UpdatedAt = ClockExtensions.GetUtcNowMicroseconds(clock);
        await SaveAsync("Conflict.", ct);

        // Tickets keep their own snapshot (label, type, display text): this edit never rewrites what an existing ticket shows.
        await audit.WriteAsync(TicketAuditActions.TicketFieldUpdated, FieldObject, field.Id, departmentId, null,
            new { key = field.Key, label = field.Label, isActive = field.IsActive, required = field.Required, common = field.AppliesToAllTypes }, ct);
        await tx.CommitAsync(ct);
        return (await ListAsync(departmentId, ct)).Single(f => f.Id == fieldId);
    }

    public async Task<IReadOnlyList<TicketFieldAdminDto>> ReorderAsync(Guid departmentId, ReorderRequest request, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var rows = await db.TicketFields.Where(f => f.DepartmentId == departmentId).ToListAsync(ct);
        var ids = request.Ids ?? [];
        if (ids.Count != rows.Count || ids.Distinct().Count() != ids.Count || !ids.All(i => rows.Any(r => r.Id == i)))
        {
            throw new RequestValidationException(new Dictionary<string, string[]> { ["ids"] = ["List every field of the department exactly once."] });
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
        await audit.WriteAsync(TicketAuditActions.TicketFieldUpdated, FieldObject, departmentId, departmentId, null, new { reordered = ids.Count }, ct);
        await tx.CommitAsync(ct);
        return await ListAsync(departmentId, ct);
    }

    // ----- helpers -----

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
        if (input is null || input.Count is 0 or > FieldValueValidator.MaxOptions)
        {
            errors["options"] = [$"Provide between 1 and {FieldValueValidator.MaxOptions} options."];
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

    private static TicketFieldAdminDto ToDto(TicketField f, bool hasValues, IReadOnlyList<FieldUsageDto> types, IReadOnlyList<FieldUsageDto> forms) =>
        new(f.Id, f.DepartmentId, f.Key, f.Label, f.HelpText, f.FieldType, f.Required, FieldValueValidator.Options(f), f.SortOrder, f.IsActive, f.AppliesToAllTypes,
            f.RequesterVisible, f.RequesterEditable, f.AgentVisible, hasValues, types, forms, ConcurrencyVersion.ToToken(f.Version));

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
