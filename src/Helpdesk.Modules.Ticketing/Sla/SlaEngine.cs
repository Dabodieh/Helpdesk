using System.Text.Json;
using Helpdesk.Modules.Ticketing.Domain;
using Helpdesk.Modules.Ticketing.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Modules.Ticketing.Sla;

internal enum ChangeKind
{
    Created,
    RequesterMessage,
    AgentPublicReply,
    Status,
    Priority,
    Team,
    Assignee,
}

/// <summary>What just happened to a ticket. Produced by the code that made the change; consumed by the SLA engine (in the same transaction) and by automation (after commit).</summary>
internal sealed record TicketChange(ChangeKind Kind, string? From = null, string? To = null, Guid? MessageId = null)
{
    /// <summary>Identity of this change as an event: retried deliveries of the same change reuse it, so automation can recognise duplicates.</summary>
    public Guid EventId { get; init; } = Guid.CreateVersion7();
}

/// <summary>The policy as it was when it was applied. Stored on the ticket; never recomputed from today's configuration.</summary>
internal sealed record SlaSnapshot(
    Guid? PolicyId,
    string PolicyName,
    int PolicyOrder,
    string? CalendarName,
    CalendarDefinition? Calendar,
    int? FirstResponseMinutes,
    int? ResolutionMinutes,
    bool PauseWhenPending,
    int ApproachingLeadMinutes);

internal sealed record SlaNotification(Guid TicketId, Guid DepartmentId, string Clock, string Kind, int Cycle);

internal static class SlaClocks
{
    public const string FirstResponse = "FirstResponse";
    public const string Resolution = "Resolution";
}

internal static class SlaEventKinds
{
    public const string Applied = "Applied";
    public const string FirstResponseMet = "FirstResponseMet";
    public const string Paused = "Paused";
    public const string Resumed = "Resumed";
    public const string ResolutionMet = "ResolutionMet";
    public const string Reopened = "Reopened";
    public const string Approaching = "Approaching";
    public const string Breached = "Breached";
}

/// <summary>
/// The only place that decides how SLA clocks start, pause, resume, stop and restart. All business-time arithmetic goes through
/// <see cref="BusinessCalendar"/> and always uses the calendar frozen in the ticket's snapshot.
/// </summary>
internal sealed class SlaEngine(TicketingDbContext db)
{
    /// <summary>Picks the first active matching policy (explicit order) and starts its clocks. No match = no SLA.</summary>
    public async Task<TicketSla?> ApplyOnCreateAsync(Ticket ticket, string requesterEmailNormalised, DateTimeOffset now, CancellationToken ct)
    {
        var policies = await db.SlaPolicies.AsNoTracking()
            .Where(p => p.DepartmentId == ticket.DepartmentId && p.IsActive)
            .OrderBy(p => p.SortOrder).ThenBy(p => p.CreatedAt).ThenBy(p => p.Id)
            .ToListAsync(ct);
        var facts = new TicketFacts(ticket.Status, ticket.Priority, ticket.TeamId, ticket.AssigneeUserId, ticket.MailboxId, ticket.Subject, requesterEmailNormalised, SlaStates.None, SlaStates.None);
        var policy = policies.FirstOrDefault(p => ConditionEvaluator.AllMatch(ConditionEvaluator.Evaluate(StoredJson.Conditions(p.Conditions), facts)));
        if (policy is null)
        {
            return null;
        }

        BusinessHoursCalendar? calendarRow = policy.CalendarId is { } calendarId
            ? await db.BusinessHours.AsNoTracking().SingleOrDefaultAsync(c => c.Id == calendarId && c.DepartmentId == ticket.DepartmentId, ct)
            : null;
        var snapshot = new SlaSnapshot(
            policy.Id, policy.Name, policy.SortOrder, calendarRow?.Name,
            calendarRow is null ? null : CalendarOf(calendarRow),
            policy.FirstResponseMinutes, policy.ResolutionMinutes, policy.PauseWhenPending, policy.ApproachingLeadMinutes);
        var calendar = Calendar(snapshot);

        var sla = new TicketSla
        {
            TicketId = ticket.Id,
            DepartmentId = ticket.DepartmentId,
            PolicyId = policy.Id,
            Snapshot = StoredJson.Write(snapshot),
            AppliedAt = now,
            ApproachingLeadMinutes = policy.ApproachingLeadMinutes,
            FirstResponseTargetMinutes = policy.FirstResponseMinutes,
            ResolutionTargetMinutes = policy.ResolutionMinutes,
        };
        if (policy.FirstResponseMinutes is { } first)
        {
            sla.FirstResponseDueAt = calendar.AddBusinessTime(now, TimeSpan.FromMinutes(first));
        }

        if (policy.ResolutionMinutes is { } resolution)
        {
            // A ticket created already Pending with pause configured starts paused with the full target remaining.
            if (policy.PauseWhenPending && ticket.Status == TicketStatus.Pending)
            {
                sla.ResolutionPausedAt = now;
                sla.ResolutionRemainingSeconds = (long)TimeSpan.FromMinutes(resolution).TotalSeconds;
            }
            else
            {
                sla.ResolutionDueAt = calendar.AddBusinessTime(now, TimeSpan.FromMinutes(resolution));
            }
        }

        Recompute(sla);
        db.TicketSlas.Add(sla);
        AddEvent(sla, now, SlaEventKinds.Applied, null, new
        {
            policy = policy.Name,
            order = policy.SortOrder,
            calendar = calendarRow?.Name ?? "24x7",
            firstResponseMinutes = policy.FirstResponseMinutes,
            resolutionMinutes = policy.ResolutionMinutes,
            firstResponseDueAt = sla.FirstResponseDueAt,
            resolutionDueAt = sla.ResolutionDueAt,
            pauseWhenPending = policy.PauseWhenPending,
        });
        await db.SaveChangesAsync(ct);
        return sla;
    }

    /// <summary>Applies the effect of ticket changes to the ticket's clocks (inside the changing transaction). Returns notifications that must be published after commit.</summary>
    public async Task<TicketSla?> OnChangesAsync(Ticket ticket, IReadOnlyList<TicketChange> changes, DateTimeOffset now, CancellationToken ct)
    {
        var sla = await db.TicketSlas.IgnoreDepartmentScope().SingleOrDefaultAsync(s => s.TicketId == ticket.Id && s.DepartmentId == ticket.DepartmentId, ct);
        if (sla is null)
        {
            return null;
        }

        var snapshot = StoredJson.Read<SlaSnapshot>(sla.Snapshot);
        var calendar = Calendar(snapshot);
        var dirty = false;
        foreach (var change in changes)
        {
            switch (change.Kind)
            {
                case ChangeKind.AgentPublicReply:
                    dirty |= MeetFirstResponse(sla, now);
                    break;
                case ChangeKind.Status when Enum.TryParse<TicketStatus>(change.From, out var from) && Enum.TryParse<TicketStatus>(change.To, out var to):
                    dirty |= ApplyStatus(sla, snapshot, calendar, from, to, now);
                    break;
            }
        }

        if (dirty)
        {
            Recompute(sla);
            await db.SaveChangesAsync(ct);
        }

        return sla;
    }

    private bool MeetFirstResponse(TicketSla sla, DateTimeOffset now)
    {
        if (sla.FirstResponseTargetMinutes is null || sla.FirstResponseMetAt is not null)
        {
            return false;
        }

        sla.FirstResponseMetAt = now;
        var late = sla.FirstResponseDueAt is { } due && now > due;
        if (late)
        {
            sla.FirstResponseBreachedAt ??= sla.FirstResponseDueAt;
        }

        AddEvent(sla, now, SlaEventKinds.FirstResponseMet, SlaClocks.FirstResponse, new { late });
        return true;
    }

    /// <summary>
    /// The pause/resume/stop/restart rules, in one place. Open runs; Pending pauses the resolution clock only when the policy says so;
    /// Resolved and Closed stop it (met); leaving Resolved or Closed for Open or Pending starts a new resolution cycle with the full target.
    /// </summary>
    private bool ApplyStatus(TicketSla sla, SlaSnapshot snapshot, BusinessCalendar calendar, TicketStatus from, TicketStatus to, DateTimeOffset now)
    {
        if (sla.ResolutionTargetMinutes is not { } target || from == to)
        {
            return false;
        }

        var wasDone = from is TicketStatus.Resolved or TicketStatus.Closed;
        var isDone = to is TicketStatus.Resolved or TicketStatus.Closed;

        if (isDone)
        {
            if (sla.ResolutionMetAt is not null)
            {
                return false; // Resolved -> Closed: already met
            }

            var late = sla.ResolutionDueAt is { } due && now > due;
            sla.ResolutionMetAt = now;
            if (late)
            {
                sla.ResolutionBreachedAt ??= sla.ResolutionDueAt;
            }

            sla.ResolutionPausedAt = null;
            sla.ResolutionRemainingSeconds = null;
            AddEvent(sla, now, SlaEventKinds.ResolutionMet, SlaClocks.Resolution, new { late, cycle = sla.ResolutionCycle });
            return true;
        }

        if (wasDone)
        {
            var previousMetAt = sla.ResolutionMetAt;
            sla.ResolutionCycle++;
            sla.ResolutionMetAt = null;
            sla.ResolutionBreachedAt = null;
            sla.ResolutionWarnedAt = null;
            sla.ResolutionDueAt = null;
            sla.ResolutionPausedAt = null;
            sla.ResolutionRemainingSeconds = null;
            var full = TimeSpan.FromMinutes(target);
            if (snapshot.PauseWhenPending && to == TicketStatus.Pending)
            {
                sla.ResolutionPausedAt = now;
                sla.ResolutionRemainingSeconds = (long)full.TotalSeconds;
            }
            else
            {
                sla.ResolutionDueAt = calendar.AddBusinessTime(now, full);
            }

            AddEvent(sla, now, SlaEventKinds.Reopened, SlaClocks.Resolution, new { cycle = sla.ResolutionCycle, previousMetAt, dueAt = sla.ResolutionDueAt });
            return true;
        }

        // Open <-> Pending
        if (to == TicketStatus.Pending && snapshot.PauseWhenPending && sla.ResolutionPausedAt is null && sla.ResolutionDueAt is { } dueAt)
        {
            var remaining = calendar.BusinessTimeBetween(now, dueAt);
            sla.ResolutionPausedAt = now;
            sla.ResolutionRemainingSeconds = (long)remaining.TotalSeconds;
            sla.ResolutionDueAt = null;
            AddEvent(sla, now, SlaEventKinds.Paused, SlaClocks.Resolution, new { remainingSeconds = sla.ResolutionRemainingSeconds });
            return true;
        }

        if (to == TicketStatus.Open && sla.ResolutionPausedAt is not null)
        {
            var remaining = TimeSpan.FromSeconds(sla.ResolutionRemainingSeconds ?? 0);
            sla.ResolutionDueAt = calendar.AddBusinessTime(now, remaining);
            sla.ResolutionPausedAt = null;
            sla.ResolutionRemainingSeconds = null;
            AddEvent(sla, now, SlaEventKinds.Resumed, SlaClocks.Resolution, new { dueAt = sla.ResolutionDueAt });
            return true;
        }

        return false;
    }

    /// <summary>Records warnings and breaches that are due (run by the monitor; selects only rows whose indexed <c>next_event_at</c> has passed).</summary>
    public IReadOnlyList<SlaNotification> RecordDue(TicketSla sla, DateTimeOffset now)
    {
        var notes = new List<SlaNotification>();
        var lead = TimeSpan.FromMinutes(sla.ApproachingLeadMinutes);

        void Check(string clock, DateTimeOffset? due, DateTimeOffset? met, bool paused, ref DateTimeOffset? warned, ref DateTimeOffset? breached, int cycle)
        {
            if (due is not { } d || met is not null || paused)
            {
                return;
            }

            if (breached is null && now >= d)
            {
                breached = d;
                warned ??= d;
                AddEvent(sla, now, SlaEventKinds.Breached, clock, new { dueAt = d, cycle });
                notes.Add(new SlaNotification(sla.TicketId, sla.DepartmentId, clock, SlaEventKinds.Breached, cycle));
            }
            else if (warned is null && breached is null && now >= d - lead)
            {
                warned = now;
                AddEvent(sla, now, SlaEventKinds.Approaching, clock, new { dueAt = d, cycle });
                notes.Add(new SlaNotification(sla.TicketId, sla.DepartmentId, clock, SlaEventKinds.Approaching, cycle));
            }
        }

        var frWarned = sla.FirstResponseWarnedAt;
        var frBreached = sla.FirstResponseBreachedAt;
        Check(SlaClocks.FirstResponse, sla.FirstResponseDueAt, sla.FirstResponseMetAt, false, ref frWarned, ref frBreached, 1);
        sla.FirstResponseWarnedAt = frWarned;
        sla.FirstResponseBreachedAt = frBreached;

        var resWarned = sla.ResolutionWarnedAt;
        var resBreached = sla.ResolutionBreachedAt;
        Check(SlaClocks.Resolution, sla.ResolutionDueAt, sla.ResolutionMetAt, sla.ResolutionPausedAt is not null, ref resWarned, ref resBreached, sla.ResolutionCycle);
        sla.ResolutionWarnedAt = resWarned;
        sla.ResolutionBreachedAt = resBreached;

        Recompute(sla);
        return notes;
    }

    /// <summary>Derives the three indexed summary columns from the clocks.</summary>
    public static void Recompute(TicketSla sla)
    {
        var lead = TimeSpan.FromMinutes(sla.ApproachingLeadMinutes);
        DateTimeOffset? nextDue = null, nextWarn = null, nextEvent = null;

        void Take(DateTimeOffset? due, DateTimeOffset? met, bool paused, DateTimeOffset? warned, DateTimeOffset? breached)
        {
            if (due is not { } d || met is not null || paused)
            {
                return;
            }

            nextDue = Min(nextDue, d);
            nextWarn = Min(nextWarn, d - lead);
            if (breached is null)
            {
                nextEvent = Min(nextEvent, d);
                if (warned is null)
                {
                    nextEvent = Min(nextEvent, d - lead);
                }
            }
        }

        Take(sla.FirstResponseDueAt, sla.FirstResponseMetAt, false, sla.FirstResponseWarnedAt, sla.FirstResponseBreachedAt);
        Take(sla.ResolutionDueAt, sla.ResolutionMetAt, sla.ResolutionPausedAt is not null, sla.ResolutionWarnedAt, sla.ResolutionBreachedAt);
        sla.NextDueAt = nextDue;
        sla.NextWarnAt = nextWarn;
        sla.NextEventAt = nextEvent;
    }

    private static DateTimeOffset Min(DateTimeOffset? a, DateTimeOffset b) => a is { } x && x < b ? x : b;

    /// <summary>State of the first-response clock at <paramref name="now"/>.</summary>
    public static string FirstResponseState(TicketSla? sla, DateTimeOffset now) =>
        sla is null || sla.FirstResponseTargetMinutes is null
            ? SlaStates.None
            : ClockState(sla.FirstResponseDueAt, sla.FirstResponseMetAt, false, sla.ApproachingLeadMinutes, now);

    public static string ResolutionState(TicketSla? sla, DateTimeOffset now) =>
        sla is null || sla.ResolutionTargetMinutes is null
            ? SlaStates.None
            : ClockState(sla.ResolutionDueAt, sla.ResolutionMetAt, sla.ResolutionPausedAt is not null, sla.ApproachingLeadMinutes, now);

    private static string ClockState(DateTimeOffset? due, DateTimeOffset? met, bool paused, int leadMinutes, DateTimeOffset now)
    {
        if (met is { } m)
        {
            return due is { } d && m > d ? SlaStates.MetLate : SlaStates.Met;
        }

        if (paused)
        {
            return SlaStates.Paused;
        }

        if (due is not { } deadline)
        {
            return SlaStates.None;
        }

        return now >= deadline ? SlaStates.Breached : now >= deadline - TimeSpan.FromMinutes(leadMinutes) ? SlaStates.Approaching : SlaStates.Running;
    }

    public static CalendarDefinition CalendarOf(BusinessHoursCalendar row) =>
        new(row.TimeZoneId, StoredJson.Read<List<WeeklyDay>>(row.Weekly), StoredJson.Read<List<CalendarException>>(row.Exceptions));

    public static BusinessCalendar Calendar(SlaSnapshot snapshot) =>
        snapshot.Calendar is { } definition ? BusinessCalendar.Create(definition) : BusinessCalendar.AlwaysOn;

    private void AddEvent(TicketSla sla, DateTimeOffset at, string kind, string? clock, object detail) =>
        db.SlaEvents.Add(new SlaEvent
        {
            Id = Guid.CreateVersion7(),
            TicketId = sla.TicketId,
            DepartmentId = sla.DepartmentId,
            At = at,
            Kind = kind,
            Clock = clock,
            Detail = JsonSerializer.Serialize(detail, CalendarJson.Options),
        });
}
