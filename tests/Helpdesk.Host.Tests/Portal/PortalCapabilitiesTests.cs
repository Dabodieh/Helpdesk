using System.Net;
using Helpdesk.Host.Tests.Infrastructure;
using Helpdesk.Modules.Attachments.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Helpdesk.Host.Tests.Portal;

public sealed class PortalCapabilitiesTests(HelpdeskFixture fixture) : IClassFixture<HelpdeskFixture>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Capabilities_require_sign_in_and_report_the_attachment_pipeline_setting(bool enabled)
    {
        await using var app = fixture.Factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.PostConfigure<AttachmentOptions>(options => options.Enabled = enabled)));

        var anonymous = fixture.CreateAnonymousClient(app);
        (await anonymous.GetAsync("/api/portal/capabilities")).Expect(HttpStatusCode.Unauthorized);

        var user = await fixture.SignInAsync($"capability-{Unique.Id()}", factory: app);
        var response = (await user.Client.GetAsync("/api/portal/capabilities")).Expect(HttpStatusCode.OK).Json;
        Assert.Single(response.EnumerateObject());
        Assert.Equal(enabled, response.GetProperty("attachmentsEnabled").GetBoolean());
    }
}
