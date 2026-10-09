namespace Helpdesk.Modules.Attachments.Services;

/// <summary>Bound from the <c>Attachments</c> configuration section.</summary>
internal sealed class AttachmentOptions
{
    public const string Section = "Attachments";

    /// <summary>Largest accepted attachment; bigger ones are recorded as blocked (too_large).</summary>
    public long MaxBytes { get; set; } = 25 * 1024 * 1024;

    /// <summary>
    /// When true an attachment is downloadable only after a scanner reported it Clean. The default is true (D-08: scanning is
    /// required before production release); Development/Testing configuration sets it to false so the flow works without an engine.
    /// </summary>
    public bool RequireCleanScan { get; set; } = true;

    /// <summary>
    /// Master switch. When false no attachment is stored, fetched or opened: uploads and inbound email attachments are recorded as Blocked (<c>attachments_disabled</c>, metadata only)
    /// and downloads are refused. Production must keep it false until a malware scanning engine is integrated (D-08); the host refuses to start otherwise.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Largest pasted (inline) image. Kept at 3 MB: Microsoft Graph accepts inline file attachments of that size in one request, so a stored image is always sendable.</summary>
    public long MaxInlineImageBytes { get; set; } = 3 * 1024 * 1024;
}

/// <summary>Bound from the <c>Storage</c> configuration section.</summary>
internal sealed class StorageOptions
{
    public const string Section = "Storage";

    /// <summary><c>InMemory</c> (tests, throw-away dev) or <c>S3</c> (any S3-compatible endpoint).</summary>
    public string Provider { get; set; } = "InMemory";

    public S3Options S3 { get; set; } = new();
}

internal sealed class S3Options
{
    public string? Endpoint { get; set; }

    public string Bucket { get; set; } = "helpdesk";

    public string Region { get; set; } = "us-east-1";

    public string? AccessKey { get; set; }

    public string? SecretKey { get; set; }

    /// <summary>Path-style addressing (needed by SeaweedFS/MinIO-style endpoints).</summary>
    public bool ForcePathStyle { get; set; } = true;
}
