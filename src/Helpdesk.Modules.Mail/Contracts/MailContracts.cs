using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Modules.Mail.Contracts;

public static class MailProviderKinds
{
    public const string Graph = "graph";
    public const string Fake = "fake";
}

/// <summary>Outbound delivery states a ticket message can report (set through <see cref="IDeliveryStateSink"/>).</summary>
public static class MailDeliveryStates
{
    public const string Pending = "Pending";
    public const string Sent = "Sent";
    public const string Failed = "Failed";
}

public sealed record MailParticipant(string Address, string? Name);

// ---- Called by Ticketing (Ticketing depends on Mail.Contracts, never the other way round) -------------------------------------

/// <param name="Name">Display name of the mailbox (the From name of replies).</param>
public sealed record ReplyMailbox(Guid MailboxId, string Address, string Name, bool IsDefault = false);

public interface IMailboxDirectory
{
    /// <summary>
    /// The mailbox a reply for this department leaves from: the ticket's own mailbox when it has one (null when that mailbox is not active: no fallback to
    /// another identity), otherwise the department's default active mailbox. Null means the reply is recorded but not sent. Never crosses departments.
    /// </summary>
    Task<ReplyMailbox?> FindReplyMailboxAsync(Guid departmentId, Guid? ticketMailboxId, CancellationToken cancellationToken = default);

    /// <summary>The department's active mailboxes (the choices when a ticket is created by hand). Never another department's.</summary>
    Task<IReadOnlyList<ReplyMailbox>> ListActiveAsync(Guid departmentId, CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<Guid, ReplyMailbox>> GetManyAsync(Guid departmentId, IReadOnlyCollection<Guid> mailboxIds, CancellationToken cancellationToken = default);
}

/// <summary>Operational summary of one mailbox. <c>Status</c>: Healthy, Attention, Failed, NotConfigured, Disabled; <c>Reasons</c> are stable codes.</summary>
public sealed record MailboxHealthInfo(
    Guid MailboxId, string Address, string DisplayName, string Status, IReadOnlyList<string> Reasons, int OutboundFailed, int OutboundUncertain, DateTimeOffset? LastSendAt);

/// <summary>
/// Mailbox diagnostics of ONE department. The CALLER must already have checked the department mailbox-management permission: this is configuration and
/// operational data, not ticket content, and ticket access alone must never reach it.
/// </summary>
public interface IMailboxHealthReader
{
    Task<IReadOnlyList<MailboxHealthInfo>> ListAsync(Guid departmentId, CancellationToken cancellationToken = default);
}

public sealed record OutboundReplyRequest(
    Guid DepartmentId,
    Guid MailboxId,
    Guid TicketId,
    Guid TicketMessageId,
    string TicketReference,
    string Subject,
    MailParticipant To,
    IReadOnlyList<MailParticipant> Cc,
    string BodyText,
    Guid? ActorUserId,
    string? SignatureHtml = null);

/// <summary>
/// The automatic acknowledgement of a new ticket: from the ticket's mailbox, no agent signature, no ticket message. The subject is used as given
/// (single line); the body is plain text.
/// </summary>
public sealed record OutboundAutoReplyRequest(Guid DepartmentId, Guid MailboxId, Guid TicketId, string TicketReference, MailParticipant To, string Subject, string BodyText);

/// <summary>The one allow-list HTML sanitiser of the application's email model (also used for agent signatures).</summary>
public interface IEmailHtmlSanitiser
{
    string Sanitise(string html);
}

public interface IMailOutbox
{
    /// <summary>
    /// Records an outbound message (state Queued) on the CALLER's connection and open transaction, so the ticket message and the
    /// outbox row commit or roll back together (same technique as the audit writer). Throws when the caller has no transaction.
    /// Call <see cref="NotifyQueuedAsync"/> after the commit.
    /// </summary>
    Task<Guid> QueueReplyAsync(OutboundReplyRequest request, DbContext caller, CancellationToken cancellationToken = default);

    /// <summary>Queues an automatic acknowledgement on the caller's open transaction (so it commits with the ticket it acknowledges). Same mailbox rule as replies.</summary>
    Task<Guid> QueueAutoReplyAsync(OutboundAutoReplyRequest request, DbContext caller, CancellationToken cancellationToken = default);

    /// <summary>Asks the scheduler to send soon (a periodic dispatcher also picks queued rows up, so a lost nudge is harmless).</summary>
    Task NotifyQueuedAsync(Guid outboundId, CancellationToken cancellationToken = default);

    /// <summary>Puts a Failed outbound message for this ticket message back in the queue; returns its id, or null when there is nothing to retry.</summary>
    Task<Guid?> RetryAsync(Guid departmentId, Guid ticketMessageId, DbContext caller, CancellationToken cancellationToken = default);
}

// ---- Called by Mail, implemented by Ticketing ---------------------------------------------------------------------------------

public sealed record InboundTicketRequest(
    Guid DepartmentId,
    Guid MailboxId,
    string SourceRef,
    MailParticipant From,
    IReadOnlyList<MailParticipant> Cc,
    string Subject,
    string BodyText,
    string? BodyHtmlSanitised,
    DateTimeOffset ReceivedAt,
    string? SystemNote);

public sealed record InboundMessageRequest(
    Guid DepartmentId,
    Guid TicketId,
    string SourceRef,
    MailParticipant From,
    IReadOnlyList<MailParticipant> Cc,
    string BodyText,
    string? BodyHtmlSanitised,
    DateTimeOffset ReceivedAt);

/// <param name="Created">True when this call created the ticket (false on an idempotent repeat).</param>
/// <param name="Reopened">True when the ticket returned to Open because of this message (D-15).</param>
public sealed record InboundTicketResult(Guid TicketId, string Reference, Guid MessageId, bool Created, bool Reopened);

public sealed record TicketLookup(Guid TicketId, string Reference, string Status);

/// <summary>
/// Mail's door into Ticketing. Every call carries the department of the RECEIVING mailbox, never one derived from message content,
/// and is idempotent on <c>SourceRef</c> (the mail message id), so a retry after a crash never duplicates a ticket or message.
/// </summary>
public interface IInboundTicketSink
{
    Task<InboundTicketResult> CreateTicketAsync(InboundTicketRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Null when the message must not join that ticket, and the caller then falls back to a new ticket: the ticket does not exist in that department, or the sender is
    /// neither its requester nor an active participant. Correlation evidence (headers, token, conversation id) only says which ticket a message is about; it never lets a
    /// stranger join it.
    /// </summary>
    Task<InboundTicketResult?> AddMessageAsync(InboundMessageRequest request, CancellationToken cancellationToken = default);

    Task<TicketLookup?> FindByReferenceAsync(Guid departmentId, string reference, CancellationToken cancellationToken = default);

    Task<TicketLookup?> FindByIdAsync(Guid departmentId, Guid ticketId, CancellationToken cancellationToken = default);

    /// <summary>Tells realtime clients that something about the ticket changed outside its own transaction (e.g. attachments were stored). <c>kind</c>: <c>attachment</c>.</summary>
    Task NotifyTicketChangedAsync(Guid departmentId, Guid ticketId, string kind, CancellationToken cancellationToken = default);
}

public interface IDeliveryStateSink
{
    /// <param name="state">One of <see cref="MailDeliveryStates"/>.</param>
    /// <param name="failureCode">Short machine code when failed (never provider text or message content).</param>
    /// <param name="sequence">
    /// Monotonic order of the outbound message's state changes. The sink applies a report only when it is higher than the highest it has accepted for that message;
    /// an equal or lower value is ignored (a repeat or a stale report), so every caller gets the same ordering guarantee.
    /// </param>
    Task SetAsync(Guid departmentId, Guid ticketMessageId, string state, string? failureCode, long sequence, CancellationToken cancellationToken = default);
}

// ---- Background execution seam (Hangfire lives in the Host; Mail only knows these) -------------------------------------------

public interface IMailJobScheduler
{
    void EnqueueSync(Guid mailboxId);

    void EnqueueSend(Guid outboundId);
}

/// <summary>The work units a scheduler runs. All are idempotent and safe to retry.</summary>
public interface IMailJobRunner
{
    /// <summary>Reads changes of one mailbox through delta and processes new messages (loops while notifications arrived meanwhile).</summary>
    Task RunSyncAsync(Guid mailboxId, CancellationToken cancellationToken = default);

    /// <summary>Periodic reconciliation: syncs every active mailbox (covers missed webhooks and expired subscriptions).</summary>
    Task RunSyncAllAsync(CancellationToken cancellationToken = default);

    Task RunSendAsync(Guid outboundId, CancellationToken cancellationToken = default);

    /// <summary>Sends every queued message that is due and re-queues sends stuck in progress.</summary>
    Task RunDispatchAsync(CancellationToken cancellationToken = default);

    /// <summary>Creates or renews Graph subscriptions that are missing or close to expiry.</summary>
    Task RunSubscriptionMaintenanceAsync(CancellationToken cancellationToken = default);
}
