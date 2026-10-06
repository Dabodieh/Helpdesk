using Helpdesk.Modules.Ticketing.Domain;
using Helpdesk.Modules.Ticketing.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Modules.Ticketing.Fields;

/// <summary>
/// Which fields are asked where. A ticket's applicable fields are the department's common fields (<c>applies_to_all_types</c>) followed by the fields linked
/// to its type; a form asks the fields linked to the form. "Required" is the link override when there is one, otherwise the field's own default.
/// Only active fields are ever offered; every query names the department explicitly (definitions have no ambient scope, requesters have none).
/// </summary>
internal sealed class FieldCatalog(TicketingDbContext db)
{
    private const int TypeOrderBase = 1_000_000;

    public async Task<List<FieldBinding>> ForTypeAsync(Guid departmentId, Guid? typeId, CancellationToken ct)
    {
        var bindings = (await db.TicketFields.AsNoTracking().Where(f => f.DepartmentId == departmentId && f.IsActive && f.AppliesToAllTypes).ToListAsync(ct))
            .Select(f => new FieldBinding(f, f.Required, f.SortOrder)).ToList();
        if (typeId is { } type)
        {
            var linked = await db.TicketTypeFields.AsNoTracking()
                .Where(l => l.TypeId == type && l.DepartmentId == departmentId)
                .Join(db.TicketFields.AsNoTracking().Where(f => f.IsActive), l => l.FieldId, f => f.Id, (l, f) => new { Link = l, Field = f })
                .ToListAsync(ct);
            foreach (var l in linked)
            {
                var existing = bindings.FindIndex(b => b.Field.Id == l.Field.Id);
                var binding = new FieldBinding(l.Field, l.Link.Required ?? l.Field.Required, existing >= 0 ? bindings[existing].Order : TypeOrderBase + l.Link.SortOrder);
                if (existing >= 0)
                {
                    bindings[existing] = binding;
                }
                else
                {
                    bindings.Add(binding);
                }
            }
        }

        return [.. bindings.OrderBy(b => b.Order).ThenBy(b => b.Field.Key, StringComparer.Ordinal)];
    }

    public async Task<List<FieldBinding>> ForFormAsync(Guid formId, Guid departmentId, CancellationToken ct) =>
        [.. (await db.RequestFormFields.AsNoTracking()
                .Where(l => l.FormId == formId && l.DepartmentId == departmentId)
                .Join(db.TicketFields.AsNoTracking().Where(f => f.IsActive), l => l.FieldId, f => f.Id, (l, f) => new { Link = l, Field = f })
                .ToListAsync(ct))
            .Select(x => new FieldBinding(x.Field, x.Link.Required ?? x.Field.Required, x.Link.SortOrder))
            .OrderBy(b => b.Order).ThenBy(b => b.Field.Key, StringComparer.Ordinal)];
}
