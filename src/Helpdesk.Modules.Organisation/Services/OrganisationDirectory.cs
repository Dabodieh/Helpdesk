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

    public Task<TeamInfo?> FindTeamAsync(Guid departmentId, Guid teamId, CancellationToken cancellationToken = default) =>
        db.Teams.AsNoTracking().Where(t => t.Id == teamId && t.DepartmentId == departmentId)
            .Select(t => new TeamInfo(t.Id, t.DepartmentId, t.Name, t.IsActive))
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, TeamInfo>> GetTeamsAsync(IReadOnlyCollection<Guid> teamIds, CancellationToken cancellationToken = default)
    {
        if (teamIds.Count == 0)
        {
            return new Dictionary<Guid, TeamInfo>();
        }

        var ids = teamIds.Distinct().ToArray();
        return await db.Teams.AsNoTracking().Where(t => ids.Contains(t.Id))
            .Select(t => new TeamInfo(t.Id, t.DepartmentId, t.Name, t.IsActive))
            .ToDictionaryAsync(t => t.Id, cancellationToken);
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

    public async Task<IReadOnlyCollection<Guid>> GetTeamIdsOfUserAsync(Guid userId, IReadOnlyCollection<Guid> departmentIds, CancellationToken cancellationToken = default)
    {
        if (departmentIds.Count == 0)
        {
            return [];
        }

        var ids = departmentIds.Distinct().ToArray();
        return await db.TeamMemberships.AsNoTracking()
            .Where(m => m.UserId == userId && ids.Contains(m.DepartmentId))
            .Select(m => m.TeamId)
            .ToListAsync(cancellationToken);
    }
}
