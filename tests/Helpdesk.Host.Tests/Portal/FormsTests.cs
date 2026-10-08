#pragma warning disable CA1861 // one-off request payloads
using System.Net;
using System.Text.Json;
using Helpdesk.Host.Tests.Infrastructure;
using Helpdesk.Host.Tests.Mail;
using Helpdesk.Host.Tests.Ticketing;

namespace Helpdesk.Host.Tests.Portal;

/// <summary>Request forms and custom fields: administration, server-side validation, protected values, the normal ticket pipeline, and historical meaning.</summary>
public sealed class FormsTests(HelpdeskFixture fixture) : IClassFixture<HelpdeskFixture>
{
    private static readonly object[] Colours = [new { label = "Red" }, new { label = "Blue" }];

    private static Dictionary<string, object> Fields(params (string Key, object Value)[] values)
    {
        var d = new Dictionary<string, object>();
        foreach (var (k, v) in values)
        {
            d[k] = v;
        }

        return d;
    }

    private async Task<(Dept D, Guid FormId, PortalKit.Employee Alice)> SetupAsync()
    {
        var d = await fixture.NewDeptAsync();
        var (formId, _) = await d.FormAsync(fixture, 
            "Hardware issue",
            new { DefaultPriority = "High", Description = "Broken equipment" },
            PortalKit.Field("Asset tag", "ShortText", required: true, key: "asset"),
            PortalKit.Field("Details", "LongText"),
            PortalKit.Field("Colour", "SingleSelect", options: Colours, key: "colour"),
            PortalKit.Field("Accessories", "MultiSelect", options: [new { label = "Mouse" }, new { label = "Keyboard" }], key: "accessories"),
            PortalKit.Field("Urgent for a meeting", "Checkbox", key: "meeting"),
            PortalKit.Field("Needed by", "Date", key: "needed_by"),
            PortalKit.Field("Quantity", "Number", key: "quantity"),
            PortalKit.Field("Cost centre", "ShortText", requesterVisible: false, key: "cost_centre"));
        return (d, formId, await fixture.EmployeeAsync("alice"));
    }

    [Fact]
    public async Task Administrators_manage_forms_and_fields_with_stable_keys_and_no_destructive_edits()
    {
        var d = await fixture.NewDeptAsync();
        var other = await fixture.NewDeptAsync();
        var (formId, _) = await d.FormAsync(fixture, "General", null, PortalKit.Field("Colour", "SingleSelect", options: Colours, key: "colour"));
        var admin = d.Admin.Client;

        var fields = (await admin.GetAsync($"{d.Department.Url}/ticket-fields")).Expect(HttpStatusCode.OK).Json;
        var field = fields[0];
        Assert.Equal("colour", field.GetProperty("key").GetString());
        Assert.Equal(["red", "blue"], field.GetProperty("options").EnumerateArray().Select(o => o.GetProperty("value").GetString()!).ToArray());
        Assert.Equal(formId, field.GetProperty("forms")[0].GetProperty("id").GetGuid()); // where the field is used
        var linked = (await admin.GetAsync($"{d.Department.Url}/request-forms")).Expect(HttpStatusCode.OK).Json[0].GetProperty("fields")[0];
        Assert.Equal(field.GetProperty("id").GetGuid(), linked.GetProperty("fieldId").GetGuid());

        // Label and options can change; the key and type cannot; options cannot be removed (deactivate instead).
        var url = $"{d.Department.Url}/ticket-fields/{field.GetProperty("id").GetGuid()}";
        var version = field.GetProperty("version").GetString();
        (await admin.PatchAsync(url, new { version, type = "Number" })).Expect(HttpStatusCode.BadRequest);
        (await admin.PatchAsync(url, new { version, key = "other" })).Expect(HttpStatusCode.BadRequest);
        (await admin.PatchAsync(url, new { version, options = new object[] { new { value = "red", label = "Red" } } })).Expect(HttpStatusCode.BadRequest);
        var renamed = (await admin.PatchAsync(url, new { version, label = "Favourite colour", options = new object[] { new { value = "red", label = "Crimson" }, new { value = "blue", label = "Blue", isActive = false }, new { label = "Green" } } })).Expect(HttpStatusCode.OK);
        Assert.Equal("Favourite colour", renamed.Json.GetProperty("label").GetString());
        Assert.Equal(["red", "blue", "green"], renamed.Json.GetProperty("options").EnumerateArray().Select(o => o.GetProperty("value").GetString()!).ToArray());
        (await admin.PatchAsync(url, new { version, label = "stale" })).Expect(HttpStatusCode.Conflict);

        // Validation and limits.
        (await admin.PostAsync($"{d.Department.Url}/ticket-fields", new { label = "", type = "ShortText" })).Expect(HttpStatusCode.BadRequest);
        (await admin.PostAsync($"{d.Department.Url}/ticket-fields", new { label = "Script", type = "Formula" })).Expect(HttpStatusCode.BadRequest);
        (await admin.PostAsync($"{d.Department.Url}/ticket-fields", new { label = "Pick", type = "SingleSelect" })).Expect(HttpStatusCode.BadRequest);
        (await admin.PostAsync($"{d.Department.Url}/ticket-fields", new { label = "Colour again", key = "colour", type = "ShortText" })).Expect(HttpStatusCode.Conflict);
        (await admin.PostAsync($"{d.Department.Url}/ticket-fields", new { label = "Nope", type = "ShortText", requesterVisible = false, requesterEditable = true })).Expect(HttpStatusCode.BadRequest);
        (await admin.PostAsync($"{d.Department.Url}/request-forms", new { name = "General" })).Expect(HttpStatusCode.Conflict);

        // Department isolation and permission.
        (await d.Agent.Client.GetAsync($"{d.Department.Url}/request-forms")).Expect(HttpStatusCode.Forbidden);
        (await d.Lead.Client.PostAsync($"{d.Department.Url}/request-forms", new { name = "nope" })).Expect(HttpStatusCode.Forbidden);
        (await admin.GetAsync($"{other.Department.Url}/request-forms")).Expect(HttpStatusCode.NotFound);
        (await admin.PatchAsync($"{other.Department.Url}/request-forms/{formId}", new { version = "x", name = "hijack" })).Expect(HttpStatusCode.NotFound);
        (await admin.GetAsync($"{other.Department.Url}/ticket-fields")).Expect(HttpStatusCode.NotFound);
        Assert.True(await fixture.CountAsync("SELECT count(*) FROM audit.audit_events WHERE department_id = @d AND action IN ('request_form.created','ticket_field.created','ticket_field.updated')", TicketKit.P("d", d.Id)) >= 3);
    }

    [Fact]
    public async Task A_submitted_request_is_a_normal_ticket_with_server_derived_values_validated_fields_sla_and_routing()
    {
        var (d, formId, alice) = await SetupAsync();
        var calendar = (await d.Admin.Client.PostAsync($"{d.Department.Url}/business-hours", new { name = "24h", timeZone = "UTC", weekly = Enum.GetNames<DayOfWeek>().Select(day => new { day, intervals = new[] { new { start = "00:00", end = "24:00" } } }).ToArray() })).Expect(HttpStatusCode.Created);
        (await d.Admin.Client.PostAsync($"{d.Department.Url}/sla-policies", new { name = "Std", calendarId = calendar.Id, firstResponseMinutes = 60, resolutionMinutes = 480 })).Expect(HttpStatusCode.Created);
        (await d.Admin.Client.PostAsync($"{d.Department.Url}/automations", new
        {
            name = "Hardware routing",
            trigger = "TicketCreated",
            conditions = new object[] { new { field = "subjectContains", @operator = "contains", values = new[] { "laptop" } } },
            actions = new object[] { new { type = "assignAgent", value = d.Agent.Id.ToString() } },
        })).Expect(HttpStatusCode.Created);

        var created = (await alice.User.Client.PostAsync("/api/portal/requests", new
        {
            formId,
            subject = "Laptop will not start",
            description = "It shows a black screen.",
            submissionId = Guid.NewGuid(),
            fields = Fields(("asset", "A-1042"), ("colour", "red"), ("accessories", new[] { "mouse", "keyboard" }), ("meeting", true), ("needed_by", "2026-12-24"), ("quantity", 2)),
            // None of these can be chosen by a requester: unknown properties are ignored.
            priority = "Urgent", assigneeUserId = d.Agent.Id, departmentId = Guid.NewGuid(), mailboxId = Guid.NewGuid(), kind = "Internal", slaPolicyId = Guid.NewGuid(),
        })).Expect(HttpStatusCode.Created).Json;
        var reference = created.GetProperty("reference").GetString()!;
        Assert.StartsWith(d.Key + "-", reference);
        Assert.Equal("In progress", created.GetProperty("statusLabel").GetString());

        // The ticket is an ordinary ticket: portal source, form defaults, routing, SLA, requester message, and visible to agents through the real queue.
        var ticket = (await d.Agent.Client.GetAsync($"/api/tickets/{reference}")).Expect(HttpStatusCode.OK).Json;
        Assert.Equal("portal", ticket.GetProperty("source").GetString());
        Assert.Equal("High", ticket.GetProperty("priority").GetString()); // the form's default, not "Urgent"
        Assert.Equal(d.Agent.Id, ticket.GetProperty("assignee").GetProperty("id").GetGuid()); // routing automation, not the requester's assigneeUserId
        Assert.Equal("Hardware issue", ticket.GetProperty("requestForm").GetString());
        Assert.Equal("Std", (await d.Agent.Client.GetAsync($"/api/tickets/{reference}/sla")).Expect(HttpStatusCode.OK).Json.GetProperty("policyName").GetString());
        var queue = (await d.Agent.Client.GetAsync($"/api/tickets?queue=all&assigneeUserId={d.Agent.Id}&limit=100")).Expect(HttpStatusCode.OK).Json.GetProperty("items");
        Assert.Contains(queue.EnumerateArray(), i => i.GetProperty("reference").GetString() == reference);
        var messages = (await d.Agent.Client.GetAsync($"/api/tickets/{reference}/messages")).Expect(HttpStatusCode.OK).Json.GetProperty("items");
        Assert.Equal("portal", Assert.Single(messages.EnumerateArray()).GetProperty("source").GetString());

        // Agents see agent-visible values; the requester sees requester-visible ones (here the same set, rendered with option labels).
        var agentFields = ticket.GetProperty("fields").EnumerateArray().ToDictionary(f => f.GetProperty("label").GetString()!, f => f.GetProperty("value").GetString()!);
        Assert.Equal("A-1042", agentFields["Asset tag"]);
        Assert.Equal("Red", agentFields["Colour"]);
        Assert.Equal("Mouse, Keyboard", agentFields["Accessories"]);
        Assert.Equal("Yes", agentFields["Urgent for a meeting"]);
        Assert.Equal("2", agentFields["Quantity"]);
        Assert.DoesNotContain("Cost centre", agentFields.Keys);
        var portal = (await alice.User.Client.GetAsync($"/api/portal/requests/{reference}")).Expect(HttpStatusCode.OK).Json;
        Assert.Equal("Hardware issue", portal.GetProperty("form").GetString());
        Assert.Equal(6, portal.GetProperty("fields").GetArrayLength());

        // It appears in My Requests, and a duplicate submission returns the same ticket.
        Assert.Contains((await alice.PortalListAsync()).GetProperty("items").EnumerateArray(), i => i.GetProperty("reference").GetString() == reference);
        Assert.Equal(1, await fixture.CountAsync("SELECT count(*) FROM ticketing.tickets WHERE department_id = @d AND subject = 'Laptop will not start'", TicketKit.P("d", d.Id)));
    }

    [Fact]
    public async Task Submissions_are_idempotent_and_invalid_values_are_rejected_per_field()
    {
        var (d, formId, alice) = await SetupAsync();
        var submission = Guid.NewGuid();
        object Body(Guid id) => new { formId, subject = "Mouse", description = "Broken", submissionId = id, fields = Fields(("asset", "T-1")) };
        var first = (await alice.User.Client.PostAsync("/api/portal/requests", Body(submission))).Expect(HttpStatusCode.Created).Json.GetProperty("reference").GetString();
        var second = (await alice.User.Client.PostAsync("/api/portal/requests", Body(submission))).Json.GetProperty("reference").GetString();
        Assert.Equal(first, second);
        Assert.Equal(1, await fixture.CountAsync("SELECT count(*) FROM ticketing.tickets WHERE department_id = @d AND subject = 'Mouse'", TicketKit.P("d", d.Id)));

        async Task<JsonElement> RejectAsync(object fields, string? subject = "S")
        {
            var response = (await alice.User.Client.PostAsync("/api/portal/requests", new { formId, subject, description = "d", fields })).Expect(HttpStatusCode.BadRequest);
            return response.Json.GetProperty("errors");
        }

        Assert.True((await RejectAsync(Fields())).TryGetProperty("fields.asset", out _)); // required
        Assert.True((await RejectAsync(Fields(("asset", " ")))).TryGetProperty("fields.asset", out _));
        Assert.True((await RejectAsync(Fields(("asset", new string('x', 501))))).TryGetProperty("fields.asset", out _));
        Assert.True((await RejectAsync(Fields(("asset", "ok"), ("colour", "purple")))).TryGetProperty("fields.colour", out _));
        Assert.True((await RejectAsync(Fields(("asset", "ok"), ("accessories", new[] { "mouse", "toaster" })))).TryGetProperty("fields.accessories", out _));
        Assert.True((await RejectAsync(Fields(("asset", "ok"), ("needed_by", "tomorrow")))).TryGetProperty("fields.needed_by", out _));
        Assert.True((await RejectAsync(Fields(("asset", "ok"), ("quantity", "many")))).TryGetProperty("fields.quantity", out _));
        Assert.True((await RejectAsync(Fields(("asset", "ok"), ("meeting", "yes")))).TryGetProperty("fields.meeting", out _));
        Assert.True((await RejectAsync(Fields(("asset", "ok")), subject: "")).TryGetProperty("subject", out _));

        // Hidden/admin-only, unknown and other forms' fields are indistinguishable and rejected; nothing was created by the failures.
        Assert.True((await RejectAsync(Fields(("asset", "ok"), ("cost_centre", "CC-9")))).TryGetProperty("fields.cost_centre", out _));
        Assert.True((await RejectAsync(Fields(("asset", "ok"), ("nonexistent", "x")))).TryGetProperty("fields.nonexistent", out _));
        Assert.Equal(1, await fixture.CountAsync("SELECT count(*) FROM ticketing.tickets WHERE department_id = @d", TicketKit.P("d", d.Id)));

        // Hidden and inactive forms do not exist for requesters.
        var (hidden, _) = await d.FormAsync(fixture, "Hidden", new { VisibleToRequesters = false });
        (await alice.User.Client.GetAsync($"/api/portal/forms/{hidden}")).Expect(HttpStatusCode.NotFound);
        (await alice.User.Client.PostAsync("/api/portal/requests", new { formId = hidden, subject = "x", description = "y" })).Expect(HttpStatusCode.NotFound);
        Assert.DoesNotContain(hidden.ToString(), (await alice.User.Client.GetAsync("/api/portal/forms")).Expect(HttpStatusCode.OK).Body, StringComparison.Ordinal);
        (await alice.User.Client.GetAsync($"/api/portal/forms/{Guid.NewGuid()}")).Expect(HttpStatusCode.NotFound);

        var portalForm = (await alice.User.Client.GetAsync($"/api/portal/forms/{formId}")).Expect(HttpStatusCode.OK).Json;
        Assert.DoesNotContain("cost_centre", portalForm.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("defaultTeamId", portalForm.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Changing_a_form_later_never_changes_what_an_existing_ticket_shows()
    {
        var (d, formId, alice) = await SetupAsync();
        var reference = (await alice.User.Client.PostAsync("/api/portal/requests", new { formId, subject = "Old", description = "d", fields = Fields(("asset", "A-1"), ("colour", "red")) }))
            .Expect(HttpStatusCode.Created).Json.GetProperty("reference").GetString()!;

        // Rename the field, relabel the option, then deactivate the field.
        var colour = (await d.Admin.Client.GetAsync($"{d.Department.Url}/ticket-fields")).Json.EnumerateArray().Single(f => f.GetProperty("key").GetString() == "colour");
        Assert.True(colour.GetProperty("hasValues").GetBoolean());
        var url = $"{d.Department.Url}/ticket-fields/{colour.GetProperty("id").GetGuid()}";
        var renamed = (await d.Admin.Client.PatchAsync(url, new { version = colour.GetProperty("version").GetString(), label = "Shade", options = new object[] { new { value = "red", label = "Scarlet" }, new { value = "blue", label = "Blue" } } })).Expect(HttpStatusCode.OK);
        (await d.Admin.Client.PatchAsync(url, new { version = renamed.Json.GetProperty("version").GetString(), isActive = false })).Expect(HttpStatusCode.OK);

        var agentFields = (await d.Agent.Client.GetAsync($"/api/tickets/{reference}")).Expect(HttpStatusCode.OK).Json.GetProperty("fields").EnumerateArray().Select(f => $"{f.GetProperty("label").GetString()}={f.GetProperty("value").GetString()}").ToArray();
        Assert.Contains("Colour=Red", agentFields);
        var portalFields = (await alice.User.Client.GetAsync($"/api/portal/requests/{reference}")).Expect(HttpStatusCode.OK).Json.GetProperty("fields").EnumerateArray().Select(f => $"{f.GetProperty("label").GetString()}={f.GetProperty("value").GetString()}").ToArray();
        Assert.Contains("Colour=Red", portalFields);

        // New submissions use the current form: the deactivated field is gone and cannot be submitted.
        var current = (await alice.User.Client.GetAsync($"/api/portal/forms/{formId}")).Expect(HttpStatusCode.OK).Json;
        Assert.DoesNotContain(current.GetProperty("fields").EnumerateArray(), f => f.GetProperty("key").GetString() == "colour");
        (await alice.User.Client.PostAsync("/api/portal/requests", new { formId, subject = "New", description = "d", fields = Fields(("asset", "A-2"), ("colour", "red")) })).Expect(HttpStatusCode.BadRequest);
    }
}
