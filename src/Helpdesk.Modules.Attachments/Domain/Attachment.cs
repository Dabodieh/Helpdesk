namespace Helpdesk.Modules.Attachments.Domain;

/// <summary>Metadata of one attachment; the bytes live in the object store under <see cref="StorageKey"/>.</summary>
internal sealed class Attachment
{
    public Guid Id { get; set; }

    public Guid DepartmentId { get; set; }

    public Guid TicketId { get; set; }

    public Guid MessageId { get; set; }

    /// <summary>Sanitised display name (never used as a storage path).</summary>
    public string FileName { get; set; } = "";

    public string ContentType { get; set; } = "";

    public long SizeBytes { get; set; }

    public byte[] Sha256 { get; set; } = [];

    /// <summary>Null when <see cref="State"/> is Blocked (nothing was stored).</summary>
    public string? StorageKey { get; set; }

    public string State { get; set; } = "";

    public string? BlockReason { get; set; }

    public string ScanState { get; set; } = "";

    public bool IsInline { get; set; }

    public string? ContentId { get; set; }

    public string SourceRef { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }
}
