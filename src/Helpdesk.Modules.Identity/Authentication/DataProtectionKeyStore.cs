using System.Security.Cryptography.X509Certificates;
using System.Xml.Linq;
using Helpdesk.Modules.Identity.Domain;
using Helpdesk.Modules.Identity.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
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

    public static void Add(IServiceCollection services, IConfiguration configuration)
    {
        var builder = services.AddDataProtection().SetApplicationName(ApplicationName);
        ProtectKeysWithConfiguredCertificate(builder, configuration);
        services.AddSingleton<IConfigureOptions<KeyManagementOptions>>(sp =>
            new ConfigureOptions<KeyManagementOptions>(o => o.XmlRepository = new PostgresXmlRepository(sp.GetRequiredService<IServiceScopeFactory>())));
    }

    /// <summary>
    /// Optional encryption of the key ring at rest (production gate F-04). With <c>DataProtection:CertificatePath</c> set, NEW keys are written encrypted with that
    /// certificate's public key; keys already in the ring (plain or encrypted with <c>DataProtection:PreviousCertificatePaths</c>) stay readable, so nothing is
    /// destroyed and rotation is: add the new certificate, move the old one to the previous list, remove it later. The private key never lives in the repository, the
    /// image or the database: the certificate is a PFX file the operator mounts read-only; its password comes from <c>DataProtection:CertificatePasswordFile</c> (a
    /// mounted secret file, preferred) or <c>DataProtection:CertificatePassword</c>. A configured certificate that cannot be used stops the start (no silent
    /// fallback to plain keys), and no message names a password.
    /// </summary>
    internal static void ProtectKeysWithConfiguredCertificate(IDataProtectionBuilder builder, IConfiguration configuration)
    {
        var path = configuration["DataProtection:CertificatePath"];
        var previous = (configuration.GetSection("DataProtection:PreviousCertificatePaths").Get<string[]>() ?? []).Where(p => !string.IsNullOrWhiteSpace(p)).ToArray();
        if (string.IsNullOrWhiteSpace(path))
        {
            if (previous.Length > 0)
            {
                throw new InvalidOperationException("DataProtection:PreviousCertificatePaths needs DataProtection:CertificatePath.");
            }

            return;
        }

        var password = PasswordFor(configuration);
        var current = Load("DataProtection:CertificatePath", path, password, requirePrivateKey: true);
        var old = previous.Select((p, i) => Load($"DataProtection:PreviousCertificatePaths:{i}", p, password, requirePrivateKey: true)).ToArray();
        builder.ProtectKeysWithCertificate(current);
        if (old.Length > 0)
        {
            builder.UnprotectKeysWithAnyCertificate([current, .. old]);
        }
    }

    private static string? PasswordFor(IConfiguration configuration)
    {
        var file = configuration["DataProtection:CertificatePasswordFile"];
        if (!string.IsNullOrWhiteSpace(file))
        {
            if (!File.Exists(file))
            {
                throw new InvalidOperationException("DataProtection:CertificatePasswordFile does not exist.");
            }

            return File.ReadAllText(file).Trim();
        }

        var inline = configuration["DataProtection:CertificatePassword"];
        return string.IsNullOrEmpty(inline) ? null : inline;
    }

    private static X509Certificate2 Load(string setting, string path, string? password, bool requirePrivateKey)
    {
        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"{setting}: the certificate file does not exist or is not mounted.");
        }

        X509Certificate2 certificate;
        try
        {
            certificate = X509CertificateLoader.LoadPkcs12FromFile(path, password, X509KeyStorageFlags.EphemeralKeySet);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            // The exception text can echo details of the file; say only what is wrong.
            throw new InvalidOperationException($"{setting}: the file is not a readable PFX or its password is wrong.");
        }

        if (requirePrivateKey && !certificate.HasPrivateKey)
        {
            throw new InvalidOperationException($"{setting}: the certificate has no private key.");
        }

        if (certificate.NotAfter.ToUniversalTime() < DateTime.UtcNow)
        {
            throw new InvalidOperationException($"{setting}: the certificate has expired.");
        }

        return certificate;
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
