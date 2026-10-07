#pragma warning disable CA1861 // one-off request payloads
using System.Net;
using System.Text.Json;
using Helpdesk.Host.Tests.Infrastructure;
using Npgsql;

namespace Helpdesk.Host.Tests.Ticketing;

/// <summary>Personal queue preferences (per account, grant no access) and the extra operational queue filters, all through the one existing list endpoint.</summary>
public sealed class QueuePreferencesTests(HelpdeskFixture fixture) : IClassFixture<HelpdeskFixture>
{
    private static async Task<JsonElement> PrefsAsync(TestUser user) => (await user.Client.GetAsync("/api/me/queue-preferences")).Expect(HttpStatusCode.OK).Json;

    private static string[] Strings(JsonElement array) => [.. array.EnumerateArray().Select(c => c.GetString()!)];

    private static async Task<Guid[]> ListAsync(TestUser user, DepartmentRef d, string query) =>
        [.. (await user.Client.GetAsync($"/api/tickets?queue=all&departmentId={d.Id}&status=Open&status=Pending&limit=100{query}")).Expect(HttpStatusCode.OK).Json
            .GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid())];

    private Task<int> SetMessageAsync(Guid ticketId, string error) =>
        fixture.ExecuteAsync("UPDATE ticketing.messages SET delivery_state = 'Failed', delivery_error = @e WHERE ticket_id = @t AND kind = 'Public' AND author_type = 'Agent'",
            new NpgsqlParameter("e", error), new NpgsqlParameter("t", ticketId));

    [Fact]
    public async Task Columns_and_the_default_are_saved_per_account_with_safe_fallbacks()
    {
        var d = await fixture.NewDeptAsync();
        Assert.Equal(JsonValueKind.Null, (await PrefsAsync(d.Agent)).GetProperty("columns").ValueKind); // standard set until chosen

        (await d.Agent.Client.PutAsync("/api/me/queue-preferences", new { columns = new[] { "status", "assignee", "updated" }, defaultQueue = "unassigned" })).Expect(HttpStatusCode.OK);
        var saved = await PrefsAsync(d.Agent);
        Assert.Equal(["status", "assignee", "updated"], Strings(saved.GetProperty("columns"))); // order is kept
        Assert.Equal("unassigned", saved.GetProperty("defaultQueue").GetString());
        Assert.Equal(JsonValueKind.Null, (await PrefsAsync(d.Lead)).GetProperty("columns").ValueKind); // another account is unaffected

        (await d.Agent.Client.PutAsync("/api/me/queue-preferences", new { columns = new[] { "status", "secret_field" } })).Expect(HttpStatusCode.BadRequest);
        (await d.Agent.Client.PutAsync("/api/me/queue-preferences", new { columns = new[] { "status", "status" } })).Expect(HttpStatusCode.BadRequest);
        (await d.Agent.Client.PutAsync("/api/me/queue-preferences", new { defaultQueue = "everything" })).Expect(HttpStatusCode.BadRequest);

        // A default view must be the caller's own; when it disappears the default quietly reads back as none.
        var view = (await d.Agent.Client.PostAsync("/api/views", new { name = "Mine", filter = new { queue = "my" }, isPinned = true })).Expect(HttpStatusCode.Created);
        var foreign = (await d.Lead.Client.PostAsync("/api/views", new { name = "Lead view", filter = new { queue = "all" } })).Expect(HttpStatusCode.Created);
        (await d.Agent.Client.PutAsync("/api/me/queue-preferences", new { defaultViewId = foreign.Id })).Expect(HttpStatusCode.BadRequest);
        (await d.Agent.Client.PutAsync("/api/me/queue-preferences", new { defaultViewId = view.Id, defaultQueue = "my" })).Expect(HttpStatusCode.BadRequest);
        (await d.Agent.Client.PutAsync("/api/me/queue-preferences", new { columns = new[] { "status" }, defaultViewId = view.Id })).Expect(HttpStatusCode.OK);
        Assert.Equal(view.Id, (await PrefsAsync(d.Agent)).GetProperty("defaultViewId").GetGuid());
        (await d.Agent.Client.DeleteAsync($"/api/views/{view.Id}")).Expect(HttpStatusCode.NoContent);
        var after = await PrefsAsync(d.Agent);
        Assert.Equal(JsonValueKind.Null, after.GetProperty("defaultViewId").ValueKind);
        Assert.Equal(JsonValueKind.Null, after.GetProperty("defaultQueue").ValueKind); // no stale default, no error
        Assert.Equal(["status"], Strings(after.GetProperty("columns")));
    }

    [Fact]
    public async Task The_extra_filters_select_the_same_sets_the_dashboard_counts_and_a_type_filter_never_crosses_departments()
    {
        var d = await fixture.NewDeptAsync();
        var other = await fixture.NewDeptAsync();
        var calendar = (await d.Admin.Client.PostAsync($"{d.Department.Url}/business-hours", new { name = "All week", timeZone = "UTC", exceptions = Array.Empty<object>(), weekly = new[] { "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday" }.Select(day => new { day, intervals = new[] { new { start = "00:00", end = "23:59" } } }).ToArray() })).Expect(HttpStatusCode.Created);
        (await d.Admin.Client.PostAsync($"{d.Department.Url}/sla-policies", new { name = "Respond", calendarId = calendar.Id, firstResponseMinutes = 600, resolutionMinutes = (int?)null, pauseWhenPending = false, approachingLeadMinutes = 30, conditions = Array.Empty<object>() })).Expect(HttpStatusCode.Created);
        var team = await fixture.CreateTeamAsync(d.Department);
        var type = (await d.Admin.Client.PostAsync($"{d.Department.Url}/ticket-types", new { name = "Hardware" })).Expect(HttpStatusCode.Created);

        var plain = await d.Lead.CreateTicketAsync(d.Department, TicketHelpers.NewTicketBody("Plain"));
        var withTeam = await d.Lead.CreateTicketAsync(d.Department, TicketHelpers.NewTicketBody("With team", extra: new { TeamId = team.Id }));
        var answered = await d.Lead.CreateTicketAsync(d.Department, TicketHelpers.NewTicketBody("Answered", extra: new { TeamId = team.Id }));
        var uncertain = await d.Lead.CreateTicketAsync(d.Department, TicketHelpers.NewTicketBody("Uncertain", extra: new { TeamId = team.Id }));
        (await d.Agent.PostMessageAsync(answered.Id, "Public", "A human reply")).Expect(HttpStatusCode.Created);
        (await d.Agent.PostMessageAsync(uncertain.Id, "Public", "Maybe sent")).Expect(HttpStatusCode.Created);
        await fixture.ExecuteAsync("UPDATE ticketing.tickets SET ticket_type_id = @ty WHERE id = @t", new NpgsqlParameter("ty", type.Id), new NpgsqlParameter("t", withTeam.Id));
        await SetMessageAsync(answered.Id, "permission_denied");
        await SetMessageAsync(uncertain.Id, "outcome_uncertain");

        Assert.Equal([plain.Id], await ListAsync(d.Agent, d.Department, "&noTeam=true"));
        Assert.Equal([answered.Id], await ListAsync(d.Agent, d.Department, "&send=failed"));
        Assert.Equal([uncertain.Id], await ListAsync(d.Agent, d.Department, "&send=uncertain"));
        Assert.Equal([withTeam.Id], await ListAsync(d.Agent, d.Department, $"&typeId={type.Id}"));
        var awaiting = await ListAsync(d.Agent, d.Department, "&sla=awaitingresponse"); // nobody has answered plain and withTeam
        Assert.Equal(new[] { plain.Id, withTeam.Id }.Order().ToArray(), awaiting.Order().ToArray());
        (await d.Agent.Client.GetAsync($"/api/tickets?queue=all&departmentId={d.Department.Id}&send=sideways")).Expect(HttpStatusCode.BadRequest);

        // The dashboard reports exactly these sets (one definition).
        var dash = (await d.Agent.Client.GetAsync($"{d.Department.Url}/dashboard")).Expect(HttpStatusCode.OK).Json;
        Assert.Equal(awaiting.Length, dash.GetProperty("backlog").GetProperty("awaitingFirstResponse").GetInt32());
        Assert.Equal(1, dash.GetProperty("sends").GetProperty("failedTickets").GetInt32());
        Assert.Equal(1, dash.GetProperty("sends").GetProperty("uncertainTickets").GetInt32());

        // Saved views carry the new filters.
        var view = (await d.Agent.Client.PostAsync("/api/views", new { name = "Failed sends", filter = new { queue = "all", send = "failed", typeId = type.Id, noTeam = true } })).Expect(HttpStatusCode.Created);
        Assert.Equal("failed", view.Json.GetProperty("filter").GetProperty("send").GetString());
        Assert.True(view.Json.GetProperty("filter").GetProperty("noTeam").GetBoolean());
        (await d.Agent.Client.PostAsync("/api/views", new { name = "Bad", filter = new { queue = "all", send = "sideways" } })).Expect(HttpStatusCode.BadRequest);

        // Types are per department: the picker is only for members, and another department's type id selects nothing there.
        Assert.Contains((await d.Agent.Client.GetAsync($"/api/departments/{d.Department.Id}/queue-types")).Expect(HttpStatusCode.OK).Json.EnumerateArray(), t => t.GetProperty("id").GetGuid() == type.Id);
        (await other.Agent.Client.GetAsync($"/api/departments/{d.Department.Id}/queue-types")).Expect(HttpStatusCode.NotFound);
        Assert.Empty(await ListAsync(other.Agent, other.Department, $"&typeId={type.Id}"));
        Assert.Empty((await other.Agent.Client.GetAsync($"/api/tickets?queue=all&departmentId={d.Department.Id}&noTeam=true")).Expect(HttpStatusCode.OK).Json.GetProperty("items").EnumerateArray()); // department scope unchanged
    }
}
