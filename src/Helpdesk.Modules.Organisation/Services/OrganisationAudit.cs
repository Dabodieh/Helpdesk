using Helpdesk.Modules.Audit.Contracts;
using Helpdesk.Modules.Organisation.Persistence;
using Helpdesk.SharedKernel.Security;

namespace Helpdesk.Modules.Organisation.Services;

/// <summary>Writes organisation-category audit events on the Organisation DbContext's current transaction.</summary>
internal sealed class OrganisationAudit(IAuditWriter writer, OrganisationDbContext db, ICurrentUser current)
{
    public Task WriteAsync(string action, string objectType, Guid objectId, Guid? departmentId, object? previous, object? next, CancellationToken ct) =>
        writer.WriteAsync(new AuditEntry(
            AuditCategories.Organisation, action, objectType, objectId, departmentId, current.UserId, current.DisplayName, previous, next), db, ct);
}
