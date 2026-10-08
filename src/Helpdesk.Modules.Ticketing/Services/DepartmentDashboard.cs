using Helpdesk.Modules.Identity.Contracts;
using Helpdesk.Modules.Mail.Contracts;
using Helpdesk.Modules.Organisation.Contracts;
using Helpdesk.Modules.Ticketing.Domain;
using Helpdesk.Modules.Ticketing.Persistence;
using Helpdesk.SharedKernel.Authorization;
using Helpdesk.SharedKernel.Errors;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Modules.Ticketing.Services;

internal sealed record DashboardBacklogDto(int Open, int Pending, int Unassigned, int AwaitingFirstResponse);

internal sealed record DashboardSlaDto(int Overdue, int DueSoon, int DueSoonWindowMinutes);

internal sealed record DashboardAgeBucketDto(string Key, string Label, int Count);

internal sealed record DashboardOldestDto(string Reference, string Subject, string Status, string Priority, DateTimeOffset CreatedAt, string? Assignee, bool SlaOverdue);

/// <summary>Current open backlog of one assignee (a person, or <c>UserId == null</c> for unassigned). A workload snapshot, not a performance measure.</summary>
internal sealed record DashboardAgentDto(Guid? UserId, string Name, int Total, int Overdue);

internal sealed record DashboardSendsDto(int FailedTickets, int UncertainTickets);

/// <summary>
/// <see cref="Mailboxes"/> is null unless the caller holds the department mailbox-management permission (diagnostics are configuration, not ticket content).
/// </summary>
internal sealed record DepartmentDashboardDto(
    Guid DepartmentId, string DepartmentName, DateTimeOffset GeneratedAt, DashboardBacklogDto Backlog, DashboardSlaDto Sla,
    IReadOnlyList<DashboardAgeBucketDto> Ageing, IReadOnlyList<DashboardOldestDto> Oldest, IReadOnlyList<DashboardAgentDto> Agents, DashboardSendsDto Sends,
    IReadOnlyList<MailboxHealthInfo>? Mailboxes);

/// <summary>
/// "What needs attention right now" for ONE explicitly named department (tickets.read on that department, enforced by the endpoint filter; every query also
/// runs through the department-scope filter, so a platform administrator without a membership sees nothing). Current state only: nothing here is a history, a
/// snapshot or a per-person figure. It builds on <see cref="QueuePredicates"/>, the same definitions as the queue filters, so each figure equals the queue it
/// links to. A later historical reporting feature should reuse these predicates rather than redefine them.
/// </summary>
internal sealed class DepartmentDashboardService(
    TicketingDbContext db,
    IDepartmentScope scope,
    IOrganisationDirectory organisation,
    IUserDirectory users,
    IAuthorizer authorizer,
    IMailboxHealthReader mailHealth,
    TimeProvider clock)
{
    private const int OldestCount = 10;

    public async Task<DepartmentDashboardDto> GetAsync(Guid departmentId, CancellationToken ct)
    {
        await scope.EnsureLoadedAsync(ct);
        var department = await organisation.FindDepartmentAsync(departmentId, ct) ?? throw new NotFoundException();
        var now = clock.GetUtcNow();
        var backlog = db.Tickets.AsNoTracking().Where(t => t.DepartmentId == departmentId).Where(QueuePredicates.Backlog);
        var breached = QueuePredicates.SlaBreached(db, now);

        var open = await backlog.CountAsync(t => t.Status == TicketStatus.Open, ct);
        var pending = await backlog.CountAsync(t => t.Status == TicketStatus.Pending, ct);
        var unassigned = await backlog.CountAsync(t => t.AssigneeUserId == null, ct);
        // Only the human response counts: an acknowledgement is not a ticket message, so it can never satisfy first response.
        var awaiting = await backlog.Where(QueuePredicates.AwaitingFirstResponse(db)).CountAsync(ct);
        var overdue = await backlog.Where(breached).CountAsync(ct);
        var dueSoon = await backlog.Where(QueuePredicates.SlaDueSoon(db, now)).CountAsync(ct);

        var ageing = new List<DashboardAgeBucketDto>();
        (string Key, string Label, TimeSpan? From, TimeSpan? To)[] buckets =
        [
            ("lt1d", "Under 1 day", null, TimeSpan.FromDays(1)),
            ("1to3d", "1 to 3 days", TimeSpan.FromDays(1), TimeSpan.FromDays(3)),
            ("3to7d", "3 to 7 days", TimeSpan.FromDays(3), TimeSpan.FromDays(7)),
            ("7to14d", "1 to 2 weeks", TimeSpan.FromDays(7), TimeSpan.FromDays(14)),
            ("gt14d", "Over 2 weeks", TimeSpan.FromDays(14), null),
        ];
        foreach (var (key, label, from, to) in buckets)
        {
            var older = from is null ? now : now - from.Value; // created at or before `older` ...
            var younger = to is null ? (DateTimeOffset?)null : now - to.Value; // ... and after `younger`
            var count = await backlog.CountAsync(t => (from == null || t.CreatedAt <= older) && (younger == null || t.CreatedAt > younger), ct);
            ageing.Add(new DashboardAgeBucketDto(key, label, count));
        }

        var oldestRows = await backlog.OrderBy(t => t.CreatedAt).ThenBy(t => t.Id).Take(OldestCount)
            .Select(t => new { t.Id, t.Reference, t.Subject, t.Status, t.Priority, t.CreatedAt, t.AssigneeUserId }).ToListAsync(ct);
        var oldestIds = oldestRows.Select(r => r.Id).ToArray();
        var overdueIds = (await backlog.Where(breached).Where(t => oldestIds.Contains(t.Id)).Select(t => t.Id).ToListAsync(ct)).ToHashSet();

        var load = await backlog.GroupBy(t => t.AssigneeUserId).Select(g => new { UserId = g.Key, Total = g.Count() }).ToListAsync(ct);
        var loadOverdue = (await backlog.Where(breached).GroupBy(t => t.AssigneeUserId).Select(g => new { UserId = g.Key, Count = g.Count() }).ToListAsync(ct)).ToDictionary(x => x.UserId ?? Guid.Empty, x => x.Count);

        var people = await users.GetManyAsync([.. oldestRows.Select(r => r.AssigneeUserId).Concat(load.Select(l => l.UserId)).OfType<Guid>().Distinct()], ct);

        var failedTickets = await backlog.Where(QueuePredicates.SendProblem(db, uncertain: false)).CountAsync(ct);
        var uncertainTickets = await backlog.Where(QueuePredicates.SendProblem(db, uncertain: true)).CountAsync(ct);

        IReadOnlyList<MailboxHealthInfo>? mailboxes = null;
        if (await authorizer.CheckAsync(PermissionCodes.DepartmentMailboxesManage, departmentId, ct) == AccessOutcome.Allowed)
        {
            mailboxes = await mailHealth.ListAsync(departmentId, ct);
        }

        return new DepartmentDashboardDto(
            department.Id, department.Name, now,
            new DashboardBacklogDto(open, pending, unassigned, awaiting),
            new DashboardSlaDto(overdue, dueSoon, (int)QueuePredicates.DueSoonWindow.TotalMinutes),
            ageing,
            [.. oldestRows.Select(r => new DashboardOldestDto(
                r.Reference, r.Subject, r.Status.ToString(), r.Priority.ToString(), r.CreatedAt,
                r.AssigneeUserId is { } a && people.TryGetValue(a, out var p) ? p.DisplayName : null, overdueIds.Contains(r.Id)))],
            // Alphabetical (unassigned last), deliberately not ordered by volume: the list shows who holds the backlog, it does not rank people.
            [.. load.Select(l => new DashboardAgentDto(l.UserId, l.UserId is { } u ? people.TryGetValue(u, out var person) ? person.DisplayName : "Former member" : "Unassigned", l.Total, loadOverdue.GetValueOrDefault(l.UserId ?? Guid.Empty)))
                .OrderBy(a => a.UserId is null).ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)],
            new DashboardSendsDto(failedTickets, uncertainTickets),
            mailboxes);
    }
}
