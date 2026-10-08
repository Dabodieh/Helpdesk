using System.Linq.Expressions;
using Helpdesk.Modules.Ticketing.Domain;
using Helpdesk.Modules.Ticketing.Persistence;

namespace Helpdesk.Modules.Ticketing.Services;

/// <summary>
/// The definitions of "actionable" work shared by the queue list filters and the department dashboard, so a dashboard figure and the queue it links to are
/// the same set by construction. SLA predicates read the indexed summary columns of ticket_sla (department scoped like everything else).
/// </summary>
internal static class QueuePredicates
{
    /// <summary>"Due soon" window of the <c>duesoon</c> filter.</summary>
    public static readonly TimeSpan DueSoonWindow = TimeSpan.FromHours(4);

    /// <summary>The default queue set: Open or Pending, never a merged source ticket.</summary>
    public static readonly Expression<Func<Ticket, bool>> Backlog =
        t => t.MergedIntoTicketId == null && (t.Status == TicketStatus.Open || t.Status == TicketStatus.Pending);

    /// <summary>Past a deadline that is still unmet (an ACTIVE overdue ticket; history of earlier breaches is not part of this).</summary>
    public static Expression<Func<Ticket, bool>> SlaBreached(TicketingDbContext db, DateTimeOffset now) =>
        t => db.TicketSlas.Any(s => s.TicketId == t.Id && s.NextDueAt != null && s.NextDueAt <= now);

    /// <summary>Inside the warning window but not yet late.</summary>
    public static Expression<Func<Ticket, bool>> SlaApproaching(TicketingDbContext db, DateTimeOffset now) =>
        t => db.TicketSlas.Any(s => s.TicketId == t.Id && s.NextWarnAt != null && s.NextWarnAt <= now && s.NextDueAt > now);

    /// <summary>
    /// Awaiting first response (D26): no public message by a human agent yet. It is read from the message stream and is independent of any SLA policy, so a department
    /// without SLA configuration still sees its unanswered tickets. An acknowledgement is not a ticket message, so it can never satisfy it.
    /// </summary>
    public static Expression<Func<Ticket, bool>> AwaitingFirstResponse(TicketingDbContext db) =>
        t => !db.Messages.Any(m => m.TicketId == t.Id && m.Kind == MessageKind.Public && m.AuthorType == AuthorType.Agent);

    /// <summary>The SLA-specific counterpart, labelled as such: a first-response target exists and its clock has not been met or stopped.</summary>
    public static Expression<Func<Ticket, bool>> SlaFirstResponsePending(TicketingDbContext db) =>
        t => db.TicketSlas.Any(s => s.TicketId == t.Id && s.FirstResponseTargetMinutes != null && s.FirstResponseMetAt == null && s.FirstResponseDueAt != null);

    /// <summary>Created in a period (D27): every ticket raised in it counts, a ticket merged away later included (it was raised).</summary>
    public static Expression<Func<Ticket, bool>> CreatedBetween(DateTimeOffset from, DateTimeOffset to) =>
        t => t.CreatedAt >= from && t.CreatedAt < to;

    /// <summary>Duplicates merged away: reported as their own volume, never as current backlog or independent work (D27). The complement of <see cref="Backlog"/>'s merge rule.</summary>
    public static readonly Expression<Func<Ticket, bool>> MergedAway = t => t.MergedIntoTicketId != null;

    /// <summary>An agent reply whose email did not go out: a confirmed failure, or (uncertain) a send whose outcome is unknown.</summary>
    public static Expression<Func<Ticket, bool>> SendProblem(TicketingDbContext db, bool uncertain) =>
        t => db.Messages.Any(m => m.TicketId == t.Id && (uncertain ? m.DeliveryState == DeliveryState.Uncertain : m.DeliveryState == DeliveryState.Failed || m.DeliveryState == DeliveryState.Bounced));

    /// <summary>A deadline within <see cref="DueSoonWindow"/> that has not passed.</summary>
    public static Expression<Func<Ticket, bool>> SlaDueSoon(TicketingDbContext db, DateTimeOffset now)
    {
        var soon = now + DueSoonWindow;
        return t => db.TicketSlas.Any(s => s.TicketId == t.Id && s.NextDueAt != null && s.NextDueAt > now && s.NextDueAt <= soon);
    }
}
