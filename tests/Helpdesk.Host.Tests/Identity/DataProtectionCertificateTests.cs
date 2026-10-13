using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Helpdesk.Modules.Identity.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Helpdesk.Host.Tests.Identity;

/// <summary>Optional encryption of the key ring at rest with an operator-supplied certificate (gate F-04). Pure tests: a temporary key folder and generated certificates.</summary>
public sealed class DataProtectionCertificateTests : IDisposable
{
    private const string Canary = "canary-pfx-password-7d41";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hd-dp-" + Guid.NewGuid().ToString("N"));

    public DataProtectionCertificateTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string NewPfx(string name, string password, DateTimeOffset? notAfter = null, bool withPrivateKey = true)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=Helpdesk key ring test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-2), notAfter ?? DateTimeOffset.UtcNow.AddDays(30));
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, withPrivateKey ? certificate.Export(X509ContentType.Pfx, password) : certificate.Export(X509ContentType.Cert));
        return path;
    }

    private static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => v.Value)).Build();

    private static IDataProtectionProvider Provider(string keyFolder, IConfiguration configuration)
    {
        var services = new ServiceCollection();
        var builder = services.AddDataProtection().SetApplicationName(DataProtectionKeyStore.ApplicationName).PersistKeysToFileSystem(new DirectoryInfo(keyFolder));
        DataProtectionKeyStore.ProtectKeysWithConfiguredCertificate(builder, configuration);
        return services.BuildServiceProvider().GetRequiredService<IDataProtectionProvider>();
    }

    private string KeyFolder(string name)
    {
        var folder = Path.Combine(_dir, name);
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static string KeyXml(string folder) => string.Concat(Directory.GetFiles(folder, "*.xml").Select(File.ReadAllText));

    [Fact]
    public void Without_a_certificate_keys_stay_plain_so_nothing_changes_for_existing_installs()
    {
        var folder = KeyFolder("plain");
        var protector = Provider(folder, Config()).CreateProtector("t");
        Assert.Equal("hello", protector.Unprotect(protector.Protect("hello")));
        Assert.Contains("masterKey", KeyXml(folder), StringComparison.Ordinal);
    }

    [Fact]
    public void With_a_certificate_new_keys_are_encrypted_and_the_ring_still_works_after_a_restart()
    {
        var pfx = NewPfx("current.pfx", Canary);
        var configuration = Config(("DataProtection:CertificatePath", pfx), ("DataProtection:CertificatePassword", Canary));
        var folder = KeyFolder("encrypted");
        var protectedValue = Provider(folder, configuration).CreateProtector("t").Protect("hello");

        var xml = KeyXml(folder);
        Assert.Contains("EncryptedData", xml, StringComparison.Ordinal); // W3C XML Encryption wrapper of the certificate-protected key
        Assert.DoesNotContain("masterKey", xml, StringComparison.Ordinal); // the key material is not in clear

        // a new process with the same certificate reads the same ring
        Assert.Equal("hello", Provider(folder, configuration).CreateProtector("t").Unprotect(protectedValue));
        // without the certificate it cannot
        Assert.ThrowsAny<CryptographicException>(() => Provider(folder, Config()).CreateProtector("t").Unprotect(protectedValue));
    }

    [Fact]
    public void Rotation_adds_a_new_certificate_and_keeps_the_previous_one_for_reading_existing_keys()
    {
        var old = NewPfx("old.pfx", "pw-old");
        var folder = KeyFolder("rotation");
        var oldConfig = Config(("DataProtection:CertificatePath", old), ("DataProtection:CertificatePassword", "pw-old"));
        var value = Provider(folder, oldConfig).CreateProtector("t").Protect("keep me");

        var next = NewPfx("next.pfx", "pw-old");
        var rotated = Config(
            ("DataProtection:CertificatePath", next), ("DataProtection:CertificatePassword", "pw-old"),
            ("DataProtection:PreviousCertificatePaths:0", old));
        Assert.Equal("keep me", Provider(folder, rotated).CreateProtector("t").Unprotect(value));
    }

    [Fact]
    public void A_certificate_that_cannot_be_used_stops_the_start_without_echoing_secrets_or_falling_back()
    {
        var good = NewPfx("good.pfx", Canary);
        var expired = NewPfx("expired.pfx", Canary, notAfter: DateTimeOffset.UtcNow.AddDays(-1));
        var publicOnly = NewPfx("public.cer", Canary, withPrivateKey: false);
        var cases = new[]
        {
            Config(("DataProtection:CertificatePath", Path.Combine(_dir, "missing.pfx"))),
            Config(("DataProtection:CertificatePath", good), ("DataProtection:CertificatePassword", "wrong-" + Canary)),
            Config(("DataProtection:CertificatePath", expired), ("DataProtection:CertificatePassword", Canary)),
            Config(("DataProtection:CertificatePath", publicOnly)),
            Config(("DataProtection:PreviousCertificatePaths:0", good)), // previous without a current certificate
            Config(("DataProtection:CertificatePath", good), ("DataProtection:CertificatePasswordFile", Path.Combine(_dir, "no-secret-file"))),
        };
        foreach (var configuration in cases)
        {
            var ex = Assert.Throws<InvalidOperationException>(() => Provider(KeyFolder("bad-" + Guid.NewGuid().ToString("N")), configuration));
            Assert.DoesNotContain(Canary, ex.ToString(), StringComparison.Ordinal);
            Assert.StartsWith("DataProtection", ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_password_can_come_from_a_mounted_secret_file()
    {
        var pfx = NewPfx("filepw.pfx", Canary);
        var secret = Path.Combine(_dir, "pfx-password");
        File.WriteAllText(secret, Canary + "\n");
        var protector = Provider(KeyFolder("filepw"), Config(("DataProtection:CertificatePath", pfx), ("DataProtection:CertificatePasswordFile", secret))).CreateProtector("t");
        Assert.Equal("x", protector.Unprotect(protector.Protect("x")));
    }
}
