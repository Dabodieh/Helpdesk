using System.Globalization;
using Helpdesk.Modules.Organisation.Contracts;
using Helpdesk.Modules.Ticketing.Domain;
using Helpdesk.Modules.Ticketing.Persistence;
using Helpdesk.SharedKernel.Authorization;
using Helpdesk.SharedKernel.Errors;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Modules.Ticketing.Services;

internal sealed record VolumeTeamDto(Guid? TeamId, string Name, int Received, int Resolved);

/// <summary>Tickets received and resolved in a period, for ONE department. <see cref="To"/> is inclusive; days are Europe/London calendar days.</summary>
internal sealed record DepartmentVolumeDto(Guid DepartmentId, string DepartmentName, DateOnly From, DateOnly To, int Received, int Resolved, IReadOnlyList<VolumeTeamDto> Teams);

/// <summary>
/// Historical volume for ONE explicitly named department (tickets.read on it, enforced by the endpoint filter; every query also runs through the department-scope
/// filter). Counts only, never per person. Definitions follow REPORTING_DEFINITIONS.md:
/// RECEIVED = tickets created in the period, a ticket merged away later included (<see cref="QueuePredicates.CreatedBetween"/>).
/// RESOLVED = distinct tickets that moved into Resolved, or into Closed from an open state, in the period (read from the content-free lifecycle record); a ticket merged
/// away is not a resolution. A reopened and re-resolved ticket counts once. Teams are the ticket's CURRENT team, because the lifecycle record keeps the team only as a change.
/// </summary>
internal sealed class DepartmentVolumeReportService(TicketingDbContext db, IDepartmentScope scope, IOrganisationDirectory organisation, TimeProvider clock)
{
    public const int MaxDays = 366;
    public const int DefaultDays = 30;
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");

    public async Task<DepartmentVolumeDto> GetAsync(Guid departmentId, string? from, string? to, CancellationToken ct)
    {
        await scope.EnsureLoadedAsync(ct);
        var department = await organisation.FindDepartmentAsync(departmentId, ct) ?? throw new NotFoundException();
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), London).DateTime);
        var last = Parse("to", to) ?? today;
        var first = Parse("from", from) ?? last.AddDays(1 - DefaultDays);
        if (first > last)
        {
            throw new RequestValidationException("from", "from must not be after to.");
        }

        if (last.DayNumber - first.DayNumber >= MaxDays)
        {
            throw new RequestValidationException("from", $"The period cannot be longer than {MaxDays} days.");
        }

        var start = StartOfDay(first);
        var end = StartOfDay(last.AddDays(1));
        var tickets = db.Tickets.AsNoTracking().Where(t => t.DepartmentId == departmentId);
        var received = tickets.Where(QueuePredicates.CreatedBetween(start, end));
        var resolvedIds = db.TicketLifecycleEvents.AsNoTracking().Where(e => e.DepartmentId == departmentId && e.Kind == "status" && e.OccurredAt >= start && e.OccurredAt < end
                && (e.ToValue == nameof(TicketStatus.Resolved) || (e.ToValue == nameof(TicketStatus.Closed) && e.FromValue != nameof(TicketStatus.Resolved))))
            .Select(e => e.TicketId).Distinct();
        var resolved = tickets.Where(t => t.MergedIntoTicketId == null && resolvedIds.Contains(t.Id));

        var receivedByTeam = await received.GroupBy(t => t.TeamId).Select(g => new { TeamId = g.Key, Count = g.Count() }).ToListAsync(ct);
        var resolvedByTeam = await resolved.GroupBy(t => t.TeamId).Select(g => new { TeamId = g.Key, Count = g.Count() }).ToListAsync(ct);
        var teamIds = receivedByTeam.Select(x => x.TeamId).Concat(resolvedByTeam.Select(x => x.TeamId)).OfType<Guid>().Distinct().ToArray();
        var names = await organisation.GetTeamsAsync(teamIds, ct);
        string Name(Guid? id) => id is null ? "No team" : names.TryGetValue(id.Value, out var info) && info.DepartmentId == departmentId ? info.Name : "Unknown team";

        var teams = receivedByTeam.Select(x => x.TeamId).Concat(resolvedByTeam.Select(x => x.TeamId)).Distinct()
            .Select(id => new VolumeTeamDto(id, Name(id), receivedByTeam.FirstOrDefault(x => x.TeamId == id)?.Count ?? 0, resolvedByTeam.FirstOrDefault(x => x.TeamId == id)?.Count ?? 0))
            .OrderByDescending(t => t.Received).ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();

        return new DepartmentVolumeDto(department.Id, department.Name, first, last, teams.Sum(t => t.Received), teams.Sum(t => t.Resolved), teams);
    }

    private static DateOnly? Parse(string field, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : throw new RequestValidationException(field, $"{field} must be a date as yyyy-MM-dd.");
    }

    private static DateTimeOffset StartOfDay(DateOnly day)
    {
        var local = day.ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, London), TimeSpan.Zero);
    }
}
