using Helpdesk.Modules.Ticketing.Domain;
using Helpdesk.Modules.Ticketing.Persistence;
using Helpdesk.Modules.Ticketing.Services;
using Helpdesk.SharedKernel.Errors;
using Helpdesk.SharedKernel.Security;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Modules.Ticketing.Portal;

/// <summary>
/// Maps the signed-in application user to department Requesters. A Requester is claimed from the VERIFIED email of the authenticated principal
/// (<see cref="ICurrentUser.Email"/>, taken from the Entra token at sign-in, never from the request), and only while it is unclaimed. The claim is stored
/// as an explicit link and used by id afterwards: a later change of display name or address does not move a request to somebody else, and nobody
/// can claim a requester that already belongs to another user. Knowing or typing an address proves nothing.
/// </summary>
internal sealed class RequesterIdentityService(TicketingDbContext db, ICurrentUser current, HubAccess hub, TimeProvider clock)
{
    private IReadOnlyList<Guid>? _requesterIds;

    /// <summary>The requesters this user owns (all departments), after claiming unclaimed ones that match the verified address.</summary>
    public async Task<IReadOnlyList<Guid>> RequesterIdsAsync(CancellationToken ct)
    {
        if (_requesterIds is not null)
        {
            return _requesterIds;
        }

        var userId = current.UserId;
        var email = NormalisedVerifiedEmail();
        if (email is not null)
        {
            var now = clock.GetUtcNowMicroseconds();
            var claimed = await db.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO ticketing.requester_links (id, requester_id, department_id, user_id, linked_email_normalised, created_at)
                SELECT gen_random_uuid(), r.id, r.department_id, {userId}, r.email_normalised, {now}
                FROM ticketing.requesters r
                WHERE r.email_normalised = {email}
                  AND NOT EXISTS (SELECT 1 FROM ticketing.requester_links l WHERE l.requester_id = r.id)
                  AND NOT EXISTS (SELECT 1 FROM ticketing.requester_links m WHERE m.user_id = {userId} AND m.department_id = r.department_id)
                ON CONFLICT DO NOTHING
                """,
                ct);
            if (claimed > 0)
            {
                await hub.AccessChangedAsync(userId, ct);
            }
        }

        _requesterIds = await db.RequesterLinks.AsNoTracking().Where(l => l.UserId == userId).Select(l => l.RequesterId).ToListAsync(ct);
        return _requesterIds;
    }

    /// <summary>The caller's requester in a department, created and linked on first use. Fails when the address already belongs to another user.</summary>
    public async Task<Guid> EnsureRequesterAsync(Guid departmentId, CancellationToken ct)
    {
        var userId = current.UserId;
        await RequesterIdsAsync(ct);
        var existing = await db.RequesterLinks.AsNoTracking().Where(l => l.UserId == userId && l.DepartmentId == departmentId).Select(l => (Guid?)l.RequesterId).SingleOrDefaultAsync(ct);
        if (existing is { } id)
        {
            return id;
        }

        var email = NormalisedVerifiedEmail() ?? throw new ConflictException("Your account has no verified email address, so a request cannot be created.", ProblemCodes.InvalidState);
        var now = clock.GetUtcNowMicroseconds();
        var requester = await PeopleUpsert.RequesterAsync(db, departmentId, email, current.DisplayName, now, ct);
        var linked = await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO ticketing.requester_links (id, requester_id, department_id, user_id, linked_email_normalised, created_at)
            SELECT gen_random_uuid(), {requester.Requester.Id}, {departmentId}, {userId}, {email}, {now}
            WHERE NOT EXISTS (SELECT 1 FROM ticketing.requester_links l WHERE l.requester_id = {requester.Requester.Id})
            ON CONFLICT DO NOTHING
            """,
            ct);
        var link = await db.RequesterLinks.AsNoTracking().SingleOrDefaultAsync(l => l.RequesterId == requester.Requester.Id, ct);
        if (link is null || link.UserId != userId)
        {
            // The requester record for this address already belongs to another account: an administrator must resolve it.
            throw new ConflictException("This address is already linked to a different account.", ProblemCodes.Conflict);
        }

        _requesterIds = null;
        if (linked > 0)
        {
            await hub.AccessChangedAsync(userId, ct);
        }

        return link.RequesterId;
    }

    private string? NormalisedVerifiedEmail()
    {
        var email = current.Email?.Trim();
        return string.IsNullOrEmpty(email) || email.Length > TicketValidation.MaxEmail || !email.Contains('@', StringComparison.Ordinal) ? null : Requester.Normalise(email);
    }
}
