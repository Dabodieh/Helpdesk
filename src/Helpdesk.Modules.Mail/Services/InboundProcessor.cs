using System.Text.Json;
using Helpdesk.Modules.Attachments.Contracts;
using Helpdesk.Modules.Mail.Contracts;
using Helpdesk.Modules.Mail.Domain;
using Helpdesk.Modules.Mail.Persistence;
using Helpdesk.Modules.Mail.Providers;
using Helpdesk.Modules.Organisation.Contracts;
using Helpdesk.SharedKernel.Database;
using Helpdesk.SharedKernel.Errors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Helpdesk.Modules.Mail.Services;

/// <summary>
/// The inbound pipeline for ONE message (EMAIL_ARCHITECTURE.md): persist raw (idempotency boundary), guard against loops and noise,
/// resolve the department from the RECEIVING mailbox, correlate, hand over to Ticketing (idempotent on the mail message id), store
/// attachments, complete. Every step is safe to repeat, so any crash or retry converges without duplicating tickets, messages or
/// attachments. Message content is never logged.
/// </summary>
internal sealed class InboundProcessor(
    MailDbContext db,
    IMailProvider provider,
    CorrelationEngine correlation,
    IInboundTicketSink sink,
    IAttachmentStore attachments,
    IOrganisationDirectory organisation,
    MailAudit audit,
    IOptions<MailOptions> options,
    TimeProvider clock,
    ILogger<InboundProcessor> log)
{
    public const int MaxAttempts = 5;
    private const int MaxSanitisedHtml = 400_000;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly string[] KeptHeaders =
        ["In-Reply-To", "References", "Auto-Submitted", "Precedence", "X-Auto-Response-Suppress", "List-Id", "List-Unsubscribe", "Content-Type", "x-helpdesk-token"];

    /// <summary>
    /// Processes (or resumes) one provider message. A failure fetching the message propagates (the sync run is retried as a whole);
    /// a failure after the row exists is recorded on it (state Failed, retried later) and does not stop the batch.
    /// </summary>
    public async Task ProcessAsync(Guid mailboxId, string providerMessageId, CancellationToken ct)
    {
        var mailbox = await db.Mailboxes.AsNoTracking().SingleOrDefaultAsync(m => m.Id == mailboxId, ct);
        if (mailbox is not { IsActive: true })
        {
            return;
        }

        var mref = new MailboxRef(mailbox.Id, mailbox.Address, mailbox.ProviderRef ?? mailbox.Address);
        var message = await db.Messages.SingleOrDefaultAsync(m => m.MailboxId == mailboxId && m.ProviderMessageId == providerMessageId, ct);
        if (message is { State: MessageStates.Completed or MessageStates.Ignored })
        {
            return;
        }

        InboundEmail? email = null;
        if (message is null)
        {
            email = await provider.GetMessageAsync(mref, providerMessageId, ct);
            if (email is null)
            {
                return; // deleted or moved away before we read it
            }

            message = await RecordAsync(mailbox, email, ct);
            if (message.State == MessageStates.Ignored)
            {
                return;
            }
        }

        try
        {
            email ??= await provider.GetMessageAsync(mref, providerMessageId, ct) ?? throw new InvalidOperationException("message_gone");
            await AdvanceAsync(mailbox, mref, message, email, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            await MarkFailedAsync(message, ex, ct);
        }
    }

    /// <summary>Admin retry: lets a Failed message try again from the start.</summary>
    public async Task<bool> RequeueAsync(Guid departmentId, Guid mailMessageId, CancellationToken ct)
    {
        var message = await db.Messages.SingleOrDefaultAsync(
            m => m.Id == mailMessageId && m.DepartmentId == departmentId && m.Direction == MessageDirections.Inbound && m.State == MessageStates.Failed, ct);
        if (message is null)
        {
            return false;
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        message.Attempts = 0;
        message.NextAttemptAt = clock.GetUtcNowMicroseconds();
        message.UpdatedAt = message.NextAttemptAt.Value;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(MailAuditActions.Retried, MailAudit.MessageObject, message.Id, departmentId, null, null, ct);
        await tx.CommitAsync(ct);
        return true;
    }

    // ----- step 1: persist raw + guard -----

    private async Task<MailMessage> RecordAsync(Mailbox mailbox, InboundEmail email, CancellationToken ct)
    {
        var now = clock.GetUtcNowMicroseconds();
        var internetId = MailText.NormaliseMessageId(email.InternetMessageId);
        var from = email.From?.Address ?? "";
        var message = new MailMessage
        {
            Id = Guid.CreateVersion7(),
            MailboxId = mailbox.Id,
            DepartmentId = mailbox.DepartmentId, // the ONLY source of the department
            Direction = MessageDirections.Inbound,
            State = MessageStates.Received,
            ProviderMessageId = email.ProviderMessageId,
            InternetMessageId = internetId,
            ConversationId = email.ConversationId,
            InReplyTo = email.Headers.TryGetValue("In-Reply-To", out var irt) ? string.Join(' ', MailText.ParseMessageIds(irt)) : null,
            References = email.Headers.TryGetValue("References", out var refs) ? string.Join(' ', MailText.ParseMessageIds(refs)) : null,
            Subject = (email.Subject ?? "").Length > 998 ? email.Subject![..998] : email.Subject ?? "",
            FromAddress = from.Length > 320 ? from[..320] : from,
            FromName = email.From?.Name is { Length: > 200 } n ? n[..200] : email.From?.Name,
            ToJson = JsonSerializer.Serialize(email.To, Json),
            CcJson = JsonSerializer.Serialize(email.Cc, Json),
            OccurredAt = email.ReceivedAt.ToUniversalTime(),
            BodyText = MailText.BodyTextOf(email.BodyText, email.BodyHtml),
            BodyHtmlOriginal = email.BodyHtml,
            HeadersJson = JsonSerializer.Serialize(
                email.Headers.Where(h => KeptHeaders.Contains(h.Key, StringComparer.OrdinalIgnoreCase)).ToDictionary(h => h.Key, h => h.Value.Length > 500 ? h.Value[..500] : h.Value), Json),
            CreatedAt = now,
            UpdatedAt = now,
        };

        var helpdeskAddresses = (await db.Mailboxes.AsNoTracking().Select(m => m.AddressNormalised).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
        var reason = MailGuard.Evaluate(email, helpdeskAddresses);
        if (reason is null && message.FromAddress.Length > 0)
        {
            var since = now - TimeSpan.FromMinutes(options.Value.SenderWindowMinutes);
            var recent = await db.Messages.CountAsync(
                m => m.MailboxId == mailbox.Id && m.Direction == MessageDirections.Inbound && m.FromAddress == message.FromAddress && m.CreatedAt >= since, ct);
            if (recent >= options.Value.SenderLimitPerWindow)
            {
                reason = IgnoreReasons.SenderRateLimited;
            }
        }

        if (reason is null && internetId is not null)
        {
            var sameId = await db.Messages.AsNoTracking()
                .Where(m => m.MailboxId == mailbox.Id && m.InternetMessageId == internetId && m.Direction == MessageDirections.Inbound)
                .Select(m => new { m.Subject, m.FromAddress, m.OccurredAt }).FirstOrDefaultAsync(ct);
            if (sameId is not null)
            {
                var sameContent = sameId.Subject == message.Subject && sameId.FromAddress == message.FromAddress
                    && Math.Abs((sameId.OccurredAt - message.OccurredAt).TotalSeconds) < 2;
                if (sameContent)
                {
                    reason = IgnoreReasons.DuplicateInternetMessageId;
                }
                else
                {
                    // A sender re-used one Message-ID for different mail: ingest it (never drop) but flag it.
                    message.Flags = "internet_id_collision";
                }
            }
        }

        if (reason is not null)
        {
            message.State = MessageStates.Ignored;
            message.IgnoreReason = reason;
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.Messages.Add(message);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (PostgresErrors.IsUniqueViolation(ex, out _))
        {
            // Lost a race with a concurrent run of the same message: continue with the winner's row.
            db.Entry(message).State = EntityState.Detached;
            await tx.RollbackAsync(ct);
            return await db.Messages.SingleAsync(m => m.MailboxId == mailbox.Id && m.ProviderMessageId == email.ProviderMessageId, ct);
        }

        if (reason is not null)
        {
            await audit.WriteSystemAsync(MailAuditActions.Ignored, MailAudit.MessageObject, message.Id, mailbox.DepartmentId, null, new { reason }, ct);
            if (MailGuard.LooksLikeNdr(email))
            {
                await CorrelateNdrAsync(mailbox, message, ct);
            }
        }

        await tx.CommitAsync(ct);
        return message;
    }

    /// <summary>
    /// A suppressed non-delivery report may prove an earlier outbound message was NOT delivered. Matched only by message id or our own token within this
    /// mailbox; the report itself stays ignored (no ticket, no requester reply). Unmatched reports are logged by id only.
    /// </summary>
    private async Task CorrelateNdrAsync(Mailbox mailbox, MailMessage ndr, CancellationToken ct)
    {
        var match = await NdrCorrelation.FindAsync(db, ndr, ct);
        if (match is null)
        {
            log.NdrUncorrelated(mailbox.Id);
            return;
        }

        var outbound = match.Outbound;
        outbound.State = MessageStates.Failed;
        outbound.LastError = MailErrorCodes.NdrDeliveryFailed;
        outbound.NextAttemptAt = null;
        outbound.DeliveryReportedAt = null; // the dispatcher reports it to the ticket
        outbound.UpdatedAt = clock.GetUtcNowMicroseconds();
        ndr.Flags = MessageFlags.With(ndr.Flags, "ndr_correlated");
        await db.SaveChangesAsync(ct);
        await audit.WriteSystemAsync(MailAuditActions.DeliveryFailedReported, MailAudit.MessageObject, outbound.Id, outbound.DepartmentId, null, new { via = match.Evidence, ndrId = ndr.Id }, ct);
        log.NdrCorrelated(mailbox.Id, outbound.Id, match.Evidence);
    }

    // ----- steps 2-5: correlate, ticket, attachments -----

    private async Task AdvanceAsync(Mailbox mailbox, MailboxRef mref, MailMessage message, InboundEmail email, CancellationToken ct)
    {
        if (message.State is MessageStates.Received or MessageStates.Failed && message.TicketMessageId is null)
        {
            await TicketAsync(mailbox, message, email, ct);
        }

        if (message.State == MessageStates.Ticketed || (message.State == MessageStates.Failed && message.TicketMessageId is not null))
        {
            await StoreAttachmentsAsync(mref, message, email, ct);
            if (email.Attachments.Count > 0)
            {
                await sink.NotifyTicketChangedAsync(message.DepartmentId, message.TicketId!.Value, "attachment", ct);
            }

            message.State = MessageStates.Completed;
            message.LastError = null;
            message.NextAttemptAt = null;
            message.UpdatedAt = clock.GetUtcNowMicroseconds();
            await db.SaveChangesAsync(ct);
        }
    }

    private async Task TicketAsync(Mailbox mailbox, MailMessage message, InboundEmail email, CancellationToken ct)
    {
        var department = await organisation.FindDepartmentAsync(mailbox.DepartmentId, ct) ?? throw new InvalidOperationException("department_missing");
        var found = await correlation.CorrelateAsync(message, email, department.Key, ct);
        var sourceRef = message.Id.ToString("N");
        var from = new MailParticipant(message.FromAddress, message.FromName);
        var html = email.BodyHtml is { Length: > 0 and <= MaxSanitisedHtml } raw ? MailText.SanitiseHtml(raw) : null;
        var cc = Participants(email, mailbox, message.FromAddress);

        InboundTicketResult? result = null;
        if (found.TicketId is { } ticketId)
        {
            result = await sink.AddMessageAsync(
                new InboundMessageRequest(mailbox.DepartmentId, ticketId, sourceRef, from, cc, message.BodyText!, html, message.OccurredAt), ct);
        }

        var method = found.Method;
        if (result is null)
        {
            // No strong evidence (or the evidence no longer resolves in this department): a NEW ticket. A subject reference of the
            // receiving department only becomes a suggestion for the agent (system note), and is never attached on its own.
            method = CorrelationMethod.New;
            string? note = null;
            if (found.SubjectHint is { } hint)
            {
                var candidate = await sink.FindByReferenceAsync(mailbox.DepartmentId, hint, ct);
                if (candidate is not null)
                {
                    note = $"The subject of this email mentions {candidate.Reference}, but it was not attached automatically (no reply headers or correlation token matched). Check whether it belongs to that ticket.";
                    message.SubjectHintReference = candidate.Reference;
                }
            }

            result = await sink.CreateTicketAsync(
                new InboundTicketRequest(
                    mailbox.DepartmentId, mailbox.Id, sourceRef, from, cc, MailText.TicketSubject(message.Subject), message.BodyText!, html, message.OccurredAt, note), ct);
        }

        var flags = new List<string>();
        if (!string.IsNullOrEmpty(message.Flags))
        {
            flags.Add(message.Flags);
        }

        if (found.CrossDepartmentIgnored)
        {
            flags.Add("cross_department_ignored");
            log.CrossDepartmentDiscarded(message.Id);
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        message.TicketId = result.TicketId;
        message.TicketMessageId = result.MessageId;
        message.CorrelationMethod = method.ToString();
        message.Flags = flags.Count == 0 ? null : string.Join(',', flags);
        message.State = MessageStates.Ticketed;
        message.UpdatedAt = clock.GetUtcNowMicroseconds();
        await db.SaveChangesAsync(ct);
        await audit.WriteSystemAsync(
            result.Created ? MailAuditActions.TicketCreated : MailAuditActions.Attached, MailAudit.MessageObject, message.Id, mailbox.DepartmentId, null,
            new { ticketId = result.TicketId, reference = result.Reference, method = method.ToString(), reopened = result.Reopened }, ct);
        await tx.CommitAsync(ct);
    }

    private async Task StoreAttachmentsAsync(MailboxRef mref, MailMessage message, InboundEmail email, CancellationToken ct)
    {
        foreach (var info in email.Attachments)
        {
            var bytes = info.Size > 0 && info.Size <= 50L * 1024 * 1024
                ? await provider.GetAttachmentAsync(mref, email.ProviderMessageId, info.ProviderAttachmentId, ct)
                : [];
            await attachments.StoreAsync(
                new StoreAttachmentRequest(
                    message.DepartmentId, message.TicketId!.Value, message.TicketMessageId!.Value, info.FileName, info.ContentType, bytes, info.IsInline,
                    info.ContentId, info.ProviderAttachmentId, info.Size), ct);
        }
    }

    private static IReadOnlyList<MailParticipant> Participants(InboundEmail email, Mailbox mailbox, string from)
    {
        var skip = new HashSet<string>(StringComparer.Ordinal) { MailText.NormaliseAddress(from), mailbox.AddressNormalised };
        return [.. email.To.Concat(email.Cc)
            .Where(a => !string.IsNullOrWhiteSpace(a.Address) && skip.Add(MailText.NormaliseAddress(a.Address)))
            .Select(a => new MailParticipant(a.Address, a.Name))];
    }

    private async Task MarkFailedAsync(MailMessage message, Exception ex, CancellationToken ct)
    {
        // Content is never logged: only the exception type/code.
        var code = MailText.ShortError(ex);
        log.InboundFailed(message.Id, code);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var tracked = await db.Messages.SingleAsync(m => m.Id == message.Id, ct);
        tracked.Attempts++;
        tracked.State = MessageStates.Failed;
        tracked.LastError = code;
        tracked.NextAttemptAt = tracked.Attempts >= MaxAttempts
            ? null
            : clock.GetUtcNowMicroseconds() + TimeSpan.FromMinutes(Math.Pow(2, tracked.Attempts));
        tracked.UpdatedAt = clock.GetUtcNowMicroseconds();
        await db.SaveChangesAsync(ct);
        await audit.WriteSystemAsync(MailAuditActions.Failed, MailAudit.MessageObject, tracked.Id, tracked.DepartmentId, null, new { attempts = tracked.Attempts, error = code }, ct);
        await tx.CommitAsync(ct);
    }
}
