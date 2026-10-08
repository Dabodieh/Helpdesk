namespace Helpdesk.Modules.Ticketing.Services.Reporting;

/// <summary>
/// One response shape for every historical report: headline figures, chart series and tables. The client renders what it is given, so a report can only show
/// numbers the server computed; anything the recorded data cannot support is named in <see cref="ReportDto.Unavailable"/> instead of being estimated.
/// </summary>
internal sealed record ReportDto(
    string Report,
    ReportPeriodDto Period,
    IReadOnlyList<ReportKpi> Kpis,
    IReadOnlyList<ReportSeries> Series,
    IReadOnlyList<ReportTable> Tables,
    IReadOnlyList<string> Notes,
    IReadOnlyList<string> Unavailable);

internal sealed record ReportPeriodDto(DateOnly From, DateOnly To, string Group, string TimeZone, DateTimeOffset GeneratedAt);

/// <param name="Unit">count | percent | minutes | hours</param>
/// <param name="Previous">The same figure for the previous period of equal length, when the report compares periods.</param>
/// <param name="Drill">Metric that lists the tickets behind this figure (see <c>/reports/tickets</c>).</param>
/// <param name="Tone">neutral | good | bad | warn, so the client can colour without knowing the metric.</param>
internal sealed record ReportKpi(string Key, string Label, double? Value, string Unit, double? Previous = null, string? Drill = null, string? Note = null, string Tone = "neutral");

/// <param name="Kind">line | area | bars | stacked | hbars (hbars and stacked also work for categories)</param>
/// <param name="BucketType">date (ISO bucket start dates) or category</param>
internal sealed record ReportSeries(string Key, string Title, string Kind, string Unit, string BucketType, IReadOnlyList<string> Buckets, IReadOnlyList<ReportLine> Lines, string? Subtitle = null);

/// <param name="Drill">Metric behind a clicked point (the bucket comes from the point's date).</param>
/// <param name="Tone">neutral | good | bad | warn | info | muted, a hint for the colour of the series.</param>
internal sealed record ReportLine(string Key, string Label, IReadOnlyList<double?> Values, string? Drill = null, string Tone = "neutral");

/// <param name="Type">text | number | percent | minutes | hours | date</param>
internal sealed record ReportColumn(string Key, string Label, string Type);

internal sealed record ReportTable(string Key, string Title, IReadOnlyList<ReportColumn> Columns, IReadOnlyList<IReadOnlyList<object?>> Rows, string? Subtitle = null);

/// <summary>The filters every report accepts. Status, assignee and priority narrow the ticket set before anything is counted.</summary>
internal sealed record ReportQuery(string? From, string? To, string? Group, string[]? Priority, string[]? Status, Guid? Assignee, bool? Unassigned);

internal sealed record ReportTicketRow(Guid Id, string Reference, string Subject, string Status, string Priority, string? Assignee, DateTimeOffset CreatedAt, DateTimeOffset? ResolvedAt, int AgeDays);

internal sealed record ReportTicketsDto(string Metric, DateOnly From, DateOnly To, int Total, bool Truncated, IReadOnlyList<ReportTicketRow> Items);

/// <summary>Repeatable filters of a report request (priority=High&amp;priority=Urgent, status=..., assignee=..., unassigned=true).</summary>
internal sealed record ReportFilterParams(string[]? Priority, string[]? Status, Guid? Assignee, bool? Unassigned)
{
    public ReportQuery ToQuery(string? from, string? to, string? group) => new(from, to, group, Priority, Status, Assignee, Unassigned);
}
