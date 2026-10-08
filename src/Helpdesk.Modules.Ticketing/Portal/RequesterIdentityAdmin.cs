using Helpdesk.Modules.Identity.Contracts;
using Helpdesk.Modules.Ticketing.Domain;
using Helpdesk.Modules.Ticketing.Persistence;
using Helpdesk.Modules.Ticketing.Services;
using Helpdesk.SharedKernel.Errors;
using Helpdesk.SharedKernel.Database;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Modules.Ticketing.Portal;

internal sealed record AccountRefDto(Guid UserId, string DisplayName, string? Email);

/// <summary>An unlinked requester that has tickets. <see cref="NeedsAttention"/> is set when an active account with the same address exists (a coincidence to look at, never proof).</summary>
internal sealed record IdentityCaseDto(
    Guid RequesterId, string DisplayName, string Email, int TicketCount, int OpenCount, DateTimeOffset FirstSeenAt, DateTimeOffset? LastActivityAt,
    AccountRefDto? MatchingAccount, bool NeedsAttention);

internal sealed record IdentityTicketDto(string Reference, string Subject, string Status, DateTimeOffset UpdatedAt);

/// <summary><see cref="Status"/> is <c>Unlinked</c> or <c>Linked</c>; <see cref="LinkedAccount"/> names the owner only (never identifiers of the external identity).</summary>
internal sealed record IdentityCaseDetailDto(IdentityCaseDto Case, string Status, AccountRefDto? LinkedAccount, IReadOnlyList<IdentityTicketDto> Tickets);

internal sealed record LinkRequesterRequest(Guid? UserId);

internal sealed record LinkResultDto(Guid RequesterId, string Status, AccountRefDto Account, bool AlreadyLinked);

/// <summary>
/// Administrative resolution of requester ownership inside ONE department (permission department.members.manage, enforced by the endpoint filter).
/// Every read goes through the department-scope filter, so a requester or ticket of a department the caller has no membership in does not exist here
/// (platform administrators included). The only thing that creates ownership is an explicit administrator decision; an address is never proof.
/// Linking makes the account the stable owner of the requester's portal-visible history. It grants no department access and no form audience.
/// </summary>
internal sealed class RequesterIdentityAdminService(TicketingDbContext db, IDepartmentScope scope, IUserDirectory users, TicketingAudit audit, HubAccess hub, TimeProvider clock)
{
    public const int MaxCases = 100;

    /// <summary>One page of unlinked requesters (most recently active first). <paramref name="query"/> finds a name or address; <paramref name="skip"/> pages on (BE-012).</summary>
    public async Task<IReadOnlyList<IdentityCaseDto>> ListAsync(Guid departmentId, string? query, int skip, CancellationToken ct)
    {
        await scope.EnsureLoadedAsync(ct);
        if (skip is < 0 or > 100_000)
        {
            throw new RequestValidationException("skip", "skip is not valid.");
        }

        var tickets = db.Tickets.AsNoTracking().Where(t => t.DepartmentId == departmentId && !db.RequesterLinks.Any(l => l.RequesterId == t.RequesterId));
        if (query?.Trim() is { Length: > 0 } text)
        {
            if (text.Length > 200)
            {
                throw new RequestValidationException("q", "q is at most 200 characters.");
            }

            var pattern = "%" + TicketValidation.EscapeLike(text) + "%";
            tickets = tickets.Where(t => EF.Functions.ILike(t.Requester.DisplayName, pattern, "\\") || EF.Functions.ILike(t.Requester.Email, pattern, "\\"));
        }

        var stats = await Stats(tickets).OrderByDescending(s => s.Last).ThenBy(s => s.Id).Skip(skip).Take(MaxCases).ToListAsync(ct);
        return await ToDtosAsync(departmentId, stats, ct);
    }

    public async Task<IdentityCaseDetailDto> GetAsync(Guid departmentId, Guid requesterId, CancellationToken ct)
    {
        await scope.EnsureLoadedAsync(ct);
        var requester = await db.Requesters.AsNoTracking().SingleOrDefaultAsync(r => r.Id == requesterId && r.DepartmentId == departmentId, ct) ?? throw new NotFoundException();
        var stats = await Stats(db.Tickets.AsNoTracking().Where(t => t.RequesterId == requester.Id)).ToListAsync(ct);
        var dto = (await ToDtosAsync(departmentId, stats.Count > 0 ? stats : [new TicketStats { Id = requester.Id }], ct))[0];
        var link = await db.RequesterLinks.AsNoTracking().SingleOrDefaultAsync(l => l.RequesterId == requester.Id, ct);
        var owner = link is null ? null : await users.FindAsync(link.UserId, ct);
        var tickets = await db.Tickets.AsNoTracking().Where(t => t.RequesterId == requester.Id).OrderByDescending(t => t.UpdatedAt).ThenByDescending(t => t.Id).Take(10)
            .Select(t => new IdentityTicketDto(t.Reference, t.Subject, t.Status.ToString(), t.UpdatedAt)).ToListAsync(ct);
        return new IdentityCaseDetailDto(dto, link is null ? "Unlinked" : "Linked", owner is null ? null : new AccountRefDto(owner.Id, owner.DisplayName, owner.Email), tickets);
    }

    public async Task<LinkResultDto> LinkAsync(Guid departmentId, Guid requesterId, LinkRequesterRequest request, CancellationToken ct)
    {
        var userId = request.UserId ?? throw new RequestValidationException("userId", "Choose the account to link.");
        await scope.EnsureLoadedAsync(ct);
        var requester = await db.Requesters.AsNoTracking().SingleOrDefaultAsync(r => r.Id == requesterId && r.DepartmentId == departmentId, ct) ?? throw new NotFoundException();
        var account = await users.FindAsync(userId, ct);
        if (account is not { IsActive: true })
        {
            throw new RequestValidationException("userId", "Choose an active account.");
        }

        var accountRef = new AccountRefDto(account.Id, account.DisplayName, account.Email);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var existing = await db.RequesterLinks.AsNoTracking().SingleOrDefaultAsync(l => l.RequesterId == requester.Id, ct);
        if (existing is not null)
        {
            await tx.RollbackAsync(ct);
            return existing.UserId == userId
                ? new LinkResultDto(requester.Id, "Linked", accountRef, AlreadyLinked: true)
                : throw new ConflictException("This requester is already linked to a different account. Changing an existing link is not available here.", ProblemCodes.Conflict);
        }

        var now = ClockExtensions.GetUtcNowMicroseconds(clock);
        var inserted = await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO ticketing.requester_links (id, requester_id, department_id, user_id, linked_email_normalised, created_at)
            VALUES (gen_random_uuid(), {requester.Id}, {departmentId}, {userId}, {requester.EmailNormalised}, {now})
            ON CONFLICT DO NOTHING
            """,
            ct);
        if (inserted == 0)
        {
            // Lost a race, or the account already owns another requester of this department (one per account and department): never a silent second link.
            var winner = await db.RequesterLinks.AsNoTracking().SingleOrDefaultAsync(l => l.RequesterId == requester.Id, ct);
            await tx.RollbackAsync(ct);
            return winner?.UserId == userId
                ? new LinkResultDto(requester.Id, "Linked", accountRef, AlreadyLinked: true)
                : throw new ConflictException(
                    winner is null
                        ? "This account already has a requester in this department. Merging requesters is not available."
                        : "This requester was just linked to a different account.",
                    ProblemCodes.Conflict);
        }

        // Identifiers and change type only: no address, name or ticket content.
        await audit.WriteAsync(TicketAuditActions.RequesterIdentityLinkedByAdmin, "requester", requester.Id, departmentId, null, new { userId, change = "linked" }, ct);
        await tx.CommitAsync(ct);
        await hub.AccessChangedAsync(userId, ct);
        return new LinkResultDto(requester.Id, "Linked", accountRef, AlreadyLinked: false);
    }

    // ----- helpers -----

    private sealed class TicketStats
    {
        public Guid Id { get; init; }

        public int Total { get; init; }

        public int Open { get; init; }

        public DateTimeOffset? Last { get; init; }
    }

    private static IQueryable<TicketStats> Stats(IQueryable<Ticket> tickets) =>
        tickets.GroupBy(t => t.RequesterId).Select(g => new TicketStats
        {
            Id = g.Key,
            Total = g.Count(),
            Open = g.Count(t => t.MergedIntoTicketId == null && (t.Status == TicketStatus.Open || t.Status == TicketStatus.Pending)),
            Last = g.Max(t => (DateTimeOffset?)t.UpdatedAt),
        });

    private async Task<IReadOnlyList<IdentityCaseDto>> ToDtosAsync(Guid departmentId, IReadOnlyList<TicketStats> stats, CancellationToken ct)
    {
        var ids = stats.Select(s => s.Id).ToArray();
        var requesters = await db.Requesters.AsNoTracking().Where(r => r.DepartmentId == departmentId && ids.Contains(r.Id)).ToDictionaryAsync(r => r.Id, ct);
        var matches = await users.FindActiveByEmailsAsync([.. requesters.Values.Select(r => r.EmailNormalised).Distinct()], ct);
        return [.. stats.Where(s => requesters.ContainsKey(s.Id)).Select(s =>
        {
            var r = requesters[s.Id];
            var match = matches.TryGetValue(r.EmailNormalised, out var u) ? new AccountRefDto(u.Id, u.DisplayName, u.Email) : null;
            return new IdentityCaseDto(r.Id, r.DisplayName, r.Email, s.Total, s.Open, r.CreatedAt, s.Last, match, match is not null);
        })];
    }
}
