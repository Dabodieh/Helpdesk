using System.Security.Cryptography;
using Helpdesk.Modules.Attachments.Contracts;
using Helpdesk.Modules.Attachments.Domain;
using Helpdesk.Modules.Attachments.Persistence;
using Helpdesk.SharedKernel.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Helpdesk.Modules.Attachments.Services;

internal sealed class AttachmentStoreService(
    AttachmentsDbContext db,
    IObjectStore objectStore,
    IAttachmentScanner scanner,
    IOptions<AttachmentOptions> options,
    TimeProvider clock) : IAttachmentStore
{
    public const string AttachmentsDisabled = "attachments_disabled";

    public bool Enabled => options.Value.Enabled;

    public async Task<AttachmentSummary> StoreAsync(StoreAttachmentRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var settings = options.Value;

        var existing = await db.Attachments.AsNoTracking().SingleOrDefaultAsync(
            a => a.DepartmentId == request.DepartmentId && a.MessageId == request.MessageId && a.SourceRef == request.SourceRef, cancellationToken);
        if (existing is not null)
        {
            return ToSummary(existing, settings);
        }

        var oversize = request.DeclaredSize is { } declared && declared > settings.MaxBytes;
        var validation = !settings.Enabled
            ? new ValidationResult(false, AttachmentValidator.SanitiseFileName(request.FileName), "application/octet-stream", AttachmentsDisabled)
            : oversize
            ? new ValidationResult(false, AttachmentValidator.SanitiseFileName(request.FileName), "application/octet-stream", AttachmentValidator.TooLarge)
            : AttachmentValidator.Validate(request.FileName, request.Content, settings.MaxBytes);
        var id = Guid.CreateVersion7();
        var attachment = new Attachment
        {
            Id = id,
            DepartmentId = request.DepartmentId,
            TicketId = request.TicketId,
            MessageId = request.MessageId,
            FileName = validation.FileName,
            ContentType = validation.ContentType,
            SizeBytes = oversize ? request.DeclaredSize!.Value : request.Content.LongLength,
            Sha256 = SHA256.HashData(request.Content),
            IsInline = request.IsInline,
            ContentId = request.ContentId is { Length: <= 255 } cid ? cid : null,
            SourceRef = request.SourceRef,
            CreatedAt = clock.GetUtcNowMicroseconds(),
        };

        if (validation.Accepted)
        {
            var verdict = await scanner.ScanAsync(request.Content, validation.FileName, cancellationToken);
            if (verdict == ScanVerdict.Infected)
            {
                attachment.State = AttachmentStates.Blocked;
                attachment.BlockReason = "infected";
                attachment.ScanState = AttachmentScanStates.Infected;
            }
            else
            {
                attachment.State = AttachmentStates.Stored;
                attachment.StorageKey = $"{request.DepartmentId:N}/{request.TicketId:N}/{id:N}";
                attachment.ScanState = verdict == ScanVerdict.Clean ? AttachmentScanStates.Clean : AttachmentScanStates.Skipped;
                await objectStore.PutAsync(attachment.StorageKey, request.Content, validation.ContentType, cancellationToken);
            }
        }
        else
        {
            attachment.State = AttachmentStates.Blocked;
            attachment.BlockReason = validation.BlockReason;
            attachment.ScanState = AttachmentScanStates.NotScanned;
        }

        db.Attachments.Add(attachment);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (PostgresErrors.IsUniqueViolation(ex, out _))
        {
            // A concurrent caller stored the same source reference first: return that one (the orphaned object is harmless).
            db.Entry(attachment).State = EntityState.Detached;
            var winner = await db.Attachments.AsNoTracking().SingleAsync(
                a => a.DepartmentId == request.DepartmentId && a.MessageId == request.MessageId && a.SourceRef == request.SourceRef, cancellationToken);
            return ToSummary(winner, settings);
        }

        return ToSummary(attachment, settings);
    }

    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<AttachmentSummary>>> ListForMessagesAsync(
        Guid departmentId, IReadOnlyCollection<Guid> messageIds, CancellationToken cancellationToken = default)
    {
        if (messageIds.Count == 0)
        {
            return new Dictionary<Guid, IReadOnlyList<AttachmentSummary>>();
        }

        var settings = options.Value;
        var rows = await db.Attachments.AsNoTracking()
            .Where(a => a.DepartmentId == departmentId && messageIds.Contains(a.MessageId))
            .OrderBy(a => a.CreatedAt).ThenBy(a => a.Id)
            .ToListAsync(cancellationToken);
        return rows.GroupBy(a => a.MessageId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<AttachmentSummary>)g.Select(a => ToSummary(a, settings)).ToList());
    }

    public async Task<AttachmentOpenResult> OpenAsync(Guid departmentId, Guid ticketId, Guid attachmentId, CancellationToken cancellationToken = default)
    {
        var attachment = await db.Attachments.AsNoTracking()
            .SingleOrDefaultAsync(a => a.Id == attachmentId && a.DepartmentId == departmentId && a.TicketId == ticketId, cancellationToken);
        if (attachment is null)
        {
            return AttachmentOpenResult.Missing;
        }

        var settings = options.Value;
        if (!settings.Enabled)
        {
            return new AttachmentOpenResult(null, false, AttachmentsDisabled);
        }

        if (!IsDownloadable(attachment, settings))
        {
            return new AttachmentOpenResult(null, false, attachment.State == AttachmentStates.Blocked ? attachment.BlockReason : "scan_required");
        }

        var stream = await objectStore.OpenReadAsync(attachment.StorageKey!, cancellationToken);
        return stream is null
            ? AttachmentOpenResult.Missing
            : new AttachmentOpenResult(new AttachmentDownload(attachment.FileName, attachment.ContentType, attachment.SizeBytes, stream), false, null);
    }

    private static bool IsDownloadable(Attachment a, AttachmentOptions settings) =>
        settings.Enabled
        && a.State == AttachmentStates.Stored
        && a.ScanState != AttachmentScanStates.Infected
        && (!settings.RequireCleanScan || a.ScanState == AttachmentScanStates.Clean);

    private static AttachmentSummary ToSummary(Attachment a, AttachmentOptions settings) =>
        new(a.Id, a.MessageId, a.FileName, a.ContentType, a.SizeBytes, a.IsInline, a.State, a.BlockReason, a.ScanState, IsDownloadable(a, settings));
}
