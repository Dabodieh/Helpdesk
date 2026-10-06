using Helpdesk.Modules.Identity.Contracts;
using Helpdesk.Modules.Organisation.Contracts;
using Helpdesk.Modules.Ticketing.Domain;
using Helpdesk.Modules.Ticketing.Persistence;
using Helpdesk.SharedKernel.Authorization;
using Helpdesk.SharedKernel.Errors;
using Helpdesk.SharedKernel.Security;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Helpdesk.Modules.Ticketing.Services;

internal sealed record FollowerDto(Guid UserId, string DisplayName, DateTimeOffset AddedAt);

/// <summary>The effective followers of a ticket and whether the caller is one of them.</summary>
internal sealed record FollowersDto(IReadOnlyList<FollowerDto> Items, bool Following);

internal sealed record FollowerCandidateDto(Guid UserId, string DisplayName, string? Email);

/// <summary>
/// Internal followers of a ticket. A follower is neither the assignee nor a CC participant (nothing here touches email) and following grants NO
/// access: every read goes through the department scope and the user's own permissions, and only users who currently can read the ticket's
/// department count as followers. Following yourself needs tickets.read (the endpoint filter); adding or removing somebody else needs tickets.update.
/// Writes are idempotent (primary key (ticket, user)) and audited only when something changed. A follower row also depends on the user's department
/// membership through a cascading foreign key, so removing the membership removes the follow.
/// </summary>
internal sealed class TicketFollowerService(
    TicketingDbContext db,
    TicketingAudit audit,
    TicketResolver resolver,
    IAuthorizer authorizer,
    ICurrentUser current,
    IOrganisationDirectory organisation,
    IUserDirectory users,
    ITicketEvents events,
    TimeProvider clock)
{
    public const int CandidateLimit = 10;

    public async Task<FollowersDto> ListAsync(string route, CancellationToken ct)
    {
        var key = await resolver.FindAsync(route, ct) ?? throw new NotFoundException();
        return await StateAsync(key, ct);
    }

    private async Task<FollowersDto> StateAsync(TicketKey key, CancellationToken ct)
    {
        var rows = await db.TicketFollowers.AsNoTracking().Where(f => f.TicketId == key.Id).OrderBy(f => f.AddedAt).ThenBy(f => f.UserId).ToListAsync(ct);
        if (rows.Count == 0)
        {
            return new FollowersDto([], false);
        }

        // Effective followers only: an active user who can read this department's tickets right now (a stale row never shows up as a follower).
        var readers = (await organisation.GetUserIdsWithPermissionAsync(key.DepartmentId, PermissionCodes.TicketsRead, ct)).ToHashSet();
        var people = await users.GetManyAsync([.. rows.Select(r => r.UserId)], ct);
        var items = rows
            .Where(r => readers.Contains(r.UserId) && people.TryGetValue(r.UserId, out var p) && p.IsActive)
            .Select(r => new FollowerDto(r.UserId, people[r.UserId].DisplayName, r.AddedAt))
            .ToList();
        return new FollowersDto(items, items.Any(i => i.UserId == current.UserId));
    }

    /// <summary>Follows as the caller (<paramref name="userId"/> null or the caller) or adds somebody else.</summary>
    public async Task<FollowersDto> AddAsync(string route, Guid? userId, CancellationToken ct)
    {
        var key = await resolver.FindAsync(route, ct) ?? throw new NotFoundException();
        var target = userId ?? current.UserId;
        var self = target == current.UserId;
        if (!self)
        {
            await authorizer.RequireAsync(PermissionCodes.TicketsUpdate, key.DepartmentId, ct);
            await RequireEligibleAsync(key.DepartmentId, target, ct);
        }

        var now = clock.GetUtcNowMicroseconds();
        int inserted;
        string reference;
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            reference = await LockAndRequireActiveAsync(key, ct);
            try
            {
                inserted = await db.Database.ExecuteSqlAsync(
                    $"""
                    INSERT INTO ticketing.ticket_followers (ticket_id, department_id, user_id, added_by_user_id, added_at)
                    VALUES ({key.Id}, {key.DepartmentId}, {target}, {current.UserId}, {now})
                    ON CONFLICT (ticket_id, user_id) DO NOTHING
                    """,
                    ct);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation)
            {
                // No membership in this department (the foreign key is the last line of defence behind the service check).
                throw new RequestValidationException("userId", "The user cannot read tickets in this department.");
            }

            if (inserted == 1)
            {
                await audit.WriteAsync(TicketAuditActions.FollowerAdded, TicketingAudit.TicketObject, key.Id, key.DepartmentId, null, new { userId = target, self }, ct);
            }

            await tx.CommitAsync(ct);
        }

        if (inserted == 1)
        {
            await events.PublishAsync(key.DepartmentId, key.Id, reference, TicketEventKinds.Updated, current.UserId, ct);
        }

        return await StateAsync(key, ct);
    }

    public async Task<FollowersDto> RemoveAsync(string route, Guid? userId, CancellationToken ct)
    {
        var key = await resolver.FindAsync(route, ct) ?? throw new NotFoundException();
        var target = userId ?? current.UserId;
        var self = target == current.UserId;
        if (!self)
        {
            await authorizer.RequireAsync(PermissionCodes.TicketsUpdate, key.DepartmentId, ct);
        }

        int deleted;
        string reference;
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            reference = await LockAndRequireActiveAsync(key, ct);
            deleted = await db.TicketFollowers.Where(f => f.TicketId == key.Id && f.UserId == target).ExecuteDeleteAsync(ct);
            if (deleted > 0)
            {
                await audit.WriteAsync(TicketAuditActions.FollowerRemoved, TicketingAudit.TicketObject, key.Id, key.DepartmentId, new { userId = target, self }, null, ct);
            }

            await tx.CommitAsync(ct);
        }

        if (deleted > 0)
        {
            await events.PublishAsync(key.DepartmentId, key.Id, reference, TicketEventKinds.Updated, current.UserId, ct);
        }

        return await StateAsync(key, ct);
    }

    /// <summary>Active department members who can read tickets and do not follow the ticket yet, matched by name or email. One small page.</summary>
    public async Task<IReadOnlyList<FollowerCandidateDto>> CandidatesAsync(string route, string? q, CancellationToken ct)
    {
        var search = q?.Trim() ?? "";
        if (search.Length > 100 || TicketValidation.HasNul(search))
        {
            throw new RequestValidationException("q", "q must be at most 100 characters.");
        }

        var key = await resolver.FindAsync(route, ct) ?? throw new NotFoundException();
        var ids = await organisation.GetUserIdsWithPermissionAsync(key.DepartmentId, PermissionCodes.TicketsRead, ct);
        var already = await db.TicketFollowers.AsNoTracking().Where(f => f.TicketId == key.Id).Select(f => f.UserId).ToListAsync(ct);
        var people = await users.GetManyAsync([.. ids.Except(already)], ct);
        return [.. people.Values
            .Where(p => p.IsActive && (search.Length == 0
                || p.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase)
                || (p.Email?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false)))
            .OrderBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Id)
            .Take(CandidateLimit)
            .Select(p => new FollowerCandidateDto(p.Id, p.DisplayName, p.Email))];
    }

    /// <summary>
    /// Merge: followers of the merged tickets who are still valid readers of the department follow the canonical ticket too (duplicates collapse, existing
    /// followers stay); the merged tickets keep none. One call inside the merge transaction; the merge audit entry carries the count.
    /// </summary>
    public async Task<int> TransferAsync(Guid[] sourceIds, Ticket canonical, CancellationToken ct)
    {
        var held = await db.TicketFollowers.Where(f => sourceIds.Contains(f.TicketId)).Select(f => f.UserId).Distinct().ToListAsync(ct);
        if (held.Count == 0)
        {
            return 0;
        }

        var readers = (await organisation.GetUserIdsWithPermissionAsync(canonical.DepartmentId, PermissionCodes.TicketsRead, ct)).ToHashSet();
        var people = await users.GetManyAsync(held, ct);
        var valid = held.Where(u => readers.Contains(u) && people.TryGetValue(u, out var p) && p.IsActive).ToArray();
        var inserted = 0;
        if (valid.Length > 0)
        {
            inserted = await db.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO ticketing.ticket_followers (ticket_id, department_id, user_id, added_by_user_id, added_at)
                SELECT {canonical.Id}, department_id, user_id, added_by_user_id, added_at FROM ticketing.ticket_followers
                WHERE ticket_id = ANY({sourceIds}) AND user_id = ANY({valid})
                ON CONFLICT (ticket_id, user_id) DO NOTHING
                """,
                ct);
        }

        await db.TicketFollowers.Where(f => sourceIds.Contains(f.TicketId)).ExecuteDeleteAsync(ct);
        return inserted;
    }

    /// <summary>The target must be an active user who can read this department's tickets (rejected server-side whatever the UI offered).</summary>
    private async Task RequireEligibleAsync(Guid departmentId, Guid userId, CancellationToken ct)
    {
        var readers = await organisation.GetUserIdsWithPermissionAsync(departmentId, PermissionCodes.TicketsRead, ct);
        var user = readers.Contains(userId) ? await users.FindAsync(userId, ct) : null;
        if (user is not { IsActive: true })
        {
            throw new RequestValidationException("userId", "The user cannot read tickets in this department.");
        }
    }

    /// <summary>Serialises with merges of the same ticket; a merged ticket has no actionable follow control. Returns the reference.</summary>
    private async Task<string> LockAndRequireActiveAsync(TicketKey key, CancellationToken ct)
    {
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM ticketing.tickets WHERE id = {key.Id} FOR UPDATE", ct);
        var row = await db.Tickets.AsNoTracking().Where(t => t.Id == key.Id).Select(t => new { t.Reference, t.MergedIntoTicketId }).SingleOrDefaultAsync(ct)
            ?? throw new NotFoundException();
        return row.MergedIntoTicketId is not null ? throw TicketService.MergedConflict() : row.Reference;
    }
}
