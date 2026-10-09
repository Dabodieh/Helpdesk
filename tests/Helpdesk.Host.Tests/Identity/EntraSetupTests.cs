using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Helpdesk.Host.Tests.Infrastructure;
using Helpdesk.Host.Tests.Mail;
using Helpdesk.Host.Tests.Ticketing;
using Helpdesk.Modules.Identity.Contracts;
using Helpdesk.Modules.Identity.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Helpdesk.Host.Tests.Identity;

/// <summary>
/// Guided Microsoft Entra setup against a stub of Microsoft's endpoints (nothing real is contacted): platform administrators only, one application created per sign-in, the secret
/// stored protected and never returned or audited, partial failures cleaned up, server settings always winning, and the setup administrator becoming the first platform admin.
/// </summary>
public sealed class EntraSetupTests(HelpdeskFixture fixture) : IClassFixture<HelpdeskFixture>, IAsyncLifetime
{
    private const string Admin = "/api/admin/entra";
    private const string Tenant = "55555555-5555-5555-5555-555555555555";
    private const string AdminOid = "66666666-6666-6666-6666-666666666666";
    private const string Secret = "s3cr3t-value-from-microsoft";

    public async Task InitializeAsync() => await fixture.ExecuteAsync("DELETE FROM identity.entra_settings");

    public async Task DisposeAsync()
    {
        await fixture.ExecuteAsync("DELETE FROM identity.entra_settings");
    }

    private sealed record Seen(HttpMethod Method, string Path, string Body, string? Authorization);

    private sealed class MicrosoftStub : HttpMessageHandler
    {
        public List<Seen> Requests { get; } = [];

        public int PendingPolls { get; set; }

        public HttpStatusCode? FailApplications { get; set; }

        public HttpStatusCode? FailAddPassword { get; set; }

        public string? TokenError { get; set; }

        private static string Jwt(object payload) =>
            "e30." + Convert.ToBase64String(Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(payload))).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".sig";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add(new Seen(request.Method, path, body, request.Headers.Authorization?.ToString()));
            static HttpResponseMessage Json(HttpStatusCode status, object value) => new(status) { Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };

            if (path.EndsWith("/devicecode", StringComparison.Ordinal))
            {
                return Json(HttpStatusCode.OK, new { device_code = "dc-secret", user_code = "ABCD-1234", verification_uri = "https://microsoft.com/devicelogin", expires_in = 900, interval = 5 });
            }

            if (path.EndsWith("/token", StringComparison.Ordinal))
            {
                if (TokenError is not null)
                {
                    return Json(HttpStatusCode.BadRequest, new { error = TokenError });
                }

                if (PendingPolls-- > 0)
                {
                    return Json(HttpStatusCode.BadRequest, new { error = "authorization_pending" });
                }

                return Json(HttpStatusCode.OK, new { access_token = "graph-access-token", id_token = Jwt(new { tid = Tenant, oid = AdminOid, name = "Alex Admin", preferred_username = "alex@example.org" }) });
            }

            if (request.Method == HttpMethod.Post && path.EndsWith("/applications", StringComparison.Ordinal))
            {
                return FailApplications is { } f ? Json(f, new { error = new { code = "Authorization_RequestDenied" } }) : Json(HttpStatusCode.Created, new { id = "app-object-1", appId = "11111111-aaaa-bbbb-cccc-222222222222" });
            }

            if (path.EndsWith("/servicePrincipals", StringComparison.Ordinal))
            {
                return Json(HttpStatusCode.Created, new { id = "sp-1" });
            }

            if (path.EndsWith("/addPassword", StringComparison.Ordinal))
            {
                return FailAddPassword is { } f ? Json(f, new { error = new { code = "x" } }) : Json(HttpStatusCode.OK, new { secretText = Secret });
            }

            return request.Method == HttpMethod.Delete ? new HttpResponseMessage(HttpStatusCode.NoContent) : Json(HttpStatusCode.NotFound, new { });
        }
    }

    private (WebApplicationFactory<Program> Factory, MicrosoftStub Stub) Variant(Action<IWebHostBuilder>? more = null)
    {
        var stub = new MicrosoftStub();
        var factory = fixture.Factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("EntraSetup:PropagationDelayMs", "0");
            b.UseSetting("Authentication:DevSignIn:Enabled", "true");
            more?.Invoke(b);
            b.ConfigureTestServices(s => s.AddHttpClient(EntraSetupService.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => stub));
        });
        return (factory, stub);
    }

    private static readonly string[] Origins = ["https://helpdesk.example.org", "http://localhost:5173"];

    private static async Task<string> StartAndSignInAsync(TestUser admin, MicrosoftStub stub)
    {
        stub.PendingPolls = 1;
        var start = (await admin.Client.PostAsync($"{Admin}/setup/start", new { })).Expect(HttpStatusCode.OK).Json;
        var id = start.GetProperty("sessionId").GetString()!;
        Assert.Equal("ABCD-1234", start.GetProperty("userCode").GetString());
        Assert.Equal("pending", (await admin.Client.PostAsync($"{Admin}/setup/poll", new { sessionId = id })).Expect(HttpStatusCode.OK).Json.GetProperty("state").GetString());
        var signedIn = (await admin.Client.PostAsync($"{Admin}/setup/poll", new { sessionId = id })).Expect(HttpStatusCode.OK).Json;
        Assert.Equal("signed_in", signedIn.GetProperty("state").GetString());
        Assert.Equal("alex@example.org", signedIn.GetProperty("adminAccount").GetString());
        Assert.Equal(Tenant, signedIn.GetProperty("tenantId").GetString());
        return id;
    }

    [Fact]
    public async Task Only_a_platform_administrator_can_see_or_run_the_setup()
    {
        var d = await fixture.NewDeptAsync();
        (await fixture.CreateAnonymousClient().GetAsync(Admin)).Expect(HttpStatusCode.Unauthorized);
        foreach (var user in new[] { d.Admin, d.Lead, d.Agent, d.Viewer, await fixture.NewUserAsync() })
        {
            (await user.Client.GetAsync(Admin)).Expect(HttpStatusCode.Forbidden);
            (await user.Client.PostAsync($"{Admin}/setup/start", new { })).Expect(HttpStatusCode.Forbidden);
            (await user.Client.PostAsync($"{Admin}/setup/poll", new { sessionId = "x" })).Expect(HttpStatusCode.Forbidden);
            (await user.Client.PostAsync($"{Admin}/setup/create", new { sessionId = "x", redirectOrigins = Origins })).Expect(HttpStatusCode.Forbidden);
            (await user.Client.PostAsync($"{Admin}/disconnect", new { })).Expect(HttpStatusCode.Forbidden);
        }
    }

    [Fact]
    public async Task The_guided_setup_creates_one_application_stores_the_secret_protected_and_makes_the_setup_admin_the_first_platform_admin()
    {
        var (factory, stub) = Variant();
        await using var _ = factory;
        var admin = await fixture.SignInAsync(HelpdeskFactory.PlatformAdminSubject, "Platform Admin", factory: factory);
        Assert.False((await admin.Client.GetAsync(Admin)).Expect(HttpStatusCode.OK).Json.GetProperty("configured").GetBoolean());
        Assert.Contains("\"entra\":false", (await admin.Client.GetAsync("/api/auth/providers")).Body, StringComparison.Ordinal);

        var auditBefore = await fixture.CountAsync("SELECT count(*) FROM audit.audit_events WHERE action = 'entra.connected'");
        var id = await StartAndSignInAsync(admin, stub);
        var created = (await admin.Client.PostAsync($"{Admin}/setup/create", new { sessionId = id, redirectOrigins = Origins })).Expect(HttpStatusCode.OK).Json;
        Assert.Equal(Tenant, created.GetProperty("tenantId").GetString());
        Assert.Equal("11111111-aaaa-bbbb-cccc-222222222222", created.GetProperty("clientId").GetString());
        Assert.Equal(2, created.GetProperty("redirectUris").GetArrayLength());

        // What was asked of Microsoft: one single-tenant application, redirect URIs for the registered origins only, no API permissions, one service principal, one secret of about 12 months.
        var app = Assert.Single(stub.Requests, r => r.Method == HttpMethod.Post && r.Path.EndsWith("/applications", StringComparison.Ordinal));
        Assert.Equal("Bearer graph-access-token", app.Authorization);
        var appBody = JsonNode.Parse(app.Body)!;
        Assert.Equal("AzureADMyOrg", appBody["signInAudience"]!.GetValue<string>());
        Assert.Equal(["https://helpdesk.example.org/api/auth/signin-oidc", "http://localhost:5173/api/auth/signin-oidc"], appBody["web"]!["redirectUris"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Null(appBody["requiredResourceAccess"]);
        Assert.Single(stub.Requests, r => r.Path.EndsWith("/servicePrincipals", StringComparison.Ordinal));
        var password = Assert.Single(stub.Requests, r => r.Path.EndsWith("/addPassword", StringComparison.Ordinal));
        var ends = DateTimeOffset.Parse(JsonNode.Parse(password.Body)!["passwordCredential"]!["endDateTime"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange((ends - DateTimeOffset.UtcNow).TotalDays, 360, 370);

        // Stored protected: the plaintext is nowhere in the database, the response, the status or the audit trail.
        Assert.DoesNotContain(Secret, created.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(0, await fixture.CountAsync("SELECT count(*) FROM identity.entra_settings WHERE client_secret_protected LIKE @s", TicketKit.P("s", $"%{Secret}%")));
        Assert.Equal(1, await fixture.CountAsync("SELECT count(*) FROM identity.entra_settings WHERE length(client_secret_protected) > 40 AND bootstrap_admin_subject = @o", TicketKit.P("o", AdminOid)));
        Assert.Equal(0, await fixture.CountAsync("SELECT count(*) FROM audit.audit_events WHERE next::text LIKE @s", TicketKit.P("s", $"%{Secret}%")));
        Assert.Equal(auditBefore + 1, await fixture.CountAsync("SELECT count(*) FROM audit.audit_events WHERE action = 'entra.connected'"));

        var status = (await admin.Client.GetAsync(Admin)).Expect(HttpStatusCode.OK);
        Assert.DoesNotContain(Secret, status.Body, StringComparison.Ordinal);
        Assert.Equal("database", status.Json.GetProperty("source").GetString());
        Assert.True(status.Json.GetProperty("configured").GetBoolean());
        Assert.Contains("\"entra\":true", (await admin.Client.GetAsync("/api/auth/providers")).Body, StringComparison.Ordinal); // applied without a restart

        // The administrator who ran the setup becomes a platform admin on first Entra sign-in; somebody else does not.
        using var scope = factory.Services.CreateScope();
        var provisioner = scope.ServiceProvider.GetRequiredService<IUserProvisioner>();
        var first = await provisioner.ProvisionAsync(new ExternalIdentityClaims(ExternalIdentityProviders.Entra, Tenant, AdminOid, "Alex Admin", "alex@example.org"));
        var other = await provisioner.ProvisionAsync(new ExternalIdentityClaims(ExternalIdentityProviders.Entra, Tenant, Guid.NewGuid().ToString(), "Someone Else", "else@example.org"));
        Assert.True(first.IsPlatformAdmin);
        Assert.False(other.IsPlatformAdmin);
    }

    [Fact]
    public async Task A_sign_in_creates_at_most_one_application_and_belongs_to_the_administrator_who_started_it()
    {
        var (factory, stub) = Variant();
        await using var _ = factory;
        var admin = await fixture.SignInAsync(HelpdeskFactory.PlatformAdminSubject, "Platform Admin", factory: factory);
        var id = await StartAndSignInAsync(admin, stub);

        var second = await fixture.SignInAsync($"admin2-{Unique.Id()}", "Second Admin", factory: factory);
        await fixture.ExecuteAsync("UPDATE identity.users SET is_platform_admin = true WHERE id = @i", TicketKit.P("i", second.Id));
        (await second.Client.PostAsync($"{Admin}/setup/poll", new { sessionId = id })).Expect(HttpStatusCode.NotFound);
        (await second.Client.PostAsync($"{Admin}/setup/create", new { sessionId = id, redirectOrigins = Origins })).Expect(HttpStatusCode.NotFound);

        (await admin.Client.PostAsync($"{Admin}/setup/create", new { sessionId = id, redirectOrigins = Origins })).Expect(HttpStatusCode.OK);
        (await admin.Client.PostAsync($"{Admin}/setup/create", new { sessionId = id, redirectOrigins = Origins })).Expect(HttpStatusCode.NotFound);
        Assert.Single(stub.Requests, r => r.Method == HttpMethod.Post && r.Path.EndsWith("/applications", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Only_real_addresses_people_use_may_become_redirect_addresses()
    {
        var (factory, stub) = Variant();
        await using var _ = factory;
        var admin = await fixture.SignInAsync(HelpdeskFactory.PlatformAdminSubject, "Platform Admin", factory: factory);
        var id = await StartAndSignInAsync(admin, stub);
        foreach (var bad in new[] { "http://evil.example", "https://helpdesk.example.org/path", "javascript:alert(1)", "https://user@helpdesk.example.org", "not a url", "https://a.example/?x=1" })
        {
            (await admin.Client.PostAsync($"{Admin}/setup/create", new { sessionId = id, redirectOrigins = new[] { bad } })).Expect(HttpStatusCode.BadRequest);
        }

        (await admin.Client.PostAsync($"{Admin}/setup/create", new { sessionId = id, redirectOrigins = Array.Empty<string>() })).Expect(HttpStatusCode.BadRequest);
        Assert.DoesNotContain(stub.Requests, r => r.Path.EndsWith("/applications", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_refused_application_is_explained_and_saves_nothing_and_a_late_failure_removes_only_what_it_just_created()
    {
        var (factory, stub) = Variant();
        await using var _ = factory;
        var admin = await fixture.SignInAsync(HelpdeskFactory.PlatformAdminSubject, "Platform Admin", factory: factory);

        var id = await StartAndSignInAsync(admin, stub);
        stub.FailApplications = HttpStatusCode.Forbidden;
        var refused = (await admin.Client.PostAsync($"{Admin}/setup/create", new { sessionId = id, redirectOrigins = Origins })).Expect(HttpStatusCode.Conflict);
        Assert.Contains("Application Administrator", refused.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(stub.Requests, r => r.Method == HttpMethod.Delete);
        Assert.Equal(0, await fixture.CountAsync("SELECT count(*) FROM identity.entra_settings"));

        // The same sign-in can try again; this time the secret step fails and the application made in this attempt is deleted.
        stub.FailApplications = null;
        stub.FailAddPassword = HttpStatusCode.InternalServerError;
        (await admin.Client.PostAsync($"{Admin}/setup/create", new { sessionId = id, redirectOrigins = Origins })).Expect(HttpStatusCode.Conflict);
        Assert.Single(stub.Requests, r => r.Method == HttpMethod.Delete && r.Path.EndsWith("/applications/app-object-1", StringComparison.Ordinal));
        Assert.Equal(0, await fixture.CountAsync("SELECT count(*) FROM identity.entra_settings"));

        stub.FailAddPassword = null;
        (await admin.Client.PostAsync($"{Admin}/setup/create", new { sessionId = id, redirectOrigins = Origins })).Expect(HttpStatusCode.OK);
        Assert.Equal(1, await fixture.CountAsync("SELECT count(*) FROM identity.entra_settings"));
    }

    [Theory]
    [InlineData("authorization_declined", "declined")]
    [InlineData("expired_token", "expired")]
    [InlineData("something_else", "failed")]
    public async Task A_declined_expired_or_failed_microsoft_sign_in_is_reported_and_nothing_is_created(string error, string expected)
    {
        var (factory, stub) = Variant();
        await using var _ = factory;
        var admin = await fixture.SignInAsync(HelpdeskFactory.PlatformAdminSubject, "Platform Admin", factory: factory);
        var id = (await admin.Client.PostAsync($"{Admin}/setup/start", new { })).Expect(HttpStatusCode.OK).Json.GetProperty("sessionId").GetString();
        stub.TokenError = error;
        Assert.Equal(expected, (await admin.Client.PostAsync($"{Admin}/setup/poll", new { sessionId = id })).Expect(HttpStatusCode.OK).Json.GetProperty("state").GetString());
        (await admin.Client.PostAsync($"{Admin}/setup/create", new { sessionId = id, redirectOrigins = Origins })).Expect(HttpStatusCode.Conflict);
        Assert.DoesNotContain(stub.Requests, r => r.Path.EndsWith("/applications", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Server_settings_always_win_so_the_wizard_refuses_while_they_exist()
    {
        var (factory, _) = Variant(b =>
        {
            b.UseSetting("Authentication:Entra:TenantId", Tenant);
            b.UseSetting("Authentication:Entra:ClientId", "44444444-4444-4444-4444-444444444444");
            b.UseSetting("Authentication:Entra:ClientSecret", "from-server-settings");
        });
        await using var _ = factory;
        var admin = await fixture.SignInAsync(HelpdeskFactory.PlatformAdminSubject, "Platform Admin", factory: factory);
        var status = (await admin.Client.GetAsync(Admin)).Expect(HttpStatusCode.OK);
        Assert.Equal("server", status.Json.GetProperty("source").GetString());
        Assert.DoesNotContain("from-server-settings", status.Body, StringComparison.Ordinal);
        (await admin.Client.PostAsync($"{Admin}/setup/start", new { })).Expect(HttpStatusCode.Conflict);
        (await admin.Client.PostAsync($"{Admin}/disconnect", new { })).Expect(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Disconnecting_removes_the_stored_settings_and_sign_in_goes_back_to_needing_setup()
    {
        var (factory, stub) = Variant();
        await using var _ = factory;
        var admin = await fixture.SignInAsync(HelpdeskFactory.PlatformAdminSubject, "Platform Admin", factory: factory);
        var id = await StartAndSignInAsync(admin, stub);
        (await admin.Client.PostAsync($"{Admin}/setup/create", new { sessionId = id, redirectOrigins = Origins })).Expect(HttpStatusCode.OK);

        (await admin.Client.PostAsync($"{Admin}/disconnect", new { })).Expect(HttpStatusCode.NoContent);
        Assert.Equal(0, await fixture.CountAsync("SELECT count(*) FROM identity.entra_settings"));
        Assert.Contains("\"entra\":false", (await admin.Client.GetAsync("/api/auth/providers")).Body, StringComparison.Ordinal);
        using var anonymous = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync("/api/auth/login?returnUrl=/")).StatusCode);
        (await admin.Client.PostAsync($"{Admin}/disconnect", new { })).Expect(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task With_no_sign_in_configured_anywhere_the_app_starts_and_says_sign_in_needs_setup()
    {
        // Microsoft sign-in can be connected later from Administration > Microsoft 365, so nothing configured is not a startup failure.
        var (factory, _) = Variant(b => b.UseSetting("Authentication:DevSignIn:Enabled", "false"));
        await using var _ = factory;
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var providers = await client.GetAsync("/api/auth/providers");
        Assert.Equal(HttpStatusCode.OK, providers.StatusCode);
        Assert.Contains("\"entra\":false", await providers.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/auth/login?returnUrl=/")).StatusCode);
    }

    [Fact]
    public async Task Once_connected_the_sign_in_challenge_uses_the_new_application_and_the_registered_address_for_the_host_asked()
    {
        var (factory, stub) = Variant(b => b.ConfigureTestServices(s => s.PostConfigure<Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectOptions>(Helpdesk.Modules.Identity.Authentication.AuthSchemes.Entra, o =>
            o.ConfigurationManager = new Microsoft.IdentityModel.Protocols.StaticConfigurationManager<Microsoft.IdentityModel.Protocols.OpenIdConnect.OpenIdConnectConfiguration>(new Microsoft.IdentityModel.Protocols.OpenIdConnect.OpenIdConnectConfiguration
            {
                Issuer = $"https://login.microsoftonline.com/{Tenant}/v2.0",
                AuthorizationEndpoint = $"https://login.microsoftonline.com/{Tenant}/oauth2/v2.0/authorize",
                TokenEndpoint = $"https://login.microsoftonline.com/{Tenant}/oauth2/v2.0/token",
            }))));
        await using var _ = factory;
        var admin = await fixture.SignInAsync(HelpdeskFactory.PlatformAdminSubject, "Platform Admin", factory: factory);
        var id = await StartAndSignInAsync(admin, stub);
        (await admin.Client.PostAsync($"{Admin}/setup/create", new { sessionId = id, redirectOrigins = Origins })).Expect(HttpStatusCode.OK);

        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Host = "helpdesk.example.org";
        var response = await client.GetAsync("/api/auth/login?returnUrl=/");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!.ToString();
        Assert.StartsWith($"https://login.microsoftonline.com/{Tenant}/oauth2/v2.0/authorize", location, StringComparison.Ordinal);
        Assert.Contains("client_id=11111111-aaaa-bbbb-cccc-222222222222", location, StringComparison.Ordinal);
        Assert.Contains("redirect_uri=" + Uri.EscapeDataString("https://helpdesk.example.org/api/auth/signin-oidc"), location, StringComparison.Ordinal); // https although the request reached the app as http
        Assert.DoesNotContain(Secret, location, StringComparison.Ordinal);
    }
}
