namespace Helpdesk.GraphSpike.Tests;

internal enum CorrelationMethod
{
    None,
    InReplyTo,
    References,
    OutboundToken,
    ConversationId,
    /// <summary>Weak: subject key + sender is a known participant of that ticket + same department.</summary>
    SubjectTokenParticipant,
}

/// <param name="TicketId">Ticket to append to, or null = create a new ticket.</param>
/// <param name="SubjectHintTicketId">Ticket named by a subject key in the receiving department; a hint only.</param>
/// <param name="CrossDepartmentMatchIgnored">Some evidence pointed into another department and was discarded (log it).</param>
/// <param name="MailboxMismatch">Matched ticket lives on another mailbox of the same department (flag for review).</param>
internal sealed record CorrelationResult(
    string? TicketId,
    CorrelationMethod Method,
    string? SubjectHintTicketId,
    bool CrossDepartmentMatchIgnored,
    bool MailboxMismatch);

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
/// 4 subject key (weak hint; attaches only with a known-participant sender). Department comes from the receiving
/// mailbox; evidence pointing into another department is discarded and reported, never followed.
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

        // 4. Subject key: weak hint, never sole evidence. It attaches only when the sender is already a participant
        //    of that ticket and the ticket is in the receiving department.
        string? hint = null;
        var keyMatch = Patterns.SubjectKey().Match(message.Subject);
        if (keyMatch.Success)
        {
            var named = _tickets.Values.Where(t => Text.Eq(t.Key, keyMatch.Groups[1].Value)).ToList();
            var inDepartment = named.FirstOrDefault(t => Text.Eq(t.DepartmentId, department));
            if (inDepartment is not null)
            {
                hint = inDepartment.TicketId;
                if (message.FromAddress is not null && inDepartment.Participants.Contains(message.FromAddress))
                {
                    return new CorrelationResult(inDepartment.TicketId, CorrelationMethod.SubjectTokenParticipant, hint,
                        crossIgnored, !Text.Eq(inDepartment.MailboxId, message.MailboxId));
                }
            }
            else if (named.Count > 0)
            {
                crossIgnored = true;
            }
        }

        return new CorrelationResult(null, CorrelationMethod.None, hint, crossIgnored, false);
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
