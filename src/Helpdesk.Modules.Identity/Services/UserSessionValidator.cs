using Helpdesk.Modules.Identity.Authentication;
using Helpdesk.Modules.Identity.Contracts;
using Helpdesk.Modules.Identity.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Helpdesk.Modules.Identity.Services;

/// <summary>The per-request session rule of the cookie middleware, for callers that hold a session open (realtime connections). See <see cref="IUserSessionValidator"/>.</summary>
internal sealed class UserSessionValidator(IdentityDbContext db, IOptions<HelpdeskAuthenticationOptions> options, TimeProvider clock) : IUserSessionValidator
{
    public async Task<bool> IsSessionValidAsync(Guid userId, int sessionVersion, DateTimeOffset issuedAt, CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow();
        var absolute = TimeSpan.FromHours(options.Value.Session.AbsoluteLifetimeHours);
        if (issuedAt > now.AddMinutes(5) || now - issuedAt > absolute)
        {
            return false;
        }

        return await db.Users.AsNoTracking().AnyAsync(u => u.Id == userId && u.IsActive && u.SessionVersion == sessionVersion, cancellationToken);
    }
}
