using System.Net;
using System.Text.Json;
using Helpdesk.Host.Tests.Infrastructure;
using Helpdesk.Host.Tests.Sla;

namespace Helpdesk.Host.Tests.Ticketing;

/// <summary>Historical volume for ONE department: received and resolved counts, merged work kept out of resolutions, and no cross-department visibility.</summary>
public sealed class DepartmentVolumeReportTests(HelpdeskFixture fixture) : IClassFixture<HelpdeskFixture>
{
    private static async Task<JsonElement> VolumeAsync(TestUser user, DepartmentRef d, string query) => (await user.Client.GetAsync($"{d.Url}/reports/volume{query}")).Expect(HttpStatusCode.OK).Json;

    [Fact]
    public async Task Received_and_resolved_follow_the_period_exclude_merged_sources_from_resolutions_and_count_a_reopened_ticket_once()
    {
        await using var w = await TimedWorld.CreateAsync(fixture, "2026-01-12T10:00:00Z");
        var a = await w.NewTicketAsync("A");
        var b = await w.NewTicketAsync("B");
        var c = await w.NewTicketAsync("C");
        (await w.Lead.Client.PostAsync($"/api/tickets/{a.Id}/merge", new
        {
            version = (await w.TicketAsync(a.Id)).Version,
            sources = new[] { new { ticketId = c.Id, version = (await w.TicketAsync(c.Id)).Version } },
        })).Expect(HttpStatusCode.OK);
        await w.GoToAsync("2026-01-13T10:00:00Z");
        await w.SetStatusAsync(b.Id, "Resolved");
        await w.SetStatusAsync(b.Id, "Open");
        await w.SetStatusAsync(b.Id, "Resolved");
        await w.SetStatusAsync(a.Id, "Closed");

        var d = w.Dept.Department;
        var monday = await VolumeAsync(w.Agent, d, "?from=2026-01-12&to=2026-01-12");
        Assert.Equal((3, 0), (monday.GetProperty("received").GetInt32(), monday.GetProperty("resolved").GetInt32())); // the merged source was still raised
        var tuesday = await VolumeAsync(w.Agent, d, "?from=2026-01-13&to=2026-01-13");
        Assert.Equal((0, 2), (tuesday.GetProperty("received").GetInt32(), tuesday.GetProperty("resolved").GetInt32())); // B once despite the reopen, A closed from open
        var both = await VolumeAsync(w.Agent, d, "?from=2026-01-12&to=2026-01-13");
        Assert.Equal((3, 2), (both.GetProperty("received").GetInt32(), both.GetProperty("resolved").GetInt32()));
        Assert.False(both.TryGetProperty("teams", out _)); // counts only, never grouped by team or person

        // The per-day series (for charts) adds up to the totals and puts each ticket on its own London day.
        var days = both.GetProperty("daily").EnumerateArray().Select(x => (x.Str("date"), x.GetProperty("received").GetInt32(), x.GetProperty("resolved").GetInt32())).ToArray();
        Assert.Equal([("2026-01-12", 3, 0), ("2026-01-13", 0, 2)], days);
        var longer = await VolumeAsync(w.Agent, d, "?from=2025-06-01&to=2026-01-13");
        Assert.Equal(0, longer.GetProperty("daily").GetArrayLength()); // over 62 days: totals only, never a truncated series
        Assert.Equal(3, longer.GetProperty("received").GetInt32());

        (await w.Agent.Client.GetAsync($"{d.Url}/reports/volume?from=2026-01-14&to=2026-01-12")).Expect(HttpStatusCode.BadRequest);
        (await w.Agent.Client.GetAsync($"{d.Url}/reports/volume?from=junk")).Expect(HttpStatusCode.BadRequest);
        (await w.Agent.Client.GetAsync($"{d.Url}/reports/volume?from=2024-01-01&to=2026-01-12")).Expect(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Another_department_sees_nothing_and_the_platform_administrator_sees_every_department()
    {
        var it = await fixture.NewDeptAsync();
        var hr = await fixture.NewDeptAsync();
        await hr.Agent.CreateTicketAsync(hr.Department, TicketHelpers.NewTicketBody($"HR {Unique.Id()}"));
        Assert.Equal(0, (await VolumeAsync(it.Agent, it.Department, "")).GetProperty("received").GetInt32());
        Assert.Equal(1, (await VolumeAsync(hr.Agent, hr.Department, "")).GetProperty("received").GetInt32());
        (await hr.Admin.Client.GetAsync($"{it.Department.Url}/reports/volume")).Expect(HttpStatusCode.NotFound);
        (await (await fixture.PlatformAdminAsync()).Client.GetAsync($"{it.Department.Url}/reports/volume")).Expect(HttpStatusCode.OK); // S-02: global access
    }
}
