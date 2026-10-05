using Helpdesk.Host.Tests.Infrastructure;
using Helpdesk.Modules.Identity.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Helpdesk.Host.Tests.Identity;

public sealed class UserProvisioningTests(HelpdeskFixture fixture) : IClassFixture<HelpdeskFixture>
{
    private async Task<ProvisionedUser> ProvisionAsync(ExternalIdentityClaims claims)
    {
        ProvisionedUser? result = null;
        await fixture.WithScopeAsync(async sp => result = await sp.GetRequiredService<IUserProvisioner>().ProvisionAsync(claims));
        return result!;
    }

    private static ExternalIdentityClaims Claims(string subject, string tenant = "tenant-a", string name = "Ada", string? email = "ada@example.test") =>
        new(ExternalIdentityProviders.Entra, tenant, subject, name, email);

    [Fact]
    public async Task First_sign_in_creates_an_active_user_with_no_access_and_audits_it()
    {
        var subject = $"oid-{Unique.Id()}";
        var user = await ProvisionAsync(Claims(subject));

        Assert.True(user.IsNewUser);
        Assert.True(user.IsActive);
        Assert.False(user.IsPlatformAdmin);
        Assert.Equal(0, await fixture.ScalarAsync<long>("SELECT count(*) FROM organisation.department_memberships WHERE user_id = @u", new NpgsqlParameter("u", user.UserId)));
        Assert.Equal(1, await fixture.CountAuditAsync("user.provisioned", user.UserId));
    }

    [Fact]
    public async Task Same_external_identity_maps_to_the_same_user_and_refreshes_attributes()
    {
        var subject = $"oid-{Unique.Id()}";
        var first = await ProvisionAsync(Claims(subject, name: "Old Name", email: "old@example.test"));
        var second = await ProvisionAsync(Claims(subject, name: "New Name", email: "new@example.test"));

        Assert.Equal(first.UserId, second.UserId);
        Assert.False(second.IsNewUser);
        Assert.Equal("New Name", second.DisplayName);
        Assert.Equal("new@example.test", second.Email);
        Assert.Equal(1, await fixture.CountAuditAsync("user.provisioned", first.UserId));
    }

    [Fact]
    public async Task Email_is_never_the_key()
    {
        var shared = $"shared-{Unique.Id()}@example.test";
        var one = await ProvisionAsync(Claims($"oid-{Unique.Id()}", email: shared));
        var two = await ProvisionAsync(Claims($"oid-{Unique.Id()}", email: shared));
        Assert.NotEqual(one.UserId, two.UserId);
    }

    [Fact]
    public async Task Same_subject_in_a_different_tenant_or_provider_is_a_different_user()
    {
        var subject = $"oid-{Unique.Id()}";
        var a = await ProvisionAsync(Claims(subject, tenant: "tenant-a"));
        var b = await ProvisionAsync(Claims(subject, tenant: "tenant-b"));
        var dev = await ProvisionAsync(new ExternalIdentityClaims(ExternalIdentityProviders.Dev, "tenant-a", subject, "Dev", null));
        Assert.Equal(3, new[] { a.UserId, b.UserId, dev.UserId }.Distinct().Count());
    }

    [Fact]
    public async Task Concurrent_first_sign_ins_create_exactly_one_user()
    {
        var claims = Claims($"oid-{Unique.Id()}");
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => ProvisionAsync(claims)));

        Assert.Single(results.Select(r => r.UserId).Distinct());
        Assert.Equal(1, await fixture.ScalarAsync<long>(
            "SELECT count(*) FROM identity.external_identities WHERE subject = @s", new NpgsqlParameter("s", claims.Subject)));
        Assert.Equal(1, await fixture.CountAuditAsync("user.provisioned", results[0].UserId));
    }

    [Theory]
    [InlineData("", "tenant", "subject")]
    [InlineData("entra", "", "subject")]
    [InlineData("entra", "tenant", "")]
    [InlineData("entra", "tenant", "   ")]
    public async Task Incomplete_identities_are_rejected(string provider, string tenant, string subject)
    {
        await Assert.ThrowsAsync<Helpdesk.SharedKernel.Errors.RequestValidationException>(
            () => ProvisionAsync(new ExternalIdentityClaims(provider, tenant, subject, "x", null)));
    }

    [Fact]
    public async Task Overlong_names_are_truncated_not_rejected()
    {
        var user = await ProvisionAsync(Claims($"oid-{Unique.Id()}", name: new string('n', 500), email: $"{new string('e', 400)}@x.test"));
        Assert.Equal(200, user.DisplayName.Length);
        Assert.Equal(320, user.Email!.Length);
    }

    [Fact]
    public async Task Provisioning_does_not_grant_platform_admin_to_unlisted_subjects()
    {
        var user = await ProvisionAsync(Claims("platform-admin-lookalike"));
        Assert.False(user.IsPlatformAdmin);
    }
}
