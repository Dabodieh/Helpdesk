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
public sealed record ReplyMailbox(Guid MailboxId, string Address, string Name);

public interface IMailboxDirectory
{
    /// <summary>
    /// The mailbox a reply for this department leaves from: the ticket's own mailbox when it is active, otherwise the department's
    /// default active mailbox. Null when the department has none (the reply is then recorded but not sent). Never crosses departments.
    /// </summary>
    Task<ReplyMailbox?> FindReplyMailboxAsync(Guid departmentId, Guid? ticketMailboxId, CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<Guid, ReplyMailbox>> GetManyAsync(Guid departmentId, IReadOnlyCollection<Guid> mailboxIds, CancellationToken cancellationToken = default);
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
    Guid? ActorUserId);

public interface IMailOutbox
{
    /// <summary>
    /// Records an outbound message (state Queued) on the CALLER's connection and open transaction, so the ticket message and the
    /// outbox row commit or roll back together (same technique as the audit writer). Throws when the caller has no transaction.
    /// Call <see cref="NotifyQueuedAsync"/> after the commit.
    /// </summary>
    Task<Guid> QueueReplyAsync(OutboundReplyRequest request, DbContext caller, CancellationToken cancellationToken = default);

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

    /// <summary>Null when the ticket does not exist in that department (the caller then falls back to a new ticket).</summary>
    Task<InboundTicketResult?> AddMessageAsync(InboundMessageRequest request, CancellationToken cancellationToken = default);

    Task<TicketLookup?> FindByReferenceAsync(Guid departmentId, string reference, CancellationToken cancellationToken = default);
}

public interface IDeliveryStateSink
{
    /// <param name="state">One of <see cref="MailDeliveryStates"/>.</param>
    /// <param name="failureCode">Short machine code when failed (never provider text or message content).</param>
    Task SetAsync(Guid departmentId, Guid ticketMessageId, string state, string? failureCode, CancellationToken cancellationToken = default);
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
