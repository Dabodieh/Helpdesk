namespace Helpdesk.Modules.Mail.Services;

/// <summary>Codes written to <c>mail.messages.last_error</c> by the outbound pipeline itself (provider failures use <see cref="MailText.ShortError"/>).</summary>
internal static class MailErrorCodes
{
    public const string OutcomeUncertain = "outcome_uncertain";
    public const string NdrDeliveryFailed = "ndr_delivery_failed";
    public const string MailboxInactive = "mailbox_inactive";
}

/// <summary>
/// Operational categories shown to agents and operators. The raw provider code stays in <c>last_error</c> (and the structured logs); agents only ever
/// see a category, never a Graph payload. Categories deliberately say what is KNOWN: <c>outcome_uncertain</c> is not <c>send_failed</c>, and an NDR
/// (<c>delivery_failed</c>) is the only confirmed non-delivery.
/// </summary>
internal static class MailErrors
{
    public const string Uncertain = "outcome_uncertain";
    public const string DeliveryFailed = "delivery_failed";
    public const string Authentication = "authentication";
    public const string PermissionDenied = "permission_denied";
    public const string MailboxUnavailable = "mailbox_unavailable";
    public const string RateLimited = "rate_limited";
    public const string ProviderUnavailable = "provider_unavailable";
    public const string Rejected = "rejected_by_provider";
    public const string SendFailed = "send_failed";

    public static string? Category(string? lastError)
    {
        if (string.IsNullOrEmpty(lastError))
        {
            return null;
        }

        if (lastError.Contains(MailErrorCodes.OutcomeUncertain, StringComparison.Ordinal))
        {
            return Uncertain;
        }

        if (lastError.Contains(MailErrorCodes.NdrDeliveryFailed, StringComparison.Ordinal))
        {
            return DeliveryFailed;
        }

        if (lastError.Contains(MailErrorCodes.MailboxInactive, StringComparison.Ordinal))
        {
            return MailboxUnavailable;
        }

        return lastError switch
        {
            _ when lastError.Contains("graph_401", StringComparison.Ordinal) => Authentication,
            _ when lastError.Contains("graph_403", StringComparison.Ordinal) => PermissionDenied,
            _ when lastError.Contains("graph_404", StringComparison.Ordinal) => MailboxUnavailable,
            _ when lastError.Contains("graph_429", StringComparison.Ordinal) => RateLimited,
            _ when lastError.Contains("graph_5", StringComparison.Ordinal) || lastError.Contains("graph_network", StringComparison.Ordinal)
                || lastError.Contains("graph_timeout", StringComparison.Ordinal) || lastError.Contains("Transient", StringComparison.Ordinal) => ProviderUnavailable,
            _ when lastError.Contains("graph_4", StringComparison.Ordinal) || lastError.Contains("Permanent", StringComparison.Ordinal) => Rejected,
            _ => SendFailed,
        };
    }
}

/// <summary>
/// What an operator can expect of a problem: <c>temporary</c> (the provider or network was unavailable or throttling; retrying later normally works), <c>permanent</c> (the message itself
/// was rejected or bounced; retrying the same message will not help), <c>action_required</c> (the mailbox or its consent is misconfigured; fix that, then retry), <c>uncertain</c>
/// (the message may already have been delivered; never retried automatically), <c>unknown</c> (no usable cause recorded).
/// </summary>
internal static class MailProblemNature
{
    public const string Temporary = "temporary";
    public const string Permanent = "permanent";
    public const string ActionRequired = "action_required";
    public const string Uncertain = "uncertain";
    public const string Unknown = "unknown";

    public static string? Of(string? category) => category switch
    {
        null => null,
        MailErrors.Uncertain => Uncertain,
        MailErrors.RateLimited or MailErrors.ProviderUnavailable => Temporary,
        MailErrors.DeliveryFailed or MailErrors.Rejected => Permanent,
        MailErrors.Authentication or MailErrors.PermissionDenied or MailErrors.MailboxUnavailable => ActionRequired,
        _ => Unknown,
    };
}

/// <summary>Machine codes of problem responses specific to mail administration.</summary>
internal static class MailProblemCodes
{
    public const string OutcomeUncertain = "outcome_uncertain";
}
