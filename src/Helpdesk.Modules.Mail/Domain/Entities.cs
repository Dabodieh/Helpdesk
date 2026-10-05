namespace Helpdesk.Modules.Mail.Domain;

internal static class MessageDirections
{
    public const string Inbound = "Inbound";
    public const string Outbound = "Outbound";
}

internal static class MessageStates
{
    // Inbound lifecycle: Received -> Ticketed -> Completed (or Ignored / Failed).
    public const string Received = "Received";
    public const string Ticketed = "Ticketed";
    public const string Completed = "Completed";
    public const string Ignored = "Ignored";

    // Outbound lifecycle: Queued -> Sending -> Sent (or Failed).
    public const string Queued = "Queued";
    public const string Sending = "Sending";
    public const string Sent = "Sent";

    public const string Failed = "Failed";

    public static readonly string[] All = [Received, Ticketed, Completed, Ignored, Queued, Sending, Sent, Failed];
}

internal static class SubscriptionStates
{
    public const string None = "None";
    public const string Active = "Active";
    public const string Failed = "Failed";
}

/// <summary>A department's mailbox (an Exchange Online shared mailbox reached through the application identity; no credentials stored).</summary>
internal sealed class Mailbox
{
    public Guid Id { get; set; }

    public Guid DepartmentId { get; set; }

    public string Address { get; set; } = "";

    /// <summary>Trimmed lower-case address; unique across the installation (one mailbox belongs to exactly one department).</summary>
    public string AddressNormalised { get; set; } = "";

    public string DisplayName { get; set; } = "";

    public string Provider { get; set; } = "";

    /// <summary>Provider's id for the mailbox (Graph: the user object id, preferred over the UPN in resource paths). Null = use the address.</summary>
    public string? ProviderRef { get; set; }

    /// <summary>Plain-text department signature appended to replies.</summary>
    public string? Signature { get; set; }

    public bool IsActive { get; set; } = true;

    /// <summary>The department's fallback sender for tickets that have no mailbox of their own.</summary>
    public bool IsDefault { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public uint Version { get; set; }

    public MailboxSync? Sync { get; set; }

    public static string Normalise(string address) => address.Trim().ToLowerInvariant();
}

/// <summary>Per-mailbox synchronisation state: webhook subscription, delta token, health.</summary>
internal sealed class MailboxSync
{
    public Guid MailboxId { get; set; }

    public string? SubscriptionId { get; set; }

    public DateTimeOffset? SubscriptionExpiresAt { get; set; }

    public string SubscriptionState { get; set; } = SubscriptionStates.None;

    /// <summary>SHA-256 of the per-subscription client state secret (the secret itself is never stored).</summary>
    public byte[]? ClientStateHash { get; set; }

    public string? DeltaToken { get; set; }

    public DateTimeOffset? LastSyncAt { get; set; }

    public string? LastSyncError { get; set; }

    public int ConsecutiveFailures { get; set; }

    /// <summary>Set by webhooks; a running sync that finds it set runs again so a notification mid-sync is never lost.</summary>
    public bool Dirty { get; set; }
}

/// <summary>An inbound or outbound email as Mail sees it. Provider specifics stop here; Ticketing only gets normalised data.</summary>
internal sealed class MailMessage
{
    public Guid Id { get; set; }

    public Guid MailboxId { get; set; }

    /// <summary>Denormalised from the mailbox (composite foreign key keeps them equal). The ONLY source of a message's department.</summary>
    public Guid DepartmentId { get; set; }

    public string Direction { get; set; } = "";

    public string State { get; set; } = "";

    /// <summary>Provider's immutable id; with the mailbox it is the idempotency key for inbound mail.</summary>
    public string? ProviderMessageId { get; set; }

    /// <summary>Normalised (no angle brackets, lower case). Not unique across mailboxes.</summary>
    public string? InternetMessageId { get; set; }

    public string? ConversationId { get; set; }

    public string? InReplyTo { get; set; }

    public string? References { get; set; }

    public string Subject { get; set; } = "";

    public string FromAddress { get; set; } = "";

    public string? FromName { get; set; }

    public string ToJson { get; set; } = "[]";

    public string CcJson { get; set; } = "[]";

    public DateTimeOffset OccurredAt { get; set; }

    /// <summary>Plain text (derived from HTML when needed). Never logged or audited.</summary>
    public string? BodyText { get; set; }

    /// <summary>The original HTML exactly as received (immutable, never rendered). The sanitised version lives with the ticket message.</summary>
    public string? BodyHtmlOriginal { get; set; }

    /// <summary>Small selected set of headers (JSON) used by the noise guard and diagnostics.</summary>
    public string HeadersJson { get; set; } = "{}";

    public Guid? TicketId { get; set; }

    public Guid? TicketMessageId { get; set; }

    /// <summary>How the ticket was chosen: InReplyTo, References, Token, Conversation, New.</summary>
    public string? CorrelationMethod { get; set; }

    /// <summary>The subject named a ticket of this department but there was no strong evidence: suggested, never attached.</summary>
    public string? SubjectHintReference { get; set; }

    public string? Flags { get; set; }

    public string? IgnoreReason { get; set; }

    public int Attempts { get; set; }

    public string? LastError { get; set; }

    public DateTimeOffset? NextAttemptAt { get; set; }

    // ---- outbound only ----
    public string? OutboundToken { get; set; }

    public Guid? ReplyToMessageId { get; set; }

    public string? ProviderDraftId { get; set; }

    public string? TicketReference { get; set; }

    public Guid? RequestedByUserId { get; set; }

    /// <summary>Set once the outcome (Sent/Failed) has been reported to Ticketing; unset rows are re-reported by the dispatcher.</summary>
    public DateTimeOffset? DeliveryReportedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Receipt of a webhook notification: identifiers only (never bodies), for diagnostics and duplicate visibility.</summary>
internal sealed class MailNotification
{
    public Guid Id { get; set; }

    public Guid MailboxId { get; set; }

    public string ChangeType { get; set; } = "";

    public string? ResourceId { get; set; }

    public DateTimeOffset ReceivedAt { get; set; }
}
