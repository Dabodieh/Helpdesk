using Helpdesk.Modules.Audit.Contracts;
using Helpdesk.Modules.Mail.Persistence;
using Helpdesk.SharedKernel.Security;

namespace Helpdesk.Modules.Mail.Services;

internal static class MailAuditActions
{
    public const string MailboxCreated = "mailbox.created";
    public const string MailboxUpdated = "mailbox.updated";
    public const string MailboxActivated = "mailbox.activated";
    public const string MailboxDeactivated = "mailbox.deactivated";
    public const string TicketCreated = "mail.ticket_created";
    public const string Attached = "mail.attached";
    public const string Ignored = "mail.ignored";
    public const string Failed = "mail.failed";
    public const string Retried = "mail.retried";
    public const string Sent = "mail.sent";
    public const string SendFailed = "mail.send_failed";
    public const string SendUncertain = "mail.send_uncertain";
    public const string SendReconciled = "mail.send_reconciled";
    public const string DeliveryFailedReported = "mail.delivery_failed_reported";
    public const string UncertainRetried = "mail.uncertain_retried";
}

/// <summary>Writes mail-category audit events on the Mail DbContext's open transaction. Never receives subjects, addresses of correspondents or bodies.</summary>
internal sealed class MailAudit(IAuditWriter writer, MailDbContext db, ICurrentUser current)
{
    public const string MailboxObject = "mailbox";
    public const string MessageObject = "mail_message";

    public Task WriteSystemAsync(string action, string objectType, Guid objectId, Guid departmentId, object? previous, object? next, CancellationToken ct) =>
        writer.WriteAsync(new AuditEntry(AuditCategories.Mail, action, objectType, objectId, departmentId, null, null, previous, next, AuditSources.System), db, ct);

    /// <summary>A change made by the signed-in administrator (mailbox configuration).</summary>
    public Task WriteAsync(string action, string objectType, Guid objectId, Guid departmentId, object? previous, object? next, CancellationToken ct) =>
        writer.WriteAsync(new AuditEntry(
            AuditCategories.Mail, action, objectType, objectId, departmentId, current.UserId, current.DisplayName, previous, next), db, ct);
}
