using Helpdesk.Modules.Mail.Providers;
using Helpdesk.SharedKernel.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Helpdesk.Modules.Mail.Services;

/// <summary>
/// Sends non-ticket mail (account invitations) from <c>Mail:SystemMailbox</c> through the same provider seam as ticket mail. The mailbox is not a department mailbox and is not
/// stored in <c>mail.mailboxes</c>; the application identity must be allowed to send from it like any other mailbox. One attempt: a failure is reported, an uncertain outcome is
/// never retried here, and nothing is logged beyond the outcome category (no addresses, bodies or links).
/// </summary>
internal sealed class SystemMailer(IMailProvider provider, IOptions<MailOptions> options, ILogger<SystemMailer> log) : ISystemMailer
{
    private static readonly Action<ILogger, string, Exception?> Outcome = LoggerMessage.Define<string>(LogLevel.Information, new EventId(1, "SystemMailOutcome"), "System mail outcome {Outcome}");

    public bool IsConfigured => !string.IsNullOrWhiteSpace(options.Value.SystemMailbox);

    public async Task<SystemMailOutcome> SendAsync(SystemMail mail, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mail);
        if (options.Value.SystemMailbox is not { Length: > 0 } sender)
        {
            return SystemMailOutcome.NotConfigured;
        }

        var mailbox = new MailboxRef(Guid.Empty, sender, sender);
        var content = new OutboundContent(null, mail.Subject, mail.HtmlBody, [new EmailAddress(mail.ToAddress, mail.ToName)], [], "system-" + Guid.NewGuid().ToString("N"), IsAutomatic: true);
        try
        {
            var draft = await provider.CreateDraftAsync(mailbox, content, cancellationToken);
            await provider.SendDraftAsync(mailbox, draft.DraftId, cancellationToken);
            Outcome(log, "accepted", null);
            return SystemMailOutcome.Accepted;
        }
        catch (TransientProviderException ex) when (ex.Ambiguous)
        {
            Outcome(log, "uncertain", null);
            return SystemMailOutcome.Uncertain;
        }
        catch (Exception ex) when (ex is TransientProviderException or PermanentProviderException)
        {
            Outcome(log, "failed", null);
            return SystemMailOutcome.Failed;
        }
    }
}
