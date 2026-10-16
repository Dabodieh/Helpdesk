using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Helpdesk.Host.Tests.Infrastructure;
using Helpdesk.Host.Tests.Ticketing;
using Helpdesk.Modules.Mail;
using Helpdesk.Modules.Mail.Contracts;
using Helpdesk.Modules.Mail.Providers;
using Helpdesk.Modules.Mail.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Helpdesk.Host.Tests.Mail;

/// <summary>
/// Inbound mail modes. Webhook (default): subscription failures and delta (mail check) failures are recorded in separate fields so neither hides the other, and a failed
/// renewal keeps the subscription id. Polling: an intentional mode with no subscription calls at all, reported as such rather than as a failed subscription, while the
/// delta synchronisation keeps running, stored subscription data and the delta token are left alone, and real delta failures stay visible.
/// </summary>
public sealed class InboundModeTests(HelpdeskFixture fixture) : IClassFixture<HelpdeskFixture>
{
    private static Task MaintainAsync(WebApplicationFactory<Program> factory) =>
        RunAsync(factory, sp => sp.GetRequiredService<IMailJobRunner>().RunSubscriptionMaintenanceAsync());

    private static async Task RunAsync(WebApplicationFactory<Program> factory, Func<IServiceProvider, Task> action)
    {
        using var scope = factory.Services.CreateScope();
        await action(scope.ServiceProvider);
    }

    private static Task SyncAsync(WebApplicationFactory<Program> factory, Mailbox box) =>
        RunAsync(factory, sp => sp.GetRequiredService<IMailJobRunner>().RunSyncAsync(box.Id));

    private WebApplicationFactory<Program> PollingHost() => fixture.Factory.WithWebHostBuilder(b => b.UseSetting("Mail:InboundMode", "Polling"));

    private static async Task<HttpClient> AdminClientAsync(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        await client.GetAsync($"/api/auth/dev-login?subject={HelpdeskFactory.PlatformAdminSubject}&name=Platform%20Admin&email=platform-admin@example.test&returnUrl=/");
        var csrf = JsonDocument.Parse(await client.GetStringAsync("/api/csrf")).RootElement.GetProperty("token").GetString()!;
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", csrf);
        return client;
    }

    private static async Task<JsonElement> MailboxAsync(HttpClient client, Dept d, Guid mailboxId)
    {
        var list = JsonDocument.Parse(await client.GetStringAsync($"{d.Department.Url}/mailboxes")).RootElement;
        return list.EnumerateArray().Single(m => m.GetProperty("id").GetGuid() == mailboxId).Clone();
    }

    private Task<string?> SyncColumnAsync(Guid mailboxId, string column) =>
        fixture.ScalarAsync<string>($"SELECT {column}::text FROM mail.mailbox_sync WHERE mailbox_id = @m", TicketKit.P("m", mailboxId));

    // ------------------------------------------------------------------ webhook mode

    [Fact]
    public async Task Subscription_and_mail_check_failures_are_recorded_separately_and_a_failed_renewal_keeps_the_subscription_id()
    {
        var d = await fixture.NewDeptAsync();
        var box = await fixture.CreateMailboxAsync(d);
        var fake = fixture.Fake();
        var held = await SyncColumnAsync(box.Id, "subscription_id");
        Assert.False(string.IsNullOrEmpty(held));

        // A renewal that Graph refuses (validation, not "gone"): Failed, the error is kept in its OWN field with the provider's detail, the id is kept, the delta fields are untouched.
        await fixture.ExecuteAsync("UPDATE mail.mailbox_sync SET subscription_expires_at = now() + interval '1 hour' WHERE mailbox_id = @m", TicketKit.P("m", box.Id));
        fake.FailSubscriptionCalls("graph_400");
        await MaintainAsync(fixture.Factory);
        Assert.Equal("Failed", await SyncColumnAsync(box.Id, "subscription_state"));
        Assert.Equal(held, await SyncColumnAsync(box.Id, "subscription_id"));
        var subscriptionError = await SyncColumnAsync(box.Id, "subscription_error");
        Assert.Contains("graph_400", subscriptionError, StringComparison.Ordinal);
        Assert.Contains("ExtensionError", subscriptionError, StringComparison.Ordinal);
        Assert.Null(await SyncColumnAsync(box.Id, "last_sync_error"));
        fake.FailSubscriptionCalls(null);

        // A real mail check (delta) failure is recorded in the delta fields and does not touch the subscription error ...
        fake.FailNextFetches(1);
        await Assert.ThrowsAnyAsync<Exception>(() => SyncAsync(fixture.Factory, box));
        Assert.Contains("graph_503", await SyncColumnAsync(box.Id, "last_sync_error"), StringComparison.Ordinal);
        Assert.Equal("1", await SyncColumnAsync(box.Id, "consecutive_failures"));
        Assert.Equal(subscriptionError, await SyncColumnAsync(box.Id, "subscription_error"));

        // ... and a successful mail check clears only the delta error: the subscription error is no longer erased by the next poll.
        await SyncAsync(fixture.Factory, box);
        Assert.Null(await SyncColumnAsync(box.Id, "last_sync_error"));
        Assert.Equal("0", await SyncColumnAsync(box.Id, "consecutive_failures"));
        Assert.Equal(subscriptionError, await SyncColumnAsync(box.Id, "subscription_error"));

        // What an administrator sees: the two errors under their own names.
        var dto = await MailboxAsync(d.Admin.Client.Http, d, box.Id);
        Assert.Equal("Failed", dto.GetProperty("subscription").GetProperty("state").GetString());
        Assert.Equal(subscriptionError, dto.GetProperty("subscription").GetProperty("error").GetString());
        Assert.Equal(JsonValueKind.Null, dto.GetProperty("lastSyncError").ValueKind);
        var reasons = dto.GetProperty("health").GetProperty("reasons").EnumerateArray().Select(r => r.GetString()).ToList();
        Assert.Contains("subscription_failed", reasons);
        Assert.DoesNotContain("sync_failing", reasons);

        // The next maintenance run renews the SAME id and clears the subscription error.
        await MaintainAsync(fixture.Factory);
        Assert.Equal("Active", await SyncColumnAsync(box.Id, "subscription_state"));
        Assert.Equal(held, await SyncColumnAsync(box.Id, "subscription_id"));
        Assert.Null(await SyncColumnAsync(box.Id, "subscription_error"));
    }

    [Fact]
    public async Task A_subscription_that_graph_no_longer_knows_is_replaced()
    {
        var d = await fixture.NewDeptAsync();
        var box = await fixture.CreateMailboxAsync(d);
        await fixture.ExecuteAsync(
            "UPDATE mail.mailbox_sync SET subscription_id = 'sub-gone', subscription_expires_at = now() + interval '1 hour' WHERE mailbox_id = @m", TicketKit.P("m", box.Id));

        await MaintainAsync(fixture.Factory);

        var id = await SyncColumnAsync(box.Id, "subscription_id");
        Assert.NotEqual("sub-gone", id);
        Assert.StartsWith("sub-", id, StringComparison.Ordinal);
        Assert.Equal("Active", await SyncColumnAsync(box.Id, "subscription_state"));
        Assert.Null(await SyncColumnAsync(box.Id, "subscription_error"));
    }

    // ------------------------------------------------------------------ polling mode

    [Fact]
    public async Task Polling_mode_makes_no_subscription_calls_leaves_stored_subscription_data_and_the_delta_token_alone_and_is_reported_as_intentional()
    {
        var d = await fixture.NewDeptAsync();
        var box = await fixture.CreateMailboxAsync(d);
        await fixture.SyncAsync(box);

        // What an earlier webhook-mode run left behind: a Failed subscription with an id, an error text and a soon-to-expire date.
        await fixture.ExecuteAsync(
            "UPDATE mail.mailbox_sync SET subscription_state = 'Failed', subscription_error = 'earlier error', subscription_expires_at = now() + interval '1 hour' WHERE mailbox_id = @m", TicketKit.P("m", box.Id));
        var id = await SyncColumnAsync(box.Id, "subscription_id");
        var hash = await SyncColumnAsync(box.Id, "encode(client_state_hash, 'hex')");
        var deltaBefore = await SyncColumnAsync(box.Id, "delta_token");
        var expires = await SyncColumnAsync(box.Id, "subscription_expires_at");
        Assert.False(string.IsNullOrEmpty(id));
        Assert.False(string.IsNullOrEmpty(deltaBefore));

        using var polling = PollingHost();
        var fake = polling.Services.GetRequiredService<FakeMailProvider>();
        await MaintainAsync(polling); // would renew in webhook mode
        Assert.Equal(0, fake.SubscriptionCount);

        // Mail still arrives: the delta synchronisation runs and creates the ticket.
        var subject = $"Polling {Unique.Id()}";
        fake.Inject(MailKit.Mail(MailKit.NewSender(), subject) with { Mailbox = box.Address });
        await SyncAsync(polling, box);
        Assert.Single(await d.Agent.TicketsAsync(d, subject));
        Assert.Null(await SyncColumnAsync(box.Id, "last_sync_error"));
        Assert.NotNull(await SyncColumnAsync(box.Id, "delta_token"));

        // Nothing about the stored subscription was touched.
        Assert.Equal("Failed", await SyncColumnAsync(box.Id, "subscription_state"));
        Assert.Equal(id, await SyncColumnAsync(box.Id, "subscription_id"));
        Assert.Equal(hash, await SyncColumnAsync(box.Id, "encode(client_state_hash, 'hex')"));
        Assert.Equal("earlier error", await SyncColumnAsync(box.Id, "subscription_error"));
        Assert.Equal(expires, await SyncColumnAsync(box.Id, "subscription_expires_at"));

        // Reported as an intentional mode, not a failed subscription; healthy; no manual retry.
        using var admin = await AdminClientAsync(polling);
        var dto = await MailboxAsync(admin, d, box.Id);
        var subscription = dto.GetProperty("subscription");
        Assert.Equal("polling", subscription.GetProperty("mode").GetString());
        Assert.Equal("NotUsed", subscription.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, subscription.GetProperty("error").ValueKind);
        Assert.Equal("Healthy", dto.GetProperty("health").GetProperty("status").GetString());
        var retry = await admin.PostAsync($"{d.Department.Url}/mailboxes/{box.Id}/subscription/retry", JsonContent.Create(new { }));
        Assert.Equal(HttpStatusCode.Conflict, retry.StatusCode);
        Assert.Equal(0, fake.SubscriptionCount);

        // The same row seen from webhook mode still says Failed: the record was never rewritten.
        Assert.Equal("Failed", (await MailboxAsync(d.Admin.Client.Http, d, box.Id)).GetProperty("subscription").GetProperty("state").GetString());
    }

    [Fact]
    public async Task A_mailbox_registered_in_polling_mode_gets_no_subscription_and_is_healthy_after_its_first_mail_check()
    {
        var d = await fixture.NewDeptAsync();
        using var polling = PollingHost();
        using var admin = await AdminClientAsync(polling);
        var address = MailKit.NewAddress();
        var created = await admin.PostAsJsonAsync($"{d.Department.Url}/mailboxes", new { address, displayName = $"Desk {Unique.Id()}", signature = "Regards" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();

        Assert.Equal("None", await SyncColumnAsync(id, "subscription_state"));
        Assert.Null(await SyncColumnAsync(id, "subscription_id"));
        Assert.Equal(0, polling.Services.GetRequiredService<FakeMailProvider>().SubscriptionCount);

        await SyncAsync(polling, new Mailbox(id, address, "x", d));
        var dto = await MailboxAsync(admin, d, id);
        Assert.Equal("Healthy", dto.GetProperty("health").GetProperty("status").GetString());
        Assert.Equal("polling", dto.GetProperty("subscription").GetProperty("mode").GetString());
    }

    [Fact]
    public async Task Polling_mode_still_shows_real_mail_check_failures_in_the_health_status_and_the_delta_error_field()
    {
        var d = await fixture.NewDeptAsync();
        var box = await fixture.CreateMailboxAsync(d);
        using var polling = PollingHost();
        using var admin = await AdminClientAsync(polling);
        var fake = polling.Services.GetRequiredService<FakeMailProvider>();

        fake.FailNextFetches(1);
        await Assert.ThrowsAnyAsync<Exception>(() => SyncAsync(polling, box));
        var failing = await MailboxAsync(admin, d, box.Id);
        Assert.Equal("Attention", failing.GetProperty("health").GetProperty("status").GetString());
        Assert.Contains("sync_failing", failing.GetProperty("health").GetProperty("reasons").EnumerateArray().Select(r => r.GetString()));
        Assert.Contains("graph_503", failing.GetProperty("lastSyncError").GetString(), StringComparison.Ordinal);
        Assert.Equal(JsonValueKind.Null, failing.GetProperty("subscription").GetProperty("error").ValueKind);

        // Three failures in a row is a failed mailbox; a good pass recovers it.
        fake.FailNextFetches(2);
        for (var i = 0; i < 2; i++)
        {
            await Assert.ThrowsAnyAsync<Exception>(() => SyncAsync(polling, box));
        }

        Assert.Equal("Failed", (await MailboxAsync(admin, d, box.Id)).GetProperty("health").GetProperty("status").GetString());
        await SyncAsync(polling, box);
        Assert.Equal("Healthy", (await MailboxAsync(admin, d, box.Id)).GetProperty("health").GetProperty("status").GetString());
    }

    // ------------------------------------------------------------------ configuration

    private sealed class Env : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";

        public string ApplicationName { get; set; } = "test";

        public string ContentRootPath { get; set; } = ".";

        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    private static MailOptions Options(string? mode, string? baseUrl)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Mail:Provider"] = "Graph",
            ["Mail:Graph:TenantId"] = "00000000-0000-0000-0000-000000000001",
            ["Mail:Graph:ClientId"] = "00000000-0000-0000-0000-000000000002",
            ["Mail:Graph:ClientSecret"] = "not-a-real-secret",
            ["Mail:NotificationBaseUrl"] = baseUrl,
            ["Database:ConnectionString"] = "Host=unused",
        };
        if (mode is not null)
        {
            settings["Mail:InboundMode"] = mode;
        }

        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMailModule(config, new Env());
        return services.BuildServiceProvider().GetRequiredService<IOptions<MailOptions>>().Value;
    }

    [Theory]
    [InlineData("Polling", null)]
    [InlineData("polling", "")]
    [InlineData("Polling", "http://not-public.example")]
    [InlineData("Webhook", "https://helpdesk.example.test")]
    [InlineData(null, "https://helpdesk.example.test")]
    public void Valid_inbound_mode_settings_are_accepted_and_polling_needs_no_public_url(string? mode, string? baseUrl)
    {
        var options = Options(mode, baseUrl);
        Assert.Equal(string.Equals(mode, "polling", StringComparison.OrdinalIgnoreCase), options.PollingOnly);
    }

    [Theory]
    [InlineData("Webhook", null)]
    [InlineData(null, null)]
    [InlineData("Webhook", "http://not-https.example")]
    [InlineData("Sometimes", "https://helpdesk.example.test")]
    [InlineData("", "https://helpdesk.example.test")]
    public void Invalid_inbound_mode_settings_are_rejected_at_start_up(string? mode, string? baseUrl) =>
        Assert.Throws<OptionsValidationException>(() => Options(mode, baseUrl));
}
