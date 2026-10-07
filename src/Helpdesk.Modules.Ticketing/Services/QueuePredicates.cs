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

    /// <summary>A deadline within <see cref="DueSoonWindow"/> that has not passed.</summary>
    public static Expression<Func<Ticket, bool>> SlaDueSoon(TicketingDbContext db, DateTimeOffset now)
    {
        var soon = now + DueSoonWindow;
        return t => db.TicketSlas.Any(s => s.TicketId == t.Id && s.NextDueAt != null && s.NextDueAt > now && s.NextDueAt <= soon);
    }
}
