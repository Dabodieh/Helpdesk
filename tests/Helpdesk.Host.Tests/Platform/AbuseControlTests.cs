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
}
