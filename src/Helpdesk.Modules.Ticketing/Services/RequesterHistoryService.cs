using System.Globalization;
using Helpdesk.Modules.Ticketing.Domain;
using Helpdesk.Modules.Ticketing.Persistence;
using Helpdesk.SharedKernel.Errors;
using Helpdesk.SharedKernel.Paging;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Modules.Ticketing.Services;

internal sealed record RequesterProfileDto(
    Guid Id,
    string DisplayName,
    string Email,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset? LastActivityAt,
    int OpenCount,
    int ResolvedCount,
    int TotalCount,
    IReadOnlyList<TicketListItemDto> Open,
    IReadOnlyList<TicketListItemDto> Previous);

/// <summary>
/// Agent-facing view of one person's support history. A requester row exists per department, so a person is the set of requester rows
/// linked to the same authenticated account (unlinked rows group by address only among themselves, see <see cref="RequesterPeople"/>). Every query runs through the department-scope global filter, so tickets (and requester rows) in
/// departments the caller cannot read do not exist here: not in lists, counts, timestamps or paging. Dates are the visible
/// <c>UpdatedAt</c>, which internal notes never move.
/// </summary>
internal sealed class RequesterHistoryService(TicketingDbContext db, IDepartmentScope scope, TicketProjection projection)
{
    public const int DefaultLimit = 20;
    public const int MaxLimit = 50;
    private const int SummaryOpen = 5;
    private const int SummaryPrevious = 3;

    private static readonly TicketStatus[] ActiveStatuses = [TicketStatus.Open, TicketStatus.Pending];

    public async Task<RequesterProfileDto> GetAsync(Guid requesterId, CancellationToken ct)
    {
        await scope.EnsureLoadedAsync(ct);
        var requester = await db.Requesters.AsNoTracking().SingleOrDefaultAsync(r => r.Id == requesterId, ct) ?? throw new NotFoundException();
        var ids = await RequesterPeople.IdsAsync(db, requester, ct);
        var tickets = db.Tickets.AsNoTracking().Where(t => ids.Contains(t.RequesterId));

        var summary = await tickets.GroupBy(_ => 1)
            .Select(g => new
            {
                Total = g.Count(),
                Open = g.Count(t => t.MergedIntoTicketId == null && (t.Status == TicketStatus.Open || t.Status == TicketStatus.Pending)),
                Last = g.Max(t => t.UpdatedAt),
            })
            .SingleOrDefaultAsync(ct);
        var firstSeen = await db.Requesters.AsNoTracking().Where(r => ids.Contains(r.Id)).MinAsync(r => r.CreatedAt, ct);

        var open = await PageAsync(tickets.Where(t => t.MergedIntoTicketId == null && ActiveStatuses.Contains(t.Status)), null, SummaryOpen, ct);
        var previous = await PageAsync(tickets.Where(t => t.MergedIntoTicketId != null || !ActiveStatuses.Contains(t.Status)), null, SummaryPrevious, ct);
        var total = summary?.Total ?? 0;
        var openCount = summary?.Open ?? 0;
        return new RequesterProfileDto(requester.Id, requester.DisplayName, requester.Email, firstSeen, summary?.Last, openCount, total - openCount, total, open.Items, previous.Items);
    }

    /// <summary>Tickets of the person, newest visible activity first. <paramref name="state"/> is active, resolved or all.</summary>
    public async Task<PageDto<TicketListItemDto>> TicketsAsync(Guid requesterId, string? state, string? cursor, int? limit, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        var kind = (state ?? "all").Trim().ToLowerInvariant();
        if (kind is not ("all" or "active" or "resolved"))
        {
            errors["state"] = ["state must be one of all, active, resolved."];
        }

        var take = limit ?? DefaultLimit;
        if (take is < 1 or > MaxLimit)
        {
            errors["limit"] = [$"limit must be between 1 and {MaxLimit}."];
        }

        TicketValidation.ThrowIfAny(errors);
        await scope.EnsureLoadedAsync(ct);
        var requester = await db.Requesters.AsNoTracking().SingleOrDefaultAsync(r => r.Id == requesterId, ct) ?? throw new NotFoundException();
        var ids = await RequesterPeople.IdsAsync(db, requester, ct);
        IQueryable<Ticket> tickets = db.Tickets.AsNoTracking().Where(t => ids.Contains(t.RequesterId));
        tickets = kind switch
        {
            "active" => tickets.Where(t => t.MergedIntoTicketId == null && ActiveStatuses.Contains(t.Status)),
            "resolved" => tickets.Where(t => t.MergedIntoTicketId != null || !ActiveStatuses.Contains(t.Status)),
            _ => tickets,
        };
        return await PageAsync(tickets, cursor, take, ct);
    }

    private async Task<PageDto<TicketListItemDto>> PageAsync(IQueryable<Ticket> tickets, string? cursor, int take, CancellationToken ct)
    {
        if (cursor is not null)
        {
            var (at, id) = Decode(cursor);
            tickets = tickets.Where(t => t.UpdatedAt < at || (t.UpdatedAt == at && t.Id.CompareTo(id) < 0));
        }

        var rows = await tickets.OrderByDescending(t => t.UpdatedAt).ThenByDescending(t => t.Id).Take(take + 1)
            .Select(t => new TicketRow(
                t.Id, t.Reference, t.DepartmentId, t.Subject, t.Status, t.Priority, t.RequesterId, t.Requester.DisplayName, t.Requester.Email,
                t.TeamId, t.AssigneeUserId, t.CreatedAt, t.UpdatedAt, t.MergedIntoTicketId))
            .ToListAsync(ct);
        var hasMore = rows.Count > take;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        var items = await projection.ListItemsAsync(rows, ct);
        var next = hasMore ? Cursor.Encode(string.Create(CultureInfo.InvariantCulture, $"{rows[^1].UpdatedAt.UtcTicks}|{rows[^1].Id}")) : null;
        return new PageDto<TicketListItemDto>(items, next);
    }

    private static (DateTimeOffset At, Guid Id) Decode(string cursor)
    {
        var parts = Cursor.TryDecode(cursor, out var payload) ? payload.Split('|') : [];
        if (parts.Length != 2
            || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
            || ticks > DateTime.MaxValue.Ticks
            || !Guid.TryParse(parts[1], out var id))
        {
            throw new RequestValidationException("cursor", "Invalid cursor.");
        }

        return (new DateTimeOffset(ticks, TimeSpan.Zero), id);
    }
}

/// <summary>
/// Which requester rows are "the same person". Linked rows: all rows linked to the same authenticated user, whatever their address (a changed or
/// reused address never moves history). Unlinked rows (email senders, contractors, legacy): rows sharing the address that are themselves unlinked,
/// so someone who now holds a reused address never merges with an account's history. Callers read tickets through the department-scope filter, so
/// grouping never widens what an agent may see.
/// </summary>
internal static class RequesterPeople
{
    public static async Task<Guid[]> IdsAsync(TicketingDbContext db, Requester requester, CancellationToken ct)
    {
        var userId = await db.RequesterLinks.AsNoTracking().Where(l => l.RequesterId == requester.Id).Select(l => (Guid?)l.UserId).SingleOrDefaultAsync(ct);
        return userId is { } u
            ? await db.Requesters.AsNoTracking().Where(r => db.RequesterLinks.Any(l => l.RequesterId == r.Id && l.UserId == u)).Select(r => r.Id).ToArrayAsync(ct)
            : await db.Requesters.AsNoTracking().Where(r => r.EmailNormalised == requester.EmailNormalised && !db.RequesterLinks.Any(l => l.RequesterId == r.Id)).Select(r => r.Id).ToArrayAsync(ct);
    }
}
