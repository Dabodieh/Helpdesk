#pragma warning disable CA1861 // one-off request payloads
using System.Net;
using System.Text.Json;
using Helpdesk.Host.Tests.Infrastructure;
using Helpdesk.Host.Tests.Mail;
using Helpdesk.Host.Tests.Portal;
using Helpdesk.Modules.Ticketing.Contracts;
using Helpdesk.Modules.Ticketing.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Helpdesk.Host.Tests.Ticketing;

/// <summary>Durable in-app notifications: persisted regardless of SignalR, one unread per user and ticket, never about one's own action, never content, never access.</summary>
public sealed class NotificationTests(HelpdeskFixture fixture) : IClassFixture<HelpdeskFixture>
{
    private static async Task<JsonElement> ListAsync(TestUser user) => (await user.Client.GetAsync("/api/notifications")).Expect(HttpStatusCode.OK).Json;

    private static int Unread(JsonElement list) => list.GetProperty("unreadCount").GetInt32();

    /// <summary>Types of the UNREAD notifications.</summary>
    private static string[] Types(JsonElement list) => [.. list.GetProperty("items").EnumerateArray().Where(i => !i.GetProperty("read").GetBoolean()).Select(i => i.Str("type"))];

    private async Task<(Dept D, PortalKit.Employee Alice, ApiResponse Ticket)> StartAsync()
    {
        var d = await fixture.NewDeptAsync();
        var alice = await fixture.EmployeeAsync("alice");
        var ticket = (await d.TicketForAsync(alice.Email, $"Notify {Unique.Id()}")).Expect(HttpStatusCode.Created);
        return (d, alice, ticket);
    }

    private static async Task AssignAsync(Dept d, Guid ticketId, Guid? team, Guid assignee)
    {
        var current = (await d.Lead.GetTicketAsync(ticketId)).Expect(HttpStatusCode.OK);
        (await d.Lead.AssignAsync(ticketId, current.Version, team, assignee)).Expect(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Assignment_notifies_the_new_assignee_only_once_not_the_actor_and_a_team_only_change_stays_quiet()
    {
        var s = await StartAsync();
        await AssignAsync(s.D, s.Ticket.Id, null, s.D.Agent.Id);

        var list = await ListAsync(s.D.Agent);
        Assert.Equal(1, Unread(list));
        Assert.Equal(["assigned"], Types(list));
        Assert.Equal(s.Ticket.Reference(), list.GetProperty("items")[0].Str("reference"));
        Assert.Equal(0, Unread(await ListAsync(s.D.Lead))); // the actor is never told about their own action

        // Read it, then change only the team: the assignee did not change, so there is nothing new to say.
        var id = list.GetProperty("items")[0].GetProperty("id").GetGuid();
        (await s.D.Agent.Client.PostAsync($"/api/notifications/{id}/read")).Expect(HttpStatusCode.NoContent);
        Assert.Equal(0, Unread(await ListAsync(s.D.Agent)));
        var team = await fixture.CreateTeamAsync(s.D.Department);
        await AssignAsync(s.D, s.Ticket.Id, team.Id, s.D.Agent.Id);
        Assert.Equal(0, Unread(await ListAsync(s.D.Agent)));
    }

    [Fact]
    public async Task A_requester_reply_reaches_the_assignee_and_followers_once_and_followed_updates_collapse_into_one_unread()
    {
        var s = await StartAsync();
        await AssignAsync(s.D, s.Ticket.Id, null, s.D.Agent.Id);
        (await s.D.Lead.Client.PostAsync($"/api/tickets/{s.Ticket.Id}/followers/me")).Expect(HttpStatusCode.OK);
        (await s.D.Agent.Client.PostAsync($"/api/notifications/read-all")).Expect(HttpStatusCode.NoContent);

        // One portal reply qualifies the assignee AND the follower; it also changes the status back: still one notification each.
        (await s.Alice.User.Client.PostAsync($"/api/portal/requests/{s.Ticket.Reference()}/replies", new { body = "Any news?" })).Expect(HttpStatusCode.Created);
        var agent = await ListAsync(s.D.Agent);
        var lead = await ListAsync(s.D.Lead);
        Assert.Equal(["requester_reply"], Types(agent));
        Assert.Equal(1, Unread(agent));
        Assert.Equal(["requester_reply"], Types(lead));
        Assert.Equal(1, Unread(lead));

        // The follower reads it; later an agent changes the status: a followed update, and the agent (the actor) hears nothing.
        (await s.D.Lead.Client.PostAsync("/api/notifications/read-all")).Expect(HttpStatusCode.NoContent);
        await fixture.ExecuteAsync("UPDATE ticketing.notifications SET read_at = now() WHERE user_id = @u", TicketKit.P("u", s.D.Agent.Id));
        var current = (await s.D.Agent.GetTicketAsync(s.Ticket.Id)).Expect(HttpStatusCode.OK);
        (await s.D.Agent.PatchTicketAsync(current, new { Status = "Pending" })).Expect(HttpStatusCode.OK);
        var after = await ListAsync(s.D.Lead);
        Assert.Equal(1, Unread(after));
        Assert.Equal("followed_update", Types(after)[0]);
        Assert.Equal(0, Unread(await ListAsync(s.D.Agent)));

        // More updates while it is unread do not add rows.
        var again = (await s.D.Agent.GetTicketAsync(s.Ticket.Id)).Expect(HttpStatusCode.OK);
        (await s.D.Agent.PatchTicketAsync(again, new { Priority = "High" })).Expect(HttpStatusCode.OK);
        Assert.Equal(1, Unread(await ListAsync(s.D.Lead)));
        Assert.Equal(1, await fixture.CountAsync("SELECT count(*) FROM ticketing.notifications WHERE user_id = @u AND ticket_id = @t AND read_at IS NULL", TicketKit.P("u", s.D.Lead.Id), TicketKit.P("t", s.Ticket.Id)));
    }

    [Fact]
    public async Task Internal_notes_never_notify_and_no_notification_carries_message_text()
    {
        var s = await StartAsync();
        (await s.D.Lead.Client.PostAsync($"/api/tickets/{s.Ticket.Id}/followers/me")).Expect(HttpStatusCode.OK);
        var secret = $"NOTE-SECRET-{Unique.Id()}";
        (await s.D.Agent.PostMessageAsync(s.Ticket.Id, "Internal", secret)).Expect(HttpStatusCode.Created);
        var list = await ListAsync(s.D.Lead);
        Assert.Equal(0, Unread(list));
        Assert.DoesNotContain(secret, list.GetRawText(), StringComparison.Ordinal);

        // A public reply does notify the follower; the stored row is ids and a type only.
        var publicText = $"PUBLIC-TEXT-{Unique.Id()}";
        (await s.D.Agent.PostMessageAsync(s.Ticket.Id, "Public", publicText)).Expect(HttpStatusCode.Created);
        var withReply = await ListAsync(s.D.Lead);
        Assert.Equal(1, Unread(withReply));
        Assert.DoesNotContain(publicText, withReply.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(0, await fixture.CountAsync("SELECT count(*) FROM ticketing.notifications WHERE to_jsonb(notifications)::text LIKE @p", TicketKit.P("p", $"%{publicText}%")));
    }

    [Fact]
    public async Task Revoked_access_removes_the_notification_and_the_count_and_other_departments_and_platform_admins_see_nothing()
    {
        var s = await StartAsync();
        var other = await fixture.NewDeptAsync();
        await AssignAsync(s.D, s.Ticket.Id, null, s.D.Agent.Id);
        Assert.Equal(1, Unread(await ListAsync(s.D.Agent))); // persisted with no realtime connection at all

        Assert.Equal(0, Unread(await ListAsync(other.Agent)));
        Assert.Empty((await ListAsync(other.Admin)).GetProperty("items").EnumerateArray());
        var platform = await fixture.PlatformAdminAsync();
        Assert.Equal(0, Unread(await ListAsync(platform)));

        // The membership is withdrawn: the row still exists, but it is neither listed nor counted, and reveals nothing.
        var admin = await fixture.PlatformAdminAsync();
        (await admin.Client.DeleteAsync($"{s.D.Department.Url}/members/{s.D.Agent.Id}")).Expect(HttpStatusCode.NoContent);
        var revoked = await ListAsync(s.D.Agent);
        Assert.Equal(0, Unread(revoked));
        Assert.Empty(revoked.GetProperty("items").EnumerateArray());
        Assert.DoesNotContain(s.Ticket.Reference(), revoked.GetRawText(), StringComparison.Ordinal);

        // A notification of someone else is simply not found.
        var foreign = await fixture.ScalarAsync<Guid>("SELECT id FROM ticketing.notifications WHERE user_id = @u LIMIT 1", TicketKit.P("u", s.D.Agent.Id));
        (await other.Agent.Client.PostAsync($"/api/notifications/{foreign}/read")).Expect(HttpStatusCode.NotFound);
    }

    private Task DrainAsync() => fixture.WithScopeAsync(sp => sp.GetRequiredService<INotificationOutbox>().DrainAsync(CancellationToken.None));

    private Task ChangeAssigneeInProcessAsync(Guid ticketId, Guid assignee, bool rollback) =>
        fixture.WithScopeAsync(async sp =>
        {
            // Straight through EF, no API and no published event: exactly what a crash right after the commit would leave behind.
            var db = sp.GetRequiredService<TicketingDbContext>();
            await using var tx = await db.Database.BeginTransactionAsync();
            var ticket = await db.Tickets.IgnoreQueryFilters().SingleAsync(t => t.Id == ticketId);
            ticket.AssigneeUserId = assignee;
            await db.SaveChangesAsync();
            if (rollback)
            {
                await tx.RollbackAsync();
            }
            else
            {
                await tx.CommitAsync();
            }
        });

    [Fact]
    public async Task The_notification_intent_commits_with_the_change_so_a_crash_before_any_event_loses_nothing()
    {
        var s = await StartAsync();
        var outboxRows = () => fixture.CountAsync("SELECT count(*) FROM ticketing.notification_outbox WHERE ticket_id = @t AND kind = 'assignment'", TicketKit.P("t", s.Ticket.Id));

        // A change that rolls back leaves no intent behind (the row lives and dies with the change).
        await ChangeAssigneeInProcessAsync(s.Ticket.Id, s.D.Agent.Id, rollback: true);
        Assert.Equal(0, await outboxRows());

        // A committed change always carries its intent, even though nothing published an event and nothing drained it yet.
        await ChangeAssigneeInProcessAsync(s.Ticket.Id, s.D.Agent.Id, rollback: false);
        Assert.Equal(1, await outboxRows());
        Assert.Equal(1, await fixture.CountAsync("SELECT count(*) FROM ticketing.notification_outbox WHERE ticket_id = @t AND processed_at IS NULL", TicketKit.P("t", s.Ticket.Id)));
        Assert.Equal(0, Unread(await ListAsync(s.D.Agent)));

        // The recurring drain turns it into the notification.
        await DrainAsync();
        Assert.Equal(["assigned"], Types(await ListAsync(s.D.Agent)));
        Assert.Equal(0, await fixture.CountAsync("SELECT count(*) FROM ticketing.notification_outbox WHERE ticket_id = @t AND processed_at IS NULL", TicketKit.P("t", s.Ticket.Id)));
    }

    [Fact]
    public async Task Replaying_or_racing_the_drain_never_duplicates_a_notification()
    {
        var s = await StartAsync();
        await ChangeAssigneeInProcessAsync(s.Ticket.Id, s.D.Agent.Id, rollback: false);

        await Task.WhenAll(DrainAsync(), DrainAsync(), DrainAsync()); // concurrent workers: one claims, the others skip
        Assert.Equal(1, await fixture.CountAsync("SELECT count(*) FROM ticketing.notifications WHERE user_id = @u AND ticket_id = @t", TicketKit.P("u", s.D.Agent.Id), TicketKit.P("t", s.Ticket.Id)));

        // Replay the same durable event (as if it had been re-delivered): the unread notification is only refreshed, never copied.
        await fixture.ExecuteAsync("UPDATE ticketing.notification_outbox SET processed_at = NULL WHERE ticket_id = @t", TicketKit.P("t", s.Ticket.Id));
        await DrainAsync();
        await DrainAsync();
        Assert.Equal(1, await fixture.CountAsync("SELECT count(*) FROM ticketing.notifications WHERE user_id = @u AND ticket_id = @t", TicketKit.P("u", s.D.Agent.Id), TicketKit.P("t", s.Ticket.Id)));
        Assert.Equal(1, Unread(await ListAsync(s.D.Agent)));
    }
}
