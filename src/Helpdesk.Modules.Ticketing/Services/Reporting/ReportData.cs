using System.Globalization;
using Helpdesk.Modules.Ticketing.Domain;
using Helpdesk.Modules.Ticketing.Persistence;
using Helpdesk.Modules.Ticketing.Sla;
using Helpdesk.SharedKernel.Errors;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Modules.Ticketing.Services.Reporting;

internal sealed record Bucket(DateOnly Start, DateOnly End, DateTimeOffset StartUtc, DateTimeOffset EndUtc);

/// <summary>The period of a report in Europe/London calendar days (inclusive), cut into day, week (Monday) or month buckets clipped to the period.</summary>
internal sealed class ReportRange
{
    public const int MaxDays = 366;
    public const int MaxDayBuckets = 92;
    public static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");

    public required DateOnly From { get; init; }

    public required DateOnly To { get; init; }

    public required string Group { get; init; }

    public required IReadOnlyList<Bucket> Buckets { get; init; }

    public DateTimeOffset StartUtc => Buckets[0].StartUtc;

    public DateTimeOffset EndUtc => Buckets[^1].EndUtc;

    public int Days => To.DayNumber - From.DayNumber + 1;

    public static ReportRange Parse(string? from, string? to, string? group, TimeProvider clock)
    {
        var today = LondonDay(clock.GetUtcNow());
        var last = ParseDay("to", to) ?? today;
        var first = ParseDay("from", from) ?? last.AddDays(-29);
        if (first > last)
        {
            throw new RequestValidationException("from", "from must not be after to.");
        }

        if (last.DayNumber - first.DayNumber >= MaxDays)
        {
            throw new RequestValidationException("from", $"The period cannot be longer than {MaxDays} days.");
        }

        var days = last.DayNumber - first.DayNumber + 1;
        var g = string.IsNullOrWhiteSpace(group) ? (days <= 31 ? "day" : days <= 120 ? "week" : "month") : group.Trim().ToLowerInvariant();
        if (g is not ("day" or "week" or "month"))
        {
            throw new RequestValidationException("group", "group must be day, week or month.");
        }

        if (g == "day" && days > MaxDayBuckets)
        {
            throw new RequestValidationException("group", $"Daily grouping is limited to {MaxDayBuckets} days; choose weekly or monthly for a longer period.");
        }

        return new ReportRange { From = first, To = last, Group = g, Buckets = BuildBuckets(first, last, g) };
    }

    /// <summary>The equally long period immediately before this one (used for "compared with the previous period").</summary>
    public (DateTimeOffset Start, DateTimeOffset End, DateOnly From, DateOnly To) Previous()
    {
        var to = From.AddDays(-1);
        var from = to.AddDays(-(Days - 1));
        return (StartOfDay(from), StartOfDay(to.AddDays(1)), from, to);
    }

    /// <summary>Index of the bucket holding the instant, or -1 when it falls outside the period.</summary>
    public int IndexOf(DateTimeOffset at)
    {
        if (at < StartUtc || at >= EndUtc)
        {
            return -1;
        }

        var lo = 0;
        var hi = Buckets.Count - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (Buckets[mid].StartUtc <= at)
            {
                lo = mid;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return lo;
    }

    public IReadOnlyList<string> Labels => [.. Buckets.Select(b => b.Start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))];

    /// <summary>The bucket whose start date is given, for drill-down.</summary>
    public Bucket? BucketStartingOn(DateOnly start) => Buckets.FirstOrDefault(b => b.Start == start);

    public static DateOnly LondonDay(DateTimeOffset at) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, London).DateTime);

    public static DateTimeOffset StartOfDay(DateOnly day) =>
        new(TimeZoneInfo.ConvertTimeToUtc(day.ToDateTime(TimeOnly.MinValue), London), TimeSpan.Zero);

    public static DateOnly? ParseDay(string field, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : throw new RequestValidationException(field, $"{field} must be a date as yyyy-MM-dd.");
    }

    private static List<Bucket> BuildBuckets(DateOnly first, DateOnly last, string group)
    {
        var buckets = new List<Bucket>();
        var start = first;
        while (start <= last)
        {
            var end = group switch
            {
                "day" => start,
                "week" => start.AddDays(7 - (((int)start.DayOfWeek + 6) % 7) - 1),
                _ => new DateOnly(start.Year, start.Month, DateTime.DaysInMonth(start.Year, start.Month)),
            };
            if (end > last)
            {
                end = last;
            }

            buckets.Add(new Bucket(start, end, StartOfDay(start), StartOfDay(end.AddDays(1))));
            start = end.AddDays(1);
        }

        return buckets;
    }
}

internal sealed record LifeEvent(string Kind, string? From, string? To, DateTimeOffset At);

internal sealed record SlaFact(
    int? FrTarget, DateTimeOffset? FrMetAt, DateTimeOffset? FrBreachedAt, int? ResTarget, DateTimeOffset? ResMetAt, DateTimeOffset? ResBreachedAt);

/// <summary>One ticket with the history the reports need, derived once from the content-free lifecycle record.</summary>
internal sealed class TicketFact
{
    public required Guid Id { get; init; }

    public required string Reference { get; init; }

    public required string Subject { get; init; }

    public required TicketStatus Status { get; init; }

    public required TicketPriority Priority { get; init; }

    public required Guid? Assignee { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required bool Merged { get; init; }

    /// <summary>The priority the ticket was raised with: the first priority change's "from", otherwise its current priority.</summary>
    public TicketPriority PriorityAtCreation { get; set; }

    public DateTimeOffset? MergedAt { get; set; }

    /// <summary>First PUBLIC message by a human agent (an acknowledgement or an internal note never counts).</summary>
    public DateTimeOffset? FirstHumanReplyAt { get; set; }

    /// <summary>Moves into Resolved, or into Closed from an open state.</summary>
    public List<DateTimeOffset> Resolutions { get; } = [];

    /// <summary>Moves from Resolved/Closed back to Open/Pending.</summary>
    public List<DateTimeOffset> Reopens { get; } = [];

    public List<(DateTimeOffset At, TicketStatus From, TicketStatus To)> Transitions { get; } = [];

    public TicketStatus InitialStatus { get; set; }

    public bool IsBacklog => !Merged && Status is TicketStatus.Open or TicketStatus.Pending;

    public DateTimeOffset? FirstResolutionEver => Resolutions.Count == 0 ? null : Resolutions.Min();

    /// <summary>First qualifying resolution inside the period, or null.</summary>
    public DateTimeOffset? FirstResolutionIn(DateTimeOffset start, DateTimeOffset end)
    {
        var inside = Resolutions.Where(r => r >= start && r < end).ToList();
        return inside.Count == 0 ? null : inside.Min();
    }

    /// <summary>Backlog membership as of an instant: Open, Pending, or null (not in the backlog then).</summary>
    public TicketStatus? BacklogStateAt(DateTimeOffset at)
    {
        if (Merged && MergedAt is null)
        {
            return null;
        }

        if (at < CreatedAt || (MergedAt is { } m && at >= m))
        {
            return null;
        }

        var state = InitialStatus;
        foreach (var t in Transitions)
        {
            if (t.At > at)
            {
                break;
            }

            state = t.To;
        }

        return state is TicketStatus.Open or TicketStatus.Pending ? state : null;
    }
}

/// <summary>The filtered ticket set of one department with everything history-based pre-computed. One load per report request.</summary>
internal sealed class ReportData
{
    public required Guid DepartmentId { get; init; }

    public required IReadOnlyList<TicketFact> Tickets { get; init; }

    public required IReadOnlyDictionary<Guid, SlaFact> Sla { get; init; }

    public required IReadOnlySet<Guid> FirstResponseNotApplicable { get; init; }

    public static async Task<ReportData> LoadAsync(TicketingDbContext db, Guid departmentId, ReportQuery filters, CancellationToken ct)
    {
        var tickets = db.Tickets.AsNoTracking().Where(t => t.DepartmentId == departmentId);
        if (filters.Status is { Length: > 0 })
        {
            var statuses = filters.Status.Select(s => Enum.TryParse<TicketStatus>(s, true, out var v) && Enum.IsDefined(v) ? v : throw new RequestValidationException("status", "Unknown status.")).ToArray();
            tickets = tickets.Where(t => statuses.Contains(t.Status));
        }

        if (filters.Unassigned == true)
        {
            tickets = tickets.Where(t => t.AssigneeUserId == null);
        }
        else if (filters.Assignee is { } assignee)
        {
            tickets = tickets.Where(t => t.AssigneeUserId == assignee);
        }

        var wantedPriorities = filters.Priority is { Length: > 0 }
            ? filters.Priority.Select(p => Enum.TryParse<TicketPriority>(p, true, out var v) && Enum.IsDefined(v) ? v : throw new RequestValidationException("priority", "Unknown priority.")).ToHashSet()
            : null;

        var rows = await tickets.Select(t => new { t.Id, t.Reference, t.Subject, t.Status, t.Priority, t.AssigneeUserId, t.CreatedAt, Merged = t.MergedIntoTicketId != null }).ToListAsync(ct);
        var events = (await db.TicketLifecycleEvents.AsNoTracking()
                .Where(e => e.DepartmentId == departmentId && (e.Kind == "status" || e.Kind == "priority" || e.Kind == "message" || e.Kind == "merged"))
                .Select(e => new { e.TicketId, e.Kind, e.FromValue, e.ToValue, e.OccurredAt }).ToListAsync(ct))
            .GroupBy(e => e.TicketId).ToDictionary(g => g.Key, g => g.OrderBy(e => e.OccurredAt).Select(e => new LifeEvent(e.Kind, e.FromValue, e.ToValue, e.OccurredAt)).ToList());

        var facts = new List<TicketFact>(rows.Count);
        foreach (var r in rows)
        {
            var fact = new TicketFact { Id = r.Id, Reference = r.Reference, Subject = r.Subject, Status = r.Status, Priority = r.Priority, Assignee = r.AssigneeUserId, CreatedAt = r.CreatedAt, Merged = r.Merged };
            var list = events.GetValueOrDefault(r.Id) ?? [];
            fact.PriorityAtCreation = list.FirstOrDefault(e => e.Kind == "priority") is { From: { } pf } && Enum.TryParse<TicketPriority>(pf, out var p0) ? p0 : r.Priority;
            fact.InitialStatus = list.FirstOrDefault(e => e.Kind == "status") is { From: { } fs } && Enum.TryParse<TicketStatus>(fs, out var s0) ? s0 : r.Status;
            foreach (var e in list)
            {
                switch (e.Kind)
                {
                    case "merged":
                        fact.MergedAt ??= e.At;
                        break;
                    case "message" when e.To == "Public:Agent" && fact.FirstHumanReplyAt is null && e.At >= r.CreatedAt:
                        fact.FirstHumanReplyAt = e.At;
                        break;
                    case "status" when Enum.TryParse<TicketStatus>(e.From, out var sf) && Enum.TryParse<TicketStatus>(e.To, out var st):
                        fact.Transitions.Add((e.At, sf, st));
                        if (st == TicketStatus.Resolved || (st == TicketStatus.Closed && sf != TicketStatus.Resolved))
                        {
                            fact.Resolutions.Add(e.At);
                        }

                        if (sf is TicketStatus.Resolved or TicketStatus.Closed && st is TicketStatus.Open or TicketStatus.Pending)
                        {
                            fact.Reopens.Add(e.At);
                        }

                        break;
                }
            }

            if (wantedPriorities is null || wantedPriorities.Contains(fact.PriorityAtCreation))
            {
                facts.Add(fact);
            }
        }

        var sla = (await db.TicketSlas.AsNoTracking().Where(s => s.DepartmentId == departmentId)
                .Select(s => new { s.TicketId, s.FirstResponseTargetMinutes, s.FirstResponseMetAt, s.FirstResponseBreachedAt, s.ResolutionTargetMinutes, s.ResolutionMetAt, s.ResolutionBreachedAt }).ToListAsync(ct))
            .ToDictionary(s => s.TicketId, s => new SlaFact(s.FirstResponseTargetMinutes, s.FirstResponseMetAt, s.FirstResponseBreachedAt, s.ResolutionTargetMinutes, s.ResolutionMetAt, s.ResolutionBreachedAt));
        var notApplicable = (await db.SlaEvents.AsNoTracking().Where(e => e.DepartmentId == departmentId && e.Kind == SlaEventKinds.FirstResponseNotApplicable).Select(e => e.TicketId).Distinct().ToListAsync(ct)).ToHashSet();

        return new ReportData { DepartmentId = departmentId, Tickets = facts, Sla = sla, FirstResponseNotApplicable = notApplicable };
    }

    public string? FirstResponseOutcome(Guid ticketId)
    {
        if (!Sla.TryGetValue(ticketId, out var s) || s.FrTarget is null)
        {
            return null;
        }

        return FirstResponseNotApplicable.Contains(ticketId) ? "na" : s.FrBreachedAt is not null ? "breached" : s.FrMetAt is not null ? "met" : "pending";
    }

    public string? ResolutionOutcome(Guid ticketId)
    {
        if (!Sla.TryGetValue(ticketId, out var s) || s.ResTarget is null)
        {
            return null;
        }

        return s.ResBreachedAt is not null ? "breached" : s.ResMetAt is not null ? "met" : "pending";
    }
}
