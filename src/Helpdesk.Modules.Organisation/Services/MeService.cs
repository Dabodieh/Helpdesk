using Helpdesk.Modules.Organisation.Authorization;
using Helpdesk.Modules.Organisation.Persistence;
using Helpdesk.SharedKernel.Security;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Modules.Organisation.Services;

internal sealed record MeMembershipDto(Guid DepartmentId, string DepartmentKey, string DepartmentName, string Role, IReadOnlyList<string> Permissions);

internal sealed record MeDto(
    Guid Id,
    string DisplayName,
    string? Email,
    bool IsPlatformAdmin,
    IReadOnlyList<string> PlatformPermissions,
    IReadOnlyList<MeMembershipDto> Memberships);

internal sealed class MeService(OrganisationDbContext db, ICurrentUser current, MembershipCache memberships)
{
    public async Task<MeDto> GetAsync(CancellationToken ct)
    {
        var roles = await memberships.GetRolesAsync(ct);
        var ids = roles.Keys.ToArray();
        var departments = await db.Departments.AsNoTracking().Where(d => ids.Contains(d.Id)).OrderBy(d => d.Name).ThenBy(d => d.Id).ToListAsync(ct);

        // Informational only (UI hints): role grants, plus administrative permissions for platform admins. The server enforces independently.
        var items = departments.Select(d =>
        {
            var role = roles[d.Id];
            var permissions = RoleCatalogue.PermissionsOf(role).AsEnumerable();
            if (current.IsPlatformAdmin)
            {
                permissions = permissions.Union(PermissionCatalogue.AdministrativePermissions);
            }

            return new MeMembershipDto(d.Id, d.Key, d.Name, role, permissions.Order(StringComparer.Ordinal).ToList());
        }).ToList();

        return new MeDto(
            current.UserId,
            current.DisplayName,
            current.Email,
            current.IsPlatformAdmin,
            current.IsPlatformAdmin ? PermissionCatalogue.PlatformPermissions : [],
            items);
    }
}
