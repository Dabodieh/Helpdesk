using Helpdesk.Modules.Mail.Contracts;
using Helpdesk.Modules.Ticketing.Domain;
using Helpdesk.Modules.Ticketing.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Modules.Ticketing.Services;

/// <summary>
/// Who is a party to a ticket: the one rule behind inbound mail correlation, participant addition, merge and outbound Cc (decisions D8 and D9,
/// <c>docs/plans/POST-REFINEMENT-10-DECISIONS.md</c>). A party is the primary requester or an ACTIVE participant.
/// <list type="bullet">
/// <item>Participants are people copied on the conversation. They are ordinary (counted against <see cref="OrdinaryParticipantCap"/>, removable by an agent who can reply)
/// or protected (the requester of a ticket merged into this one: never removable, never counted, so a merge cannot orphan them).</item>
/// <item>A removal is final for the ticket: the row stays (inactive, with who and when), so a later Cc of the same address does not bring it back.</item>
/// <item>Raw SQL with an explicit department and ticket on every statement: the callers include system work that has no signed-in scope.</item>
/// </list>
/// </summary>
internal sealed class TicketParties(TicketingDbContext db)
{
    public const int OrdinaryParticipantCap = 20;

    public enum RemoveOutcome
    {
        Removed,
        NotFound,
        Requester,
        Protected,
    }

    /// <summary>True when the address is the ticket's primary requester or an active participant (protected ones included).</summary>
    public async Task<bool> IsPartyAsync(Guid departmentId, Guid ticketId, string address, CancellationToken ct)
    {
        var normalised = Requester.Normalise(address.Trim());
        return await db.Database.SqlQuery<bool>(
            $"""
            SELECT (
                EXISTS (SELECT 1 FROM ticketing.tickets t JOIN ticketing.requesters r ON r.id = t.requester_id AND r.department_id = t.department_id
                        WHERE t.id = {ticketId} AND t.department_id = {departmentId} AND r.email_normalised = {normalised})
                OR EXISTS (SELECT 1 FROM ticketing.ticket_participants p
                           WHERE p.ticket_id = {ticketId} AND p.department_id = {departmentId} AND p.email_normalised = {normalised} AND p.removed_at IS NULL)
            ) AS "Value"
            """).SingleAsync(ct);
    }

    /// <summary>
    /// Adds people copied on a message sent by a party as ordinary participants, up to the cap. Never adds the primary requester, never re-adds a removed person, never exceeds
    /// <see cref="OrdinaryParticipantCap"/>. Returns the addresses that were NOT added because the cap was reached (callers tell the agents). The caller holds the ticket lock.
    /// </summary>
    public async Task<IReadOnlyList<string>> AddCopiedAsync(
        Guid departmentId, Guid ticketId, string requesterEmailNormalised, IEnumerable<MailParticipant> people, DateTimeOffset now, CancellationToken ct)
    {
        var active = await db.Database.SqlQuery<int>(
            $"""
            SELECT count(*)::int AS "Value" FROM ticketing.ticket_participants
            WHERE ticket_id = {ticketId} AND department_id = {departmentId} AND removed_at IS NULL AND NOT is_protected AND email_normalised <> {requesterEmailNormalised}
            """).SingleAsync(ct);
        var notAdded = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal) { requesterEmailNormalised };
        foreach (var person in people)
        {
            var email = person.Address.Trim();
            if (email.Length is 0 or > TicketValidation.MaxEmail)
            {
                continue;
            }

            var normalised = Requester.Normalise(email);
            if (!seen.Add(normalised))
            {
                continue;
            }

            var known = await db.Database.SqlQuery<bool>(
                $"SELECT EXISTS (SELECT 1 FROM ticketing.ticket_participants WHERE ticket_id = {ticketId} AND department_id = {departmentId} AND email_normalised = {normalised}) AS \"Value\"")
                .SingleAsync(ct);
            if (known)
            {
                continue; // already a participant, or removed: a removal is final
            }

            if (active >= OrdinaryParticipantCap)
            {
                notAdded.Add(email);
                continue;
            }

            var name = person.Name is { Length: > 0 } n ? (n.Length > TicketValidation.MaxDisplayName ? n[..TicketValidation.MaxDisplayName] : n) : null;
            var inserted = await db.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO ticketing.ticket_participants (ticket_id, department_id, email_normalised, email, display_name, created_at)
                VALUES ({ticketId}, {departmentId}, {normalised}, {email}, {name}, {now})
                ON CONFLICT (ticket_id, email_normalised) DO NOTHING
                """,
                ct);
            active += inserted;
        }

        return notAdded;
    }

    /// <summary>
    /// A merge keeps the source ticket's requester in the conversation: they become a protected participant of the canonical ticket (an existing or removed row is upgraded and
    /// reactivated). Nothing happens when the source has the same requester as the canonical ticket.
    /// </summary>
    public async Task ProtectRequesterAsync(Guid departmentId, Guid canonicalTicketId, Guid canonicalRequesterId, Requester sourceRequester, DateTimeOffset now, CancellationToken ct)
    {
        if (sourceRequester.Id == canonicalRequesterId)
        {
            return;
        }

        await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO ticketing.ticket_participants (ticket_id, department_id, email_normalised, email, display_name, created_at, is_protected, requester_id)
            VALUES ({canonicalTicketId}, {departmentId}, {sourceRequester.EmailNormalised}, {sourceRequester.Email}, {sourceRequester.DisplayName}, {now}, true, {sourceRequester.Id})
            ON CONFLICT (ticket_id, email_normalised) DO UPDATE
                SET is_protected = true, requester_id = EXCLUDED.requester_id, removed_at = NULL, removed_by_user_id = NULL
            """,
            ct);
    }

    /// <summary>
    /// A ticket that is itself merged into another keeps its protected requesters: they follow it to the final canonical ticket (merging flattens), so merging a ticket that
    /// already held merged requesters orphans none of them.
    /// </summary>
    public async Task CarryProtectedAsync(Guid departmentId, Guid fromTicketId, Guid toTicketId, Guid toRequesterId, DateTimeOffset now, CancellationToken ct) =>
        await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO ticketing.ticket_participants (ticket_id, department_id, email_normalised, email, display_name, created_at, is_protected, requester_id)
            SELECT {toTicketId}, department_id, email_normalised, email, display_name, {now}, true, requester_id
            FROM ticketing.ticket_participants
            WHERE ticket_id = {fromTicketId} AND department_id = {departmentId} AND is_protected AND removed_at IS NULL AND requester_id <> {toRequesterId}
            ON CONFLICT (ticket_id, email_normalised) DO UPDATE
                SET is_protected = true, requester_id = EXCLUDED.requester_id, removed_at = NULL, removed_by_user_id = NULL
            """,
            ct);

    /// <summary>Removes an ordinary participant from future Cc (history and audit are untouched). The primary requester and protected requesters cannot be removed.</summary>
    public async Task<RemoveOutcome> RemoveAsync(Guid departmentId, Guid ticketId, string requesterEmailNormalised, string address, Guid? userId, DateTimeOffset now, CancellationToken ct)
    {
        var normalised = Requester.Normalise(address.Trim());
        if (normalised == requesterEmailNormalised)
        {
            return RemoveOutcome.Requester;
        }

        var protectedFlags = await db.Database.SqlQuery<bool>(
            $"SELECT is_protected AS \"Value\" FROM ticketing.ticket_participants WHERE ticket_id = {ticketId} AND department_id = {departmentId} AND email_normalised = {normalised} AND removed_at IS NULL")
            .ToListAsync(ct);
        if (protectedFlags.Count == 0)
        {
            return RemoveOutcome.NotFound;
        }

        if (protectedFlags[0])
        {
            return RemoveOutcome.Protected;
        }

        var removed = await db.Database.ExecuteSqlAsync(
            $"""
            UPDATE ticketing.ticket_participants SET removed_at = {now}, removed_by_user_id = {userId}
            WHERE ticket_id = {ticketId} AND department_id = {departmentId} AND email_normalised = {normalised} AND removed_at IS NULL AND NOT is_protected
            """,
            ct);
        return removed == 1 ? RemoveOutcome.Removed : RemoveOutcome.NotFound;
    }
}
