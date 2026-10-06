using System.Text.Json;
using Helpdesk.Modules.Ticketing.Domain;
using Helpdesk.Modules.Ticketing.Persistence;
using Helpdesk.SharedKernel.Errors;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Modules.Ticketing.Sla;

/// <summary>The compact SLA state shown in queues and the ticket header.</summary>
internal sealed record SlaSummaryDto(string State, DateTimeOffset? DueAt, string? Clock, string FirstResponse, string Resolution);

internal sealed record SlaClockDto(int? TargetMinutes, DateTimeOffset? DueAt, DateTimeOffset? MetAt, DateTimeOffset? PausedAt, string State, int? Cycle);

internal sealed record SlaEventDto(DateTimeOffset At, string Kind, string? Clock, JsonElement Detail);

/// <summary>Everything that explains a ticket's deadlines: the frozen policy, both clocks and the event history. Content free.</summary>
internal sealed record TicketSlaDto(
    string PolicyName, string Calendar, string TimeZone, bool PauseWhenPending, DateTimeOffset AppliedAt,
    SlaClockDto FirstResponse, SlaClockDto Resolution, IReadOnlyList<SlaEventDto> Events);

internal static class SlaSummary
{
    public static SlaSummaryDto? Of(TicketSla? sla, DateTimeOffset now)
    {
        if (sla is null)
        {
            return null;
        }

        var first = SlaEngine.FirstResponseState(sla, now);
        var resolution = SlaEngine.ResolutionState(sla, now);
        string[] states = [first, resolution];
        var state = states.Contains(SlaStates.Breached) ? SlaStates.Breached
            : states.Contains(SlaStates.Approaching) ? SlaStates.Approaching
            : states.Contains(SlaStates.Running) ? SlaStates.Running
            : states.Contains(SlaStates.Paused) ? SlaStates.Paused
            : states.Contains(SlaStates.MetLate) ? SlaStates.MetLate
            : states.Contains(SlaStates.Met) ? SlaStates.Met
            : SlaStates.None;
        var clock = sla.NextDueAt is null ? null
            : sla.FirstResponseDueAt == sla.NextDueAt && sla.FirstResponseMetAt is null ? SlaClocks.FirstResponse
            : SlaClocks.Resolution;
        return new SlaSummaryDto(state, sla.NextDueAt, clock, first, resolution);
    }
}

/// <summary>Ticket-level SLA read (permission tickets.read, resolved through the caller's department scope like every ticket route).</summary>
internal sealed class SlaReadService(TicketingDbContext db, Services.TicketResolver resolver, TimeProvider clock)
{
    public async Task<TicketSlaDto?> GetAsync(string route, CancellationToken ct)
    {
        var key = await resolver.FindAsync(route, ct) ?? throw new NotFoundException();
        var sla = await db.TicketSlas.AsNoTracking().SingleOrDefaultAsync(s => s.TicketId == key.Id, ct);
        if (sla is null)
        {
            return null;
        }

        var now = clock.GetUtcNow();
        var snapshot = StoredJson.Read<SlaSnapshot>(sla.Snapshot);
        var events = await db.SlaEvents.AsNoTracking().Where(e => e.TicketId == key.Id).OrderBy(e => e.At).ThenBy(e => e.Id).Take(200).ToListAsync(ct);
        return new TicketSlaDto(
            snapshot.PolicyName,
            snapshot.CalendarName ?? "24x7",
            snapshot.Calendar?.TimeZoneId ?? "UTC",
            snapshot.PauseWhenPending,
            sla.AppliedAt,
            new SlaClockDto(sla.FirstResponseTargetMinutes, sla.FirstResponseDueAt, sla.FirstResponseMetAt, null, SlaEngine.FirstResponseState(sla, now), null),
            new SlaClockDto(sla.ResolutionTargetMinutes, sla.ResolutionDueAt, sla.ResolutionMetAt, sla.ResolutionPausedAt, SlaEngine.ResolutionState(sla, now), sla.ResolutionCycle),
            [.. events.Select(e => new SlaEventDto(e.At, e.Kind, e.Clock, JsonDocument.Parse(e.Detail).RootElement.Clone()))]);
    }
}
