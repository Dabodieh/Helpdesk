using System.Net;
using System.Text.Json;
using Helpdesk.Host.Tests.Infrastructure;
using Helpdesk.Host.Tests.Ticketing;

namespace Helpdesk.Host.Tests.Productivity;

/// <summary>Saved views (owner-only, structured, executed through the normal scoped list) and bulk actions (per-ticket authorization).</summary>
public sealed class ViewsAndBulkTests(HelpdeskFixture fixture) : IClassFixture<HelpdeskFixture>
{
    private static object Filter(string? queue = "all", string[]? status = null, string[]? priority = null, Guid? departmentId = null, string? sort = null, string? q = null, bool? unassigned = null) =>
        new { queue, status, priority, departmentId, sort, q, unassigned };

    // ----- saved views -----

    [Fact]
    public async Task Views_are_created_listed_pinned_renamed_and_deleted_by_their_owner()
    {
        var d = await fixture.NewDeptAsync();
        var name = $"Urgent {Unique.Id()}";

        var created = (await d.Agent.Client.PostAsync("/api/views", new { name, filter = Filter(status: ["Open", "pending"], priority: ["urgent"], sort: "priority_desc"), isPinned = true })).Expect(HttpStatusCode.Created);
        Assert.Equal(["Open", "Pending"], created.Json.GetProperty("filter").GetProperty("status").EnumerateArray().Select(e => e.GetString()!).ToArray()); // canonical casing
        Assert.Equal("Urgent", created.Json.GetProperty("filter").GetProperty("priority")[0].GetString());

        var list = (await d.Agent.Client.GetAsync("/api/views")).Expect(HttpStatusCode.OK);
        Assert.Contains(list.Json.EnumerateArray(), v => v.GetProperty("id").GetGuid() == created.Id && v.GetProperty("isPinned").GetBoolean());

        var renamed = (await d.Agent.Client.PatchAsync($"/api/views/{created.Id}", new { version = created.Version, name = name + " v2", isPinned = false })).Expect(HttpStatusCode.OK);
        Assert.Equal(name + " v2", renamed.Json.Str("name"));
        (await d.Agent.Client.PatchAsync($"/api/views/{created.Id}", new { version = created.Version, name = "stale" })).ExpectCode(HttpStatusCode.Conflict, "version_conflict");
        (await d.Agent.Client.DeleteAsync($"/api/views/{created.Id}")).Expect(HttpStatusCode.NoContent);
        Assert.DoesNotContain((await d.Agent.Client.GetAsync("/api/views")).Json.EnumerateArray(), v => v.GetProperty("id").GetGuid() == created.Id);
    }

    [Fact]
    public async Task Views_are_private_validated_and_names_are_unique_per_owner()
    {
        var d = await fixture.NewDeptAsync();
        var name = $"Mine {Unique.Id()}";
        var created = (await d.Agent.Client.PostAsync("/api/views", new { name, filter = Filter() })).Expect(HttpStatusCode.Created);

        // Another user (even in the same department, even an admin) neither sees, edits nor runs it: 404.
        Assert.DoesNotContain((await d.Admin.Client.GetAsync("/api/views")).Json.EnumerateArray(), v => v.GetProperty("id").GetGuid() == created.Id);
        (await d.Admin.Client.PatchAsync($"/api/views/{created.Id}", new { version = created.Version, name = "x" })).Expect(HttpStatusCode.NotFound);
        (await d.Admin.Client.DeleteAsync($"/api/views/{created.Id}")).Expect(HttpStatusCode.NotFound);
        (await d.Admin.Client.GetAsync($"/api/tickets?view={created.Id}")).Expect(HttpStatusCode.NotFound);
        (await fixture.CreateAnonymousClient().GetAsync("/api/views")).Expect(HttpStatusCode.Unauthorized);

        (await d.Agent.Client.PostAsync("/api/views", new { name = name.ToUpperInvariant(), filter = Filter() })).ExpectCode(HttpStatusCode.Conflict, "duplicate");
        Assert.Contains("name", (await d.Agent.Client.PostAsync("/api/views", new { name = " ", filter = Filter() })).Expect(HttpStatusCode.BadRequest).Json.GetProperty("errors").EnumerateObject().Select(p => p.Name));
        foreach (var bad in new object[] { Filter(queue: "everything"), Filter(status: ["Nope"]), Filter(priority: ["Huge"]), Filter(sort: "random"), Filter(q: "x") })
        {
            (await d.Agent.Client.PostAsync("/api/views", new { name = $"Bad {Unique.Id()}", filter = bad })).Expect(HttpStatusCode.BadRequest);
        }
    }

    [Fact]
    public async Task A_view_can_never_return_more_than_the_normal_list_even_when_it_names_a_foreign_department()
    {
        var a = await fixture.NewDeptAsync();
        var b = await fixture.NewDeptAsync();
        var inB = await b.Agent.CreateTicketAsync(b.Department);
        var inA = await a.Agent.CreateTicketAsync(a.Department);

        // The stored definition points at department B and at B's ticket's team/assignee ids: execution is still scoped to A's agent.
        var view = (await a.Agent.Client.PostAsync("/api/views", new { name = $"Foreign {Unique.Id()}", filter = Filter(departmentId: b.Id, status: ["Open"]) })).Expect(HttpStatusCode.Created);
        var page = (await a.Agent.Client.GetAsync($"/api/tickets?view={view.Id}")).Expect(HttpStatusCode.OK);
        Assert.Empty(page.Ids());

        var own = (await a.Agent.Client.PostAsync("/api/views", new { name = $"Own {Unique.Id()}", filter = Filter(departmentId: a.Id, status: ["Open"]) })).Expect(HttpStatusCode.Created);
        var ownPage = (await a.Agent.Client.GetAsync($"/api/tickets?view={own.Id}")).Expect(HttpStatusCode.OK);
        Assert.Contains(inA.Id, ownPage.Ids());
        Assert.DoesNotContain(inB.Id, ownPage.Ids());

        // Pagination and limits still apply to a view run.
        (await a.Agent.Client.GetAsync($"/api/tickets?view={own.Id}&limit=0")).Expect(HttpStatusCode.BadRequest);
    }

    // ----- bulk -----

    [Fact]
    public async Task Bulk_changes_status_priority_team_and_assignee_and_audits_each_ticket()
    {
        var d = await fixture.NewDeptAsync();
        var team = await fixture.CreateTeamAsync(d.Department);
        var t1 = await d.Agent.CreateTicketAsync(d.Department);
        var t2 = await d.Agent.CreateTicketAsync(d.Department);

        var result = (await d.Lead.Client.PostAsync("/api/tickets/bulk", new
        {
            ticketIds = new[] { t1.Id, t2.Id },
            status = "Pending",
            priority = "High",
            team = new { id = team.Id },
            assignee = new { id = d.Agent.Id },
        })).Expect(HttpStatusCode.OK);

        Assert.Equal(2, result.Json.GetProperty("updated").GetInt32());
        Assert.Equal(0, result.Json.GetProperty("failed").GetInt32());
        foreach (var id in new[] { t1.Id, t2.Id })
        {
            var ticket = (await d.Agent.GetTicketAsync(id)).Expect(HttpStatusCode.OK);
            Assert.Equal("Pending", ticket.Json.Str("status"));
            Assert.Equal("High", ticket.Json.Str("priority"));
            Assert.Equal(team.Id, ticket.Json.GetProperty("team").GetProperty("id").GetGuid());
            Assert.Equal(d.Agent.Id, ticket.Json.GetProperty("assignee").GetProperty("id").GetGuid());
            var audit = await fixture.AuditOfAsync(id);
            foreach (var action in new[] { "ticket.status_changed", "ticket.priority_changed", "ticket.team_changed", "ticket.assignee_changed" })
            {
                Assert.Contains(audit, a => a.Action == action && a.ActorUserId == d.Lead.Id && a.Next!.Contains("\"bulk\": true", StringComparison.Ordinal));
            }
        }

        // The same request again changes nothing (reported as unchanged, no extra audit rows).
        var again = (await d.Lead.Client.PostAsync("/api/tickets/bulk", new { ticketIds = new[] { t1.Id }, status = "Pending" })).Expect(HttpStatusCode.OK);
        Assert.Equal(1, again.Json.GetProperty("unchanged").GetInt32());
    }

    [Fact]
    public async Task Bulk_authorises_every_ticket_individually_and_reports_partial_failure()
    {
        var a = await fixture.NewDeptAsync();
        var b = await fixture.NewDeptAsync();
        var own = await a.Agent.CreateTicketAsync(a.Department);
        var foreign = await b.Agent.CreateTicketAsync(b.Department);
        var ghost = Guid.NewGuid();
        var foreignBefore = (await b.Agent.GetTicketAsync(foreign.Id)).Version;

        var result = (await a.Agent.Client.PostAsync("/api/tickets/bulk", new { ticketIds = new[] { own.Id, foreign.Id, ghost }, priority = "Urgent" })).Expect(HttpStatusCode.OK);

        var items = result.Json.GetProperty("results").EnumerateArray().ToDictionary(r => r.GetProperty("ticketId").GetGuid(), r => r.Str("outcome"));
        Assert.Equal("updated", items[own.Id]);
        Assert.Equal("not_found", items[foreign.Id]); // indistinguishable from a missing id
        Assert.Equal("not_found", items[ghost]);
        Assert.Equal(1, result.Json.GetProperty("updated").GetInt32());
        Assert.Equal(2, result.Json.GetProperty("failed").GetInt32());
        Assert.Equal(foreignBefore, (await b.Agent.GetTicketAsync(foreign.Id)).Version); // untouched
        Assert.DoesNotContain(await fixture.AuditOfAsync(foreign.Id), x => x.Action == "ticket.priority_changed");
    }

    [Fact]
    public async Task Bulk_enforces_permissions_per_field_and_department_constraints()
    {
        var d = await fixture.NewDeptAsync();
        var other = await fixture.NewDeptAsync();
        var foreignTeam = await fixture.CreateTeamAsync(other.Department);
        var t = await d.Agent.CreateTicketAsync(d.Department);

        // Viewer: no update permission at all. Agent: may change status/priority and claim for self, but not assign teams or others.
        Assert.Equal("forbidden", Outcome(await d.Viewer.Client.PostAsync("/api/tickets/bulk", new { ticketIds = new[] { t.Id }, status = "Resolved" }), t.Id));
        Assert.Equal("updated", Outcome(await d.Agent.Client.PostAsync("/api/tickets/bulk", new { ticketIds = new[] { t.Id }, status = "Resolved" }), t.Id));
        Assert.Equal("forbidden", Outcome(await d.Agent.Client.PostAsync("/api/tickets/bulk", new { ticketIds = new[] { t.Id }, assignee = new { id = d.Lead.Id } }), t.Id));
        Assert.Equal("updated", Outcome(await d.Agent.Client.PostAsync("/api/tickets/bulk", new { ticketIds = new[] { t.Id }, assignee = new { id = d.Agent.Id } }), t.Id));

        // Department constraints: a team of another department and an assignee without a role there are refused, nothing changes.
        var invalidTeam = (await d.Lead.Client.PostAsync("/api/tickets/bulk", new { ticketIds = new[] { t.Id }, team = new { id = foreignTeam.Id } })).Expect(HttpStatusCode.OK);
        Assert.Equal("invalid", invalidTeam.Json.GetProperty("results")[0].Str("outcome"));
        Assert.Equal("invalid_state", invalidTeam.Json.GetProperty("results")[0].Str("code"));
        Assert.Equal("invalid", Outcome(await d.Lead.Client.PostAsync("/api/tickets/bulk", new { ticketIds = new[] { t.Id }, assignee = new { id = other.Agent.Id } }), t.Id));
        Assert.Equal("invalid", Outcome(await d.Lead.Client.PostAsync("/api/tickets/bulk", new { ticketIds = new[] { t.Id }, assignee = new { id = d.Viewer.Id } }), t.Id));
        Assert.Equal(JsonValueKind.Null, (await d.Agent.GetTicketAsync(t.Id)).Json.GetProperty("team").ValueKind);
    }

    [Fact]
    public async Task Bulk_requests_are_validated_and_limited()
    {
        var d = await fixture.NewDeptAsync();
        var t = await d.Agent.CreateTicketAsync(d.Department);

        (await d.Agent.Client.PostAsync("/api/tickets/bulk", new { ticketIds = Array.Empty<Guid>(), status = "Open" })).Expect(HttpStatusCode.BadRequest);
        (await d.Agent.Client.PostAsync("/api/tickets/bulk", new { ticketIds = Enumerable.Range(0, 101).Select(_ => Guid.NewGuid()).ToArray(), status = "Open" })).Expect(HttpStatusCode.BadRequest);
        (await d.Agent.Client.PostAsync("/api/tickets/bulk", new { ticketIds = new[] { t.Id } })).Expect(HttpStatusCode.BadRequest);
        (await d.Agent.Client.PostAsync("/api/tickets/bulk", new { ticketIds = new[] { t.Id }, status = "Nope" })).Expect(HttpStatusCode.BadRequest);
        (await fixture.CreateAnonymousClient().PostAsync("/api/tickets/bulk", new { ticketIds = new[] { t.Id }, status = "Open" })).Expect(HttpStatusCode.BadRequest); // CSRF first, as for every unsafe call
    }

    [Fact]
    public async Task Bulk_status_changes_keep_the_ticket_timestamp_rules()
    {
        var d = await fixture.NewDeptAsync();
        var t = await d.Agent.CreateTicketAsync(d.Department);

        await d.Agent.Client.PostAsync("/api/tickets/bulk", new { ticketIds = new[] { t.Id }, status = "Closed" });
        var closed = (await d.Agent.GetTicketAsync(t.Id)).Json;
        Assert.NotEqual(JsonValueKind.Null, closed.GetProperty("closedAt").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, closed.GetProperty("resolvedAt").ValueKind);

        await d.Agent.Client.PostAsync("/api/tickets/bulk", new { ticketIds = new[] { t.Id }, status = "Open" });
        var reopened = (await d.Agent.GetTicketAsync(t.Id)).Json;
        Assert.Equal(JsonValueKind.Null, reopened.GetProperty("closedAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, reopened.GetProperty("resolvedAt").ValueKind);
    }

    private static string Outcome(ApiResponse response, Guid ticketId) =>
        response.Expect(HttpStatusCode.OK).Json.GetProperty("results").EnumerateArray().Single(r => r.GetProperty("ticketId").GetGuid() == ticketId).Str("outcome");
}
