using System.Globalization;
using Helpdesk.Modules.Organisation.Contracts;
using Helpdesk.Modules.Ticketing.Domain;
using Helpdesk.Modules.Ticketing.Persistence;
using Helpdesk.SharedKernel.Authorization;
using Helpdesk.SharedKernel.Errors;
using Helpdesk.SharedKernel.Paging;
using Helpdesk.SharedKernel.Security;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Modules.Ticketing.Services;

/// <summary>
/// Queue lists and counts. Queues are filtered views over <c>tickets</c>: the department scope (global query filter) is applied by
/// the database query itself, so no per-ticket permission checks happen and filters can only narrow the caller's own scope.
/// </summary>
internal sealed class TicketQueryService(
    TicketingDbContext db,
    IDepartmentScope scope,
    IOrganisationDirectory organisation,
    TicketProjection projection,
    ICurrentUser current,
    IAuthorizer authorizer,
    SavedViewService views)
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 100;

    private enum Sort
    {
        UpdatedDesc,
        UpdatedAsc,
        CreatedDesc,
        CreatedAsc,
        PriorityDesc,
    }

    public async Task<PageDto<TicketListItemDto>> ListAsync(TicketListQuery query, CancellationToken ct)
    {
        if (query.View is { } viewId)
        {
            // A saved view only supplies the filter; scope, validation and pagination are exactly those of the normal list.
            var f = await views.FilterOfAsync(viewId, ct);
            query = query with
            {
                Queue = f.Queue, DepartmentId = f.DepartmentId, Status = f.Status, Priority = f.Priority, TeamId = f.TeamId,
                AssigneeUserId = f.AssigneeUserId, Unassigned = f.Unassigned, Q = f.Q, Sort = f.Sort,
            };
        }

        var errors = new Dictionary<string, string[]>();
        var queue = (query.Queue ?? "all").Trim().ToLowerInvariant();
        if (queue is not ("my" or "team" or "unassigned" or "all"))
        {
            errors["queue"] = ["queue must be one of my, team, unassigned, all."];
        }

        var statuses = new List<TicketStatus>();
        foreach (var raw in query.Status ?? [])
        {
            if (TicketValidation.ParseEnum<TicketStatus>(errors, "status", raw) is { } status)
            {
                statuses.Add(status);
            }
        }

        var priorities = new List<TicketPriority>();
        foreach (var raw in query.Priority ?? [])
        {
            if (TicketValidation.ParseEnum<TicketPriority>(errors, "priority", raw) is { } priority)
            {
                priorities.Add(priority);
            }
        }

        var search = query.Q?.Trim();
        if (query.Q is not null && (search!.Length is < 2 or > 100 || TicketValidation.HasNul(search)))
        {
            errors["q"] = ["q must be between 2 and 100 characters."];
        }

        var sort = ParseSort(query.Sort, errors);
        var take = query.Limit ?? DefaultLimit;
        if (take is < 1 or > MaxLimit)
        {
            errors["limit"] = [$"limit must be between 1 and {MaxLimit}."];
        }

        TicketValidation.ThrowIfAny(errors);

        await scope.EnsureLoadedAsync(ct);
        IQueryable<Ticket> tickets = db.Tickets.AsNoTracking();

        if (query.DepartmentId is { } departmentId)
        {
            tickets = tickets.Where(t => t.DepartmentId == departmentId);
        }

        switch (queue)
        {
            case "my":
                var me = current.UserId;
                tickets = tickets.Where(t => t.AssigneeUserId == me);
                break;
            case "team":
                var teamIds = (await organisation.GetTeamIdsOfUserAsync(current.UserId, scope.DepartmentIds, ct)).ToArray();
                tickets = tickets.Where(t => t.TeamId != null && teamIds.Contains(t.TeamId.Value));
                break;
            case "unassigned":
                tickets = tickets.Where(t => t.AssigneeUserId == null);
                break;
        }

        var statusFilter = statuses.ToArray();
        tickets = statusFilter.Length == 0
            ? tickets.Where(t => t.Status != TicketStatus.Closed)
            : tickets.Where(t => statusFilter.Contains(t.Status));

        if (priorities.Count > 0)
        {
            var priorityFilter = priorities.ToArray();
            tickets = tickets.Where(t => priorityFilter.Contains(t.Priority));
        }

        if (query.TeamId is { } filterTeam)
        {
            tickets = tickets.Where(t => t.TeamId == filterTeam);
        }

        if (query.AssigneeUserId is { } filterAssignee)
        {
            tickets = tickets.Where(t => t.AssigneeUserId == filterAssignee);
        }

        if (query.Unassigned == true)
        {
            tickets = tickets.Where(t => t.AssigneeUserId == null);
        }

        // Search (trigram-indexed ILIKE): reference, subject, requester name/email, message text. Internal notes are searched only in
        // departments where the caller may read notes, so a match can never reveal a note's existence or content.
        string? like = null;
        Guid[] notesDepartments = [];
        if (search is not null)
        {
            var escaped = TicketValidation.EscapeLike(search);
            var prefix = escaped + "%";
            like = "%" + escaped + "%";
            var pattern = like;
            var notes = notesDepartments = [.. await authorizer.AccessibleDepartmentsAsync(PermissionCodes.TicketsNotesRead, ct)];
            tickets = tickets.Where(t =>
                EF.Functions.ILike(t.Reference, prefix, "\\")
                || EF.Functions.ILike(t.Subject, pattern, "\\")
                || EF.Functions.ILike(t.Requester.DisplayName, pattern, "\\")
                || EF.Functions.ILike(t.Requester.Email, pattern, "\\")
                || db.Messages.Any(m => m.TicketId == t.Id && EF.Functions.ILike(m.Body, pattern, "\\") && (m.Kind == MessageKind.Public || notes.Contains(m.DepartmentId))));
        }

        var ordered = ApplyCursorAndOrder(tickets, sort, query.Cursor);
        var rows = await ordered
            .Take(take + 1)
            .Select(t => new TicketRow(
                t.Id, t.Reference, t.DepartmentId, t.Subject, t.Status, t.Priority, t.RequesterId, t.Requester.DisplayName, t.Requester.Email,
                t.TeamId, t.AssigneeUserId, t.CreatedAt, t.UpdatedAt))
            .ToListAsync(ct);

        var hasMore = rows.Count > take;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        var matches = search is null ? null : await MatchesAsync(rows, search, like!, notesDepartments, ct);
        var items = await projection.ListItemsAsync(rows, ct, matches);
        return new PageDto<TicketListItemDto>(items, hasMore ? EncodeCursor(sort, rows[^1]) : null);
    }

    /// <summary>Why each row matched, with a plain-text snippet for message hits. Snippets come only from visible messages and are never HTML.</summary>
    private async Task<IReadOnlyDictionary<Guid, MatchDto>> MatchesAsync(IReadOnlyList<TicketRow> rows, string search, string like, Guid[] notesDepartments, CancellationToken ct)
    {
        var matches = new Dictionary<Guid, MatchDto>();
        var needMessages = new List<Guid>();
        foreach (var r in rows)
        {
            if (r.Reference.StartsWith(search, StringComparison.OrdinalIgnoreCase))
            {
                matches[r.Id] = new MatchDto("reference", null);
            }
            else if (r.Subject.Contains(search, StringComparison.OrdinalIgnoreCase))
            {
                matches[r.Id] = new MatchDto("subject", null);
            }
            else if (r.RequesterName.Contains(search, StringComparison.OrdinalIgnoreCase) || r.RequesterEmail.Contains(search, StringComparison.OrdinalIgnoreCase))
            {
                matches[r.Id] = new MatchDto("requester", null);
            }
            else
            {
                needMessages.Add(r.Id);
            }
        }

        if (needMessages.Count > 0)
        {
            var ids = needMessages.ToArray();
            var hits = await db.Messages.AsNoTracking()
                .Where(m => ids.Contains(m.TicketId) && EF.Functions.ILike(m.Body, like, "\\") && (m.Kind == MessageKind.Public || notesDepartments.Contains(m.DepartmentId)))
                .OrderByDescending(m => m.CreatedAt).Take(ids.Length * 3)
                .Select(m => new { m.TicketId, m.Body, m.Kind })
                .ToListAsync(ct);
            foreach (var hit in hits)
            {
                if (!matches.ContainsKey(hit.TicketId))
                {
                    matches[hit.TicketId] = new MatchDto(hit.Kind == MessageKind.Internal ? "note" : "message", SnippetOf(hit.Body, search));
                }
            }
        }

        return matches;
    }

    internal static string SnippetOf(string body, string search)
    {
        var at = Math.Max(0, body.IndexOf(search, StringComparison.OrdinalIgnoreCase));
        var start = Math.Max(0, at - 50);
        var length = Math.Min(body.Length - start, 150);
        var text = string.Join(' ', body.Substring(start, length).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return (start > 0 ? "…" : "") + text + (start + length < body.Length ? "…" : "");
    }

    public async Task<QueueCountsDto> QueueCountsAsync(CancellationToken ct)
    {
        await scope.EnsureLoadedAsync(ct);
        var me = current.UserId;
        var teamIds = (await organisation.GetTeamIdsOfUserAsync(me, scope.DepartmentIds, ct)).ToArray();
        var counts = await db.Tickets.AsNoTracking()
            .Where(t => t.Status == TicketStatus.Open)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                All = g.Count(),
                My = g.Count(t => t.AssigneeUserId == me),
                Unassigned = g.Count(t => t.AssigneeUserId == null),
                Team = g.Count(t => t.TeamId != null && teamIds.Contains(t.TeamId.Value)),
            })
            .SingleOrDefaultAsync(ct);
        return counts is null ? new QueueCountsDto(0, 0, 0, 0) : new QueueCountsDto(counts.My, counts.Team, counts.Unassigned, counts.All);
    }

    // ----- sorting and keyset pagination -----

    private static Sort ParseSort(string? raw, Dictionary<string, string[]> errors)
    {
        switch (raw?.Trim().ToLowerInvariant())
        {
            case null or "" or "updated_desc":
                return Sort.UpdatedDesc;
            case "updated_asc":
                return Sort.UpdatedAsc;
            case "created_desc":
                return Sort.CreatedDesc;
            case "created_asc":
                return Sort.CreatedAsc;
            case "priority_desc":
                return Sort.PriorityDesc;
            default:
                errors["sort"] = ["sort must be one of updated_desc, updated_asc, created_desc, created_asc, priority_desc."];
                return Sort.UpdatedDesc;
        }
    }

    private static string Code(Sort sort) => sort switch
    {
        Sort.UpdatedDesc => "ud",
        Sort.UpdatedAsc => "ua",
        Sort.CreatedDesc => "cd",
        Sort.CreatedAsc => "ca",
        _ => "pd",
    };

    /// <summary>Cursor = sort code | key values | id. Ties on the sort key are broken by id, so pages never skip or repeat rows.</summary>
    private static string EncodeCursor(Sort sort, TicketRow last)
    {
        var parts = sort switch
        {
            Sort.CreatedDesc or Sort.CreatedAsc => $"{last.CreatedAt.UtcTicks}|{last.Id}",
            Sort.PriorityDesc => $"{(short)last.Priority}|{last.UpdatedAt.UtcTicks}|{last.Id}",
            _ => $"{last.UpdatedAt.UtcTicks}|{last.Id}",
        };
        return Cursor.Encode(string.Create(CultureInfo.InvariantCulture, $"{Code(sort)}|{parts}"));
    }

    private static IQueryable<Ticket> ApplyCursorAndOrder(IQueryable<Ticket> tickets, Sort sort, string? cursor)
    {
        string[] parts = [];
        if (cursor is not null)
        {
            if (!Cursor.TryDecode(cursor, out var payload))
            {
                throw new RequestValidationException("cursor", "Invalid cursor.");
            }

            parts = payload.Split('|');
            if (parts.Length == 0 || parts[0] != Code(sort))
            {
                throw new RequestValidationException("cursor", "Invalid cursor.");
            }
        }

        switch (sort)
        {
            case Sort.UpdatedDesc:
                if (cursor is not null)
                {
                    var (at, id) = TimeAndId(parts, 1);
                    tickets = tickets.Where(t => t.UpdatedAt < at || (t.UpdatedAt == at && t.Id.CompareTo(id) < 0));
                }

                return tickets.OrderByDescending(t => t.UpdatedAt).ThenByDescending(t => t.Id);
            case Sort.UpdatedAsc:
                if (cursor is not null)
                {
                    var (at, id) = TimeAndId(parts, 1);
                    tickets = tickets.Where(t => t.UpdatedAt > at || (t.UpdatedAt == at && t.Id.CompareTo(id) > 0));
                }

                return tickets.OrderBy(t => t.UpdatedAt).ThenBy(t => t.Id);
            case Sort.CreatedDesc:
                if (cursor is not null)
                {
                    var (at, id) = TimeAndId(parts, 1);
                    tickets = tickets.Where(t => t.CreatedAt < at || (t.CreatedAt == at && t.Id.CompareTo(id) < 0));
                }

                return tickets.OrderByDescending(t => t.CreatedAt).ThenByDescending(t => t.Id);
            case Sort.CreatedAsc:
                if (cursor is not null)
                {
                    var (at, id) = TimeAndId(parts, 1);
                    tickets = tickets.Where(t => t.CreatedAt > at || (t.CreatedAt == at && t.Id.CompareTo(id) > 0));
                }

                return tickets.OrderBy(t => t.CreatedAt).ThenBy(t => t.Id);
            default:
                if (cursor is not null)
                {
                    if (parts.Length != 4 || !short.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var rank)
                        || !Enum.IsDefined((TicketPriority)rank))
                    {
                        throw new RequestValidationException("cursor", "Invalid cursor.");
                    }

                    var priority = (TicketPriority)rank;
                    var (at, id) = TimeAndId(parts, 2);
                    tickets = tickets.Where(t => t.Priority < priority
                        || (t.Priority == priority && (t.UpdatedAt < at || (t.UpdatedAt == at && t.Id.CompareTo(id) < 0))));
                }

                return tickets.OrderByDescending(t => t.Priority).ThenByDescending(t => t.UpdatedAt).ThenByDescending(t => t.Id);
        }
    }

    private static (DateTimeOffset At, Guid Id) TimeAndId(string[] parts, int start)
    {
        if (parts.Length != start + 2
            || !long.TryParse(parts[start], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
            || ticks < 0 || ticks > DateTime.MaxValue.Ticks
            || !Guid.TryParse(parts[start + 1], out var id))
        {
            throw new RequestValidationException("cursor", "Invalid cursor.");
        }

        return (new DateTimeOffset(ticks, TimeSpan.Zero), id);
    }
}

/// <summary>Requester type-ahead for the create-ticket form (one department).</summary>
internal sealed class RequesterService(TicketingDbContext db, IDepartmentScope scope)
{
    public const int MaxResults = 10;

    public async Task<IReadOnlyList<RequesterLookupDto>> LookupAsync(Guid departmentId, string? q, CancellationToken ct)
    {
        var term = q?.Trim();
        if (term is null || term.Length is < 2 or > 100 || TicketValidation.HasNul(term))
        {
            throw new RequestValidationException("q", "q must be between 2 and 100 characters.");
        }

        await scope.EnsureLoadedAsync(ct);
        var escaped = TicketValidation.EscapeLike(term);
        var prefix = escaped + "%";
        var contains = "%" + escaped + "%";
        return await db.Requesters.AsNoTracking()
            .Where(r => r.DepartmentId == departmentId
                && (EF.Functions.ILike(r.EmailNormalised, prefix, "\\") || EF.Functions.ILike(r.DisplayName, contains, "\\")))
            .OrderBy(r => r.DisplayName).ThenBy(r => r.Id)
            .Take(MaxResults)
            .Select(r => new RequesterLookupDto(r.Id, r.DisplayName, r.Email))
            .ToListAsync(ct);
    }
}
