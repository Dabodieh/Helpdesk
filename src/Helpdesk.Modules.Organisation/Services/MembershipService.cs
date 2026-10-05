using Helpdesk.Modules.Identity.Contracts;
using Helpdesk.Modules.Organisation.Authorization;
using Helpdesk.Modules.Organisation.Contracts;
using Helpdesk.Modules.Organisation.Domain;
using Helpdesk.Modules.Organisation.Persistence;
using Helpdesk.SharedKernel.Errors;
using Helpdesk.SharedKernel.Security;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Modules.Organisation.Services;

internal sealed record MemberDto(Guid UserId, string DisplayName, string? Email, string Role, IReadOnlyList<Guid> TeamIds);

internal sealed record SetRoleRequest(string? Role);

internal sealed class MembershipService(
    OrganisationDbContext db,
    OrganisationAudit audit,
    IUserDirectory users,
    ICurrentUser current,
    MembershipCache memberships,
    TimeProvider clock)
{
    public async Task<IReadOnlyList<MemberDto>> ListAsync(Guid departmentId, CancellationToken ct)
    {
        var rows = await db.DepartmentMemberships.AsNoTracking().Where(m => m.DepartmentId == departmentId).ToListAsync(ct);
        var teams = await db.TeamMemberships.AsNoTracking().Where(m => m.DepartmentId == departmentId)
            .Select(m => new { m.UserId, m.TeamId }).ToListAsync(ct);
        var directory = await users.GetManyAsync(rows.Select(r => r.UserId).ToList(), ct);
        return rows.Where(r => directory.ContainsKey(r.UserId))
            .Select(r => new MemberDto(r.UserId, directory[r.UserId].DisplayName, directory[r.UserId].Email, r.Role,
                teams.Where(t => t.UserId == r.UserId).Select(t => t.TeamId).Order().ToList()))
            .OrderBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase).ThenBy(m => m.UserId).ToList();
    }

    /// <summary>
    /// Adds a member or changes the role. Authorization (<c>department.members.manage</c> in this department, i.e. DepartmentAdmin
    /// or platform admin) is enforced by the endpoint; a DepartmentAdmin is the top department role, so no grant can exceed the
    /// caller's own, and the department id comes from the route resolved against the authorizer, never from the body.
    /// </summary>
    public async Task<MemberDto> SetRoleAsync(Guid departmentId, Guid userId, SetRoleRequest request, CancellationToken ct)
    {
        if (!RoleCatalogue.IsValid(request.Role))
        {
            throw new RequestValidationException("role", $"role must be one of {string.Join(", ", RoleCatalogue.Roles)}.");
        }

        var role = request.Role!;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var department = await LockDepartmentAsync(departmentId, ct);

        var existing = await db.DepartmentMemberships.SingleOrDefaultAsync(m => m.DepartmentId == departmentId && m.UserId == userId, ct);
        var selfGrant = userId == current.UserId;
        var now = clock.GetUtcNow();

        if (existing is null)
        {
            var user = await users.FindAsync(userId, ct) ?? throw new NotFoundException();
            if (!user.IsActive)
            {
                throw new ConflictException("The user is inactive.");
            }

            if (!department.IsActive)
            {
                throw new ConflictException("The department is inactive.");
            }

            db.DepartmentMemberships.Add(new DepartmentMembership { UserId = userId, DepartmentId = departmentId, Role = role, CreatedAt = now, UpdatedAt = now });
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync("membership.added", "department_membership", userId, departmentId, null, new { role, selfGrant }, ct);
        }
        else if (existing.Role != role)
        {
            if (existing.Role == DepartmentRoles.DepartmentAdmin)
            {
                await EnsureNotLastAdminAsync(departmentId, userId, ct);
            }

            var previous = existing.Role;
            existing.Role = role;
            existing.UpdatedAt = now;
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync("membership.role_changed", "department_membership", userId, departmentId, new { role = previous }, new { role, selfGrant }, ct);
        }

        await tx.CommitAsync(ct);
        memberships.Invalidate();

        var teamIds = await db.TeamMemberships.AsNoTracking().Where(m => m.DepartmentId == departmentId && m.UserId == userId)
            .Select(m => m.TeamId).OrderBy(id => id).ToListAsync(ct);
        var summary = await users.FindAsync(userId, ct) ?? throw new NotFoundException();
        return new MemberDto(userId, summary.DisplayName, summary.Email, role, teamIds);
    }

    public async Task RemoveAsync(Guid departmentId, Guid userId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockDepartmentAsync(departmentId, ct);

        var membership = await db.DepartmentMemberships.SingleOrDefaultAsync(m => m.DepartmentId == departmentId && m.UserId == userId, ct)
            ?? throw new NotFoundException();
        if (membership.Role == DepartmentRoles.DepartmentAdmin)
        {
            await EnsureNotLastAdminAsync(departmentId, userId, ct);
        }

        // Remove (and audit) team memberships explicitly; the composite FK cascade is only the backstop.
        var teamMemberships = await db.TeamMemberships.Where(m => m.DepartmentId == departmentId && m.UserId == userId).ToListAsync(ct);
        db.TeamMemberships.RemoveRange(teamMemberships);
        db.DepartmentMemberships.Remove(membership);
        await db.SaveChangesAsync(ct);

        foreach (var tm in teamMemberships)
        {
            await audit.WriteAsync("team_membership.removed", "team_membership", tm.TeamId, departmentId,
                new { teamId = tm.TeamId, userId }, new { reason = "department_membership_removed" }, ct);
        }

        await audit.WriteAsync("membership.removed", "department_membership", userId, departmentId, new { role = membership.Role }, null, ct);
        await tx.CommitAsync(ct);
        memberships.Invalidate();
    }

    /// <summary>Serialises membership mutations per department so the last-admin rule cannot be raced.</summary>
    private async Task<Department> LockDepartmentAsync(Guid departmentId, CancellationToken ct)
    {
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM organisation.departments WHERE id = {departmentId} FOR UPDATE", ct);
        return await db.Departments.AsNoTracking().SingleOrDefaultAsync(d => d.Id == departmentId, ct) ?? throw new NotFoundException();
    }

    private async Task EnsureNotLastAdminAsync(Guid departmentId, Guid excludingUserId, CancellationToken ct)
    {
        var others = await db.DepartmentMemberships.CountAsync(
            m => m.DepartmentId == departmentId && m.Role == DepartmentRoles.DepartmentAdmin && m.UserId != excludingUserId, ct);
        if (others == 0)
        {
            throw new ConflictException("The last DepartmentAdmin of a department cannot be demoted or removed.");
        }
    }
}
