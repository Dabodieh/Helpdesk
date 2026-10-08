using System.Net;
using Helpdesk.Host.Tests.Infrastructure;
using Npgsql;

namespace Helpdesk.Host.Tests.Ticketing;

/// <summary>PATCH (subject/status/priority), assignment rules, concurrency and the database constraints behind them.</summary>
public sealed class TicketChangeTests(HelpdeskFixture fixture) : IClassFixture<HelpdeskFixture>
{
    [Fact]
    public async Task Patch_changes_subject_priority_and_status_and_audits_each_change()
    {
        var d = await fixture.NewDeptAsync();
        var t = await d.Agent.CreateTicketAsync(d.Department);

        var patched = (await d.Agent.PatchTicketAsync(t, new { Subject = "  New subject ", Priority = "urgent", Status = "pending" })).Expect(HttpStatusCode.OK);
        Assert.Equal("New subject", patched.Json.Str("subject"));
        Assert.Equal("Urgent", patched.Json.Str("priority"));
        Assert.Equal("Pending", patched.Json.Str("status"));
        Assert.NotEqual(t.Version, patched.Version);
        Assert.True(patched.Json.GetProperty("updatedAt").GetDateTimeOffset() >= t.Json.GetProperty("updatedAt").GetDateTimeOffset());

        var rows = (await fixture.AuditOfAsync(t.Id)).Where(r => r.Action != "ticket.created" && r.Action != "ticket.message_added").ToList();
        Assert.Equal(["ticket.priority_changed", "ticket.status_changed", "ticket.subject_changed"], rows.Select(r => r.Action).Order(StringComparer.Ordinal));
        Assert.All(rows, r => Assert.Equal(d.Agent.Id, r.ActorUserId));
        var status = rows.Single(r => r.Action == "ticket.status_changed");
        Assert.Equal("""{"status": "Open"}""", status.Previous);
        Assert.Equal("""{"status": "Pending"}""", status.Next);
        Assert.Equal("""{"subject": "New subject"}""", rows.Single(r => r.Action == "ticket.subject_changed").Next);

        // A patch that changes nothing writes nothing.
        var again = (await d.Agent.PatchTicketAsync(patched, new { Priority = "Urgent", Status = "Pending" })).Expect(HttpStatusCode.OK);
        Assert.Equal(patched.Version, again.Version);
        Assert.Equal(rows.Count + 2, (await fixture.AuditOfAsync(t.Id)).Count);
    }

    [Fact]
    public async Task Status_timestamps_follow_the_rules()
    {
        var d = await fixture.NewDeptAsync();
        var t = await d.Agent.CreateTicketAsync(d.Department);

        var resolved = (await d.Agent.PatchTicketAsync(t, new { Status = "Resolved" })).Expect(HttpStatusCode.OK);
        Assert.NotEqual(System.Text.Json.JsonValueKind.Null, resolved.Json.GetProperty("resolvedAt").ValueKind);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, resolved.Json.GetProperty("closedAt").ValueKind);

        var closed = (await d.Agent.PatchTicketAsync(resolved, new { Status = "Closed" })).Expect(HttpStatusCode.OK);
        Assert.Equal(resolved.Json.GetProperty("resolvedAt").GetDateTimeOffset(), closed.Json.GetProperty("resolvedAt").GetDateTimeOffset());
        Assert.NotEqual(System.Text.Json.JsonValueKind.Null, closed.Json.GetProperty("closedAt").ValueKind);

        var reopened = (await d.Agent.PatchTicketAsync(closed, new { Status = "Open" })).Expect(HttpStatusCode.OK);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, reopened.Json.GetProperty("resolvedAt").ValueKind);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, reopened.Json.GetProperty("closedAt").ValueKind);

        // Open -> Closed directly sets both.
        var direct = (await d.Agent.PatchTicketAsync(reopened, new { Status = "Closed" })).Expect(HttpStatusCode.OK);
        Assert.NotEqual(System.Text.Json.JsonValueKind.Null, direct.Json.GetProperty("resolvedAt").ValueKind);
        Assert.NotEqual(System.Text.Json.JsonValueKind.Null, direct.Json.GetProperty("closedAt").ValueKind);
        // Closed -> Resolved clears closed_at.
        var back = (await d.Agent.PatchTicketAsync(direct, new { Status = "Resolved" })).Expect(HttpStatusCode.OK);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, back.Json.GetProperty("closedAt").ValueKind);
        // Pending clears both.
        var pending = (await d.Agent.PatchTicketAsync(back, new { Status = "Pending" })).Expect(HttpStatusCode.OK);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, pending.Json.GetProperty("resolvedAt").ValueKind);
    }

    [Fact]
    public async Task The_database_rejects_inconsistent_status_timestamps()
    {
        var d = await fixture.NewDeptAsync();
        var t = await d.Agent.CreateTicketAsync(d.Department);
        foreach (var sql in new[]
        {
            "UPDATE ticketing.tickets SET closed_at = now() WHERE id = @t",                       // closed_at implies status Closed
            "UPDATE ticketing.tickets SET status = 'Closed', closed_at = now() WHERE id = @t",    // Closed needs resolved_at
            "UPDATE ticketing.tickets SET status = 'Resolved' WHERE id = @t",                     // Resolved needs resolved_at
            "UPDATE ticketing.tickets SET resolved_at = now() WHERE id = @t",                     // Open must not carry resolved_at
            "UPDATE ticketing.tickets SET status = 'Bogus' WHERE id = @t",
            "UPDATE ticketing.tickets SET priority = 9 WHERE id = @t",
            "UPDATE ticketing.tickets SET subject = '   ' WHERE id = @t",
        })
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(() => fixture.ExecuteAsync(sql, TicketKit.P("t", t.Id)));
            Assert.Equal(PostgresErrorCodes.CheckViolation, ex.SqlState);
        }
    }

    [Fact]
    public async Task Patch_validation_permission_and_visibility()
    {
        var d = await fixture.NewDeptAsync();
        var other = await fixture.NewDeptAsync();
        var t = await d.Agent.CreateTicketAsync(d.Department);

        (await d.Agent.Client.PatchAsync(TicketKit.Url(t.Id), new { subject = "x" })).Expect(HttpStatusCode.BadRequest); // version required
        (await d.Agent.Client.PatchAsync(TicketKit.Url(t.Id), new { version = "abc", subject = "x" })).Expect(HttpStatusCode.BadRequest);
        (await d.Agent.Client.PatchAsync(TicketKit.Url(t.Id), new { version = t.Version })).Expect(HttpStatusCode.BadRequest); // nothing to change
        (await d.Agent.PatchTicketAsync(t, new { Status = "Nope" })).Expect(HttpStatusCode.BadRequest);
        (await d.Agent.PatchTicketAsync(t, new { Priority = "5" })).Expect(HttpStatusCode.BadRequest);
        (await d.Agent.PatchTicketAsync(t, new { Subject = new string('s', 201) })).Expect(HttpStatusCode.BadRequest);
        (await d.Agent.PatchTicketAsync(t, new { Subject = "  " })).Expect(HttpStatusCode.BadRequest);

        (await d.Viewer.PatchTicketAsync(t, new { Status = "Closed" })).Expect(HttpStatusCode.Forbidden);
        (await other.Agent.PatchTicketAsync(t, new { Status = "Closed" })).Expect(HttpStatusCode.NotFound);
        (await (await fixture.PlatformAdminAsync()).PatchTicketAsync(t, new { Status = "Closed" })).Expect(HttpStatusCode.NotFound);
        Assert.Equal("Open", (await d.Agent.GetTicketAsync(t.Id)).Json.Str("status"));
    }

    [Fact]
    public async Task A_stale_version_is_a_version_conflict_and_concurrent_writers_cannot_both_win()
    {
        var d = await fixture.NewDeptAsync();
        var t = await d.Agent.CreateTicketAsync(d.Department);
        (await d.Agent.PatchTicketAsync(t, new { Priority = "High" })).Expect(HttpStatusCode.OK);
        (await d.Lead.PatchTicketAsync(t, new { Priority = "Low" })).ExpectCode(HttpStatusCode.Conflict, "version_conflict");
        (await d.Lead.AssignAsync(t.Id, t.Version, d.Lead.Id)).ExpectCode(HttpStatusCode.Conflict, "version_conflict");
        Assert.Equal("High", (await d.Agent.GetTicketAsync(t.Id)).Json.Str("priority"));

        var fresh = (await d.Agent.GetTicketAsync(t.Id)).Expect(HttpStatusCode.OK);
        var results = await Task.WhenAll(
            d.Agent.PatchTicketAsync(fresh, new { Subject = "from agent" }),
            d.Lead.PatchTicketAsync(fresh, new { Subject = "from lead" }));
        Assert.Single(results, r => r.Status == HttpStatusCode.OK);
        Assert.Single(results, r => r.Status == HttpStatusCode.Conflict);
        Assert.Equal("version_conflict", results.Single(r => r.Status == HttpStatusCode.Conflict).Code());
    }

    // ----- assignment -----

    [Fact]
    public async Task Assign_any_sets_the_assignee_and_audits_it()
    {
        var d = await fixture.NewDeptAsync();
        var t = await d.Agent.CreateTicketAsync(d.Department);

        var assigned = (await d.Lead.AssignAsync(t.Id, t.Version, d.Agent.Id)).Expect(HttpStatusCode.OK);
        Assert.Equal(d.Agent.Id, assigned.Json.GetProperty("assignee").GetProperty("id").GetGuid());
        Assert.Equal((await d.Agent.Client.GetAsync("/api/me")).Json.Str("displayName"), assigned.Json.GetProperty("assignee").Str("displayName"));

        var rows = await fixture.AuditOfAsync(t.Id);
        var change = rows.Single(r => r.Action == "ticket.assignee_changed");
        Assert.Equal($$"""{"assigneeUserId": "{{d.Agent.Id}}"}""", change.Next);
        Assert.Equal("""{"assigneeUserId": null}""", change.Previous);
        Assert.Equal(d.Lead.Id, change.ActorUserId);
        Assert.DoesNotContain(rows, r => r.Action == "ticket.team_changed");

        // Clearing the assignee.
        var cleared = (await d.Lead.AssignAsync(t.Id, assigned.Version, null)).Expect(HttpStatusCode.OK);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, cleared.Json.GetProperty("assignee").ValueKind);
        // No change = no new events and an unchanged version.
        var same = (await d.Lead.AssignAsync(t.Id, cleared.Version, null)).Expect(HttpStatusCode.OK);
        Assert.Equal(cleared.Version, same.Version);
    }

    [Fact]
    public async Task Assign_self_allows_taking_and_releasing_own_tickets_only()
    {
        var d = await fixture.NewDeptAsync();
        var t = await d.Agent.CreateTicketAsync(d.Department);

        (await d.Viewer.AssignAsync(t.Id, t.Version, d.Viewer.Id)).Expect(HttpStatusCode.Forbidden); // no assign permission at all
        (await d.Agent.AssignAsync(t.Id, t.Version, d.Lead.Id)).Expect(HttpStatusCode.Forbidden);     // someone else
        (await d.Agent.AssignAsync(t.Id, t.Version, null)).Expect(HttpStatusCode.Forbidden);          // not the assignee: nothing to release

        var taken = (await d.Agent.AssignAsync(t.Id, t.Version, d.Agent.Id)).Expect(HttpStatusCode.OK);
        Assert.Equal(d.Agent.Id, taken.Json.GetProperty("assignee").GetProperty("id").GetGuid());
        (await d.Agent.AssignAsync(t.Id, taken.Version, d.Lead.Id)).Expect(HttpStatusCode.Forbidden);
        var released = (await d.Agent.AssignAsync(t.Id, taken.Version, null)).Expect(HttpStatusCode.OK);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, released.Json.GetProperty("assignee").ValueKind);

        // Others' tickets: an agent cannot release someone else's ticket.
        var byLead = (await d.Lead.AssignAsync(t.Id, released.Version, d.Lead.Id)).Expect(HttpStatusCode.OK);
        (await d.Agent.AssignAsync(t.Id, byLead.Version, null)).Expect(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Assign_self_claims_unassigned_tickets_but_never_takes_another_agents_ticket()
    {
        var d = await fixture.NewDeptAsync();
        var t = await d.Agent.CreateTicketAsync(d.Department);

        var ownedByLead = (await d.Lead.AssignAsync(t.Id, t.Version, d.Lead.Id)).Expect(HttpStatusCode.OK);
        // Already assigned to another agent: self-assign alone is not allowed to take it over, whatever the version.
        (await d.Agent.AssignAsync(t.Id, ownedByLead.Version, d.Agent.Id)).Expect(HttpStatusCode.Forbidden);
        Assert.Equal(d.Lead.Id, (await d.Agent.GetTicketAsync(t.Id)).Json.GetProperty("assignee").GetProperty("id").GetGuid());

        // Reassignment of someone else's ticket needs the broader permission (TeamLead holds it).
        var reassigned = (await d.Lead.AssignAsync(t.Id, ownedByLead.Version, d.Agent.Id)).Expect(HttpStatusCode.OK);
        Assert.Equal(d.Agent.Id, reassigned.Json.GetProperty("assignee").GetProperty("id").GetGuid());

        // The assignee keeps it (idempotent), releases it, and then anybody may claim the unassigned ticket.
        (await d.Agent.AssignAsync(t.Id, reassigned.Version, d.Agent.Id)).Expect(HttpStatusCode.OK);
        var released = (await d.Agent.AssignAsync(t.Id, reassigned.Version, null)).Expect(HttpStatusCode.OK);
        var claimed = (await d.Agent.AssignAsync(t.Id, released.Version, d.Agent.Id)).Expect(HttpStatusCode.OK);
        Assert.Equal(d.Agent.Id, claimed.Json.GetProperty("assignee").GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Assignment_to_a_user_outside_the_department_is_invalid_state()
    {
        var d = await fixture.NewDeptAsync();
        var other = await fixture.NewDeptAsync();
        var t = await d.Agent.CreateTicketAsync(d.Department);

        (await d.Lead.AssignAsync(t.Id, t.Version, other.Agent.Id)).ExpectCode(HttpStatusCode.Conflict, "invalid_state");
        (await d.Lead.AssignAsync(t.Id, t.Version, d.Viewer.Id)).ExpectCode(HttpStatusCode.Conflict, "invalid_state");
        Assert.Equal(t.Version, (await d.Agent.GetTicketAsync(t.Id)).Version);
    }

    [Fact]
    public async Task The_database_composite_foreign_keys_reject_foreign_assignees_and_cross_department_requesters()
    {
        var d = await fixture.NewDeptAsync();
        var other = await fixture.NewDeptAsync();
        var t = await d.Agent.CreateTicketAsync(d.Department);
        var otherTicket = await other.Agent.CreateTicketAsync(other.Department);

        async Task Rejects(string sql, string constraint)
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(() => fixture.ExecuteAsync(
                sql, TicketKit.P("t", t.Id), TicketKit.P("u", other.Agent.Id), TicketKit.P("viewerless", d.Viewer.Id),
                TicketKit.P("r", otherTicket.Json.GetProperty("requester").GetProperty("id").GetGuid())));
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, ex.SqlState);
            Assert.Equal(constraint, ex.ConstraintName);
        }

        await Rejects("UPDATE ticketing.tickets SET assignee_user_id = @u WHERE id = @t", "fk_tickets_assignee_membership");
        await Rejects("UPDATE ticketing.tickets SET requester_id = @r WHERE id = @t", "fk_tickets_requester_department");
        // A message cannot live in another department than its ticket, nor be authored by another department's requester.
        await Rejects(
            $"INSERT INTO ticketing.messages (id, ticket_id, department_id, kind, author_type, author_requester_id, body, source, delivery_state, created_at) VALUES (gen_random_uuid(), @t, '{other.Id}', 'Public', 'Requester', @r, 'x', 'web', 'None', now())",
            "fk_messages_ticket_department");
        await Rejects(
            $"INSERT INTO ticketing.messages (id, ticket_id, department_id, kind, author_type, author_requester_id, body, source, delivery_state, created_at) VALUES (gen_random_uuid(), @t, '{d.Id}', 'Public', 'Requester', @r, 'x', 'web', 'None', now())",
            "fk_messages_author_requester");
        // Control: a member of the ticket's own department is accepted by the same FK.
        Assert.Equal(1, await fixture.ExecuteAsync("UPDATE ticketing.tickets SET assignee_user_id = @viewerless WHERE id = @t", TicketKit.P("t", t.Id), TicketKit.P("viewerless", d.Viewer.Id)));
    }

    [Fact]
    public async Task Removing_a_department_membership_unassigns_the_members_tickets()
    {
        var d = await fixture.NewDeptAsync();
        var leaving = await fixture.NewMemberAsync(d.Department, "Agent");
        var t = await d.Agent.CreateTicketAsync(d.Department);
        var assigned = (await d.Lead.AssignAsync(t.Id, t.Version, leaving.Id)).Expect(HttpStatusCode.OK);
        Assert.Equal(leaving.Id, assigned.Json.GetProperty("assignee").GetProperty("id").GetGuid());

        var admin = await fixture.PlatformAdminAsync();
        (await admin.Client.DeleteAsync($"{d.Department.Url}/members/{leaving.Id}")).Expect(HttpStatusCode.NoContent);

        var after = (await d.Agent.GetTicketAsync(t.Id)).Expect(HttpStatusCode.OK);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, after.Json.GetProperty("assignee").ValueKind);
        Assert.Equal(d.Id, await fixture.ScalarAsync<Guid>("SELECT department_id FROM ticketing.tickets WHERE id = @t", TicketKit.P("t", t.Id))); // department untouched
    }

    [Fact]
    public async Task Assignment_is_hidden_from_outsiders_and_requires_a_valid_version()
    {
        var d = await fixture.NewDeptAsync();
        var other = await fixture.NewDeptAsync();
        var t = await d.Agent.CreateTicketAsync(d.Department);

        (await other.Lead.AssignAsync(t.Id, t.Version, null)).Expect(HttpStatusCode.NotFound);
        (await d.Lead.Client.PutAsync(TicketKit.Url(t.Id, "/assignment"), new { assigneeUserId = d.Lead.Id })).Expect(HttpStatusCode.BadRequest);
        (await d.Lead.AssignAsync(Guid.NewGuid(), t.Version, null)).Expect(HttpStatusCode.NotFound);
    }
}
