using Helpdesk.Modules.Ticketing.Domain;

namespace Helpdesk.Modules.Ticketing.Services.Reporting;

internal sealed record Stats(int Count, double? Average, double? Median, double? P90);

/// <summary>
/// The arithmetic of every report, over the pre-computed <see cref="ReportData"/>. Definitions follow docs/architecture/REPORTING_DEFINITIONS.md: received = created in the
/// period (a merged-away duplicate was still raised), resolved = distinct tickets whose first qualifying move into Resolved/Closed falls in the period, first response =
/// the first PUBLIC message by a human agent (acknowledgements, notes and system messages never count), times are wall-clock, days are Europe/London.
/// </summary>
internal sealed class ReportBuilder(ReportData data, ReportRange range, DateTimeOffset now)
{
    private static readonly TicketPriority[] PriorityOrder = [TicketPriority.Urgent, TicketPriority.High, TicketPriority.Normal, TicketPriority.Low];

    public static string PriorityTone(TicketPriority p) => p switch { TicketPriority.Urgent => "bad", TicketPriority.High => "warn", TicketPriority.Normal => "info", _ => "muted" };

    private IEnumerable<TicketFact> Cohort => data.Tickets.Where(t => t.CreatedAt >= range.StartUtc && t.CreatedAt < range.EndUtc);

    public ReportPeriodDto Period => new(range.From, range.To, range.Group, "Europe/London", now);

    // ----- shared series -----

    private double?[] CountPerBucket(IEnumerable<DateTimeOffset> times)
    {
        var counts = new double?[range.Buckets.Count];
        Array.Fill(counts, 0d);
        foreach (var t in times)
        {
            var i = range.IndexOf(t);
            if (i >= 0)
            {
                counts[i] = counts[i]! + 1;
            }
        }

        return counts;
    }

    private IEnumerable<DateTimeOffset> ResolvedTimes() =>
        data.Tickets.Where(t => !t.Merged).Select(t => t.FirstResolutionIn(range.StartUtc, range.EndUtc)).OfType<DateTimeOffset>();

    private int ResolvedCount(DateTimeOffset start, DateTimeOffset end) =>
        data.Tickets.Count(t => !t.Merged && t.FirstResolutionIn(start, end) is not null);

    private int ReceivedCount(DateTimeOffset start, DateTimeOffset end) => data.Tickets.Count(t => t.CreatedAt >= start && t.CreatedAt < end);

    private IEnumerable<DateTimeOffset> ReopenTimes() => data.Tickets.SelectMany(t => t.Reopens).Where(r => r >= range.StartUtc && r < range.EndUtc);

    private DateTimeOffset BucketInstant(Bucket b) => b.EndUtc - TimeSpan.FromTicks(10) > now ? now : b.EndUtc - TimeSpan.FromTicks(10);

    /// <summary>Open and Pending backlog at the end of each bucket (reconstructed from the status history; merged duplicates are never backlog).</summary>
    private (double?[] Open, double?[] Pending) BacklogSeries()
    {
        var open = new double?[range.Buckets.Count];
        var pending = new double?[range.Buckets.Count];
        for (var i = 0; i < range.Buckets.Count; i++)
        {
            var at = BucketInstant(range.Buckets[i]);
            double o = 0, p = 0;
            foreach (var t in data.Tickets)
            {
                switch (t.BacklogStateAt(at))
                {
                    case TicketStatus.Open:
                        o++;
                        break;
                    case TicketStatus.Pending:
                        p++;
                        break;
                }
            }

            open[i] = o;
            pending[i] = p;
        }

        return (open, pending);
    }

    private (int Open, int Pending) BacklogAt(DateTimeOffset at)
    {
        int o = 0, p = 0;
        foreach (var t in data.Tickets)
        {
            switch (t.BacklogStateAt(at))
            {
                case TicketStatus.Open:
                    o++;
                    break;
                case TicketStatus.Pending:
                    p++;
                    break;
            }
        }

        return (o, p);
    }

    private static Stats Summarise(List<double> values)
    {
        if (values.Count == 0)
        {
            return new Stats(0, null, null, null);
        }

        values.Sort();
        double Percentile(double q)
        {
            var pos = (values.Count - 1) * q;
            var lo = (int)Math.Floor(pos);
            var hi = (int)Math.Ceiling(pos);
            return values[lo] + ((values[hi] - values[lo]) * (pos - lo));
        }

        return new Stats(values.Count, Math.Round(values.Average(), 1), Math.Round(Percentile(0.5), 1), Math.Round(Percentile(0.9), 1));
    }

    private static double? Pct(int part, int whole) => whole == 0 ? null : Math.Round(100d * part / whole, 1);

    private static double? Round(double? v) => v is null ? null : Math.Round(v.Value, 1);

    private static double? Delta(double current, double previous) => Math.Round(current - previous, 1);

    // ----- response and resolution time facts -----

    private static double? FirstResponseMinutes(TicketFact t) => t.FirstHumanReplyAt is { } r ? Math.Max(0, (r - t.CreatedAt).TotalMinutes) : null;

    private static double? ResolutionMinutes(TicketFact t) => !t.Merged && t.FirstResolutionEver is { } r ? Math.Max(0, (r - t.CreatedAt).TotalMinutes) : null;

    private double?[] StatPerBucket(Func<TicketFact, double?> value, Func<Stats, double?> pick)
    {
        var lists = Enumerable.Range(0, range.Buckets.Count).Select(_ => new List<double>()).ToArray();
        foreach (var t in Cohort)
        {
            if (value(t) is { } v && range.IndexOf(t.CreatedAt) is var i and >= 0)
            {
                lists[i].Add(v);
            }
        }

        return [.. lists.Select(l => pick(Summarise(l)))];
    }

    private double?[] ComplianceSeries(Func<Guid, string?> outcome)
    {
        var met = new int[range.Buckets.Count];
        var breached = new int[range.Buckets.Count];
        foreach (var t in Cohort)
        {
            var i = range.IndexOf(t.CreatedAt);
            switch (outcome(t.Id))
            {
                case "met":
                    met[i]++;
                    break;
                case "breached":
                    breached[i]++;
                    break;
            }
        }

        return [.. Enumerable.Range(0, range.Buckets.Count).Select(i => Pct(met[i], met[i] + breached[i]))];
    }

    private static (int Met, int Breached, int Pending, int NotApplicable, int NoSla) Outcomes(IEnumerable<TicketFact> tickets, Func<Guid, string?> outcome)
    {
        int met = 0, breached = 0, pending = 0, na = 0, none = 0;
        foreach (var t in tickets)
        {
            switch (outcome(t.Id))
            {
                case "met":
                    met++;
                    break;
                case "breached":
                    breached++;
                    break;
                case "pending":
                    pending++;
                    break;
                case "na":
                    na++;
                    break;
                default:
                    none++;
                    break;
            }
        }

        return (met, breached, pending, na, none);
    }

    // ----- the reports -----

    public ReportDto TicketVolume()
    {
        var received = CountPerBucket(Cohort.Select(t => t.CreatedAt));
        var resolved = CountPerBucket(ResolvedTimes());
        var previous = range.Previous();
        var receivedTotal = (int)received.Sum(v => v!.Value);
        var resolvedTotal = (int)resolved.Sum(v => v!.Value);
        var perPriority = PriorityOrder.Select(p =>
        {
            var arr = CountPerBucket(Cohort.Where(t => t.PriorityAtCreation == p).Select(t => t.CreatedAt));
            return new ReportLine(p.ToString().ToLowerInvariant(), p.ToString(), arr, "received", PriorityTone(p));
        }).ToList();
        var statusDist = new[] { TicketStatus.Open, TicketStatus.Pending, TicketStatus.Resolved, TicketStatus.Closed }
            .Select(s => (double?)Cohort.Count(t => t.Status == s)).ToArray();

        var rows = new List<IReadOnlyList<object?>>();
        for (var i = 0; i < range.Buckets.Count; i++)
        {
            var row = new List<object?> { range.Labels[i], (int)received[i]!.Value, (int)resolved[i]!.Value, (int)(received[i]!.Value - resolved[i]!.Value) };
            row.AddRange(perPriority.Select(l => (object?)(int)l.Values[i]!.Value));
            rows.Add(row);
        }

        var columns = new List<ReportColumn> { new("period", range.Group == "day" ? "Day" : range.Group == "week" ? "Week starting" : "Month starting", "date"), new("received", "Received", "number"), new("resolved", "Resolved", "number"), new("net", "Net", "number") };
        columns.AddRange(perPriority.Select(l => new ReportColumn(l.Key, l.Label, "number")));

        return new ReportDto("ticket-volume", Period,
            [
                new("received", "Received", receivedTotal, "count", ReceivedCount(previous.Start, previous.End), "received"),
                new("resolved", "Resolved", resolvedTotal, "count", ResolvedCount(previous.Start, previous.End), "resolved", Tone: "good"),
                new("net", "Net change in backlog", receivedTotal - resolvedTotal, "count", Tone: receivedTotal > resolvedTotal ? "warn" : "good"),
                new("merged", "Duplicates merged away", Cohort.Count(t => t.Merged), "count", Note: "Raised in the period, merged into another ticket since."),
                new("perDay", "Average received per day", Math.Round(receivedTotal / (double)range.Days, 1), "count"),
            ],
            [
                new("volume", "Tickets received and resolved", "bars", "count", "date", range.Labels,
                    [new("received", "Received", received, "received", "info"), new("resolved", "Resolved", resolved, "resolved", "good")]),
                new("priority", "Received by priority", "stacked", "count", "date", range.Labels, perPriority, "Priority the ticket was raised with"),
                new("status", "Where the tickets received in this period stand now", "hbars", "count", "category", ["Open", "Pending", "Resolved", "Closed"],
                    [new("status", "Tickets", statusDist, null, "info")]),
            ],
            [new("buckets", "Volume by " + range.Group, columns, rows)],
            ["Received counts every ticket raised in the period, including duplicates merged away later. Resolved counts a ticket once, on its first move into Resolved (or into Closed from an open state) in the period."],
            ["Breakdown by Department: a report covers one Department at a time, because the Department is the security boundary."]);
    }

    public ReportDto Backlog()
    {
        var (open, pending) = BacklogSeries();
        var current = data.Tickets.Where(t => t.IsBacklog).ToList();
        var unassigned = current.Count(t => t.Assignee is null);
        var awaiting = current.Count(t => t.FirstHumanReplyAt is null);
        var startState = BacklogAt(range.StartUtc - TimeSpan.FromTicks(10));
        var endState = BacklogAt(BucketInstant(range.Buckets[^1]));
        string[] ageLabels = ["Under 1 day", "1 to 3 days", "3 to 7 days", "1 to 2 weeks", "Over 2 weeks"];
        double?[] AgeCounts(TicketStatus status)
        {
            var arr = new double?[5];
            Array.Fill(arr, 0d);
            foreach (var t in current.Where(t => t.Status == status))
            {
                var days = (now - t.CreatedAt).TotalDays;
                var i = days < 1 ? 0 : days < 3 ? 1 : days < 7 ? 2 : days < 14 ? 3 : 4;
                arr[i] = arr[i]! + 1;
            }

            return arr;
        }

        var oldest = current.OrderBy(t => t.CreatedAt).Take(10).Select(t => (IReadOnlyList<object?>)[t.Reference, t.Subject, t.Status.ToString(), t.PriorityAtCreation.ToString(), t.CreatedAt, (int)(now - t.CreatedAt).TotalDays]).ToList();
        var total = endState.Open + endState.Pending;
        return new ReportDto("backlog", Period,
            [
                new("unresolved", "Unresolved now", current.Count, "count", Drill: "backlog"),
                new("unassigned", "Unassigned", unassigned, "count", Drill: "unassigned", Tone: unassigned > 0 ? "warn" : "good"),
                new("awaiting", "Awaiting first human reply", awaiting, "count", Drill: "awaiting-response", Tone: awaiting > 0 ? "warn" : "good"),
                new("change", "Change over the period", total - (startState.Open + startState.Pending), "count", Note: $"From {startState.Open + startState.Pending} at the start to {total} at the end.", Tone: total > startState.Open + startState.Pending ? "warn" : "good"),
                new("oldest", "Oldest unresolved (days)", current.Count == 0 ? null : (int)(now - current.Min(t => t.CreatedAt)).TotalDays, "count"),
            ],
            [
                new("over-time", "Unresolved tickets over time", "stacked", "count", "date", range.Labels,
                    [new("open", "Open", open, "backlog", "info"), new("pending", "Pending", pending, "backlog", "warn")], "At the end of each " + range.Group),
                new("age", "Age of unresolved tickets", "stacked", "count", "category", ageLabels,
                    [new("open", "Open", AgeCounts(TicketStatus.Open), "backlog", "info"), new("pending", "Pending", AgeCounts(TicketStatus.Pending), "backlog", "warn")], "Age is time since the ticket was raised, split by status so waiting-on-requester tickets are visible"),
            ],
            [
                new("oldest", "Longest outstanding tickets",
                    [new("reference", "Ticket", "text"), new("subject", "Subject", "text"), new("status", "Status", "text"), new("priority", "Priority", "text"), new("created", "Raised", "date"), new("age", "Days open", "number")], oldest),
            ],
            ["Backlog is Open plus Pending, never a merged duplicate. The history is rebuilt from recorded status changes."],
            ["Unassigned backlog over time: the assignee a ticket is created with is not recorded in the history, so only today's unassigned count is reported."]);
    }

    public ReportDto Lifecycle()
    {
        var created = CountPerBucket(Cohort.Select(t => t.CreatedAt));
        double?[] Transition(Func<(DateTimeOffset At, TicketStatus From, TicketStatus To), bool> pick) =>
            CountPerBucket(data.Tickets.SelectMany(t => t.Transitions).Where(pick).Select(x => x.At));
        var toPending = Transition(x => x.To == TicketStatus.Pending && x.From == TicketStatus.Open);
        var backToOpen = Transition(x => x.To == TicketStatus.Open && x.From == TicketStatus.Pending);
        var resolvedMoves = Transition(x => x.To == TicketStatus.Resolved);
        var closedMoves = Transition(x => x.To == TicketStatus.Closed);
        var reopened = CountPerBucket(ReopenTimes());
        var all = data.Tickets.SelectMany(t => t.Transitions).Where(x => x.At >= range.StartUtc && x.At < range.EndUtc).ToList();
        var matrix = all.GroupBy(x => (x.From, x.To)).OrderByDescending(g => g.Count()).ThenBy(g => g.Key.From).ThenBy(g => g.Key.To)
            .Select(g => (IReadOnlyList<object?>)[g.Key.From.ToString(), g.Key.To.ToString(), g.Count()]).ToList();
        var resolvedTickets = ResolvedTimes().Count();
        var reopenedTickets = data.Tickets.Count(t => t.Reopens.Any(r => r >= range.StartUtc && r < range.EndUtc));
        var rows = Enumerable.Range(0, range.Buckets.Count).Select(i => (IReadOnlyList<object?>)[range.Labels[i], (int)created[i]!.Value, (int)toPending[i]!.Value, (int)backToOpen[i]!.Value, (int)resolvedMoves[i]!.Value, (int)closedMoves[i]!.Value, (int)reopened[i]!.Value]).ToList();
        return new ReportDto("lifecycle", Period,
            [
                new("created", "Created", (int)created.Sum(v => v!.Value), "count", Drill: "received"),
                new("resolved", "Resolved", resolvedTickets, "count", Drill: "resolved", Tone: "good"),
                new("reopened", "Reopened", reopenedTickets, "count", Drill: "reopened", Tone: reopenedTickets > 0 ? "warn" : "neutral", Note: "Tickets moved from Resolved or Closed back to Open or Pending."),
                new("reopenRate", "Reopen rate", Pct(reopenedTickets, resolvedTickets), "percent", Note: "Reopened tickets as a share of resolved tickets in the same period."),
            ],
            [
                new("activity", "Status activity", "line", "count", "date", range.Labels,
                    [new("created", "Created", created, "received", "info"), new("pending", "Moved to Pending", toPending, null, "warn"), new("open", "Back to Open", backToOpen, null, "neutral"),
                     new("resolved", "Resolved", resolvedMoves, "resolved", "good"), new("closed", "Closed", closedMoves, null, "muted"), new("reopened", "Reopened", reopened, "reopened", "bad")]),
                new("reopen-trend", "Resolved and reopened", "bars", "count", "date", range.Labels,
                    [new("resolved", "Resolved", CountPerBucket(ResolvedTimes()), "resolved", "good"), new("reopened", "Reopened", reopened, "reopened", "bad")]),
            ],
            [
                new("buckets", "Activity by " + range.Group, [new("period", "Period", "date"), new("created", "Created", "number"), new("pending", "To Pending", "number"), new("open", "Back to Open", "number"), new("resolved", "To Resolved", "number"), new("closed", "To Closed", "number"), new("reopened", "Reopened", "number")], rows),
                new("transitions", "Status transitions in the period", [new("from", "From", "text"), new("to", "To", "text"), new("count", "Count", "number")], matrix),
            ],
            ["Every move is counted from the recorded status history; the created/opened counts are not estimated."],
            []);
    }

    public ReportDto Sla()
    {
        var cohort = Cohort.ToList();
        var fr = Outcomes(cohort, data.FirstResponseOutcome);
        var res = Outcomes(cohort, data.ResolutionOutcome);
        var frSeries = ComplianceSeries(data.FirstResponseOutcome);
        var resSeries = ComplianceSeries(data.ResolutionOutcome);
        double?[] Counts(Func<Guid, string?> outcome, string which) =>
            CountPerBucket(cohort.Where(t => outcome(t.Id) == which).Select(t => t.CreatedAt));
        var backlog = data.Tickets.Where(t => t.IsBacklog).ToList();
        var breachedNow = backlog.Count(t => data.Sla.TryGetValue(t.Id, out var s) && ((s.FrBreachedAt is not null && s.FrMetAt is null) || (s.ResBreachedAt is not null && s.ResMetAt is null)));
        var priorityRows = PriorityOrder.Select(p =>
        {
            var set = cohort.Where(t => t.PriorityAtCreation == p).ToList();
            var f = Outcomes(set, data.FirstResponseOutcome);
            var r = Outcomes(set, data.ResolutionOutcome);
            return (IReadOnlyList<object?>)[p.ToString(), set.Count, f.Met, f.Breached, Pct(f.Met, f.Met + f.Breached), r.Met, r.Breached, Pct(r.Met, r.Met + r.Breached)];
        }).ToList();
        return new ReportDto("sla", Period,
            [
                new("frCompliance", "First response compliance", Pct(fr.Met, fr.Met + fr.Breached), "percent", Note: $"{fr.Met} met, {fr.Breached} breached", Tone: fr.Breached == 0 ? "good" : "warn"),
                new("resCompliance", "Resolution compliance", Pct(res.Met, res.Met + res.Breached), "percent", Note: $"{res.Met} met, {res.Breached} breached", Tone: res.Breached == 0 ? "good" : "warn"),
                new("frBreached", "First response breached", fr.Breached, "count", Drill: "sla-fr-breached", Tone: fr.Breached > 0 ? "bad" : "good"),
                new("resBreached", "Resolution breached", res.Breached, "count", Drill: "sla-res-breached", Tone: res.Breached > 0 ? "bad" : "good"),
                new("breachedNow", "Overdue right now", breachedNow, "count", Note: "Unresolved tickets with a missed target that is still open.", Tone: breachedNow > 0 ? "bad" : "good"),
            ],
            [
                new("compliance", "SLA compliance over time", "line", "percent", "date", range.Labels,
                    [new("fr", "First response", frSeries, null, "info"), new("res", "Resolution", resSeries, null, "good")], "Of tickets raised in each " + range.Group + " whose target is decided (met or breached)"),
                new("fr-outcomes", "First response outcomes", "stacked", "count", "date", range.Labels,
                    [new("met", "Met", Counts(data.FirstResponseOutcome, "met"), null, "good"), new("breached", "Breached", Counts(data.FirstResponseOutcome, "breached"), "sla-fr-breached", "bad"), new("pending", "Still running", Counts(data.FirstResponseOutcome, "pending"), null, "muted")]),
                new("res-outcomes", "Resolution outcomes", "stacked", "count", "date", range.Labels,
                    [new("met", "Met", Counts(data.ResolutionOutcome, "met"), null, "good"), new("breached", "Breached", Counts(data.ResolutionOutcome, "breached"), "sla-res-breached", "bad"), new("pending", "Still running", Counts(data.ResolutionOutcome, "pending"), null, "muted")]),
            ],
            [
                new("priority", "By priority",
                    [new("priority", "Priority", "text"), new("tickets", "Tickets raised", "number"), new("frMet", "First response met", "number"), new("frBreached", "First response breached", "number"), new("frPct", "First response %", "percent"), new("resMet", "Resolution met", "number"), new("resBreached", "Resolution breached", "number"), new("resPct", "Resolution %", "percent")], priorityRows),
                new("coverage", "Coverage",
                    [new("what", "Tickets raised in the period", "text"), new("firstResponse", "First response", "number"), new("resolution", "Resolution", "number")],
                    [["Met", fr.Met, res.Met], ["Breached", fr.Breached, res.Breached], ["Still running", fr.Pending, res.Pending], ["Not applicable (resolved without a reply)", fr.NotApplicable, null], ["No SLA target", fr.NoSla, res.NoSla + res.NotApplicable]]),
            ],
            ["Outcomes are read from each ticket's recorded SLA state. A target that was missed and answered late counts as breached. Compliance is met divided by met plus breached; tickets still running or without a target are excluded."],
            ["Why a ticket had no SLA policy is not recorded, so tickets without one are only counted, not explained."]);
    }

    public ReportDto ResponseTimes()
    {
        var cohort = Cohort.ToList();
        var fr = Summarise([.. cohort.Select(FirstResponseMinutes).OfType<double>()]);
        var res = Summarise([.. cohort.Select(ResolutionMinutes).OfType<double>()]);
        var priorityRows = PriorityOrder.Select(p =>
        {
            var set = cohort.Where(t => t.PriorityAtCreation == p).ToList();
            var f = Summarise([.. set.Select(FirstResponseMinutes).OfType<double>()]);
            var r = Summarise([.. set.Select(ResolutionMinutes).OfType<double>()]);
            return (IReadOnlyList<object?>)[p.ToString(), f.Count, f.Average, f.Median, f.P90, r.Count, r.Average, r.Median, r.P90];
        }).ToList();
        (string Label, double Max)[] bins = [("Within 15 min", 15), ("15 min to 1 hour", 60), ("1 to 4 hours", 240), ("4 hours to 1 day", 1440), ("1 to 3 days", 4320), ("Over 3 days", double.MaxValue)];
        var histogram = new double?[bins.Length + 1];
        Array.Fill(histogram, 0d);
        foreach (var t in cohort)
        {
            if (FirstResponseMinutes(t) is { } m)
            {
                var i = Array.FindIndex(bins, b => m <= b.Max);
                histogram[i] = histogram[i]! + 1;
            }
            else
            {
                histogram[bins.Length] = histogram[bins.Length]! + 1;
            }
        }

        return new ReportDto("response-times", Period,
            [
                new("frMedian", "Median first response", fr.Median, "minutes", Note: $"{fr.Count} tickets answered"),
                new("frAverage", "Average first response", fr.Average, "minutes"),
                new("frP90", "90th percentile first response", fr.P90, "minutes", Note: "Nine in ten tickets were answered faster than this."),
                new("resMedian", "Median resolution", res.Median, "minutes", Note: $"{res.Count} tickets resolved"),
                new("resAverage", "Average resolution", res.Average, "minutes"),
            ],
            [
                new("fr-trend", "First response time over time", "line", "minutes", "date", range.Labels,
                    [new("median", "Median", StatPerBucket(FirstResponseMinutes, s => s.Median), null, "info"), new("average", "Average", StatPerBucket(FirstResponseMinutes, s => s.Average), null, "muted")], "By the " + range.Group + " the ticket was raised"),
                new("res-trend", "Resolution time over time", "line", "minutes", "date", range.Labels,
                    [new("median", "Median", StatPerBucket(ResolutionMinutes, s => s.Median), null, "good"), new("average", "Average", StatPerBucket(ResolutionMinutes, s => s.Average), null, "muted")]),
                new("fr-distribution", "How quickly tickets were first answered", "hbars", "count", "category", [.. bins.Select(b => b.Label), "No human reply yet"], [new("tickets", "Tickets", histogram, null, "info")]),
            ],
            [
                new("priority", "By priority",
                    [new("priority", "Priority", "text"), new("frCount", "Answered", "number"), new("frAvg", "First response avg", "minutes"), new("frMedian", "First response median", "minutes"), new("frP90", "First response 90th pct", "minutes"), new("resCount", "Resolved", "number"), new("resAvg", "Resolution avg", "minutes"), new("resMedian", "Resolution median", "minutes"), new("resP90", "Resolution 90th pct", "minutes")], priorityRows),
            ],
            ["First response is the first PUBLIC message by a human agent after the ticket was raised: acknowledgements, internal notes and system messages never count. Resolution is the time to the first move into Resolved or Closed. Both are wall-clock times, not business hours (SLA targets use business hours)."],
            []);
    }

    public ReportDto Workload(IReadOnlyDictionary<Guid, string> names)
    {
        var backlog = data.Tickets.Where(t => t.IsBacklog).ToList();
        var resolvedIn = data.Tickets.Where(t => !t.Merged && t.FirstResolutionIn(range.StartUtc, range.EndUtc) is not null).ToList();
        var people = backlog.Select(t => t.Assignee).Concat(resolvedIn.Select(t => t.Assignee)).OfType<Guid>().Distinct()
            .Select(id => (Id: id, Name: names.GetValueOrDefault(id, "Former member"))).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var rows = people.Select(p => (IReadOnlyList<object?>)[p.Name, backlog.Count(t => t.Assignee == p.Id && t.Status == TicketStatus.Open), backlog.Count(t => t.Assignee == p.Id && t.Status == TicketStatus.Pending), backlog.Count(t => t.Assignee == p.Id), resolvedIn.Count(t => t.Assignee == p.Id), p.Id.ToString()]).ToList();
        var unassigned = backlog.Count(t => t.Assignee is null);
        rows.Add(["Unassigned", backlog.Count(t => t.Assignee is null && t.Status == TicketStatus.Open), backlog.Count(t => t.Assignee is null && t.Status == TicketStatus.Pending), unassigned, resolvedIn.Count(t => t.Assignee is null), "none"]);
        var categories = people.Select(p => p.Name).Append("Unassigned").ToArray();
        double?[] Col(Func<TicketFact, bool> pick) => [.. people.Select(p => (double?)backlog.Count(t => t.Assignee == p.Id && pick(t))).Append(backlog.Count(t => t.Assignee is null && pick(t)))];
        var held = people.Count(p => backlog.Any(t => t.Assignee == p.Id));
        return new ReportDto("workload", Period,
            [
                new("backlog", "Unresolved now", backlog.Count, "count", Drill: "backlog"),
                new("assigned", "Assigned", backlog.Count - unassigned, "count"),
                new("unassigned", "Unassigned", unassigned, "count", Drill: "unassigned", Tone: unassigned > 0 ? "warn" : "good"),
                new("holders", "People holding open work", held, "count"),
                new("resolved", "Resolved in the period", resolvedIn.Count, "count", Drill: "resolved", Tone: "good"),
            ],
            [
                new("distribution", "Unresolved tickets by assignee", "stacked", "count", "category", categories,
                    [new("open", "Open", Col(t => t.Status == TicketStatus.Open), "backlog", "info"), new("pending", "Pending", Col(t => t.Status == TicketStatus.Pending), "backlog", "warn")], "Listed alphabetically: this shows where the work sits, it is not a ranking"),
            ],
            [
                new("assignees", "Workload by assignee",
                    [new("assignee", "Assignee", "text"), new("open", "Open", "number"), new("pending", "Pending", "number"), new("unresolved", "Unresolved", "number"), new("resolved", "Resolved in period", "number")], rows.Select(r => (IReadOnlyList<object?>)[.. r.Take(5)]).ToList()),
            ],
            ["Open and pending work is counted against each ticket's current assignee. Resolved in period is also by current assignee, because the history does not record who resolved a ticket. No scores or rankings are produced."],
            ["Unassigned backlog over time: the assignee a ticket is created with is not recorded in the history, so only the current split is available."]);
    }

    public ReportDto SupportOverview()
    {
        var received = CountPerBucket(Cohort.Select(t => t.CreatedAt));
        var resolved = CountPerBucket(ResolvedTimes());
        var (open, pending) = BacklogSeries();
        var previous = range.Previous();
        var receivedTotal = (int)received.Sum(v => v!.Value);
        var resolvedTotal = (int)resolved.Sum(v => v!.Value);
        var cohort = Cohort.ToList();
        var fr = Outcomes(cohort, data.FirstResponseOutcome);
        var res = Outcomes(cohort, data.ResolutionOutcome);
        var frStats = Summarise([.. cohort.Select(FirstResponseMinutes).OfType<double>()]);
        var resStats = Summarise([.. cohort.Select(ResolutionMinutes).OfType<double>()]);
        var unresolved = data.Tickets.Count(t => t.IsBacklog);
        var startBacklog = BacklogAt(range.StartUtc - TimeSpan.FromTicks(10));
        var endBacklog = BacklogAt(BucketInstant(range.Buckets[^1]));
        var rows = Enumerable.Range(0, range.Buckets.Count).Select(i => (IReadOnlyList<object?>)[range.Labels[i], (int)received[i]!.Value, (int)resolved[i]!.Value, (int)(open[i]!.Value + pending[i]!.Value)]).ToList();
        return new ReportDto("support-overview", Period,
            [
                new("received", "Received", receivedTotal, "count", ReceivedCount(previous.Start, previous.End), "received"),
                new("resolved", "Resolved", resolvedTotal, "count", ResolvedCount(previous.Start, previous.End), "resolved", Tone: "good"),
                new("unresolved", "Unresolved now", unresolved, "count", Drill: "backlog"),
                new("backlogChange", "Backlog change", (endBacklog.Open + endBacklog.Pending) - (startBacklog.Open + startBacklog.Pending), "count", Tone: endBacklog.Open + endBacklog.Pending > startBacklog.Open + startBacklog.Pending ? "warn" : "good"),
                new("frMedian", "Median first response", frStats.Median, "minutes"),
                new("resMedian", "Median resolution", resStats.Median, "minutes"),
                new("frSla", "First response SLA", Pct(fr.Met, fr.Met + fr.Breached), "percent", Tone: fr.Breached == 0 ? "good" : "warn"),
                new("resSla", "Resolution SLA", Pct(res.Met, res.Met + res.Breached), "percent", Tone: res.Breached == 0 ? "good" : "warn"),
            ],
            [
                new("flow", "Received and resolved", "bars", "count", "date", range.Labels,
                    [new("received", "Received", received, "received", "info"), new("resolved", "Resolved", resolved, "resolved", "good")]),
                new("backlog", "Unresolved tickets", "area", "count", "date", range.Labels,
                    [new("open", "Open", open, "backlog", "info"), new("pending", "Pending", pending, "backlog", "warn")], "At the end of each " + range.Group),
                new("sla", "SLA compliance", "line", "percent", "date", range.Labels,
                    [new("fr", "First response", ComplianceSeries(data.FirstResponseOutcome), null, "info"), new("res", "Resolution", ComplianceSeries(data.ResolutionOutcome), null, "good")]),
                new("response", "Median first response", "line", "minutes", "date", range.Labels,
                    [new("median", "Median", StatPerBucket(FirstResponseMinutes, s => s.Median), null, "info")]),
            ],
            [new("buckets", "Summary by " + range.Group, [new("period", "Period", "date"), new("received", "Received", "number"), new("resolved", "Resolved", "number"), new("backlog", "Unresolved at end", "number")], rows)],
            ["Each figure follows the shared reporting definitions; open any of the other reports for the detail behind a number."],
            []);
    }
}
