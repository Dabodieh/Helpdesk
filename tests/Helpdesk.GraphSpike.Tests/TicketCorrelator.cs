namespace Helpdesk.GraphSpike.Tests;

internal enum CorrelationMethod
{
    None,
    InReplyTo,
    References,
    OutboundToken,
    ConversationId,
}

/// <param name="TicketId">Ticket to append to, or null = create a new ticket.</param>
/// <param name="SubjectHintTicketId">Ticket named by a subject key in the receiving department. A suggestion only: never attaches.</param>
/// <param name="CrossDepartmentMatchIgnored">Some evidence pointed into another department and was discarded (log it).</param>
/// <param name="MailboxMismatch">Matched ticket lives on another mailbox of the same department (flag for review).</param>
/// <param name="NeedsReview">No strong evidence, but the subject names a ticket in the receiving department: create a new ticket or route to manual correlation, recording SubjectHintTicketId as a suggestion.</param>
internal sealed record CorrelationResult(
    string? TicketId,
    CorrelationMethod Method,
    string? SubjectHintTicketId,
    bool CrossDepartmentMatchIgnored,
    bool MailboxMismatch,
    bool NeedsReview = false);

internal sealed class TicketInfo
{
    public TicketInfo(string ticketId, string departmentId, string mailboxId, string key, IEnumerable<string> participants)
    {
        TicketId = ticketId;
        DepartmentId = departmentId;
        MailboxId = mailboxId;
        Key = key;
        Participants = new HashSet<string>(participants, StringComparer.OrdinalIgnoreCase);
    }

    public string TicketId { get; }
    public string DepartmentId { get; }
    public string MailboxId { get; }
    public string Key { get; }
    public HashSet<string> Participants { get; }
}

internal sealed record StoredMessage(
    string TicketId,
    string MailboxId,
    string DepartmentId,
    string? InternetMessageId,
    string? ConversationId,
    Direction Direction);

/// <summary>
/// Deterministic, ordered correlation of an inbound message to an existing ticket (spike version).
/// Precedence: 1 In-Reply-To / References, 2 outbound token, 3 conversationId (same mailbox only),
/// otherwise no attachment. A subject key (e.g. [IT-42]) is a hint only: it never attaches, not even for a known
/// participant; it is returned as a suggestion (NeedsReview). Department comes from the receiving mailbox;
/// evidence pointing into another department is discarded and reported, never followed.
/// </summary>
internal sealed class TicketCorrelator
{
    private readonly MailboxRegistry _registry;
    private readonly List<StoredMessage> _messages = new();
    private readonly Dictionary<string, TicketInfo> _tickets = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _tokens = new(StringComparer.Ordinal);

    public TicketCorrelator(MailboxRegistry registry) => _registry = registry;

    public void AddTicket(TicketInfo ticket) => _tickets[ticket.TicketId] = ticket;

    public void RegisterOutboundToken(string token, string ticketId) => _tokens[token] = ticketId;

    /// <summary>Remember a persisted message (inbound or our own outbound) as part of a ticket.</summary>
    public void Record(string ticketId, SpikeMessage message) =>
        _messages.Add(new StoredMessage(
            ticketId,
            message.MailboxId,
            _registry.DepartmentOf(message.MailboxId),
            message.InternetMessageId,
            message.ConversationId,
            message.Direction));

    public CorrelationResult Correlate(SpikeMessage message)
    {
        var department = _registry.DepartmentOf(message.MailboxId);
        var crossIgnored = false;

        // 1. In-Reply-To, then References (nearest ancestor first), matched against stored internetMessageId
        //    of inbound AND outbound messages. Same department only; the same mailbox is preferred.
        foreach (var (id, method) in HeaderCandidates(message))
        {
            var candidates = _messages
                .Where(s => Text.Eq(s.InternetMessageId, id))
                .OrderByDescending(s => Text.Eq(s.MailboxId, message.MailboxId))
                .ToList();
            foreach (var stored in candidates)
            {
                if (!Text.Eq(stored.DepartmentId, department))
                {
                    crossIgnored = true;
                    continue;
                }

                return new CorrelationResult(stored.TicketId, method, null, crossIgnored,
                    !Text.Eq(stored.MailboxId, message.MailboxId));
            }
        }

        // 2. Token we control (opaque random value from our outbound mail: x- header echoed by DSNs / forwards,
        //    or a body footer marker). Not guessable, unlike ticket numbers.
        foreach (var token in Tokens(message))
        {
            if (!_tokens.TryGetValue(token, out var ticketId) || !_tickets.TryGetValue(ticketId, out var ticket))
            {
                continue;
            }

            if (!Text.Eq(ticket.DepartmentId, department))
            {
                crossIgnored = true;
                continue;
            }

            return new CorrelationResult(ticket.TicketId, CorrelationMethod.OutboundToken, null, crossIgnored,
                !Text.Eq(ticket.MailboxId, message.MailboxId));
        }

        // 3. conversationId: only meaningful inside one mailbox. Anything seen in another mailbox is ignored;
        //    if that mailbox belongs to another department it is also reported.
        if (!string.IsNullOrEmpty(message.ConversationId))
        {
            var sameConversation = _messages.Where(s => string.Equals(s.ConversationId, message.ConversationId, StringComparison.Ordinal)).ToList();
            foreach (var stored in sameConversation)
            {
                if (Text.Eq(stored.MailboxId, message.MailboxId))
                {
                    return new CorrelationResult(stored.TicketId, CorrelationMethod.ConversationId, null, crossIgnored, false);
                }

                if (!Text.Eq(stored.DepartmentId, department))
                {
                    crossIgnored = true;
                }
            }
        }

        // 4. Subject key: hint only. Without strong evidence (steps 1-3) the safe outcome is NOT to attach; the
        //    candidate in the receiving department is recorded as a suggestion for a new ticket / manual correlation.
        //    A candidate in another department is discarded and reported.
        string? hint = null;
        var keyMatch = Patterns.SubjectKey().Match(message.Subject);
        if (keyMatch.Success)
        {
            var named = _tickets.Values.Where(t => Text.Eq(t.Key, keyMatch.Groups[1].Value)).ToList();
            var inDepartment = named.FirstOrDefault(t => Text.Eq(t.DepartmentId, department));
            if (inDepartment is not null)
            {
                hint = inDepartment.TicketId;
            }
            else if (named.Count > 0)
            {
                crossIgnored = true;
            }
        }

        return new CorrelationResult(null, CorrelationMethod.None, hint, crossIgnored, false, hint is not null);
    }

    private static List<(string Id, CorrelationMethod Method)> HeaderCandidates(SpikeMessage message)
    {
        var list = new List<(string, CorrelationMethod)>();
        if (message.InReplyTo is not null)
        {
            list.Add((message.InReplyTo, CorrelationMethod.InReplyTo));
        }

        for (var i = message.References.Count - 1; i >= 0; i--)
        {
            var id = message.References[i];
            if (!Text.Eq(id, message.InReplyTo))
            {
                list.Add((id, CorrelationMethod.References));
            }
        }

        return list;
    }

    private static List<string> Tokens(SpikeMessage message)
    {
        var tokens = new List<string>();
        if (message.Headers.TryGetValue("x-helpdesk-token", out var header) && !string.IsNullOrWhiteSpace(header))
        {
            tokens.Add(header.Trim());
        }

        foreach (System.Text.RegularExpressions.Match m in Patterns.BodyToken().Matches(message.BodyText))
        {
            tokens.Add(m.Groups[1].Value);
        }

        return tokens;
    }
}
