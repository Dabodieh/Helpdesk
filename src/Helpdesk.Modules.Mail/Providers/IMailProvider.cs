namespace Helpdesk.Modules.Mail.Providers;

/// <summary>A mailbox as the provider addresses it. <see cref="Reference"/> is the provider's stable id (Graph: user object id) or the address.</summary>
internal sealed record MailboxRef(Guid Id, string Address, string Reference);

internal sealed record EmailAddress(string Address, string? Name);

internal sealed record InboundAttachmentInfo(string ProviderAttachmentId, string FileName, string? ContentType, long Size, bool IsInline, string? ContentId);

/// <summary>The normalised inbound email: no provider types leak past this record.</summary>
internal sealed record InboundEmail(
    string ProviderMessageId,
    string? InternetMessageId,
    string? ConversationId,
    string Subject,
    EmailAddress? From,
    IReadOnlyList<EmailAddress> To,
    IReadOnlyList<EmailAddress> Cc,
    DateTimeOffset ReceivedAt,
    string? BodyText,
    string? BodyHtml,
    IReadOnlyDictionary<string, string> Headers,
    bool IsDraft,
    IReadOnlyList<InboundAttachmentInfo> Attachments);

internal sealed record MailChange(string ProviderMessageId, bool Removed);

internal sealed record DeltaResult(IReadOnlyList<MailChange> Changes, string DeltaToken);

internal sealed record ProviderSubscription(string Id, DateTimeOffset ExpiresAt);

internal sealed record SubscriptionRequest(string NotificationUrl, string LifecycleUrl, string ClientState, DateTimeOffset ExpiresAt);

/// <param name="ReplyToProviderMessageId">Set for a reply: the provider message being answered (Exchange then sets In-Reply-To/References, E-23).</param>
/// <param name="Token">Opaque correlation token (header and body footer).</param>
/// <param name="IsAutomatic">An acknowledgement: marked <c>x-auto-response-suppress: All</c> so a peer auto-responder or helpdesk does not answer it (MSG-006). Graph accepts only <c>x-</c> headers on a draft, so <c>Auto-Submitted</c> cannot be set; the real header set is a D-16 tenant check (E-37, E-38).</param>
internal sealed record OutboundContent(
    string? ReplyToProviderMessageId,
    string Subject,
    string BodyHtml,
    IReadOnlyList<EmailAddress> To,
    IReadOnlyList<EmailAddress> Cc,
    string Token,
    EmailAddress? ReplyTo = null,
    bool IsAutomatic = false);

/// <param name="InternetMessageId">What the provider reports for the draft; Exchange may rewrite it on submission (E-12), so the sent copy is looked up afterwards.</param>
internal sealed record DraftResult(string DraftId, string? InternetMessageId);

internal sealed record SentCopy(string ProviderMessageId, string? InternetMessageId);

/// <summary>Thrown for failures worth retrying (network, 5xx, 429). Anything else is permanent for the message.</summary>
internal sealed class TransientProviderException(string code, Exception? inner = null) : Exception(code, inner)
{
    public string Code { get; } = code;

    public TimeSpan? RetryAfter { get; init; }

    /// <summary>True when the request may nevertheless have been processed (connection lost, timeout, 5xx). False when the provider certainly did not act (e.g. 429).</summary>
    public bool Ambiguous { get; init; }
}

/// <summary>Permanent provider failure (4xx other than 429): retrying will not help.</summary>
internal sealed class PermanentProviderException(string code, Exception? inner = null) : Exception(code, inner)
{
    public string Code { get; } = code;
}

/// <summary>The provider forgot our delta state (Graph 410 syncStateNotFound): start over with a full resynchronisation.</summary>
internal sealed class DeltaResetException() : Exception("delta_reset");

/// <summary>
/// Provider seam (ADR-004). <c>MicrosoftGraphMailProvider</c> is the production implementation; the fake keeps the whole email
/// loop testable without a tenant. Implementations never log message content or tokens.
/// </summary>
internal interface IMailProvider
{
    string Kind { get; }

    Task<ProviderSubscription> CreateSubscriptionAsync(MailboxRef mailbox, SubscriptionRequest request, CancellationToken cancellationToken);

    Task<DateTimeOffset> RenewSubscriptionAsync(MailboxRef mailbox, string subscriptionId, DateTimeOffset newExpiry, CancellationToken cancellationToken);

    Task DeleteSubscriptionAsync(MailboxRef mailbox, string subscriptionId, CancellationToken cancellationToken);

    /// <summary>
    /// All changes since <paramref name="deltaToken"/>, with the new token. With no token (first run, or after a reset) only mail received at or
    /// after <paramref name="notBefore"/> is returned, so a mailbox that already holds history is never imported wholesale. Throws
    /// <see cref="DeltaResetException"/> when the token is no longer valid.
    /// </summary>
    Task<DeltaResult> FetchChangesAsync(MailboxRef mailbox, string? deltaToken, DateTimeOffset notBefore, CancellationToken cancellationToken);

    /// <summary>Null when the message no longer exists (deleted or moved away).</summary>
    Task<InboundEmail?> GetMessageAsync(MailboxRef mailbox, string providerMessageId, CancellationToken cancellationToken);

    Task<byte[]> GetAttachmentAsync(MailboxRef mailbox, string providerMessageId, string providerAttachmentId, CancellationToken cancellationToken);

    Task<DraftResult> CreateDraftAsync(MailboxRef mailbox, OutboundContent content, CancellationToken cancellationToken);

    Task SendDraftAsync(MailboxRef mailbox, string draftId, CancellationToken cancellationToken);

    /// <summary>The Sent Items copy of a sent draft (looked up by the draft's immutable id); null while it is not there (yet).</summary>
    Task<SentCopy?> FindSentCopyAsync(MailboxRef mailbox, string draftId, CancellationToken cancellationToken);
}
