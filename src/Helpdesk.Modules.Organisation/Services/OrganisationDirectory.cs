using Helpdesk.Modules.Identity.Contracts;
using Helpdesk.Modules.Organisation.Authorization;
using Helpdesk.Modules.Organisation.Contracts;
using Helpdesk.Modules.Organisation.Persistence;
using Helpdesk.SharedKernel.Authorization;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Modules.Organisation.Services;

internal sealed class OrganisationDirectory(OrganisationDbContext db, IUserDirectory users) : IOrganisationDirectory
{
    public Task<DepartmentInfo?> FindDepartmentAsync(Guid departmentId, CancellationToken cancellationToken = default) =>
        db.Departments.AsNoTracking().Where(d => d.Id == departmentId)
            .Select(d => new DepartmentInfo(d.Id, d.Key, d.Name, d.IsActive))
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, DepartmentInfo>> GetDepartmentsAsync(IReadOnlyCollection<Guid> departmentIds, CancellationToken cancellationToken = default)
    {
        if (departmentIds.Count == 0)
        {
            return new Dictionary<Guid, DepartmentInfo>();
        }

        var ids = departmentIds.Distinct().ToArray();
        return await db.Departments.AsNoTracking().Where(d => ids.Contains(d.Id))
            .Select(d => new DepartmentInfo(d.Id, d.Key, d.Name, d.IsActive))
            .ToDictionaryAsync(d => d.Id, cancellationToken);
    }

    public async Task<bool> IsEligibleAssigneeAsync(Guid departmentId, Guid userId, CancellationToken cancellationToken = default)
    {
        var role = await db.DepartmentMemberships.AsNoTracking()
            .Where(m => m.DepartmentId == departmentId && m.UserId == userId)
            .Select(m => m.Role)
            .SingleOrDefaultAsync(cancellationToken);
        if (role is null || !RoleCatalogue.RoleGrants(role, PermissionCodes.TicketsUpdate))
        {
            return false;
        }

        var user = await users.FindAsync(userId, cancellationToken);
        return user is { IsActive: true };
    }

    public async Task<IReadOnlyCollection<Guid>> GetDepartmentsWithPermissionAsync(Guid userId, string permission, CancellationToken cancellationToken = default)
    {
        var user = await users.FindAsync(userId, cancellationToken);
        if (user is not { IsActive: true })
        {
            return [];
        }

        var rows = await db.DepartmentMemberships.AsNoTracking().Where(m => m.UserId == userId)
            .Select(m => new { m.DepartmentId, m.Role }).ToListAsync(cancellationToken);
        return [.. rows.Where(r => RoleCatalogue.RoleGrants(r.Role, permission)).Select(r => r.DepartmentId)];
    }

    public async Task<IReadOnlyCollection<Guid>> GetUserIdsWithPermissionAsync(Guid departmentId, string permission, CancellationToken cancellationToken = default)
    {
        var rows = await db.DepartmentMemberships.AsNoTracking().Where(m => m.DepartmentId == departmentId)
            .Select(m => new { m.UserId, m.Role }).ToListAsync(cancellationToken);
        return [.. rows.Where(r => RoleCatalogue.RoleGrants(r.Role, permission)).Select(r => r.UserId)];
    }
}
