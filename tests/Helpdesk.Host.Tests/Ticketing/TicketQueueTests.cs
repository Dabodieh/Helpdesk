using System.Net;
using Helpdesk.Host.Tests.Infrastructure;
using Helpdesk.Modules.Organisation.Contracts;

namespace Helpdesk.Host.Tests.Ticketing;

/// <summary>Queues, filters, sorting, keyset pagination and queue counts of GET /api/tickets.</summary>
public sealed class TicketQueueTests(HelpdeskFixture fixture) : IClassFixture<HelpdeskFixture>
{
    private static readonly string[] Priorities = ["Low", "Normal", "High"];
    private static readonly string[] CountKeys = ["my", "team", "unassigned", "all"];

    private static Task<ApiResponse> List(TestUser user, string query = "") => user.Client.GetAsync("/api/tickets" + query);

    private static async Task<Guid[]> IdsAsync(TestUser user, string query = "") => (await List(user, query)).Expect(HttpStatusCode.OK).Ids();

    [Fact]
    public async Task List_items_carry_the_contract_shape_with_resolved_names()
    {
        var d = await fixture.NewDeptAsync();
        var team = await fixture.CreateTeamAsync(d.Department);
        var t = await d.Lead.CreateTicketAsync(d.Department, TicketHelpers.NewTicketBody(extra: new { TeamId = team.Id, AssigneeUserId = d.Agent.Id }));

        var item = Assert.Single((await List(d.Viewer)).Expect(HttpStatusCode.OK).Json.GetProperty("items").EnumerateArray());
        Assert.Equal(
            ["assignee", "createdAt", "departmentId", "departmentKey", "id", "match", "mergedInto", "priority", "reference", "requester", "sla", "status", "subject", "team", "updatedAt"],
            item.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(t.Id, item.GetProperty("id").GetGuid());
        Assert.Equal(d.Key, item.Str("departmentKey"));
        Assert.Equal(team.Json.Str("name"), item.GetProperty("team").Str("name"));
        Assert.Equal(d.Agent.Id, item.GetProperty("assignee").GetProperty("id").GetGuid());
        Assert.Equal("Some Requester", item.GetProperty("requester").Str("displayName"));
        Assert.Equal(["displayName", "email", "id"], item.GetProperty("requester").EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Queues_my_team_unassigned_and_all()
    {
        var d = await fixture.NewDeptAsync();
        var admin = await fixture.PlatformAdminAsync();
        var teamA = await fixture.CreateTeamAsync(d.Department);
        var teamB = await fixture.CreateTeamAsync(d.Department);
        (await admin.Client.PutAsync($"{d.Department.Url}/teams/{teamA.Id}/members/{d.Agent.Id}")).Expect(HttpStatusCode.NoContent);

        var mine = await d.Lead.CreateTicketAsync(d.Department, TicketHelpers.NewTicketBody(extra: new { AssigneeUserId = d.Agent.Id }));
        var inTeam = await d.Lead.CreateTicketAsync(d.Department, TicketHelpers.NewTicketBody(extra: new { TeamId = teamA.Id }));
        var otherTeam = await d.Lead.CreateTicketAsync(d.Department, TicketHelpers.NewTicketBody(extra: new { TeamId = teamB.Id, AssigneeUserId = d.Lead.Id }));
        var loose = await d.Lead.CreateTicketAsync(d.Department);

        Assert.Equal([mine.Id], await IdsAsync(d.Agent, "?queue=my"));
        Assert.Equal([inTeam.Id], await IdsAsync(d.Agent, "?queue=team"));
        Assert.Equal([otherTeam.Id], await IdsAsync(d.Lead, "?queue=my"));
        Assert.Empty(await IdsAsync(d.Lead, "?queue=team")); // the lead is in no team
        Assert.Equivalent(new[] { inTeam.Id, loose.Id }, await IdsAsync(d.Agent, "?queue=unassigned"));
        Assert.Equivalent(new[] { mine.Id, inTeam.Id, otherTeam.Id, loose.Id }, await IdsAsync(d.Agent));
        Assert.Equivalent(new[] { mine.Id, inTeam.Id, otherTeam.Id, loose.Id }, await IdsAsync(d.Viewer, "?queue=all"));
        Assert.Empty(await IdsAsync(d.Viewer, "?queue=my"));

        (await List(d.Agent, "?queue=nonsense")).Expect(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Filters_only_narrow_the_callers_own_scope()
    {
        var d = await fixture.NewDeptAsync();
        var other = await fixture.NewDeptAsync();
        var team = await fixture.CreateTeamAsync(d.Department);
        var a = await d.Lead.CreateTicketAsync(d.Department, TicketHelpers.NewTicketBody("Alpha router", extra: new { Priority = "High", TeamId = team.Id, AssigneeUserId = d.Agent.Id }));
        var b = await d.Lead.CreateTicketAsync(d.Department, TicketHelpers.NewTicketBody("Beta 100%_done", extra: new { Priority = "Low" }));
        var c = await d.Lead.CreateTicketAsync(d.Department, TicketHelpers.NewTicketBody("Gamma closed", extra: new { Priority = "Urgent" }));
        (await d.Lead.PatchTicketAsync(c, new { Status = "Closed" })).Expect(HttpStatusCode.OK);
        (await d.Lead.PatchTicketAsync(b, new { Status = "Pending" })).Expect(HttpStatusCode.OK);
        var foreign = await other.Agent.CreateTicketAsync(other.Department);

        // Default excludes Closed; explicit status wins; repeatable.
        Assert.Equivalent(new[] { a.Id, b.Id }, await IdsAsync(d.Viewer));
        Assert.Equal([c.Id], await IdsAsync(d.Viewer, "?status=closed"));
        Assert.Equivalent(new[] { a.Id, b.Id, c.Id }, await IdsAsync(d.Viewer, "?status=Open&status=Pending&status=Closed"));
        Assert.Equal([b.Id], await IdsAsync(d.Viewer, "?status=Pending"));
        Assert.Equal([a.Id], await IdsAsync(d.Viewer, "?priority=high"));
        Assert.Equivalent(new[] { a.Id, b.Id }, await IdsAsync(d.Viewer, "?priority=High&priority=Low"));
        Assert.Equal([a.Id], await IdsAsync(d.Viewer, $"?teamId={team.Id}"));
        Assert.Equal([a.Id], await IdsAsync(d.Viewer, $"?assigneeUserId={d.Agent.Id}"));
        Assert.Equal([b.Id], await IdsAsync(d.Viewer, "?unassigned=true"));

        // q: reference prefix or subject substring, wildcards are literal.
        Assert.Equal([a.Id], await IdsAsync(d.Viewer, "?q=" + a.Json.Str("reference").ToLowerInvariant()));
        Assert.Equal([a.Id], await IdsAsync(d.Viewer, "?q=ROUTER"));
        Assert.Equal([b.Id], await IdsAsync(d.Viewer, "?q=" + Uri.EscapeDataString("100%_")));
        Assert.Empty(await IdsAsync(d.Viewer, "?q=" + Uri.EscapeDataString("%%")));
        Assert.Empty(await IdsAsync(d.Viewer, "?q=" + Uri.EscapeDataString("_____")));
        (await List(d.Viewer, "?q=a")).Expect(HttpStatusCode.BadRequest);
        (await List(d.Viewer, "?q=" + new string('x', 101))).Expect(HttpStatusCode.BadRequest);

        // Inaccessible department / team / assignee filters: an empty page, indistinguishable from "no matches".
        Assert.Empty(await IdsAsync(d.Viewer, $"?departmentId={other.Id}"));
        Assert.Empty(await IdsAsync(d.Viewer, $"?departmentId={Guid.NewGuid()}"));
        var foreignTeam = await fixture.CreateTeamAsync(other.Department);
        Assert.Empty(await IdsAsync(d.Viewer, $"?teamId={foreignTeam.Id}"));
        Assert.DoesNotContain(foreign.Id, await IdsAsync(d.Viewer, "?status=Open&status=Pending&status=Resolved&status=Closed"));
        Assert.Equal([foreign.Id], await IdsAsync(other.Agent, $"?departmentId={other.Id}"));

        // Bad enums and ids are 400.
        (await List(d.Viewer, "?status=Bogus")).Expect(HttpStatusCode.BadRequest);
        (await List(d.Viewer, "?priority=Huge")).Expect(HttpStatusCode.BadRequest);
        (await List(d.Viewer, "?sort=newest")).Expect(HttpStatusCode.BadRequest);
        (await List(d.Viewer, "?departmentId=abc")).Expect(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Users_see_the_union_of_their_departments_and_nothing_else()
    {
        var a = await fixture.NewDeptAsync();
        var b = await fixture.NewDeptAsync();
        var c = await fixture.NewDeptAsync();
        var both = await fixture.NewUserAsync();
        await fixture.AddMemberAsync(a.Department, both, DepartmentRoles.Viewer);
        await fixture.AddMemberAsync(b.Department, both, DepartmentRoles.Agent);
        var ta = await a.Agent.CreateTicketAsync(a.Department);
        var tb = await b.Agent.CreateTicketAsync(b.Department);
        var tc = await c.Agent.CreateTicketAsync(c.Department);

        Assert.Equivalent(new[] { ta.Id, tb.Id }, await IdsAsync(both));
        Assert.Equal([tb.Id], await IdsAsync(both, $"?departmentId={b.Id}"));
        Assert.Empty(await IdsAsync(both, $"?departmentId={c.Id}"));
        Assert.DoesNotContain(tc.Id, await IdsAsync(both));
        Assert.Empty(await IdsAsync(await fixture.NewUserAsync()));
        Assert.Empty(await IdsAsync(await fixture.PlatformAdminAsync(), $"?departmentId={a.Id}")); // platform admin: no content access
        (await fixture.CreateAnonymousClient().GetAsync("/api/tickets")).Expect(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Keyset_pagination_is_stable_across_equal_timestamps_in_every_sort()
    {
        var d = await fixture.NewDeptAsync();
        var created = new List<Guid>();
        for (var i = 0; i < 9; i++)
        {
            created.Add((await d.Agent.CreateTicketAsync(d.Department, TicketHelpers.NewTicketBody(extra: new { Priority = Priorities[i % 3] }))).Id);
        }

        // Identical timestamps for most rows force the id tie-break.
        await fixture.ExecuteAsync(
            "UPDATE ticketing.tickets SET updated_at = '2026-03-01T10:00:00Z', created_at = '2026-03-01T09:00:00Z' WHERE department_id = @d AND id = ANY(@ids)",
            TicketKit.P("d", d.Id), TicketKit.P("ids", created.Take(7).ToArray()));

        foreach (var sort in new[] { "updated_desc", "updated_asc", "created_desc", "created_asc", "priority_desc" })
        {
            var full = await IdsAsync(d.Viewer, $"?sort={sort}&limit=100");
            Assert.Equal(9, full.Length);
            var paged = new List<Guid>();
            string? cursor = null;
            var pages = 0;
            do
            {
                var page = (await List(d.Viewer, $"?sort={sort}&limit=2" + (cursor is null ? "" : $"&cursor={cursor}"))).Expect(HttpStatusCode.OK);
                paged.AddRange(page.Ids());
                cursor = page.NextCursor();
                pages++;
            }
            while (cursor is not null);

            Assert.Equal(full, paged); // no skips, no repeats, same order
            Assert.Equal(5, pages);
            Assert.Equal(9, paged.Distinct().Count());
        }

        var urgentFirst = (await List(d.Viewer, "?sort=priority_desc&limit=100")).Expect(HttpStatusCode.OK).Json.GetProperty("items").EnumerateArray().Select(i => i.Str("priority")).ToList();
        Assert.Equal(urgentFirst.OrderBy(p => p switch { "High" => 0, "Normal" => 1, _ => 2 }), urgentFirst);

        // Equal timestamps: ordered by id desc / asc.
        var tied = await IdsAsync(d.Viewer, "?sort=updated_desc&limit=100");
        var tiedIds = tied.Where(id => created.Take(7).Contains(id)).ToList();
        Assert.Equal(tiedIds.OrderByDescending(x => x), tiedIds);
    }

    [Fact]
    public async Task Limit_and_cursor_are_validated()
    {
        var d = await fixture.NewDeptAsync();
        for (var i = 0; i < 3; i++)
        {
            await d.Agent.CreateTicketAsync(d.Department);
        }

        (await List(d.Viewer, "?limit=0")).Expect(HttpStatusCode.BadRequest);
        (await List(d.Viewer, "?limit=101")).Expect(HttpStatusCode.BadRequest);
        (await List(d.Viewer, "?limit=-5")).Expect(HttpStatusCode.BadRequest);
        (await List(d.Viewer, "?limit=100")).Expect(HttpStatusCode.OK);
        (await List(d.Viewer, "?cursor=not-a-cursor")).Expect(HttpStatusCode.BadRequest);
        (await List(d.Viewer, $"?cursor={Helpdesk.SharedKernel.Paging.Cursor.Encode("ud|x|y")}")).Expect(HttpStatusCode.BadRequest);

        // A cursor of another sort is invalid.
        var page = (await List(d.Viewer, "?limit=1&sort=created_desc")).Expect(HttpStatusCode.OK);
        (await List(d.Viewer, $"?sort=updated_desc&cursor={page.NextCursor()}")).Expect(HttpStatusCode.BadRequest);
        Assert.Null((await List(d.Viewer, "?limit=3")).NextCursor());
    }

    [Fact]
    public async Task Queue_counts_count_open_tickets_in_accessible_departments_only()
    {
        var d = await fixture.NewDeptAsync();
        var other = await fixture.NewDeptAsync();
        var admin = await fixture.PlatformAdminAsync();
        var team = await fixture.CreateTeamAsync(d.Department);
        (await admin.Client.PutAsync($"{d.Department.Url}/teams/{team.Id}/members/{d.Agent.Id}")).Expect(HttpStatusCode.NoContent);

        await d.Lead.CreateTicketAsync(d.Department, TicketHelpers.NewTicketBody(extra: new { AssigneeUserId = d.Agent.Id }));          // my
        await d.Lead.CreateTicketAsync(d.Department, TicketHelpers.NewTicketBody(extra: new { TeamId = team.Id }));                      // team + unassigned
        await d.Lead.CreateTicketAsync(d.Department);                                                                                   // unassigned
        var pending = await d.Lead.CreateTicketAsync(d.Department, TicketHelpers.NewTicketBody(extra: new { AssigneeUserId = d.Agent.Id }));
        (await d.Lead.PatchTicketAsync(pending, new { Status = "Pending" })).Expect(HttpStatusCode.OK); // not Open: not counted
        await other.Agent.CreateTicketAsync(other.Department);
        await other.Agent.CreateTicketAsync(other.Department);

        var counts = (await d.Agent.Client.GetAsync("/api/tickets/queue-counts")).Expect(HttpStatusCode.OK).Json;
        Assert.Equal(1, counts.GetProperty("my").GetInt32());
        Assert.Equal(1, counts.GetProperty("team").GetInt32());
        Assert.Equal(2, counts.GetProperty("unassigned").GetInt32());
        Assert.Equal(3, counts.GetProperty("all").GetInt32());

        var empty = (await (await fixture.NewUserAsync()).Client.GetAsync("/api/tickets/queue-counts")).Expect(HttpStatusCode.OK).Json;
        Assert.Equal([0, 0, 0, 0], CountKeys.Select(k => empty.GetProperty(k).GetInt32()));
        (await fixture.CreateAnonymousClient().GetAsync("/api/tickets/queue-counts")).Expect(HttpStatusCode.Unauthorized);
    }
}
