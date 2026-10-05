using Helpdesk.Modules.Organisation.Persistence;
using Helpdesk.SharedKernel.Security;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Modules.Organisation.Authorization;

/// <summary>The current user's department roles, loaded once per request.</summary>
internal sealed class MembershipCache(OrganisationDbContext db, ICurrentUser current)
{
    private Dictionary<Guid, string>? _roles;

    public async Task<IReadOnlyDictionary<Guid, string>> GetRolesAsync(CancellationToken cancellationToken)
    {
        if (!current.IsAuthenticated)
        {
            return new Dictionary<Guid, string>();
        }

        return _roles ??= await db.DepartmentMemberships.AsNoTracking()
            .Where(m => m.UserId == current.UserId)
            .ToDictionaryAsync(m => m.DepartmentId, m => m.Role, cancellationToken);
    }

    /// <summary>Call after the current user's own memberships change within a request.</summary>
    public void Invalidate() => _roles = null;
}
