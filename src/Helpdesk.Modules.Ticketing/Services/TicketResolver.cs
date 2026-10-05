using Helpdesk.Modules.Ticketing.Persistence;
using Helpdesk.SharedKernel.Authorization;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Modules.Ticketing.Services;

internal sealed record TicketKey(Guid Id, Guid DepartmentId);

/// <summary>
/// Resolves the <c>{ticket}</c> route value (GUID or reference such as <c>it-1042</c>) through the caller's department-scoped
/// set. "Does not exist" and "not visible" are the same null, hence the same 404. Used by the resource-permission endpoint filter
/// (to authorise against the ticket's persisted department) and by the services; results are cached per request.
/// </summary>
internal sealed class TicketResolver(TicketingDbContext db, IDepartmentScope scope) : IResourceDepartmentResolver
{
    private readonly Dictionary<string, TicketKey?> _cache = new(StringComparer.Ordinal);

    public async Task<TicketKey?> FindAsync(string routeValue, CancellationToken ct)
    {
        if (_cache.TryGetValue(routeValue, out var cached))
        {
            return cached;
        }

        await scope.EnsureLoadedAsync(ct);
        TicketKey? key = null;
        if (Guid.TryParse(routeValue, out var id))
        {
            key = await db.Tickets.AsNoTracking().Where(t => t.Id == id).Select(t => new TicketKey(t.Id, t.DepartmentId)).SingleOrDefaultAsync(ct);
        }
        else if (TicketValidation.IsReference(routeValue))
        {
            var reference = routeValue.ToUpperInvariant();
            key = await db.Tickets.AsNoTracking().Where(t => t.Reference == reference).Select(t => new TicketKey(t.Id, t.DepartmentId)).SingleOrDefaultAsync(ct);
        }

        _cache[routeValue] = key;
        return key;
    }

    async Task<Guid?> IResourceDepartmentResolver.ResolveDepartmentAsync(string routeValue, CancellationToken cancellationToken) =>
        (await FindAsync(routeValue, cancellationToken))?.DepartmentId;
}
