using System.Net;
using System.Text.Json;
using Helpdesk.Host.Tests.Infrastructure;

namespace Helpdesk.Host.Tests.Ticketing;

/// <summary>The historical report suite: every figure agrees with the tickets listed behind it, definitions hold, and another department's data never appears.</summary>
public sealed class ReportingTests(HelpdeskFixture fixture) : IClassFixture<HelpdeskFixture>
{
    private static string Today => TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Europe/London")).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    private static string Url(Dept d, string kind, string extra = "") => $"/api/departments/{d.Id}/reports/{kind}?from={Today}&to={Today}&group=day{extra}";

    private static double Kpi(JsonElement report, string key) =>
        report.GetProperty("kpis").EnumerateArray().Single(k => k.GetProperty("key").GetString() == key).GetProperty("value").GetDouble();

    private static JsonElement Series(JsonElement report, string key) => report.GetProperty("series").EnumerateArray().Single(s => s.GetProperty("key").GetString() == key);

    private static double[] Line(JsonElement series, string key) =>
        [.. series.GetProperty("lines").EnumerateArray().Single(l => l.GetProperty("key").GetString() == key).GetProperty("values").EnumerateArray().Select(v => v.ValueKind == JsonValueKind.Null ? double.NaN : v.GetDouble())];

    private async Task<(Dept D, ApiResponse Answered, ApiResponse Resolved, ApiResponse Waiting)> SeedAsync()
    {
        var d = await fixture.NewDeptAsync();
        var answered = await d.Agent.CreateTicketAsync(d.Department, TicketHelpers.NewTicketBody("Answered", extra: new { Priority = "High" }));
        var resolved = await d.Agent.CreateTicketAsync(d.Department, TicketHelpers.NewTicketBody("Resolved", extra: new { Priority = "Low" }));
        var waiting = await d.Lead.CreateTicketAsync(d.Department, TicketHelpers.NewTicketBody("Waiting", extra: new { AssigneeUserId = d.Agent.Id }));
        (await d.Agent.PostMessageAsync(answered.Id, "Public", "We are on it")).Expect(HttpStatusCode.Created);
        (await d.Agent.PostMessageAsync(answered.Id, "Internal", "note only")).Expect(HttpStatusCode.Created);
        (await d.Agent.PatchTicketAsync(resolved, new { Status = "Resolved" })).Expect(HttpStatusCode.OK);
        (await d.Agent.PatchTicketAsync(waiting, new { Status = "Pending" })).Expect(HttpStatusCode.OK);
        return (d, answered, resolved, waiting);
    }

    [Fact]
    public async Task Every_report_answers_with_numbers_that_agree_with_the_tickets_behind_them()
    {
        var (d, answered, resolved, waiting) = await SeedAsync();

        var volume = (await d.Agent.Client.GetAsync(Url(d, "ticket-volume"))).Expect(HttpStatusCode.OK).Json;
        Assert.Equal(3, Kpi(volume, "received"));
        Assert.Equal(1, Kpi(volume, "resolved"));
        Assert.Equal(3, Line(Series(volume, "volume"), "received").Sum());
        Assert.Equal(1, Line(Series(volume, "priority"), "high").Sum()); // one High and one Low were raised
        Assert.Equal(1, Line(Series(volume, "priority"), "low").Sum());

        var backlog = (await d.Agent.Client.GetAsync(Url(d, "backlog"))).Expect(HttpStatusCode.OK).Json;
        Assert.Equal(2, Kpi(backlog, "unresolved")); // answered (Open) + waiting (Pending); the resolved one is not backlog
        Assert.Equal(1, Kpi(backlog, "unassigned")); // "answered" has no assignee
        Assert.Equal(1, Line(Series(backlog, "over-time"), "pending").Last());

        var unresolvedBehind = (await d.Agent.Client.GetAsync($"/api/departments/{d.Id}/reports/tickets?metric=backlog&from={Today}&to={Today}&group=day")).Expect(HttpStatusCode.OK).Json;
        Assert.Equal(2, unresolvedBehind.GetProperty("total").GetInt32());
        Assert.Contains(unresolvedBehind.GetProperty("items").EnumerateArray(), i => i.GetProperty("id").GetGuid() == waiting.Id);
        Assert.DoesNotContain(unresolvedBehind.GetProperty("items").EnumerateArray(), i => i.GetProperty("id").GetGuid() == resolved.Id);

        var responses = (await d.Agent.Client.GetAsync(Url(d, "response-times"))).Expect(HttpStatusCode.OK).Json;
        Assert.True(Kpi(responses, "frMedian") >= 0); // exactly one ticket was answered by a human; the internal note and the creation did not count
        var table = responses.GetProperty("tables").EnumerateArray().Single(t => t.GetProperty("key").GetString() == "priority");
        Assert.Equal(1, table.GetProperty("rows").EnumerateArray().Sum(r => r[1].ValueKind == JsonValueKind.Number ? r[1].GetInt32() : 0));

        var lifecycle = (await d.Agent.Client.GetAsync(Url(d, "lifecycle"))).Expect(HttpStatusCode.OK).Json;
        Assert.Equal(3, Kpi(lifecycle, "created"));
        Assert.Equal(1, Kpi(lifecycle, "resolved"));
        Assert.Equal(0, Kpi(lifecycle, "reopened"));

        var workload = (await d.Agent.Client.GetAsync(Url(d, "workload"))).Expect(HttpStatusCode.OK).Json;
        Assert.Equal(2, Kpi(workload, "backlog"));
        var rows = workload.GetProperty("tables")[0].GetProperty("rows").EnumerateArray().ToList();
        Assert.Equal("Unassigned", rows[^1][0].GetString());
        Assert.Equal(1, rows[^1][3].GetInt32());

        foreach (var kind in new[] { "support-overview", "sla", "email" })
        {
            var report = (await d.Agent.Client.GetAsync(Url(d, kind))).Expect(HttpStatusCode.OK).Json;
            Assert.Equal(kind, report.GetProperty("report").GetString());
            Assert.True(report.GetProperty("kpis").GetArrayLength() > 0);
        }

        _ = answered;
    }

    [Fact]
    public async Task A_reopened_ticket_is_counted_as_reopened_and_back_in_the_backlog()
    {
        var d = await fixture.NewDeptAsync();
        var t = await d.Agent.CreateTicketAsync(d.Department);
        var done = (await d.Agent.PatchTicketAsync(t, new { Status = "Resolved" })).Expect(HttpStatusCode.OK);
        (await d.Agent.PatchTicketAsync(done, new { Status = "Open" })).Expect(HttpStatusCode.OK);

        var lifecycle = (await d.Agent.Client.GetAsync(Url(d, "lifecycle"))).Expect(HttpStatusCode.OK).Json;
        Assert.Equal(1, Kpi(lifecycle, "reopened"));
        var backlog = (await d.Agent.Client.GetAsync(Url(d, "backlog"))).Expect(HttpStatusCode.OK).Json;
        Assert.Equal(1, Kpi(backlog, "unresolved"));
    }

    [Fact]
    public async Task Filters_narrow_the_set_and_a_bad_request_is_refused_without_guessing()
    {
        var (d, _, _, _) = await SeedAsync();
        var onlyHigh = (await d.Agent.Client.GetAsync(Url(d, "ticket-volume", "&priority=High"))).Expect(HttpStatusCode.OK).Json;
        Assert.Equal(1, Kpi(onlyHigh, "received"));
        var unassigned = (await d.Agent.Client.GetAsync(Url(d, "ticket-volume", "&unassigned=true"))).Expect(HttpStatusCode.OK).Json;
        Assert.Equal(2, Kpi(unassigned, "received"));

        (await d.Agent.Client.GetAsync($"/api/departments/{d.Id}/reports/ticket-volume?from=2026-01-10&to=2026-01-01")).Expect(HttpStatusCode.BadRequest);
        (await d.Agent.Client.GetAsync($"/api/departments/{d.Id}/reports/ticket-volume?from=2025-01-01&to=2026-12-31")).Expect(HttpStatusCode.BadRequest); // longer than a year
        (await d.Agent.Client.GetAsync($"/api/departments/{d.Id}/reports/ticket-volume?from=2026-01-01&to=2026-06-30&group=day")).Expect(HttpStatusCode.BadRequest); // too many daily buckets
        (await d.Agent.Client.GetAsync(Url(d, "ticket-volume", "&priority=Huge"))).Expect(HttpStatusCode.BadRequest);
        (await d.Agent.Client.GetAsync(Url(d, "nonsense"))).Expect(HttpStatusCode.NotFound);
        (await d.Agent.Client.GetAsync($"/api/departments/{d.Id}/reports/tickets?metric=nonsense")).Expect(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Reports_and_drill_downs_never_cross_departments_or_work_without_ticket_access()
    {
        var (a, _, _, _) = await SeedAsync();
        var b = await fixture.NewDeptAsync();
        var outsider = b.Agent;
        foreach (var kind in ReportKinds)
        {
            (await outsider.Client.GetAsync(Url(a, kind))).Expect(HttpStatusCode.NotFound);
        }

        (await outsider.Client.GetAsync($"/api/departments/{a.Id}/reports/tickets?metric=backlog")).Expect(HttpStatusCode.NotFound);
        var platform = await fixture.PlatformAdminAsync();
        (await platform.Client.GetAsync(Url(a, "ticket-volume"))).Expect(HttpStatusCode.OK); // S-02: the platform administrator reports on every department
        (await (await fixture.NewUserAsync()).Client.GetAsync(Url(a, "backlog"))).Expect(HttpStatusCode.NotFound);

        // B's own report holds none of A's tickets.
        var own = (await b.Agent.Client.GetAsync(Url(b, "ticket-volume"))).Expect(HttpStatusCode.OK).Json;
        Assert.Equal(0, Kpi(own, "received"));
        (await fixture.CreateAnonymousClient().GetAsync(Url(a, "backlog"))).Expect(HttpStatusCode.Unauthorized);
    }

    private static readonly string[] ReportKinds = ["support-overview", "ticket-volume", "sla", "response-times", "lifecycle", "backlog", "workload", "email"];
}
