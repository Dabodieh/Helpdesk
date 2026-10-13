using Microsoft.Extensions.Configuration;
using System.Net;
using Helpdesk.Host.Tests.Infrastructure;
using Helpdesk.Host.Tests.Ticketing;
using Microsoft.AspNetCore.Hosting;

namespace Helpdesk.Host.Tests.Platform;

/// <summary>Per-user rate limits on the abuse-prone endpoints (SEC-003). Limits are lowered through configuration so the test stays small.</summary>
public sealed class AbuseControlTests(HelpdeskFixture fixture) : IClassFixture<HelpdeskFixture>
{
    [Fact]
    public async Task Portal_create_reply_and_search_are_limited_per_signed_in_user_and_the_rejection_is_a_429_with_a_code()
    {
        var factory = fixture.Factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("RateLimits:PortalCreate:Permits", "2");
            b.UseSetting("RateLimits:PortalReply:Permits", "2");
            b.UseSetting("RateLimits:Search:Permits", "2");
        });
        var one = await fixture.SignInAsync($"abuse-{Unique.Id()}", factory: factory);
        var other = await fixture.SignInAsync($"abuse-{Unique.Id()}", factory: factory);

        // Invalid bodies are fine for this purpose: the limiter runs before the handler, so only the count matters.
        var create = new { formId = Guid.NewGuid(), subject = "x", description = "y", fields = new Dictionary<string, object>(), submissionId = Guid.NewGuid() };
        for (var i = 0; i < 2; i++)
        {
            Assert.NotEqual(HttpStatusCode.TooManyRequests, (await one.Client.PostAsync("/api/portal/requests", create)).Status);
        }

        var limited = await one.Client.PostAsync("/api/portal/requests", create);
        limited.ExpectCode(HttpStatusCode.TooManyRequests, "rate_limited");
        Assert.True(limited.Message.Headers.Contains("Retry-After"));
        Assert.NotEqual(HttpStatusCode.TooManyRequests, (await other.Client.PostAsync("/api/portal/requests", create)).Status); // another account is unaffected

        for (var i = 0; i < 2; i++)
        {
            Assert.NotEqual(HttpStatusCode.TooManyRequests, (await one.Client.PostAsync("/api/portal/requests/IT-1/replies", new { body = "hello" })).Status);
        }

        (await one.Client.PostAsync("/api/portal/requests/IT-1/replies", new { body = "hello" })).ExpectCode(HttpStatusCode.TooManyRequests, "rate_limited");

        // Search is counted only when there is a search term; the plain queue is never limited.
        for (var i = 0; i < 5; i++)
        {
            (await one.Client.GetAsync("/api/tickets?queue=all")).Expect(HttpStatusCode.OK);
        }

        for (var i = 0; i < 2; i++)
        {
            (await one.Client.GetAsync("/api/tickets?queue=all&q=printer")).Expect(HttpStatusCode.OK);
        }

        (await one.Client.GetAsync("/api/tickets?queue=all&q=printer")).ExpectCode(HttpStatusCode.TooManyRequests, "rate_limited");
    }

    private static string Partition(string method, string path, string? address = "203.0.113.7")
    {
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        context.Connection.RemoteIpAddress = address is null ? null : IPAddress.Parse(address);
        return Helpdesk.Host.Hosting.AbuseControls.InfrastructurePartition(context, new Helpdesk.Host.Hosting.RateLimitOptions()).PartitionKey;
    }

    [Fact]
    public void Infrastructure_limits_pick_the_unauthenticated_surface_by_address_and_administrative_writes_but_not_ticket_work()
    {
        Assert.Equal("webhook:203.0.113.7", Partition("POST", "/api/mail/graph/notifications"));
        Assert.Equal("auth:203.0.113.7", Partition("GET", "/api/auth/login"));
        Assert.Equal("auth:203.0.113.7", Partition("GET", "/api/auth/dev-login"));
        Assert.Equal("infrastructure-none", Partition("GET", "/api/auth/providers")); // the cheap probe the sign-in page needs
        Assert.StartsWith("invitation:", Partition("POST", "/api/invitations/accept"));
        Assert.StartsWith("admin:", Partition("POST", "/api/admin/microsoft-365/check-connection"));
        Assert.StartsWith("admin:", Partition("PUT", "/api/departments/00000000-0000-0000-0000-000000000001/members/00000000-0000-0000-0000-000000000002"));
        Assert.StartsWith("admin:", Partition("POST", "/api/departments"));
        Assert.Equal("infrastructure-none", Partition("GET", "/api/departments/00000000-0000-0000-0000-000000000001/members")); // reads are not limited here
        Assert.Equal("infrastructure-none", Partition("POST", "/api/departments/00000000-0000-0000-0000-000000000001/tickets")); // agents' ordinary work
        Assert.Equal("infrastructure-none", Partition("PATCH", "/api/tickets/IT-1"));
    }

    [Fact]
    public void Infrastructure_limits_cannot_be_switched_off_in_production_and_are_opt_in_for_development_and_testing()
    {
        static Microsoft.Extensions.Configuration.IConfiguration Config(bool? enabled) =>
            new Microsoft.Extensions.Configuration.ConfigurationBuilder()
                .AddInMemoryCollection(enabled is null ? [] : new Dictionary<string, string?> { ["RateLimits:Infrastructure:Enabled"] = enabled.Value.ToString() })
                .Build();
        static Microsoft.Extensions.Hosting.IHostEnvironment Env(string name) => new Microsoft.Extensions.Hosting.Internal.HostingEnvironment { EnvironmentName = name };
        Assert.True(Helpdesk.Host.Hosting.AbuseControls.InfrastructureLimitsEnabled(Config(null), Env("Production")));
        Assert.True(Helpdesk.Host.Hosting.AbuseControls.InfrastructureLimitsEnabled(Config(false), Env("Production"))); // no switch in production
        Assert.False(Helpdesk.Host.Hosting.AbuseControls.InfrastructureLimitsEnabled(Config(null), Env("Development")));
        Assert.False(Helpdesk.Host.Hosting.AbuseControls.InfrastructureLimitsEnabled(Config(null), Env("Testing")));
        Assert.True(Helpdesk.Host.Hosting.AbuseControls.InfrastructureLimitsEnabled(Config(true), Env("Testing")));
    }

    [Fact]
    public async Task Sign_in_and_webhook_endpoints_are_limited_per_client_address_when_the_infrastructure_limits_are_on()
    {
        var factory = fixture.Factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("RateLimits:Infrastructure:Enabled", "true");
            b.UseSetting("RateLimits:Authentication:Permits", "2");
            b.UseSetting("RateLimits:Webhooks:Permits", "2");
        });
        var client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        for (var i = 0; i < 2; i++)
        {
            Assert.NotEqual(HttpStatusCode.TooManyRequests, (await client.GetAsync("/api/auth/dev-login?subject=")).StatusCode);
            Assert.NotEqual(HttpStatusCode.TooManyRequests, (await client.PostAsync("/api/mail/graph/notifications", new StringContent("{}"))).StatusCode);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.GetAsync("/api/auth/dev-login?subject=")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.PostAsync("/api/mail/graph/notifications", new StringContent("{}"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/providers")).StatusCode); // never limited
    }

    [Fact]
    public async Task A_spoofed_forwarded_client_address_cannot_dodge_a_limit_when_no_proxy_is_trusted()
    {
        var factory = fixture.Factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("RateLimits:Infrastructure:Enabled", "true");
            b.UseSetting("RateLimits:Authentication:Permits", "2");
        });
        var client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 4; i++)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/dev-login?subject=");
            request.Headers.Add("X-Forwarded-For", $"198.51.100.{i + 1}"); // a different "client" every time
            statuses.Add((await client.SendAsync(request)).StatusCode);
        }

        Assert.DoesNotContain(HttpStatusCode.TooManyRequests, statuses.Take(2));
        Assert.All(statuses.Skip(2), s => Assert.Equal(HttpStatusCode.TooManyRequests, s));
    }
}
