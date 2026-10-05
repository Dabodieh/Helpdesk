using System.Text.RegularExpressions;
using Helpdesk.Modules.Mail.Domain;
using Helpdesk.Modules.Mail.Persistence;
using Helpdesk.Modules.Mail.Providers;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Modules.Mail.Services;

internal static class IgnoreReasons
{
    public const string OwnAddress = "own_address";
    public const string AutoSubmitted = "auto_submitted";
    public const string Bulk = "precedence_bulk";
    public const string AutoResponseSuppress = "auto_response_suppress";
    public const string MailingList = "mailing_list";
    public const string Bounce = "bounce";
    public const string Draft = "draft";
    public const string NoSender = "no_sender";
    public const string SenderRateLimited = "sender_rate_limited";
    public const string DuplicateInternetMessageId = "duplicate_internet_message_id";
}

/// <summary>
/// Loop and noise protection (EMAIL_ARCHITECTURE.md, inbound step 7). Pure: decides from addresses and a few headers whether an
/// inbound message may become (part of) a ticket. Ignored mail is recorded, never dropped silently.
/// </summary>
internal static class MailGuard
{
    private static readonly string[] BounceSenders = ["mailer-daemon", "postmaster"];

    /// <param name="helpdeskAddresses">Normalised addresses of EVERY helpdesk mailbox (all departments), so two mailboxes cannot ping-pong.</param>
    public static string? Evaluate(InboundEmail email, ISet<string> helpdeskAddresses)
    {
        if (email.IsDraft)
        {
            return IgnoreReasons.Draft;
        }

        if (email.From is null || string.IsNullOrWhiteSpace(email.From.Address))
        {
            return IgnoreReasons.NoSender;
        }

        var from = MailText.NormaliseAddress(email.From.Address);
        if (helpdeskAddresses.Contains(from))
        {
            return IgnoreReasons.OwnAddress;
        }

        var headers = email.Headers;
        if (headers.TryGetValue("Auto-Submitted", out var auto) && !string.Equals(auto.Trim(), "no", StringComparison.OrdinalIgnoreCase))
        {
            return IgnoreReasons.AutoSubmitted;
        }

        if (headers.TryGetValue("Precedence", out var precedence)
            && precedence.Trim().ToLowerInvariant() is "bulk" or "list" or "junk")
        {
            return IgnoreReasons.Bulk;
        }

        if (headers.ContainsKey("X-Auto-Response-Suppress"))
        {
            return IgnoreReasons.AutoResponseSuppress;
        }

        if (headers.ContainsKey("List-Id") || headers.ContainsKey("List-Unsubscribe"))
        {
            return IgnoreReasons.MailingList;
        }

        var local = from.Split('@')[0];
        if (BounceSenders.Contains(local, StringComparer.Ordinal)
            || (headers.TryGetValue("Content-Type", out var contentType)
                && contentType.Contains("report-type=delivery-status", StringComparison.OrdinalIgnoreCase)))
        {
            return IgnoreReasons.Bounce;
        }

        return null;
    }
}

internal enum CorrelationMethod
{
    New,
    InReplyTo,
    References,
    Token,
    Conversation,
}

/// <param name="TicketId">The ticket the message is evidence for, or null (new ticket).</param>
/// <param name="SubjectHint">A ticket reference of the receiving department found in the subject: a SUGGESTION only, never attached.</param>
/// <param name="CrossDepartmentIgnored">Strong evidence pointed into another department and was discarded.</param>
internal sealed record Correlation(Guid? TicketId, CorrelationMethod Method, string? SubjectHint, bool CrossDepartmentIgnored);

/// <summary>
/// Deterministic ticket correlation (ADR-011, EMAIL_ARCHITECTURE.md "Threading"), always within the receiving mailbox's
/// department: In-Reply-To, then References (nearest ancestor first), then our outbound token, then the conversation id of the SAME
/// mailbox. A ticket reference in the subject is a hint only and never attaches by itself. Evidence that points into another
/// department is discarded and reported, never used.
/// </summary>
internal sealed partial class CorrelationEngine(MailDbContext db)
{
    [GeneratedRegex(@"(?<![A-Za-z0-9])([A-Za-z][A-Za-z0-9]{1,9}-[0-9]{1,9})(?![A-Za-z0-9])")]
    private static partial Regex ReferencePattern();

    public static IReadOnlyList<string> SubjectReferences(string? subject, string departmentKey)
    {
        if (string.IsNullOrWhiteSpace(subject))
        {
            return [];
        }

        // Only the receiving department's own key counts as a hint; a key of another department is not even suggested.
        return [.. ReferencePattern().Matches(subject)
            .Select(m => m.Groups[1].Value.ToUpperInvariant())
            .Where(r => r.StartsWith(departmentKey + "-", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.Ordinal)];
    }

    public async Task<Correlation> CorrelateAsync(MailMessage message, InboundEmail email, string departmentKey, CancellationToken ct)
    {
        var departmentId = message.DepartmentId;
        var crossDepartment = false;

        // 1 + 2: In-Reply-To, then References nearest ancestor first, matched against stored internet ids of our inbound AND outbound mail.
        var candidates = new List<(string Id, CorrelationMethod Method)>();
        if (email.Headers.TryGetValue("In-Reply-To", out var inReplyTo))
        {
            candidates.AddRange(MailText.ParseMessageIds(inReplyTo).Select(id => (id, CorrelationMethod.InReplyTo)));
        }

        if (email.Headers.TryGetValue("References", out var references))
        {
            candidates.AddRange(MailText.ParseMessageIds(references).Reverse().Select(id => (id, CorrelationMethod.References)));
        }

        if (candidates.Count > 0)
        {
            var ids = candidates.Select(c => c.Id).Distinct(StringComparer.Ordinal).ToList();
            var rows = await db.Messages.AsNoTracking()
                .Where(m => m.InternetMessageId != null && ids.Contains(m.InternetMessageId) && m.TicketId != null)
                .Select(m => new { m.InternetMessageId, m.DepartmentId, m.MailboxId, m.TicketId })
                .ToListAsync(ct);
            foreach (var (id, method) in candidates)
            {
                var hits = rows.Where(r => r.InternetMessageId == id).ToList();
                var own = hits.Where(r => r.DepartmentId == departmentId).OrderByDescending(r => r.MailboxId == message.MailboxId).FirstOrDefault();
                if (own is not null)
                {
                    return new Correlation(own.TicketId, method, null, crossDepartment);
                }

                crossDepartment |= hits.Count > 0;
            }
        }

        // 3: the opaque token we put into our own outbound mail (body footer; the x- header never comes back in customer replies).
        var token = MailText.ExtractToken(message.BodyText) ?? (email.Headers.TryGetValue("x-helpdesk-token", out var header) ? header.Trim() : null);
        if (token is not null)
        {
            var hit = await db.Messages.AsNoTracking().Where(m => m.OutboundToken == token && m.TicketId != null)
                .Select(m => new { m.DepartmentId, m.TicketId }).FirstOrDefaultAsync(ct);
            if (hit is not null)
            {
                if (hit.DepartmentId == departmentId)
                {
                    return new Correlation(hit.TicketId, CorrelationMethod.Token, null, crossDepartment);
                }

                crossDepartment = true;
            }
        }

        // 4: conversation id, SAME mailbox only (a conversation id is per mailbox and fragile; never the sole key across mailboxes).
        if (!string.IsNullOrEmpty(email.ConversationId))
        {
            var conversationTicket = await db.Messages.AsNoTracking()
                .Where(m => m.MailboxId == message.MailboxId && m.ConversationId == email.ConversationId && m.TicketId != null && m.Id != message.Id)
                .OrderByDescending(m => m.OccurredAt).Select(m => m.TicketId).FirstOrDefaultAsync(ct);
            if (conversationTicket is not null)
            {
                return new Correlation(conversationTicket, CorrelationMethod.Conversation, null, crossDepartment);
            }
        }

        // 5: no strong evidence -> new ticket; the subject's candidate (receiving department only) is recorded as a suggestion.
        var subjectReferences = SubjectReferences(email.Subject, departmentKey);
        var hint = subjectReferences.Count > 0 ? subjectReferences[0] : null;
        return new Correlation(null, CorrelationMethod.New, hint, crossDepartment);
    }
}
