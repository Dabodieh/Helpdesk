using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using Ganss.Xss;

namespace Helpdesk.Modules.Mail.Services;

/// <summary>Address, Message-ID, subject and body normalisation. Pure functions; nothing here logs content.</summary>
internal static partial class MailText
{
    public const int MaxTicketBody = 20000;
    public const int MaxSubject = 200;

    [GeneratedRegex(@"<([^<>\s]+)>")]
    private static partial Regex AngleIdPattern();

    [GeneratedRegex(@"\[hd:([A-Za-z0-9_-]{16,64})\]")]
    private static partial Regex TokenPattern();

    [GeneratedRegex(@"[\r\n]+")]
    private static partial Regex LineBreaks();

    [GeneratedRegex(@"(?im)^(?:(?:re|fw|fwd|aw|sv|vs)\s*:\s*)+")]
    private static partial Regex ReplyPrefix();

    [GeneratedRegex(@"[ \t]+\n")]
    private static partial Regex TrailingSpace();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex BlankRuns();

    private static readonly HtmlSanitizer Sanitizer = BuildSanitizer();

    private static HtmlSanitizer BuildSanitizer()
    {
        var sanitizer = new HtmlSanitizer();

        // Remote content is blocked by default: images may only be inline data or cid: references; links keep http(s)/mailto.
        sanitizer.AllowedSchemes.Clear();
        foreach (var scheme in new[] { "http", "https", "mailto", "cid", "data" })
        {
            sanitizer.AllowedSchemes.Add(scheme);
        }

        sanitizer.FilterUrl += (_, e) =>
        {
            if (e.Tag.LocalName == "img" && !(e.OriginalUrl.StartsWith("cid:", StringComparison.OrdinalIgnoreCase)
                    || e.OriginalUrl.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)))
            {
                e.SanitizedUrl = null;
            }

            // data: is for inline images only; a data: link (or any other element) is dropped (SEC-010).
            if (e.Tag.LocalName != "img" && e.OriginalUrl.TrimStart().StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                e.SanitizedUrl = null;
            }
        };
        return sanitizer;
    }

    /// <summary>Lower-case address without surrounding whitespace.</summary>
    public static string NormaliseAddress(string address) => address.Trim().ToLowerInvariant();

    /// <summary>Message-ID without angle brackets/whitespace, lower case (case differences and bracket styles must not break matching). Null when blank.</summary>
    public static string? NormaliseMessageId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var match = AngleIdPattern().Match(id);
        var core = match.Success ? match.Groups[1].Value : id.Trim().Trim('<', '>');
        core = core.Trim().ToLowerInvariant();
        return core.Length is 0 or > 998 ? null : core;
    }

    /// <summary>All Message-IDs of a header value (In-Reply-To / References), oldest first, normalised, de-duplicated.</summary>
    public static IReadOnlyList<string> ParseMessageIds(string? headerValue)
    {
        if (string.IsNullOrWhiteSpace(headerValue))
        {
            return [];
        }

        var ids = AngleIdPattern().Matches(headerValue).Select(m => NormaliseMessageId(m.Value)).OfType<string>().ToList();
        if (ids.Count == 0 && NormaliseMessageId(headerValue) is { } single)
        {
            ids.Add(single);
        }

        return [.. ids.Distinct(StringComparer.Ordinal)];
    }

    public static string? ExtractToken(string? text)
    {
        var match = string.IsNullOrEmpty(text) ? null : TokenPattern().Match(text);
        return match is { Success: true } ? match.Groups[1].Value : null;
    }

    /// <summary>Single-line subject of at most 200 characters (the ticket limit); never empty.</summary>
    public static string TicketSubject(string? subject)
    {
        var clean = LineBreaks().Replace(subject ?? "", " ").Trim();
        clean = ReplyPrefix().Replace(clean, "").Trim();
        if (clean.Length == 0)
        {
            return "(no subject)";
        }

        return clean.Length > MaxSubject ? clean[..MaxSubject].TrimEnd() : clean;
    }

    /// <summary>Allow-list sanitisation (scripts, frames, event handlers, remote images removed). The original stays immutable elsewhere.</summary>
    public static string SanitiseHtml(string html) => Sanitizer.Sanitize(html);

    /// <summary>Readable plain text from HTML (block elements become line breaks; scripts and styles are dropped).</summary>
    public static string HtmlToText(string html)
    {
        var document = new HtmlParser().ParseDocument(html);
        foreach (var node in document.QuerySelectorAll("script,style,head,title").ToList())
        {
            node.Remove();
        }

        foreach (var br in document.QuerySelectorAll("br").ToList())
        {
            br.Parent?.ReplaceChild(document.CreateTextNode("\n"), br);
        }

        foreach (var block in document.QuerySelectorAll("p,div,li,tr,h1,h2,h3,h4,h5,h6,blockquote,pre,table").ToList())
        {
            block.AppendChild(document.CreateTextNode("\n"));
        }

        return Tidy(document.Body?.TextContent ?? "");
    }

    /// <summary>Normalises line endings and blank runs, trims, and caps the length for a ticket message (original is kept in Mail).</summary>
    public static string Tidy(string text)
    {
        var t = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Replace(' ', ' ').Replace("\0", "", StringComparison.Ordinal);
        t = TrailingSpace().Replace(t, "\n");
        t = BlankRuns().Replace(t, "\n\n").Trim();
        return t;
    }

    public static string CapForTicket(string text)
    {
        if (text.Length <= MaxTicketBody)
        {
            return text;
        }

        const string marker = "\n\n[message truncated]";
        return text[..(MaxTicketBody - marker.Length)] + marker;
    }

    /// <summary>The plain-text body of a reply as HTML (escaped, line breaks kept) for sending.</summary>
    public static string TextToHtml(string text)
    {
        var encoded = System.Net.WebUtility.HtmlEncode(Tidy(text));
        return encoded.Replace("\n", "<br>\n", StringComparison.Ordinal);
    }

    private static readonly System.Text.RegularExpressions.Regex ImageMarker = new(
        @"\[image:([0-9a-fA-F]{8})-([0-9a-fA-F]{4})-([0-9a-fA-F]{4})-([0-9a-fA-F]{4})-([0-9a-fA-F]{12})\]", System.Text.RegularExpressions.RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    /// <summary>The Content-ID of a pasted image (its attachment id): what the HTML references as <c>cid:</c> and what the inline attachment carries.</summary>
    public static string ImageContentId(Guid attachmentId) => attachmentId.ToString("N") + "@helpdesk";

    /// <summary>Turns <c>[image:ID]</c> markers (already HTML-safe text) into inline image tags. Only a well-formed id can become a tag, so no other markup is introduced.</summary>
    public static string ImageMarkersToHtml(string html) =>
        ImageMarker.Replace(html, m => $"<img src=\"cid:{ImageContentId(Guid.Parse(m.Value[7..^1]))}\" alt=\"Pasted image\" style=\"max-width:100%;height:auto;display:block;margin:8px 0\">");

    public static string ImageMarkersToText(string text) => ImageMarker.Replace(text, "[image]");

    /// <summary>The pasted-image ids a stored message body refers to.</summary>
    public static IReadOnlyList<Guid> ImageIds(string text) => [.. ImageMarker.Matches(text).Select(m => Guid.Parse(m.Value[7..^1])).Distinct()];

    public static string BodyTextOf(string? text, string? html)
    {
        var basis = !string.IsNullOrWhiteSpace(text) ? Tidy(text) : !string.IsNullOrWhiteSpace(html) ? HtmlToText(html) : "";
        return basis.Length == 0 ? "(empty message)" : CapForTicket(basis);
    }

    public static string ShortError(Exception ex)
    {
        var name = ex.GetType().Name;
        var code = ex is Providers.TransientProviderException t ? t.Code : ex is Providers.PermanentProviderException p ? p.Code : null;
        var s = code is null ? name : $"{name}:{code}";
        return s.Length > 200 ? s[..200] : s;
    }

    /// <summary>
    /// <see cref="ShortError"/> plus, for a permanent provider failure, the provider's own error code and short message (Graph <c>error.code</c>/<c>error.message</c>),
    /// so an operator can tell a validation failure from a permission failure. Control characters are removed; never contains a token or message content.
    /// </summary>
    public static string ShortSubscriptionError(Exception ex)
    {
        var s = ShortError(ex);
        if (ex is Providers.PermanentProviderException { InnerException: InvalidOperationException inner } && !string.IsNullOrWhiteSpace(inner.Message))
        {
            var detail = new string([.. inner.Message.Where(c => !char.IsControl(c))]).Trim();
            s += ": " + (detail.Length > 120 ? detail[..120] : detail);
        }

        return s.Length > 200 ? s[..200] : s;
    }

    public static string FormatList(IEnumerable<string> items)
    {
        var sb = new StringBuilder();
        foreach (var item in items)
        {
            if (sb.Length > 0)
            {
                sb.Append(',');
            }

            sb.Append(item);
        }

        return sb.ToString();
    }
}
