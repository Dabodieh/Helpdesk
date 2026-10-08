#pragma warning disable CA1861 // test payloads are one-off literals
using System.Net;
using Helpdesk.Host.Tests.Infrastructure;
using Helpdesk.Host.Tests.Ticketing;
using Helpdesk.Modules.Ticketing.Contracts;
using Helpdesk.Modules.Ticketing.Services;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Helpdesk.Host.Tests.Sla;

/// <summary>Durable after-commit work (WS-9, D16): automation intents, the notification outbox attempt cap and classification by the event's own message.</summary>
public sealed class DurableAfterCommitTests(HelpdeskFixture fixture) : IClassFixture<HelpdeskFixture>
{
    private const string Start = "2026-01-12T10:00:00Z";

    private static object Action(string type, string? value = null, string? text = null) => new { type, value, text };

    private static async Task<int> DrainAutomationAsync(TimedWorld w)
    {
        await using var scope = w.Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IAutomationOutbox>().DrainAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Automation_staged_with_a_change_still_runs_when_the_process_died_before_dispatching_it()
    {
        await using var w = await TimedWorld.CreateAsync(fixture, Start);
        (await w.Admin.Client.PostAsync($"{w.Base}/automations", new { name = "Escalate", trigger = "TicketCreated", isActive = true, stopProcessing = false, conditions = Array.Empty<object>(), actions = new[] { Action("setPriority", "High") } }))
            .Expect(HttpStatusCode.Created);

        var ticket = await w.NewTicketAsync("Printer jam", priority: "Low");
        Assert.Equal("High", (await w.TicketAsync(ticket.Id)).Json.GetProperty("priority").GetString()); // the fast path ran it
        Assert.Equal(1, await w.CountAsync("SELECT count(*) FROM ticketing.automation_outbox WHERE ticket_id = @t AND processed_at IS NOT NULL", new NpgsqlParameter("t", ticket.Id)));

        // "Restart between commit and dispatch": nothing was dispatched and nothing is recorded, but the intent committed with the ticket.
        await fixture.ExecuteAsync("UPDATE ticketing.tickets SET priority = 1 WHERE id = @t", new NpgsqlParameter("t", ticket.Id));
        await fixture.ExecuteAsync("DELETE FROM ticketing.automation_executions WHERE ticket_id = @t", new NpgsqlParameter("t", ticket.Id));
        await fixture.ExecuteAsync(
            "UPDATE ticketing.automation_outbox SET processed_at = NULL, attempts = 0, created_at = '2026-01-12 09:00:00+00' WHERE ticket_id = @t", new NpgsqlParameter("t", ticket.Id));

        Assert.True(await DrainAutomationAsync(w) >= 1);
        Assert.Equal("High", (await w.TicketAsync(ticket.Id)).Json.GetProperty("priority").GetString());
        Assert.Equal(0, await DrainAutomationAsync(w)); // processed: a second drain has nothing to do, and the deterministic key prevents a repeat anyway
    }

    [Fact]
    public async Task An_sla_breach_row_and_its_follow_up_automation_commit_together_and_the_follow_up_survives_a_lost_dispatch()
    {
        await using var w = await TimedWorld.CreateAsync(fixture, Start);
        var calendar = await w.CalendarAsync();
        await w.PolicyAsync("Fast", calendar, 60, null);
        (await w.Admin.Client.PostAsync($"{w.Base}/automations", new { name = "On breach", trigger = "SlaBreached", isActive = true, stopProcessing = false, conditions = Array.Empty<object>(), actions = new[] { Action("setPriority", "Urgent") } }))
            .Expect(HttpStatusCode.Created);
        var ticket = await w.NewTicketAsync("Slow one", priority: "Low");

        await w.GoToAsync("2026-01-12T12:30:00Z");
        await w.RunMonitorAsync();
        Assert.Equal("Urgent", (await w.TicketAsync(ticket.Id)).Json.GetProperty("priority").GetString());
        Assert.True(await w.CountAsync("SELECT count(*) FROM ticketing.automation_outbox WHERE ticket_id = @t AND trigger = 'SlaBreached'", new NpgsqlParameter("t", ticket.Id)) >= 1);

        // The breach is recorded and will not be offered again, so only the staged intent can restore the lost follow-up.
        await fixture.ExecuteAsync("UPDATE ticketing.tickets SET priority = 1 WHERE id = @t", new NpgsqlParameter("t", ticket.Id));
        await fixture.ExecuteAsync("DELETE FROM ticketing.automation_executions WHERE ticket_id = @t", new NpgsqlParameter("t", ticket.Id));
        await fixture.ExecuteAsync("UPDATE ticketing.automation_outbox SET processed_at = NULL, created_at = '2026-01-12 09:00:00+00' WHERE ticket_id = @t AND trigger = 'SlaBreached'", new NpgsqlParameter("t", ticket.Id));
        await DrainAutomationAsync(w);
        Assert.Equal("Urgent", (await w.TicketAsync(ticket.Id)).Json.GetProperty("priority").GetString());
    }

    [Fact]
    public async Task One_persistently_failing_notification_item_is_counted_then_parked_and_does_not_block_newer_items()
    {
        await using var w = await TimedWorld.CreateAsync(fixture, Start);
        await fixture.ExecuteAsync(
            """
            CREATE OR REPLACE FUNCTION ticketing.fail_notify_state() RETURNS trigger AS $$ BEGIN RAISE EXCEPTION 'poison'; END $$ LANGUAGE plpgsql;
            CREATE TRIGGER fail_notify_state BEFORE INSERT ON ticketing.ticket_notify_state FOR EACH ROW EXECUTE FUNCTION ticketing.fail_notify_state();
            """);
        try
        {
            // An assigned ticket stages an Assignment item whose processing hits the failing insert.
            var poisoned = await w.NewTicketAsync("Poison", extra: new { AssigneeUserId = w.Agent.Id });
            var other = await w.NewTicketAsync("Healthy");
            await using var scope = w.Factory.Services.CreateAsyncScope();
            var outbox = scope.ServiceProvider.GetRequiredService<INotificationOutbox>();
            for (var i = 0; i < 6; i++)
            {
                await outbox.DrainAsync(CancellationToken.None);
            }

            var attempts = await w.CountAsync("SELECT attempts::bigint FROM ticketing.notification_outbox WHERE ticket_id = @t AND kind = 'assignment'", new NpgsqlParameter("t", poisoned.Id));
            Assert.Equal((long)NotificationOutboxDrainer.MaxAttempts, attempts);
            Assert.Equal(1, await w.CountAsync("SELECT count(*) FROM ticketing.notification_outbox WHERE ticket_id = @t AND processed_at IS NULL AND last_error IS NOT NULL", new NpgsqlParameter("t", poisoned.Id)));

            // Parked: not retried any more. A newer, healthy item (an agent reply on another ticket) is processed regardless.
            await outbox.DrainAsync(CancellationToken.None);
            Assert.Equal(attempts, await w.CountAsync("SELECT attempts::bigint FROM ticketing.notification_outbox WHERE ticket_id = @t AND kind = 'assignment'", new NpgsqlParameter("t", poisoned.Id)));
            (await w.Agent.PostMessageAsync(other.Id, "Public", "Newer item")).Expect(HttpStatusCode.Created);
            await outbox.DrainAsync(CancellationToken.None);
            Assert.Equal(0, await w.CountAsync("SELECT count(*) FROM ticketing.notification_outbox WHERE ticket_id = @t AND processed_at IS NULL", new NpgsqlParameter("t", other.Id)));
        }
        finally
        {
            await fixture.ExecuteAsync("DROP TRIGGER IF EXISTS fail_notify_state ON ticketing.ticket_notify_state; DROP FUNCTION IF EXISTS ticketing.fail_notify_state();");
        }
    }

    [Fact]
    public async Task A_late_drain_classifies_the_event_by_its_own_message_not_by_the_latest_one()
    {
        await using var w = await TimedWorld.CreateAsync(fixture, Start);
        var ticket = await w.NewTicketAsync("Late drain", extra: new { AssigneeUserId = w.Agent.Id });
        var requesterId = Guid.Parse((await fixture.ScalarAsync<string>("SELECT requester_id::text FROM ticketing.tickets WHERE id = @t", new NpgsqlParameter("t", ticket.Id)))!);
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ITicketIngress>().AddRequesterMessageAsync(w.Dept.Id, ticket.Id, requesterId, "Please help", "manual");
        }

        (await w.Lead.PostMessageAsync(ticket.Id, "Public", "We are on it")).Expect(HttpStatusCode.Created); // the LATEST public message is now an agent reply
        var requesterMessage = await fixture.ScalarAsync<Guid>("SELECT id FROM ticketing.messages WHERE ticket_id = @t AND author_type = 'Requester' AND kind = 'Public' ORDER BY created_at LIMIT 1", new NpgsqlParameter("t", ticket.Id));
        Assert.Equal(1, await w.CountAsync("SELECT count(*) FROM ticketing.notification_outbox WHERE message_id = @m AND kind = 'message'", new NpgsqlParameter("m", requesterMessage)));

        // The drain for the requester's message arrives late: it must still be a requester reply for the assignee.
        await fixture.ExecuteAsync("DELETE FROM ticketing.notifications WHERE ticket_id = @t", new NpgsqlParameter("t", ticket.Id));
        await fixture.ExecuteAsync("UPDATE ticketing.notification_outbox SET processed_at = NULL, attempts = 0 WHERE message_id = @m AND kind = 'message'", new NpgsqlParameter("m", requesterMessage));
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<INotificationOutbox>().DrainAsync(CancellationToken.None);
        }

        Assert.Equal("requester_reply", await fixture.ScalarAsync<string>("SELECT type FROM ticketing.notifications WHERE ticket_id = @t AND user_id = @u", new NpgsqlParameter("t", ticket.Id), new NpgsqlParameter("u", w.Agent.Id)));
    }
}
