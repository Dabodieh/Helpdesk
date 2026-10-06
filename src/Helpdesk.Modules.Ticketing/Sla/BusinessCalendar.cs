using System.Text.Json;
using System.Text.Json.Serialization;

namespace Helpdesk.Modules.Ticketing.Sla;

/// <summary>A working interval inside one local day, in minutes from local midnight (0 to 1440, start before end).</summary>
internal sealed record WorkInterval(int StartMinute, int EndMinute);

internal sealed record WeeklyDay(DayOfWeek Day, IReadOnlyList<WorkInterval> Intervals);

/// <summary>A date-specific override: no intervals = closed all day (holiday), otherwise the alternative hours of that date.</summary>
internal sealed record CalendarException(DateOnly Date, string? Name, IReadOnlyList<WorkInterval> Intervals);

internal sealed record CalendarDefinition(string TimeZoneId, IReadOnlyList<WeeklyDay> Weekly, IReadOnlyList<CalendarException> Exceptions);

internal static class CalendarJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
}

/// <summary>
/// Business-time arithmetic over a time zone, weekly hours and date exceptions. Pure and deterministic (no clock, no database).
/// Local working times are converted to instants with <see cref="TimeZoneInfo"/>, so a day that is 23 or 25 hours long, or a working
/// interval that spans the clock change, yields the real elapsed working time (never "9 to 17 is always 8 hours").
/// </summary>
internal sealed class BusinessCalendar
{
    /// <summary>How far ahead a search for working time may go before the calendar is treated as having none.</summary>
    private const int MaxDaysAhead = 900;

    private readonly TimeZoneInfo _zone;
    private readonly Dictionary<DayOfWeek, IReadOnlyList<WorkInterval>> _weekly;
    private readonly Dictionary<DateOnly, IReadOnlyList<WorkInterval>> _exceptions;

    private BusinessCalendar(TimeZoneInfo zone, Dictionary<DayOfWeek, IReadOnlyList<WorkInterval>> weekly, Dictionary<DateOnly, IReadOnlyList<WorkInterval>> exceptions)
    {
        _zone = zone;
        _weekly = weekly;
        _exceptions = exceptions;
    }

    /// <summary>Round the clock, every day (UTC): used by policies without a calendar.</summary>
    public static BusinessCalendar AlwaysOn { get; } = Create(new CalendarDefinition(
        "UTC",
        [.. Enum.GetValues<DayOfWeek>().Select(d => new WeeklyDay(d, [new WorkInterval(0, 1440)]))],
        []));

    public string TimeZoneId => _zone.Id;

    /// <summary>Throws <see cref="ArgumentException"/> for an unknown time zone or invalid intervals (callers validate first).</summary>
    public static BusinessCalendar Create(CalendarDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var zone = FindZone(definition.TimeZoneId) ?? throw new ArgumentException($"Unknown time zone '{definition.TimeZoneId}'.", nameof(definition));
        var weekly = new Dictionary<DayOfWeek, IReadOnlyList<WorkInterval>>();
        foreach (var day in Enum.GetValues<DayOfWeek>())
        {
            weekly[day] = Normalise(definition.Weekly.Where(w => w.Day == day).SelectMany(w => w.Intervals));
        }

        var exceptions = definition.Exceptions.ToDictionary(e => e.Date, e => Normalise(e.Intervals));
        return new BusinessCalendar(zone, weekly, exceptions);
    }

    public static TimeZoneInfo? FindZone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id.Trim());
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return null;
        }
    }

    /// <summary>Sorted, merged, valid intervals (overlapping or touching intervals are joined).</summary>
    public static IReadOnlyList<WorkInterval> Normalise(IEnumerable<WorkInterval> intervals)
    {
        var sorted = intervals.OrderBy(i => i.StartMinute).ThenBy(i => i.EndMinute).ToList();
        var merged = new List<WorkInterval>();
        foreach (var interval in sorted)
        {
            if (merged.Count > 0 && interval.StartMinute <= merged[^1].EndMinute)
            {
                merged[^1] = merged[^1] with { EndMinute = Math.Max(merged[^1].EndMinute, interval.EndMinute) };
            }
            else
            {
                merged.Add(interval);
            }
        }

        return merged;
    }

    /// <summary>The intervals of one local date: the exception when there is one, otherwise the weekly hours of that weekday.</summary>
    public IReadOnlyList<WorkInterval> IntervalsOn(DateOnly date) =>
        _exceptions.TryGetValue(date, out var exceptional) ? exceptional : _weekly[date.DayOfWeek];

    /// <summary>Working intervals as instants, ascending, the first one clipped to start no earlier than <paramref name="from"/>.</summary>
    public IEnumerable<(DateTimeOffset Start, DateTimeOffset End)> WorkingIntervalsFrom(DateTimeOffset from)
    {
        var localDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(from, _zone).DateTime);
        for (var offset = 0; offset <= MaxDaysAhead; offset++)
        {
            var date = localDate.AddDays(offset);
            foreach (var interval in IntervalsOn(date))
            {
                var start = ToInstant(date, interval.StartMinute, preferEarlier: true);
                var end = ToInstant(date, interval.EndMinute, preferEarlier: false);
                if (end <= from || end <= start)
                {
                    continue;
                }

                yield return (start < from ? from : start, end);
            }
        }
    }

    public bool IsWorking(DateTimeOffset at) => WorkingIntervalsFrom(at).Take(1).Any(i => i.Start <= at);

    /// <summary>The first instant at or after <paramref name="from"/> that is inside working time, or null when the calendar has none.</summary>
    public DateTimeOffset? NextWorkingInstant(DateTimeOffset from)
    {
        foreach (var (start, _) in WorkingIntervalsFrom(from))
        {
            return start;
        }

        return null;
    }

    /// <summary>
    /// The instant at which <paramref name="amount"/> of working time has elapsed after <paramref name="start"/>. Time outside working
    /// hours does not count, so a start outside hours simply begins at the next opening. Null when the calendar has no working time.
    /// </summary>
    public DateTimeOffset? AddBusinessTime(DateTimeOffset start, TimeSpan amount)
    {
        if (amount <= TimeSpan.Zero)
        {
            return start;
        }

        var remaining = amount;
        foreach (var (from, to) in WorkingIntervalsFrom(start))
        {
            var length = to - from;
            if (length >= remaining)
            {
                return from + remaining;
            }

            remaining -= length;
        }

        return null;
    }

    /// <summary>Working time between two instants (zero when the range is empty or reversed).</summary>
    public TimeSpan BusinessTimeBetween(DateTimeOffset from, DateTimeOffset to)
    {
        if (to <= from)
        {
            return TimeSpan.Zero;
        }

        var total = TimeSpan.Zero;
        foreach (var (start, end) in WorkingIntervalsFrom(from))
        {
            if (start >= to)
            {
                break;
            }

            total += (end < to ? end : to) - start;
        }

        return total;
    }

    /// <summary>
    /// A local wall-clock time as an instant. A time skipped by the spring-forward gap moves forward past the gap; an ambiguous time
    /// (autumn repeat) takes the first occurrence for a start and the last for an end, so the repeated hour counts as working time
    /// whenever the interval contains it.
    /// </summary>
    private DateTimeOffset ToInstant(DateOnly date, int minuteOfDay, bool preferEarlier)
    {
        var local = date.ToDateTime(TimeOnly.MinValue).AddMinutes(minuteOfDay);
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        var guard = 0;
        while (_zone.IsInvalidTime(local) && guard++ < 8)
        {
            local = local.AddMinutes(15);
        }

        TimeSpan offset;
        if (_zone.IsAmbiguousTime(local))
        {
            var offsets = _zone.GetAmbiguousTimeOffsets(local);
            offset = preferEarlier ? offsets.Max() : offsets.Min();
        }
        else
        {
            offset = _zone.GetUtcOffset(local);
        }

        return new DateTimeOffset(local, offset).ToUniversalTime();
    }
}
