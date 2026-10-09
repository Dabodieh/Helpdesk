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

/// <param name="UserId">The agent who pasted the image. Only that user can attach it to a message.</param>
public sealed record StoreInlineImageRequest(Guid DepartmentId, Guid TicketId, Guid UserId, byte[] Content);

/// <param name="Image">The stored image; null when it was refused, with <see cref="Reason"/> (attachments_disabled, empty, too_large, not_an_image, scan_failed).</param>
public sealed record InlineImageResult(AttachmentSummary? Image, string? Reason);

public static class InlineImageReasons
{
    public const string Disabled = "attachments_disabled";
    public const string Empty = "empty";
    public const string TooLarge = "too_large";
    public const string NotAnImage = "not_an_image";
    public const string Unavailable = "unavailable";
}

/// <summary>
/// Department-scoped attachment storage for other modules (no table access across modules). Every method takes the department
/// explicitly and filters by it; callers authorise access to the ticket before calling.
/// </summary>
public interface IAttachmentStore
{
    /// <summary>False when attachments are switched off: callers need not fetch or read bytes (they would not be stored).</summary>
    bool Enabled { get; }

    /// <summary>The largest pasted image accepted.</summary>
    long MaxInlineImageBytes { get; }

    /// <summary>
    /// Stores a pasted image as a pending upload of this ticket and user. The type comes from the bytes themselves (PNG, JPEG or GIF; never SVG or anything
    /// scriptable), never from a name or header the client sent. Goes through the same size, scan and storage pipeline as every attachment, so it cannot bypass the
    /// attachment switch or the clean-scan requirement.
    /// </summary>
    Task<InlineImageResult> StoreInlineImageAsync(StoreInlineImageRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Attaches pending pasted images of this user to a message. Returns the ids that could NOT be claimed (unknown, someone else's, already used, not stored or not
    /// clean); nothing is changed unless all of them can be.
    /// </summary>
    Task<IReadOnlyList<Guid>> ClaimInlineImagesAsync(Guid departmentId, Guid ticketId, Guid userId, IReadOnlyCollection<Guid> attachmentIds, Guid messageId, CancellationToken cancellationToken = default);

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
