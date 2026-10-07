using Helpdesk.Modules.Organisation.Contracts;
using Helpdesk.Modules.Ticketing.Domain;
using Helpdesk.Modules.Ticketing.Fields;
using Helpdesk.Modules.Ticketing.Persistence;
using Helpdesk.SharedKernel.Errors;
using Helpdesk.SharedKernel.Security;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Modules.Ticketing.Portal;

internal sealed record PortalFormListItemDto(Guid Id, string Name, string? Description, string Department);

internal sealed record PortalFieldDto(string Key, string Label, string? HelpText, string Type, bool Required, IReadOnlyList<PortalOptionDto> Options);

internal sealed record PortalOptionDto(string Value, string Label);

internal sealed record PortalFormDto(Guid Id, string Name, string? Description, string Department, IReadOnlyList<PortalFieldDto> Fields);

/// <summary>
/// The ONE server-side answer to "may the signed-in employee use this request form?", shared by the catalogue, form metadata and submission.
/// A form is usable when it is active, offered to requesters, and its requester audience includes the caller (every employee, or an explicit user).
/// Department membership plays no part: this grants the form and nothing else. Anything else is indistinguishable from a form that does not exist.
/// </summary>
internal sealed class PortalFormAccess(TicketingDbContext db, ICurrentUser current)
{
    public IQueryable<RequestForm> Usable()
    {
        var userId = current.UserId;
        return db.RequestForms.Where(f => f.IsActive && f.VisibleToRequesters
            && (f.Audience == FormAudiences.AllEmployees || db.RequestFormAudienceUsers.Any(a => a.FormId == f.Id && a.UserId == userId)));
    }
}

/// <summary>
/// The forms a signed-in employee may fill in: only those whose requester audience includes them (<see cref="PortalFormAccess"/>). A form that is
/// inactive, not visible to requesters or outside the audience, and any field that is inactive or not requester visible, simply does not exist here.
/// </summary>
internal sealed class PortalFormsService(PortalFormAccess access, IOrganisationDirectory organisation, FieldCatalog catalog)
{
    public async Task<IReadOnlyList<PortalFormListItemDto>> ListAsync(CancellationToken ct)
    {
        var forms = await access.Usable().AsNoTracking().OrderBy(f => f.SortOrder).ThenBy(f => f.Name).Take(500).ToListAsync(ct);
        var departments = await organisation.GetDepartmentsAsync(forms.Select(f => f.DepartmentId).Distinct().ToArray(), ct);
        return [.. forms.Where(f => departments.TryGetValue(f.DepartmentId, out var d) && d.IsActive).Select(f => new PortalFormListItemDto(f.Id, f.Name, f.Description, departments[f.DepartmentId].Name))
            .OrderBy(f => f.Department, StringComparer.OrdinalIgnoreCase)];
    }

    public async Task<PortalFormDto> GetAsync(Guid id, CancellationToken ct)
    {
        var form = await access.Usable().AsNoTracking().SingleOrDefaultAsync(f => f.Id == id, ct) ?? throw new NotFoundException();
        var department = await organisation.FindDepartmentAsync(form.DepartmentId, ct);
        if (department is not { IsActive: true })
        {
            throw new NotFoundException();
        }

        var fields = (await catalog.ForFormAsync(form.Id, form.DepartmentId, ct)).Where(b => b.Field.RequesterVisible).ToList();
        return new PortalFormDto(
            form.Id, form.Name, form.Description, department.Name,
            [.. fields.Select(b => new PortalFieldDto(b.Field.Key, b.Field.Label, b.Field.HelpText, b.Field.FieldType, b.Required,
                [.. FieldValueValidator.Options(b.Field).Where(o => o.IsActive).Select(o => new PortalOptionDto(o.Value, o.Label))]))]);
    }
}
