using System.Net;
using System.Text.Json;
using Helpdesk.Host.Tests.Infrastructure;
using Helpdesk.Modules.Organisation.Contracts;
using Helpdesk.SharedKernel.Authorization;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Helpdesk.Host.Tests.Ticketing;

/// <summary>Messages (public replies, internal notes), delivery truthfulness, notes gate, activity feed and message CHECK constraints.</summary>
public sealed class TicketMessageTests(HelpdeskFixture fixture) : IClassFixture<HelpdeskFixture>
{
    [Fact]
    public async Task Public_reply_is_recorded_not_delivered_and_internal_note_carries_no_delivery_state()
    {
        var d = await fixture.NewDeptAsync();
        var t = await d.Agent.CreateTicketAsync(d.Department);

        var reply = (await d.Agent.PostMessageAsync(t.Id, "public", "We are on it.")).Expect(HttpStatusCode.Created);
        var message = reply.Json.GetProperty("message");
        Assert.Equal("Public", message.Str("kind"));
        Assert.Equal("Agent", message.Str("authorType"));
        Assert.Equal("NotDelivered", message.Str("deliveryState"));
        Assert.Equal("web", message.Str("source"));
        Assert.Equal(d.Agent.Id, message.GetProperty("author").GetProperty("id").GetGuid());
        Assert.Equal(t.Id, reply.Json.GetProperty("ticket").GetProperty("id").GetGuid());
        Assert.NotEqual(t.Version, reply.Json.GetProperty("ticket").Str("version"));

        var note = (await d.Agent.PostMessageAsync(t.Id, "Internal", "Looks like the fuser.")).Expect(HttpStatusCode.Created);
        Assert.Equal("None", note.Json.GetProperty("message").Str("deliveryState"));
        Assert.Equal("Internal", note.Json.GetProperty("message").Str("kind"));

        var rows = (await fixture.AuditOfAsync(t.Id)).Where(r => r.Action == "ticket.message_added").ToList();
        Assert.Equal(3, rows.Count); // first message + reply + note
        Assert.Contains("\"Internal\"", rows[2].Next, StringComparison.Ordinal);
        Assert.DoesNotContain("body", rows[2].Next, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Message_bodies_never_reach_the_audit_stream()
    {
        var d = await fixture.NewDeptAsync();
        var t = await d.Agent.CreateTicketAsync(d.Department);
        var secret = $"body-{Unique.Id()}";
        (await d.Agent.PostMessageAsync(t.Id, "Public", secret)).Expect(HttpStatusCode.Created);
        (await d.Agent.PostMessageAsync(t.Id, "Internal", secret)).Expect(HttpStatusCode.Created);

        Assert.Equal(0, await fixture.ScalarAsync<long>("SELECT count(*) FROM audit.audit_events WHERE previous::text LIKE @s OR next::text LIKE @s", TicketKit.P("s", $"%{secret}%")));
        var activity = (await d.Agent.Client.GetAsync(TicketKit.Url(t.Id, "/activity"))).Expect(HttpStatusCode.OK);
        Assert.DoesNotContain(secret, activity.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Post_requires_the_permission_of_the_kind_and_validates_input()
    {
        var d = await fixture.NewDeptAsync();
        var other = await fixture.NewDeptAsync();
        var t = await d.Agent.CreateTicketAsync(d.Department);

        (await d.Viewer.PostMessageAsync(t.Id, "Public", "x")).Expect(HttpStatusCode.Forbidden);
        (await d.Viewer.PostMessageAsync(t.Id, "Internal", "x")).Expect(HttpStatusCode.Forbidden);
        (await other.Agent.PostMessageAsync(t.Id, "Public", "x")).Expect(HttpStatusCode.NotFound);
        (await d.Agent.PostMessageAsync(t.Id, "Requester", "x")).Expect(HttpStatusCode.BadRequest);
        (await d.Agent.PostMessageAsync(t.Id, "Public", "  ")).Expect(HttpStatusCode.BadRequest);
        (await d.Agent.PostMessageAsync(t.Id, "Public", new string('b', 20001))).Expect(HttpStatusCode.BadRequest);
        (await d.Agent.PostMessageAsync(t.Id, "Public", new string('b', 20000))).Expect(HttpStatusCode.Created);
        (await d.Agent.PostMessageAsync(t.Id, "Public", "x", status: "Nope")).Expect(HttpStatusCode.BadRequest);
        (await d.Agent.Client.PostAsync(TicketKit.Url(t.Id, "/messages"), new { body = "no kind" })).Expect(HttpStatusCode.BadRequest);
        Assert.Equal(1, await fixture.ScalarAsync<long>("SELECT count(*) FROM ticketing.messages WHERE ticket_id = @t AND author_type = 'Agent'", TicketKit.P("t", t.Id)));
    }

    [Fact]
    public async Task A_status_can_ride_along_with_a_message_in_one_transaction_and_is_audited()
    {
        var d = await fixture.NewDeptAsync();
        var t = await d.Agent.CreateTicketAsync(d.Department);

        var posted = (await d.Agent.PostMessageAsync(t.Id, "Public", "Fixed, please confirm.", status: "resolved")).Expect(HttpStatusCode.Created);
        var ticket = posted.Json.GetProperty("ticket");
        Assert.Equal("Resolved", ticket.Str("status"));
        Assert.NotEqual(JsonValueKind.Null, ticket.GetProperty("resolvedAt").ValueKind);
        Assert.Contains((await fixture.AuditOfAsync(t.Id)), r => r.Action == "ticket.status_changed" && r.Next == """{"status": "Resolved"}""");

        // Same status again changes nothing and writes no status event.
        var before = (await fixture.AuditOfAsync(t.Id)).Count(r => r.Action == "ticket.status_changed");
        (await d.Agent.PostMessageAsync(t.Id, "Internal", "note", status: "Resolved")).Expect(HttpStatusCode.Created);
        Assert.Equal(before, (await fixture.AuditOfAsync(t.Id)).Count(r => r.Action == "ticket.status_changed"));
    }

    [Fact]
    public async Task Public_reply_on_a_closed_ticket_is_refused_but_internal_notes_are_allowed()
    {
        var d = await fixture.NewDeptAsync();
        var t = await d.Agent.CreateTicketAsync(d.Department);
        var closed = (await d.Agent.PatchTicketAsync(t, new { Status = "Closed" })).Expect(HttpStatusCode.OK);

        (await d.Agent.PostMessageAsync(t.Id, "Public", "too late")).ExpectCode(HttpStatusCode.Conflict, "invalid_state");
        (await d.Agent.PostMessageAsync(t.Id, "Public", "too late", status: "Open")).ExpectCode(HttpStatusCode.Conflict, "invalid_state");
        Assert.Equal(1, await fixture.ScalarAsync<long>("SELECT count(*) FROM ticketing.messages WHERE ticket_id = @t", TicketKit.P("t", t.Id)));
        (await d.Agent.PostMessageAsync(t.Id, "Internal", "post-mortem")).Expect(HttpStatusCode.Created);

        var reopened = (await d.Agent.PatchTicketAsync((await d.Agent.GetTicketAsync(t.Id)), new { Status = "Open" })).Expect(HttpStatusCode.OK);
        (await d.Agent.PostMessageAsync(t.Id, "Public", "now fine")).Expect(HttpStatusCode.Created);
        _ = closed;
        _ = reopened;
    }

    [Fact]
    public async Task Messages_are_listed_newest_first_with_authors_and_a_cursor()
    {
        var d = await fixture.NewDeptAsync();
        var t = await d.Agent.CreateTicketAsync(d.Department);
        for (var i = 1; i <= 5; i++)
        {
            (await d.Agent.PostMessageAsync(t.Id, i % 2 == 0 ? "Internal" : "Public", $"m{i}")).Expect(HttpStatusCode.Created);
        }

        var all = (await d.Viewer.Client.GetAsync(TicketKit.Url(t.Id, "/messages"))).Expect(HttpStatusCode.OK);
        var items = all.Json.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(["m5", "m4", "m3", "m2", "m1", "Something is broken."], items.Select(i => i.Str("body")));
        Assert.Equal(d.Agent.Id, items[0].GetProperty("author").GetProperty("id").GetGuid());
        Assert.Null(all.NextCursor());

        var seen = new List<string>();
        string? cursor = null;
        do
        {
            var page = (await d.Agent.Client.GetAsync(TicketKit.Url(t.Id, "/messages?limit=2" + (cursor is null ? "" : $"&cursor={cursor}")))).Expect(HttpStatusCode.OK);
            seen.AddRange(page.Json.GetProperty("items").EnumerateArray().Select(i => i.Str("body")));
            cursor = page.NextCursor();
        }
        while (cursor is not null);
        Assert.Equal(items.Select(i => i.Str("body")), seen);

        (await d.Agent.Client.GetAsync(TicketKit.Url(t.Id, "/messages?limit=0"))).Expect(HttpStatusCode.BadRequest);
        (await d.Agent.Client.GetAsync(TicketKit.Url(t.Id, "/messages?limit=201"))).Expect(HttpStatusCode.BadRequest);
        (await d.Agent.Client.GetAsync(TicketKit.Url(t.Id, "/messages?cursor=garbage"))).Expect(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Internal_notes_are_excluded_in_the_query_for_callers_without_notes_read()
    {
        // Every system role holds tickets.notes.read, so a restricted authorizer stands in for a role without it.
        var d = await fixture.NewDeptAsync();
        var t = await d.Agent.CreateTicketAsync(d.Department);
        (await d.Agent.PostMessageAsync(t.Id, "Public", "visible")).Expect(HttpStatusCode.Created);
        (await d.Agent.PostMessageAsync(t.Id, "Internal", "hidden")).Expect(HttpStatusCode.Created);

        await using var restricted = fixture.Factory.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
        {
            var original = services.Single(s => s.ServiceType == typeof(IAuthorizer));
            services.AddScoped(original.ImplementationType!);
            services.AddScoped<IAuthorizer>(sp => new NotesBlockedAuthorizer((IAuthorizer)sp.GetRequiredService(original.ImplementationType!)));
        }));
        var viewer = await fixture.SignInAsync(d.Viewer.Subject, factory: restricted);

        var limited = (await viewer.Client.GetAsync(TicketKit.Url(t.Id, "/messages"))).Expect(HttpStatusCode.OK);
        Assert.Equal(["visible", "Something is broken."], limited.Json.GetProperty("items").EnumerateArray().Select(i => i.Str("body")));
        Assert.DoesNotContain("hidden", limited.Body, StringComparison.Ordinal);

        var full = (await d.Viewer.Client.GetAsync(TicketKit.Url(t.Id, "/messages"))).Expect(HttpStatusCode.OK);
        Assert.Contains("hidden", full.Body, StringComparison.Ordinal);
    }

    private sealed class NotesBlockedAuthorizer(IAuthorizer inner) : IAuthorizer
    {
        public Task<AccessOutcome> CheckAsync(string permission, Guid? departmentId, CancellationToken cancellationToken = default) =>
            permission == PermissionCodes.TicketsNotesRead ? Task.FromResult(AccessOutcome.Forbidden) : inner.CheckAsync(permission, departmentId, cancellationToken);

        public Task RequireAsync(string permission, Guid? departmentId, CancellationToken cancellationToken = default) =>
            inner.RequireAsync(permission, departmentId, cancellationToken);

        public Task<bool> HasInAnyDepartmentAsync(string permission, CancellationToken cancellationToken = default) =>
            inner.HasInAnyDepartmentAsync(permission, cancellationToken);

        public Task<IReadOnlyCollection<Guid>> AccessibleDepartmentsAsync(string permission, CancellationToken cancellationToken = default) =>
            inner.AccessibleDepartmentsAsync(permission, cancellationToken);
    }

    [Fact]
    public async Task Message_check_constraints_hold_in_the_database()
    {
        var d = await fixture.NewDeptAsync();
        var t = await d.Agent.CreateTicketAsync(d.Department);
        var requesterId = t.Json.GetProperty("requester").GetProperty("id").GetGuid();

        async Task Rejected(string kind, string authorType, string? user, string? requester, string delivery, string constraint, string body = "x", string source = "web")
        {
            var sql =
                "INSERT INTO ticketing.messages (id, ticket_id, department_id, kind, author_type, author_user_id, author_requester_id, body, source, delivery_state, created_at) " +
                "VALUES (gen_random_uuid(), @t, @d, @kind, @type, @u, @r, @body, @source, @delivery, now())";
            var ex = await Assert.ThrowsAsync<PostgresException>(() => fixture.ExecuteAsync(
                sql,
                TicketKit.P("t", t.Id), TicketKit.P("d", d.Id), TicketKit.P("kind", kind), TicketKit.P("type", authorType),
                new NpgsqlParameter("u", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = user is null ? DBNull.Value : Guid.Parse(user) },
                new NpgsqlParameter("r", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = requester is null ? DBNull.Value : Guid.Parse(requester) },
                TicketKit.P("body", body), TicketKit.P("source", source), TicketKit.P("delivery", delivery)));
            Assert.Equal(PostgresErrorCodes.CheckViolation, ex.SqlState);
            Assert.Equal(constraint, ex.ConstraintName);
        }

        var agent = d.Agent.Id.ToString();
        var req = requesterId.ToString();
        await Rejected("Internal", "Requester", null, req, "None", "ck_messages_internal_never_requester_or_delivered");
        await Rejected("Internal", "Agent", agent, null, "NotDelivered", "ck_messages_delivery_only_agent_public");
        await Rejected("Public", "Agent", null, null, "NotDelivered", "ck_messages_author_matches_type");
        await Rejected("Public", "Requester", agent, null, "None", "ck_messages_author_matches_type");
        await Rejected("Public", "System", agent, null, "None", "ck_messages_author_matches_type");
        await Rejected("Secret", "Agent", agent, null, "None", "ck_messages_kind");
        await Rejected("Public", "Agent", agent, null, "Bounced", "ck_messages_delivery_state");
        await Rejected("Public", "Agent", agent, null, "None", "ck_messages_source", source: "sms");
        // Only an agent's public reply has a delivery state (Pending/Sent/Failed come from the mail outbox).
        await Rejected("Public", "Requester", null, req, "Pending", "ck_messages_delivery_only_agent_public");
        await Rejected("Public", "System", null, null, "Sent", "ck_messages_delivery_only_agent_public");
        await Rejected("Internal", "Agent", agent, null, "Failed", "ck_messages_delivery_only_agent_public");
        await Rejected("Public", "Agent", agent, null, "None", "ck_messages_body", body: "   ");
        await Rejected("Public", "Agent", agent, null, "None", "ck_messages_body", body: new string('x', 20001));

        // Controls: the allowed shapes are accepted.
        Assert.Equal(1, await fixture.ExecuteAsync(
            "INSERT INTO ticketing.messages (id, ticket_id, department_id, kind, author_type, author_user_id, body, source, delivery_state, created_at) VALUES (gen_random_uuid(), @t, @d, 'Internal', 'Agent', @u, 'ok', 'web', 'None', now())",
            TicketKit.P("t", t.Id), TicketKit.P("d", d.Id), TicketKit.P("u", d.Agent.Id)));
        Assert.Equal(1, await fixture.ExecuteAsync(
            "INSERT INTO ticketing.messages (id, ticket_id, department_id, kind, author_type, body, source, delivery_state, created_at) VALUES (gen_random_uuid(), @t, @d, 'Public', 'System', 'ok', 'web', 'None', now())",
            TicketKit.P("t", t.Id), TicketKit.P("d", d.Id)));
    }

    // ----- activity -----

    [Fact]
    public async Task Activity_lists_ticket_events_newest_first_with_actor_keys_and_no_bodies()
    {
        var d = await fixture.NewDeptAsync();
        var t = await d.Agent.CreateTicketAsync(d.Department);
        var otherTicket = await d.Agent.CreateTicketAsync(d.Department);
        var patched = (await d.Lead.PatchTicketAsync(t, new { Priority = "High" })).Expect(HttpStatusCode.OK);
        (await d.Lead.AssignAsync(t.Id, patched.Version, null, d.Agent.Id)).Expect(HttpStatusCode.OK);
        (await d.Agent.PostMessageAsync(t.Id, "Public", "hello")).Expect(HttpStatusCode.Created);
        (await d.Agent.PatchTicketAsync(otherTicket, new { Priority = "Low" })).Expect(HttpStatusCode.OK);

        var page = (await d.Viewer.Client.GetAsync(TicketKit.Url(t.Id, "/activity"))).Expect(HttpStatusCode.OK);
        var items = page.Json.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(
            ["ticket.message_added", "ticket.assignee_changed", "ticket.priority_changed", "ticket.message_added", "ticket.created"],
            items.Select(i => i.Str("action")));
        var priority = items[2];
        Assert.Equal(d.Lead.Id, priority.GetProperty("actor").GetProperty("id").GetGuid());
        Assert.Equal("High", priority.GetProperty("next").Str("priority"));
        Assert.Equal("Normal", priority.GetProperty("previous").Str("priority"));
        Assert.Equal(JsonValueKind.Null, priority.GetProperty("reason").ValueKind);
        Assert.DoesNotContain("hello", page.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(otherTicket.Id.ToString(), page.Body, StringComparison.Ordinal);
        Assert.Equal(["action", "actor", "id", "next", "occurredAt", "previous", "reason"], items[0].EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));

        // Paging and bounds.
        var first = (await d.Viewer.Client.GetAsync(TicketKit.Url(t.Id, "/activity?limit=2"))).Expect(HttpStatusCode.OK);
        Assert.Equal(2, first.Json.GetProperty("items").GetArrayLength());
        var second = (await d.Viewer.Client.GetAsync(TicketKit.Url(t.Id, $"/activity?limit=10&cursor={first.NextCursor()}"))).Expect(HttpStatusCode.OK);
        Assert.Equal(3, second.Json.GetProperty("items").GetArrayLength());
        Assert.Null(second.NextCursor());
        (await d.Viewer.Client.GetAsync(TicketKit.Url(t.Id, "/activity?limit=201"))).Expect(HttpStatusCode.BadRequest);
        (await d.Viewer.Client.GetAsync(TicketKit.Url(t.Id, "/activity?cursor=x"))).Expect(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Activity_is_hidden_from_outsiders_and_ticket_events_stay_out_of_the_department_and_platform_audit()
    {
        var d = await fixture.NewDeptAsync();
        var other = await fixture.NewDeptAsync();
        var t = await d.Agent.CreateTicketAsync(d.Department);

        (await other.Agent.Client.GetAsync(TicketKit.Url(t.Id, "/activity"))).Expect(HttpStatusCode.NotFound);
        var deptAudit = (await d.Lead.Client.GetAsync($"{d.Department.Url}/audit?limit=200")).Expect(HttpStatusCode.OK);
        Assert.DoesNotContain("ticket.", deptAudit.Body, StringComparison.Ordinal);
        var platformAudit = (await (await fixture.PlatformAdminAsync()).Client.GetAsync("/api/platform/audit?limit=200")).Expect(HttpStatusCode.OK);
        Assert.DoesNotContain(t.Id.ToString(), platformAudit.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tickets_are_reachable_by_reference_case_insensitively()
    {
        var d = await fixture.NewDeptAsync();
        var t = await d.Agent.CreateTicketAsync(d.Department);
        var reference = t.Json.Str("reference");

        Assert.Equal(t.Id, (await d.Viewer.Client.GetAsync($"/api/tickets/{reference.ToLowerInvariant()}")).Expect(HttpStatusCode.OK).Id);
        Assert.NotEmpty((await d.Viewer.Client.GetAsync($"/api/tickets/{reference}/messages")).Expect(HttpStatusCode.OK).Json.GetProperty("items").EnumerateArray());
        (await d.Viewer.Client.GetAsync($"/api/tickets/{d.Key}-999")).Expect(HttpStatusCode.NotFound);
        (await d.Viewer.Client.GetAsync("/api/tickets/not-a-ticket")).Expect(HttpStatusCode.NotFound);
        (await d.Viewer.Client.GetAsync("/api/tickets/IT-1;DROP")).Expect(HttpStatusCode.NotFound);
        var other = await fixture.NewDeptAsync();
        (await other.Agent.Client.GetAsync($"/api/tickets/{reference}")).Expect(HttpStatusCode.NotFound);
    }
}
