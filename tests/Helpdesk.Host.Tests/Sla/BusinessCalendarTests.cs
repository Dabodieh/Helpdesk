using Helpdesk.Modules.Ticketing.Sla;

namespace Helpdesk.Host.Tests.Sla;

/// <summary>Pure business-time arithmetic: weekends, closures, alternative hours, out-of-hours starts, day boundaries and DST (London and New York).</summary>
public sealed class BusinessCalendarTests
{
    private static readonly WorkInterval Office = new(9 * 60, 17 * 60);

    private static BusinessCalendar Weekdays(string zone = "Europe/London", params CalendarException[] exceptions) =>
        BusinessCalendar.Create(new CalendarDefinition(
            zone,
            [.. new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday }.Select(d => new WeeklyDay(d, [Office]))],
            exceptions));

    private static BusinessCalendar EveryDay(string zone, WorkInterval interval) =>
        BusinessCalendar.Create(new CalendarDefinition(zone, [.. Enum.GetValues<DayOfWeek>().Select(d => new WeeklyDay(d, [interval]))], []));

    private static DateTimeOffset Utc(string iso) => DateTimeOffset.Parse(iso, System.Globalization.CultureInfo.InvariantCulture).ToUniversalTime();

    [Fact]
    public void Friday_1630_plus_four_business_hours_is_monday_1230()
    {
        // 30 minutes remain on Friday, the other 3.5 hours start Monday 09:00 (January: London is UTC).
        Assert.Equal(Utc("2026-01-12T12:30:00Z"), Weekdays().AddBusinessTime(Utc("2026-01-09T16:30:00Z"), TimeSpan.FromHours(4)));
    }

    [Fact]
    public void A_start_on_a_weekend_begins_at_monday_opening()
    {
        Assert.Equal(Utc("2026-01-12T10:00:00Z"), Weekdays().AddBusinessTime(Utc("2026-01-10T10:00:00Z"), TimeSpan.FromHours(1)));
        Assert.Equal(Utc("2026-01-12T09:00:00Z"), Weekdays().NextWorkingInstant(Utc("2026-01-11T23:00:00Z")));
    }

    [Fact]
    public void A_ticket_created_before_opening_or_after_closing_waits_for_the_next_opening()
    {
        var calendar = Weekdays();
        Assert.Equal(Utc("2026-01-12T10:00:00Z"), calendar.AddBusinessTime(Utc("2026-01-12T07:00:00Z"), TimeSpan.FromHours(1)));
        Assert.Equal(Utc("2026-01-13T10:00:00Z"), calendar.AddBusinessTime(Utc("2026-01-12T19:00:00Z"), TimeSpan.FromHours(1)));
    }

    [Fact]
    public void The_end_and_start_of_the_working_day_are_exact()
    {
        var calendar = Weekdays();
        Assert.Equal(Utc("2026-01-12T17:00:00Z"), calendar.AddBusinessTime(Utc("2026-01-12T09:00:00Z"), TimeSpan.FromHours(8)));
        Assert.Equal(Utc("2026-01-13T09:30:00Z"), calendar.AddBusinessTime(Utc("2026-01-12T17:00:00Z"), TimeSpan.FromMinutes(30)));
        Assert.Equal(Utc("2026-01-13T09:30:00Z"), calendar.AddBusinessTime(Utc("2026-01-12T16:59:00Z"), TimeSpan.FromMinutes(31)));
        Assert.True(calendar.IsWorking(Utc("2026-01-12T09:00:00Z")));
        Assert.False(calendar.IsWorking(Utc("2026-01-12T17:00:00Z")));
        Assert.False(calendar.IsWorking(Utc("2026-01-12T08:59:59Z")));
    }

    [Fact]
    public void A_full_day_closure_is_skipped()
    {
        // Monday 12 January is a holiday: the remaining 3.5 hours land on Tuesday.
        var calendar = Weekdays(exceptions: new CalendarException(new DateOnly(2026, 1, 12), "Bank holiday", []));
        Assert.Equal(Utc("2026-01-13T12:30:00Z"), calendar.AddBusinessTime(Utc("2026-01-09T16:30:00Z"), TimeSpan.FromHours(4)));
        Assert.Equal(TimeSpan.FromMinutes(30), calendar.BusinessTimeBetween(Utc("2026-01-09T16:30:00Z"), Utc("2026-01-13T09:00:00Z")));
    }

    [Fact]
    public void Alternative_hours_replace_the_weekly_hours_of_that_date()
    {
        // Friday closes at 13:00; a Saturday opening is added for one date.
        var calendar = Weekdays(exceptions:
        [
            new CalendarException(new DateOnly(2026, 1, 9), "Early close", [new WorkInterval(9 * 60, 13 * 60)]),
            new CalendarException(new DateOnly(2026, 1, 10), "Extra Saturday", [new WorkInterval(10 * 60, 12 * 60)]),
        ]);
        Assert.Equal(Utc("2026-01-09T13:00:00Z"), calendar.AddBusinessTime(Utc("2026-01-09T12:00:00Z"), TimeSpan.FromHours(1)));
        Assert.Equal(Utc("2026-01-10T10:30:00Z"), calendar.AddBusinessTime(Utc("2026-01-09T12:30:00Z"), TimeSpan.FromHours(1)));
    }

    [Fact]
    public void Split_shifts_skip_the_break()
    {
        var calendar = BusinessCalendar.Create(new CalendarDefinition("UTC", [new WeeklyDay(DayOfWeek.Monday, [new WorkInterval(9 * 60, 12 * 60), new WorkInterval(13 * 60, 17 * 60)])], []));
        Assert.Equal(Utc("2026-01-12T13:30:00Z"), calendar.AddBusinessTime(Utc("2026-01-12T11:00:00Z"), TimeSpan.FromHours(1.5)));
    }

    [Fact]
    public void Spring_forward_makes_the_short_day_five_working_hours_in_london()
    {
        // 2026-03-29: clocks jump 01:00 -> 02:00. A 00:00-06:00 local day lasts 5 real hours (00:00Z-05:00Z).
        var calendar = EveryDay("Europe/London", new WorkInterval(0, 6 * 60));
        Assert.Equal(TimeSpan.FromHours(5), calendar.BusinessTimeBetween(Utc("2026-03-29T00:00:00Z"), Utc("2026-03-29T06:00:00Z")));
        Assert.Equal(Utc("2026-03-29T05:00:00Z"), calendar.AddBusinessTime(Utc("2026-03-29T00:00:00Z"), TimeSpan.FromHours(5)));
        // A sixth hour does not fit: it starts the next local day (00:00 BST on the 30th is 23:00Z on the 29th).
        Assert.Equal(Utc("2026-03-29T23:00:00Z") + TimeSpan.FromHours(1), calendar.AddBusinessTime(Utc("2026-03-29T00:00:00Z"), TimeSpan.FromHours(6))); // 00:00 BST on the 30th is 23:00Z
    }

    [Fact]
    public void Autumn_fallback_makes_the_long_day_seven_working_hours_in_london()
    {
        // 2026-10-25: clocks go back 02:00 -> 01:00. A 00:00-06:00 local day lasts 7 real hours (23:00Z on the 24th to 06:00Z).
        var calendar = EveryDay("Europe/London", new WorkInterval(0, 6 * 60));
        var start = Utc("2026-10-24T23:00:00Z");
        Assert.Equal(TimeSpan.FromHours(7), calendar.BusinessTimeBetween(start, Utc("2026-10-25T06:00:00Z")));
        Assert.Equal(Utc("2026-10-25T06:00:00Z"), calendar.AddBusinessTime(start, TimeSpan.FromHours(7)));
    }

    [Fact]
    public void An_office_day_around_the_clock_change_keeps_local_opening_hours()
    {
        // Friday 27 March 2026 16:30 GMT + 4h: Monday 30 March is BST, so 09:00 local is 08:00Z and the deadline is 12:30 BST = 11:30Z.
        var calendar = Weekdays();
        Assert.Equal(Utc("2026-03-30T11:30:00Z"), calendar.AddBusinessTime(Utc("2026-03-27T16:30:00Z"), TimeSpan.FromHours(4)));
    }

    [Fact]
    public void New_york_spring_forward_is_handled_the_same_way()
    {
        // 2026-03-08 in New York: 00:00-06:00 local lasts 5 hours (05:00Z to 10:00Z, since 06:00 EDT = 10:00Z).
        var calendar = EveryDay("America/New_York", new WorkInterval(0, 6 * 60));
        Assert.Equal(TimeSpan.FromHours(5), calendar.BusinessTimeBetween(Utc("2026-03-08T05:00:00Z"), Utc("2026-03-08T10:00:00Z")));
    }

    [Fact]
    public void An_interval_that_starts_inside_the_spring_gap_starts_after_it()
    {
        // 01:30-03:00 local on 2026-03-29 in London: 01:30 does not exist, so working time is 02:00-03:00 BST = 01:00Z-02:00Z.
        var calendar = EveryDay("Europe/London", new WorkInterval(90, 180));
        Assert.Equal(TimeSpan.FromHours(1), calendar.BusinessTimeBetween(Utc("2026-03-29T00:00:00Z"), Utc("2026-03-29T05:00:00Z")));
    }

    [Fact]
    public void Business_time_between_matches_add_business_time()
    {
        var calendar = Weekdays();
        var start = Utc("2026-01-09T15:10:00Z");
        var due = calendar.AddBusinessTime(start, TimeSpan.FromHours(10))!.Value;
        Assert.Equal(TimeSpan.FromHours(10), calendar.BusinessTimeBetween(start, due));
        Assert.Equal(TimeSpan.Zero, calendar.BusinessTimeBetween(due, start));
    }

    [Fact]
    public void Always_on_counts_wall_clock_time()
    {
        Assert.Equal(Utc("2026-01-10T02:00:00Z"), BusinessCalendar.AlwaysOn.AddBusinessTime(Utc("2026-01-09T22:00:00Z"), TimeSpan.FromHours(4)));
    }

    [Fact]
    public void A_calendar_without_working_time_returns_null_instead_of_looping()
    {
        var calendar = BusinessCalendar.Create(new CalendarDefinition("UTC", [], []));
        Assert.Null(calendar.AddBusinessTime(Utc("2026-01-09T22:00:00Z"), TimeSpan.FromHours(1)));
        Assert.Null(calendar.NextWorkingInstant(Utc("2026-01-09T22:00:00Z")));
    }

    [Fact]
    public void Unknown_time_zones_are_rejected()
    {
        Assert.Null(BusinessCalendar.FindZone("Mars/Olympus"));
        Assert.Throws<ArgumentException>(() => BusinessCalendar.Create(new CalendarDefinition("Mars/Olympus", [], [])));
    }
}
