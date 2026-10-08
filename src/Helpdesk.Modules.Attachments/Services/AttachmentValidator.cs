namespace Helpdesk.Modules.Attachments.Services;

internal sealed record ValidationResult(bool Accepted, string FileName, string ContentType, string? BlockReason);

/// <summary>
/// Size, extension, content-type and magic-byte checks (SECURITY.md). Executable and script types are blocked outright; for
/// formats with a reliable signature the bytes must match the extension. The served content type comes from our own table (or
/// application/octet-stream), never from the sender's header.
/// </summary>
internal static class AttachmentValidator
{
    public const string TooLarge = "too_large";
    public const string BlockedType = "blocked_type";
    public const string ContentMismatch = "content_mismatch";
    public const string Empty = "empty";

    private static readonly HashSet<string> BlockedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "exe", "dll", "bat", "cmd", "com", "scr", "msi", "msp", "ps1", "psm1", "vbs", "vbe", "js", "jse", "wsf", "wsh", "jar",
        "lnk", "hta", "cpl", "reg", "sh", "apk", "app", "pif", "gadget", "inf",

        // D13 (until a malware scanner exists): macro-enabled Office files and add-ins, disk and container images, HTML/SVG, shortcuts and other launchers.
        // Legitimate macro-enabled documents may be allowed only after a clean scan, once a scanner exists.
        "docm", "dotm", "xlsm", "xltm", "xlam", "xll", "wll", "pptm", "potm", "ppam", "ppsm", "sldm", "one",
        "iso", "img", "vhd", "vhdx", "dmg",
        "html", "htm", "xhtml", "svg",
        "url", "scf", "library-ms", "settingcontent-ms", "chm",
        "msix", "msixbundle", "appx", "appxbundle", "appinstaller",
    };

    private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["pdf"] = "application/pdf",
        ["png"] = "image/png",
        ["jpg"] = "image/jpeg",
        ["jpeg"] = "image/jpeg",
        ["gif"] = "image/gif",
        ["txt"] = "text/plain",
        ["log"] = "text/plain",
        ["csv"] = "text/csv",
        ["zip"] = "application/zip",
        ["docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ["xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ["pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        ["doc"] = "application/msword",
        ["xls"] = "application/vnd.ms-excel",
        ["ppt"] = "application/vnd.ms-powerpoint",
        ["eml"] = "message/rfc822",
        ["msg"] = "application/vnd.ms-outlook",
    };

    private static readonly byte[] Zip = [0x50, 0x4B, 0x03, 0x04];
    private static readonly byte[] Ole = [0xD0, 0xCF, 0x11, 0xE0];

    private static readonly Dictionary<string, byte[][]> Signatures = new(StringComparer.OrdinalIgnoreCase)
    {
        ["pdf"] = [[0x25, 0x50, 0x44, 0x46]],
        ["png"] = [[0x89, 0x50, 0x4E, 0x47]],
        ["jpg"] = [[0xFF, 0xD8, 0xFF]],
        ["jpeg"] = [[0xFF, 0xD8, 0xFF]],
        ["gif"] = [[0x47, 0x49, 0x46, 0x38]],
        ["zip"] = [Zip, [0x50, 0x4B, 0x05, 0x06]],
        ["docx"] = [Zip],
        ["xlsx"] = [Zip],
        ["pptx"] = [Zip],
        ["doc"] = [Ole],
        ["xls"] = [Ole],
        ["ppt"] = [Ole],
        ["msg"] = [Ole],
    };

    public static ValidationResult Validate(string? fileName, byte[] content, long maxBytes)
    {
        var name = SanitiseFileName(fileName);
        var extension = ExtensionOf(name);
        var contentType = extension is not null && ContentTypes.TryGetValue(extension, out var known) ? known : "application/octet-stream";

        // Every dotted part is judged, so "invoice.exe.pdf" is blocked as well as "invoice.exe".
        if (name.Split('.').Skip(1).Any(part => BlockedExtensions.Contains(part)))
        {
            return new ValidationResult(false, name, "application/octet-stream", BlockedType);
        }

        if (content.LongLength == 0)
        {
            return new ValidationResult(false, name, contentType, Empty);
        }

        if (content.LongLength > maxBytes)
        {
            return new ValidationResult(false, name, contentType, TooLarge);
        }

        if (extension is not null && Signatures.TryGetValue(extension, out var signatures)
            && !signatures.Any(signature => content.AsSpan().StartsWith(signature)))
        {
            return new ValidationResult(false, name, "application/octet-stream", ContentMismatch);
        }

        return new ValidationResult(true, name, contentType, null);
    }

    /// <summary>Strips paths and control characters, trims, caps at 255 chars; never empty.</summary>
    public static string SanitiseFileName(string? fileName)
    {
        var name = fileName ?? "";
        var slash = name.LastIndexOfAny(['/', '\\']);
        if (slash >= 0)
        {
            name = name[(slash + 1)..];
        }

        name = new string([.. name.Where(c => !char.IsControl(c) && c is not ('"' or '<' or '>' or '|' or ':' or '*' or '?'))]).Trim().Trim('.');
        if (name.Length > 255)
        {
            var extension = ExtensionOf(name);
            name = extension is { Length: < 20 } ? name[..(255 - extension.Length - 1)] + "." + extension : name[..255];
        }

        return name.Length == 0 ? "attachment" : name;
    }

    private static string? ExtensionOf(string name)
    {
        var dot = name.LastIndexOf('.');
        return dot > 0 && dot < name.Length - 1 ? name[(dot + 1)..] : null;
    }
}
