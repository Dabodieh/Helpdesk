using Helpdesk.Host.Tests.Infrastructure;
using Helpdesk.Modules.Identity.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Helpdesk.Host.Tests.Identity;

/// <summary>Own database per class: the bootstrap rules depend on whether any platform admin exists yet.</summary>
public sealed class BootstrapAdminTests(HelpdeskFixture fixture) : IClassFixture<HelpdeskFixture>
{
    private async Task<ProvisionedUser> ProvisionAsync(string subject, params string[] bootstrapSubjects)
    {
        var factory = fixture.Factory.WithWebHostBuilder(b =>
        {
            for (var i = 0; i < bootstrapSubjects.Length; i++)
            {
                b.UseSetting($"Authentication:BootstrapPlatformAdminSubjects:{i}", bootstrapSubjects[i]);
            }
        });
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IUserProvisioner>()
            .ProvisionAsync(new ExternalIdentityClaims(ExternalIdentityProviders.Entra, "tenant", subject, subject, null));
    }

    [Fact]
    public async Task Listed_subject_becomes_platform_admin_on_first_provisioning_and_it_is_audited_as_bootstrap()
    {
        var subject = $"boot-{Unique.Id()}";
        var user = await ProvisionAsync(subject, subject);

        Assert.True(user.IsPlatformAdmin);
        Assert.Equal(1, await fixture.ScalarAsync<long>(
            "SELECT count(*) FROM audit.audit_events WHERE action = 'user.platform_admin_changed' AND object_id = @u AND source = 'bootstrap' AND actor_user_id IS NULL",
            new NpgsqlParameter("u", user.UserId)));
    }

    [Fact]
    public async Task Listing_an_existing_user_later_does_not_promote_them_while_a_platform_admin_exists()
    {
        var plainSubject = $"plain-{Unique.Id()}";
        var adminSubject = $"boot-{Unique.Id()}";
        var plain = await ProvisionAsync(plainSubject);                 // not listed: no admin rights
        var admin = await ProvisionAsync(adminSubject, adminSubject);   // listed and new: an active platform admin now exists
        Assert.True(admin.IsPlatformAdmin);

        var again = await ProvisionAsync(plainSubject, plainSubject);   // now listed, but not new and an admin exists

        Assert.Equal(plain.UserId, again.UserId);
        Assert.False(again.IsPlatformAdmin);
    }

    [Fact]
    public async Task Listed_existing_user_is_promoted_when_no_active_platform_admin_exists_recovery()
    {
        // Isolated database so that "no admin exists" is true.
        await using var database = await TestDatabase.CreateAsync();
        await using var isolated = new IsolatedFactory(database);
        await isolated.MigrateAsync();

        var subject = $"recover-{Unique.Id()}";
        var first = await isolated.ProvisionAsync(subject, bootstrap: []);
        Assert.False(first.IsPlatformAdmin);
        var second = await isolated.ProvisionAsync(subject, bootstrap: [subject]);
        Assert.True(second.IsPlatformAdmin);
        Assert.Equal(first.UserId, second.UserId);
    }

    private sealed class IsolatedFactory(TestDatabase database) : IAsyncDisposable
    {
        private readonly HelpdeskFactory _factory = new(database.ConnectionString);

        public async Task MigrateAsync() =>
            await _factory.Services.GetRequiredService<Helpdesk.SharedKernel.Database.ModuleMigrationRunner>().RunAsync();

        public async Task<ProvisionedUser> ProvisionAsync(string subject, string[] bootstrap)
        {
            var factory = _factory.WithWebHostBuilder(b =>
            {
                // The base factory seeds "platform-admin"; replace the list so only the given subjects are bootstrap subjects.
                b.UseSetting("Authentication:BootstrapPlatformAdminSubjects:0", bootstrap.Length > 0 ? bootstrap[0] : "nobody");
            });
            using var scope = factory.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<IUserProvisioner>()
                .ProvisionAsync(new ExternalIdentityClaims(ExternalIdentityProviders.Entra, "tenant", subject, subject, null));
        }

        public async ValueTask DisposeAsync() => await _factory.DisposeAsync();
    }
}
