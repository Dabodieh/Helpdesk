using Helpdesk.Modules.Mail.Contracts;

namespace Helpdesk.Modules.Mail.Services;

/// <summary><see cref="IEmailHtmlSanitiser"/> over the one allow-list sanitiser the email model already uses (no scripts, frames, handlers or remote images).</summary>
internal sealed class EmailHtmlSanitiser : IEmailHtmlSanitiser
{
    public string Sanitise(string html) => MailText.SanitiseHtml(html);
}
