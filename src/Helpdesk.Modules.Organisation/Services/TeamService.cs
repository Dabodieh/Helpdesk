using Helpdesk.Modules.Identity.Contracts;
using Helpdesk.Modules.Organisation.Domain;
using Helpdesk.Modules.Organisation.Persistence;
using Helpdesk.SharedKernel.Database;
using Helpdesk.SharedKernel.Errors;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Modules.Organisation.Services;

internal sealed record TeamDto(Guid Id, string Name, string? Description, bool IsActive, int MemberCount, string Version);

internal sealed record CreateTeamRequest(string? Name, string? Description);

internal sealed record UpdateTeamRequest(string? Version, string? Name, string? Description, bool? IsActive);

internal sealed record TeamMemberDto(Guid UserId, string DisplayName, string? Email);

internal sealed class TeamService(OrganisationDbContext db, OrganisationAudit audit, IUserDirectory users, TimeProvider clock)
{
    public async Task<IReadOnlyList<TeamDto>> ListAsync(Guid departmentId, CancellationToken ct)
    {
        var teams = await db.Teams.AsNoTracking().Where(t => t.DepartmentId == departmentId).OrderBy(t => t.Name).ThenBy(t => t.Id).ToListAsync(ct);
        var counts = await db.TeamMemberships.AsNoTracking().Where(m => m.DepartmentId == departmentId)
            .GroupBy(m => m.TeamId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        return teams.Select(t => ToDto(t, counts.GetValueOrDefault(t.Id))).ToList();
    }

    public async Task<TeamDto> GetAsync(Guid departmentId, Guid teamId, CancellationToken ct)
    {
        var team = await FindAsync(departmentId, teamId, track: false, ct);
        var count = await db.TeamMemberships.CountAsync(m => m.TeamId == teamId && m.DepartmentId == departmentId, ct);
        return ToDto(team, count);
    }

    public async Task<TeamDto> CreateAsync(Guid departmentId, CreateTeamRequest request, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        Validation.Name(errors, "name", request.Name);
        Validation.Description(errors, request.Description);
        Validation.ThrowIfAny(errors);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var department = await db.Departments.AsNoTracking().SingleOrDefaultAsync(d => d.Id == departmentId, ct) ?? throw new NotFoundException();
        if (!department.IsActive)
        {
            throw new ConflictException("The department is inactive.");
        }

        var now = clock.GetUtcNow();
        var team = new Team
        {
            Id = Guid.CreateVersion7(),
            DepartmentId = departmentId,
            Name = request.Name!.Trim(),
            Description = Validation.NormaliseDescription(request.Description),
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Teams.Add(team);
        await SaveAsync(ct);
        await audit.WriteAsync("team.created", "team", team.Id, departmentId, null,
            new { name = team.Name, description = team.Description, isActive = true }, ct);
        await tx.CommitAsync(ct);
        return ToDto(team, 0);
    }

    public async Task<TeamDto> UpdateAsync(Guid departmentId, Guid teamId, UpdateTeamRequest request, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        var version = Validation.Version(errors, request.Version);
        if (request.Name is not null)
        {
            Validation.Name(errors, "name", request.Name);
        }

        Validation.Description(errors, request.Description);
        if (request.Name is null && request.Description is null && request.IsActive is null)
        {
            errors["body"] = ["Provide at least one of name, description, isActive."];
        }

        Validation.ThrowIfAny(errors);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var team = await FindAsync(departmentId, teamId, track: true, ct);
        if (team.Version != version)
        {
            throw new ConflictException("The team was changed by someone else. Reload and retry.");
        }

        db.Entry(team).Property(t => t.Version).OriginalValue = version;

        var previous = new Dictionary<string, object?>();
        var next = new Dictionary<string, object?>();
        var newName = request.Name?.Trim();
        if (newName is not null && newName != team.Name)
        {
            previous["name"] = team.Name;
            next["name"] = newName;
            team.Name = newName;
        }

        if (request.Description is not null)
        {
            var newDescription = Validation.NormaliseDescription(request.Description);
            if (newDescription != team.Description)
            {
                previous["description"] = team.Description;
                next["description"] = newDescription;
                team.Description = newDescription;
            }
        }

        var wasActive = team.IsActive;
        var activeChanged = request.IsActive is { } active && active != wasActive;
        if (activeChanged)
        {
            team.IsActive = request.IsActive!.Value;
        }

        if (previous.Count > 0 || activeChanged)
        {
            team.UpdatedAt = clock.GetUtcNow();
            await SaveAsync(ct);
            if (previous.Count > 0)
            {
                await audit.WriteAsync("team.updated", "team", team.Id, departmentId, previous, next, ct);
            }

            if (activeChanged)
            {
                await audit.WriteAsync(team.IsActive ? "team.activated" : "team.deactivated", "team", team.Id, departmentId,
                    new { isActive = wasActive }, new { isActive = team.IsActive }, ct);
            }
        }

        await tx.CommitAsync(ct);
        var count = await db.TeamMemberships.CountAsync(m => m.TeamId == teamId && m.DepartmentId == departmentId, ct);
        return ToDto(team, count);
    }

    public async Task<IReadOnlyList<TeamMemberDto>> MembersAsync(Guid departmentId, Guid teamId, CancellationToken ct)
    {
        await FindAsync(departmentId, teamId, track: false, ct);
        var ids = await db.TeamMemberships.AsNoTracking().Where(m => m.TeamId == teamId && m.DepartmentId == departmentId)
            .Select(m => m.UserId).ToListAsync(ct);
        var directory = await users.GetManyAsync(ids, ct);
        return ids.Where(directory.ContainsKey)
            .Select(id => new TeamMemberDto(id, directory[id].DisplayName, directory[id].Email))
            .OrderBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase).ThenBy(m => m.UserId).ToList();
    }

    public async Task AddMemberAsync(Guid departmentId, Guid teamId, Guid userId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var team = await FindAsync(departmentId, teamId, track: false, ct);
        if (!team.IsActive)
        {
            throw new ConflictException("The team is inactive.");
        }

        if (await db.TeamMemberships.AnyAsync(m => m.TeamId == teamId && m.UserId == userId, ct))
        {
            return; // idempotent
        }

        // Share-lock the department membership so it cannot be removed concurrently while we add the team membership.
        await db.Database.ExecuteSqlAsync(
            $"SELECT 1 FROM organisation.department_memberships WHERE user_id = {userId} AND department_id = {departmentId} FOR SHARE", ct);
        var isMember = await db.DepartmentMemberships.AnyAsync(m => m.UserId == userId && m.DepartmentId == departmentId, ct);
        if (!isMember)
        {
            throw new ConflictException("The user must be a member of the department first.");
        }

        db.TeamMemberships.Add(new TeamMembership { TeamId = teamId, UserId = userId, DepartmentId = departmentId, CreatedAt = clock.GetUtcNow() });
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("team_membership.added", "team_membership", teamId, departmentId, null, new { teamId, userId }, ct);
        await tx.CommitAsync(ct);
    }

    public async Task RemoveMemberAsync(Guid departmentId, Guid teamId, Guid userId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await FindAsync(departmentId, teamId, track: false, ct);
        var membership = await db.TeamMemberships.SingleOrDefaultAsync(m => m.TeamId == teamId && m.UserId == userId && m.DepartmentId == departmentId, ct)
            ?? throw new NotFoundException();
        db.TeamMemberships.Remove(membership);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("team_membership.removed", "team_membership", teamId, departmentId, new { teamId, userId }, null, ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>Teams are always resolved by both ids: a team of another department is simply not found.</summary>
    private async Task<Team> FindAsync(Guid departmentId, Guid teamId, bool track, CancellationToken ct)
    {
        var query = track ? db.Teams.AsQueryable() : db.Teams.AsNoTracking();
        return await query.SingleOrDefaultAsync(t => t.Id == teamId && t.DepartmentId == departmentId, ct) ?? throw new NotFoundException();
    }

    private async Task SaveAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictException("The team was changed by someone else. Reload and retry.");
        }
        catch (DbUpdateException ex) when (PostgresErrors.IsUniqueViolation(ex, out _))
        {
            throw new ConflictException("A team with this name already exists in the department.");
        }
    }

    private static TeamDto ToDto(Team t, int memberCount) =>
        new(t.Id, t.Name, t.Description, t.IsActive, memberCount, ConcurrencyVersion.ToToken(t.Version));
}
