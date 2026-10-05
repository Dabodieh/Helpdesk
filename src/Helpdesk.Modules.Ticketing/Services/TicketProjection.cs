using Helpdesk.Modules.Identity.Contracts;
using Helpdesk.Modules.Organisation.Contracts;
using Helpdesk.Modules.Ticketing.Domain;
using Helpdesk.Modules.Ticketing.Persistence;
using Helpdesk.SharedKernel.Database;
using Helpdesk.SharedKernel.Errors;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Modules.Ticketing.Services;

/// <summary>Row of the list query: the ticket columns plus its requester, fetched in one query.</summary>
internal sealed record TicketRow(
    Guid Id,
    string Reference,
    Guid DepartmentId,
    string Subject,
    TicketStatus Status,
    TicketPriority Priority,
    Guid RequesterId,
    string RequesterName,
    string RequesterEmail,
    Guid? TeamId,
    Guid? AssigneeUserId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>Builds API shapes from tickets: resolves team, assignee and department names through the module contracts, in batches.</summary>
internal sealed class TicketProjection(TicketingDbContext db, IOrganisationDirectory organisation, IUserDirectory users)
{
    public async Task<TicketDetailDto> DetailAsync(Ticket ticket, Requester requester, CancellationToken ct)
    {
        var department = await organisation.FindDepartmentAsync(ticket.DepartmentId, ct) ?? throw new NotFoundException();
        TeamRefDto? team = null;
        if (ticket.TeamId is { } teamId)
        {
            var info = await organisation.FindTeamAsync(ticket.DepartmentId, teamId, ct);
            team = info is null ? null : new TeamRefDto(info.Id, info.Name);
        }

        RefDto? assignee = null;
        if (ticket.AssigneeUserId is { } assigneeId)
        {
            var user = await users.FindAsync(assigneeId, ct);
            assignee = user is null ? null : new RefDto(user.Id, user.DisplayName);
        }

        var counts = await db.Tickets.AsNoTracking()
            .Where(t => t.RequesterId == requester.Id)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Total = g.Count(),
                Open = g.Count(t => t.Status == TicketStatus.Open || t.Status == TicketStatus.Pending),
            })
            .SingleOrDefaultAsync(ct);

        return new TicketDetailDto(
            ticket.Id,
            ticket.Reference,
            ticket.DepartmentId,
            department.Key,
            department.Name,
            ticket.Subject,
            ticket.Status.ToString(),
            ticket.Priority.ToString(),
            new RequesterDetailDto(requester.Id, requester.DisplayName, requester.Email, counts?.Open ?? 0, counts?.Total ?? 0),
            team,
            assignee,
            ticket.CreatedAt,
            ticket.UpdatedAt,
            ticket.ResolvedAt,
            ticket.ClosedAt,
            ConcurrencyVersion.ToToken(ticket.Version));
    }

    public async Task<IReadOnlyList<TicketListItemDto>> ListItemsAsync(IReadOnlyList<TicketRow> rows, CancellationToken ct)
    {
        if (rows.Count == 0)
        {
            return [];
        }

        // One lookup per kind per page, never per ticket.
        var departments = await organisation.GetDepartmentsAsync(rows.Select(r => r.DepartmentId).Distinct().ToArray(), ct);
        var teams = await organisation.GetTeamsAsync(rows.Where(r => r.TeamId is not null).Select(r => r.TeamId!.Value).Distinct().ToArray(), ct);
        var people = await users.GetManyAsync(rows.Where(r => r.AssigneeUserId is not null).Select(r => r.AssigneeUserId!.Value).Distinct().ToArray(), ct);

        return rows.Select(r => new TicketListItemDto(
            r.Id,
            r.Reference,
            r.DepartmentId,
            departments.TryGetValue(r.DepartmentId, out var department) ? department.Key : "",
            r.Subject,
            r.Status.ToString(),
            r.Priority.ToString(),
            new RequesterRefDto(r.RequesterId, r.RequesterName, r.RequesterEmail),
            r.TeamId is { } teamId && teams.TryGetValue(teamId, out var team) ? new TeamRefDto(team.Id, team.Name) : null,
            r.AssigneeUserId is { } userId && people.TryGetValue(userId, out var person) ? new RefDto(person.Id, person.DisplayName) : null,
            r.CreatedAt,
            r.UpdatedAt)).ToList();
    }
}
