namespace Helpdesk.Modules.Ticketing.Contracts;

/// <summary>Message sources accepted by <see cref="ITicketIngress"/> (email arrives with the Phase 3 mail module).</summary>
public static class TicketMessageSources
{
    public const string Web = "web";
    public const string Manual = "manual";
}

/// <param name="MessageId">The stored message.</param>
/// <param name="Reopened">True when the ticket was Closed, Resolved or Pending and is now Open because of this message (D-15).</param>
public sealed record RequesterMessageResult(Guid MessageId, bool Reopened);

/// <summary>
/// Non-HTTP entry point for adding a requester's public message to an existing ticket (used by Phase 3 mail ingestion; never
/// exposed over HTTP). The department comes from the receiving mailbox, never from message content: the ticket must belong to
/// <c>departmentId</c>, otherwise it is not found. Runs as the system principal (audit actor null, source <c>system</c>).
/// </summary>
public interface ITicketIngress
{
    /// <summary>
    /// Appends a Public message authored by the requester. Closed, Resolved or Pending tickets return to Open (same ticket,
    /// reference, team and assignee), audited as <c>ticket.status_changed</c> with reason <c>requester_reply</c>.
    /// </summary>
    Task<RequesterMessageResult> AddRequesterMessageAsync(
        Guid departmentId, Guid ticketId, Guid requesterId, string body, string source, CancellationToken cancellationToken = default);
}
