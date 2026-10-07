#pragma warning disable CA1861 // one-off request payloads
using System.Net;
using System.Text.Json;
using Helpdesk.Host.Tests.Infrastructure;
using Helpdesk.Host.Tests.Mail;
using Helpdesk.Host.Tests.Portal;
using Helpdesk.Modules.Ticketing.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Helpdesk.Host.Tests.Ticketing;

/// <summary>@mentions in internal notes: eligible colleagues only, saved with the note, one durable notification each, never access, never to requesters.</summary>
public sealed class MentionTests(HelpdeskFixture fixture) : IClassFixture<HelpdeskFixture>
{
    private async Task<(Dept D, PortalKit.Employee Alice, ApiResponse Ticket)> StartAsync()
    {
        var d = await fixture.NewDeptAsync();
        var alice = await fixture.EmployeeAsync("alice");
        var ticket = (await d.TicketForAsync(alice.Email, $"Mention {Unique.Id()}")).Expect(HttpStatusCode.Created);
        return (d, alice, ticket);
    }

    private static Task<ApiResponse> NoteAsync(TestUser user, Guid ticketId, string body, params Guid[] mentions) =>
        user.Client.PostAsync($"/api/tickets/{ticketId}/messages", new { kind = "Internal", body, mentionUserIds = mentions });

    private static async Task<JsonElement> NotificationsAsync(TestUser user) => (await user.Client.GetAsync("/api/notifications")).Expect(HttpStatusCode.OK).Json;

    private Task<string> NameOfAsync(TestUser user) => fixture.ScalarAsync<string>("SELECT display_name FROM identity.users WHERE id = @u", TicketKit.P("u", user.Id))!;

    [Fact]
    public async Task An_eligible_colleague_is_mentioned_saved_with_the_note_notified_once_and_nothing_else_changes()
    {
        var s = await StartAsync();
        var note = (await NoteAsync(s.D.Agent, s.Ticket.Id, "Please check this, @colleague", s.D.Lead.Id)).Expect(HttpStatusCode.Created);
        var noteId = note.Json.GetProperty("message").GetProperty("id").GetGuid();

        // Persisted with the note, and returned (name included) wherever the note is.
        Assert.Equal(1, await fixture.CountAsync("SELECT count(*) FROM ticketing.message_mentions WHERE message_id = @m AND user_id = @u", TicketKit.P("m", noteId), TicketKit.P("u", s.D.Lead.Id)));
        var listed = (await s.D.Agent.MessagesAsync(s.Ticket.Id)).Single(m => m.Str("kind") == "Internal");
        Assert.Equal(await NameOfAsync(s.D.Lead), listed.GetProperty("mentions")[0].Str("displayName"));

        // One durable notification, pointing at that note, with no note text in it.
        var list = await NotificationsAsync(s.D.Lead);
        Assert.Equal(1, list.GetProperty("unreadCount").GetInt32());
        var item = list.GetProperty("items")[0];
        Assert.Equal("mentioned", item.Str("type"));
        Assert.Equal(noteId, item.GetProperty("messageId").GetGuid());
        Assert.DoesNotContain("Please check this", list.GetRawText(), StringComparison.Ordinal);

        // A mention neither follows nor reassigns.
        Assert.Equal(0, await fixture.CountAsync("SELECT count(*) FROM ticketing.ticket_followers WHERE ticket_id = @t", TicketKit.P("t", s.Ticket.Id)));
        Assert.Equal(JsonValueKind.Null, (await s.D.Agent.GetTicketAsync(s.Ticket.Id)).Expect(HttpStatusCode.OK).Json.GetProperty("assignee").ValueKind);
    }

    [Fact]
    public async Task Ineligible_users_and_public_replies_cannot_carry_mentions_and_the_picker_offers_only_eligible_colleagues()
    {
        var s = await StartAsync();
        var other = await fixture.NewDeptAsync();
        var outsider = await fixture.NewUserAsync("outsider");
        var before = (await s.D.Agent.MessagesAsync(s.Ticket.Id)).Count;

        (await NoteAsync(s.D.Agent, s.Ticket.Id, "foreign", other.Agent.Id)).Expect(HttpStatusCode.BadRequest); // a member of another department
        (await NoteAsync(s.D.Agent, s.Ticket.Id, "nobody", outsider.Id)).Expect(HttpStatusCode.BadRequest); // no membership at all
        (await NoteAsync(s.D.Agent, s.Ticket.Id, "unknown", Guid.NewGuid())).Expect(HttpStatusCode.BadRequest);
        (await s.D.Agent.Client.PostAsync($"/api/tickets/{s.Ticket.Id}/messages", new { kind = "Public", body = "hi", mentionUserIds = new[] { s.D.Lead.Id } })).Expect(HttpStatusCode.BadRequest);
        Assert.Equal(before, (await s.D.Agent.MessagesAsync(s.Ticket.Id)).Count); // nothing was written
        Assert.Equal(0, await fixture.CountAsync("SELECT count(*) FROM ticketing.message_mentions WHERE ticket_id = @t", TicketKit.P("t", s.Ticket.Id)));

        // The picker: this department's eligible colleagues by name, never oneself, never another department, and only for people who may write notes.
        var lead = await NameOfAsync(s.D.Lead);
        var found = (await s.D.Agent.Client.GetAsync($"/api/tickets/{s.Ticket.Id}/mentionable?q={Uri.EscapeDataString(lead[..4])}")).Expect(HttpStatusCode.OK).Json;
        Assert.Contains(found.EnumerateArray(), p => p.GetProperty("userId").GetGuid() == s.D.Lead.Id);
        Assert.DoesNotContain(found.EnumerateArray(), p => p.GetProperty("userId").GetGuid() == s.D.Agent.Id);
        var foreign = await NameOfAsync(other.Agent);
        Assert.DoesNotContain(foreign, (await s.D.Agent.Client.GetAsync($"/api/tickets/{s.Ticket.Id}/mentionable?q={Uri.EscapeDataString(foreign[..4])}")).Expect(HttpStatusCode.OK).Body, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.D.Viewer.Client.GetAsync($"/api/tickets/{s.Ticket.Id}/mentionable?q=ab")).Status);
        (await other.Agent.Client.GetAsync($"/api/tickets/{s.Ticket.Id}/mentionable?q=ab")).Expect(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Duplicates_collapse_self_mentions_stay_silent_replays_do_not_repeat_and_revoked_access_hides_everything()
    {
        var s = await StartAsync();
        (await NoteAsync(s.D.Agent, s.Ticket.Id, "dup and self", s.D.Lead.Id, s.D.Lead.Id, s.D.Agent.Id)).Expect(HttpStatusCode.Created);
        (await NoteAsync(s.D.Agent, s.Ticket.Id, "second note", s.D.Lead.Id)).Expect(HttpStatusCode.Created); // collapses into the same unread row

        Assert.Equal(1, (await NotificationsAsync(s.D.Lead)).GetProperty("unreadCount").GetInt32());
        Assert.Equal(0, (await NotificationsAsync(s.D.Agent)).GetProperty("unreadCount").GetInt32()); // the author is never told about their own mention

        // Replaying the durable intents (as a retry after a crash would) never adds a copy.
        await fixture.ExecuteAsync("UPDATE ticketing.notification_outbox SET processed_at = NULL WHERE ticket_id = @t AND kind = 'mention'", TicketKit.P("t", s.Ticket.Id));
        await fixture.WithScopeAsync(sp => sp.GetRequiredService<INotificationOutbox>().DrainAsync(CancellationToken.None));
        await fixture.WithScopeAsync(sp => sp.GetRequiredService<INotificationOutbox>().DrainAsync(CancellationToken.None));
        Assert.Equal(1, await fixture.CountAsync("SELECT count(*) FROM ticketing.notifications WHERE user_id = @u AND ticket_id = @t", TicketKit.P("u", s.D.Lead.Id), TicketKit.P("t", s.Ticket.Id)));

        // Requesters never see a note or a mention.
        var portal = (await s.Alice.User.Client.GetAsync($"/api/portal/requests/{s.Ticket.Reference()}")).Expect(HttpStatusCode.OK).Body;
        Assert.DoesNotContain("dup and self", portal, StringComparison.Ordinal);
        Assert.DoesNotContain("\"mentions\"", portal, StringComparison.Ordinal);

        // Access withdrawn: the notification and its count reveal nothing, and the note itself is no longer readable.
        var admin = await fixture.PlatformAdminAsync();
        (await admin.Client.DeleteAsync($"{s.D.Department.Url}/members/{s.D.Lead.Id}")).Expect(HttpStatusCode.NoContent);
        var revoked = await NotificationsAsync(s.D.Lead);
        Assert.Equal(0, revoked.GetProperty("unreadCount").GetInt32());
        Assert.Empty(revoked.GetProperty("items").EnumerateArray());
        (await s.D.Lead.Client.GetAsync($"/api/tickets/{s.Ticket.Id}/messages")).ExpectNotFoundLike();
    }
}

internal static class MentionAssertions
{
    public static void ExpectNotFoundLike(this ApiResponse response) => Assert.True(response.Status is HttpStatusCode.NotFound or HttpStatusCode.Forbidden, $"Expected 404/403 but got {(int)response.Status}");
}
