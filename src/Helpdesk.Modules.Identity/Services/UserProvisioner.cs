using Helpdesk.Modules.Audit.Contracts;
using Helpdesk.Modules.Identity.Authentication;
using Helpdesk.Modules.Identity.Contracts;
using Helpdesk.Modules.Identity.Domain;
using Helpdesk.Modules.Identity.Persistence;
using Helpdesk.SharedKernel.Database;
using Helpdesk.SharedKernel.Errors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Helpdesk.Modules.Identity.Services;

internal sealed class UserProvisioner(
    IdentityDbContext db,
    IAuditWriter audit,
    TimeProvider clock,
    IOptions<HelpdeskAuthenticationOptions> options) : IUserProvisioner
{
    private const int MaxName = 200;
    private const int MaxEmail = 320;
    private const int MaxSubject = 200;
    private const int MaxTenant = 64;

    public async Task<ProvisionedUser> ProvisionAsync(ExternalIdentityClaims identity, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (string.IsNullOrWhiteSpace(identity.Provider)
            || string.IsNullOrWhiteSpace(identity.IssuerTenant) || identity.IssuerTenant.Length > MaxTenant
            || string.IsNullOrWhiteSpace(identity.Subject) || identity.Subject.Length > MaxSubject)
        {
            throw new RequestValidationException("identity", "Provider, tenant and subject are required.");
        }

        try
        {
            return await ProvisionOnceAsync(identity, cancellationToken);
        }
        catch (DbUpdateException ex) when (PostgresErrors.IsUniqueViolation(ex, out _))
        {
            // Two first sign-ins raced; the other transaction created the identity. Re-read it.
            db.ChangeTracker.Clear();
            return await ProvisionOnceAsync(identity, cancellationToken);
        }
    }

    private async Task<ProvisionedUser> ProvisionOnceAsync(ExternalIdentityClaims claims, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var now = clock.GetUtcNow();
        var name = Truncate(string.IsNullOrWhiteSpace(claims.DisplayName) ? claims.Subject : claims.DisplayName.Trim(), MaxName);
        var email = string.IsNullOrWhiteSpace(claims.Email) ? null : Truncate(claims.Email.Trim(), MaxEmail);

        var external = await db.ExternalIdentities.Include(i => i.User).SingleOrDefaultAsync(
            i => i.Provider == claims.Provider && i.IssuerTenant == claims.IssuerTenant && i.Subject == claims.Subject, ct);

        var isNew = external is null;
        User user;
        if (external is null)
        {
            user = new User { Id = Guid.CreateVersion7(), DisplayName = name, Email = email, IsActive = true, CreatedAt = now, UpdatedAt = now, LastSignInAt = now };
            db.Users.Add(user);
            db.ExternalIdentities.Add(new ExternalIdentity
            {
                Id = Guid.CreateVersion7(),
                UserId = user.Id,
                Provider = claims.Provider,
                IssuerTenant = claims.IssuerTenant,
                Subject = claims.Subject,
                CreatedAt = now,
            });
        }
        else
        {
            user = external.User;
            user.DisplayName = name;
            user.Email = email;
            user.LastSignInAt = now;
            user.UpdatedAt = now;
        }

        var bootstrapped = false;
        if (IsBootstrapSubject(claims.Subject) && !user.IsPlatformAdmin
            && (isNew || !await db.Users.AnyAsync(u => u.IsPlatformAdmin && u.IsActive, ct)))
        {
            user.IsPlatformAdmin = true;
            bootstrapped = true;
        }

        await db.SaveChangesAsync(ct);

        if (isNew)
        {
            await audit.WriteAsync(new AuditEntry(
                AuditCategories.Identity, "user.provisioned", "user", user.Id, null, user.Id, user.DisplayName,
                Next: new { provider = claims.Provider }), db, ct);
        }

        if (bootstrapped)
        {
            await audit.WriteAsync(new AuditEntry(
                AuditCategories.Identity, "user.platform_admin_changed", "user", user.Id, null, null, null,
                Previous: new { isPlatformAdmin = false }, Next: new { isPlatformAdmin = true, reason = "bootstrap" },
                Source: AuditSources.Bootstrap), db, ct);
        }

        await tx.CommitAsync(ct);
        return new ProvisionedUser(user.Id, user.DisplayName, user.Email, user.IsActive, user.IsPlatformAdmin, isNew);
    }

    private bool IsBootstrapSubject(string subject) =>
        options.Value.BootstrapPlatformAdminSubjects.Any(s => string.Equals(s.Trim(), subject, StringComparison.OrdinalIgnoreCase));

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
