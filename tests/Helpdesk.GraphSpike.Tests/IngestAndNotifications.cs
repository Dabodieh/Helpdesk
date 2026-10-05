using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Helpdesk.GraphSpike.Tests;

internal enum IngestOutcome
{
    Ingested,
    DuplicateImmutableId,
    /// <summary>Same mailbox + same internetMessageId + same fingerprint under a different Graph id (e.g. id changed).</summary>
    DuplicateInternetMessageId,
    /// <summary>Same mailbox + same internetMessageId but different content: ingest, and flag for review.</summary>
    InternetMessageIdCollisionIngested,
}

/// <summary>
/// Ingestion idempotency boundary: (mailbox, immutable id) primary; (mailbox, internetMessageId) fallback guarded by a
/// content fingerprint so a sender re-using a Message-ID cannot make us silently drop a different message.
/// Keys are always scoped to the mailbox: the same Message-ID legitimately arrives in several mailboxes.
/// </summary>
internal sealed class IngestStore
{
    private readonly Dictionary<string, HashSet<string>> _immutableIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<string, string>> _internetIds = new(StringComparer.OrdinalIgnoreCase);

    public IngestOutcome TryIngest(SpikeMessage message)
    {
        // Graph ids are case-sensitive (docs): ordinal comparison. Mailbox address: case-insensitive.
        if (!_immutableIds.TryGetValue(message.MailboxId, out var ids))
        {
            ids = new HashSet<string>(StringComparer.Ordinal);
            _immutableIds[message.MailboxId] = ids;
        }

        if (ids.Contains(message.ImmutableId))
        {
            return IngestOutcome.DuplicateImmutableId;
        }

        var outcome = IngestOutcome.Ingested;
        if (message.InternetMessageId is not null)
        {
            if (!_internetIds.TryGetValue(message.MailboxId, out var seen))
            {
                seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                _internetIds[message.MailboxId] = seen;
            }

            var fingerprint = string.Join('|', message.Subject, message.FromAddress, message.SentRaw);
            if (seen.TryGetValue(message.InternetMessageId, out var existing))
            {
                if (string.Equals(existing, fingerprint, StringComparison.Ordinal))
                {
                    return IngestOutcome.DuplicateInternetMessageId;
                }

                outcome = IngestOutcome.InternetMessageIdCollisionIngested;
            }
            else
            {
                seen[message.InternetMessageId] = fingerprint;
            }
        }

        ids.Add(message.ImmutableId);
        return outcome;
    }
}

internal sealed record SpikeNotification(string SubscriptionId, string ChangeType, string MessageId, string? ClientState);

internal sealed record Subscription(string MailboxId, string ClientState);

internal enum NotificationValidity { Valid, UnknownSubscription, ClientStateMismatch }

internal sealed record FetchTarget(string MailboxId, string MessageId);

internal enum LifecycleAction
{
    /// <summary>PATCH expirationDateTime (renews and reauthorizes in one call).</summary>
    RenewSubscription,
    /// <summary>Create a new subscription, then reconcile via delta.</summary>
    RecreateSubscriptionAndReconcile,
    /// <summary>Run a delta reconciliation for the mailbox.</summary>
    ReconcileViaDelta,
    Ignore,
}

/// <summary>
/// Webhook payloads are only a nudge. This turns a notification POST body into a de-duplicated set of
/// (mailbox, message id) fetch targets. The mailbox is derived from OUR subscription record, never from the payload.
/// Order does not matter: created/updated/duplicates for one message collapse to one target, and the fetch + idempotent
/// ingest is what establishes state.
/// </summary>
internal sealed class NotificationProcessor
{
    private readonly Dictionary<string, Subscription> _subscriptions = new(StringComparer.Ordinal);
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

    public void Register(string subscriptionId, string mailboxId, string clientState) =>
        _subscriptions[subscriptionId] = new Subscription(mailboxId, clientState);

    public static List<SpikeNotification> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var list = new List<SpikeNotification>();
        foreach (var item in doc.RootElement.GetProperty("value").EnumerateArray())
        {
            string? messageId = null;
            if (item.TryGetProperty("resourceData", out var data))
            {
                messageId = GraphPayloadParser.Str(data, "id");
            }

            list.Add(new SpikeNotification(
                GraphPayloadParser.Str(item, "subscriptionId") ?? string.Empty,
                GraphPayloadParser.Str(item, "changeType") ?? string.Empty,
                messageId ?? string.Empty,
                GraphPayloadParser.Str(item, "clientState")));
        }

        return list;
    }

    public NotificationValidity Validate(SpikeNotification notification)
    {
        if (!_subscriptions.TryGetValue(notification.SubscriptionId, out var subscription))
        {
            return NotificationValidity.UnknownSubscription;
        }

        var expected = Encoding.UTF8.GetBytes(subscription.ClientState);
        var actual = Encoding.UTF8.GetBytes(notification.ClientState ?? string.Empty);
        return CryptographicOperations.FixedTimeEquals(expected, actual)
            ? NotificationValidity.Valid
            : NotificationValidity.ClientStateMismatch;
    }

    /// <summary>Returns fetch targets; invalid notifications are dropped (caller logs without payload bodies).</summary>
    public List<FetchTarget> Process(string json)
    {
        var targets = new List<FetchTarget>();
        var batch = new HashSet<string>(StringComparer.Ordinal);
        foreach (var notification in Parse(json))
        {
            if (Validate(notification) != NotificationValidity.Valid || notification.MessageId.Length == 0)
            {
                continue;
            }

            // Redelivery of the same notification (at-least-once) is dropped here ...
            var dedupeKey = string.Join('|', notification.SubscriptionId, notification.ChangeType, notification.MessageId);
            if (!_seen.Add(dedupeKey))
            {
                continue;
            }

            // ... and created/updated for the same message collapse into one fetch within a batch.
            var mailbox = _subscriptions[notification.SubscriptionId].MailboxId;
            if (batch.Add(mailbox + "|" + notification.MessageId))
            {
                targets.Add(new FetchTarget(mailbox, notification.MessageId));
            }
        }

        return targets;
    }

    public List<(string SubscriptionId, LifecycleAction Action)> ProcessLifecycle(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var list = new List<(string, LifecycleAction)>();
        foreach (var item in doc.RootElement.GetProperty("value").EnumerateArray())
        {
            var subscriptionId = GraphPayloadParser.Str(item, "subscriptionId") ?? string.Empty;
            if (!_subscriptions.TryGetValue(subscriptionId, out var subscription))
            {
                continue;
            }

            var supplied = Encoding.UTF8.GetBytes(GraphPayloadParser.Str(item, "clientState") ?? string.Empty);
            if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(subscription.ClientState), supplied))
            {
                continue;
            }

            var action = GraphPayloadParser.Str(item, "lifecycleEvent") switch
            {
                "reauthorizationRequired" => LifecycleAction.RenewSubscription,
                "subscriptionRemoved" => LifecycleAction.RecreateSubscriptionAndReconcile,
                "missed" => LifecycleAction.ReconcileViaDelta,
                _ => LifecycleAction.Ignore,
            };
            list.Add((subscriptionId, action));
        }

        return list;
    }
}

/// <summary>Loop / noise prevention hints. Returns a reason string when the message must not create or update a ticket.</summary>
internal static class MailLoopGuard
{
    private static readonly string[] BulkPrecedence = ["bulk", "list", "junk"];
    private static readonly string[] BounceSenders = ["mailer-daemon", "postmaster"];

    public static string? Evaluate(SpikeMessage message, MailboxRegistry registry)
    {
        if (registry.IsOwnAddress(message.FromAddress))
        {
            return "own-mailbox-sender";
        }

        if (message.Headers.TryGetValue("Auto-Submitted", out var auto)
            && !string.Equals(auto.Trim(), "no", StringComparison.OrdinalIgnoreCase))
        {
            return "auto-submitted";
        }

        if (message.Headers.TryGetValue("Precedence", out var precedence)
            && BulkPrecedence.Contains(precedence.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            return "precedence";
        }

        if (message.Headers.ContainsKey("X-Auto-Response-Suppress"))
        {
            return "auto-response-suppress";
        }

        if (message.Headers.ContainsKey("List-Id") || message.Headers.ContainsKey("List-Unsubscribe"))
        {
            return "mailing-list";
        }

        if (message.FromAddress is not null)
        {
            var local = message.FromAddress.Split('@')[0];
            if (BounceSenders.Contains(local, StringComparer.OrdinalIgnoreCase))
            {
                return "bounce-sender";
            }
        }

        if (message.Headers.TryGetValue("Content-Type", out var contentType)
            && contentType.Contains("report-type=delivery-status", StringComparison.OrdinalIgnoreCase))
        {
            return "delivery-status-report";
        }

        return null;
    }
}
