using Helpdesk.Modules.Ticketing.Domain;
using Helpdesk.Modules.Ticketing.Persistence;
using Helpdesk.Modules.Ticketing.Services;
using Helpdesk.SharedKernel.Errors;
using Helpdesk.SharedKernel.Security;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Helpdesk.Modules.Ticketing.Portal;

/// <summary>
/// Maps the signed-in application user to Department-local Requesters. The authenticated user (the internal user bound to the Entra subject at sign-in)
/// is the STABLE owner: a <see cref="RequesterLink"/> says "this requester row is this user", is used by id afterwards, and survives any change of address or
/// display name. The verified address (<see cref="ICurrentUser.Email"/>, from the sign-in token, never from the request) is contact data: it is never enough
/// to inherit existing history. A link is created only by the user's own portal submission, and only for a requester that is new or has no tickets; an
/// address that already carries unlinked history (email-created tickets, or a previous holder of the address) is left unlinked and reported for
/// administrator resolution. Nobody can claim a requester that already belongs to another user.
/// </summary>
internal sealed class RequesterIdentityService(TicketingDbContext db, ICurrentUser current, HubAccess hub, TicketingAudit audit, TimeProvider clock)
{
    private IReadOnlyList<Guid>? _requesterIds;

    /// <summary>The requesters this user owns (all departments), by explicit link only.</summary>
    public async Task<IReadOnlyList<Guid>> RequesterIdsAsync(CancellationToken ct)
    {
        _requesterIds ??= await db.RequesterLinks.AsNoTracking().Where(l => l.UserId == current.UserId).Select(l => l.RequesterId).ToListAsync(ct);
        return _requesterIds;
    }

    /// <summary>The caller's requester in a department, created and linked on first use. Existing contact data follows the account (address, name).</summary>
    public async Task<Guid> EnsureRequesterAsync(Guid departmentId, CancellationToken ct)
    {
        var userId = current.UserId;
        var existing = await db.RequesterLinks.AsNoTracking().Where(l => l.UserId == userId && l.DepartmentId == departmentId).Select(l => (Guid?)l.RequesterId).SingleOrDefaultAsync(ct);
        if (existing is { } id)
        {
            await RefreshContactAsync(id, departmentId, ct);
            return id;
        }

        var email = NormalisedVerifiedEmail() ?? throw new ConflictException("Your account has no verified email address, so a request cannot be created.", ProblemCodes.InvalidState);
        var now = clock.GetUtcNowMicroseconds();
        var requester = await db.Requesters.AsNoTracking().IgnoreDepartmentScope().SingleOrDefaultAsync(r => r.DepartmentId == departmentId && r.EmailNormalised == email, ct);
        var created = requester is null;
        requester ??= (await PeopleUpsert.RequesterAsync(db, departmentId, email, current.DisplayName, now, ct)).Requester;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var owner = await db.RequesterLinks.AsNoTracking().SingleOrDefaultAsync(l => l.RequesterId == requester.Id, ct);
        if (owner is null && !created && await db.Tickets.AsNoTracking().IgnoreDepartmentScope().AnyAsync(t => t.RequesterId == requester.Id, ct))
        {
            // Unproven history under this address (email-created tickets, or the previous holder of the address): never inherited on the strength of the address alone.
            await audit.WriteAsync(TicketAuditActions.RequesterLinkAmbiguous, "requester", requester.Id, departmentId, null, new { userId }, ct);
            await tx.CommitAsync(ct);
            throw new ConflictException("Your address already has request history that is not linked to your account. Ask an administrator to resolve it.", ProblemCodes.Conflict);
        }

        var linked = owner is not null ? 0 : await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO ticketing.requester_links (id, requester_id, department_id, user_id, linked_email_normalised, created_at)
            SELECT gen_random_uuid(), {requester.Id}, {departmentId}, {userId}, {email}, {now}
            WHERE NOT EXISTS (SELECT 1 FROM ticketing.requester_links l WHERE l.requester_id = {requester.Id})
            ON CONFLICT DO NOTHING
            """,
            ct);
        var link = await db.RequesterLinks.AsNoTracking().SingleOrDefaultAsync(l => l.RequesterId == requester.Id, ct);
        if (link is null || link.UserId != userId)
        {
            await tx.RollbackAsync(ct);
            // The requester record for this address already belongs to another account: an administrator must resolve it.
            throw new ConflictException("This address is already linked to a different account.", ProblemCodes.Conflict);
        }

        if (linked > 0)
        {
            await audit.WriteAsync(TicketAuditActions.RequesterIdentityLinked, "requester", requester.Id, departmentId, null, new { userId, created }, ct);
        }

        await tx.CommitAsync(ct);
        _requesterIds = null;
        if (linked > 0)
        {
            await hub.AccessChangedAsync(userId, ct);
        }

        return link.RequesterId;
    }

    /// <summary>
    /// Keeps the linked requester's contact data current: the address follows the account unless another requester of the department already uses it
    /// (never two rows for one address); a real display name replaces the stored one. History, tickets and sent messages are not rewritten.
    /// </summary>
    private async Task RefreshContactAsync(Guid requesterId, Guid departmentId, CancellationToken ct)
    {
        var email = NormalisedVerifiedEmail();
        if (email is not null)
        {
            var raw = current.Email!.Trim();
            try
            {
                await db.Database.ExecuteSqlAsync(
                    $"""
                    UPDATE ticketing.requesters SET email = {raw}, email_normalised = {email}
                    WHERE id = {requesterId} AND department_id = {departmentId} AND email_normalised <> {email}
                      AND NOT EXISTS (SELECT 1 FROM ticketing.requesters o WHERE o.department_id = {departmentId} AND o.email_normalised = {email})
                    """,
                    ct);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                // Lost a race for the address: keep the previous contact address.
            }
        }

        var name = current.DisplayName?.Trim();
        if (!string.IsNullOrEmpty(name) && !name.Contains('@', StringComparison.Ordinal))
        {
            name = name.Length > TicketValidation.MaxDisplayName ? name[..TicketValidation.MaxDisplayName] : name;
            await db.Database.ExecuteSqlAsync($"UPDATE ticketing.requesters SET display_name = {name} WHERE id = {requesterId} AND department_id = {departmentId} AND display_name <> {name}", ct);
        }
    }

    private string? NormalisedVerifiedEmail()
    {
        var email = current.Email?.Trim();
        return string.IsNullOrEmpty(email) || email.Length > TicketValidation.MaxEmail || !email.Contains('@', StringComparison.Ordinal) ? null : Requester.Normalise(email);
    }
}
