using System.Text.RegularExpressions;
using Helpdesk.Modules.Mail.Domain;
using Helpdesk.Modules.Mail.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Modules.Mail.Services;

/// <summary>
/// Matches a non-delivery report (NDR) to the outbound helpdesk message it is about. The NDR itself is always suppressed (never a ticket, a reply or an
/// acknowledgement trigger); this only turns confirmed evidence into "that message was not delivered". Evidence, strongest first and never the subject:
/// the NDR's In-Reply-To/References naming the sent copy's Internet Message-ID, or the random per-message token our own footer carries (<c>[hd:token]</c>),
/// which providers usually quote. Only outbound mail of the SAME mailbox (hence the same department) can match.
/// </summary>
internal static partial class NdrCorrelation
{
    [GeneratedRegex(@"\[hd:([0-9a-f]{32})\]", RegexOptions.CultureInvariant)]
    private static partial Regex TokenPattern();

    public sealed record Match(MailMessage Outbound, string Evidence);

    public static async Task<Match?> FindAsync(MailDbContext db, MailMessage ndr, CancellationToken ct)
    {
        var candidates = db.Messages.Where(m => m.MailboxId == ndr.MailboxId && m.DepartmentId == ndr.DepartmentId && m.Direction == MessageDirections.Outbound
            && (m.State == MessageStates.Sent || m.State == MessageStates.Uncertain));

        var ids = $"{ndr.InReplyTo} {ndr.References}".Split(' ', StringSplitOptions.RemoveEmptyEntries).Distinct().ToArray();
        if (ids.Length > 0)
        {
            var byHeader = await candidates.Where(m => m.InternetMessageId != null && ids.Contains(m.InternetMessageId)).Take(2).ToListAsync(ct);
            if (byHeader.Count == 1)
            {
                return new Match(byHeader[0], "message_id");
            }
        }

        var tokens = TokenPattern().Matches(ndr.BodyText ?? "").Select(m => m.Groups[1].Value).Distinct().Take(5).ToArray();
        if (tokens.Length > 0)
        {
            var byToken = await candidates.Where(m => m.OutboundToken != null && tokens.Contains(m.OutboundToken)).Take(2).ToListAsync(ct);
            if (byToken.Count == 1)
            {
                return new Match(byToken[0], "token");
            }
        }

        return null;
    }
}
