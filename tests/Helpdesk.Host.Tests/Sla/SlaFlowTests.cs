#pragma warning disable CA1861 // test payloads are one-off literals
using System.Net;
using System.Text.Json;
using Helpdesk.Host.Tests.Infrastructure;
using Helpdesk.Host.Tests.Ticketing;
using Npgsql;

namespace Helpdesk.Host.Tests.Sla;

/// <summary>SLA clocks end to end through the real API with a controllable clock: deadlines, pause/resume, first response, resolution, reopening, snapshots, monitor and filters.</summary>
public sealed class SlaFlowTests(HelpdeskFixture fixture) : IClassFixture<HelpdeskFixture>
{
    [Fact]
    public async Task Friday_1630_plus_four_business_hours_is_monday_1230_and_first_response_monday_0930()
    {
        await using var w = await TimedWorld.CreateAsync(fixture, "2026-01-09T16:30:00Z");
        var calendar = await w.CalendarAsync();
        await w.PolicyAsync("Standard", calendar, firstResponse: 60, resolution: 240);
        var ticket = await w.NewTicketAsync();

        var sla = await w.SlaAsync(ticket.Id);
        Assert.Equal("Standard", sla.GetProperty("policyName").GetString());
        Assert.Equal("Office", sla.GetProperty("calendar").GetString());
        Assert.Equal(DateTimeOffset.Parse("2026-01-12T09:30:00Z", System.Globalization.CultureInfo.InvariantCulture), TimedWorld.At(sla, "firstResponse", "dueAt"));
        Assert.Equal(DateTimeOffset.Parse("2026-01-12T12:30:00Z", System.Globalization.CultureInfo.InvariantCulture), TimedWorld.At(sla, "resolution", "dueAt"));
        Assert.Equal("Running", sla.GetProperty("resolution").GetProperty("state").GetString());
        Assert.Equal("Applied", sla.GetProperty("events")[0].GetProperty("kind").GetString());
    }

    [Fact]
    public async Task A_holiday_closure_moves_the_deadline_and_out_of_hours_tickets_start_at_the_next_opening()
    {
        await using var w = await TimedWorld.CreateAsync(fixture, "2026-01-09T16:30:00Z");
        var calendar = await w.CalendarAsync(exceptions: new[] { new { date = "2026-01-12", name = "Bank holiday", intervals = Array.Empty<object>() } });
        await w.PolicyAsync("Standard", calendar, firstResponse: null, resolution: 240);
        var friday = await w.NewTicketAsync();
        Assert.Equal(DateTimeOffset.Parse("2026-01-13T12:30:00Z", System.Globalization.CultureInfo.InvariantCulture), TimedWorld.At(await w.SlaAsync(friday.Id), "resolution", "dueAt"));

        await w.GoToAsync("2026-01-10T10:00:00Z"); // Saturday
        var saturday = await w.NewTicketAsync();
        Assert.Equal(DateTimeOffset.Parse("2026-01-13T13:00:00Z", System.Globalization.CultureInfo.InvariantCulture), TimedWorld.At(await w.SlaAsync(saturday.Id), "resolution", "dueAt"));
    }

    [Fact]
    public async Task Deadlines_across_the_clock_change_use_local_opening_hours()
    {
        await using var w = await TimedWorld.CreateAsync(fixture, "2026-03-27T16:30:00Z"); // last Friday of GMT; Monday 30 March is BST
        var calendar = await w.CalendarAsync();
        await w.PolicyAsync("Standard", calendar, firstResponse: null, resolution: 240);
        var ticket = await w.NewTicketAsync();
        Assert.Equal(DateTimeOffset.Parse("2026-03-30T11:30:00Z", System.Globalization.CultureInfo.InvariantCulture), TimedWorld.At(await w.SlaAsync(ticket.Id), "resolution", "dueAt"));
    }

    [Fact]
    public async Task Pending_pauses_the_resolution_clock_and_open_resumes_it_with_the_remaining_business_time()
    {
        await using var w = await TimedWorld.CreateAsync(fixture, "2026-01-12T10:00:00Z"); // Monday
        var calendar = await w.CalendarAsync();
        await w.PolicyAsync("Standard", calendar, firstResponse: null, resolution: 240, pause: true);
        var ticket = await w.NewTicketAsync();
        Assert.Equal(DateTimeOffset.Parse("2026-01-12T14:00:00Z", System.Globalization.CultureInfo.InvariantCulture), TimedWorld.At(await w.SlaAsync(ticket.Id), "resolution", "dueAt"));

        await w.GoToAsync("2026-01-12T11:00:00Z");
        await w.SetStatusAsync(ticket.Id, "Pending");
        var paused = await w.SlaAsync(ticket.Id);
        Assert.Equal("Paused", paused.GetProperty("resolution").GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, paused.GetProperty("resolution").GetProperty("dueAt").ValueKind);

        // Paused time never breaches: a monitor run days later records nothing.
        await w.GoToAsync("2026-01-14T09:00:00Z");
        await w.RunMonitorAsync();
        Assert.DoesNotContain((await w.SlaAsync(ticket.Id)).GetProperty("events").EnumerateArray(), e => e.GetProperty("kind").GetString() is "Breached" or "Approaching");

        await w.GoToAsync("2026-01-14T15:00:00Z"); // Wednesday: 3 hours remain -> 15:00-17:00 today, 09:00-10:00 Thursday
        await w.SetStatusAsync(ticket.Id, "Open");
        var resumed = await w.SlaAsync(ticket.Id);
        Assert.Equal(DateTimeOffset.Parse("2026-01-15T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture), TimedWorld.At(resumed, "resolution", "dueAt"));
        Assert.Equal(["Applied", "Paused", "Resumed"], resumed.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("kind").GetString()!).ToArray());
    }

    [Fact]
    public async Task Without_the_pause_setting_pending_keeps_the_clock_running()
    {
        await using var w = await TimedWorld.CreateAsync(fixture, "2026-01-12T10:00:00Z");
        await w.PolicyAsync("No pause", await w.CalendarAsync(), firstResponse: null, resolution: 240, pause: false);
        var ticket = await w.NewTicketAsync();
        await w.SetStatusAsync(ticket.Id, "Pending");
        var sla = await w.SlaAsync(ticket.Id);
        Assert.Equal("Running", sla.GetProperty("resolution").GetProperty("state").GetString());
        Assert.Equal(DateTimeOffset.Parse("2026-01-12T14:00:00Z", System.Globalization.CultureInfo.InvariantCulture), TimedWorld.At(sla, "resolution", "dueAt"));
    }

    [Fact]
    public async Task Only_the_first_public_agent_reply_satisfies_first_response_exactly_once()
    {
        await using var w = await TimedWorld.CreateAsync(fixture, "2026-01-12T10:00:00Z");
        await w.PolicyAsync("Standard", await w.CalendarAsync(), firstResponse: 60, resolution: null);
        var ticket = await w.NewTicketAsync();

        // An internal note, and the requester's own message, do not count.
        await w.GoToAsync("2026-01-12T10:10:00Z");
        (await w.Agent.PostMessageAsync(ticket.Id, "Internal", "thinking")).Expect(HttpStatusCode.Created);
        Assert.Equal(JsonValueKind.Null, (await w.SlaAsync(ticket.Id)).GetProperty("firstResponse").GetProperty("metAt").ValueKind);
        await using (var scope = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.CreateAsyncScope(fixture.Factory.Services))
        {
            var ingress = (Helpdesk.Modules.Ticketing.Contracts.ITicketIngress)scope.ServiceProvider.GetService(typeof(Helpdesk.Modules.Ticketing.Contracts.ITicketIngress))!;
            var requesterId = Guid.Parse((await fixture.ScalarAsync<string>("SELECT requester_id::text FROM ticketing.tickets WHERE id = @t", new NpgsqlParameter("t", ticket.Id)))!);
            await ingress.AddRequesterMessageAsync(w.Dept.Id, ticket.Id, requesterId, "Any news?", "manual");
        }

        Assert.Equal(JsonValueKind.Null, (await w.SlaAsync(ticket.Id)).GetProperty("firstResponse").GetProperty("metAt").ValueKind);

        await w.GoToAsync("2026-01-12T10:30:00Z");
        (await w.Agent.PostMessageAsync(ticket.Id, "Public", "Hello, looking into it.")).Expect(HttpStatusCode.Created);
        var met = await w.SlaAsync(ticket.Id);
        Assert.Equal(DateTimeOffset.Parse("2026-01-12T10:30:00Z", System.Globalization.CultureInfo.InvariantCulture), TimedWorld.At(met, "firstResponse", "metAt"));
        Assert.Equal("Met", met.GetProperty("firstResponse").GetProperty("state").GetString());

        await w.GoToAsync("2026-01-12T12:00:00Z");
        (await w.Agent.PostMessageAsync(ticket.Id, "Public", "A second reply.")).Expect(HttpStatusCode.Created);
        var again = await w.SlaAsync(ticket.Id);
        Assert.Equal(DateTimeOffset.Parse("2026-01-12T10:30:00Z", System.Globalization.CultureInfo.InvariantCulture), TimedWorld.At(again, "firstResponse", "metAt"));
        Assert.Single(again.GetProperty("events").EnumerateArray(), e => e.GetProperty("kind").GetString() == "FirstResponseMet");
    }

    [Fact]
    public async Task Resolved_and_closed_complete_resolution_late_ones_are_met_late_and_pending_never_completes()
    {
        await using var w = await TimedWorld.CreateAsync(fixture, "2026-01-12T10:00:00Z");
        await w.PolicyAsync("Standard", await w.CalendarAsync(), firstResponse: null, resolution: 120, pause: true);
        var inTime = await w.NewTicketAsync();
        var late = await w.NewTicketAsync();
        var viaClose = await w.NewTicketAsync();

        await w.GoToAsync("2026-01-12T10:30:00Z");
        await w.SetStatusAsync(inTime.Id, "Pending");
        Assert.NotEqual("Met", (await w.SlaAsync(inTime.Id)).GetProperty("resolution").GetProperty("state").GetString());
        await w.SetStatusAsync(inTime.Id, "Resolved");
        Assert.Equal("Met", (await w.SlaAsync(inTime.Id)).GetProperty("resolution").GetProperty("state").GetString());

        await w.SetStatusAsync(viaClose.Id, "Closed");
        Assert.Equal("Met", (await w.SlaAsync(viaClose.Id)).GetProperty("resolution").GetProperty("state").GetString());

        await w.GoToAsync("2026-01-12T13:00:00Z"); // past the 12:00 deadline
        await w.SetStatusAsync(late.Id, "Resolved");
        var sla = await w.SlaAsync(late.Id);
        Assert.Equal("MetLate", sla.GetProperty("resolution").GetProperty("state").GetString());
        Assert.Contains(sla.GetProperty("events").EnumerateArray(), e => e.GetProperty("kind").GetString() == "ResolutionMet" && e.GetProperty("detail").GetProperty("late").GetBoolean());
    }

    [Fact]
    public async Task Reopening_a_resolved_ticket_starts_a_new_resolution_cycle_and_keeps_first_response_met()
    {
        await using var w = await TimedWorld.CreateAsync(fixture, "2026-01-12T10:00:00Z");
        await w.PolicyAsync("Standard", await w.CalendarAsync(), firstResponse: 60, resolution: 120);
        var ticket = await w.NewTicketAsync();
        await w.GoToAsync("2026-01-12T10:20:00Z");
        (await w.Agent.PostMessageAsync(ticket.Id, "Public", "On it.")).Expect(HttpStatusCode.Created);
        await w.GoToAsync("2026-01-12T11:00:00Z");
        await w.SetStatusAsync(ticket.Id, "Resolved");
        Assert.Equal("Met", (await w.SlaAsync(ticket.Id)).GetProperty("resolution").GetProperty("state").GetString());

        await w.GoToAsync("2026-01-13T09:00:00Z");
        await w.SetStatusAsync(ticket.Id, "Open");
        var reopened = await w.SlaAsync(ticket.Id);
        Assert.Equal(2, reopened.GetProperty("resolution").GetProperty("cycle").GetInt32());
        Assert.Equal("Running", reopened.GetProperty("resolution").GetProperty("state").GetString());
        Assert.Equal(DateTimeOffset.Parse("2026-01-13T11:00:00Z", System.Globalization.CultureInfo.InvariantCulture), TimedWorld.At(reopened, "resolution", "dueAt")); // full target from the reopening
        Assert.Equal("Met", reopened.GetProperty("firstResponse").GetProperty("state").GetString());
        Assert.Contains(reopened.GetProperty("events").EnumerateArray(), e => e.GetProperty("kind").GetString() == "Reopened");

        // The second cycle can be met again.
        await w.GoToAsync("2026-01-13T09:30:00Z");
        await w.SetStatusAsync(ticket.Id, "Closed");
        Assert.Equal("Met", (await w.SlaAsync(ticket.Id)).GetProperty("resolution").GetProperty("state").GetString());
    }

    [Fact]
    public async Task Editing_a_policy_never_rewrites_the_deadlines_of_existing_tickets()
    {
        await using var w = await TimedWorld.CreateAsync(fixture, "2026-01-12T10:00:00Z");
        var calendar = await w.CalendarAsync();
        var policy = await w.PolicyAsync("Standard", calendar, firstResponse: null, resolution: 240);
        var before = await w.NewTicketAsync();

        (await w.Admin.Client.PatchAsync($"{w.Base}/sla-policies/{policy.Id}", new { version = policy.Version, resolutionMinutes = 30, name = "Rushed" })).Expect(HttpStatusCode.OK);
        // The calendar is edited too (Monday closed): old tickets still carry the calendar they were issued with.
        var cal = (await w.Admin.Client.GetAsync($"{w.Base}/business-hours/{calendar}")).Expect(HttpStatusCode.OK);
        (await w.Admin.Client.PatchAsync($"{w.Base}/business-hours/{calendar}", new { version = cal.Version, exceptions = new[] { new { date = "2026-01-12", name = "Closed", intervals = Array.Empty<object>() } } })).Expect(HttpStatusCode.OK);

        var old = await w.SlaAsync(before.Id);
        Assert.Equal("Standard", old.GetProperty("policyName").GetString());
        Assert.Equal(DateTimeOffset.Parse("2026-01-12T14:00:00Z", System.Globalization.CultureInfo.InvariantCulture), TimedWorld.At(old, "resolution", "dueAt"));
        Assert.Equal(240, old.GetProperty("resolution").GetProperty("targetMinutes").GetInt32());

        var fresh = await w.NewTicketAsync();
        var created = await w.SlaAsync(fresh.Id);
        Assert.Equal("Rushed", created.GetProperty("policyName").GetString());
        Assert.Equal(DateTimeOffset.Parse("2026-01-13T09:30:00Z", System.Globalization.CultureInfo.InvariantCulture), TimedWorld.At(created, "resolution", "dueAt")); // Monday closed now
    }

    [Fact]
    public async Task The_first_active_matching_policy_by_explicit_order_wins_and_policies_are_never_combined()
    {
        await using var w = await TimedWorld.CreateAsync(fixture, "2026-01-12T10:00:00Z");
        var calendar = await w.CalendarAsync();
        var urgent = await w.PolicyAsync("Urgent", calendar, null, 60, conditions: new[] { new { field = "priority", @operator = "in", values = new[] { "Urgent" } } });
        var general = await w.PolicyAsync("General", calendar, null, 480);
        var inactive = await w.PolicyAsync("Disabled", calendar, null, 5);
        (await w.Admin.Client.PatchAsync($"{w.Base}/sla-policies/{inactive.Id}", new { version = inactive.Version, isActive = false })).Expect(HttpStatusCode.OK);

        var normal = await w.NewTicketAsync(priority: "Normal");
        var rush = await w.NewTicketAsync(priority: "Urgent");
        Assert.Equal("General", (await w.SlaAsync(normal.Id)).GetProperty("policyName").GetString());
        Assert.Equal("Urgent", (await w.SlaAsync(rush.Id)).GetProperty("policyName").GetString());

        // Reordering changes which one wins: General first now matches everything.
        (await w.Admin.Client.PutAsync($"{w.Base}/sla-policies/order", new { ids = new[] { general.Id, urgent.Id, inactive.Id } })).Expect(HttpStatusCode.OK);
        var after = await w.NewTicketAsync(priority: "Urgent");
        Assert.Equal("General", (await w.SlaAsync(after.Id)).GetProperty("policyName").GetString());
        (await w.Admin.Client.PutAsync($"{w.Base}/sla-policies/order", new { ids = new[] { general.Id } })).Expect(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task The_monitor_records_approaching_and_breached_once_and_queue_filters_counts_and_sorting_follow()
    {
        await using var w = await TimedWorld.CreateAsync(fixture, "2026-01-12T09:00:00Z");
        await w.PolicyAsync("Fast", await w.CalendarAsync(), firstResponse: null, resolution: 120, lead: 30);
        var near = await w.NewTicketAsync(subject: "near");
        await w.GoToAsync("2026-01-12T09:30:00Z");
        var later = await w.NewTicketAsync(subject: "later"); // due 11:30
        await w.NewTicketAsync(subject: "plain");

        // 10:45: `near` (due 11:00) is inside its 30 minute warning window; nothing is breached yet.
        await w.GoToAsync("2026-01-12T10:45:00Z");
        await w.RunMonitorAsync();
        var approaching = (await w.Agent.Client.GetAsync("/api/tickets?sla=approaching")).Expect(HttpStatusCode.OK).Json.GetProperty("items");
        Assert.Contains(approaching.EnumerateArray(), i => i.GetProperty("subject").GetString() == "near");
        Assert.DoesNotContain(approaching.EnumerateArray(), i => i.GetProperty("subject").GetString() == "later");
        var duesoon = (await w.Agent.Client.GetAsync("/api/tickets?sla=duesoon")).Expect(HttpStatusCode.OK).Json.GetProperty("items");
        Assert.Equal(["near", "later"], [.. duesoon.EnumerateArray().Select(i => i.GetProperty("subject").GetString()!).Where(s => s is "near" or "later").Order(StringComparer.Ordinal).Reverse()]);

        await w.GoToAsync("2026-01-12T11:15:00Z");
        await w.RunMonitorAsync();
        var breached = (await w.Agent.Client.GetAsync("/api/tickets?sla=breached")).Expect(HttpStatusCode.OK).Json.GetProperty("items");
        Assert.Equal(["near"], [.. breached.EnumerateArray().Select(i => i.GetProperty("subject").GetString()!)]);
        Assert.Equal("Breached", breached[0].GetProperty("sla").GetProperty("state").GetString());
        var counts = (await w.Agent.Client.GetAsync("/api/tickets/queue-counts")).Expect(HttpStatusCode.OK).Json;
        Assert.Equal(1, counts.GetProperty("slaBreached").GetInt32());

        // A repeated run records nothing new: one Approaching and one Breached event for `near`.
        await w.RunMonitorAsync();
        var events = (await w.SlaAsync(near.Id)).GetProperty("events").EnumerateArray().Select(e => e.GetProperty("kind").GetString()).ToArray();
        Assert.Equal(1, events.Count(k => k == "Approaching"));
        Assert.Equal(1, events.Count(k => k == "Breached"));

        // Sorted by SLA: only tickets with a running deadline, soonest first.
        var sorted = (await w.Agent.Client.GetAsync("/api/tickets?sort=sla_due_asc")).Expect(HttpStatusCode.OK).Json.GetProperty("items");
        Assert.Equal("near", sorted[0].GetProperty("subject").GetString());
        Assert.Equal(3, sorted.GetArrayLength());
        Assert.All(sorted.EnumerateArray(), i => Assert.NotEqual(System.Text.Json.JsonValueKind.Null, i.GetProperty("sla").ValueKind));
    }

    [Fact]
    public async Task Sla_data_of_another_department_is_invisible_in_lists_counts_filters_and_detail()
    {
        await using var a = await TimedWorld.CreateAsync(fixture, "2026-01-12T09:00:00Z");
        await using var b = await TimedWorld.CreateAsync(fixture, "2026-01-12T09:00:00Z");
        await a.PolicyAsync("Fast", await a.CalendarAsync(), null, 30);
        await b.PolicyAsync("Fast", await b.CalendarAsync(), null, 30);
        var ticketB = await b.NewTicketAsync(subject: "B only");
        await a.GoToAsync("2026-01-12T12:00:00Z");

        var items = (await a.Agent.Client.GetAsync("/api/tickets?sla=breached&limit=100")).Expect(HttpStatusCode.OK).Json.GetProperty("items");
        Assert.DoesNotContain(items.EnumerateArray(), i => i.GetProperty("id").GetGuid() == ticketB.Id);
        (await a.Agent.Client.GetAsync($"/api/tickets/{ticketB.Id}/sla")).Expect(HttpStatusCode.NotFound);

        // Counts only reflect what the caller can read: a user of A sees A's own breached tickets only.
        await a.NewTicketAsync(subject: "A own");
        await a.GoToAsync("2026-01-12T13:00:00Z");
        Assert.Equal(1, await CountVisibleBreachedAsync(a));

        // Configuration of B is invisible to A's administrator (404) and to A's non-administrators (403 in A, 404 in B).
        (await a.Admin.Client.GetAsync($"/api/departments/{b.Dept.Id}/sla-policies")).Expect(HttpStatusCode.NotFound);
        (await a.Admin.Client.GetAsync($"/api/departments/{b.Dept.Id}/business-hours")).Expect(HttpStatusCode.NotFound);
        (await a.Agent.Client.GetAsync($"{a.Base}/sla-policies")).Expect(HttpStatusCode.Forbidden);
        (await a.Viewer.Client.GetAsync($"{a.Base}/business-hours")).Expect(HttpStatusCode.Forbidden);
    }

    private static async Task<int> CountVisibleBreachedAsync(TimedWorld w) =>
        (await w.Agent.Client.GetAsync("/api/tickets/queue-counts")).Expect(HttpStatusCode.OK).Json.GetProperty("slaBreached").GetInt32();

    [Fact]
    public async Task Business_hours_and_policy_input_is_validated()
    {
        await using var w = await TimedWorld.CreateAsync(fixture, "2026-01-12T09:00:00Z");
        var bad = new[] { new { day = "Monday", intervals = new[] { new { start = "09:00", end = "12:00" }, new { start = "11:00", end = "13:00" } } } };
        (await w.Admin.Client.PostAsync($"{w.Base}/business-hours", new { name = "X", timeZone = "Europe/London", weekly = bad })).Expect(HttpStatusCode.BadRequest);
        (await w.Admin.Client.PostAsync($"{w.Base}/business-hours", new { name = "X", timeZone = "Mars/Base", weekly = TimedWorld.Weekdays() })).Expect(HttpStatusCode.BadRequest);
        (await w.Admin.Client.PostAsync($"{w.Base}/business-hours", new { name = "X", timeZone = "UTC", weekly = Array.Empty<object>() })).Expect(HttpStatusCode.BadRequest);
        await w.CalendarAsync("Dup");
        (await w.Admin.Client.PostAsync($"{w.Base}/business-hours", new { name = "Dup", timeZone = "UTC", weekly = TimedWorld.Weekdays() })).Expect(HttpStatusCode.Conflict);

        (await w.Admin.Client.PostAsync($"{w.Base}/sla-policies", new { name = "No target" })).Expect(HttpStatusCode.BadRequest);
        (await w.Admin.Client.PostAsync($"{w.Base}/sla-policies", new { name = "Zero", resolutionMinutes = 0 })).Expect(HttpStatusCode.BadRequest);
        (await w.Admin.Client.PostAsync($"{w.Base}/sla-policies", new { name = "Regex", resolutionMinutes = 10, conditions = new[] { new { field = "subjectContains", @operator = "matches", values = new[] { ".*" } } } })).Expect(HttpStatusCode.BadRequest);
        (await w.Admin.Client.PostAsync($"{w.Base}/sla-policies", new { name = "Status", resolutionMinutes = 10, conditions = new[] { new { field = "status", @operator = "in", values = new[] { "Open" } } } })).Expect(HttpStatusCode.BadRequest);
        (await w.Admin.Client.PostAsync($"{w.Base}/sla-policies", new { name = "Foreign calendar", resolutionMinutes = 10, calendarId = Guid.NewGuid() })).Expect(HttpStatusCode.BadRequest);

        var dueCheck = (await w.Admin.Client.PostAsync($"{w.Base}/business-hours/{await w.CalendarAsync("Check")}/due", new { start = "2026-01-09T16:30:00Z", minutes = 240 })).Expect(HttpStatusCode.OK);
        Assert.Equal(DateTimeOffset.Parse("2026-01-12T12:30:00Z", System.Globalization.CultureInfo.InvariantCulture), TimedWorld.At(dueCheck.Json, "dueAt"));
    }

    [Fact]
    public async Task Ticket_changes_through_every_path_keep_the_clocks_consistent_bulk_status_resolves_the_sla()
    {
        await using var w = await TimedWorld.CreateAsync(fixture, "2026-01-12T10:00:00Z");
        await w.PolicyAsync("Standard", await w.CalendarAsync(), firstResponse: null, resolution: 240);
        var ticket = await w.NewTicketAsync();
        await w.GoToAsync("2026-01-12T11:00:00Z");
        (await w.Lead.Client.PostAsync("/api/tickets/bulk", new { ticketIds = new[] { ticket.Id }, status = "Resolved" })).Expect(HttpStatusCode.OK);
        Assert.Equal("Met", (await w.SlaAsync(ticket.Id)).GetProperty("resolution").GetProperty("state").GetString());
        var detail = await w.TicketAsync(ticket.Id);
        Assert.Equal("Met", detail.Json.GetProperty("sla").GetProperty("state").GetString());
    }
}
