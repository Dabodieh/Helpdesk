using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;

namespace Helpdesk.Host.Tests;

public sealed class HealthEndpointTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private HttpClient CreateClient() => factory.WithWebHostBuilder(b =>
        b.UseSetting("Database:ConnectionString", "Host=localhost;Database=unused;Username=x;Password=x")).CreateClient();

    [Fact]
    public async Task Live_returns_ok_without_database()
    {
        var response = await CreateClient().GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Ready_reports_unhealthy_when_database_unreachable()
    {
        var response = await CreateClient().GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }
}
