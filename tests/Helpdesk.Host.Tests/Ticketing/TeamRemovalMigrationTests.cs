using System.Net;
using Helpdesk.Host.Tests.Infrastructure;
using Helpdesk.Modules.Ticketing.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Helpdesk.Host.Tests.Ticketing;

/// <summary>
/// The data migration that removes Teams (a Department is the team): it refuses to run while configuration still names a team, and otherwise rewrites the
/// "My Team" queue to "All" without touching tickets, history or any table. The migration is a no-op to undo, so rolling back to the previous one and forward again is safe.
/// </summary>
public sealed class TeamRemovalMigrationTests(HelpdeskFixture fixture) : IClassFixture<HelpdeskFixture>
{
    private const string Previous = "20261008180000_UnassignedNotification";
    private static readonly string[] Columns = ["status", "assignee"];

    private Task MigrateAsync(string? target) => fixture.WithScopeAsync(async sp =>
    {
        var db = sp.GetRequiredService<TicketingDbContext>();
        await db.GetService<IMigrator>().MigrateAsync(target);
    });

    [Fact]
    public async Task It_refuses_while_configuration_names_a_team_and_otherwise_rewrites_the_team_queue_to_all_keeping_the_data()
    {
        var d = await fixture.NewDeptAsync();
        var ticket = await d.Agent.CreateTicketAsync(d.Department);
        var rule = Guid.CreateVersion7();
        await MigrateAsync(Previous);
        try
        {
            await fixture.ExecuteAsync(
                "INSERT INTO ticketing.automation_rules (id, department_id, name, is_active, trigger, sort_order, stop_processing, conditions, actions, created_at, updated_at) "
                + "VALUES (@id, @d, 'old team rule', true, 'TicketCreated', 10, false, '[]'::jsonb, CAST(@a AS jsonb), now(), now())",
                new NpgsqlParameter("id", rule), new NpgsqlParameter("d", d.Id), new NpgsqlParameter("a", $"[{{\"type\":\"assignTeam\",\"value\":\"{Guid.NewGuid()}\",\"text\":null}}]"));

            var refusal = await Assert.ThrowsAnyAsync<Exception>(() => MigrateAsync(null));
            Assert.Contains(rule.ToString(), refusal.ToString(), StringComparison.Ordinal);
            Assert.Contains("Teams are being removed", refusal.ToString(), StringComparison.Ordinal);

            await fixture.ExecuteAsync("DELETE FROM ticketing.automation_rules WHERE id = @id", new NpgsqlParameter("id", rule));
            var view = (await d.Agent.Client.PostAsync("/api/views", new { name = $"Mine {Unique.Id()}", filter = new { queue = "all" } })).Expect(HttpStatusCode.Created);
            await fixture.ExecuteAsync("UPDATE ticketing.saved_views SET filter = jsonb_set(filter, '{queue}', '\"team\"') WHERE id = @v", new NpgsqlParameter("v", view.Id));
            (await d.Agent.Client.PutAsync("/api/me/queue-preferences", new { columns = Columns, defaultQueue = "unassigned" })).Expect(HttpStatusCode.OK);
            await fixture.ExecuteAsync("UPDATE ticketing.user_queue_preferences SET default_queue = 'team', columns = '[\"status\",\"team\",\"assignee\"]'::jsonb WHERE user_id = @u", new NpgsqlParameter("u", d.Agent.Id));
        }
        finally
        {
            await fixture.ExecuteAsync("DELETE FROM ticketing.automation_rules WHERE id = @id", new NpgsqlParameter("id", rule));
        }

        await MigrateAsync(null);

        Assert.Equal("all", await fixture.ScalarAsync<string>("SELECT filter ->> 'queue' FROM ticketing.saved_views WHERE owner_user_id = @u AND filter ->> 'queue' IN ('all','team') ORDER BY created_at DESC LIMIT 1", new NpgsqlParameter("u", d.Agent.Id)));
        Assert.Equal("all", await fixture.ScalarAsync<string>("SELECT default_queue FROM ticketing.user_queue_preferences WHERE user_id = @u", new NpgsqlParameter("u", d.Agent.Id)));
        Assert.Equal("[\"status\", \"assignee\"]", await fixture.ScalarAsync<string>("SELECT columns::text FROM ticketing.user_queue_preferences WHERE user_id = @u", new NpgsqlParameter("u", d.Agent.Id)));
        Assert.Equal(1, await fixture.ScalarAsync<long>("SELECT count(*) FROM ticketing.tickets WHERE id = @t", TicketKit.P("t", ticket.Id))); // nothing was deleted
    }
}
