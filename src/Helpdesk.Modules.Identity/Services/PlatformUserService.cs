using System.Globalization;
using Helpdesk.Modules.Audit.Contracts;
using Helpdesk.Modules.Identity.Persistence;
using Helpdesk.SharedKernel.Errors;
using Helpdesk.SharedKernel.Paging;
using Helpdesk.SharedKernel.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace Helpdesk.Modules.Identity.Services;

internal sealed record PlatformUserDto(Guid Id, string DisplayName, string? Email, bool IsPlatformAdmin, bool IsActive);

internal sealed record UserLookupDto(Guid Id, string DisplayName, string? Email);

internal sealed record PlatformUserPage(IReadOnlyList<PlatformUserDto> Items, string? NextCursor);

internal sealed partial class PlatformUserService(
    IdentityDbContext db,
    IAuditWriter audit,
    ICurrentUser current,
    TimeProvider clock,
    IEnumerable<IUserDeactivationListener> deactivationListeners,
    IEnumerable<IAccessChangeListener> accessListeners,
    ILogger<PlatformUserService> log)
{
    public const int ReasonMaxLength = 200;

    public const int LookupMinLength = 3;
    public const int LookupMaxResults = 20;
    private const int DefaultLimit = 50;
    private const int MaxLimit = 200;

    // Arbitrary constant used to serialise platform-admin changes so the "last platform admin" rule cannot be raced.
    private const long PlatformAdminLockKey = 0x4844_504C_4144_4D01;

    public async Task<PlatformUserPage> ListAsync(string? search, string? cursor, int? limit, CancellationToken ct)
    {
        var take = limit ?? DefaultLimit;
        if (take is < 1 or > MaxLimit)
        {
            throw new RequestValidationException("limit", $"limit must be between 1 and {MaxLimit}.");
        }

        var query = db.Users.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = LikePattern(search.Trim());
            query = query.Where(u => EF.Functions.ILike(u.DisplayName, pattern, "\\") || (u.Email != null && EF.Functions.ILike(u.Email, pattern, "\\")));
        }

        if (cursor is not null)
        {
            if (!Cursor.TryDecode(cursor, out var payload) || !Guid.TryParse(payload, out var after))
            {
                throw new RequestValidationException("cursor", "Invalid cursor.");
            }

            query = query.Where(u => u.Id.CompareTo(after) > 0);
        }

        var rows = await query.OrderBy(u => u.Id).Take(take + 1)
            .Select(u => new PlatformUserDto(u.Id, u.DisplayName, u.Email, u.IsPlatformAdmin, u.IsActive))
            .ToListAsync(ct);
        string? next = null;
        if (rows.Count > take)
        {
            rows.RemoveAt(rows.Count - 1);
            next = Cursor.Encode(rows[^1].Id.ToString());
        }

        return new PlatformUserPage(rows, next);
    }

    public async Task<IReadOnlyList<UserLookupDto>> LookupAsync(string? q, CancellationToken ct)
    {
        var term = q?.Trim() ?? "";
        if (term.Length < LookupMinLength)
        {
            throw new RequestValidationException("q", $"q must be at least {LookupMinLength} characters.");
        }

        var pattern = LikePattern(term);
        return await db.Users.AsNoTracking()
            .Where(u => u.IsActive && (EF.Functions.ILike(u.DisplayName, pattern, "\\") || (u.Email != null && EF.Functions.ILike(u.Email, pattern, "\\"))))
            .OrderBy(u => u.DisplayName).ThenBy(u => u.Id)
            .Take(LookupMaxResults)
            .Select(u => new UserLookupDto(u.Id, u.DisplayName, u.Email))
            .ToListAsync(ct);
    }

    public async Task<PlatformUserDto> SetPlatformAdminAsync(Guid userId, bool value, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({PlatformAdminLockKey})", ct);

        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId, ct) ?? throw new NotFoundException();
        if (user.IsPlatformAdmin != value)
        {
            if (!value && user.IsActive)
            {
                var otherActiveAdmins = await db.Users.CountAsync(u => u.IsPlatformAdmin && u.IsActive && u.Id != userId, ct);
                if (otherActiveAdmins == 0)
                {
                    throw new ConflictException("The last platform administrator cannot be removed.", ProblemCodes.LastAdmin);
                }
            }

            user.IsPlatformAdmin = value;
            user.UpdatedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(new AuditEntry(
                AuditCategories.Identity, "user.platform_admin_changed", "user", user.Id, null, current.UserId, current.DisplayName,
                Previous: new { isPlatformAdmin = !value }, Next: new { isPlatformAdmin = value, selfChange = current.UserId == user.Id }), db, ct);
        }

        await tx.CommitAsync(ct);
        return new PlatformUserDto(user.Id, user.DisplayName, user.Email, user.IsPlatformAdmin, user.IsActive);
    }

    /// <summary>
    /// Deactivates or reactivates an account (platform administrators only; the caller's permission is declared on the endpoint). Deactivation: refuses the caller's own
    /// account and the last active platform administrator; increments the session version (every session issued so far is revoked for good, even after reactivation);
    /// releases the user's assigned tickets in the same transaction (listeners); audits. Reactivation: explicit and audited; old sessions stay revoked, the person
    /// signs in again. Memberships, tickets, messages, audit history and requester records are never changed. Repeating the current state is a no-op.
    /// </summary>
    public async Task<PlatformUserDto> SetActiveAsync(Guid userId, bool value, string? reason, CancellationToken ct)
    {
        reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        if (reason is { Length: > ReasonMaxLength })
        {
            throw new RequestValidationException("reason", $"reason must be at most {ReasonMaxLength} characters.");
        }

        if (!value && userId == current.UserId)
        {
            throw new ConflictException("You cannot deactivate your own account.", ProblemCodes.SelfDeactivation);
        }

        bool changed;
        PlatformUserDto result;
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({PlatformAdminLockKey})", ct);
            var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId, ct) ?? throw new NotFoundException();
            changed = user.IsActive != value;
            if (changed)
            {
                if (!value && user.IsPlatformAdmin)
                {
                    var otherActiveAdmins = await db.Users.CountAsync(u => u.IsPlatformAdmin && u.IsActive && u.Id != userId, ct);
                    if (otherActiveAdmins == 0)
                    {
                        throw new ConflictException("The last active platform administrator cannot be deactivated.", ProblemCodes.LastAdmin);
                    }
                }

                user.IsActive = value;
                user.SessionVersion++;
                user.UpdatedAt = clock.GetUtcNow();
                await db.SaveChangesAsync(ct);
                await audit.WriteAsync(new AuditEntry(
                    AuditCategories.Identity, value ? "user.reactivated" : "user.deactivated", "user", user.Id, null, current.UserId, current.DisplayName,
                    Previous: new { isActive = !value },
                    Next: new { isActive = value, sessionsRevoked = true, sessionVersion = user.SessionVersion, reason }), db, ct);

                if (!value)
                {
                    var dbTransaction = tx.GetDbTransaction();
                    foreach (var listener in deactivationListeners)
                    {
                        await listener.UserDeactivatingAsync(userId, UserAssignmentEndReasons.UserDeactivated, dbTransaction, ct);
                    }
                }
            }

            await tx.CommitAsync(ct);
            result = new PlatformUserDto(user.Id, user.DisplayName, user.Email, user.IsPlatformAdmin, user.IsActive);
        }

        if (changed && !value)
        {
            // After commit, best effort: drop the user's realtime groups at once instead of at the next periodic re-check.
            foreach (var listener in deactivationListeners)
            {
                await Safely(() => listener.UserDeactivatedAsync(userId, ct), listener.GetType().Name);
            }

            foreach (var listener in accessListeners)
            {
                await Safely(() => listener.AccessChangedAsync(userId, ct), listener.GetType().Name);
            }
        }

        return result;
    }

    private async Task Safely(Func<Task> action, string name)
    {
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            FollowUpFailed(log, name, ex.GetType().Name);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Follow-up after user deactivation failed in {Listener} ({Error})")]
    private static partial void FollowUpFailed(ILogger logger, string listener, string error);

    private static string LikePattern(string term) =>
        string.Create(CultureInfo.InvariantCulture, $"%{term.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal)}%");
}
