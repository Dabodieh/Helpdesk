using System.Security.Claims;
using Helpdesk.Modules.Identity.Authentication;
using Helpdesk.Modules.Identity.Persistence;
using Helpdesk.SharedKernel.Security;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Modules.Identity.Services;

/// <summary>
/// Request-scoped current user. Loaded from the database once per request by the cookie validation event, which also
/// enforces that the user still exists and is active (so deactivation and platform-admin revocation take effect immediately).
/// </summary>
internal sealed class CurrentUser(IdentityDbContext db) : ICurrentUser
{
    private Snapshot? _snapshot;

    public bool IsAuthenticated => _snapshot is not null;

    public Guid UserId => (_snapshot ?? throw NotAuthenticated()).Id;

    public string DisplayName => (_snapshot ?? throw NotAuthenticated()).DisplayName;

    public string? Email => (_snapshot ?? throw NotAuthenticated()).Email;

    public bool IsPlatformAdmin => (_snapshot ?? throw NotAuthenticated()).IsPlatformAdmin;

    /// <summary>Loads the user for the principal. Returns false (and stays unauthenticated) when unknown or inactive.</summary>
    public async Task<bool> LoadAsync(ClaimsPrincipal? principal, CancellationToken cancellationToken)
    {
        if (!HelpdeskPrincipal.TryGetUserId(principal, out var id))
        {
            return false;
        }

        _snapshot = await db.Users.AsNoTracking()
            .Where(u => u.Id == id && u.IsActive)
            .Select(u => new Snapshot(u.Id, u.DisplayName, u.Email, u.IsPlatformAdmin))
            .SingleOrDefaultAsync(cancellationToken);
        return _snapshot is not null;
    }

    private static InvalidOperationException NotAuthenticated() => new("There is no authenticated user for this request.");

    private sealed record Snapshot(Guid Id, string DisplayName, string? Email, bool IsPlatformAdmin);
}
