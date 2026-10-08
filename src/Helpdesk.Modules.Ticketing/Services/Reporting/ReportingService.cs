using Helpdesk.Modules.Identity.Contracts;
using Helpdesk.Modules.Ticketing.Domain;
using Helpdesk.Modules.Ticketing.Persistence;
using Helpdesk.SharedKernel.Authorization;
using Helpdesk.SharedKernel.Errors;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Modules.Ticketing.Services.Reporting;

/// <summary>
/// Historical reports of ONE explicitly named department (<c>tickets.read</c> on it, enforced by the endpoint filter; every query also names the department and runs
/// through the department-scope filter). The same permission and the same numbers lie behind a figure and the list of tickets it opens, so an aggregate never
/// discloses a ticket the caller could not list.
/// </summary>
internal sealed class ReportingService(TicketingDbContext db, IDepartmentScope scope, IUserDirectory users, TimeProvider clock)
{
    public static readonly string[] Kinds = ["support-overview", "ticket-volume", "sla", "response-times", "lifecycle", "backlog", "workload", "email"];

    private static readonly TimeSpan DelayedAfter = TimeSpan.FromMinutes(15);

    public async Task<ReportDto> RunAsync(Guid departmentId, string kind, ReportQuery query, CancellationToken ct)
    {
        await scope.EnsureLoadedAsync(ct);
        if (!Kinds.Contains(kind))
        {
            throw new NotFoundException();
        }

        var range = ReportRange.Parse(query.From, query.To, query.Group, clock);
        var data = await ReportData.LoadAsync(db, departmentId, query, ct);
        var builder = new ReportBuilder(data, range, clock.GetUtcNow());
        switch (kind)
        {
            case "support-overview":
                return builder.SupportOverview();
            case "ticket-volume":
                return builder.TicketVolume();
            case "sla":
                return builder.Sla();
            case "response-times":
                return builder.ResponseTimes();
            case "lifecycle":
                return builder.Lifecycle();
            case "backlog":
                return builder.Backlog();
            case "workload":
            {
                var ids = data.Tickets.Select(t => t.Assignee).OfType<Guid>().Distinct().ToArray();
                var people = await users.GetManyAsync(ids, ct);
                return builder.Workload(people.ToDictionary(p => p.Key, p => p.Value.DisplayName));
            }

            default:
                return await EmailAsync(departmentId, data, range, ct);
        }
    }

    private async Task<ReportDto> EmailAsync(Guid departmentId, ReportData data, ReportRange range, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var ids = data.Tickets.Select(t => t.Id).ToArray();
        var messages = ids.Length == 0
            ? []
            : await db.Messages.AsNoTracking()
                .Where(m => m.DepartmentId == departmentId && m.Kind == MessageKind.Public && m.AuthorType == AuthorType.Agent && m.CreatedAt >= range.StartUtc && m.CreatedAt < range.EndUtc && ids.Contains(m.TicketId))
                .Select(m => new { m.TicketId, m.CreatedAt, m.DeliveryState, m.DeliveryError }).ToListAsync(ct);

        double?[] PerBucket(Func<DeliveryState, DateTimeOffset, bool> pick)
        {
            var arr = new double?[range.Buckets.Count];
            Array.Fill(arr, 0d);
            foreach (var m in messages.Where(m => pick(m.DeliveryState, m.CreatedAt)))
            {
                var i = range.IndexOf(m.CreatedAt);
                if (i >= 0)
                {
                    arr[i] = arr[i]! + 1;
                }
            }

            return arr;
        }

        bool Delayed(DeliveryState s, DateTimeOffset at) => s == DeliveryState.Pending && now - at > DelayedAfter;
        bool Waiting(DeliveryState s, DateTimeOffset at) => s == DeliveryState.Pending && now - at <= DelayedAfter;
        var sent = messages.Count(m => m.DeliveryState == DeliveryState.Sent);
        var failed = messages.Count(m => m.DeliveryState is DeliveryState.Failed or DeliveryState.NotDelivered);
        var uncertain = messages.Count(m => m.DeliveryState == DeliveryState.Uncertain);
        var bounced = messages.Count(m => m.DeliveryState == DeliveryState.Bounced);
        var delayed = messages.Count(m => Delayed(m.DeliveryState, m.CreatedAt));
        var emailed = messages.Count(m => m.DeliveryState != DeliveryState.None);
        var problems = failed + uncertain + bounced + delayed;
        var reasons = messages.Where(m => m.DeliveryState is DeliveryState.Failed or DeliveryState.Uncertain or DeliveryState.Bounced or DeliveryState.NotDelivered)
            .GroupBy(m => (m.DeliveryState.ToString(), m.DeliveryError ?? "(no reason recorded)")).OrderByDescending(g => g.Count()).ThenBy(g => g.Key.Item2)
            .Take(15).Select(g => (IReadOnlyList<object?>)[g.Key.Item1, g.Key.Item2, g.Count()]).ToList();
        var rows = Enumerable.Range(0, range.Buckets.Count).Select(i => (IReadOnlyList<object?>)[range.Labels[i],
            (int)PerBucket((s, _) => s != DeliveryState.None)[i]!.Value, (int)PerBucket((s, _) => s == DeliveryState.Sent)[i]!.Value,
            (int)PerBucket((s, _) => s is DeliveryState.Failed or DeliveryState.NotDelivered)[i]!.Value, (int)PerBucket((s, _) => s == DeliveryState.Uncertain)[i]!.Value,
            (int)PerBucket((s, _) => s == DeliveryState.Bounced)[i]!.Value, (int)PerBucket(Delayed)[i]!.Value]).ToList();
        var report = new ReportDto("email", new ReportPeriodDto(range.From, range.To, range.Group, "Europe/London", now),
            [
                new("replies", "Agent replies in the period", messages.Count, "count", Note: $"{emailed} were sent by email; the rest needed no email."),
                new("sent", "Accepted for delivery", sent, "count", Tone: "good", Note: "The provider accepted the message. Acceptance is not proof of delivery."),
                new("failed", "Failed", failed, "count", Drill: "email-problem", Tone: failed > 0 ? "bad" : "good"),
                new("uncertain", "Outcome uncertain", uncertain, "count", Drill: "email-problem", Tone: uncertain > 0 ? "warn" : "good", Note: "Not retried automatically: the message may have gone out."),
                new("bounced", "Bounced", bounced, "count", Drill: "email-problem", Tone: bounced > 0 ? "bad" : "good"),
                new("delayed", "Delayed (still queued over 15 minutes)", delayed, "count", Drill: "email-problem", Tone: delayed > 0 ? "warn" : "good"),
                new("problemRate", "Problem rate", emailed == 0 ? null : Math.Round(100d * problems / emailed, 1), "percent", Tone: problems > 0 ? "warn" : "good"),
            ],
            [
                new("delivery", "Outgoing replies by delivery state", "stacked", "count", "date", range.Labels,
                    [new("sent", "Accepted", PerBucket((s, _) => s == DeliveryState.Sent), null, "good"), new("waiting", "Queued", PerBucket(Waiting), null, "muted"), new("delayed", "Delayed", PerBucket(Delayed), "email-problem", "warn"),
                     new("uncertain", "Uncertain", PerBucket((s, _) => s == DeliveryState.Uncertain), "email-problem", "info"), new("failed", "Failed", PerBucket((s, _) => s is DeliveryState.Failed or DeliveryState.NotDelivered), "email-problem", "bad"),
                     new("bounced", "Bounced", PerBucket((s, _) => s == DeliveryState.Bounced), "email-problem", "bad")]),
            ],
            [
                new("buckets", "Delivery by " + range.Group, [new("period", "Period", "date"), new("emailed", "Emailed", "number"), new("sent", "Accepted", "number"), new("failed", "Failed", "number"), new("uncertain", "Uncertain", "number"), new("bounced", "Bounced", "number"), new("delayed", "Delayed", "number")], rows),
                new("reasons", "Recorded reasons for problems", [new("state", "State", "text"), new("reason", "Reason", "text"), new("count", "Count", "number")], reasons),
            ],
            ["Only replies written by a human agent are counted; acknowledgements and system messages are not. The reasons are the safe codes recorded with each problem, never message text or addresses."],
            ["Mailbox outage history is not recorded (only the latest sync state is kept), so mailbox availability over time cannot be reported."]);
        return report;
    }

    /// <summary>The tickets behind a number, capped, for the caller's department only.</summary>
    public async Task<ReportTicketsDto> TicketsAsync(Guid departmentId, string metric, ReportQuery query, string? bucket, CancellationToken ct)
    {
        await scope.EnsureLoadedAsync(ct);
        var range = ReportRange.Parse(query.From, query.To, query.Group, clock);
        var now = clock.GetUtcNow();
        var start = range.StartUtc;
        var end = range.EndUtc;
        var from = range.From;
        var to = range.To;
        if (ReportRange.ParseDay("bucket", bucket) is { } day)
        {
            var b = range.BucketStartingOn(day) ?? throw new RequestValidationException("bucket", "bucket is not a bucket of this period.");
            (start, end, from, to) = (b.StartUtc, b.EndUtc, b.Start, b.End);
        }

        var data = await ReportData.LoadAsync(db, departmentId, query, ct);
        IEnumerable<TicketFact> set;
        switch (metric)
        {
            case "received":
                set = data.Tickets.Where(t => t.CreatedAt >= start && t.CreatedAt < end);
                break;
            case "resolved":
                set = data.Tickets.Where(t => !t.Merged && t.FirstResolutionIn(start, end) is not null);
                break;
            case "reopened":
                set = data.Tickets.Where(t => t.Reopens.Any(r => r >= start && r < end));
                break;
            case "backlog":
                set = data.Tickets.Where(t => t.IsBacklog);
                break;
            case "unassigned":
                set = data.Tickets.Where(t => t.IsBacklog && t.Assignee is null);
                break;
            case "awaiting-response":
                set = data.Tickets.Where(t => t.IsBacklog && t.FirstHumanReplyAt is null);
                break;
            case "sla-fr-breached":
                set = data.Tickets.Where(t => t.CreatedAt >= start && t.CreatedAt < end && data.FirstResponseOutcome(t.Id) == "breached");
                break;
            case "sla-res-breached":
                set = data.Tickets.Where(t => t.CreatedAt >= start && t.CreatedAt < end && data.ResolutionOutcome(t.Id) == "breached");
                break;
            case "email-problem":
            {
                var ids = data.Tickets.Select(t => t.Id).ToArray();
                var bad = ids.Length == 0
                    ? []
                    : await db.Messages.AsNoTracking()
                        .Where(m => m.DepartmentId == departmentId && m.Kind == MessageKind.Public && m.AuthorType == AuthorType.Agent && m.CreatedAt >= start && m.CreatedAt < end && ids.Contains(m.TicketId)
                            && (m.DeliveryState == DeliveryState.Failed || m.DeliveryState == DeliveryState.NotDelivered || m.DeliveryState == DeliveryState.Uncertain || m.DeliveryState == DeliveryState.Bounced
                                || (m.DeliveryState == DeliveryState.Pending && m.CreatedAt < now - DelayedAfter)))
                        .Select(m => m.TicketId).Distinct().ToListAsync(ct);
                var badSet = bad.ToHashSet();
                set = data.Tickets.Where(t => badSet.Contains(t.Id));
                break;
            }

            default:
                throw new RequestValidationException("metric", "Unknown metric.");
        }

        var all = set.OrderByDescending(t => t.CreatedAt).ToList();
        var page = all.Take(200).ToList();
        var people = await users.GetManyAsync([.. page.Select(t => t.Assignee).OfType<Guid>().Distinct()], ct);
        var rows = page.Select(t => new ReportTicketRow(t.Id, t.Reference, t.Subject, t.Status.ToString(), t.Priority.ToString(), t.Assignee is { } a && people.TryGetValue(a, out var p) ? p.DisplayName : null,
            t.CreatedAt, t.FirstResolutionEver, (int)(now - t.CreatedAt).TotalDays)).ToList();
        return new ReportTicketsDto(metric, from, to, all.Count, all.Count > page.Count, rows);
    }
}
