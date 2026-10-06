using Helpdesk.Modules.Identity.Contracts;
using Helpdesk.Modules.Mail.Contracts;
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
internal sealed class TicketProjection(TicketingDbContext db, IOrganisationDirectory organisation, IUserDirectory users, IMailboxDirectory mailboxes, TimeProvider clock)
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
            .Where(t => t.Requester.EmailNormalised == requester.EmailNormalised)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Total = g.Count(),
                Open = g.Count(t => t.Status == TicketStatus.Open || t.Status == TicketStatus.Pending),
            })
            .SingleOrDefaultAsync(ct);

        MailboxRefDto? mailbox = null;
        if (ticket.MailboxId is { } mailboxId)
        {
            var found = await mailboxes.GetManyAsync(ticket.DepartmentId, [mailboxId], ct);
            mailbox = found.TryGetValue(mailboxId, out var m) ? new MailboxRefDto(m.MailboxId, m.Address, m.Name) : null;
        }

        var participants = await db.Participants.AsNoTracking().Where(p => p.TicketId == ticket.Id).OrderBy(p => p.CreatedAt).ThenBy(p => p.EmailNormalised)
            .Select(p => new ParticipantDto(p.Email, p.DisplayName)).ToListAsync(ct);

        var sla = await db.TicketSlas.AsNoTracking().SingleOrDefaultAsync(s => s.TicketId == ticket.Id, ct);
        var fields = await db.TicketFieldValues.AsNoTracking().Where(v => v.TicketId == ticket.Id && v.AgentVisible).OrderBy(v => v.SortOrder)
            .Select(v => new AgentFieldDto(v.Label, v.Display)).ToListAsync(ct);
        var formName = ticket.RequestFormId is { } formId ? await db.RequestForms.AsNoTracking().Where(f => f.Id == formId).Select(f => f.Name).SingleOrDefaultAsync(ct) : null;

        var type = ticket.TicketTypeId is { } typeId
            ? await db.TicketTypes.AsNoTracking().Where(t => t.Id == typeId && t.DepartmentId == ticket.DepartmentId).Select(t => new RefDto(t.Id, t.Name)).SingleOrDefaultAsync(ct)
            : null;

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
            ConcurrencyVersion.ToToken(ticket.Version),
            mailbox,
            participants,
            Helpdesk.Modules.Ticketing.Sla.SlaSummary.Of(sla, clock.GetUtcNow()),
            ticket.Source,
            formName,
            fields,
            type);
    }

    public async Task<IReadOnlyList<TicketListItemDto>> ListItemsAsync(IReadOnlyList<TicketRow> rows, CancellationToken ct, IReadOnlyDictionary<Guid, MatchDto>? matches = null, IReadOnlyDictionary<Guid, Helpdesk.Modules.Ticketing.Domain.TicketSla>? slas = null)
    {
        if (rows.Count == 0)
        {
            return [];
        }

        // One lookup per kind per page, never per ticket.
        var departments = await organisation.GetDepartmentsAsync(rows.Select(r => r.DepartmentId).Distinct().ToArray(), ct);
        var teams = await organisation.GetTeamsAsync(rows.Where(r => r.TeamId is not null).Select(r => r.TeamId!.Value).Distinct().ToArray(), ct);
        var people = await users.GetManyAsync(rows.Where(r => r.AssigneeUserId is not null).Select(r => r.AssigneeUserId!.Value).Distinct().ToArray(), ct);

        var now = clock.GetUtcNow();
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
            r.UpdatedAt,
            matches is not null && matches.TryGetValue(r.Id, out var match) ? match : null,
            slas is not null && slas.TryGetValue(r.Id, out var sla) ? Helpdesk.Modules.Ticketing.Sla.SlaSummary.Of(sla, now) : null)).ToList();
    }
}
