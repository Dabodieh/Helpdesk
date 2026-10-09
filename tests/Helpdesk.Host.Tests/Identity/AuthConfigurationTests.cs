using System.Net;
using Helpdesk.Host.Tests.Infrastructure;
using Helpdesk.Modules.Identity.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using System.Web;

namespace Helpdesk.Host.Tests.Identity;

public sealed class AuthConfigurationTests(HelpdeskFixture fixture) : IClassFixture<HelpdeskFixture>
{
    private const string TenantId = "33333333-3333-3333-3333-333333333333";
    private const string ClientId = "44444444-4444-4444-4444-444444444444";

    private WebApplicationFactory<Program> Variant(string environment, Action<IWebHostBuilder>? configure = null) =>
        fixture.Factory.WithWebHostBuilder(b =>
        {
            b.UseEnvironment(environment);
            configure?.Invoke(b);
        });

    private static void Entra(IWebHostBuilder b, bool devSignIn)
    {
        b.UseSetting("Authentication:DevSignIn:Enabled", devSignIn ? "true" : "false");
        b.UseSetting("Authentication:Entra:TenantId", TenantId);
        b.UseSetting("Authentication:Entra:ClientId", ClientId);
        b.UseSetting("Authentication:Entra:ClientSecret", "test-secret-not-real");
    }

    /// <summary>Offline stand-in for the tenant's OIDC discovery document.</summary>
    private static StaticConfigurationManager<OpenIdConnectConfiguration> StaticMetadata() =>
        new(new OpenIdConnectConfiguration
        {
            Issuer = $"https://login.microsoftonline.com/{TenantId}/v2.0",
            AuthorizationEndpoint = $"https://login.microsoftonline.com/{TenantId}/oauth2/v2.0/authorize",
            TokenEndpoint = $"https://login.microsoftonline.com/{TenantId}/oauth2/v2.0/token",
        });

    private static void Throws(WebApplicationFactory<Program> factory, string expectedFragment)
    {
        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains(expectedFragment, ex.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Dev_sign_in_is_refused_at_startup_outside_development_and_testing()
    {
        Throws(Variant("Production", b => b.UseSetting("Authentication:DevSignIn:Enabled", "true")), "DevSignIn");
        Throws(Variant("Staging", b => b.UseSetting("Authentication:DevSignIn:Enabled", "true")), "DevSignIn");
    }

    [Theory]
    [InlineData("Authentication:Entra:TenantId", "not-a-guid", "TenantId")]
    [InlineData("Authentication:Entra:ClientId", "not-a-guid", "ClientId")]
    [InlineData("Authentication:Entra:ClientSecret", "", "ClientSecret")]
    public void Startup_fails_on_partial_or_invalid_entra_configuration(string key, string value, string expected)
    {
        var factory = Variant("Production", b =>
        {
            Entra(b, devSignIn: false);
            b.UseSetting(key, value);
        });
        Throws(factory, expected);
    }

    [Fact]
    public void Startup_fails_on_blank_bootstrap_subjects()
    {
        Throws(Variant("Testing", b => b.UseSetting("Authentication:BootstrapPlatformAdminSubjects:1", " ")), "BootstrapPlatformAdminSubjects");
    }

    [Fact]
    public async Task Entra_only_production_configuration_starts_and_has_no_dev_login_route()
    {
        var factory = Variant("Production", b =>
        {
            Entra(b, devSignIn: false);

            // Production has no simulator or in-memory store: Graph and S3 must be configured explicitly (secrets come from the environment).
            b.UseSetting("Mail:Provider", "Graph");
            b.UseSetting("Mail:NotificationBaseUrl", "https://helpdesk.example.test");
            b.UseSetting("Mail:Graph:TenantId", "11111111-1111-1111-1111-111111111111");
            b.UseSetting("Mail:Graph:ClientId", "22222222-2222-2222-2222-222222222222");
            b.UseSetting("Mail:Graph:ClientSecret", "test-secret-not-real");
            b.UseSetting("Storage:Provider", "S3");
            b.UseSetting("Storage:S3:AccessKey", "test-access");
            b.UseSetting("Storage:S3:SecretKey", "test-secret");
            b.UseSetting("Attachments:RequireCleanScan", "true");
            b.UseSetting("Attachments:Enabled", "false");
            b.UseSetting("AllowedHosts", "helpdesk.example.test;localhost");
            b.UseSetting("Proxy:KnownProxies:0", "10.0.0.2");
            b.UseSetting("Jobs:Enabled", "false");
        });
        var client = fixture.CreateAnonymousClient(factory);

        // The route is not merely forbidden: it is not registered at all.
        var routes = factory.Services.GetRequiredService<Microsoft.AspNetCore.Routing.EndpointDataSource>().Endpoints
            .OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>().Select(e => e.RoutePattern.RawText).ToList();
        Assert.DoesNotContain("/api/auth/dev-login", routes);
        Assert.Contains("/api/auth/login", routes);
        // Unmatched paths also fall under the authenticated fallback policy (401 for anonymous callers).
        (await client.GetAsync("/api/auth/dev-login?subject=x")).Expect(HttpStatusCode.Unauthorized);
        (await client.GetAsync("/health/live")).Expect(HttpStatusCode.OK);
    }

    [Fact]
    public void The_mail_simulator_and_missing_graph_or_storage_settings_stop_a_production_host()
    {
        // Fake provider outside Development/Testing, and Graph/S3 left unconfigured, must fail at startup (never silently run).
        Assert.ThrowsAny<Exception>(() => Variant("Production", b =>
        {
            Entra(b, devSignIn: false);
            b.UseSetting("Mail:Provider", "Fake");
            b.UseSetting("Storage:Provider", "S3");
            b.UseSetting("Storage:S3:AccessKey", "a");
            b.UseSetting("Storage:S3:SecretKey", "b");
            b.UseSetting("Jobs:Enabled", "false");
        }).Services);
        Assert.ThrowsAny<Exception>(() => Variant("Production", b =>
        {
            Entra(b, devSignIn: false);
            b.UseSetting("Jobs:Enabled", "false");
        }).Services);
    }

    [Fact]
    public async Task Entra_login_challenges_with_code_flow_pkce_state_and_nonce_for_the_single_tenant()
    {
        var factory = Variant("Testing", b =>
        {
            Entra(b, devSignIn: false);
            b.ConfigureTestServices(services => services.PostConfigure<OpenIdConnectOptions>(AuthSchemes.Entra, o =>
                o.ConfigurationManager = StaticMetadata()));
        });
        var client = fixture.CreateAnonymousClient(factory);

        var response = (await client.GetAsync("/api/auth/login?returnUrl=/inbox")).Expect(HttpStatusCode.Redirect);
        var location = response.Message.Headers.Location!;
        var query = HttpUtility.ParseQueryString(location.Query);

        Assert.Equal($"/{TenantId}/oauth2/v2.0/authorize", location.AbsolutePath);
        Assert.Equal("login.microsoftonline.com", location.Host);
        Assert.Equal(ClientId, query["client_id"]);
        Assert.Equal("code", query["response_type"]);
        Assert.True(query["response_mode"] is null or "query", "code flow must use the query response mode (the protocol default when omitted)");
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.False(string.IsNullOrEmpty(query["code_challenge"]));
        Assert.False(string.IsNullOrEmpty(query["state"]));
        Assert.False(string.IsNullOrEmpty(query["nonce"]));
        Assert.Contains("openid", query["scope"]!, StringComparison.Ordinal);
        Assert.EndsWith("/api/auth/signin-oidc", query["redirect_uri"], StringComparison.Ordinal);
        Assert.DoesNotContain("client_secret", location.Query, StringComparison.Ordinal);
        Assert.Contains(response.Message.Headers.GetValues("Set-Cookie"), c => c.Contains(".AspNetCore.OpenIdConnect.Nonce", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Entra_login_does_not_redirect_to_a_non_local_return_url()
    {
        var factory = Variant("Testing", b =>
        {
            Entra(b, devSignIn: false);
            b.ConfigureTestServices(services => services.PostConfigure<OpenIdConnectOptions>(AuthSchemes.Entra, o =>
                o.ConfigurationManager = StaticMetadata()));
        });
        var client = fixture.CreateAnonymousClient(factory);
        var response = (await client.GetAsync("/api/auth/login?returnUrl=https://evil.test/")).Expect(HttpStatusCode.Redirect);
        Assert.Equal("login.microsoftonline.com", response.Message.Headers.Location!.Host);
        var state = HttpUtility.ParseQueryString(response.Message.Headers.Location.Query)["state"];
        Assert.DoesNotContain("evil.test", state, StringComparison.Ordinal); // state is protected, never echoes the target
    }
}
