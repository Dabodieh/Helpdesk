using Microsoft.Extensions.Logging;

namespace Helpdesk.Modules.Mail;

/// <summary>Source-generated log messages. Only identifiers and short error codes appear here: never addresses, subjects or bodies.</summary>
internal static partial class MailLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Mailbox {MailboxId}: delta state was reset by the provider; resynchronising")]
    public static partial void DeltaReset(this ILogger logger, Guid mailboxId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Mailbox {MailboxId}: sync failed: {Error}")]
    public static partial void SyncFailed(this ILogger logger, Guid mailboxId, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Mailbox {MailboxId}: subscription maintenance failed: {Error}")]
    public static partial void SubscriptionFailed(this ILogger logger, Guid mailboxId, string? error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Mailbox {MailboxId}: reconciliation failed: {Error}")]
    public static partial void ReconciliationFailed(this ILogger logger, Guid mailboxId, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Mail webhook: {Rejected} notification(s) rejected (unknown subscription or client state mismatch)")]
    public static partial void WebhookRejected(this ILogger logger, int rejected);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Mail {MailMessageId}: evidence pointing into another department was discarded")]
    public static partial void CrossDepartmentDiscarded(this ILogger logger, Guid mailMessageId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Mail {MailMessageId} failed in the inbound pipeline: {Error}")]
    public static partial void InboundFailed(this ILogger logger, Guid mailMessageId, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Outbound mail {OutboundId} failed: {Error} (transient: {Transient})")]
    public static partial void OutboundFailed(this ILogger logger, Guid outboundId, string error, bool transient);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Outbound mail {OutboundId}: send outcome is uncertain ({Error}); not retried automatically")]
    public static partial void OutboundUncertain(this ILogger logger, Guid outboundId, string error);

    [LoggerMessage(Level = LogLevel.Information, Message = "Outbound mail {OutboundId}: reconciled with the provider sent copy")]
    public static partial void OutboundReconciled(this ILogger logger, Guid outboundId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Mailbox {MailboxId}: a non-delivery report was matched to outbound mail {OutboundId} by {Evidence}")]
    public static partial void NdrCorrelated(this ILogger logger, Guid mailboxId, Guid outboundId, string evidence);

    [LoggerMessage(Level = LogLevel.Information, Message = "Mailbox {MailboxId}: a non-delivery report could not be matched to any outbound mail (suppressed, no ticket)")]
    public static partial void NdrUncorrelated(this ILogger logger, Guid mailboxId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reporting delivery of {OutboundId} to Ticketing failed ({Error}); the dispatcher will retry")]
    public static partial void DeliveryReportFailed(this ILogger logger, Guid outboundId, string error);
}
