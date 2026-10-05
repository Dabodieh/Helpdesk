using System.Net;
using Helpdesk.Host.Tests.Infrastructure;

namespace Helpdesk.Host.Tests.Platform;

public sealed class HealthEndpointTests(HelpdeskFixture fixture) : IClassFixture<HelpdeskFixture>
{
    [Fact]
    public async Task Live_is_anonymous_and_ok()
    {
        (await fixture.CreateAnonymousClient().GetAsync("/health/live")).Expect(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Ready_is_anonymous_and_includes_the_database()
    {
        (await fixture.CreateAnonymousClient().GetAsync("/health/ready")).Expect(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Ready_reports_unhealthy_when_database_unreachable()
    {
        var factory = fixture.Factory.WithWebHostBuilder(b =>
            b.UseSetting("Database:ConnectionString", "Host=localhost;Port=1;Database=unused;Username=x;Password=x;Timeout=2"));
        var client = fixture.CreateAnonymousClient(factory);
        (await client.GetAsync("/health/ready")).Expect(HttpStatusCode.ServiceUnavailable);
    }
}
