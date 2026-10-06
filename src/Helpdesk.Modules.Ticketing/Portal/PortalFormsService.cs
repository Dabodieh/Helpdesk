using Helpdesk.Modules.Organisation.Contracts;
using Helpdesk.Modules.Ticketing.Persistence;
using Helpdesk.SharedKernel.Errors;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Modules.Ticketing.Portal;

internal sealed record PortalFormListItemDto(Guid Id, string Name, string? Description, string Department);

internal sealed record PortalFieldDto(string Key, string Label, string? HelpText, string Type, bool Required, IReadOnlyList<PortalOptionDto> Options);

internal sealed record PortalOptionDto(string Value, string Label);

internal sealed record PortalFormDto(Guid Id, string Name, string? Description, string Department, IReadOnlyList<PortalFieldDto> Fields);

/// <summary>
/// The forms a signed-in employee may fill in. Request forms are published to every signed-in employee (an internal deployment); a form that is
/// inactive or not visible to requesters, and any field that is inactive or not requester visible, simply does not exist here.
/// </summary>
internal sealed class PortalFormsService(TicketingDbContext db, IOrganisationDirectory organisation)
{
    public async Task<IReadOnlyList<PortalFormListItemDto>> ListAsync(CancellationToken ct)
    {
        var forms = await db.RequestForms.AsNoTracking().Where(f => f.IsActive && f.VisibleToRequesters).OrderBy(f => f.SortOrder).ThenBy(f => f.Name).Take(500).ToListAsync(ct);
        var departments = await organisation.GetDepartmentsAsync(forms.Select(f => f.DepartmentId).Distinct().ToArray(), ct);
        return [.. forms.Where(f => departments.TryGetValue(f.DepartmentId, out var d) && d.IsActive).Select(f => new PortalFormListItemDto(f.Id, f.Name, f.Description, departments[f.DepartmentId].Name))
            .OrderBy(f => f.Department, StringComparer.OrdinalIgnoreCase)];
    }

    public async Task<PortalFormDto> GetAsync(Guid id, CancellationToken ct)
    {
        var form = await db.RequestForms.AsNoTracking().SingleOrDefaultAsync(f => f.Id == id && f.IsActive && f.VisibleToRequesters, ct) ?? throw new NotFoundException();
        var department = await organisation.FindDepartmentAsync(form.DepartmentId, ct);
        if (department is not { IsActive: true })
        {
            throw new NotFoundException();
        }

        var fields = await db.FormFields.AsNoTracking().Where(f => f.FormId == id && f.DepartmentId == form.DepartmentId && f.IsActive && f.RequesterVisible)
            .OrderBy(f => f.SortOrder).ThenBy(f => f.Key).ToListAsync(ct);
        return new PortalFormDto(
            form.Id, form.Name, form.Description, department.Name,
            [.. fields.Select(f => new PortalFieldDto(f.Key, f.Label, f.HelpText, f.FieldType, f.Required,
                [.. FormSubmissionValidator.Options(f).Where(o => o.IsActive).Select(o => new PortalOptionDto(o.Value, o.Label))]))]);
    }
}
