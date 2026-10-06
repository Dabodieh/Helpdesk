using System.Globalization;
using Helpdesk.Modules.Organisation.Contracts;
using Helpdesk.Modules.Ticketing.Automation;
using Helpdesk.Modules.Ticketing.Domain;
using Helpdesk.Modules.Ticketing.Persistence;
using Helpdesk.Modules.Ticketing.Services;
using Helpdesk.SharedKernel.Database;
using Helpdesk.SharedKernel.Errors;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Helpdesk.Modules.Ticketing.Sla;

internal sealed record IntervalDto(string Start, string End);

internal sealed record WeeklyDayDto(string Day, IReadOnlyList<IntervalDto>? Intervals);

internal sealed record ExceptionDto(string Date, string? Name, IReadOnlyList<IntervalDto>? Intervals);

internal sealed record BusinessHoursRequest(string? Name, string? TimeZone, bool? IsActive, IReadOnlyList<WeeklyDayDto>? Weekly, IReadOnlyList<ExceptionDto>? Exceptions, string? Version);

internal sealed record BusinessHoursDto(
    Guid Id, Guid DepartmentId, string Name, string TimeZone, bool IsActive,
    IReadOnlyList<WeeklyDayDto> Weekly, IReadOnlyList<ExceptionDto> Exceptions, DateTimeOffset UpdatedAt, string Version);

internal sealed record DueCheckRequest(string? Start, int? Minutes);

internal sealed record DueCheckDto(DateTimeOffset Start, int Minutes, DateTimeOffset? DueAt, string TimeZone);

/// <summary>Business hours administration (permission department.sla.manage, enforced by the endpoint filter; every query also filters by the department itself).</summary>
internal sealed class BusinessHoursService(TicketingDbContext db, TicketingAudit audit, TimeProvider clock)
{
    public const int MaxCalendars = 10;
    public const int MaxExceptions = 400;
    private const string Object = "business_hours";

    public async Task<IReadOnlyList<BusinessHoursDto>> ListAsync(Guid departmentId, CancellationToken ct) =>
        [.. (await db.BusinessHours.AsNoTracking().Where(c => c.DepartmentId == departmentId).OrderBy(c => c.Name).ToListAsync(ct)).Select(ToDto)];

    public async Task<BusinessHoursDto> GetAsync(Guid departmentId, Guid id, CancellationToken ct) =>
        ToDto(await db.BusinessHours.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id && c.DepartmentId == departmentId, ct) ?? throw new NotFoundException());

    public async Task<BusinessHoursDto> CreateAsync(Guid departmentId, BusinessHoursRequest request, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        var parsed = Validate(errors, request, requireAll: true);
        TicketValidation.ThrowIfAny(errors);
        if (await db.BusinessHours.CountAsync(c => c.DepartmentId == departmentId, ct) >= MaxCalendars)
        {
            throw new ConflictException($"A department can keep at most {MaxCalendars} business hours calendars.", ProblemCodes.InvalidState);
        }

        var now = ClockExtensions.GetUtcNowMicroseconds(clock);
        var row = new BusinessHoursCalendar
        {
            Id = Guid.CreateVersion7(),
            DepartmentId = departmentId,
            Name = parsed.Name!,
            TimeZoneId = parsed.TimeZone!,
            IsActive = request.IsActive ?? true,
            Weekly = StoredJson.Write(parsed.Weekly!),
            Exceptions = StoredJson.Write(parsed.Exceptions!),
            CreatedAt = now,
            UpdatedAt = now,
        };
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.BusinessHours.Add(row);
        await SaveAsync(ct);
        await audit.WriteAsync(TicketAuditActions.BusinessHoursCreated, Object, row.Id, departmentId, null, new { name = row.Name, timeZone = row.TimeZoneId }, ct);
        await tx.CommitAsync(ct);
        return ToDto(row);
    }

    public async Task<BusinessHoursDto> UpdateAsync(Guid departmentId, Guid id, BusinessHoursRequest request, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        var version = TicketValidation.Version(errors, request.Version);
        var parsed = Validate(errors, request, requireAll: false);
        TicketValidation.ThrowIfAny(errors);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var row = await db.BusinessHours.SingleOrDefaultAsync(c => c.Id == id && c.DepartmentId == departmentId, ct) ?? throw new NotFoundException();
        if (row.Version != version)
        {
            throw new ConflictException("The calendar was changed elsewhere. Reload and retry.", ProblemCodes.VersionConflict);
        }

        db.Entry(row).Property(c => c.Version).OriginalValue = version;
        if (parsed.Name is not null)
        {
            row.Name = parsed.Name;
        }

        if (parsed.TimeZone is not null)
        {
            row.TimeZoneId = parsed.TimeZone;
        }

        if (request.IsActive is { } active)
        {
            row.IsActive = active;
        }

        if (parsed.Weekly is not null)
        {
            row.Weekly = StoredJson.Write(parsed.Weekly);
        }

        if (parsed.Exceptions is not null)
        {
            row.Exceptions = StoredJson.Write(parsed.Exceptions);
        }

        row.UpdatedAt = ClockExtensions.GetUtcNowMicroseconds(clock);
        await SaveAsync(ct);

        // Tickets that already carry an SLA keep their frozen snapshot: this edit only affects tickets created from now on.
        await audit.WriteAsync(TicketAuditActions.BusinessHoursUpdated, Object, row.Id, departmentId, null, new { name = row.Name, timeZone = row.TimeZoneId, isActive = row.IsActive }, ct);
        await tx.CommitAsync(ct);
        return ToDto(row);
    }

    /// <summary>"If a ticket arrived at this moment with this target, when would it be due?" (read-only explanation for administrators).</summary>
    public async Task<DueCheckDto> CheckDueAsync(Guid departmentId, Guid id, DueCheckRequest request, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        if (!DateTimeOffset.TryParse(request.Start, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var start))
        {
            errors["start"] = ["Provide a date and time (ISO 8601)."];
        }

        if (request.Minutes is not > 0 or > 525600)
        {
            errors["minutes"] = ["minutes must be between 1 and 525600."];
        }

        TicketValidation.ThrowIfAny(errors);
        var row = await db.BusinessHours.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id && c.DepartmentId == departmentId, ct) ?? throw new NotFoundException();
        var due = BusinessCalendar.Create(SlaEngine.CalendarOf(row)).AddBusinessTime(start.ToUniversalTime(), TimeSpan.FromMinutes(request.Minutes!.Value));
        return new DueCheckDto(start.ToUniversalTime(), request.Minutes!.Value, due, row.TimeZoneId);
    }

    private sealed record Parsed(string? Name, string? TimeZone, List<WeeklyDay>? Weekly, List<CalendarException>? Exceptions);

    private static Parsed Validate(Dictionary<string, string[]> errors, BusinessHoursRequest r, bool requireAll)
    {
        string? name = null;
        if (r.Name is not null || requireAll)
        {
            name = r.Name?.Trim();
            if (string.IsNullOrEmpty(name) || name.Length > 100)
            {
                errors["name"] = ["Name must be 1 to 100 characters."];
                name = null;
            }
        }

        string? zone = null;
        if (r.TimeZone is not null || requireAll)
        {
            zone = BusinessCalendar.FindZone(r.TimeZone) is { } found ? found.Id : null;
            if (zone is null)
            {
                errors["timeZone"] = ["Choose a valid IANA time zone, for example Europe/London."];
            }
        }

        List<WeeklyDay>? weekly = null;
        if (r.Weekly is not null || requireAll)
        {
            weekly = ParseWeekly(errors, r.Weekly);
        }

        List<CalendarException>? exceptions = null;
        if (r.Exceptions is not null || requireAll)
        {
            exceptions = ParseExceptions(errors, r.Exceptions ?? []);
        }

        return new Parsed(name, zone, weekly, exceptions);
    }

    private static List<WeeklyDay>? ParseWeekly(Dictionary<string, string[]> errors, IReadOnlyList<WeeklyDayDto>? days)
    {
        var result = new List<WeeklyDay>();
        var seen = new HashSet<DayOfWeek>();
        foreach (var (day, i) in (days ?? []).Select((d, i) => (d, i)))
        {
            if (!Enum.TryParse<DayOfWeek>(day.Day, ignoreCase: true, out var dow) || !Enum.IsDefined(dow) || !seen.Add(dow))
            {
                errors[$"weekly[{i}]"] = ["Use each weekday name once."];
                continue;
            }

            var intervals = ParseIntervals(errors, $"weekly[{i}]", day.Intervals);
            if (intervals is not null && intervals.Count > 0)
            {
                result.Add(new WeeklyDay(dow, intervals));
            }
        }

        if (result.Count == 0 && !errors.Keys.Any(k => k.StartsWith("weekly", StringComparison.Ordinal)))
        {
            errors["weekly"] = ["Add working hours for at least one weekday."];
        }

        return result;
    }

    private static List<CalendarException>? ParseExceptions(Dictionary<string, string[]> errors, IReadOnlyList<ExceptionDto> list)
    {
        if (list.Count > MaxExceptions)
        {
            errors["exceptions"] = [$"At most {MaxExceptions} exceptions are allowed."];
            return null;
        }

        var result = new List<CalendarException>();
        var seen = new HashSet<DateOnly>();
        foreach (var (e, i) in list.Select((e, i) => (e, i)))
        {
            if (!DateOnly.TryParseExact(e.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) || !seen.Add(date))
            {
                errors[$"exceptions[{i}]"] = ["Use a unique date as yyyy-MM-dd."];
                continue;
            }

            var name = e.Name?.Trim();
            if (name is { Length: > 100 })
            {
                errors[$"exceptions[{i}]"] = ["Name is at most 100 characters."];
                continue;
            }

            var intervals = ParseIntervals(errors, $"exceptions[{i}]", e.Intervals ?? []) ?? [];
            result.Add(new CalendarException(date, string.IsNullOrEmpty(name) ? null : name, intervals));
        }

        return [.. result.OrderBy(x => x.Date)];
    }

    private static List<WorkInterval>? ParseIntervals(Dictionary<string, string[]> errors, string path, IReadOnlyList<IntervalDto>? intervals)
    {
        var result = new List<WorkInterval>();
        foreach (var interval in intervals ?? [])
        {
            if (!TryMinute(interval.Start, out var start) || !TryMinute(interval.End, out var end) || start >= end || start >= 1440)
            {
                errors[path] = ["Each interval needs a start before its end, as HH:mm (end may be 24:00)."];
                return null;
            }

            result.Add(new WorkInterval(start, end));
        }

        var sorted = result.OrderBy(i => i.StartMinute).ToList();
        for (var i = 1; i < sorted.Count; i++)
        {
            if (sorted[i].StartMinute < sorted[i - 1].EndMinute)
            {
                errors[path] = ["Intervals on the same day must not overlap."];
                return null;
            }
        }

        return sorted;
    }

    private static bool TryMinute(string? text, out int minute)
    {
        minute = 0;
        if (text is null || text.Length != 5 || text[2] != ':' || !int.TryParse(text.AsSpan(0, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var h)
            || !int.TryParse(text.AsSpan(3, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var m) || m > 59 || h > 24 || (h == 24 && m != 0))
        {
            return false;
        }

        minute = h * 60 + m;
        return true;
    }

    private static string Format(int minute) => string.Create(CultureInfo.InvariantCulture, $"{minute / 60:00}:{minute % 60:00}");

    private static BusinessHoursDto ToDto(BusinessHoursCalendar c)
    {
        var weekly = StoredJson.Read<List<WeeklyDay>>(c.Weekly);
        var exceptions = StoredJson.Read<List<CalendarException>>(c.Exceptions);
        return new BusinessHoursDto(
            c.Id, c.DepartmentId, c.Name, c.TimeZoneId, c.IsActive,
            [.. weekly.Select(w => new WeeklyDayDto(w.Day.ToString(), [.. w.Intervals.Select(i => new IntervalDto(Format(i.StartMinute), Format(i.EndMinute)))]))],
            [.. exceptions.Select(e => new ExceptionDto(e.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), e.Name, [.. e.Intervals.Select(i => new IntervalDto(Format(i.StartMinute), Format(i.EndMinute)))]))],
            c.UpdatedAt, ConcurrencyVersion.ToToken(c.Version));
    }

    private async Task SaveAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictException("The calendar was changed elsewhere. Reload and retry.", ProblemCodes.VersionConflict);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw new ConflictException("A calendar with this name already exists.", ProblemCodes.Duplicate);
        }
    }
}

internal sealed record SlaPolicyRequest(
    string? Name, bool? IsActive, IReadOnlyList<RuleCondition>? Conditions, Guid? CalendarId, bool? ClearCalendar,
    int? FirstResponseMinutes, int? ResolutionMinutes, bool? ClearFirstResponse, bool? ClearResolution,
    bool? PauseWhenPending, int? ApproachingLeadMinutes, string? Version);

internal sealed record SlaPolicyDto(
    Guid Id, Guid DepartmentId, string Name, bool IsActive, int Order, IReadOnlyList<RuleCondition> Conditions, Guid? CalendarId, string? CalendarName,
    int? FirstResponseMinutes, int? ResolutionMinutes, bool PauseWhenPending, int ApproachingLeadMinutes, DateTimeOffset UpdatedAt, string Version);

internal sealed record ReorderRequest(IReadOnlyList<Guid>? Ids);

/// <summary>SLA policy administration. Policies are ordered explicitly; the first active match wins; nothing is combined and existing tickets keep their snapshot.</summary>
internal sealed class SlaPolicyService(TicketingDbContext db, TicketingAudit audit, RuleReferenceChecker references, TimeProvider clock)
{
    public const int MaxPolicies = 50;
    private const string Object = "sla_policy";

    public async Task<IReadOnlyList<SlaPolicyDto>> ListAsync(Guid departmentId, CancellationToken ct)
    {
        var rows = await db.SlaPolicies.AsNoTracking().Where(p => p.DepartmentId == departmentId).OrderBy(p => p.SortOrder).ThenBy(p => p.CreatedAt).ThenBy(p => p.Id).ToListAsync(ct);
        var names = await CalendarNamesAsync(departmentId, ct);
        return [.. rows.Select(p => ToDto(p, names))];
    }

    public async Task<SlaPolicyDto> CreateAsync(Guid departmentId, SlaPolicyRequest request, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        var shape = Validate(errors, request, creating: true);
        TicketValidation.ThrowIfAny(errors);
        await CheckReferencesAsync(departmentId, shape.Conditions, request.CalendarId, ct);
        if (await db.SlaPolicies.CountAsync(p => p.DepartmentId == departmentId, ct) >= MaxPolicies)
        {
            throw new ConflictException($"A department can keep at most {MaxPolicies} SLA policies.", ProblemCodes.InvalidState);
        }

        var now = ClockExtensions.GetUtcNowMicroseconds(clock);
        var last = await db.SlaPolicies.Where(p => p.DepartmentId == departmentId).MaxAsync(p => (int?)p.SortOrder, ct) ?? 0;
        var row = new SlaPolicy
        {
            Id = Guid.CreateVersion7(),
            DepartmentId = departmentId,
            Name = shape.Name!,
            IsActive = request.IsActive ?? true,
            SortOrder = last + 10,
            Conditions = StoredJson.Write(shape.Conditions!),
            CalendarId = request.CalendarId,
            FirstResponseMinutes = request.FirstResponseMinutes,
            ResolutionMinutes = request.ResolutionMinutes,
            PauseWhenPending = request.PauseWhenPending ?? false,
            ApproachingLeadMinutes = request.ApproachingLeadMinutes ?? 60,
            CreatedAt = now,
            UpdatedAt = now,
        };
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.SlaPolicies.Add(row);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(TicketAuditActions.SlaPolicyCreated, Object, row.Id, departmentId, null, Summary(row), ct);
        await tx.CommitAsync(ct);
        return ToDto(row, await CalendarNamesAsync(departmentId, ct));
    }

    public async Task<SlaPolicyDto> UpdateAsync(Guid departmentId, Guid id, SlaPolicyRequest request, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        var version = TicketValidation.Version(errors, request.Version);
        var shape = Validate(errors, request, creating: false);
        TicketValidation.ThrowIfAny(errors);
        await CheckReferencesAsync(departmentId, shape.Conditions, request.CalendarId, ct);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var row = await db.SlaPolicies.SingleOrDefaultAsync(p => p.Id == id && p.DepartmentId == departmentId, ct) ?? throw new NotFoundException();
        if (row.Version != version)
        {
            throw new ConflictException("The policy was changed elsewhere. Reload and retry.", ProblemCodes.VersionConflict);
        }

        db.Entry(row).Property(p => p.Version).OriginalValue = version;
        row.Name = shape.Name ?? row.Name;
        row.IsActive = request.IsActive ?? row.IsActive;
        if (shape.Conditions is not null)
        {
            row.Conditions = StoredJson.Write(shape.Conditions);
        }

        if (request.ClearCalendar == true)
        {
            row.CalendarId = null;
        }
        else if (request.CalendarId is { } calendarId)
        {
            row.CalendarId = calendarId;
        }

        row.FirstResponseMinutes = request.ClearFirstResponse == true ? null : request.FirstResponseMinutes ?? row.FirstResponseMinutes;
        row.ResolutionMinutes = request.ClearResolution == true ? null : request.ResolutionMinutes ?? row.ResolutionMinutes;
        row.PauseWhenPending = request.PauseWhenPending ?? row.PauseWhenPending;
        row.ApproachingLeadMinutes = request.ApproachingLeadMinutes ?? row.ApproachingLeadMinutes;
        if (row.FirstResponseMinutes is null && row.ResolutionMinutes is null)
        {
            throw new RequestValidationException(new Dictionary<string, string[]> { ["firstResponseMinutes"] = ["Keep at least one target (first response or resolution)."] });
        }

        row.UpdatedAt = ClockExtensions.GetUtcNowMicroseconds(clock);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictException("The policy was changed elsewhere. Reload and retry.", ProblemCodes.VersionConflict);
        }

        // Existing tickets are never recalculated: they carry the snapshot taken when their SLA was applied.
        await audit.WriteAsync(TicketAuditActions.SlaPolicyUpdated, Object, row.Id, departmentId, null, Summary(row), ct);
        await tx.CommitAsync(ct);
        return ToDto(row, await CalendarNamesAsync(departmentId, ct));
    }

    /// <summary>Renumbers the department's policies 10, 20, 30... in the given order (all ids must be listed).</summary>
    public async Task<IReadOnlyList<SlaPolicyDto>> ReorderAsync(Guid departmentId, ReorderRequest request, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var rows = await db.SlaPolicies.Where(p => p.DepartmentId == departmentId).ToListAsync(ct);
        var ids = request.Ids ?? [];
        if (ids.Count != rows.Count || ids.Distinct().Count() != ids.Count || !ids.All(i => rows.Any(r => r.Id == i)))
        {
            throw new RequestValidationException(new Dictionary<string, string[]> { ["ids"] = ["List every policy of the department exactly once."] });
        }

        var now = ClockExtensions.GetUtcNowMicroseconds(clock);
        for (var i = 0; i < ids.Count; i++)
        {
            var row = rows.Single(r => r.Id == ids[i]);
            if (row.SortOrder != (i + 1) * 10)
            {
                row.SortOrder = (i + 1) * 10;
                row.UpdatedAt = now;
            }
        }

        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(TicketAuditActions.SlaPolicyUpdated, Object, departmentId, departmentId, null, new { reordered = ids.Count }, ct);
        await tx.CommitAsync(ct);
        return await ListAsync(departmentId, ct);
    }

    private sealed record Shape(string? Name, List<RuleCondition>? Conditions);

    private static Shape Validate(Dictionary<string, string[]> errors, SlaPolicyRequest r, bool creating)
    {
        string? name = null;
        if (r.Name is not null || creating)
        {
            name = r.Name?.Trim();
            if (string.IsNullOrEmpty(name) || name.Length > 100)
            {
                errors["name"] = ["Name must be 1 to 100 characters."];
                name = null;
            }
        }

        foreach (var (key, value) in ConditionCatalogue.Validate(r.Conditions, ConditionCatalogue.PolicyFields, "conditions"))
        {
            errors[key] = value;
        }

        if (creating && r.FirstResponseMinutes is null && r.ResolutionMinutes is null)
        {
            errors["firstResponseMinutes"] = ["Set a first response target, a resolution target, or both."];
        }

        foreach (var (field, minutes) in new[] { ("firstResponseMinutes", r.FirstResponseMinutes), ("resolutionMinutes", r.ResolutionMinutes) })
        {
            if (minutes is < 1 or > 525600)
            {
                errors[field] = ["Targets are between 1 minute and one year (525600)."];
            }
        }

        if (r.ApproachingLeadMinutes is < 0 or > 10080)
        {
            errors["approachingLeadMinutes"] = ["The warning lead is between 0 and 10080 minutes."];
        }

        return new Shape(name, r.Conditions is null ? (creating ? [] : null) : [.. ConditionCatalogue.Normalise(r.Conditions)]);
    }

    private async Task CheckReferencesAsync(Guid departmentId, List<RuleCondition>? conditions, Guid? calendarId, CancellationToken ct)
    {
        var errors = conditions is null ? [] : await references.CheckAsync(departmentId, conditions, [], ct);
        if (calendarId is { } id && !await db.BusinessHours.AnyAsync(c => c.Id == id && c.DepartmentId == departmentId && c.IsActive, ct))
        {
            errors["calendarId"] = ["Choose an active business hours calendar of this department."];
        }

        TicketValidation.ThrowIfAny(errors);
    }

    private async Task<Dictionary<Guid, string>> CalendarNamesAsync(Guid departmentId, CancellationToken ct) =>
        await db.BusinessHours.AsNoTracking().Where(c => c.DepartmentId == departmentId).ToDictionaryAsync(c => c.Id, c => c.Name, ct);

    private static object Summary(SlaPolicy p) =>
        new { name = p.Name, isActive = p.IsActive, order = p.SortOrder, firstResponseMinutes = p.FirstResponseMinutes, resolutionMinutes = p.ResolutionMinutes, pauseWhenPending = p.PauseWhenPending };

    private static SlaPolicyDto ToDto(SlaPolicy p, Dictionary<Guid, string> calendars) =>
        new(p.Id, p.DepartmentId, p.Name, p.IsActive, p.SortOrder, StoredJson.Conditions(p.Conditions), p.CalendarId,
            p.CalendarId is { } c && calendars.TryGetValue(c, out var n) ? n : null,
            p.FirstResponseMinutes, p.ResolutionMinutes, p.PauseWhenPending, p.ApproachingLeadMinutes, p.UpdatedAt, ConcurrencyVersion.ToToken(p.Version));
}
