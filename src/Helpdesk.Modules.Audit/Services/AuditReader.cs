using Helpdesk.Modules.Audit.Contracts;
using Helpdesk.Modules.Audit.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Modules.Audit.Services;

internal sealed class AuditReader(AuditDbContext db) : IAuditReader
{
    public async Task<AuditRecordPage> ReadObjectEventsAsync(
        string category, string objectType, Guid objectId, Guid departmentId, string? cursor, int limit, CancellationToken cancellationToken = default)
    {
        var page = await AuditQueries.PageAsync(
            db.Events.Where(e => e.ObjectId == objectId && e.Category == category && e.ObjectType == objectType && e.DepartmentId == departmentId),
            cursor, limit, cancellationToken);
        return new AuditRecordPage(
            page.Items.Select(e => new AuditRecord(e.Id, e.OccurredAt, e.Actor?.Id, e.Actor?.DisplayName, e.Action, e.Previous, e.Next, e.Source)).ToList(),
            page.NextCursor);
    }
}
