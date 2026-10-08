using System.Xml.Linq;
using Helpdesk.Modules.Identity.Domain;
using Helpdesk.Modules.Identity.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Helpdesk.Modules.Identity.Authentication;

/// <summary>
/// The ASP.NET Data Protection key ring lives in PostgreSQL (<c>identity.data_protection_keys</c>, decision D2) so sessions, antiforgery and OIDC correlation cookies survive
/// a restart or a replaced container, and so the keys are restored with the database backup. The application name is fixed: every instance and every image shares one ring.
/// </summary>
internal static class DataProtectionKeyStore
{
    public const string ApplicationName = "Helpdesk";

    public static void Add(IServiceCollection services)
    {
        services.AddDataProtection().SetApplicationName(ApplicationName);
        services.AddSingleton<IConfigureOptions<KeyManagementOptions>>(sp =>
            new ConfigureOptions<KeyManagementOptions>(o => o.XmlRepository = new PostgresXmlRepository(sp.GetRequiredService<IServiceScopeFactory>())));
    }
}

internal sealed class PostgresXmlRepository(IServiceScopeFactory scopes) : IXmlRepository
{
    public IReadOnlyCollection<XElement> GetAllElements()
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        return [.. db.DataProtectionKeys.AsNoTracking().OrderBy(k => k.CreatedAt).ThenBy(k => k.FriendlyName).Select(k => k.Xml).AsEnumerable().Select(XElement.Parse)];
    }

    public void StoreElement(XElement element, string friendlyName)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        // A name is written once; a second writer with the same name (two instances racing) keeps the first key.
        if (!db.DataProtectionKeys.Any(k => k.FriendlyName == friendlyName))
        {
            db.DataProtectionKeys.Add(new DataProtectionKeyRow { FriendlyName = friendlyName, Xml = element.ToString(SaveOptions.DisableFormatting), CreatedAt = DateTimeOffset.UtcNow });
            try
            {
                db.SaveChanges();
            }
            catch (DbUpdateException)
            {
                // Lost the race: the winner's key is the one in the ring.
            }
        }
    }
}
