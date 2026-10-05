using System.Text.Json;
using System.Text.RegularExpressions;

namespace Helpdesk.GraphSpike.Tests;

// Spike-local, test-only logic. NOT the Phase 3 mail subsystem: pure functions over parsed Graph payloads, no HTTP,
// no persistence. It exists to pin down the threading / idempotency / isolation rules from EMAIL_ARCHITECTURE.md
// ("Graph spike findings") so they can be re-used as acceptance criteria when the real module is built.

internal enum Direction { Inbound, Outbound }

internal sealed record SpikeMessage(
    string MailboxId,
    string ImmutableId,
    string? InternetMessageId,
    string? ConversationId,
    string Subject,
    string? FromAddress,
    string? SentRaw,
    string? InReplyTo,
    IReadOnlyList<string> References,
    IReadOnlyDictionary<string, string> Headers,
    string BodyText,
    Direction Direction);

internal static partial class Patterns
{
    [GeneratedRegex(@"<[^<>\s]+>", RegexOptions.CultureInvariant)]
    public static partial Regex MessageId();

    [GeneratedRegex(@"\[hd:([A-Za-z0-9]{16,64})\]", RegexOptions.CultureInvariant)]
    public static partial Regex BodyToken();

    [GeneratedRegex(@"\[([A-Z][A-Z0-9]{1,9}-[0-9]{1,9})\]", RegexOptions.CultureInvariant)]
    public static partial Regex SubjectKey();
}

internal static class Text
{
    /// <summary>Case-insensitive, null-safe equality. Used for addresses and RFC 5322 message ids.</summary>
    public static bool Eq(string? a, string? b) =>
        a is not null && b is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// internetMessageId / In-Reply-To / References normalisation: strip angle brackets and whitespace, keep the rest.
/// Comparison is case-insensitive (see Text.Eq): RFC 5322 treats the local part as case-sensitive in theory, but
/// real systems do not rely on that and some gateways change case; a collision between ids differing only in case is
/// negligible and the idempotency fallback additionally checks a content fingerprint.
/// </summary>
internal static class InternetMessageId
{
    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var match = Patterns.MessageId().Match(raw);
        var core = (match.Success ? match.Value : raw).Trim().Trim('<', '>').Trim();
        return core.Length == 0 ? null : core;
    }

    /// <summary>Parses a References-style header: whitespace separated &lt;id&gt; tokens, oldest first.</summary>
    public static List<string> ParseList(string? raw)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return result;
        }

        foreach (Match m in Patterns.MessageId().Matches(raw))
        {
            var id = Normalize(m.Value);
            if (id is not null)
            {
                result.Add(id);
            }
        }

        return result;
    }
}

internal sealed class MailboxRegistry
{
    private readonly Dictionary<string, string> _departments = new(StringComparer.OrdinalIgnoreCase);

    public MailboxRegistry Add(string mailboxAddress, string departmentId)
    {
        _departments[mailboxAddress] = departmentId;
        return this;
    }

    /// <summary>The department is derived from the receiving mailbox only, never from message content.</summary>
    public string DepartmentOf(string mailboxAddress) =>
        _departments.TryGetValue(mailboxAddress, out var department)
            ? department
            : throw new InvalidOperationException("Unknown mailbox: " + mailboxAddress);

    public bool IsOwnAddress(string? address) => address is not null && _departments.ContainsKey(address);
}

internal static class Fixtures
{
    public static string Read(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
}

internal static class GraphPayloadParser
{
    /// <summary>
    /// Parses a Graph v1.0 message resource (as returned with $select=...,internetMessageHeaders).
    /// Direction is Outbound when the sender is the mailbox itself (Sent Items copy), else Inbound.
    /// </summary>
    public static SpikeMessage ParseMessage(string json, string mailboxAddress)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("internetMessageHeaders", out var headerArray) && headerArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var header in headerArray.EnumerateArray())
            {
                var name = Str(header, "name");
                var value = Str(header, "value");
                if (name is not null && value is not null)
                {
                    headers.TryAdd(name, value);
                }
            }
        }

        string? from = null;
        if (root.TryGetProperty("from", out var fromElement) && fromElement.TryGetProperty("emailAddress", out var address))
        {
            from = Str(address, "address");
        }

        var body = string.Empty;
        if (root.TryGetProperty("body", out var bodyElement))
        {
            body = Str(bodyElement, "content") ?? string.Empty;
        }

        headers.TryGetValue("In-Reply-To", out var inReplyTo);
        headers.TryGetValue("References", out var references);

        return new SpikeMessage(
            MailboxId: mailboxAddress,
            ImmutableId: Str(root, "id") ?? throw new InvalidDataException("Message without id"),
            InternetMessageId: InternetMessageId.Normalize(Str(root, "internetMessageId")),
            ConversationId: Str(root, "conversationId"),
            Subject: Str(root, "subject") ?? string.Empty,
            FromAddress: from,
            SentRaw: Str(root, "sentDateTime"),
            InReplyTo: InternetMessageId.Normalize(inReplyTo),
            References: InternetMessageId.ParseList(references),
            Headers: headers,
            BodyText: body,
            Direction: Text.Eq(from, mailboxAddress) ? Direction.Outbound : Direction.Inbound);
    }

    public static string? Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
