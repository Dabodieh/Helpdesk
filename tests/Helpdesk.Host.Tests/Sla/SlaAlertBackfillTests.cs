using System.Net;
using System.Text.Json;
using Helpdesk.Host.Tests.Infrastructure;
using Helpdesk.Host.Tests.Portal;
using Helpdesk.Host.Tests.Ticketing;
using Helpdesk.Modules.Ticketing.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Helpdesk.Host.Tests.Sla;

public sealed class SlaAlertBackfillTests(HelpdeskFixture fixture) : IClassFixture<HelpdeskFixture>
{
    private static async Task<SlaAlertBackfillResult> BackfillAsync(TimedWorld w, bool apply)
    {
        using var scope = w.Factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ISlaAlertBackfill>().RunAsync(apply, CancellationToken.None);
    }

    private static async Task DrainAsync(TimedWorld w)
    {
        using var scope = w.Factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<INotificationOutbox>().DrainAsync(CancellationToken.None);
    }

    private static async Task<string[]> UnreadAsync(TestUser user) =>
        [.. (await user.Client.GetAsync("/api/notifications")).Expect(HttpStatusCode.OK).Json.GetProperty("items").EnumerateArray()
            .Where(i => !i.GetProperty("read").GetBoolean()).Select(i => $"{i.GetProperty("type").GetString()}:{i.GetProperty("reference").GetString()}")];

    [Fact]
    public async Task The_backfill_dry_run_writes_nothing_apply_schedules_future_alerts_silently_marks_past_ones_and_a_rerun_does_nothing()
    {
        await using var w = await TimedWorld.CreateAsync(fixture, "2026-01-12T09:00:00Z");
        await w.PolicyAsync("Legacy", await w.CalendarAsync(), firstResponse: null, resolution: 120);
        var past = await w.NewTicketAsync(subject: "past"); // due 11:00, 80% at 10:36
        await w.GoToAsync("2026-01-12T10:00:00Z");
        var future = await w.NewTicketAsync(subject: "future"); // due 12:00, 80% at 11:36
        var paused = await w.NewTicketAsync(subject: "paused");
        var current = (await w.Lead.GetTicketAsync(past.Id)).Expect(HttpStatusCode.OK);
        (await w.Lead.AssignAsync(past.Id, current.Version, null, w.Agent.Id)).Expect(HttpStatusCode.OK);
        current = (await w.Lead.GetTicketAsync(future.Id)).Expect(HttpStatusCode.OK);
        (await w.Lead.AssignAsync(future.Id, current.Version, null, w.Agent.Id)).Expect(HttpStatusCode.OK);
        await DrainAsync(w);
        (await w.Agent.Client.PostAsync("/api/notifications/read-all")).Expect(HttpStatusCode.NoContent);

        // Rows as they were before the alert columns existed; `paused` is excluded by hand-setting a pause (no deadline, remaining time kept).
        await fixture.ExecuteAsync("UPDATE ticketing.ticket_sla SET fr_alert_at = NULL, fr_alerted_at = NULL, res_alert_at = NULL, res_alerted_at = NULL");
        await fixture.ExecuteAsync("UPDATE ticketing.ticket_sla SET res_due_at = NULL, res_paused_at = now(), res_remaining_seconds = 3600 WHERE ticket_id = @id", new Npgsql.NpgsqlParameter("id", paused.Id));
        await w.GoToAsync("2026-01-12T10:40:00Z");

        var dry = await BackfillAsync(w, apply: false);
        Assert.Equal((false, 2, 1, 1), (dry.Applied, dry.Tickets, dry.ClocksScheduled, dry.ClocksAlreadyPast));
        Assert.Equal(0L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ticketing.ticket_sla WHERE res_alert_at IS NOT NULL OR res_alerted_at IS NOT NULL"));

        var dueBefore = await fixture.ScalarAsync<long>("SELECT sum(extract(epoch FROM res_due_at))::bigint FROM ticketing.ticket_sla");
        var events = await fixture.ScalarAsync<long>("SELECT count(*) FROM ticketing.sla_events");
        var applied = await BackfillAsync(w, apply: true);
        Assert.Equal((true, 2, 1, 1), (applied.Applied, applied.Tickets, applied.ClocksScheduled, applied.ClocksAlreadyPast));
        Assert.Equal(1L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ticketing.ticket_sla WHERE res_alert_at IS NOT NULL AND res_alerted_at IS NULL"));
        Assert.Equal(1L, await fixture.ScalarAsync<long>("SELECT count(*) FROM ticketing.ticket_sla WHERE res_alerted_at IS NOT NULL AND res_alert_at IS NULL"));
        Assert.Equal(dueBefore, await fixture.ScalarAsync<long>("SELECT sum(extract(epoch FROM res_due_at))::bigint FROM ticketing.ticket_sla"));
        Assert.Equal(events, await fixture.ScalarAsync<long>("SELECT count(*) FROM ticketing.sla_events"));

        var again = await BackfillAsync(w, apply: true);
        Assert.Equal((0, 0, 0), (again.Tickets, again.ClocksScheduled, again.ClocksAlreadyPast));

        // No retrospective alert; the scheduled one fires when its time comes.
        await w.RunMonitorAsync();
        await DrainAsync(w);
        Assert.Empty(await UnreadAsync(w.Agent));
        await w.GoToAsync("2026-01-12T11:37:00Z");
        await w.RunMonitorAsync();
        await DrainAsync(w);
        // The breach of the older ticket (due 11:00) is still delivered as normal; only its skipped 80% alert is not.
        Assert.Equal([$"sla_approaching:{future.Reference()}", $"sla_breached:{past.Reference()}"], [.. (await UnreadAsync(w.Agent)).Order(StringComparer.Ordinal)]);
    }
}
