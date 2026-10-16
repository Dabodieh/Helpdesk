using Helpdesk.Modules.Organisation.Authorization;
using Helpdesk.Modules.Organisation.Persistence;
using Helpdesk.SharedKernel.Security;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Modules.Organisation.Services;

internal sealed record MeMembershipDto(Guid DepartmentId, string DepartmentKey, string DepartmentName, string Role, IReadOnlyList<string> Permissions);

internal sealed record MeDepartmentDto(Guid DepartmentId, string DepartmentKey, string DepartmentName);

internal sealed record MeDto(
    Guid Id,
    string DisplayName,
    string? Email,
    bool IsPlatformAdmin,
    IReadOnlyList<string> PlatformPermissions,
    IReadOnlyList<MeMembershipDto> Memberships,
    IReadOnlyList<MeDepartmentDto> Departments);

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
                permissions = permissions.Union(PermissionCatalogue.DepartmentPermissions);
            }

            return new MeMembershipDto(d.Id, d.Key, d.Name, role, permissions.Order(StringComparer.Ordinal).ToList());
        }).ToList();

        // S-02: a platform administrator reaches every department without a membership (UI hint for the department pickers; the server authorises each request).
        var reachable = current.IsPlatformAdmin
            ? await db.Departments.AsNoTracking().OrderBy(d => d.Name).ThenBy(d => d.Id).Select(d => new MeDepartmentDto(d.Id, d.Key, d.Name)).ToListAsync(ct)
            : [];

        return new MeDto(
            current.UserId,
            current.DisplayName,
            current.Email,
            current.IsPlatformAdmin,
            current.IsPlatformAdmin ? PermissionCatalogue.PlatformPermissions : [],
            items,
            reachable);
    }
}
