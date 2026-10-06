#pragma warning disable CA1861 // one-off request payloads
using System.Net;
using System.Text.Json;
using Helpdesk.Host.Tests.Infrastructure;
using Helpdesk.Host.Tests.Portal;

namespace Helpdesk.Host.Tests.Ticketing;


public sealed class RequesterHistoryTests(HelpdeskFixture fixture) : IClassFixture<HelpdeskFixture>
{
    private static Guid RequesterOf(ApiResponse ticket) => ticket.Json.GetProperty("requester").GetProperty("id").GetGuid();

    private static string[] References(JsonElement items) => [.. items.EnumerateArray().Select(i => i.GetProperty("reference").GetString()!)];

    [Fact]
    public async Task The_profile_shows_visible_data_and_counts_with_active_and_resolved_history()
    {
        var d = await fixture.NewDeptAsync();
        var email = $"dana-{Unique.Id()}@example.test";
        var t1 = (await d.TicketForAsync(email, "VPN drops")).Expect(HttpStatusCode.Created);
        var t2 = (await d.TicketForAsync(email, "Docking issue")).Expect(HttpStatusCode.Created);
        var t3 = (await d.TicketForAsync(email, "Password reset")).Expect(HttpStatusCode.Created);
        var detail3 = (await d.Agent.GetTicketAsync(t3.Id)).Expect(HttpStatusCode.OK);
        (await d.Agent.PatchTicketAsync(detail3, new { Status = "Resolved" })).Expect(HttpStatusCode.OK);
        var detail2 = (await d.Agent.GetTicketAsync(t2.Id)).Expect(HttpStatusCode.OK);
        (await d.Agent.PatchTicketAsync(detail2, new { Status = "Pending" })).Expect(HttpStatusCode.OK);

        var id = RequesterOf((await d.Agent.GetTicketAsync(t1.Id)).Expect(HttpStatusCode.OK));
        var profile = (await d.Agent.Client.GetAsync($"/api/requesters/{id}")).Expect(HttpStatusCode.OK).Json;
        Assert.Equal(email, profile.GetProperty("email").GetString());
        Assert.Equal(2, profile.GetProperty("openCount").GetInt32()); // Open + Pending
        Assert.Equal(1, profile.GetProperty("resolvedCount").GetInt32());
        Assert.Equal(3, profile.GetProperty("totalCount").GetInt32());
        Assert.Equal(2, profile.GetProperty("open").GetArrayLength());
        Assert.Equal([t3.Reference()], References(profile.GetProperty("previous")));

        var active = (await d.Agent.Client.GetAsync($"/api/requesters/{id}/tickets?state=active")).Expect(HttpStatusCode.OK).Json;
        Assert.Equal(2, active.GetProperty("items").GetArrayLength());
        var resolved = (await d.Agent.Client.GetAsync($"/api/requesters/{id}/tickets?state=resolved")).Expect(HttpStatusCode.OK).Json;
        Assert.Equal([t3.Reference()], References(resolved.GetProperty("items")));
        (await d.Agent.Client.GetAsync($"/api/requesters/{id}/tickets?state=nope")).Expect(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task History_is_paged_by_cursor_without_repeats_or_gaps()
    {
        var d = await fixture.NewDeptAsync();
        var email = $"pager-{Unique.Id()}@example.test";
        var created = new List<ApiResponse>();
        for (var i = 0; i < 5; i++)
        {
            created.Add((await d.TicketForAsync(email, $"Request {i}")).Expect(HttpStatusCode.Created));
            await Task.Delay(15);
        }

        var id = RequesterOf((await d.Agent.GetTicketAsync(created[0].Id)).Expect(HttpStatusCode.OK));
        var seen = new List<string>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = (await d.Agent.Client.GetAsync($"/api/requesters/{id}/tickets?limit=2" + (cursor is null ? "" : $"&cursor={cursor}"))).Expect(HttpStatusCode.OK);
            seen.AddRange(References(page.Json.GetProperty("items")));
            cursor = page.NextCursor();
            pages++;
        }
        while (cursor is not null);

        Assert.Equal(3, pages);
        Assert.Equal(5, seen.Distinct().Count());
        Assert.Equal(created.Select(c => c.Reference()).Reverse().ToArray(), seen.ToArray());
        (await d.Agent.Client.GetAsync($"/api/requesters/{id}/tickets?cursor=garbage")).Expect(HttpStatusCode.BadRequest);
        (await d.Agent.Client.GetAsync($"/api/requesters/{id}/tickets?limit=500")).Expect(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Tickets_in_departments_the_agent_cannot_read_are_absent_from_history_and_counts()
    {
        var it = await fixture.NewDeptAsync();
        var hr = await fixture.NewDeptAsync();
        var email = $"dana-{Unique.Id()}@example.test";
        var itTicket = (await it.TicketForAsync(email, "IT laptop")).Expect(HttpStatusCode.Created);
        var hrOpen = (await hr.TicketForAsync(email, "HR secret grievance")).Expect(HttpStatusCode.Created);
        var hrOpen2 = (await hr.TicketForAsync(email, "HR second matter")).Expect(HttpStatusCode.Created);
        var hrDetail = (await hr.Agent.GetTicketAsync(hrOpen2.Id)).Expect(HttpStatusCode.OK);
        (await hr.Agent.PatchTicketAsync(hrDetail, new { Status = "Resolved" })).Expect(HttpStatusCode.OK);

        var itRequester = RequesterOf((await it.Agent.GetTicketAsync(itTicket.Id)).Expect(HttpStatusCode.OK));
        var hrRequester = RequesterOf((await hr.Agent.GetTicketAsync(hrOpen.Id)).Expect(HttpStatusCode.OK));

        // The IT agent sees only the IT ticket: counts, lists and the summary all agree and mention nothing else.
        var profile = (await it.Agent.Client.GetAsync($"/api/requesters/{itRequester}")).Expect(HttpStatusCode.OK);
        Assert.Equal(1, profile.Json.GetProperty("totalCount").GetInt32());
        Assert.Equal(1, profile.Json.GetProperty("openCount").GetInt32());
        Assert.Equal(0, profile.Json.GetProperty("resolvedCount").GetInt32());
        var all = (await it.Agent.Client.GetAsync($"/api/requesters/{itRequester}/tickets")).Expect(HttpStatusCode.OK);
        Assert.Equal([itTicket.Reference()], References(all.Json.GetProperty("items")));
        Assert.Null(all.NextCursor());
        foreach (var body in new[] { profile.Body, all.Body })
        {
            Assert.DoesNotContain("HR secret", body);
            Assert.DoesNotContain(hrOpen.Reference(), body);
            Assert.DoesNotContain(hrOpen2.Reference(), body);
        }

        // The ticket header count agrees, and the other department's requester row simply does not exist for this agent.
        var header = (await it.Agent.GetTicketAsync(itTicket.Id)).Expect(HttpStatusCode.OK).Json.GetProperty("requester");
        Assert.Equal(1, header.GetProperty("totalTicketCount").GetInt32());
        (await it.Agent.Client.GetAsync($"/api/requesters/{hrRequester}")).Expect(HttpStatusCode.NotFound);
        (await it.Agent.Client.GetAsync($"/api/requesters/{hrRequester}/tickets")).Expect(HttpStatusCode.NotFound);

        // An agent who can read both departments sees one combined history of the same person.
        var both = await fixture.NewMemberAsync(it.Department, "Agent");
        await fixture.AddMemberAsync(hr.Department, both, "Agent");
        var combined = (await both.Client.GetAsync($"/api/requesters/{itRequester}")).Expect(HttpStatusCode.OK).Json;
        Assert.Equal(3, combined.GetProperty("totalCount").GetInt32());
        Assert.Equal(2, combined.GetProperty("openCount").GetInt32());
        Assert.Equal(1, combined.GetProperty("resolvedCount").GetInt32());
        Assert.Equal(3, (await both.Client.GetAsync($"/api/requesters/{hrRequester}/tickets")).Expect(HttpStatusCode.OK).Json.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Someone_without_any_ticket_access_gets_a_plain_404_and_a_viewer_of_the_ticket_department_may_see_it()
    {
        var d = await fixture.NewDeptAsync();
        var email = $"x-{Unique.Id()}@example.test";
        var t = (await d.TicketForAsync(email)).Expect(HttpStatusCode.Created);
        var id = RequesterOf((await d.Agent.GetTicketAsync(t.Id)).Expect(HttpStatusCode.OK));

        var stranger = await fixture.SignInAsync($"stranger-{Unique.Id()}", "Stranger", $"stranger-{Unique.Id()}@example.test");
        (await stranger.Client.GetAsync($"/api/requesters/{id}")).Expect(HttpStatusCode.NotFound);
        (await stranger.Client.GetAsync($"/api/requesters/{id}/tickets")).Expect(HttpStatusCode.NotFound);
        (await stranger.Client.GetAsync($"/api/requesters/{Guid.NewGuid()}")).Expect(HttpStatusCode.NotFound);
        (await d.Viewer.Client.GetAsync($"/api/requesters/{id}")).Expect(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Internal_note_activity_leaves_the_history_metadata_untouched_and_a_visible_ticket_opens_normally()
    {
        var d = await fixture.NewDeptAsync();
        var email = $"note-{Unique.Id()}@example.test";
        var older = (await d.TicketForAsync(email, "Older")).Expect(HttpStatusCode.Created);
        await Task.Delay(30);
        var newer = (await d.TicketForAsync(email, "Newer")).Expect(HttpStatusCode.Created);
        var id = RequesterOf((await d.Agent.GetTicketAsync(older.Id)).Expect(HttpStatusCode.OK));

        var before = (await d.Agent.Client.GetAsync($"/api/requesters/{id}")).Expect(HttpStatusCode.OK).Body;
        await Task.Delay(50);
        (await d.Agent.PostMessageAsync(older.Id, "Internal", "private thought")).Expect(HttpStatusCode.Created);
        var after = (await d.Agent.Client.GetAsync($"/api/requesters/{id}")).Expect(HttpStatusCode.OK).Body;
        Assert.Equal(before, after);
        Assert.DoesNotContain("private thought", after);
        var order = References((await d.Agent.Client.GetAsync($"/api/requesters/{id}/tickets")).Expect(HttpStatusCode.OK).Json.GetProperty("items"));
        Assert.Equal([newer.Reference(), older.Reference()], order);

        // Opening a listed ticket uses the normal ticket endpoint and its authorization.
        (await d.Agent.GetTicketAsync(older.Id)).Expect(HttpStatusCode.OK);
    }
}
