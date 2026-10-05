using System.Globalization;
using Helpdesk.Modules.Audit.Contracts;
using Helpdesk.Modules.Identity.Persistence;
using Helpdesk.SharedKernel.Errors;
using Helpdesk.SharedKernel.Paging;
using Helpdesk.SharedKernel.Security;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Modules.Identity.Services;

internal sealed record PlatformUserDto(Guid Id, string DisplayName, string? Email, bool IsPlatformAdmin, bool IsActive);

internal sealed record UserLookupDto(Guid Id, string DisplayName, string? Email);

internal sealed record PlatformUserPage(IReadOnlyList<PlatformUserDto> Items, string? NextCursor);

internal sealed class PlatformUserService(IdentityDbContext db, IAuditWriter audit, ICurrentUser current, TimeProvider clock)
{
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
                    throw new ConflictException("The last platform administrator cannot be removed.");
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

    private static string LikePattern(string term) =>
        string.Create(CultureInfo.InvariantCulture, $"%{term.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal)}%");
}
