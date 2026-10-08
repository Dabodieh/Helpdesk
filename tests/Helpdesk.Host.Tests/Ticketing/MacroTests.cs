#pragma warning disable CA1861 // one-off request payloads
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Helpdesk.Host.Tests.Infrastructure;
using Helpdesk.Host.Tests.Mail;
using Helpdesk.Host.Tests.Portal;
using Helpdesk.SharedKernel.Authorization;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Helpdesk.Host.Tests.Ticketing;

/// <summary>Macros: department scoped, validated against the final ticket state, atomic, permission-neutral and never sending email.</summary>
public sealed class MacroTests(HelpdeskFixture fixture) : IClassFixture<HelpdeskFixture>
{
    private static object Act(string type, string? value = null, string? text = null, string? key = null, object? data = null) => new { type, value, text, key, data };

    private static Task<ApiResponse> CreateMacroAsync(Dept d, string name, params object[] actions) =>
        d.Admin.Client.PostAsync($"{d.Department.Url}/macros", new { name, description = $"about {name}", actions });

    private static async Task<ApiResponse> RunAsync(TestUser user, Guid ticketId, Guid macroId)
    {
        var current = (await user.GetTicketAsync(ticketId)).Expect(HttpStatusCode.OK);
        return await user.Client.PostAsync(TicketKit.Url(ticketId, $"/macros/{macroId}/run"), new { version = current.Version });
    }

    private static string Normalised(ApiResponse r)
    {
        try
        {
            if (JsonNode.Parse(r.Body) is JsonObject o)
            {
                o.Remove("traceId");
                return o.ToJsonString();
            }
        }
        catch (JsonException)
        {
        }

        return r.Body;
    }

    private static async Task SameAsRandomAsync(string what, Func<Guid, Task<ApiResponse>> call, Guid real)
    {
        var foreign = await call(real);
        var missing = await call(Guid.NewGuid());
        Assert.False(foreign.Status is >= HttpStatusCode.OK and < HttpStatusCode.MultipleChoices, $"{what}: succeeded ({foreign.Status})");
        Assert.True(foreign.Status == missing.Status && Normalised(foreign) == Normalised(missing), $"{what}: {foreign.Status} {foreign.Body} vs {missing.Status} {missing.Body}");
    }

    private static async Task<ApiResponse> CannedAsync(Dept d, string title, string body) =>
        (await d.Admin.Client.PostAsync("/api/canned-responses", new { scope = "Department", departmentId = d.Id, title, body })).Expect(HttpStatusCode.Created);

    private WebApplicationFactory<Program> WithoutPermissions(params string[] denied) =>
        fixture.Factory.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
        {
            var original = services.Single(s => s.ServiceType == typeof(IAuthorizer));
            services.AddScoped(original.ImplementationType!);
            services.AddScoped<IAuthorizer>(sp => new TicketIsolationLeakageTests.DenyingAuthorizer((IAuthorizer)sp.GetRequiredService(original.ImplementationType!), denied));
        }));

    // ----- administration -----

    [Fact]
    public async Task Macros_are_created_edited_deactivated_and_reordered_by_the_department_manager_with_validated_structured_actions()
    {
        var d = await fixture.NewDeptAsync();
        var created = (await CreateMacroAsync(d, "Resolve password reset", Act("setStatus", "resolved"), Act("setPriority", "Normal"))).Expect(HttpStatusCode.Created);
        Assert.Equal(["Status → Resolved", "Priority → Normal"], created.Json.GetProperty("summary").EnumerateArray().Select(s => s.GetString()!).ToArray());
        var second = (await CreateMacroAsync(d, "Second", Act("assignToMe"))).Expect(HttpStatusCode.Created);

        (await d.Viewer.Client.GetAsync($"{d.Department.Url}/macros")).Expect(HttpStatusCode.Forbidden);
        (await d.Agent.Client.PostAsync($"{d.Department.Url}/macros", new { name = "x", actions = new[] { Act("assignToMe") } })).Expect(HttpStatusCode.Forbidden);
        (await CreateMacroAsync(d, "Resolve password reset", Act("assignToMe"))).ExpectCode(HttpStatusCode.Conflict, "duplicate");

        // Shape: unknown action, repeats, assignee kinds are one group, bad values, empty.
        (await CreateMacroAsync(d, "bad1", Act("runScript", "rm"))).Expect(HttpStatusCode.BadRequest);
        (await CreateMacroAsync(d, "bad2", Act("setStatus", "Open"), Act("setStatus", "Closed"))).Expect(HttpStatusCode.BadRequest);
        (await CreateMacroAsync(d, "bad3", Act("assignToMe"), Act("clearAssignee"))).Expect(HttpStatusCode.BadRequest);
        (await CreateMacroAsync(d, "bad4", Act("setStatus", "Deleted"))).Expect(HttpStatusCode.BadRequest);
        (await CreateMacroAsync(d, "bad5", Act("internalNote", text: " "))).Expect(HttpStatusCode.BadRequest);
        (await CreateMacroAsync(d, "bad6", Act("publicReply", value: Guid.NewGuid().ToString()))).Expect(HttpStatusCode.BadRequest);
        (await CreateMacroAsync(d, "bad7")).Expect(HttpStatusCode.BadRequest);
        Assert.Equal(2, (await d.Admin.Client.GetAsync($"{d.Department.Url}/macros")).Json.GetArrayLength());

        var edited = (await d.Admin.Client.PatchAsync($"{d.Department.Url}/macros/{created.Id}", new { version = created.Version, name = "Renamed", isActive = false })).Expect(HttpStatusCode.OK);
        Assert.False(edited.Json.GetProperty("isActive").GetBoolean());
        (await d.Admin.Client.PatchAsync($"{d.Department.Url}/macros/{created.Id}", new { version = created.Version, name = "stale" })).ExpectCode(HttpStatusCode.Conflict, "version_conflict");

        var order = (await d.Admin.Client.PutAsync($"{d.Department.Url}/macros/order", new { ids = new[] { second.Id, created.Id } })).Expect(HttpStatusCode.OK);
        Assert.Equal([second.Id, created.Id], order.Json.EnumerateArray().Select(m => m.GetProperty("id").GetGuid()).ToArray());
        (await d.Admin.Client.PutAsync($"{d.Department.Url}/macros/order", new { ids = new[] { second.Id } })).Expect(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task The_picker_lists_only_active_macros_of_the_ticket_department_that_the_caller_may_run_and_searches_on_the_server()
    {
        var d = await fixture.NewDeptAsync();
        var other = await fixture.NewDeptAsync();
        var t = (await d.TicketForAsync($"a-{Unique.Id()}@example.test")).Expect(HttpStatusCode.Created);
        var reset = (await CreateMacroAsync(d, "Reset password", Act("setStatus", "Resolved"))).Expect(HttpStatusCode.Created);
        var off = (await CreateMacroAsync(d, "Retired macro", Act("setPriority", "High"))).Expect(HttpStatusCode.Created);
        (await d.Admin.Client.PatchAsync($"{d.Department.Url}/macros/{off.Id}", new { version = off.Version, isActive = false })).Expect(HttpStatusCode.OK);
        await CreateMacroAsync(other, "Foreign reset", Act("setStatus", "Resolved"));
        var noteMacro = (await CreateMacroAsync(d, "Note only", Act("internalNote", text: "checked"))).Expect(HttpStatusCode.Created);

        var all = (await d.Agent.Client.GetAsync(TicketKit.Url(t.Id, "/macros"))).Expect(HttpStatusCode.OK).Json.EnumerateArray().Select(m => m.GetProperty("id").GetGuid()).ToArray();
        Assert.Equal(new[] { reset.Id, noteMacro.Id }.Order().ToArray(), all.Order().ToArray());
        var found = (await d.Agent.Client.GetAsync(TicketKit.Url(t.Id, "/macros?q=reset"))).Expect(HttpStatusCode.OK);
        Assert.Equal(reset.Id, Assert.Single(found.Json.EnumerateArray()).GetProperty("id").GetGuid());
        Assert.DoesNotContain("Foreign", found.Body, StringComparison.Ordinal);

        // A read-only viewer gets no macro it could not run by hand; a caller without note permission gets no macro with a note.
        Assert.Empty((await d.Viewer.Client.GetAsync(TicketKit.Url(t.Id, "/macros"))).Expect(HttpStatusCode.OK).Json.EnumerateArray());
        await using var noNotes = WithoutPermissions(PermissionCodes.TicketsNote);
        var limited = await fixture.SignInAsync(d.Agent.Subject, factory: noNotes);
        Assert.Equal([reset.Id], (await limited.Client.GetAsync(TicketKit.Url(t.Id, "/macros"))).Expect(HttpStatusCode.OK).Json.EnumerateArray().Select(m => m.GetProperty("id").GetGuid()).ToArray());
    }

    // ----- execution -----

    [Fact]
    public async Task Running_a_macro_applies_every_state_change_in_one_step_and_only_prepares_the_public_reply()
    {
        var d = await fixture.NewDeptAsync();
        await fixture.CreateMailboxAsync(d); // replies WOULD be emailed from here if anything sent one
        var type = (await d.Admin.Client.PostAsync($"{d.Department.Url}/ticket-types", new { name = "Hardware Request" })).Expect(HttpStatusCode.Created);
        var field = (await d.Admin.Client.PostAsync($"{d.Department.Url}/ticket-fields", PortalKit.Field("Device action", "SingleSelect", key: "device_action", options: [new { value = "replace", label = "Replacement", isActive = true }, new { value = "repair", label = "Repair", isActive = true }]))).Expect(HttpStatusCode.Created);
        (await d.Admin.Client.PutAsync($"{d.Department.Url}/ticket-types/{type.Id}/fields", new { fields = new[] { new { fieldId = field.Id } } })).Expect(HttpStatusCode.OK);
        var canned = await CannedAsync(d, "Laptop replaced", "Your laptop replacement is booked.");

        // Field listed BEFORE the type that makes it applicable: valid, because only the final state counts.
        var macro = (await CreateMacroAsync(d, "Prepare laptop replacement",
            Act("setField", key: "device_action", data: "replace"), Act("setType", type.Id.ToString()), Act("setPriority", "High"), Act("setStatus", "Pending"),
            Act("setAssignee", d.Agent.Id.ToString()), Act("internalNote", text: "macro note"), Act("publicReply", canned.Id.ToString()))).Expect(HttpStatusCode.Created);
        var t = (await d.TicketForAsync($"a-{Unique.Id()}@example.test", "Broken laptop")).Expect(HttpStatusCode.Created);
        var outbound = await fixture.CountAsync("SELECT count(*) FROM mail.messages WHERE department_id = @d AND direction = 'Outbound'", TicketKit.P("d", d.Id));
        var before = (await d.Lead.GetTicketAsync(t.Id)).Expect(HttpStatusCode.OK);

        var run = (await RunAsync(d.Lead, t.Id, macro.Id)).Expect(HttpStatusCode.OK).Json;
        var after = run.GetProperty("ticket");
        Assert.Equal("Pending", after.Str("status"));
        Assert.Equal("High", after.Str("priority"));
        Assert.Equal(d.Agent.Id, after.GetProperty("assignee").GetProperty("id").GetGuid());
        Assert.Equal(type.Id, after.GetProperty("type").GetProperty("id").GetGuid());
        Assert.True(run.GetProperty("noteAdded").GetBoolean());
        Assert.NotEqual(before.Version, after.Str("version"));

        var props = (await d.Lead.Client.GetAsync($"/api/tickets/{t.Reference()}/properties")).Expect(HttpStatusCode.OK).Json;
        Assert.Equal("Replacement", props.GetProperty("fields").EnumerateArray().Single(f => f.Str("key") == "device_action").Str("display"));

        // The reply is a draft returned to the caller only: no message, no outbound mail, resolved from the canned response now.
        var draft = run.GetProperty("draft");
        Assert.Equal("Your laptop replacement is booked.", draft.Str("body"));
        Assert.Equal(canned.Id, draft.GetProperty("cannedResponseId").GetGuid());
        var messages = (await d.Lead.MessagesAsync(t.Id)).ToList();
        Assert.DoesNotContain(messages, m => m.Str("authorType") == "Agent" && m.Str("kind") == "Public");
        Assert.Contains(messages, m => m.Str("kind") == "Internal" && m.Str("body") == "macro note");
        Assert.Equal(outbound, await fixture.CountAsync("SELECT count(*) FROM mail.messages WHERE department_id = @d AND direction = 'Outbound'", TicketKit.P("d", d.Id)));

        // Normal history for the real changes plus one record of the run; the record names the macro and nothing of the note or reply.
        var audit = await fixture.AuditOfAsync(t.Id);
        var ran = audit.Single(a => a.Action == "ticket.macro_run");
        Assert.Equal(d.Lead.Id, ran.ActorUserId);
        Assert.Contains("Prepare laptop replacement", ran.Next, StringComparison.Ordinal);
        Assert.DoesNotContain("macro note", ran.Next, StringComparison.Ordinal);
        Assert.DoesNotContain("replacement is booked", ran.Next ?? "", StringComparison.Ordinal);
        Assert.Contains(audit, a => a.Action == "ticket.status_changed");
        Assert.Contains(audit, a => a.Action == "ticket.assignee_changed");
        Assert.Contains(audit, a => a.Action == "ticket.type_changed");

        // A renamed macro keeps the historical name in the ticket history.
        var current = (await d.Admin.Client.GetAsync($"{d.Department.Url}/macros")).Json.EnumerateArray().Single(m => m.GetProperty("id").GetGuid() == macro.Id);
        (await d.Admin.Client.PatchAsync($"{d.Department.Url}/macros/{macro.Id}", new { version = current.Str("version"), name = "Renamed later" })).Expect(HttpStatusCode.OK);
        Assert.Contains("Prepare laptop replacement", (await fixture.AuditOfAsync(t.Id)).Single(a => a.Action == "ticket.macro_run").Next, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_macro_never_does_more_than_the_caller_could_and_a_refused_or_invalid_macro_changes_nothing()
    {
        var d = await fixture.NewDeptAsync();
        var t = (await d.TicketForAsync($"a-{Unique.Id()}@example.test")).Expect(HttpStatusCode.Created);
        var update = (await CreateMacroAsync(d, "Update", Act("setPriority", "Urgent"))).Expect(HttpStatusCode.Created);
        var withNote = (await CreateMacroAsync(d, "Update and note", Act("setPriority", "Urgent"), Act("internalNote", text: "n"))).Expect(HttpStatusCode.Created);

        // No tickets.update: forbidden, nothing applied.
        (await RunAsync(d.Viewer, t.Id, update.Id)).Expect(HttpStatusCode.Forbidden);

        // No tickets.note: the whole macro is refused up front, including its permitted priority change.
        await using (var noNotes = WithoutPermissions(PermissionCodes.TicketsNote))
        {
            var agent = await fixture.SignInAsync(d.Agent.Subject, factory: noNotes);
            (await RunAsync(agent, t.Id, withNote.Id)).Expect(HttpStatusCode.Forbidden);
        }

        Assert.Equal("Normal", (await d.Agent.GetTicketAsync(t.Id)).Expect(HttpStatusCode.OK).Json.Str("priority"));
        Assert.DoesNotContain(await d.Agent.MessagesAsync(t.Id), m => m.Str("kind") == "Internal");

        // An assignee who leaves the department after the macro was saved: validation fails, the priority change is NOT applied (atomic).
        var leaver = await fixture.NewMemberAsync(d.Department, "Agent");
        var atomic = (await CreateMacroAsync(d, "Atomic", Act("setPriority", "Urgent"), Act("setAssignee", leaver.Id.ToString()))).Expect(HttpStatusCode.Created);
        var admin = await fixture.PlatformAdminAsync();
        (await admin.Client.DeleteAsync($"{d.Department.Url}/members/{leaver.Id}")).Expect(HttpStatusCode.NoContent);
        (await RunAsync(d.Lead, t.Id, atomic.Id)).Expect(HttpStatusCode.Conflict);
        Assert.Equal("Normal", (await d.Agent.GetTicketAsync(t.Id)).Expect(HttpStatusCode.OK).Json.Str("priority"));
        Assert.Null((await fixture.AuditOfAsync(t.Id)).FirstOrDefault(a => a.Action is "ticket.macro_run" or "ticket.priority_changed"));

        // A field that does not apply to the ticket's FINAL type fails the whole macro.
        var field = (await d.Admin.Client.PostAsync($"{d.Department.Url}/ticket-fields", PortalKit.Field("Only for typed", "ShortText", key: "only_typed"))).Expect(HttpStatusCode.Created);
        var type = (await d.Admin.Client.PostAsync($"{d.Department.Url}/ticket-types", new { name = "Typed" })).Expect(HttpStatusCode.Created);
        (await d.Admin.Client.PutAsync($"{d.Department.Url}/ticket-types/{type.Id}/fields", new { fields = new[] { new { fieldId = field.Id } } })).Expect(HttpStatusCode.OK);
        var inapplicable = (await CreateMacroAsync(d, "Inapplicable", Act("setPriority", "High"), Act("setField", key: "only_typed", data: "x"))).Expect(HttpStatusCode.Created);
        (await RunAsync(d.Lead, t.Id, inapplicable.Id)).Expect(HttpStatusCode.BadRequest);
        Assert.Equal("Normal", (await d.Agent.GetTicketAsync(t.Id)).Expect(HttpStatusCode.OK).Json.Str("priority"));

        // Assignment follows the normal rules: assigning to someone else needs assign.any (an agent only has assign.self), assigning to myself does not.
        var toOther = (await CreateMacroAsync(d, "To lead", Act("setAssignee", d.Lead.Id.ToString()))).Expect(HttpStatusCode.Created);
        var toMe = (await CreateMacroAsync(d, "To me", Act("assignToMe"))).Expect(HttpStatusCode.Created);
        (await RunAsync(d.Agent, t.Id, toOther.Id)).Expect(HttpStatusCode.Forbidden);
        var mine = (await RunAsync(d.Agent, t.Id, toMe.Id)).Expect(HttpStatusCode.OK).Json.GetProperty("ticket");
        Assert.Equal(d.Agent.Id, mine.GetProperty("assignee").GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Inactive_macros_unavailable_canned_responses_and_merged_tickets_refuse_to_run()
    {
        var d = await fixture.NewDeptAsync();
        var t = (await d.TicketForAsync($"a-{Unique.Id()}@example.test")).Expect(HttpStatusCode.Created);
        var canonical = (await d.TicketForAsync($"b-{Unique.Id()}@example.test")).Expect(HttpStatusCode.Created);
        var macro = (await CreateMacroAsync(d, "Prio", Act("setPriority", "High"))).Expect(HttpStatusCode.Created);

        var off = (await d.Admin.Client.PatchAsync($"{d.Department.Url}/macros/{macro.Id}", new { version = macro.Version, isActive = false })).Expect(HttpStatusCode.OK);
        (await RunAsync(d.Lead, t.Id, macro.Id)).ExpectCode(HttpStatusCode.Conflict, "inactive");
        (await d.Admin.Client.PatchAsync($"{d.Department.Url}/macros/{macro.Id}", new { version = off.Version, isActive = true })).Expect(HttpStatusCode.OK);

        // The canned response is looked up when the macro runs; if it was deactivated, nothing is substituted and nothing is applied.
        var canned = await CannedAsync(d, "Gone soon", "text");
        var withReply = (await CreateMacroAsync(d, "Reply", Act("setPriority", "Urgent"), Act("publicReply", canned.Id.ToString()))).Expect(HttpStatusCode.Created);
        (await d.Admin.Client.PatchAsync($"/api/canned-responses/{canned.Id}", new { version = canned.Version, isActive = false })).Expect(HttpStatusCode.OK);
        (await RunAsync(d.Lead, t.Id, withReply.Id)).ExpectCode(HttpStatusCode.Conflict, "canned_unavailable");
        Assert.Equal("Normal", (await d.Lead.GetTicketAsync(t.Id)).Expect(HttpStatusCode.OK).Json.Str("priority"));

        // A merged ticket is read-only: the macro is refused, not redirected.
        var c = (await d.Lead.GetTicketAsync(canonical.Id)).Expect(HttpStatusCode.OK);
        var s = (await d.Lead.GetTicketAsync(t.Id)).Expect(HttpStatusCode.OK);
        (await d.Lead.Client.PostAsync(TicketKit.Url(canonical.Id, "/merge"), new { version = c.Version, sources = new[] { new { ticketId = t.Id, version = s.Version } } })).Expect(HttpStatusCode.OK);
        (await RunAsync(d.Lead, t.Id, macro.Id)).ExpectCode(HttpStatusCode.Conflict, "ticket_merged");
        Assert.Equal("Normal", (await d.Lead.GetTicketAsync(canonical.Id)).Expect(HttpStatusCode.OK).Json.Str("priority"));
    }

    // ----- isolation and privacy -----

    [Fact]
    public async Task Macros_of_another_department_do_not_exist_for_everyone_else_and_cannot_reference_foreign_objects()
    {
        var it = await fixture.NewDeptAsync();
        var hr = await fixture.NewDeptAsync();
        var itType = (await it.Admin.Client.PostAsync($"{it.Department.Url}/ticket-types", new { name = "IT type" })).Expect(HttpStatusCode.Created);
        await it.Admin.Client.PostAsync($"{it.Department.Url}/ticket-fields", PortalKit.Field("IT secret field", "ShortText", key: "it_secret"));
        var itCanned = await CannedAsync(it, "IT canned", "secret text");
        var itMacro = (await CreateMacroAsync(it, "IT secret macro", Act("setPriority", "High"))).Expect(HttpStatusCode.Created);
        var itTicket = (await it.TicketForAsync($"a-{Unique.Id()}@example.test")).Expect(HttpStatusCode.Created);
        var hrTicket = (await hr.TicketForAsync($"b-{Unique.Id()}@example.test")).Expect(HttpStatusCode.Created);

        // Listing, creating and changing macros through another department's routes answers like a department that does not exist.
        await SameAsRandomAsync("list", id => hr.Admin.Client.GetAsync($"/api/departments/{id}/macros"), it.Id);
        await SameAsRandomAsync("create", id => hr.Admin.Client.PostAsync($"/api/departments/{id}/macros", new { name = "x", actions = new[] { Act("assignToMe") } }), it.Id);
        await SameAsRandomAsync("patch", id => hr.Admin.Client.PatchAsync($"{hr.Department.Url}/macros/{id}", new { version = "1", name = "x" }), itMacro.Id);

        // The picker and run of a ticket in another department, or an IT macro on an HR ticket, are indistinguishable from missing objects.
        (await hr.Agent.Client.GetAsync(TicketKit.Url(itTicket.Id, "/macros"))).Expect(HttpStatusCode.NotFound);
        await SameAsRandomAsync("run foreign macro", id => hr.Lead.Client.PostAsync(TicketKit.Url(hrTicket.Id, $"/macros/{id}/run"), new { version = "1" }), itMacro.Id);
        await SameAsRandomAsync("run on foreign ticket", id => hr.Lead.Client.PostAsync(TicketKit.Url(id, $"/macros/{itMacro.Id}/run"), new { version = "1" }), itTicket.Id);
        Assert.DoesNotContain("IT secret macro", (await hr.Agent.Client.GetAsync(TicketKit.Url(hrTicket.Id, "/macros"))).Expect(HttpStatusCode.OK).Body, StringComparison.Ordinal);
        Assert.Equal("Normal", (await it.Agent.GetTicketAsync(itTicket.Id)).Expect(HttpStatusCode.OK).Json.Str("priority"));

        // Crafted ids in a macro of the HR department: foreign person, type and canned response answer like random ids; a foreign field key like an unknown key.
        (await CreateMacroAsync(hr, $"t{Unique.Id()}", Act("setTeam", Guid.NewGuid().ToString()))).Expect(HttpStatusCode.BadRequest); // the team action no longer exists
        await SameAsRandomAsync("assignee", id => CreateMacroAsync(hr, $"a{Unique.Id()}", Act("setAssignee", id.ToString())), it.Agent.Id);
        await SameAsRandomAsync("type", id => CreateMacroAsync(hr, $"y{Unique.Id()}", Act("setType", id.ToString())), itType.Id);
        await SameAsRandomAsync("canned", id => CreateMacroAsync(hr, $"c{Unique.Id()}", Act("publicReply", id.ToString())), itCanned.Id);
        var foreignKey = await CreateMacroAsync(hr, $"f{Unique.Id()}", Act("setField", key: "it_secret", data: "x"));
        var unknownKey = await CreateMacroAsync(hr, $"g{Unique.Id()}", Act("setField", key: "zzz_unknown", data: "x"));
        Assert.Equal(unknownKey.Status, foreignKey.Status);
        Assert.DoesNotContain("IT secret", foreignKey.Body, StringComparison.Ordinal);
        Assert.Equal(0, (await hr.Admin.Client.GetAsync($"{hr.Department.Url}/macros")).Expect(HttpStatusCode.OK).Json.GetArrayLength());
    }

    [Fact]
    public async Task Requester_apis_expose_nothing_about_macros()
    {
        var d = await fixture.NewDeptAsync();
        var alice = await fixture.EmployeeAsync("alice");
        var t = (await d.TicketForAsync(alice.Email, "Alice request")).Expect(HttpStatusCode.Created);
        var macro = (await CreateMacroAsync(d, "Secret macro name", Act("setStatus", "Resolved"), Act("internalNote", text: "secret macro note"))).Expect(HttpStatusCode.Created);
        (await RunAsync(d.Lead, t.Id, macro.Id)).Expect(HttpStatusCode.OK);

        var list = (await alice.User.Client.GetAsync("/api/portal/requests")).Expect(HttpStatusCode.OK);
        var detail = (await alice.User.Client.GetAsync($"/api/portal/requests/{t.Reference()}")).Expect(HttpStatusCode.OK);
        foreach (var body in new[] { list.Body, detail.Body })
        {
            Assert.DoesNotContain("macro", body, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Equal("resolved", detail.Json.Str("status").ToLowerInvariant());
        (await alice.User.Client.GetAsync(TicketKit.Url(t.Id, "/macros"))).Expect(HttpStatusCode.NotFound);
        (await alice.User.Client.PostAsync(TicketKit.Url(t.Id, $"/macros/{macro.Id}/run"), new { version = "1" })).Expect(HttpStatusCode.NotFound);
    }
}
