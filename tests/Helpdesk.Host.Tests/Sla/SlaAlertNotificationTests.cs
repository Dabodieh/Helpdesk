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
        await w.PolicyAsync("Alerts", await w.CalendarAsync(), firstResponse: null, resolution: 120, lead: 24); // 80% of 120 minutes consumed = 96 minutes in
        var owned = await w.NewTicketAsync(subject: "owned");
        var orphan = await w.NewTicketAsync(subject: "orphan");
        var current = (await w.Lead.GetTicketAsync(owned.Id)).Expect(HttpStatusCode.OK);
        (await w.Lead.AssignAsync(owned.Id, current.Version, null, w.Agent.Id)).Expect(HttpStatusCode.OK);
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
}
