#pragma warning disable CA1861 // one-off request payloads
using System.Net;
using System.Text.Json;
using Helpdesk.Host.Tests.Infrastructure;
using Helpdesk.Host.Tests.Mail;
using Helpdesk.Host.Tests.Portal;
using Helpdesk.SharedKernel.Authorization;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Helpdesk.Host.Tests.Ticketing;

/// <summary>Internal ticket followers: relational and unique, never a source of access, department-safe, merge-aware.</summary>
public sealed class TicketFollowerTests(HelpdeskFixture fixture) : IClassFixture<HelpdeskFixture>
{
    private static string Url(Guid ticketId, string suffix = "") => TicketKit.Url(ticketId, "/followers" + suffix);

    private static Guid[] FollowerIds(ApiResponse r) => [.. r.Json.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("userId").GetGuid())];

    private static async Task<ApiResponse> MergeAsync(TestUser user, Guid canonicalId, Guid sourceId)
    {
        var c = (await user.GetTicketAsync(canonicalId)).Expect(HttpStatusCode.OK);
        var s = (await user.GetTicketAsync(sourceId)).Expect(HttpStatusCode.OK);
        return await user.Client.PostAsync(TicketKit.Url(canonicalId, "/merge"), new { version = c.Version, sources = new[] { new { ticketId = sourceId, version = s.Version } } });
    }

    [Fact]
    public async Task Following_yourself_needs_only_read_access_is_idempotent_and_unfollowing_twice_is_harmless()
    {
        var d = await fixture.NewDeptAsync();
        var t = (await d.TicketForAsync($"a-{Unique.Id()}@example.test", "VPN")).Expect(HttpStatusCode.Created);

        // A viewer (read-only role) can follow; nobody follows by default.
        Assert.False((await d.Viewer.Client.GetAsync(Url(t.Id))).Expect(HttpStatusCode.OK).Json.GetProperty("following").GetBoolean());
        var first = (await d.Viewer.Client.PostAsync(Url(t.Id, "/me"))).Expect(HttpStatusCode.OK);
        Assert.True(first.Json.GetProperty("following").GetBoolean());
        (await d.Viewer.Client.PostAsync(Url(t.Id, "/me"))).Expect(HttpStatusCode.OK);
        Assert.Equal([d.Viewer.Id], FollowerIds((await d.Agent.Client.GetAsync(Url(t.Id))).Expect(HttpStatusCode.OK)));
        Assert.Equal(1, await fixture.CountAsync("SELECT count(*) FROM ticketing.ticket_followers WHERE ticket_id = @t", TicketKit.P("t", t.Id)));
        Assert.Single((await fixture.AuditOfAsync(t.Id)), a => a.Action == "ticket.follower_added"); // the repeat changed nothing, so it is not audited again

        // Following does not touch what the requester or note-less callers could observe: the ticket row is not written.
        var before = (await d.Agent.GetTicketAsync(t.Id)).Expect(HttpStatusCode.OK);
        (await d.Agent.Client.PostAsync(Url(t.Id, "/me"))).Expect(HttpStatusCode.OK);
        Assert.Equal(before.Json.Str("updatedAt"), (await d.Agent.GetTicketAsync(t.Id)).Expect(HttpStatusCode.OK).Json.Str("updatedAt"));
        Assert.Equal(before.Version, (await d.Agent.GetTicketAsync(t.Id)).Expect(HttpStatusCode.OK).Version);

        (await d.Viewer.Client.DeleteAsync(Url(t.Id, "/me"))).Expect(HttpStatusCode.OK);
        var again = (await d.Viewer.Client.DeleteAsync(Url(t.Id, "/me"))).Expect(HttpStatusCode.OK);
        Assert.False(again.Json.GetProperty("following").GetBoolean());
        Assert.Equal([d.Agent.Id], FollowerIds(again));
        Assert.Single((await fixture.AuditOfAsync(t.Id)), a => a.Action == "ticket.follower_removed");
        var added = (await fixture.AuditOfAsync(t.Id)).First(a => a.Action == "ticket.follower_added");
        Assert.Equal(d.Viewer.Id, added.ActorUserId);
        Assert.Contains("\"self\": true", added.Next, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Adding_or_removing_somebody_else_needs_update_permission_and_a_valid_department_reader()
    {
        var d = await fixture.NewDeptAsync();
        var other = await fixture.NewDeptAsync();
        var t = (await d.TicketForAsync($"a-{Unique.Id()}@example.test")).Expect(HttpStatusCode.Created);

        // The viewer may follow itself but not add the lead; an agent (tickets.update) can add and remove people.
        (await d.Viewer.Client.PostAsync(Url(t.Id, $"/{d.Lead.Id}"))).Expect(HttpStatusCode.Forbidden);
        (await d.Viewer.Client.DeleteAsync(Url(t.Id, $"/{d.Lead.Id}"))).Expect(HttpStatusCode.Forbidden);
        (await d.Viewer.Client.PostAsync(Url(t.Id, $"/{d.Viewer.Id}"))).Expect(HttpStatusCode.OK); // the explicit own id is the self path

        var added = (await d.Agent.Client.PostAsync(Url(t.Id, $"/{d.Lead.Id}"))).Expect(HttpStatusCode.OK);
        Assert.Contains(d.Lead.Id, FollowerIds(added));
        Assert.False(added.Json.GetProperty("following").GetBoolean()); // the caller (agent) is not the one added
        var audit = (await fixture.AuditOfAsync(t.Id)).Single(a => a.Action == "ticket.follower_added" && a.Next!.Contains(d.Lead.Id.ToString(), StringComparison.OrdinalIgnoreCase));
        Assert.Equal(d.Agent.Id, audit.ActorUserId);
        Assert.Contains("\"self\": false", audit.Next, StringComparison.Ordinal);
        Assert.Contains(d.Lead.Id, FollowerIds((await d.Agent.Client.PostAsync(Url(t.Id, $"/{d.Lead.Id}"))).Expect(HttpStatusCode.OK))); // idempotent

        // Another department's user and arbitrary ids are refused server-side, whatever the UI offered.
        (await d.Agent.Client.PostAsync(Url(t.Id, $"/{other.Agent.Id}"))).Expect(HttpStatusCode.BadRequest);
        (await d.Agent.Client.PostAsync(Url(t.Id, $"/{Guid.NewGuid()}"))).Expect(HttpStatusCode.BadRequest);
        var stranger = await fixture.NewUserAsync("stranger");
        (await d.Agent.Client.PostAsync(Url(t.Id, $"/{stranger.Id}"))).Expect(HttpStatusCode.BadRequest);
        Assert.Equal(0, await fixture.CountAsync("SELECT count(*) FROM ticketing.ticket_followers WHERE ticket_id = @t AND user_id = ANY(@u)", TicketKit.P("t", t.Id), TicketKit.P("u", new[] { other.Agent.Id, stranger.Id })));

        // The database refuses it as well: a follower must hold a membership of the ticket's department.
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => fixture.CountAsync(
            "INSERT INTO ticketing.ticket_followers (ticket_id, department_id, user_id, added_at) VALUES (@t, @d, @u, now()) RETURNING 1",
            TicketKit.P("t", t.Id), TicketKit.P("d", d.Id), TicketKit.P("u", other.Agent.Id)));
        Assert.Contains("fk_ticket_followers_membership", ex.ToString(), StringComparison.Ordinal);

        Assert.DoesNotContain(d.Lead.Id, FollowerIds((await d.Agent.Client.DeleteAsync(Url(t.Id, $"/{d.Lead.Id}"))).Expect(HttpStatusCode.OK)));
        Assert.Single((await fixture.AuditOfAsync(t.Id)), a => a.Action == "ticket.follower_removed");
    }

    [Fact]
    public async Task Following_never_grants_access_and_a_ticket_of_an_unreadable_department_is_a_plain_404()
    {
        var d = await fixture.NewDeptAsync();
        var other = await fixture.NewDeptAsync();
        var t = (await d.TicketForAsync($"a-{Unique.Id()}@example.test", "Private subject")).Expect(HttpStatusCode.Created);

        (await other.Agent.Client.PostAsync(Url(t.Id, "/me"))).Expect(HttpStatusCode.NotFound);
        (await other.Agent.Client.GetAsync(Url(t.Id))).Expect(HttpStatusCode.NotFound);
        (await other.Agent.Client.GetAsync(TicketKit.Url(t.Id, "/follower-candidates?q=a"))).Expect(HttpStatusCode.NotFound);
        (await other.Agent.GetTicketAsync(t.Id)).Expect(HttpStatusCode.NotFound);
        Assert.Equal(0, await fixture.CountAsync("SELECT count(*) FROM ticketing.ticket_followers WHERE ticket_id = @t", TicketKit.P("t", t.Id)));

        // Losing the membership removes the follow (cascading key) and never leaves access behind.
        var member = await fixture.NewMemberAsync(d.Department, "Agent");
        (await member.Client.PostAsync(Url(t.Id, "/me"))).Expect(HttpStatusCode.OK);
        var admin = await fixture.PlatformAdminAsync();
        (await admin.Client.DeleteAsync($"{d.Department.Url}/members/{member.Id}")).Expect(HttpStatusCode.NoContent);
        Assert.DoesNotContain(member.Id, FollowerIds((await d.Agent.Client.GetAsync(Url(t.Id))).Expect(HttpStatusCode.OK)));
        Assert.Equal(0, await fixture.CountAsync("SELECT count(*) FROM ticketing.ticket_followers WHERE ticket_id = @t AND user_id = @u", TicketKit.P("t", t.Id), TicketKit.P("u", member.Id)));
        (await member.GetTicketAsync(t.Id)).Expect(HttpStatusCode.NotFound);
        Assert.Empty((await member.Client.GetAsync("/api/tickets?queue=following")).Expect(HttpStatusCode.OK).Ids());
    }

    [Fact]
    public async Task Candidates_are_department_readers_matched_by_name_or_email_without_current_followers()
    {
        var d = await fixture.NewDeptAsync();
        var other = await fixture.NewDeptAsync();
        var t = (await d.TicketForAsync($"a-{Unique.Id()}@example.test")).Expect(HttpStatusCode.Created);
        var outsider = (await other.Agent.Client.GetAsync("/api/me")).Expect(HttpStatusCode.OK).Json.Str("email");

        var all = (await d.Agent.Client.GetAsync(TicketKit.Url(t.Id, "/follower-candidates?q="))).Expect(HttpStatusCode.OK);
        var ids = all.Json.EnumerateArray().Select(c => c.GetProperty("userId").GetGuid()).ToArray();
        Assert.Equal(new[] { d.Admin.Id, d.Lead.Id, d.Agent.Id, d.Viewer.Id }.Order().ToArray(), ids.Order().ToArray());
        Assert.DoesNotContain(other.Agent.Id.ToString(), all.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(outsider, all.Body, StringComparison.Ordinal);

        var byEmail = (await d.Agent.Client.GetAsync(TicketKit.Url(t.Id, $"/follower-candidates?q={Uri.EscapeDataString((await d.Lead.Client.GetAsync("/api/me")).Expect(HttpStatusCode.OK).Json.Str("email"))}"))).Expect(HttpStatusCode.OK);
        Assert.Equal(d.Lead.Id, Assert.Single(byEmail.Json.EnumerateArray()).GetProperty("userId").GetGuid());

        (await d.Agent.Client.PostAsync(Url(t.Id, $"/{d.Lead.Id}"))).Expect(HttpStatusCode.OK);
        var after = (await d.Agent.Client.GetAsync(TicketKit.Url(t.Id, "/follower-candidates?q="))).Expect(HttpStatusCode.OK);
        Assert.DoesNotContain(after.Json.EnumerateArray(), c => c.GetProperty("userId").GetGuid() == d.Lead.Id);
        (await d.Viewer.Client.GetAsync(TicketKit.Url(t.Id, "/follower-candidates?q="))).Expect(HttpStatusCode.Forbidden); // listing colleagues is part of managing followers
    }

    [Fact]
    public async Task Followers_get_no_internal_note_access_from_following()
    {
        var d = await fixture.NewDeptAsync();
        var t = (await d.TicketForAsync($"a-{Unique.Id()}@example.test")).Expect(HttpStatusCode.Created);
        (await d.Agent.PostMessageAsync(t.Id, "Internal", "secret-note-text")).Expect(HttpStatusCode.Created);
        (await d.Viewer.Client.PostAsync(Url(t.Id, "/me"))).Expect(HttpStatusCode.OK);

        await using var noNotes = fixture.Factory.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
        {
            var original = services.Single(s => s.ServiceType == typeof(IAuthorizer));
            services.AddScoped(original.ImplementationType!);
            services.AddScoped<IAuthorizer>(sp => new TicketIsolationLeakageTests.DenyingAuthorizer((IAuthorizer)sp.GetRequiredService(original.ImplementationType!), [PermissionCodes.TicketsNotesRead]));
        }));
        var follower = await fixture.SignInAsync(d.Viewer.Subject, factory: noNotes);
        Assert.True((await follower.Client.GetAsync(Url(t.Id))).Expect(HttpStatusCode.OK).Json.GetProperty("following").GetBoolean());
        var messages = (await follower.Client.GetAsync(TicketKit.Url(t.Id, "/messages"))).Expect(HttpStatusCode.OK);
        Assert.DoesNotContain("secret-note-text", messages.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("Internal", messages.Body, StringComparison.Ordinal);
        var activity = (await follower.Client.GetAsync(TicketKit.Url(t.Id, "/activity"))).Expect(HttpStatusCode.OK);
        Assert.DoesNotContain("Internal", activity.Body, StringComparison.Ordinal);
        Assert.Contains("ticket.follower_added", activity.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_following_queue_is_a_server_side_filter_of_readable_tickets_and_works_as_a_saved_view()
    {
        var d = await fixture.NewDeptAsync();
        var followed = (await d.TicketForAsync($"a-{Unique.Id()}@example.test", "Followed")).Expect(HttpStatusCode.Created);
        var ignored = (await d.TicketForAsync($"b-{Unique.Id()}@example.test", "Ignored")).Expect(HttpStatusCode.Created);
        (await d.Agent.Client.PostAsync(Url(followed.Id, "/me"))).Expect(HttpStatusCode.OK);
        (await d.Lead.Client.PostAsync(Url(ignored.Id, "/me"))).Expect(HttpStatusCode.OK);

        var mine = (await d.Agent.Client.GetAsync($"/api/tickets?queue=following&departmentId={d.Id}&limit=1")).Expect(HttpStatusCode.OK);
        Assert.Equal([followed.Id], mine.Ids());
        Assert.Null(mine.NextCursor());
        Assert.Equal([ignored.Id], (await d.Lead.Client.GetAsync($"/api/tickets?queue=following&departmentId={d.Id}")).Expect(HttpStatusCode.OK).Ids());
        Assert.Equal(1, (await d.Agent.Client.GetAsync("/api/tickets/queue-counts")).Expect(HttpStatusCode.OK).Json.GetProperty("following").GetInt32());

        var view = (await d.Agent.Client.PostAsync("/api/views", new { name = "Following", filter = new { queue = "following", status = new[] { "Open" } } })).Expect(HttpStatusCode.Created);
        Assert.Equal([followed.Id], (await d.Agent.Client.GetAsync($"/api/tickets?view={view.Id}")).Expect(HttpStatusCode.OK).Ids());

        // Unfollowing removes it from the queue; the ticket itself stays readable by everyone who could read it.
        (await d.Agent.Client.DeleteAsync(Url(followed.Id, "/me"))).Expect(HttpStatusCode.OK);
        Assert.Empty((await d.Agent.Client.GetAsync($"/api/tickets?queue=following&departmentId={d.Id}")).Expect(HttpStatusCode.OK).Ids());
    }

    [Fact]
    public async Task Merging_carries_valid_followers_to_the_canonical_ticket_and_the_merged_ticket_has_no_follow_control()
    {
        var d = await fixture.NewDeptAsync();
        var canonical = (await d.TicketForAsync($"a-{Unique.Id()}@example.test", "Canonical")).Expect(HttpStatusCode.Created);
        var source = (await d.TicketForAsync($"b-{Unique.Id()}@example.test", "Duplicate")).Expect(HttpStatusCode.Created);
        (await d.Agent.Client.PostAsync(Url(source.Id, "/me"))).Expect(HttpStatusCode.OK);
        (await d.Lead.Client.PostAsync(Url(source.Id, "/me"))).Expect(HttpStatusCode.OK);
        (await d.Lead.Client.PostAsync(Url(canonical.Id, "/me"))).Expect(HttpStatusCode.OK); // already follows the canonical ticket: collapses
        (await d.Viewer.Client.PostAsync(Url(canonical.Id, "/me"))).Expect(HttpStatusCode.OK); // existing canonical follower stays

        // A follower who lost the membership is already gone and cannot be carried over.
        var leaver = await fixture.NewMemberAsync(d.Department, "Agent");
        (await leaver.Client.PostAsync(Url(source.Id, "/me"))).Expect(HttpStatusCode.OK);
        var admin = await fixture.PlatformAdminAsync();
        (await admin.Client.DeleteAsync($"{d.Department.Url}/members/{leaver.Id}")).Expect(HttpStatusCode.NoContent);

        var auditBefore = (await fixture.AuditOfAsync(canonical.Id)).Count;
        (await MergeAsync(d.Agent, canonical.Id, source.Id)).Expect(HttpStatusCode.OK);

        var result = (await d.Agent.Client.GetAsync(Url(canonical.Id))).Expect(HttpStatusCode.OK);
        Assert.Equal(new[] { d.Agent.Id, d.Lead.Id, d.Viewer.Id }.Order().ToArray(), FollowerIds(result).Order().ToArray());
        Assert.Equal(3, await fixture.CountAsync("SELECT count(*) FROM ticketing.ticket_followers WHERE ticket_id = @t", TicketKit.P("t", canonical.Id)));
        Assert.Equal(0, await fixture.CountAsync("SELECT count(*) FROM ticketing.ticket_followers WHERE ticket_id = @t", TicketKit.P("t", source.Id)));

        // One merge event, no per-follower noise.
        var audit = await fixture.AuditOfAsync(canonical.Id);
        Assert.Equal(auditBefore + 1, audit.Count);
        Assert.Contains("\"followersTransferred\": 1", audit.Single(a => a.Action == "ticket.merged_in").Next, StringComparison.Ordinal);

        (await d.Agent.Client.PostAsync(Url(source.Id, "/me"))).ExpectCode(HttpStatusCode.Conflict, "ticket_merged");
        (await d.Agent.Client.PostAsync(Url(source.Id, $"/{d.Lead.Id}"))).ExpectCode(HttpStatusCode.Conflict, "ticket_merged");
        Assert.Empty((await d.Agent.Client.GetAsync(Url(source.Id))).Expect(HttpStatusCode.OK).Json.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task Followers_do_not_follow_links_and_the_portal_never_shows_follower_information()
    {
        var d = await fixture.NewDeptAsync();
        var alice = await fixture.EmployeeAsync("alice");
        var a = (await d.TicketForAsync(alice.Email, "Alice request")).Expect(HttpStatusCode.Created);
        var b = (await d.TicketForAsync($"b-{Unique.Id()}@example.test", "Linked")).Expect(HttpStatusCode.Created);
        (await d.Agent.Client.PostAsync(TicketKit.Url(a.Id, "/links"), new { ticketId = b.Id, type = "Related" })).Expect(HttpStatusCode.Created);
        (await d.Agent.Client.PostAsync(Url(a.Id, "/me"))).Expect(HttpStatusCode.OK);

        Assert.Empty(FollowerIds((await d.Agent.Client.GetAsync(Url(b.Id))).Expect(HttpStatusCode.OK))); // relationships stay independent

        var list = (await alice.User.Client.GetAsync("/api/portal/requests")).Expect(HttpStatusCode.OK);
        var detail = (await alice.User.Client.GetAsync($"/api/portal/requests/{a.Reference()}")).Expect(HttpStatusCode.OK);
        foreach (var body in new[] { list.Body, detail.Body })
        {
            Assert.DoesNotContain("follow", body, StringComparison.OrdinalIgnoreCase);
        }

        // There is no follower route for a requester: they hold no agent permissions.
        (await alice.User.Client.GetAsync(Url(a.Id))).Expect(HttpStatusCode.NotFound);
        (await alice.User.Client.PostAsync(Url(a.Id, "/me"))).Expect(HttpStatusCode.NotFound);
        _ = JsonDocument.Parse(detail.Body);
    }
}
