using System.Net;
using System.Text;
using System.Text.Json;
using Helpdesk.Host.Tests.Infrastructure;
using Helpdesk.Host.Tests.Ticketing;
using Microsoft.Extensions.DependencyInjection;

namespace Helpdesk.Host.Tests.Mail;

/// <summary>The anonymous Graph webhooks: handshake, client-state authentication, duplicates, lifecycle events, size limits.</summary>
public sealed class MailWebhookTests(HelpdeskFixture fixture) : IClassFixture<HelpdeskFixture>
{
    private HttpClient Client() => fixture.Factory.CreateClient(new() { AllowAutoRedirect = false });

    private async Task<(Mailbox Box, string SubscriptionId, string ClientState)> NewSubscribedMailboxAsync()
    {
        var d = await fixture.NewDeptAsync();
        var box = await fixture.CreateMailboxAsync(d);
        var id = (await fixture.ScalarAsync<string>("SELECT subscription_id FROM mail.mailbox_sync WHERE mailbox_id = @m", TicketKit.P("m", box.Id)))!;
        return (box, id, fixture.Fake().ClientStateOf(id)!);
    }

    private static StringContent Payload(object body) => new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    private static object Notification(string subscription, string clientState, string resource = "AAMk-1") => new
    {
        value = new[] { new { subscriptionId = subscription, clientState, changeType = "created", resourceData = new { id = resource } } },
    };

    [Fact]
    public async Task The_validation_handshake_echoes_the_token_as_text_without_a_session_or_csrf_token()
    {
        var response = await Client().PostAsync("/api/mail/graph/notifications?validationToken=Validation%3A+Token%20123", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("text/plain", response.Content.Headers.ContentType!.MediaType, StringComparison.Ordinal);
        Assert.Equal("Validation: Token 123", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_valid_notification_is_accepted_recorded_and_nudges_the_mailbox()
    {
        var s = await NewSubscribedMailboxAsync();

        var response = await Client().PostAsync("/api/mail/graph/notifications", Payload(Notification(s.SubscriptionId, s.ClientState)));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(1, await fixture.CountAsync("SELECT count(*) FROM mail.notifications WHERE mailbox_id = @m AND resource_id = 'AAMk-1'", TicketKit.P("m", s.Box.Id)));
        Assert.True(await fixture.ScalarAsync<bool>("SELECT dirty FROM mail.mailbox_sync WHERE mailbox_id = @m", TicketKit.P("m", s.Box.Id)));
    }

    [Fact]
    public async Task Forged_or_unknown_notifications_are_rejected_silently_with_no_effect()
    {
        var s = await NewSubscribedMailboxAsync();

        var wrongState = await Client().PostAsync("/api/mail/graph/notifications", Payload(Notification(s.SubscriptionId, "not-the-secret")));
        var unknown = await Client().PostAsync("/api/mail/graph/notifications", Payload(Notification("sub-does-not-exist", s.ClientState)));
        var empty = await Client().PostAsync("/api/mail/graph/notifications", Payload(new { value = Array.Empty<object>() }));

        // The same 202 for everything, so the endpoint reveals nothing about which subscriptions exist.
        Assert.All(new[] { wrongState, unknown, empty }, r => Assert.Equal(HttpStatusCode.Accepted, r.StatusCode));
        Assert.Equal(0, await fixture.CountAsync("SELECT count(*) FROM mail.notifications WHERE mailbox_id = @m", TicketKit.P("m", s.Box.Id)));
        Assert.False(await fixture.ScalarAsync<bool>("SELECT dirty FROM mail.mailbox_sync WHERE mailbox_id = @m", TicketKit.P("m", s.Box.Id)));
    }

    [Fact]
    public async Task The_mailbox_comes_from_our_subscription_record_never_from_the_payload()
    {
        var s = await NewSubscribedMailboxAsync();
        var other = await NewSubscribedMailboxAsync();

        // A payload claiming another mailbox through its resource path is still attributed to the subscription's own mailbox.
        var body = new
        {
            value = new[]
            {
                new
                {
                    subscriptionId = s.SubscriptionId, clientState = s.ClientState, changeType = "created",
                    resource = $"users/{other.Box.Address}/mailFolders('inbox')/messages/AAMk-2", resourceData = new { id = "AAMk-2" },
                },
            },
        };
        await Client().PostAsync("/api/mail/graph/notifications", Payload(body));

        Assert.Equal(1, await fixture.CountAsync("SELECT count(*) FROM mail.notifications WHERE mailbox_id = @m", TicketKit.P("m", s.Box.Id)));
        Assert.Equal(0, await fixture.CountAsync("SELECT count(*) FROM mail.notifications WHERE mailbox_id = @m", TicketKit.P("m", other.Box.Id)));
    }

    [Fact]
    public async Task Duplicate_notifications_are_harmless_because_delta_is_the_source_of_truth()
    {
        var s = await NewSubscribedMailboxAsync();
        var subject = $"Webhook {Unique.Id()}";
        var id = fixture.Fake().Inject(MailKit.Mail(MailKit.NewSender(), subject) with { Mailbox = s.Box.Address });

        for (var i = 0; i < 3; i++)
        {
            await Client().PostAsync("/api/mail/graph/notifications", Payload(Notification(s.SubscriptionId, s.ClientState, id)));
        }

        await fixture.SyncAsync(s.Box);
        await fixture.SyncAsync(s.Box);
        Assert.Equal(1, await fixture.CountAsync("SELECT count(*) FROM mail.messages WHERE mailbox_id = @m AND subject = @s", TicketKit.P("m", s.Box.Id), TicketKit.P("s", subject)));
        Assert.Equal(1, await fixture.CountAsync(
            "SELECT count(*) FROM ticketing.tickets t JOIN mail.messages m ON m.ticket_id = t.id WHERE m.mailbox_id = @m AND m.subject = @s", TicketKit.P("m", s.Box.Id), TicketKit.P("s", subject)));
    }

    [Fact]
    public async Task Reauthorization_required_renews_the_subscription_and_subscription_removed_recreates_it()
    {
        var s = await NewSubscribedMailboxAsync();
        await fixture.ExecuteAsync("UPDATE mail.mailbox_sync SET subscription_expires_at = now() + interval '1 hour' WHERE mailbox_id = @m", TicketKit.P("m", s.Box.Id));
        var before = await fixture.ScalarAsync<DateTime>("SELECT subscription_expires_at FROM mail.mailbox_sync WHERE mailbox_id = @m", TicketKit.P("m", s.Box.Id));

        var lifecycle = new { value = new[] { new { subscriptionId = s.SubscriptionId, clientState = s.ClientState, lifecycleEvent = "reauthorizationRequired" } } };
        Assert.Equal(HttpStatusCode.Accepted, (await Client().PostAsync("/api/mail/graph/lifecycle", Payload(lifecycle))).StatusCode);

        var after = await fixture.ScalarAsync<DateTime>("SELECT subscription_expires_at FROM mail.mailbox_sync WHERE mailbox_id = @m", TicketKit.P("m", s.Box.Id));
        Assert.True(after > before.AddDays(1));
        Assert.Equal(s.SubscriptionId, await fixture.ScalarAsync<string>("SELECT subscription_id FROM mail.mailbox_sync WHERE mailbox_id = @m", TicketKit.P("m", s.Box.Id)));

        var removed = new { value = new[] { new { subscriptionId = s.SubscriptionId, clientState = s.ClientState, lifecycleEvent = "subscriptionRemoved" } } };
        await Client().PostAsync("/api/mail/graph/lifecycle", Payload(removed));
        var recreated = await fixture.ScalarAsync<string>("SELECT subscription_id FROM mail.mailbox_sync WHERE mailbox_id = @m", TicketKit.P("m", s.Box.Id));
        Assert.NotEqual(s.SubscriptionId, recreated);
        Assert.Equal("Active", await fixture.ScalarAsync<string>("SELECT subscription_state FROM mail.mailbox_sync WHERE mailbox_id = @m", TicketKit.P("m", s.Box.Id)));
    }

    [Fact]
    public async Task Subscription_maintenance_renews_those_close_to_expiry()
    {
        var s = await NewSubscribedMailboxAsync();
        await fixture.ExecuteAsync("UPDATE mail.mailbox_sync SET subscription_expires_at = now() + interval '2 hours' WHERE mailbox_id = @m", TicketKit.P("m", s.Box.Id));

        await fixture.WithScopeAsync(sp => sp.GetRequiredService<Modules.Mail.Contracts.IMailJobRunner>().RunSubscriptionMaintenanceAsync());

        var expires = await fixture.ScalarAsync<DateTime>("SELECT subscription_expires_at FROM mail.mailbox_sync WHERE mailbox_id = @m", TicketKit.P("m", s.Box.Id));
        Assert.True(expires > DateTime.UtcNow.AddDays(2));
    }

    [Fact]
    public async Task Malformed_oversized_and_non_object_bodies_are_refused()
    {
        var client = Client();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/mail/graph/notifications", new StringContent("{not json", Encoding.UTF8, "application/json"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/mail/graph/notifications", Payload(new List<int> { 1, 2, 3 }))).StatusCode);
        var huge = new StringContent(new string('x', 300 * 1024), Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await client.PostAsync("/api/mail/graph/notifications", huge)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/mail/graph/notifications?validationToken=" + new string('a', 3000), null)).StatusCode);
    }

    [Fact]
    public async Task The_webhooks_leak_nothing_in_their_responses()
    {
        var s = await NewSubscribedMailboxAsync();
        var response = await Client().PostAsync("/api/mail/graph/notifications", Payload(Notification(s.SubscriptionId, "wrong")));

        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync());
        Assert.False(response.Headers.Contains("Location"));
    }
}
