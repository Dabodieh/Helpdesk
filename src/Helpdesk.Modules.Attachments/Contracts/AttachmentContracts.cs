namespace Helpdesk.Modules.Attachments.Contracts;

public static class AttachmentStates
{
    /// <summary>Bytes are in the object store.</summary>
    public const string Stored = "Stored";

    /// <summary>Rejected by validation; metadata only, no bytes were stored.</summary>
    public const string Blocked = "Blocked";
}

public static class AttachmentScanStates
{
    public const string NotScanned = "NotScanned";
    public const string Clean = "Clean";
    public const string Infected = "Infected";

    /// <summary>No scanner is configured (D-08 undecided); never counts as clean when scanning is required.</summary>
    public const string Skipped = "Skipped";
}

/// <param name="DeclaredSize">Size announced by the sender/provider; when above the limit the attachment is recorded as blocked without needing the bytes (pass empty content).</param>
/// <param name="SourceRef">Idempotency key within (department, message), e.g. the provider attachment id. Storing twice returns the first result.</param>
public sealed record StoreAttachmentRequest(
    Guid DepartmentId,
    Guid TicketId,
    Guid MessageId,
    string FileName,
    string? ContentType,
    byte[] Content,
    bool IsInline,
    string? ContentId,
    string SourceRef,
    long? DeclaredSize = null);

public sealed record AttachmentSummary(
    Guid Id,
    Guid MessageId,
    string FileName,
    string ContentType,
    long Size,
    bool IsInline,
    string State,
    string? BlockReason,
    string ScanState,
    bool Downloadable);

public sealed record AttachmentDownload(string FileName, string ContentType, long Size, Stream Content);

/// <summary>Outcome of <see cref="IAttachmentStore.OpenAsync"/>: <see cref="NotFound"/> and <see cref="Blocked"/> carry no stream.</summary>
public sealed record AttachmentOpenResult(AttachmentDownload? Download, bool NotFound, string? BlockedReason)
{
    public static AttachmentOpenResult Missing { get; } = new(null, true, null);
}

/// <summary>
/// Department-scoped attachment storage for other modules (no table access across modules). Every method takes the department
/// explicitly and filters by it; callers authorise access to the ticket before calling.
/// </summary>
public interface IAttachmentStore
{
    /// <summary>Validates (size, type, extension, magic bytes), hashes, stores and records the attachment. Idempotent on <c>SourceRef</c>.</summary>
    Task<AttachmentSummary> StoreAsync(StoreAttachmentRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<Guid, IReadOnlyList<AttachmentSummary>>> ListForMessagesAsync(
        Guid departmentId, IReadOnlyCollection<Guid> messageIds, CancellationToken cancellationToken = default);

    /// <summary>Opens the attachment of this ticket for download; blocked or (when scanning is required) unscanned items are refused.</summary>
    Task<AttachmentOpenResult> OpenAsync(Guid departmentId, Guid ticketId, Guid attachmentId, CancellationToken cancellationToken = default);
}

/// <summary>S3-compatible object storage seam (SeaweedFS locally; production provider is deployment-configurable, D-17).</summary>
public interface IObjectStore
{
    Task PutAsync(string key, byte[] content, string contentType, CancellationToken cancellationToken = default);

    Task<Stream?> OpenReadAsync(string key, CancellationToken cancellationToken = default);
}

public enum ScanVerdict
{
    Clean,
    Infected,

    /// <summary>No scanning engine configured.</summary>
    Skipped,
}

/// <summary>Malware scanning seam (D-08: the engine is undecided; scanning is required before production attachment release).</summary>
public interface IAttachmentScanner
{
    Task<ScanVerdict> ScanAsync(byte[] content, string fileName, CancellationToken cancellationToken = default);
}
