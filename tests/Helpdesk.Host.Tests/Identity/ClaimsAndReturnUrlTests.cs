using System.Security.Claims;
using Helpdesk.Modules.Identity.Authentication;
using Helpdesk.Modules.Identity.Contracts;

namespace Helpdesk.Host.Tests.Identity;

public sealed class ClaimsAndReturnUrlTests
{
    private const string Tenant = "11111111-1111-1111-1111-111111111111";
    private static readonly string Oid = Guid.NewGuid().ToString();

    private static ClaimsPrincipal Principal(params (string Type, string Value)[] claims) =>
        new(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)), "test"));

    [Fact]
    public void Reads_tenant_and_object_id_as_the_identity_key_and_treats_names_as_attributes()
    {
        var principal = Principal(("tid", Tenant.ToUpperInvariant()), ("oid", Oid), ("name", "Ada Lovelace"), ("preferred_username", "ada@contoso.test"));

        Assert.True(EntraClaimsReader.TryRead(principal, Tenant, out var identity, out var error));
        Assert.Null(error);
        Assert.Equal(ExternalIdentityProviders.Entra, identity!.Provider);
        Assert.Equal(Tenant, identity.IssuerTenant);
        Assert.Equal(Oid, identity.Subject);
        Assert.Equal("Ada Lovelace", identity.DisplayName);
        Assert.Equal("ada@contoso.test", identity.Email);
    }

    [Fact]
    public void Rejects_tokens_from_another_tenant()
    {
        var principal = Principal(("tid", "22222222-2222-2222-2222-222222222222"), ("oid", Oid));
        Assert.False(EntraClaimsReader.TryRead(principal, Tenant, out var identity, out var error));
        Assert.Null(identity);
        Assert.Equal("tenant_mismatch", error);
    }

    [Fact]
    public void Rejects_tokens_without_a_tenant_claim()
    {
        Assert.False(EntraClaimsReader.TryRead(Principal(("oid", Oid)), Tenant, out _, out var error));
        Assert.Equal("tenant_mismatch", error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-guid")]
    public void Rejects_tokens_without_a_valid_object_id(string oid)
    {
        var claims = oid.Length == 0 ? new[] { ("tid", Tenant) } : [("tid", Tenant), ("oid", oid)];
        Assert.False(EntraClaimsReader.TryRead(Principal(claims), Tenant, out _, out var error));
        Assert.Equal("missing_subject", error);
    }

    [Fact]
    public void Falls_back_to_the_object_id_when_no_name_claims_exist()
    {
        Assert.True(EntraClaimsReader.TryRead(Principal(("tid", Tenant), ("oid", Oid)), Tenant, out var identity, out _));
        Assert.Equal(Oid, identity!.DisplayName);
        Assert.Null(identity.Email);
    }

    [Fact]
    public void Accepts_the_long_form_claim_types_when_inbound_mapping_is_on()
    {
        var principal = Principal(
            ("http://schemas.microsoft.com/identity/claims/tenantid", Tenant),
            ("http://schemas.microsoft.com/identity/claims/objectidentifier", Oid));
        Assert.True(EntraClaimsReader.TryRead(principal, Tenant, out var identity, out _));
        Assert.Equal(Oid, identity!.Subject);
    }

    [Theory]
    [InlineData("/", "/")]
    [InlineData("/tickets?x=1#top", "/tickets?x=1#top")]
    [InlineData("", "/")]
    [InlineData(null, "/")]
    [InlineData("https://evil.test/", "/")]
    [InlineData("//evil.test/path", "/")]
    [InlineData("/\\evil.test", "/")]
    [InlineData("\\\\evil.test", "/")]
    [InlineData("javascript:alert(1)", "/")]
    [InlineData("evil", "/")]
    [InlineData("/ok\r\nSet-Cookie: x=1", "/")]
    public void Return_url_must_be_a_local_path(string? input, string expected)
    {
        Assert.Equal(expected, ReturnUrl.Sanitize(input));
    }

    [Fact]
    public void Cookie_principal_has_the_internal_user_id_and_no_external_secrets()
    {
        var id = Guid.NewGuid();
        var principal = HelpdeskPrincipal.Create(new ProvisionedUser(id, "Ada", "ada@x.test", true, false, true), ExternalIdentityProviders.Entra);

        Assert.True(HelpdeskPrincipal.TryGetUserId(principal, out var parsed));
        Assert.Equal(id, parsed);
        Assert.Equal(id.ToString(), principal.FindFirst(ClaimTypes.NameIdentifier)?.Value);
        Assert.True(principal.Identity!.IsAuthenticated);
        Assert.DoesNotContain(principal.Claims, c => c.Type is "tid" or "oid" or "access_token" or "id_token");
    }
}
