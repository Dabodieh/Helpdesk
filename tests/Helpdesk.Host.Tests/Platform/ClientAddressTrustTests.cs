using System.Net;
using Helpdesk.Host.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Helpdesk.Host.Tests.Platform;

/// <summary>
/// S-03: which client address the infrastructure rate limits see behind a reverse proxy. The real host pipeline runs (forwarded headers, then authentication, then the
/// limiter); only the TCP peer address is simulated, from the <c>X-Test-Peer</c> header, because a test server has no socket. This is synthetic evidence about the
/// application. It says nothing about what Cloudflare or cloudflared actually send; that needs the controlled live-tunnel check in the S-03 record.
/// </summary>
public sealed class ClientAddressTrustTests(HelpdeskFixture fixture) : IClassFixture<HelpdeskFixture>
{
    private const string TrustedPeer = "172.25.0.9";   // inside the compose network range
    private const string UntrustedPeer = "203.0.113.50";

    /// <summary>Sets the connection's remote address from a test header, before the application's own pipeline (so before forwarded headers).</summary>
    private sealed class SimulatedPeer : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nxt) =>
            {
                if (context.Request.Headers.TryGetValue("X-Test-Peer", out var peer))
                {
                    context.Connection.RemoteIpAddress = IPAddress.Parse(peer.ToString());
                }

                return nxt(context);
            });
            next(app);
        };
    }

    private HttpClient Client(int authenticationPermits = 2, bool trustComposeNetwork = true) =>
        fixture.Factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("RateLimits:Infrastructure:Enabled", "true");
            b.UseSetting("RateLimits:Authentication:Permits", authenticationPermits.ToString(System.Globalization.CultureInfo.InvariantCulture));
            b.UseSetting("RateLimits:Webhooks:Permits", authenticationPermits.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (trustComposeNetwork)
            {
                b.UseSetting("Proxy:KnownNetworks:0", "172.16.0.0/12");
            }

            b.ConfigureServices(s => s.AddTransient<IStartupFilter, SimulatedPeer>());
        }).CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static async Task<HttpStatusCode> SignInAsync(HttpClient client, string peer, string? forwardedFor)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/dev-login?subject=");
        request.Headers.Add("X-Test-Peer", peer);
        if (forwardedFor is not null)
        {
            request.Headers.TryAddWithoutValidation("X-Forwarded-For", forwardedFor);
        }

        return (await client.SendAsync(request)).StatusCode;
    }

    private static async Task<HttpStatusCode> WebhookAsync(HttpClient client, string peer, string? forwardedFor)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/mail/graph/notifications") { Content = new StringContent("{}") };
        request.Headers.Add("X-Test-Peer", peer);
        if (forwardedFor is not null)
        {
            request.Headers.TryAddWithoutValidation("X-Forwarded-For", forwardedFor);
        }

        return (await client.SendAsync(request)).StatusCode;
    }

    [Fact]
    public async Task Behind_a_trusted_proxy_each_real_client_has_its_own_bucket_so_users_do_not_share_one()
    {
        var client = Client();

        // Client A uses its two permits and is then limited ...
        Assert.NotEqual(HttpStatusCode.TooManyRequests, await SignInAsync(client, TrustedPeer, "203.0.113.10"));
        Assert.NotEqual(HttpStatusCode.TooManyRequests, await SignInAsync(client, TrustedPeer, "203.0.113.10"));
        Assert.Equal(HttpStatusCode.TooManyRequests, await SignInAsync(client, TrustedPeer, "203.0.113.10"));

        // ... while other people behind the very same proxy connection are untouched.
        Assert.NotEqual(HttpStatusCode.TooManyRequests, await SignInAsync(client, TrustedPeer, "203.0.113.11"));
        Assert.NotEqual(HttpStatusCode.TooManyRequests, await SignInAsync(client, TrustedPeer, "203.0.113.12"));
    }

    [Fact]
    public async Task A_client_cannot_dodge_the_limit_by_forging_extra_entries_in_front_of_the_address_the_proxy_appended()
    {
        var client = Client();
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 4; i++)
        {
            // The edge appends the real address on the RIGHT; whatever the client put on the left is its own claim.
            statuses.Add(await SignInAsync(client, TrustedPeer, $"198.51.100.{i + 1}, 203.0.113.20"));
        }

        Assert.DoesNotContain(HttpStatusCode.TooManyRequests, statuses.Take(2));
        Assert.All(statuses.Skip(2), s => Assert.Equal(HttpStatusCode.TooManyRequests, s));

        // A longer forged chain changes nothing either.
        Assert.Equal(HttpStatusCode.TooManyRequests, await SignInAsync(client, TrustedPeer, "1.1.1.1, 2.2.2.2, 3.3.3.3, 203.0.113.20"));
    }

    [Fact]
    public async Task A_caller_that_is_not_a_trusted_proxy_cannot_choose_its_address_with_forwarding_headers()
    {
        var client = Client();
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 4; i++)
        {
            statuses.Add(await SignInAsync(client, UntrustedPeer, $"198.51.100.{i + 1}"));
        }

        Assert.DoesNotContain(HttpStatusCode.TooManyRequests, statuses.Take(2));
        Assert.All(statuses.Skip(2), s => Assert.Equal(HttpStatusCode.TooManyRequests, s));
    }

    [Fact]
    public async Task Missing_empty_or_invalid_forwarded_headers_fall_back_to_the_proxy_address_and_never_open_a_bypass()
    {
        foreach (var header in new string?[] { null, "", "not-an-address", "999.1.1.1", "203.0.113.30:abc" })
        {
            var client = Client();
            var statuses = new List<HttpStatusCode>();
            for (var i = 0; i < 4; i++)
            {
                statuses.Add(await SignInAsync(client, TrustedPeer, header));
            }

            Assert.All(statuses.Skip(2), s => Assert.Equal(HttpStatusCode.TooManyRequests, s));
        }
    }

    [Fact]
    public async Task The_webhook_limit_is_per_forwarded_client_and_cannot_be_dodged_by_forged_entries()
    {
        var client = Client();
        Assert.NotEqual(HttpStatusCode.TooManyRequests, await WebhookAsync(client, TrustedPeer, "9.9.9.9, 52.96.0.10"));
        Assert.NotEqual(HttpStatusCode.TooManyRequests, await WebhookAsync(client, TrustedPeer, "8.8.8.8, 52.96.0.10"));
        Assert.Equal(HttpStatusCode.TooManyRequests, await WebhookAsync(client, TrustedPeer, "7.7.7.7, 52.96.0.10"));

        // A different sender (another Microsoft address) is not affected, and a spoofer from an untrusted peer is keyed by its own address.
        Assert.NotEqual(HttpStatusCode.TooManyRequests, await WebhookAsync(client, TrustedPeer, "52.96.0.11"));
        Assert.NotEqual(HttpStatusCode.TooManyRequests, await WebhookAsync(client, UntrustedPeer, "52.96.0.10"));
    }

    [Fact]
    public async Task Rotating_addresses_inside_one_ipv6_prefix_does_not_give_a_client_fresh_buckets()
    {
        var client = Client();
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++)
        {
            // One subscriber normally holds a whole /64; every address in it belongs to the same person.
            statuses.Add(await SignInAsync(client, TrustedPeer, $"2001:db8:aaaa:bbbb::{i + 1:x}"));
        }

        Assert.DoesNotContain(HttpStatusCode.TooManyRequests, statuses.Take(2));
        Assert.All(statuses.Skip(2), s => Assert.Equal(HttpStatusCode.TooManyRequests, s));

        // A different /64 is a different client.
        Assert.NotEqual(HttpStatusCode.TooManyRequests, await SignInAsync(client, TrustedPeer, "2001:db8:aaaa:cccc::1"));
    }

    [Fact]
    public async Task An_ipv4_address_and_its_ipv4_mapped_ipv6_form_share_one_bucket()
    {
        var client = Client();
        Assert.NotEqual(HttpStatusCode.TooManyRequests, await SignInAsync(client, TrustedPeer, "203.0.113.40"));
        Assert.NotEqual(HttpStatusCode.TooManyRequests, await SignInAsync(client, TrustedPeer, "::ffff:203.0.113.40"));
        Assert.Equal(HttpStatusCode.TooManyRequests, await SignInAsync(client, TrustedPeer, "203.0.113.40"));
    }

    [Fact]
    public async Task With_no_trusted_proxy_configured_every_forwarded_header_is_ignored_even_from_the_compose_range()
    {
        var client = Client(trustComposeNetwork: false);
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 4; i++)
        {
            statuses.Add(await SignInAsync(client, TrustedPeer, $"203.0.113.{60 + i}"));
        }

        Assert.All(statuses.Skip(2), s => Assert.Equal(HttpStatusCode.TooManyRequests, s));
    }

    [Theory]
    [InlineData("203.0.113.7", "203.0.113.7")]
    [InlineData("::ffff:203.0.113.7", "203.0.113.7")]
    [InlineData("2001:db8:1:2:3:4:5:6", "2001:db8:1:2::/64")]
    [InlineData("2001:db8:1:2::1", "2001:db8:1:2::/64")]
    [InlineData("::1", "::/64")]
    public void The_rate_limit_identity_maps_ipv4_mapped_addresses_to_ipv4_and_reduces_ipv6_to_its_64_bit_prefix(string address, string expected)
    {
        Assert.Equal(expected, Helpdesk.Host.Hosting.AbuseControls.ClientKey(IPAddress.Parse(address)));
        Assert.Null(Helpdesk.Host.Hosting.AbuseControls.ClientKey(null));
    }

    [Fact]
    public async Task Https_is_taken_from_a_trusted_proxy_only_and_the_client_address_is_the_rightmost_entry()
    {
        var factory = fixture.Factory.WithWebHostBuilder(b => b.UseSetting("Proxy:KnownNetworks:0", "172.16.0.0/12"));
        var options = factory.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.Builder.ForwardedHeadersOptions>>();
        Assert.Equal(1, options.Value.ForwardLimit);

        async Task<(string Scheme, IPAddress? Address)> RunAsync(string peer, string forwardedFor, string proto)
        {
            var seen = (Scheme: "", Address: (IPAddress?)null);
            var middleware = new Microsoft.AspNetCore.HttpOverrides.ForwardedHeadersMiddleware(
                ctx =>
                {
                    seen = (ctx.Request.Scheme, ctx.Connection.RemoteIpAddress);
                    return Task.CompletedTask;
                },
                Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance,
                options);
            var context = new DefaultHttpContext();
            context.Request.Scheme = "http";
            context.Connection.RemoteIpAddress = IPAddress.Parse(peer);
            context.Request.Headers["X-Forwarded-For"] = forwardedFor;
            context.Request.Headers["X-Forwarded-Proto"] = proto;
            await middleware.Invoke(context);
            return seen;
        }

        var trusted = await RunAsync(TrustedPeer, "198.51.100.1, 203.0.113.77", "https");
        Assert.Equal("https", trusted.Scheme);
        Assert.Equal(IPAddress.Parse("203.0.113.77"), trusted.Address);

        var untrusted = await RunAsync(UntrustedPeer, "198.51.100.1, 203.0.113.77", "https");
        Assert.Equal("http", untrusted.Scheme);
        Assert.Equal(IPAddress.Parse(UntrustedPeer), untrusted.Address);
    }
}
