#pragma warning disable CA1861 // test payloads are one-off literals
using System.Globalization;
using System.Net;
using System.Text.Json;
using Helpdesk.Host.Tests.Infrastructure;
using Helpdesk.Host.Tests.Ticketing;

namespace Helpdesk.Host.Tests.Sla;

/// <summary>SLA lifecycle rules (WS-12): policy re-evaluation (D17), first response on resolve/close without a reply (D18), the SLA sort (BE-008).</summary>
public sealed class SlaLifecycleTests(HelpdeskFixture fixture) : IClassFixture<HelpdeskFixture>
{
    private const string Start = "2026-01-12T10:00:00Z";

    private static DateTimeOffset T(string iso) => DateTimeOffset.Parse(iso, CultureInfo.InvariantCulture);

    private static string[] Kinds(JsonElement sla) => [.. sla.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("kind").GetString()!)];

    [Fact]
    public async Task A_priority_change_selects_the_policy_that_now_applies_counted_from_creation_and_an_exceeded_target_is_shown_as_exceeded()
    {
        await using var w = await TimedWorld.CreateAsync(fixture, Start);
        await w.PolicyAsync("Urgent", null, 30, null, conditions: new[] { new { field = "priority", @operator = "in", values = new[] { "Urgent" } } });
        await w.PolicyAsync("Standard", null, 240, null);
        var ticket = await w.NewTicketAsync();
        Assert.Equal(T("2026-01-12T14:00:00Z"), TimedWorld.At(await w.SlaAsync(ticket.Id), "firstResponse", "dueAt"));

        await w.GoToAsync("2026-01-12T10:20:00Z");
        await w.SetPriorityAsync(ticket.Id, "Urgent");
        var changed = await w.SlaAsync(ticket.Id);
        Assert.Equal(T("2026-01-12T10:30:00Z"), TimedWorld.At(changed, "firstResponse", "dueAt")); // 30 minutes from CREATION (10:00), not from the change
        Assert.Equal("Urgent", changed.GetProperty("policyName").GetString());
        Assert.Contains("PolicyChanged", Kinds(changed));

        // The deadline of the new policy has passed by now: the truth is a breach, not a fresh clock.
        await w.GoToAsync("2026-01-12T10:45:00Z");
        await w.RunMonitorAsync();
        Assert.Equal("Breached", (await w.SlaAsync(ticket.Id)).GetProperty("firstResponse").GetProperty("state").GetString());
    }

    [Fact]
    public async Task A_team_set_by_automation_after_creation_selects_the_team_policy()
    {
        await using var w = await TimedWorld.CreateAsync(fixture, Start);
        var team = await fixture.CreateTeamAsync(w.Dept.Department);
        await w.PolicyAsync("Network team", null, 60, null, conditions: new[] { new { field = "team", @operator = "in", values = new[] { team.Id.ToString() } } });
        await w.PolicyAsync("Default", null, 480, null);
        (await w.Admin.Client.PostAsync($"{w.Base}/automations", new
        {
            name = "Route", trigger = "TicketCreated", isActive = true, stopProcessing = false, conditions = Array.Empty<object>(),
            actions = new object[] { new { type = "assignTeam", value = team.Id.ToString() } },
        })).Expect(HttpStatusCode.Created);

        var ticket = await w.NewTicketAsync();

        var sla = await w.SlaAsync(ticket.Id);
        Assert.Equal("Network team", sla.GetProperty("policyName").GetString());
        Assert.Equal(T("2026-01-12T11:00:00Z"), TimedWorld.At(sla, "firstResponse", "dueAt"));
    }

    [Fact]
    public async Task Resolving_without_a_public_reply_makes_first_response_not_applicable_and_nothing_breaches_afterwards()
    {
        await using var w = await TimedWorld.CreateAsync(fixture, Start);
        await w.PolicyAsync("Standard", null, 60, null);
        var ticket = await w.NewTicketAsync();

        await w.GoToAsync("2026-01-12T10:10:00Z");
        await w.SetStatusAsync(ticket.Id, "Resolved");
        var resolved = await w.SlaAsync(ticket.Id);
        Assert.Equal("NotApplicable", resolved.GetProperty("firstResponse").GetProperty("state").GetString());
        Assert.Contains("FirstResponseNotApplicable", Kinds(resolved));

        await w.GoToAsync("2026-01-12T14:00:00Z");
        await w.RunMonitorAsync();
        Assert.DoesNotContain(Kinds(await w.SlaAsync(ticket.Id)), k => k is "Breached" or "Approaching");

        // Terminal: a reply after a reopen does not turn it into Met.
        await w.SetStatusAsync(ticket.Id, "Open");
        (await w.Agent.PostMessageAsync(ticket.Id, "Public", "Back again")).Expect(HttpStatusCode.Created);
        var after = await w.SlaAsync(ticket.Id);
        Assert.Equal("NotApplicable", after.GetProperty("firstResponse").GetProperty("state").GetString());
        Assert.DoesNotContain("FirstResponseMet", Kinds(after));
    }

    [Fact]
    public async Task Sorting_by_sla_deadline_keeps_tickets_without_a_deadline_and_lists_them_last()
    {
        await using var w = await TimedWorld.CreateAsync(fixture, Start);
        var plain = await w.NewTicketAsync("No policy yet");
        await w.PolicyAsync("Standard", null, 60, null);
        var timed = await w.NewTicketAsync("With a deadline");

        var items = (await w.Lead.Client.GetAsync($"/api/tickets?queue=all&departmentId={w.Dept.Department.Id}&sort=sla_due_asc&limit=50")).Expect(HttpStatusCode.OK).Json.GetProperty("items")
            .EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToList();
        Assert.Contains(plain.Id, items);
        Assert.True(items.IndexOf(timed.Id) < items.IndexOf(plain.Id));

        // Paging through the sentinel works: one item per page reaches both.
        var seen = new List<Guid>();
        string? cursor = null;
        for (var page = 0; page < 5 && (page == 0 || cursor is not null); page++)
        {
            var json = (await w.Lead.Client.GetAsync($"/api/tickets?queue=all&departmentId={w.Dept.Department.Id}&sort=sla_due_asc&limit=1" + (cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}"))).Expect(HttpStatusCode.OK).Json;
            seen.AddRange(json.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()));
            cursor = json.TryGetProperty("nextCursor", out var next) && next.ValueKind == JsonValueKind.String ? next.GetString() : null;
        }

        Assert.Equal([timed.Id, plain.Id], seen);
    }
}
