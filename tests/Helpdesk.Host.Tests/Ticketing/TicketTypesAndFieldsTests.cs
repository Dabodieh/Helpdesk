#pragma warning disable CA1861 // one-off request payloads
using System.Net;
using System.Text.Json;
using Helpdesk.Host.Tests.Infrastructure;
using Helpdesk.Host.Tests.Mail;
using Helpdesk.Host.Tests.Portal;

namespace Helpdesk.Host.Tests.Ticketing;

/// <summary>Ticket types and ticket fields: configuration, the invariants between them and tickets, agent editing, requester safety and history.</summary>
public sealed class TicketTypesAndFieldsTests(HelpdeskFixture fixture) : IClassFixture<HelpdeskFixture>
{
    private static Dictionary<string, object> Fields(params (string Key, object Value)[] values) => values.ToDictionary(v => v.Key, v => v.Value);

    private static async Task<ApiResponse> TypeAsync(Dept d, string name, bool isDefault = false) =>
        (await d.Admin.Client.PostAsync($"{d.Department.Url}/ticket-types", new { name, isDefault })).Expect(HttpStatusCode.Created);

    private static async Task<ApiResponse> FieldAsync(Dept d, object field) =>
        (await d.Admin.Client.PostAsync($"{d.Department.Url}/ticket-fields", field)).Expect(HttpStatusCode.Created);

    private static async Task LinkAsync(Dept d, ApiResponse type, params object[] links) =>
        (await d.Admin.Client.PutAsync($"{d.Department.Url}/ticket-types/{type.Id}/fields", new { fields = links })).Expect(HttpStatusCode.OK);

    private static async Task<JsonElement> PropertiesAsync(TestUser user, string reference) =>
        (await user.Client.GetAsync($"/api/tickets/{reference}/properties")).Expect(HttpStatusCode.OK).Json;

    private static JsonElement Field(JsonElement properties, string key) =>
        properties.GetProperty("fields").EnumerateArray().Single(f => f.GetProperty("key").GetString() == key);

    private static async Task<ApiResponse> CreateAsync(Dept d, string subject, object? extra = null, string? email = null) =>
        await d.Agent.CreateTicketAsync(d.Department, TicketHelpers.NewTicketBody(subject, email ?? $"r-{Unique.Id()}@example.test", extra));

    /// <summary>Raw create (no status expectation) for the refusal cases.</summary>
    private static async Task<ApiResponse> TryCreateAsync(Dept d, string subject, object? extra) =>
        await d.Agent.Client.PostAsync($"{d.Department.Url}/tickets", TicketHelpers.NewTicketBody(subject, $"r-{Unique.Id()}@example.test", extra));

    private static async Task<string> VersionOfTypeAsync(Dept d, Guid id) =>
        (await d.Admin.Client.GetAsync($"{d.Department.Url}/ticket-types")).Json.EnumerateArray().Single(t => t.GetProperty("id").GetGuid() == id).GetProperty("version").GetString()!;

    [Fact]
    public async Task Types_are_managed_per_department_with_one_default_and_no_destructive_edits()
    {
        var d = await fixture.NewDeptAsync();
        var other = await fixture.NewDeptAsync();
        var incident = await TypeAsync(d, "Incident", isDefault: true);
        var access = await TypeAsync(d, "Access Request");
        var url = $"{d.Department.Url}/ticket-types";

        var list = (await d.Admin.Client.GetAsync(url)).Expect(HttpStatusCode.OK).Json;
        Assert.Equal(["Incident", "Access Request"], list.EnumerateArray().Select(t => t.GetProperty("name").GetString()!).ToArray());
        Assert.True(list[0].GetProperty("isDefault").GetBoolean());

        // Making another type the default moves it; deactivating the default clears it; an inactive type cannot be the default.
        var moved = (await d.Admin.Client.PatchAsync($"{url}/{access.Id}", new { version = access.Version, isDefault = true })).Expect(HttpStatusCode.OK);
        Assert.True(moved.Json.GetProperty("isDefault").GetBoolean());
        Assert.False((await d.Admin.Client.GetAsync(url)).Json[0].GetProperty("isDefault").GetBoolean());
        var inactive = (await d.Admin.Client.PatchAsync($"{url}/{access.Id}", new { version = moved.Version, isActive = false })).Expect(HttpStatusCode.OK);
        Assert.False(inactive.Json.GetProperty("isDefault").GetBoolean());
        (await d.Admin.Client.PatchAsync($"{url}/{access.Id}", new { version = inactive.Version, isDefault = true })).Expect(HttpStatusCode.BadRequest);

        // Validation, duplicates, stale versions, reordering.
        (await d.Admin.Client.PostAsync(url, new { name = " " })).Expect(HttpStatusCode.BadRequest);
        (await d.Admin.Client.PostAsync(url, new { name = "Incident" })).Expect(HttpStatusCode.Conflict);
        (await d.Admin.Client.PatchAsync($"{url}/{incident.Id}", new { version = "bogus", name = "x" })).Expect(HttpStatusCode.BadRequest);
        (await d.Admin.Client.PatchAsync($"{url}/{access.Id}", new { version = access.Version, name = "stale" })).Expect(HttpStatusCode.Conflict);
        (await d.Admin.Client.PutAsync($"{url}/order", new { ids = new[] { access.Id, incident.Id } })).Expect(HttpStatusCode.OK);
        Assert.Equal(["Access Request", "Incident"], (await d.Admin.Client.GetAsync(url)).Json.EnumerateArray().Select(t => t.GetProperty("name").GetString()!).ToArray());
        (await d.Admin.Client.PutAsync($"{url}/order", new { ids = new[] { access.Id } })).Expect(HttpStatusCode.BadRequest);

        // Permission and department isolation; the type list of one department is invisible to another's administrator.
        (await d.Agent.Client.GetAsync(url)).Expect(HttpStatusCode.Forbidden);
        (await d.Admin.Client.GetAsync($"{other.Department.Url}/ticket-types")).Expect(HttpStatusCode.NotFound);
        (await d.Admin.Client.PatchAsync($"{other.Department.Url}/ticket-types/{incident.Id}", new { version = "x", name = "hijack" })).Expect(HttpStatusCode.NotFound);
        Assert.True(await fixture.CountAsync("SELECT count(*) FROM audit.audit_events WHERE department_id = @d AND action IN ('ticket_type.created','ticket_type.updated')", TicketKit.P("d", d.Id)) >= 4);
    }

    [Fact]
    public async Task Types_and_forms_can_only_use_the_fields_and_types_of_their_own_department()
    {
        var d = await fixture.NewDeptAsync();
        var other = await fixture.NewDeptAsync();
        var mine = await FieldAsync(d, PortalKit.Field("Asset", "ShortText", key: "asset"));
        var foreign = await FieldAsync(other, PortalKit.Field("Asset", "ShortText", key: "asset")); // the same key in another department is fine
        var type = await TypeAsync(d, "Hardware");
        var foreignType = await TypeAsync(other, "Hardware");
        var (formId, _) = await d.FormAsync("Hardware form");

        await LinkAsync(d, type, new { fieldId = mine.Id });
        (await d.Admin.Client.PutAsync($"{d.Department.Url}/ticket-types/{type.Id}/fields", new { fields = new[] { new { fieldId = foreign.Id } } })).Expect(HttpStatusCode.BadRequest);
        (await d.Admin.Client.PutAsync($"{d.Department.Url}/request-forms/{formId}/fields", new { fields = new[] { new { fieldId = foreign.Id } } })).Expect(HttpStatusCode.BadRequest);
        (await d.Admin.Client.PutAsync($"{d.Department.Url}/ticket-types/{foreignType.Id}/fields", new { fields = new[] { new { fieldId = mine.Id } } })).Expect(HttpStatusCode.NotFound);
        (await d.Admin.Client.PostAsync($"{d.Department.Url}/request-forms", new { name = "Crafted", ticketTypeId = foreignType.Id })).Expect(HttpStatusCode.BadRequest);
        var form = (await d.Admin.Client.GetAsync($"{d.Department.Url}/request-forms")).Json[0];
        (await d.Admin.Client.PatchAsync($"{d.Department.Url}/request-forms/{formId}", new { version = form.GetProperty("version").GetString(), ticketTypeId = foreignType.Id })).Expect(HttpStatusCode.BadRequest);

        // The database refuses a cross-department link even if the API check were bypassed.
        await Assert.ThrowsAnyAsync<Exception>(() => fixture.CountAsync(
            "INSERT INTO ticketing.ticket_type_fields (type_id, field_id, department_id, sort_order) VALUES (@t, @f, @d, 1) RETURNING 1", TicketKit.P("t", type.Id), TicketKit.P("f", foreign.Id), TicketKit.P("d", d.Id)));

        // Usage is visible to the administrator.
        var usage = (await d.Admin.Client.GetAsync($"{d.Department.Url}/ticket-fields")).Json.EnumerateArray().Single(f => f.GetProperty("id").GetGuid() == mine.Id);
        Assert.Equal("Hardware", usage.GetProperty("types")[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task A_ticket_gets_an_explicit_type_else_the_default_and_a_foreign_or_inactive_type_is_refused()
    {
        var d = await fixture.NewDeptAsync();
        var other = await fixture.NewDeptAsync();
        var box = await fixture.CreateMailboxAsync(d);
        var noDefault = await CreateAsync(d, "No types configured");
        Assert.Equal(JsonValueKind.Null, (await d.Agent.GetTicketAsync(noDefault.Id)).Json.GetProperty("type").ValueKind);

        var hardware = await TypeAsync(d, "Hardware");
        var general = await TypeAsync(d, "General", isDefault: true);
        var foreign = await TypeAsync(other, "Hardware");

        Assert.Equal(hardware.Id, (await d.Agent.GetTicketAsync((await CreateAsync(d, "Explicit", new { ticketTypeId = hardware.Id })).Id)).Json.GetProperty("type").GetProperty("id").GetGuid());
        Assert.Equal(general.Id, (await d.Agent.GetTicketAsync((await CreateAsync(d, "Defaulted")).Id)).Json.GetProperty("type").GetProperty("id").GetGuid());
        (await TryCreateAsync(d, "Crafted", new { ticketTypeId = foreign.Id })).Expect(HttpStatusCode.BadRequest);
        (await TryCreateAsync(d, "Random", new { ticketTypeId = Guid.NewGuid() })).Expect(HttpStatusCode.BadRequest);
        (await d.Admin.Client.PatchAsync($"{d.Department.Url}/ticket-types/{hardware.Id}", new { version = hardware.Version, isActive = false })).Expect(HttpStatusCode.OK);
        (await TryCreateAsync(d, "Disabled", new { ticketTypeId = hardware.Id })).Expect(HttpStatusCode.BadRequest);

        // Email cannot choose and never fails: it gets the department default.
        var subject = $"Mail {Unique.Id()}";
        await fixture.ReceiveAsync(box, MailKit.Mail($"mail-{Unique.Id()}@example.test", subject, "Hello", "Mailer"));
        var ticket = Assert.Single(await d.Agent.TicketsAsync(d, subject));
        Assert.Equal(general.Id, (await d.Agent.GetTicketAsync(ticket.GetProperty("id").GetGuid())).Json.GetProperty("type").GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Agents_edit_the_type_and_fields_of_a_ticket_with_validation_audit_and_history_that_survives_configuration_changes()
    {
        var d = await fixture.NewDeptAsync();
        var other = await fixture.NewDeptAsync();
        var location = await FieldAsync(d, PortalKit.Field("Location", "ShortText", key: "location", appliesToAllTypes: true));
        var device = await FieldAsync(d, PortalKit.Field("Device", "SingleSelect", options: [new { label = "Laptop" }, new { label = "Phone" }], key: "device"));
        var cost = await FieldAsync(d, PortalKit.Field("Cost centre", "ShortText", requesterVisible: false, key: "cost"));
        var tag = await FieldAsync(d, PortalKit.Field("Asset tag", "ShortText", required: true, key: "tag"));
        var hardware = await TypeAsync(d, "Hardware");
        var access = await TypeAsync(d, "Access");
        await LinkAsync(d, hardware, new { fieldId = device.Id }, new { fieldId = cost.Id }, new { fieldId = tag.Id, required = false });
        await LinkAsync(d, access, new { fieldId = tag.Id });
        var foreignField = await FieldAsync(other, PortalKit.Field("Foreign", "ShortText", key: "foreign"));

        var created = await CreateAsync(d, "Laptop request");
        var reference = created.Reference();
        var empty = await PropertiesAsync(d.Agent, reference);
        Assert.Equal(["location"], empty.GetProperty("fields").EnumerateArray().Select(f => f.GetProperty("key").GetString()!).ToArray()); // untyped: common fields only
        var version = empty.GetProperty("version").GetString();

        // Choose a type: its fields appear, in order, with requirements that apply here (the link overrides the field default).
        var typed = (await d.Agent.Client.PatchAsync($"/api/tickets/{reference}/properties", new { version, ticketTypeId = hardware.Id })).Expect(HttpStatusCode.OK).Json;
        Assert.Equal("Hardware", typed.GetProperty("type").GetProperty("displayName").GetString());
        Assert.Equal(["location", "device", "cost", "tag"], typed.GetProperty("fields").EnumerateArray().Select(f => f.GetProperty("key").GetString()!).ToArray());
        Assert.False(Field(typed, "tag").GetProperty("required").GetBoolean());
        version = typed.GetProperty("version").GetString();

        // Typed values are validated server side; the first failure changes nothing.
        async Task<ApiResponse> Patch(object fields) => await d.Agent.Client.PatchAsync($"/api/tickets/{reference}/properties", new { version, fields });
        (await Patch(Fields(("device", "tablet")))).Expect(HttpStatusCode.BadRequest);
        (await Patch(Fields(("location", 5)))).Expect(HttpStatusCode.BadRequest);
        (await Patch(Fields(("foreign", "x")))).Expect(HttpStatusCode.BadRequest); // another department's key is simply not available
        (await Patch(Fields(("location", "HQ"), ("device", "nope")))).Expect(HttpStatusCode.BadRequest);
        Assert.Equal(JsonValueKind.Null, Field(await PropertiesAsync(d.Agent, reference), "location").GetProperty("value").ValueKind);
        var filled = (await Patch(Fields(("location", "HQ 2nd floor"), ("device", "laptop"), ("cost", "CC-7")))).Expect(HttpStatusCode.OK).Json;
        Assert.Equal("Laptop", Field(filled, "device").GetProperty("display").GetString());
        version = filled.GetProperty("version").GetString();

        // Stale version and permissions.
        (await d.Agent.Client.PatchAsync($"/api/tickets/{reference}/properties", new { version = typed.GetProperty("version").GetString(), fields = Fields(("location", "x")) })).Expect(HttpStatusCode.Conflict);
        (await d.Viewer.Client.PatchAsync($"/api/tickets/{reference}/properties", new { version, fields = Fields(("location", "x")) })).Expect(HttpStatusCode.Forbidden);
        (await d.Viewer.Client.GetAsync($"/api/tickets/{reference}/properties")).Expect(HttpStatusCode.OK);
        (await other.Agent.Client.GetAsync($"/api/tickets/{reference}/properties")).Expect(HttpStatusCode.NotFound);
        (await d.Agent.Client.PatchAsync($"/api/tickets/{reference}/properties", new { version })).Expect(HttpStatusCode.BadRequest);

        // Requirements: a required field cannot be cleared; changing the type to one that requires a field never invalidates the ticket, it is only reported.
        var access2 = (await d.Agent.Client.PatchAsync($"/api/tickets/{reference}/properties", new { version, ticketTypeId = access.Id })).Expect(HttpStatusCode.OK).Json;
        version = access2.GetProperty("version").GetString();
        Assert.True(Field(access2, "tag").GetProperty("missingRequired").GetBoolean());
        Assert.Equal("Laptop", Field(access2, "device").GetProperty("display").GetString()); // not applicable any more, still readable
        Assert.False(Field(access2, "device").GetProperty("applicable").GetBoolean());
        (await Patch(Fields(("tag", null!)))).Expect(HttpStatusCode.BadRequest);
        var tagged = (await Patch(Fields(("tag", "T-100")))).Expect(HttpStatusCode.OK).Json;
        version = tagged.GetProperty("version").GetString();
        (await Patch(Fields(("tag", null!)))).Expect(HttpStatusCode.BadRequest); // required here: no clearing

        // Cross-department and disabled types cannot be chosen; clearing is allowed.
        var foreignType = await TypeAsync(other, "Foreign");
        (await d.Agent.Client.PatchAsync($"/api/tickets/{reference}/properties", new { version, ticketTypeId = foreignType.Id })).Expect(HttpStatusCode.BadRequest);
        (await d.Admin.Client.PatchAsync($"{d.Department.Url}/ticket-types/{hardware.Id}", new { version = await VersionOfTypeAsync(d, hardware.Id), isActive = false })).Expect(HttpStatusCode.OK);
        (await d.Agent.Client.PatchAsync($"/api/tickets/{reference}/properties", new { version, ticketTypeId = hardware.Id })).Expect(HttpStatusCode.BadRequest);

        // History survives configuration changes: a disabled field and a renamed option keep the stored value readable.
        var fieldsUrl = $"{d.Department.Url}/ticket-fields";
        var deviceNow = (await d.Admin.Client.GetAsync(fieldsUrl)).Json.EnumerateArray().Single(f => f.GetProperty("id").GetGuid() == device.Id);
        var relabelled = (await d.Admin.Client.PatchAsync($"{fieldsUrl}/{device.Id}", new { version = deviceNow.GetProperty("version").GetString(), options = new object[] { new { value = "laptop", label = "Notebook" }, new { value = "phone", label = "Phone" } } })).Expect(HttpStatusCode.OK);
        (await d.Admin.Client.PatchAsync($"{fieldsUrl}/{device.Id}", new { version = relabelled.Version, isActive = false })).Expect(HttpStatusCode.OK);
        var later = await PropertiesAsync(d.Agent, reference);
        Assert.Equal("Laptop", Field(later, "device").GetProperty("display").GetString()); // the label as recorded; the stored value is the stable option value
        Assert.Equal("laptop", Field(later, "device").GetProperty("value").GetString());
        Assert.False(Field(later, "device").GetProperty("editable").GetBoolean());
        (await d.Agent.Client.PatchAsync($"/api/tickets/{reference}/properties", new { version = later.GetProperty("version").GetString(), fields = Fields(("device", "phone")) })).Expect(HttpStatusCode.BadRequest);

        // Audit: the type change and each field change with previous and new values.
        var activity = (await d.Agent.Client.GetAsync($"/api/tickets/{reference}/activity")).Expect(HttpStatusCode.OK).Json.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(2, activity.Count(a => a.GetProperty("action").GetString() == "ticket.type_changed"));
        var locationChange = activity.Single(a => a.GetProperty("action").GetString() == "ticket.field_changed" && a.GetProperty("next").GetProperty("key").GetString() == "location");
        Assert.Equal("HQ 2nd floor", locationChange.GetProperty("next").GetProperty("display").GetString());
        Assert.Equal(JsonValueKind.Null, locationChange.GetProperty("previous").GetProperty("display").ValueKind);
        _ = foreignField;
    }

    [Fact]
    public async Task Requesters_see_and_supply_only_requester_visible_fields_and_edit_only_editable_ones()
    {
        var d = await fixture.NewDeptAsync();
        var alice = await fixture.EmployeeAsync("alice");
        var bob = await fixture.EmployeeAsync("bob");
        var type = await TypeAsync(d, "Hardware");
        var colour = await FieldAsync(d, PortalKit.Field("Colour", "SingleSelect", options: [new { label = "Red" }, new { label = "Blue" }], key: "colour", requesterEditable: true));
        var asset = await FieldAsync(d, PortalKit.Field("Asset", "ShortText", required: true, key: "asset"));
        var cost = await FieldAsync(d, PortalKit.Field("Cost centre", "ShortText", requesterVisible: false, key: "cost"));
        await LinkAsync(d, type, new { fieldId = cost.Id });
        var form = (await d.Admin.Client.PostAsync($"{d.Department.Url}/request-forms", new { name = $"Hardware {Unique.Id()}", ticketTypeId = type.Id, audience = "AllEmployees" })).Expect(HttpStatusCode.Created);
        (await d.Admin.Client.PutAsync($"{d.Department.Url}/request-forms/{form.Id}/fields", new { fields = new object[] { new { fieldId = asset.Id }, new { fieldId = colour.Id, required = true }, new { fieldId = cost.Id } } })).Expect(HttpStatusCode.OK);

        // The portal form shows requester-visible fields only, in order, with the form's requirement override.
        var portalForm = (await alice.User.Client.GetAsync($"/api/portal/forms/{form.Id}")).Expect(HttpStatusCode.OK).Json;
        Assert.Equal(["asset", "colour"], portalForm.GetProperty("fields").EnumerateArray().Select(f => f.GetProperty("key").GetString()!).ToArray());
        Assert.True(portalForm.GetProperty("fields")[1].GetProperty("required").GetBoolean());
        Assert.DoesNotContain("cost", portalForm.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("requesterEditable", portalForm.GetRawText(), StringComparison.Ordinal);

        // An agent-only field cannot be submitted; the override is enforced; a valid submission carries the form's type.
        object Body(params (string Key, object Value)[] f) => new { formId = form.Id, subject = "Laptop", description = "d", submissionId = Guid.NewGuid(), fields = Fields(f) };
        (await alice.User.Client.PostAsync("/api/portal/requests", Body(("asset", "A-1"), ("cost", "CC-1"), ("colour", "red")))).Expect(HttpStatusCode.BadRequest);
        (await alice.User.Client.PostAsync("/api/portal/requests", Body(("asset", "A-1")))).Expect(HttpStatusCode.BadRequest);
        var reference = (await alice.User.Client.PostAsync("/api/portal/requests", Body(("asset", "A-1"), ("colour", "red")))).Expect(HttpStatusCode.Created).Json.GetProperty("reference").GetString()!;
        Assert.Equal(type.Id, (await d.Agent.Client.GetAsync($"/api/tickets/{reference}")).Expect(HttpStatusCode.OK).Json.GetProperty("type").GetProperty("id").GetGuid());

        // An agent records the agent-only value; the requester can never read it in the ticket, the list, or realtime-visible shapes.
        var props = await PropertiesAsync(d.Agent, reference);
        (await d.Agent.Client.PatchAsync($"/api/tickets/{reference}/properties", new { version = props.GetProperty("version").GetString(), fields = Fields(("cost", "CC-SECRET")) })).Expect(HttpStatusCode.OK);
        var mine = (await alice.User.Client.GetAsync($"/api/portal/requests/{reference}")).Expect(HttpStatusCode.OK).Json;
        Assert.DoesNotContain("CC-SECRET", mine.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("Cost centre", mine.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("CC-SECRET", (await alice.User.Client.GetAsync("/api/portal/requests?filter=all")).Body, StringComparison.Ordinal);

        // Only requester-editable fields can be changed, only by their owner, only with valid values.
        var edit = $"/api/portal/requests/{reference}/fields";
        Assert.Equal(["colour"], mine.GetProperty("editableFields").EnumerateArray().Select(f => f.GetProperty("key").GetString()!).ToArray());
        (await alice.User.Client.PutAsync(edit, new { fields = Fields(("asset", "A-2")) })).Expect(HttpStatusCode.BadRequest);
        (await alice.User.Client.PutAsync(edit, new { fields = Fields(("cost", "x")) })).Expect(HttpStatusCode.BadRequest);
        (await alice.User.Client.PutAsync(edit, new { fields = Fields(("colour", "green")) })).Expect(HttpStatusCode.BadRequest);
        (await bob.User.Client.PutAsync(edit, new { fields = Fields(("colour", "blue")) })).Expect(HttpStatusCode.NotFound);
        var edited = (await alice.User.Client.PutAsync(edit, new { fields = Fields(("colour", "blue")) })).Expect(HttpStatusCode.OK).Json;
        Assert.Contains(edited.GetProperty("fields").EnumerateArray(), f => f.GetProperty("label").GetString() == "Colour" && f.GetProperty("value").GetString() == "Blue");
        Assert.Equal("Blue", Field(await PropertiesAsync(d.Agent, reference), "colour").GetProperty("display").GetString());

        // Hiding a field later hides its old values from the requester too.
        var colourNow = (await d.Admin.Client.GetAsync($"{d.Department.Url}/ticket-fields")).Json.EnumerateArray().Single(f => f.GetProperty("id").GetGuid() == colour.Id);
        (await d.Admin.Client.PatchAsync($"{d.Department.Url}/ticket-fields/{colour.Id}", new { version = colourNow.GetProperty("version").GetString(), requesterEditable = false, requesterVisible = false })).Expect(HttpStatusCode.OK);
        var hidden = (await alice.User.Client.GetAsync($"/api/portal/requests/{reference}")).Expect(HttpStatusCode.OK).Json;
        Assert.DoesNotContain(hidden.GetProperty("fields").EnumerateArray(), f => f.GetProperty("label").GetString() == "Colour");
    }

    [Fact]
    public async Task Type_is_usable_as_an_sla_and_automation_condition_and_as_a_queue_filter_within_its_department()
    {
        var d = await fixture.NewDeptAsync();
        var other = await fixture.NewDeptAsync();
        var hardware = await TypeAsync(d, "Hardware");
        var access = await TypeAsync(d, "Access");
        var foreign = await TypeAsync(other, "Foreign");
        var calendar = (await d.Admin.Client.PostAsync($"{d.Department.Url}/business-hours", new { name = "24h", timeZone = "UTC", weekly = Enum.GetNames<DayOfWeek>().Select(day => new { day, intervals = new[] { new { start = "00:00", end = "24:00" } } }).ToArray() })).Expect(HttpStatusCode.Created);
        object Conditions(Guid type) => new object[] { new { field = "ticketType", @operator = "in", values = new[] { type.ToString() } } };
        (await d.Admin.Client.PostAsync($"{d.Department.Url}/sla-policies", new { name = "Hardware SLA", calendarId = calendar.Id, firstResponseMinutes = 60, resolutionMinutes = 480, conditions = Conditions(hardware.Id) })).Expect(HttpStatusCode.Created);
        (await d.Admin.Client.PostAsync($"{d.Department.Url}/sla-policies", new { name = "Foreign SLA", calendarId = calendar.Id, firstResponseMinutes = 60, resolutionMinutes = 480, conditions = Conditions(foreign.Id) })).Expect(HttpStatusCode.BadRequest);
        (await d.Admin.Client.PostAsync($"{d.Department.Url}/automations", new { name = "Foreign rule", trigger = "TicketCreated", conditions = Conditions(foreign.Id), actions = new object[] { new { type = "setPriority", value = "High" } } })).Expect(HttpStatusCode.BadRequest);
        var rule = (await d.Admin.Client.PostAsync($"{d.Department.Url}/automations", new { name = "Access is high", trigger = "TicketCreated", conditions = Conditions(access.Id), actions = new object[] { new { type = "setPriority", value = "High" } } })).Expect(HttpStatusCode.Created);
        Assert.NotEqual(Guid.Empty, rule.Id);

        var hw = await CreateAsync(d, "Hardware one", new { ticketTypeId = hardware.Id });
        var ac = await CreateAsync(d, "Access one", new { ticketTypeId = access.Id });
        Assert.Equal("Hardware SLA", (await d.Agent.Client.GetAsync($"/api/tickets/{hw.Reference()}/sla")).Expect(HttpStatusCode.OK).Json.GetProperty("policyName").GetString());
        Assert.NotEqual(HttpStatusCode.OK, (await d.Agent.Client.GetAsync($"/api/tickets/{ac.Reference()}/sla")).Status);
        Assert.Equal("High", (await d.Agent.GetTicketAsync(ac.Id)).Json.GetProperty("priority").GetString());
        Assert.Equal("Normal", (await d.Agent.GetTicketAsync(hw.Id)).Json.GetProperty("priority").GetString());

        var filtered = (await d.Agent.Client.GetAsync($"/api/tickets?queue=all&typeId={hardware.Id}&limit=100")).Expect(HttpStatusCode.OK).Json.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("reference").GetString()!).ToArray();
        Assert.Equal([hw.Reference()], filtered);
    }
}
