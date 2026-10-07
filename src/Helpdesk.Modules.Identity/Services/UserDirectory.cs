using Helpdesk.Modules.Identity.Contracts;
using Helpdesk.Modules.Identity.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Modules.Identity.Services;

internal sealed class UserDirectory(IdentityDbContext db) : IUserDirectory
{
    public Task<UserSummary?> FindAsync(Guid userId, CancellationToken cancellationToken = default) =>
        db.Users.AsNoTracking().Where(u => u.Id == userId)
            .Select(u => new UserSummary(u.Id, u.DisplayName, u.Email, u.IsActive, u.IsPlatformAdmin))
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, UserSummary>> GetManyAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken = default)
    {
        if (userIds.Count == 0)
        {
            return new Dictionary<Guid, UserSummary>();
        }

        var ids = userIds.Distinct().ToArray();
        return await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id))
            .Select(u => new UserSummary(u.Id, u.DisplayName, u.Email, u.IsActive, u.IsPlatformAdmin))
            .ToDictionaryAsync(u => u.Id, cancellationToken);
    }

    public async Task<IReadOnlyDictionary<string, UserSummary>> FindActiveByEmailsAsync(IReadOnlyCollection<string> normalisedEmails, CancellationToken cancellationToken = default)
    {
        if (normalisedEmails.Count == 0)
        {
            return new Dictionary<string, UserSummary>();
        }

        var emails = normalisedEmails.Distinct().ToArray();
#pragma warning disable CA1304, CA1311 // translated to SQL lower(); the culture-sensitive overload is never executed in .NET
        var found = await db.Users.AsNoTracking().Where(u => u.IsActive && u.Email != null && emails.Contains(u.Email.ToLower()))
            .Select(u => new UserSummary(u.Id, u.DisplayName, u.Email, u.IsActive, u.IsPlatformAdmin)).ToListAsync(cancellationToken);
#pragma warning restore CA1304, CA1311
        return found.GroupBy(u => u.Email!.ToLowerInvariant()).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single());
    }
}
