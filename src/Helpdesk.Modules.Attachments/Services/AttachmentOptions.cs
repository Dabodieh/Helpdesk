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
