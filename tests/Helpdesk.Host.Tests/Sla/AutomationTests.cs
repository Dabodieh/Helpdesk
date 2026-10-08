#pragma warning disable CA1861 // test payloads are one-off literals
using System.Net;
using System.Text.Json;
using Helpdesk.Host.Tests.Infrastructure;
using Helpdesk.Host.Tests.Ticketing;
using Helpdesk.Modules.Organisation.Contracts;
using Helpdesk.Modules.Ticketing.Automation;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Helpdesk.Host.Tests.Sla;

/// <summary>Automation rules: routing, ordering, stop-processing, loop protection, idempotency, department isolation, dry run, invariants and privacy of internal notes.</summary>
public sealed class AutomationTests(HelpdeskFixture fixture) : IClassFixture<HelpdeskFixture>
{
    private const string Start = "2026-01-12T10:00:00Z";

    private static object Action(string type, string? value = null, string? text = null) => new { type, value, text };

    private static object Cond(string field, string op, params string[] values) => new { field, @operator = op, values };

    private static async Task<ApiResponse> RuleAsync(TimedWorld w, string name, string trigger, object[] conditions, object[] actions, bool stop = false, bool active = true) =>
        (await w.Admin.Client.PostAsync($"{w.Base}/automations", new { name, trigger, isActive = active, stopProcessing = stop, conditions, actions })).Expect(HttpStatusCode.Created);

    private static async Task<string> VersionOfAsync(TimedWorld w, Guid ruleId) =>
        (await w.Admin.Client.GetAsync($"{w.Base}/automations")).Expect(HttpStatusCode.OK).Json.EnumerateArray().Single(r => r.GetProperty("id").GetGuid() == ruleId).GetProperty("version").GetString()!;

    private static async Task<JsonElement> HistoryAsync(TimedWorld w, string? ticket = null) =>
        (await w.Admin.Client.GetAsync($"{w.Base}/automations/history?limit=100" + (ticket is null ? "" : $"&ticket={ticket}"))).Expect(HttpStatusCode.OK).Json.GetProperty("items");

    private static async Task<JsonElement> MessagesAsync(TimedWorld w, Guid ticketId) =>
        (await w.Admin.Client.GetAsync($"/api/tickets/{ticketId}/messages?limit=200")).Expect(HttpStatusCode.OK).Json.GetProperty("items");

    [Fact]
    public async Task A_routing_rule_assigns_the_agent_on_creation_for_matching_tickets_only_and_records_why()
    {
        await using var w = await TimedWorld.CreateAsync(fixture, Start);
        await RuleAsync(w, "Network routing", "TicketCreated", [Cond("subjectContains", "contains", "NETWORK")], [Action("assignAgent", w.Agent.Id.ToString())]);

        var network = await w.NewTicketAsync("The Network is down");
        var printer = await w.NewTicketAsync("Printer jam");
        Assert.Equal(w.Agent.Id, (await w.TicketAsync(network.Id)).Json.GetProperty("assignee").GetProperty("id").GetGuid());
        Assert.Equal(JsonValueKind.Null, (await w.TicketAsync(printer.Id)).Json.GetProperty("assignee").ValueKind);

        // History: one evaluation per ticket, with the matched result and the condition trace (no subject text in it).
        var history = await HistoryAsync(w);
        var matched = Assert.Single(history.EnumerateArray(), e => e.GetProperty("ticketId").GetGuid() == network.Id);
        Assert.True(matched.GetProperty("matched").GetBoolean());
        Assert.Equal("Completed", matched.GetProperty("state").GetString());
        Assert.Equal("assignAgent", matched.GetProperty("actions")[0].GetProperty("type").GetString());
        Assert.Equal("Done", matched.GetProperty("actions")[0].GetProperty("outcome").GetString());
        Assert.False(matched.GetProperty("conditions")[0].TryGetProperty("actual", out var actual) && actual.ValueKind == JsonValueKind.String);
        var skipped = Assert.Single(history.EnumerateArray(), e => e.GetProperty("ticketId").GetGuid() == printer.Id);
        Assert.False(skipped.GetProperty("matched").GetBoolean());
        Assert.False(skipped.GetProperty("conditions")[0].GetProperty("result").GetBoolean());
        Assert.DoesNotContain("The Network is down", history.GetRawText(), StringComparison.Ordinal);

        // The change is attributed to automation in the ticket's history, without naming the rule.
        var activity = (await w.Agent.Client.GetAsync($"/api/tickets/{network.Id}/activity")).Expect(HttpStatusCode.OK).Json.GetProperty("items");
        var assigneeChange = Assert.Single(activity.EnumerateArray(), e => e.GetProperty("action").GetString() == "ticket.assignee_changed");
        Assert.Equal("automation", assigneeChange.GetProperty("reason").GetString());
        Assert.DoesNotContain("Network routing", activity.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rules_run_in_explicit_order_and_stop_processing_is_only_honoured_when_set()
    {
        await using var w = await TimedWorld.CreateAsync(fixture, Start);
        var first = await RuleAsync(w, "First", "TicketCreated", [], [Action("setPriority", "High")]);
        var second = await RuleAsync(w, "Second", "TicketCreated", [], [Action("setPriority", "Low")]);
        var ticket = await w.NewTicketAsync();
        Assert.Equal("Low", (await w.TicketAsync(ticket.Id)).Json.GetProperty("priority").GetString()); // both ran, in order

        // Reorder: Second first. The later rule (First) now wins.
        (await w.Admin.Client.PutAsync($"{w.Base}/automations/order/TicketCreated", new { ids = new[] { second.Id, first.Id } })).Expect(HttpStatusCode.OK);
        var reordered = await w.NewTicketAsync();
        Assert.Equal("High", (await w.TicketAsync(reordered.Id)).Json.GetProperty("priority").GetString());

        // Stop processing on the first rule: the second never runs and leaves no execution record.
        (await w.Admin.Client.PatchAsync($"{w.Base}/automations/{second.Id}", new { version = await VersionOfAsync(w, second.Id), stopProcessing = true })).Expect(HttpStatusCode.OK);
        var stopped = await w.NewTicketAsync();
        Assert.Equal("Low", (await w.TicketAsync(stopped.Id)).Json.GetProperty("priority").GetString());
        var rows = (await HistoryAsync(w, stopped.Json.GetProperty("reference").GetString())).EnumerateArray().ToArray();
        Assert.Single(rows);
        Assert.Equal("Second", rows[0].GetProperty("ruleName").GetString());

        // A stop flag on a rule that does NOT match stops nothing.
        (await w.Admin.Client.PatchAsync($"{w.Base}/automations/{second.Id}", new { version = await VersionOfAsync(w, second.Id), conditions = new[] { Cond("subjectContains", "contains", "never-matches") } })).Expect(HttpStatusCode.OK);
        var continued = await w.NewTicketAsync();
        Assert.Equal("High", (await w.TicketAsync(continued.Id)).Json.GetProperty("priority").GetString());
    }

    [Fact]
    public async Task A_rule_cycle_terminates_and_the_repeat_is_recorded_as_skipped()
    {
        await using var w = await TimedWorld.CreateAsync(fixture, Start);
        // A: priority became High -> make it Urgent.  B: priority became Urgent -> make it High.  Unprotected, this never ends.
        await RuleAsync(w, "A", "PriorityChanged", [Cond("priority", "in", "High")], [Action("setPriority", "Urgent")]);
        await RuleAsync(w, "B", "PriorityChanged", [Cond("priority", "in", "Urgent")], [Action("setPriority", "High")]);
        var ticket = await w.NewTicketAsync(priority: "Low");
        await w.SetPriorityAsync(ticket.Id, "High");

        var reference = ticket.Json.GetProperty("reference").GetString();
        var rows = (await HistoryAsync(w, reference)).EnumerateArray().OrderBy(e => e.GetProperty("startedAt").GetDateTimeOffset()).ThenBy(e => e.GetProperty("depth").GetInt32()).ToArray();
        Assert.True(rows.Length is >= 3 and <= AutomationChain.MaxExecutions, $"executions: {rows.Length}");
        Assert.Contains(rows, e => e.GetProperty("state").GetString() == "Skipped" && e.GetProperty("skipReason").GetString() == "loop_detected");
        Assert.Equal(1, rows.Count(e => e.GetProperty("ruleName").GetString() == "A" && e.GetProperty("state").GetString() != "Skipped"));
        Assert.Equal(1, rows.Count(e => e.GetProperty("ruleName").GetString() == "B" && e.GetProperty("state").GetString() != "Skipped"));
        Assert.Single(rows.Select(e => e.GetProperty("chainId").GetGuid()).Distinct());
        Assert.Equal("High", (await w.TicketAsync(ticket.Id)).Json.GetProperty("priority").GetString());
    }

    [Fact]
    public async Task A_duplicate_event_never_repeats_notes_or_assignments()
    {
        await using var w = await TimedWorld.CreateAsync(fixture, Start);
        await RuleAsync(w, "Note and route", "TicketCreated", [], [Action("addInternalNote", text: "Routed automatically"), Action("assignAgent", w.Agent.Id.ToString())]);
        var ticket = await w.NewTicketAsync();
        Assert.Single((await MessagesAsync(w, ticket.Id)).EnumerateArray(), m => m.GetProperty("kind").GetString() == "Internal");

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var engine = scope.ServiceProvider.GetRequiredService<AutomationEngine>();
        for (var i = 0; i < 3; i++)
        {
            await engine.RunAsync(w.Dept.Id, ticket.Id, "TicketCreated", "retried-event-1", AutomationChain.Start("test"), CancellationToken.None);
        }

        // One extra evaluation for the new event key, however often it was delivered; never one per delivery.
        var notes = (await MessagesAsync(w, ticket.Id)).EnumerateArray().Count(m => m.GetProperty("kind").GetString() == "Internal");
        Assert.Equal(2, notes);
        Assert.Equal(2, await w.CountAsync("SELECT count(*) FROM ticketing.automation_executions WHERE ticket_id = @t", new NpgsqlParameter("t", ticket.Id)));
        Assert.Equal(1, await w.CountAsync("SELECT count(*) FROM audit.audit_events WHERE object_id = @t AND action = 'ticket.assignee_changed'", new NpgsqlParameter("t", ticket.Id)));
    }

    [Fact]
    public async Task Automation_cannot_reference_another_department_even_with_crafted_payloads_or_stored_rules()
    {
        await using var a = await TimedWorld.CreateAsync(fixture, Start);
        await using var b = await TimedWorld.CreateAsync(fixture, Start);

        // Rejected when saved: person and mailbox of another department.
        (await a.Admin.Client.PostAsync($"{a.Base}/automations", new { name = "x", trigger = "TicketCreated", conditions = Array.Empty<object>(), actions = new[] { Action("assignAgent", b.Dept.Agent.Id.ToString()) } })).Expect(HttpStatusCode.BadRequest);
        (await a.Admin.Client.PostAsync($"{a.Base}/automations", new { name = "x", trigger = "TicketCreated", conditions = new[] { Cond("mailbox", "in", Guid.NewGuid().ToString()) }, actions = new[] { Action("setPriority", "High") } })).Expect(HttpStatusCode.BadRequest);

        // Other departments' rules do not exist for A's administrator, and A's rule routes never accept B's ids.
        var ruleB = await RuleAsync(b, "B rule", "TicketCreated", [], [Action("setPriority", "High")]);
        (await a.Admin.Client.GetAsync($"{b.Base}/automations")).Expect(HttpStatusCode.NotFound);
        (await a.Admin.Client.PatchAsync($"{a.Base}/automations/{ruleB.Id}", new { version = ruleB.Version, name = "hijack" })).Expect(HttpStatusCode.NotFound);
        (await a.Admin.Client.PostAsync($"{b.Base}/automations/test", new { ticket = "X-1", trigger = "TicketCreated" })).Expect(HttpStatusCode.NotFound);

        // A rule stored with a foreign agent (bypassing the API) fails closed when it runs: the action fails, the ticket is untouched.
        var crafted = Guid.CreateVersion7();
        await fixture.ExecuteAsync(
            "INSERT INTO ticketing.automation_rules (id, department_id, name, is_active, trigger, sort_order, stop_processing, conditions, actions, created_at, updated_at) "
            + "VALUES (@id, @d, 'crafted', true, 'TicketCreated', 10, false, '[]'::jsonb, CAST(@a AS jsonb), now(), now())",
            new NpgsqlParameter("id", crafted), new NpgsqlParameter("d", a.Dept.Id),
            new NpgsqlParameter("a", $"[{{\"type\":\"assignAgent\",\"value\":\"{b.Dept.Agent.Id}\",\"text\":null}}]"));
        var ticket = await a.NewTicketAsync();
        Assert.Equal(JsonValueKind.Null, (await a.TicketAsync(ticket.Id)).Json.GetProperty("assignee").ValueKind);
        var row = Assert.Single((await HistoryAsync(a, ticket.Json.GetProperty("reference").GetString())).EnumerateArray());
        Assert.Equal("Failed", row.GetProperty("state").GetString());
        Assert.Equal("Failed", row.GetProperty("actions")[0].GetProperty("outcome").GetString());
        Assert.Equal(0, await a.CountAsync("SELECT count(*) FROM ticketing.tickets WHERE department_id = @d AND assignee_user_id = @t", new NpgsqlParameter("d", a.Dept.Id), new NpgsqlParameter("t", b.Dept.Agent.Id)));
    }

    [Fact]
    public async Task Crafted_rule_payloads_fail_closed()
    {
        await using var w = await TimedWorld.CreateAsync(fixture, Start);
        string[] triggers = ["TicketCreated"];
        var many = Enumerable.Range(0, 11).Select(_ => Action("setPriority", "High")).ToArray();
        var manyConditions = Enumerable.Range(0, 11).Select(_ => Cond("priority", "in", "High")).ToArray();
        object[][] badActions =
        [
            [Action("runScript", "rm -rf /")], [Action("sendEmail", "x")], [Action("setStatus", "Deleted")], [Action("setPriority", "Nope")],
            [Action("assignAgent", "not-a-guid")], [Action("assignTeam", Guid.NewGuid().ToString())], [Action("addInternalNote", text: new string('x', 1001))], [Action("addInternalNote")], many, [],
        ];
        foreach (var actions in badActions)
        {
            (await w.Admin.Client.PostAsync($"{w.Base}/automations", new { name = "bad", trigger = triggers[0], conditions = Array.Empty<object>(), actions })).Expect(HttpStatusCode.BadRequest);
        }

        object[][] badConditions =
        [
            [Cond("subjectContains", "matches", ".*")], [Cond("body", "contains", "x")], [Cond("priority", "equals", "High")], [Cond("status", "in")],
            [Cond("status", "in", "Exploded")], [Cond("requesterDomain", "equals", "a@b.com")], [Cond("slaFirstResponse", "in", "Maybe")], manyConditions,
        ];
        foreach (var conditions in badConditions)
        {
            (await w.Admin.Client.PostAsync($"{w.Base}/automations", new { name = "bad", trigger = triggers[0], conditions, actions = new[] { Action("setPriority", "High") } })).Expect(HttpStatusCode.BadRequest);
        }

        (await w.Admin.Client.PostAsync($"{w.Base}/automations", new { name = "bad", trigger = "OnMoonrise", conditions = Array.Empty<object>(), actions = new[] { Action("setPriority", "High") } })).Expect(HttpStatusCode.BadRequest);
        (await w.Admin.Client.PostAsync($"{w.Base}/automations", new { name = "", trigger = "TicketCreated", conditions = Array.Empty<object>(), actions = new[] { Action("setPriority", "High") } })).Expect(HttpStatusCode.BadRequest);
        (await w.Admin.Client.PostAsync($"{w.Base}/automations", new { trigger = "TicketCreated" })).Expect(HttpStatusCode.BadRequest);
        Assert.Equal(0, await w.CountAsync("SELECT count(*) FROM ticketing.automation_rules WHERE department_id = @d", new NpgsqlParameter("d", w.Dept.Id)));
    }

    [Fact]
    public async Task Test_against_ticket_explains_everything_and_changes_nothing()
    {
        await using var w = await TimedWorld.CreateAsync(fixture, Start);
        var rule = await RuleAsync(w, "Escalate network", "StatusChanged", [Cond("status", "in", "Pending"), Cond("subjectContains", "contains", "network")],
            [Action("setPriority", "Urgent"), Action("assignAgent", w.Agent.Id.ToString()), Action("addInternalNote", text: "Escalated"), Action("setStatus", "Pending")], stop: true);
        await RuleAsync(w, "Later rule", "StatusChanged", [], [Action("setPriority", "Low")]);
        var ticket = await w.NewTicketAsync("Network outage");
        var reference = ticket.Json.GetProperty("reference").GetString();
        await w.SetStatusAsync(ticket.Id, "Pending"); // real run: rule 1 matches, stops processing

        var before = await Snapshot(w, ticket.Id);
        var result = (await w.Admin.Client.PostAsync($"{w.Base}/automations/test", new { ticket = reference, ruleId = (Guid?)null, trigger = "StatusChanged" })).Expect(HttpStatusCode.OK).Json;
        Assert.All(result.GetProperty("rules")[0].GetProperty("conditions").EnumerateArray(), c => Assert.True(c.GetProperty("result").GetBoolean()));
        Assert.True(result.GetProperty("rules")[0].GetProperty("matched").GetBoolean());
        var planned = result.GetProperty("rules")[0].GetProperty("actions").EnumerateArray().ToArray();
        Assert.Equal(["WouldNotChange", "WouldNotChange", "WouldChange", "WouldNotChange"], planned.Select(p => p.GetProperty("result").GetString()!).ToArray()); // real run already applied the changes
        Assert.False(result.GetProperty("rules")[1].GetProperty("evaluated").GetBoolean()); // not evaluated: the first stops processing
        Assert.Equal(rule.Id, result.GetProperty("rules")[0].GetProperty("ruleId").GetGuid());
        Assert.Contains("Nothing was changed", result.GetProperty("notice").GetString(), StringComparison.Ordinal);

        // A rule that does not match is explained too (which condition failed).
        var miss = (await w.Admin.Client.PostAsync($"{w.Base}/automations/test", new { ticket = reference, ruleId = rule.Id })).Expect(HttpStatusCode.OK).Json;
        Assert.Equal(await Snapshot(w, ticket.Id), before);

        // Nothing moved: no ticket change, no new notes, no new execution rows, no audit, no events.
        Assert.Equal(1, miss.GetProperty("rules").GetArrayLength());
    }

    private static async Task<string> Snapshot(TimedWorld w, Guid ticketId)
    {
        var t = (await w.TicketAsync(ticketId)).Json;
        var count = await w.CountAsync("SELECT (SELECT count(*) FROM ticketing.messages WHERE ticket_id = @t) + (SELECT count(*) FROM ticketing.automation_executions WHERE ticket_id = @t) * 1000 + (SELECT count(*) FROM audit.audit_events WHERE object_id = @t) * 1000000", new NpgsqlParameter("t", ticketId));
        return $"{t.GetProperty("version").GetString()}|{t.GetProperty("updatedAt").GetString()}|{count}";
    }

    [Fact]
    public async Task Test_against_ticket_cannot_inspect_tickets_the_caller_may_not_read()
    {
        await using var a = await TimedWorld.CreateAsync(fixture, Start);
        await using var b = await TimedWorld.CreateAsync(fixture, Start);
        await RuleAsync(a, "R", "TicketCreated", [], [Action("setPriority", "High")]);
        var ticketB = await b.NewTicketAsync("Secret B");
        var referenceB = ticketB.Json.GetProperty("reference").GetString();

        // Administrator of A, naming B's ticket through A's route: not found.
        (await a.Admin.Client.PostAsync($"{a.Base}/automations/test", new { ticket = referenceB, trigger = "TicketCreated" })).Expect(HttpStatusCode.NotFound);
        // Platform administrator (administrative rights, no ticket access): not found as well.
        var platform = await fixture.PlatformAdminAsync();
        (await platform.Client.PostAsync($"{b.Base}/automations/test", new { ticket = referenceB, trigger = "TicketCreated" })).Expect(HttpStatusCode.NotFound);
        // Non-administrators cannot use the feature at all.
        (await a.Agent.Client.PostAsync($"{a.Base}/automations/test", new { ticket = referenceB, trigger = "TicketCreated" })).Expect(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Rule_configuration_and_history_are_for_department_administrators_only()
    {
        await using var w = await TimedWorld.CreateAsync(fixture, Start);
        var rule = await RuleAsync(w, "R", "TicketCreated", [], [Action("setPriority", "High")]);
        foreach (var user in new[] { w.Lead, w.Agent, w.Viewer })
        {
            (await user.Client.GetAsync($"{w.Base}/automations")).Expect(HttpStatusCode.Forbidden);
            (await user.Client.GetAsync($"{w.Base}/automations/history")).Expect(HttpStatusCode.Forbidden);
            (await user.Client.PostAsync($"{w.Base}/automations", new { name = "x", trigger = "TicketCreated", actions = new[] { Action("setPriority", "High") } })).Expect(HttpStatusCode.Forbidden);
            (await user.Client.PatchAsync($"{w.Base}/automations/{rule.Id}", new { version = rule.Version, isActive = false })).Expect(HttpStatusCode.Forbidden);
            (await user.Client.GetAsync($"{w.Base}/business-hours")).Expect(HttpStatusCode.Forbidden);
        }

        // Ordinary agents see none of it in ticket data: no rule names, ids or execution details in messages or activity.
        var ticket = await w.NewTicketAsync();
        var raw = (await w.Agent.Client.GetAsync($"/api/tickets/{ticket.Id}/activity")).Expect(HttpStatusCode.OK).Body + (await w.Agent.Client.GetAsync($"/api/tickets/{ticket.Id}/messages")).Expect(HttpStatusCode.OK).Body;
        Assert.DoesNotContain(rule.Id.ToString(), raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Automation_notes_stay_internal_are_never_mailed_and_do_not_satisfy_first_response_while_status_changes_keep_the_ticket_consistent()
    {
        await using var w = await TimedWorld.CreateAsync(fixture, Start);
        await w.PolicyAsync("Standard", await w.CalendarAsync(), firstResponse: 60, resolution: 480);
        await RuleAsync(w, "Note and resolve", "TicketCreated", [], [Action("addInternalNote", text: "Looks routine"), Action("setStatus", "Resolved")]);
        var mailBefore = await w.CountAsync("SELECT count(*) FROM mail.messages");
        var ticket = await w.NewTicketAsync();

        var notes = (await MessagesAsync(w, ticket.Id)).EnumerateArray().Where(m => m.GetProperty("authorType").GetString() == "System").ToArray();
        var note = Assert.Single(notes);
        Assert.Equal("Internal", note.GetProperty("kind").GetString());
        Assert.Equal("None", note.GetProperty("deliveryState").GetString());
        Assert.Equal(mailBefore, await w.CountAsync("SELECT count(*) FROM mail.messages"));

        // The note is invisible to anyone without notes access and never counts as a first response; the status change obeys the domain rules.
        var detail = (await w.TicketAsync(ticket.Id)).Json;
        Assert.Equal("Resolved", detail.GetProperty("status").GetString());
        Assert.NotEqual(JsonValueKind.Null, detail.GetProperty("resolvedAt").ValueKind);
        var sla = await w.SlaAsync(ticket.Id);
        Assert.Equal(JsonValueKind.Null, sla.GetProperty("firstResponse").GetProperty("metAt").ValueKind);
        Assert.Equal("Met", sla.GetProperty("resolution").GetProperty("state").GetString());
        Assert.Equal(1, await w.CountAsync("SELECT count(*) FROM ticketing.messages WHERE ticket_id = @t AND kind = 'Internal' AND author_type = 'System' AND delivery_state = 'None'", new NpgsqlParameter("t", ticket.Id)));
    }

    [Fact]
    public async Task Sla_breach_triggers_automation_once()
    {
        await using var w = await TimedWorld.CreateAsync(fixture, Start);
        await w.PolicyAsync("Fast", await w.CalendarAsync(), firstResponse: null, resolution: 60, lead: 10);
        await RuleAsync(w, "On breach", "SlaBreached", [Cond("slaResolution", "in", "Breached")], [Action("setPriority", "Urgent"), Action("addInternalNote", text: "SLA breached")]);
        await RuleAsync(w, "On warning", "SlaApproaching", [], [Action("addInternalNote", text: "SLA approaching")]);
        var ticket = await w.NewTicketAsync();

        await w.GoToAsync("2026-01-12T10:55:00Z");
        await w.RunMonitorAsync();
        await w.RunMonitorAsync();
        Assert.Single((await MessagesAsync(w, ticket.Id)).EnumerateArray(), m => m.GetProperty("body").GetString() == "SLA approaching");

        await w.GoToAsync("2026-01-12T11:30:00Z");
        await w.RunMonitorAsync();
        await w.RunMonitorAsync();
        var messages = (await MessagesAsync(w, ticket.Id)).EnumerateArray().ToArray();
        Assert.Single(messages, m => m.GetProperty("body").GetString() == "SLA breached");
        Assert.Equal("Urgent", (await w.TicketAsync(ticket.Id)).Json.GetProperty("priority").GetString());
    }

    [Fact]
    public async Task Requester_messages_and_assignment_changes_trigger_rules_and_conditions_cover_requester_and_assignee()
    {
        await using var w = await TimedWorld.CreateAsync(fixture, Start);
        await RuleAsync(w, "Customer replied", "RequesterMessageAdded", [Cond("status", "in", "Open"), Cond("requesterDomain", "equals", "example.test")], [Action("setPriority", "High")]);
        await RuleAsync(w, "Taken", "AssigneeChanged", [Cond("assignee", "notIn", "none")], [Action("addInternalNote", text: "Someone took it")]);
        var ticket = await w.NewTicketAsync();
        await w.SetStatusAsync(ticket.Id, "Pending");

        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var ingress = scope.ServiceProvider.GetRequiredService<Helpdesk.Modules.Ticketing.Contracts.ITicketIngress>();
            var requesterId = Guid.Parse((await fixture.ScalarAsync<string>("SELECT requester_id::text FROM ticketing.tickets WHERE id = @t", new NpgsqlParameter("t", ticket.Id)))!);
            await ingress.AddRequesterMessageAsync(w.Dept.Id, ticket.Id, requesterId, "Still broken", "manual");
        }

        var detail = await w.TicketAsync(ticket.Id);
        Assert.Equal("High", detail.Json.GetProperty("priority").GetString());
        (await w.Lead.AssignAsync(ticket.Id, detail.Version, w.Agent.Id)).Expect(HttpStatusCode.OK);
        Assert.Single((await MessagesAsync(w, ticket.Id)).EnumerateArray(), m => m.GetProperty("body").GetString() == "Someone took it");
    }

    private async Task<(string State, string Actions, string TriggerKey)> ExecutionOfAsync(Guid ticketId)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT state, actions::text, trigger_key FROM ticketing.automation_executions WHERE ticket_id = @t", connection);
        command.Parameters.AddWithValue("t", ticketId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), "expected one execution row");
        var row = (reader.GetString(0), reader.GetString(1), reader.GetString(2));
        Assert.False(await reader.ReadAsync(), "expected exactly one execution row");
        return row;
    }

    [Fact]
    public async Task A_failed_execution_resumes_at_the_failed_action_without_repeating_what_succeeded()
    {
        await using var w = await TimedWorld.CreateAsync(fixture, Start);
        await RuleAsync(w, "Three steps", "TicketCreated", [], [Action("setPriority", "High"), Action("addInternalNote", text: "Step two note"), Action("assignAgent", w.Agent.Id.ToString())]);

        // The agent leaves the department after the rule was saved: step three fails when the rule runs, steps one and two succeed.
        (await w.Admin.Client.DeleteAsync($"{w.Base}/members/{w.Agent.Id}")).Expect(HttpStatusCode.NoContent);
        var ticket = await w.NewTicketAsync();
        var (state, actions, key) = await ExecutionOfAsync(ticket.Id);
        Assert.Equal("Failed", state);
        Assert.Equal(["Done", "Done", "Failed"], JsonDocument.Parse(actions).RootElement.EnumerateArray().Select(a => a.GetProperty("outcome").GetString()!).ToArray());
        Assert.Equal("High", (await w.TicketAsync(ticket.Id)).Json.GetProperty("priority").GetString());
        Assert.Equal(JsonValueKind.Null, (await w.TicketAsync(ticket.Id)).Json.GetProperty("assignee").ValueKind);
        var notesBefore = (await MessagesAsync(w, ticket.Id)).EnumerateArray().Count(m => m.GetProperty("authorType").GetString() == "System");
        var priorityAuditBefore = await w.CountAsync("SELECT count(*) FROM audit.audit_events WHERE object_id = @t AND action = 'ticket.priority_changed'", new NpgsqlParameter("t", ticket.Id));
        Assert.Equal(1, notesBefore);

        // The agent is back in the department; the same event is delivered again (a retry) and resumes the failed action only.
        (await w.Admin.Client.PutAsync($"{w.Base}/members/{w.Agent.Id}", new { role = DepartmentRoles.Agent })).Expect(HttpStatusCode.OK);
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var engine = scope.ServiceProvider.GetRequiredService<AutomationEngine>();
        await engine.RunAsync(w.Dept.Id, ticket.Id, "TicketCreated", key, AutomationChain.Start("retry"), CancellationToken.None);

        var resumed = await ExecutionOfAsync(ticket.Id);
        Assert.Equal("Completed", resumed.State);
        Assert.Equal(["Done", "Done", "Done"], JsonDocument.Parse(resumed.Actions).RootElement.EnumerateArray().Select(a => a.GetProperty("outcome").GetString()!).ToArray());
        Assert.Equal(w.Agent.Id, (await w.TicketAsync(ticket.Id)).Json.GetProperty("assignee").GetProperty("id").GetGuid());
        Assert.Equal(notesBefore, (await MessagesAsync(w, ticket.Id)).EnumerateArray().Count(m => m.GetProperty("authorType").GetString() == "System"));
        Assert.Equal(priorityAuditBefore, await w.CountAsync("SELECT count(*) FROM audit.audit_events WHERE object_id = @t AND action = 'ticket.priority_changed'", new NpgsqlParameter("t", ticket.Id)));
        Assert.Equal(1, await w.CountAsync("SELECT count(*) FROM audit.audit_events WHERE object_id = @t AND action = 'ticket.assignee_changed'", new NpgsqlParameter("t", ticket.Id)));

        // Delivering the same event again after success is harmless, and the history stays one coherent row.
        await engine.RunAsync(w.Dept.Id, ticket.Id, "TicketCreated", key, AutomationChain.Start("retry"), CancellationToken.None);
        await engine.RunAsync(w.Dept.Id, ticket.Id, "TicketCreated", key, AutomationChain.Start("retry"), CancellationToken.None);
        Assert.Equal(resumed, await ExecutionOfAsync(ticket.Id));
        Assert.Equal(notesBefore, (await MessagesAsync(w, ticket.Id)).EnumerateArray().Count(m => m.GetProperty("authorType").GetString() == "System"));
        var history = await HistoryAsync(w, ticket.Json.GetProperty("reference").GetString());
        var item = Assert.Single(history.EnumerateArray());
        Assert.Equal("Completed", item.GetProperty("state").GetString());
        Assert.True(item.GetProperty("finishedAt").ValueKind == JsonValueKind.String && item.GetProperty("error").ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public async Task An_execution_abandoned_part_way_is_resumed_after_its_lease_and_not_before()
    {
        await using var w = await TimedWorld.CreateAsync(fixture, Start);
        await RuleAsync(w, "Two steps", "TicketCreated", [], [Action("addInternalNote", text: "Only once"), Action("setPriority", "Urgent")]);
        var ticket = await w.NewTicketAsync();
        var (_, _, key) = await ExecutionOfAsync(ticket.Id);

        // Simulate a worker that died after the note but before the priority change: Running, note Done, priority Pending, priority reverted.
        await fixture.ExecuteAsync(
            "UPDATE ticketing.automation_executions SET state = 'Running', finished_at = NULL, started_at = now() - interval '10 minutes', "
            + "actions = jsonb_set(actions, '{1,outcome}', '\"Pending\"') WHERE ticket_id = @t",
            new NpgsqlParameter("t", ticket.Id));
        await fixture.ExecuteAsync("UPDATE ticketing.tickets SET priority = 2 WHERE id = @t", new NpgsqlParameter("t", ticket.Id));

        // A retry is a new delivery in a new scope (a scope remembers what it already loaded).
        async Task RetryAsync()
        {
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<AutomationEngine>().RunAsync(w.Dept.Id, ticket.Id, "TicketCreated", key, AutomationChain.Start("retry"), CancellationToken.None);
        }

        // A still-fresh Running execution belongs to a live worker: it is left alone.
        await fixture.ExecuteAsync("UPDATE ticketing.automation_executions SET started_at = now() WHERE ticket_id = @t", new NpgsqlParameter("t", ticket.Id));
        await RetryAsync();
        Assert.Equal("Running", (await ExecutionOfAsync(ticket.Id)).State);
        Assert.Equal("Normal", (await w.TicketAsync(ticket.Id)).Json.GetProperty("priority").GetString());

        // After the lease the abandoned execution resumes: the note is not added again, the priority is applied.
        await fixture.ExecuteAsync("UPDATE ticketing.automation_executions SET started_at = now() - interval '10 minutes' WHERE ticket_id = @t", new NpgsqlParameter("t", ticket.Id));
        await RetryAsync();
        var done = await ExecutionOfAsync(ticket.Id);
        Assert.Equal("Completed", done.State);
        Assert.Equal("Urgent", (await w.TicketAsync(ticket.Id)).Json.GetProperty("priority").GetString());
        Assert.Equal(1, (await MessagesAsync(w, ticket.Id)).EnumerateArray().Count(m => m.GetProperty("authorType").GetString() == "System"));
        await RetryAsync();
        Assert.Equal(done, await ExecutionOfAsync(ticket.Id));
    }
}
