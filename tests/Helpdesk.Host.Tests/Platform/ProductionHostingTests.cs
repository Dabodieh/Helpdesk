using System.Net;
using System.Net.WebSockets;
using Helpdesk.Host.Tests.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Helpdesk.Host.Tests.Platform;

/// <summary>Production hosting rules (ADR-020): trusted-proxy handling, start-up guards, application page headers, SPA serving.</summary>
public sealed class ProductionHostingTests(HelpdeskFixture fixture) : IClassFixture<HelpdeskFixture>
{
    private const string Spoofed = "198.51.100.77";

    private WebApplicationFactory<Program> Variant(Action<IWebHostBuilder> configure) =>
        fixture.Factory.WithWebHostBuilder(b => configure(b));

    /// <summary>Runs the real forwarded-headers middleware, configured as the host configures it, for a caller at <paramref name="remote"/> that claims to be <see cref="Spoofed"/>.</summary>
    private static async Task<IPAddress?> EffectiveClientAsync(WebApplicationFactory<Program> factory, string remote)
    {
        var options = factory.Services.GetRequiredService<IOptions<ForwardedHeadersOptions>>();
        IPAddress? seen = null;
        var middleware = new ForwardedHeadersMiddleware(
            ctx =>
            {
                seen = ctx.Connection.RemoteIpAddress;
                return Task.CompletedTask;
            },
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance,
            options);
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(remote);
        context.Request.Headers["X-Forwarded-For"] = Spoofed;
        context.Request.Headers["X-Forwarded-Proto"] = "https";
        await middleware.Invoke(context);
        return seen;
    }

    [Fact]
    public async Task Forwarded_headers_are_honoured_only_from_the_configured_proxy_and_never_by_default()
    {
        // Nothing configured: nobody is trusted, not even loopback (SRE-006 verified against the framework behaviour).
        Assert.Equal(IPAddress.Parse("203.0.113.9"), await EffectiveClientAsync(fixture.Factory, "203.0.113.9"));
        Assert.Equal(IPAddress.Parse("127.0.0.1"), await EffectiveClientAsync(fixture.Factory, "127.0.0.1"));

        var configured = Variant(b =>
        {
            b.UseSetting("Proxy:KnownProxies:0", "10.0.0.2");
            b.UseSetting("Proxy:KnownNetworks:0", "172.20.0.0/16");
        });
        Assert.Equal(IPAddress.Parse(Spoofed), await EffectiveClientAsync(configured, "10.0.0.2"));
        Assert.Equal(IPAddress.Parse(Spoofed), await EffectiveClientAsync(configured, "172.20.4.5"));
        Assert.Equal(IPAddress.Parse("203.0.113.9"), await EffectiveClientAsync(configured, "203.0.113.9"));
    }

    [Theory]
    [InlineData("Proxy:KnownProxies:0", "", "Proxy:KnownProxies")]
    [InlineData("AllowedHosts", "*", "AllowedHosts")]
    [InlineData("Storage:Provider", "InMemory", "Storage:Provider")]
    [InlineData("Attachments:RequireCleanScan", "false", "RequireCleanScan")]
    [InlineData("Attachments:Enabled", "true", "Attachments:Enabled")]
    public void A_production_host_refuses_unsafe_settings(string key, string value, string expected)
    {
        var factory = Variant(b =>
        {
            b.UseEnvironment("Production");
            b.UseSetting("Authentication:DevSignIn:Enabled", "false");
            b.UseSetting("Authentication:Entra:TenantId", "33333333-3333-3333-3333-333333333333");
            b.UseSetting("Authentication:Entra:ClientId", "44444444-4444-4444-4444-444444444444");
            b.UseSetting("Authentication:Entra:ClientSecret", "test-secret-not-real");
            b.UseSetting("Mail:Provider", "Graph");
            b.UseSetting("Mail:NotificationBaseUrl", "https://helpdesk.example.test");
            b.UseSetting("Mail:Graph:TenantId", "11111111-1111-1111-1111-111111111111");
            b.UseSetting("Mail:Graph:ClientId", "22222222-2222-2222-2222-222222222222");
            b.UseSetting("Mail:Graph:ClientSecret", "test-secret-not-real");
            b.UseSetting("Storage:Provider", "S3");
            b.UseSetting("Storage:S3:AccessKey", "a");
            b.UseSetting("Storage:S3:SecretKey", "b");
            b.UseSetting("Attachments:RequireCleanScan", "true");
            b.UseSetting("Attachments:Enabled", "false");
            b.UseSetting("AllowedHosts", "helpdesk.example.test");
            b.UseSetting("Proxy:KnownProxies:0", "10.0.0.2");
            b.UseSetting("Jobs:Enabled", "false");
            b.UseSetting(key, value);
        });

        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains(expected, ex.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_websocket_from_a_foreign_origin_is_refused_by_the_realtime_hub_and_a_same_origin_one_is_not()
    {
        // Sign in WITHOUT a cookie jar so the session cookie can be replayed on the socket, like a browser page of another origin would.
        using var http = fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        var login = await http.GetAsync("/api/auth/dev-login?subject=ws-origin&name=ws-origin");
        var cookie = string.Join("; ", login.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0]));
        Assert.NotEmpty(cookie);

        async Task<WebSocketState> ConnectAsync(string origin)
        {
            var client = fixture.Factory.Server.CreateWebSocketClient();
            client.ConfigureRequest = r =>
            {
                r.Headers["Cookie"] = cookie;
                r.Headers["Origin"] = origin;
            };
            using var socket = await client.ConnectAsync(new Uri("ws://localhost/hubs/tickets"), CancellationToken.None);
            return socket.State;
        }

        Assert.Equal(WebSocketState.Open, await ConnectAsync("http://localhost"));
        await Assert.ThrowsAnyAsync<Exception>(() => ConnectAsync("https://evil.example.test"));
    }

    [Fact]
    public async Task The_page_the_host_serves_carries_framing_and_content_security_headers_and_unknown_server_paths_stay_404()
    {
        var root = Directory.CreateTempSubdirectory("hd-spa").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "assets"));
            await File.WriteAllTextAsync(Path.Combine(root, "index.html"), "<!doctype html><title>Helpdesk</title><div id=root></div>");
            await File.WriteAllTextAsync(Path.Combine(root, "assets", "app-abc123.js"), "console.log(1)");
            var client = fixture.CreateAnonymousClient(Variant(b => b.UseSetting("Spa:Root", root)));

            // A client route returns the application page, with the page policy, anonymous.
            var page = (await client.GetAsync("/tickets/IT-1")).Expect(HttpStatusCode.OK);
            Assert.Contains("<div id=root>", page.Body, StringComparison.Ordinal);
            Assert.Contains("frame-ancestors 'none'", page.Message.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);
            Assert.Contains("script-src 'self'", page.Message.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);
            Assert.Equal("DENY", page.Message.Headers.GetValues("X-Frame-Options").Single());
            Assert.Equal("nosniff", page.Message.Headers.GetValues("X-Content-Type-Options").Single());
            Assert.Equal("no-referrer", page.Message.Headers.GetValues("Referrer-Policy").Single());
            Assert.Equal("no-cache", page.Message.Headers.CacheControl?.ToString());

            // Fingerprinted assets are cacheable; the API keeps its own policy and an unknown API path is NOT answered with the page.
            var asset = (await client.GetAsync("/assets/app-abc123.js")).Expect(HttpStatusCode.OK);
            Assert.Contains("immutable", asset.Message.Headers.CacheControl?.ToString(), StringComparison.Ordinal);
            var api = await client.GetAsync("/api/does-not-exist");
            Assert.NotEqual(HttpStatusCode.OK, api.Status);
            Assert.DoesNotContain("<div id=root>", api.Body, StringComparison.Ordinal);
            Assert.Equal("default-src 'none'; frame-ancestors 'none'", api.Message.Headers.GetValues("Content-Security-Policy").Single());
            (await client.GetAsync("/hubs/nothing")).Expect(HttpStatusCode.NotFound);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
