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
}
