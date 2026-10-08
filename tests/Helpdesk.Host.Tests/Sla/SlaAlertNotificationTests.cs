using System.Net;
using System.Text.Json;
using Helpdesk.Host.Tests.Infrastructure;
using Helpdesk.Host.Tests.Ticketing;
using Helpdesk.Host.Tests.Portal;
using Helpdesk.Modules.Ticketing.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Helpdesk.Host.Tests.Sla;

public sealed class SlaAlertNotificationTests(HelpdeskFixture fixture) : IClassFixture<HelpdeskFixture>
{
    private static async Task<JsonElement> ListAsync(TestUser user) => (await user.Client.GetAsync("/api/notifications")).Expect(HttpStatusCode.OK).Json;

    private static string[] Unread(JsonElement list) =>
        [.. list.GetProperty("items").EnumerateArray().Where(i => !i.GetProperty("read").GetBoolean()).Select(i => $"{i.GetProperty("type").GetString()}:{i.GetProperty("reference").GetString()}")];

    private static async Task ReadAllAsync(TestUser user) => (await user.Client.PostAsync("/api/notifications/read-all")).Expect(HttpStatusCode.NoContent);

    private static async Task DrainAsync(TimedWorld w)
    {
        using var scope = w.Factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<INotificationOutbox>().DrainAsync(CancellationToken.None);
    }

    [Fact]
    public async Task The_assigned_agent_is_told_once_when_the_target_is_approaching_and_once_on_breach_and_unassigned_tickets_notify_nobody()
    {
        await using var w = await TimedWorld.CreateAsync(fixture, "2026-01-12T09:00:00Z");
        await w.PolicyAsync("Alerts", await w.CalendarAsync(), firstResponse: null, resolution: 120); // default 60 minute lead (automation) must not move the 80% alert: 96 minutes in, 10:36
        var owned = await w.NewTicketAsync(subject: "owned");
        var orphan = await w.NewTicketAsync(subject: "orphan");
        var current = (await w.Lead.GetTicketAsync(owned.Id)).Expect(HttpStatusCode.OK);
        (await w.Lead.AssignAsync(owned.Id, current.Version, w.Agent.Id)).Expect(HttpStatusCode.OK);
        await DrainAsync(w);
        await ReadAllAsync(w.Agent);

        await w.GoToAsync("2026-01-12T10:35:00Z"); // 95 minutes: not yet 80%
        await w.RunMonitorAsync();
        await DrainAsync(w);
        Assert.Empty(Unread(await ListAsync(w.Agent)));

        await w.GoToAsync("2026-01-12T10:37:00Z");
        await w.RunMonitorAsync();
        await DrainAsync(w);
        Assert.Equal([$"sla_approaching:{owned.Reference()}"], Unread(await ListAsync(w.Agent)));
        Assert.Empty(Unread(await ListAsync(w.Lead))); // the unassigned ticket's warning reaches nobody in-app
        Assert.DoesNotContain(orphan.Reference(), string.Join(',', Unread(await ListAsync(w.Agent))));

        await ReadAllAsync(w.Agent);
        await w.RunMonitorAsync(); // a repeated run adds nothing
        await DrainAsync(w);
        Assert.Empty(Unread(await ListAsync(w.Agent)));

        await w.GoToAsync("2026-01-12T11:05:00Z");
        await w.RunMonitorAsync();
        await DrainAsync(w);
        await DrainAsync(w);
        Assert.Equal([$"sla_breached:{owned.Reference()}"], Unread(await ListAsync(w.Agent)));
    }

    [Fact]
    public async Task A_breach_escalates_the_unread_approaching_alert_instead_of_being_suppressed_by_it()
    {
        await using var w = await TimedWorld.CreateAsync(fixture, "2026-01-12T09:00:00Z");
        await w.PolicyAsync("Escalate", await w.CalendarAsync(), firstResponse: null, resolution: 120);
        var t = await w.NewTicketAsync(subject: "escalate");
        var current = (await w.Lead.GetTicketAsync(t.Id)).Expect(HttpStatusCode.OK);
        (await w.Lead.AssignAsync(t.Id, current.Version, w.Agent.Id)).Expect(HttpStatusCode.OK);
        await DrainAsync(w);
        await ReadAllAsync(w.Agent);

        await w.GoToAsync("2026-01-12T10:40:00Z");
        await w.RunMonitorAsync();
        await DrainAsync(w);
        Assert.Equal([$"sla_approaching:{t.Reference()}"], Unread(await ListAsync(w.Agent)));

        await w.GoToAsync("2026-01-12T11:01:00Z"); // approaching is still unread
        await w.RunMonitorAsync();
        await DrainAsync(w);
        Assert.Equal([$"sla_breached:{t.Reference()}"], Unread(await ListAsync(w.Agent)));
    }

    [Fact]
    public async Task A_pause_moves_the_80_percent_alert_with_the_deadline()
    {
        await using var w = await TimedWorld.CreateAsync(fixture, "2026-01-12T09:00:00Z");
        await w.PolicyAsync("Pausing", await w.CalendarAsync(), firstResponse: null, resolution: 120, pause: true);
        var t = await w.NewTicketAsync(subject: "paused");
        var current = (await w.Lead.GetTicketAsync(t.Id)).Expect(HttpStatusCode.OK);
        (await w.Lead.AssignAsync(t.Id, current.Version, w.Agent.Id)).Expect(HttpStatusCode.OK);
        await DrainAsync(w);
        await ReadAllAsync(w.Agent);

        await w.GoToAsync("2026-01-12T09:30:00Z");
        await w.SetStatusAsync(t.Id, "Pending"); // 90 minutes remain
        await w.GoToAsync("2026-01-12T12:00:00Z");
        await w.RunMonitorAsync();
        await DrainAsync(w);
        Assert.Empty(Unread(await ListAsync(w.Agent))); // paused time never alerts
        await w.SetStatusAsync(t.Id, "Open"); // due 13:30, 80% at 13:06

        await w.GoToAsync("2026-01-12T13:05:00Z");
        await w.RunMonitorAsync();
        await DrainAsync(w);
        Assert.Empty(Unread(await ListAsync(w.Agent)));
        await w.GoToAsync("2026-01-12T13:07:00Z");
        await w.RunMonitorAsync();
        await DrainAsync(w);
        Assert.Equal([$"sla_approaching:{t.Reference()}"], Unread(await ListAsync(w.Agent)));
    }
}
