using System.Globalization;

namespace Helpdesk.Modules.Mail.Providers;

internal sealed record FakeAttachment(string Name, byte[] Content, string? ContentType = null, bool IsInline = false, string? ContentId = null);

internal sealed record FakeInbound(
    string Mailbox,
    string From,
    string Subject,
    string? FromName = null,
    string? BodyText = null,
    string? BodyHtml = null,
    IReadOnlyList<string>? To = null,
    IReadOnlyList<string>? Cc = null,
    string? InReplyTo = null,
    IReadOnlyList<string>? References = null,
    string? ConversationId = null,
    string? InternetMessageId = null,
    IReadOnlyDictionary<string, string>? Headers = null,
    IReadOnlyList<FakeAttachment>? Attachments = null,
    DateTimeOffset? ReceivedAt = null);

internal sealed record FakeSent(
    string Mailbox,
    string DraftId,
    string InternetMessageId,
    string? InReplyTo,
    string? References,
    string? ConversationId,
    string Subject,
    string BodyHtml,
    IReadOnlyList<EmailAddress> To,
    IReadOnlyList<EmailAddress> Cc,
    string Token,
    DateTimeOffset SentAt,
    EmailAddress? ReplyTo = null);

/// <summary>
/// In-process mailbox simulator behind <see cref="IMailProvider"/>: drives the complete email loop without a tenant (integration
/// tests, local development). It reproduces the DOCUMENTED Graph shapes only; in particular it ASSUMES Exchange sets
/// <c>In-Reply-To</c>/<c>References</c> on a reply draft (runbook E-23), which is NOT yet validated against a real tenant.
/// </summary>
internal sealed class FakeMailProvider : IMailProvider
{
    private readonly object _gate = new();
    private readonly Dictionary<string, MailboxState> _mailboxes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _subscriptions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _clientStates = new(StringComparer.Ordinal);
    private int _sequence;
    private int _failNextSends;
    private int _failNextAttachmentFetches;
    private bool _failNextSendPermanently;

    public string Kind => Contracts.MailProviderKinds.Fake;

    private sealed class StoredMail
    {
        public required int Sequence { get; init; }

        public required InboundEmail Email { get; init; }

        public required Dictionary<string, byte[]> AttachmentBytes { get; init; }
    }

    private sealed class MailboxState
    {
        public List<StoredMail> Inbox { get; } = [];

        public Dictionary<string, (OutboundContent Content, string InternetMessageId, string? InReplyTo, string? References, string? ConversationId)> Drafts { get; } = new(StringComparer.Ordinal);

        public List<FakeSent> Sent { get; } = [];
    }

    // ---- test / dev control surface ----

    public string Inject(FakeInbound mail)
    {
        ArgumentNullException.ThrowIfNull(mail);
        lock (_gate)
        {
            var state = StateOf(mail.Mailbox);
            var sequence = ++_sequence;
            var id = "fake-" + Guid.NewGuid().ToString("N");
            var internetId = mail.InternetMessageId ?? $"<{Guid.NewGuid():N}@sender.fake>";
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (mail.InReplyTo is not null)
            {
                headers["In-Reply-To"] = mail.InReplyTo;
            }

            if (mail.References is { Count: > 0 })
            {
                headers["References"] = string.Join(' ', mail.References);
            }

            foreach (var (name, value) in mail.Headers ?? new Dictionary<string, string>())
            {
                headers[name] = value;
            }

            var attachments = mail.Attachments ?? [];
            var infos = new List<InboundAttachmentInfo>();
            var bytes = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            for (var i = 0; i < attachments.Count; i++)
            {
                var a = attachments[i];
                var attachmentId = $"att-{sequence}-{i}";
                infos.Add(new InboundAttachmentInfo(attachmentId, a.Name, a.ContentType, a.Content.LongLength, a.IsInline, a.ContentId));
                bytes[attachmentId] = a.Content;
            }

            var email = new InboundEmail(
                id,
                internetId,
                mail.ConversationId ?? "conv-" + Guid.NewGuid().ToString("N"),
                mail.Subject,
                new EmailAddress(mail.From, mail.FromName),
                [.. (mail.To ?? [mail.Mailbox]).Select(a => new EmailAddress(a, null))],
                [.. (mail.Cc ?? []).Select(a => new EmailAddress(a, null))],
                mail.ReceivedAt ?? DateTimeOffset.UtcNow,
                mail.BodyText,
                mail.BodyHtml,
                headers,
                false,
                infos);
            state.Inbox.Add(new StoredMail { Sequence = sequence, Email = email, AttachmentBytes = bytes });
            return id;
        }
    }

    /// <summary>Builds the reply a customer would send to something we sent (headers as a mail client sets them).</summary>
    public static FakeInbound CustomerReplyTo(FakeSent sent, string from, string bodyText, string? fromName = null) =>
        new(
            sent.Mailbox,
            from,
            sent.Subject.StartsWith("Re:", StringComparison.OrdinalIgnoreCase) ? sent.Subject : "Re: " + sent.Subject,
            fromName,
            bodyText,
            InReplyTo: sent.InternetMessageId,
            References: [.. (sent.References ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries), sent.InternetMessageId],
            ConversationId: sent.ConversationId);

    public IReadOnlyList<FakeSent> SentMail(string mailbox)
    {
        lock (_gate)
        {
            return [.. StateOf(mailbox).Sent];
        }
    }

    public void FailNextSends(int count, bool permanently = false)
    {
        lock (_gate)
        {
            _failNextSends = count;
            _failNextSendPermanently = permanently;
        }
    }

    /// <summary>The client state the application sent when creating a subscription (what a real Graph would echo in notifications).</summary>
    public string? ClientStateOf(string subscriptionId)
    {
        lock (_gate)
        {
            return _clientStates.TryGetValue(subscriptionId, out var state) ? state : null;
        }
    }

    /// <summary>Makes the next attachment downloads fail transiently (to exercise the retry path of the inbound pipeline).</summary>
    public void FailNextAttachmentFetches(int count)
    {
        lock (_gate)
        {
            _failNextAttachmentFetches = count;
        }
    }

    public int SubscriptionCount
    {
        get
        {
            lock (_gate)
            {
                return _subscriptions.Count;
            }
        }
    }

    // ---- IMailProvider ----

    public Task<ProviderSubscription> CreateSubscriptionAsync(MailboxRef mailbox, SubscriptionRequest request, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var id = "sub-" + Guid.NewGuid().ToString("N");
            _subscriptions[id] = mailbox.Address;
            _clientStates[id] = request.ClientState;
            return Task.FromResult(new ProviderSubscription(id, request.ExpiresAt));
        }
    }

    public Task<DateTimeOffset> RenewSubscriptionAsync(MailboxRef mailbox, string subscriptionId, DateTimeOffset newExpiry, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_subscriptions.ContainsKey(subscriptionId))
            {
                throw new PermanentProviderException("subscription_not_found");
            }
        }

        return Task.FromResult(newExpiry);
    }

    public Task DeleteSubscriptionAsync(MailboxRef mailbox, string subscriptionId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _subscriptions.Remove(subscriptionId);
        }

        return Task.CompletedTask;
    }

    public Task<DeltaResult> FetchChangesAsync(MailboxRef mailbox, string? deltaToken, DateTimeOffset notBefore, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var since = 0;
            if (deltaToken is not null && !int.TryParse(deltaToken, NumberStyles.None, CultureInfo.InvariantCulture, out since))
            {
                throw new DeltaResetException();
            }

            var state = StateOf(mailbox.Address);
            var changes = state.Inbox.Where(m => m.Sequence > since && (deltaToken is not null || m.Email.ReceivedAt >= notBefore)).OrderBy(m => m.Sequence)
                .Select(m => new MailChange(m.Email.ProviderMessageId, false)).ToList();
            var token = (state.Inbox.Count == 0 ? since : Math.Max(since, state.Inbox.Max(m => m.Sequence))).ToString(CultureInfo.InvariantCulture);
            return Task.FromResult(new DeltaResult(changes, token));
        }
    }

    public Task<InboundEmail?> GetMessageAsync(MailboxRef mailbox, string providerMessageId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult(StateOf(mailbox.Address).Inbox.FirstOrDefault(m => m.Email.ProviderMessageId == providerMessageId)?.Email);
        }
    }

    public Task<byte[]> GetAttachmentAsync(MailboxRef mailbox, string providerMessageId, string providerAttachmentId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_failNextAttachmentFetches > 0)
            {
                _failNextAttachmentFetches--;
                throw new TransientProviderException("attachment_unavailable");
            }

            var stored = StateOf(mailbox.Address).Inbox.FirstOrDefault(m => m.Email.ProviderMessageId == providerMessageId)
                ?? throw new PermanentProviderException("message_not_found");
            return Task.FromResult(stored.AttachmentBytes.TryGetValue(providerAttachmentId, out var bytes)
                ? bytes
                : throw new PermanentProviderException("attachment_not_found"));
        }
    }

    public Task<DraftResult> CreateDraftAsync(MailboxRef mailbox, OutboundContent content, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var state = StateOf(mailbox.Address);
            string? inReplyTo = null;
            string? references = null;
            string? conversationId = null;
            if (content.ReplyToProviderMessageId is { } replyTo)
            {
                var original = state.Inbox.FirstOrDefault(m => m.Email.ProviderMessageId == replyTo)?.Email
                    ?? throw new PermanentProviderException("reply_target_not_found");

                // Assumed Exchange behaviour (E-23): the reply draft carries In-Reply-To and an extended References chain.
                inReplyTo = original.InternetMessageId;
                var chain = original.Headers.TryGetValue("References", out var r) ? r : "";
                references = string.Join(' ', chain.Split(' ', StringSplitOptions.RemoveEmptyEntries).Append(original.InternetMessageId ?? "").Where(x => x.Length > 0));
                conversationId = original.ConversationId;
            }

            var draftId = "draft-" + Guid.NewGuid().ToString("N");
            var internetId = $"<{Guid.NewGuid():N}@helpdesk.fake>";
            state.Drafts[draftId] = (content, internetId, inReplyTo, references, conversationId);
            return Task.FromResult(new DraftResult(draftId, internetId));
        }
    }

    public Task SendDraftAsync(MailboxRef mailbox, string draftId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_failNextSends > 0)
            {
                _failNextSends--;
                throw _failNextSendPermanently ? new PermanentProviderException("send_rejected") : new TransientProviderException("send_unavailable");
            }

            var state = StateOf(mailbox.Address);
            if (!state.Drafts.TryGetValue(draftId, out var draft))
            {
                throw new PermanentProviderException("draft_not_found");
            }

            state.Drafts.Remove(draftId);
            state.Sent.Add(new FakeSent(
                mailbox.Address, draftId, draft.InternetMessageId, draft.InReplyTo, draft.References, draft.ConversationId,
                draft.Content.ReplyToProviderMessageId is null ? draft.Content.Subject : (draft.Content.Subject.StartsWith("Re:", StringComparison.OrdinalIgnoreCase) ? draft.Content.Subject : "Re: " + draft.Content.Subject),
                draft.Content.BodyHtml, draft.Content.To, draft.Content.Cc, draft.Content.Token, DateTimeOffset.UtcNow, draft.Content.ReplyTo));
            return Task.CompletedTask;
        }
    }

    public Task<SentCopy?> FindSentCopyAsync(MailboxRef mailbox, string draftId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var sent = StateOf(mailbox.Address).Sent.FirstOrDefault(s => s.DraftId == draftId);
            return Task.FromResult(sent is null ? null : new SentCopy(sent.DraftId, sent.InternetMessageId));
        }
    }

    private MailboxState StateOf(string address)
    {
        if (!_mailboxes.TryGetValue(address, out var state))
        {
            _mailboxes[address] = state = new MailboxState();
        }

        return state;
    }
}
