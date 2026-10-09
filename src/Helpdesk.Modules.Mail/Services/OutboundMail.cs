using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Helpdesk.Modules.Audit.Contracts;
using Helpdesk.Modules.Mail.Contracts;
using Helpdesk.Modules.Mail.Domain;
using Helpdesk.Modules.Mail.Persistence;
using Helpdesk.Modules.Mail.Providers;
using Helpdesk.SharedKernel.Database;
using Helpdesk.SharedKernel.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace Helpdesk.Modules.Mail.Services;

/// <summary>
/// The outbox side used by Ticketing: the reply row is inserted on the caller's own connection and transaction, so the ticket
/// message and its outbound mail commit or roll back together (nothing is "sent" that the ticket does not show, and vice versa).
/// </summary>
internal sealed class MailOutbox(IMailJobScheduler scheduler, TimeProvider clock, IAuditWriter auditWriter, ICurrentUser current, Microsoft.Extensions.Options.IOptions<MailOptions> options) : IMailOutbox
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private const string InsertSql = """
        INSERT INTO mail.messages
            (id, mailbox_id, department_id, direction, state, subject, from_address, from_name, to_addresses, cc_addresses, occurred_at,
             body_text, body_html_original, headers, ticket_id, ticket_message_id, ticket_reference, outbound_token, reply_to_message_id, requested_by_user_id,
             attempts, next_attempt_at, created_at, updated_at)
        SELECT @id, mb.id, mb.department_id, 'Outbound', 'Queued', @subject, mb.address, mb.display_name, @to::jsonb, @cc::jsonb, @now,
               @body, @html, jsonb_build_object(), @ticket_id, @ticket_message_id, @reference, @token,
               (SELECT m.id FROM mail.messages m
                 WHERE m.mailbox_id = mb.id AND m.department_id = mb.department_id AND m.ticket_id = @ticket_id
                   AND m.direction = 'Inbound' AND m.provider_message_id IS NOT NULL AND m.state IN ('Ticketed','Completed')
                 ORDER BY m.occurred_at DESC, m.id DESC LIMIT 1),
               @actor, 0, @now, @now, @now
        FROM mail.mailboxes mb
        WHERE mb.id = @mailbox_id AND mb.department_id = @department_id AND mb.is_active
        """;

    public async Task<Guid> QueueReplyAsync(OutboundReplyRequest request, DbContext caller, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(caller);
        if (caller.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("IMailOutbox requires the caller to run inside an explicit transaction.");
        }

        var id = Guid.CreateVersion7();
        var now = clock.GetUtcNowMicroseconds();
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

        // What is stored is what is sent: the agent's text plus the signature resolved at THIS moment (sanitised once, here). Later edits of the signature never reach it.
        var signature = string.IsNullOrWhiteSpace(request.SignatureHtml) ? null : MailText.SanitiseHtml(request.SignatureHtml).Trim();
        if (signature is { Length: 0 })
        {
            signature = null;
        }

        var html = MailText.ImageMarkersToHtml(MailText.TextToHtml(request.BodyText)) + (signature is null ? "" : "<div style=\"margin-top:16px\">-- <br>\n" + signature + "</div>");
        var plain = MailText.ImageMarkersToText(request.BodyText);
        var text = signature is null ? plain : plain + "\n\n-- \n" + MailText.HtmlToText(signature);
        var inserted = await caller.Database.ExecuteSqlRawAsync(
            InsertSql,
            [
                new NpgsqlParameter("id", id),
                new NpgsqlParameter("subject", request.Subject.Length > 998 ? request.Subject[..998] : request.Subject),
                new NpgsqlParameter("to", NpgsqlDbType.Text) { Value = JsonSerializer.Serialize(new[] { request.To }, Json) },
                new NpgsqlParameter("cc", NpgsqlDbType.Text) { Value = JsonSerializer.Serialize(request.Cc, Json) },
                new NpgsqlParameter("now", now),
                new NpgsqlParameter("body", text),
                new NpgsqlParameter("html", html),
                new NpgsqlParameter("ticket_id", request.TicketId),
                new NpgsqlParameter("ticket_message_id", request.TicketMessageId),
                new NpgsqlParameter("reference", request.TicketReference),
                new NpgsqlParameter("token", token),
                new NpgsqlParameter("actor", NpgsqlDbType.Uuid) { Value = (object?)request.ActorUserId ?? DBNull.Value },
                new NpgsqlParameter("mailbox_id", request.MailboxId),
                new NpgsqlParameter("department_id", request.DepartmentId),
            ],
            cancellationToken);
        if (inserted != 1)
        {
            // The mailbox is not an active mailbox of this department: replies never fall back to another sender.
            throw new InvalidOperationException("The mailbox is not an active mailbox of the department.");
        }

        return id;
    }

    private const string AutoReplySql = """
        INSERT INTO mail.messages
            (id, mailbox_id, department_id, direction, state, subject, from_address, from_name, to_addresses, cc_addresses, occurred_at,
             body_text, body_html_original, headers, flags, ticket_id, ticket_message_id, ticket_reference, outbound_token, reply_to_message_id, requested_by_user_id,
             attempts, next_attempt_at, created_at, updated_at)
        SELECT @id, mb.id, mb.department_id, 'Outbound', 'Queued', @subject, mb.address, mb.display_name, @to::jsonb, '[]'::jsonb, @now,
               @body, @html, jsonb_build_object(), 'auto_reply', @ticket_id, NULL, @reference, @token, NULL, NULL, 0, @now, @now, @now
        FROM mail.mailboxes mb
        WHERE mb.id = @mailbox_id AND mb.department_id = @department_id AND mb.is_active
        """;

    public async Task<Guid?> QueueAutoReplyAsync(OutboundAutoReplyRequest request, DbContext caller, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(caller);
        if (caller.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("IMailOutbox requires the caller to run inside an explicit transaction.");
        }

        var id = Guid.CreateVersion7();
        var now = clock.GetUtcNowMicroseconds();

        // Per-mailbox cap (D11): the sender address is unauthenticated and can be rotated, so the limit is on what the MAILBOX sends.
        var since = now - TimeSpan.FromMinutes(options.Value.AcknowledgementWindowMinutes);
        var recent = await caller.Database.SqlQuery<long>(
            $"SELECT count(*) AS \"Value\" FROM mail.messages WHERE mailbox_id = {request.MailboxId} AND direction = 'Outbound' AND flags = 'auto_reply' AND created_at > {since}").SingleAsync(cancellationToken);
        if (recent >= options.Value.AcknowledgementLimitPerWindow)
        {
            return null;
        }

        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var subject = request.Subject.ReplaceLineEndings(" ").Trim();
        var inserted = await caller.Database.ExecuteSqlRawAsync(
            AutoReplySql,
            [
                new NpgsqlParameter("id", id),
                new NpgsqlParameter("subject", subject.Length > 998 ? subject[..998] : subject),
                new NpgsqlParameter("to", NpgsqlDbType.Text) { Value = JsonSerializer.Serialize(new[] { request.To }, Json) },
                new NpgsqlParameter("now", now),
                new NpgsqlParameter("body", request.BodyText),
                new NpgsqlParameter("html", MailText.TextToHtml(request.BodyText)),
                new NpgsqlParameter("ticket_id", request.TicketId),
                new NpgsqlParameter("reference", request.TicketReference),
                new NpgsqlParameter("token", token),
                new NpgsqlParameter("mailbox_id", request.MailboxId),
                new NpgsqlParameter("department_id", request.DepartmentId),
            ],
            cancellationToken);
        return inserted == 1 ? id : throw new InvalidOperationException("The mailbox is not an active mailbox of the department.");
    }

    public Task NotifyQueuedAsync(Guid outboundId, CancellationToken cancellationToken = default)
    {
        scheduler.EnqueueSend(outboundId);
        return Task.CompletedTask;
    }

    public async Task<Guid?> RetryAsync(Guid departmentId, Guid ticketMessageId, DbContext caller, CancellationToken cancellationToken = default)
    {
        if (caller.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("IMailOutbox requires the caller to run inside an explicit transaction.");
        }

        var now = clock.GetUtcNowMicroseconds();
        await using var command = caller.Database.GetDbConnection().CreateCommand();
        command.Transaction = caller.Database.CurrentTransaction.GetDbTransaction();
        // A confirmed non-delivery report means the earlier send DID reach the provider: the old draft and its Sent Items copy must not be mistaken for this retry,
        // so the draft id is cleared and the retry creates a new draft (a deliberate re-send). Any other failure keeps its draft (the sender then reconciles first).
        command.CommandText =
            """
            UPDATE mail.messages m SET state = 'Queued', attempts = 0, last_error = NULL, next_attempt_at = @now, delivery_reported_at = NULL, updated_at = @now,
                provider_draft_id = CASE WHEN o.last_error = @ndr THEN NULL ELSE m.provider_draft_id END
            FROM (SELECT id, last_error FROM mail.messages
                  WHERE department_id = @department_id AND ticket_message_id = @ticket_message_id AND direction = 'Outbound' AND state = 'Failed' FOR UPDATE) o
            WHERE m.id = o.id
            RETURNING m.id, (o.last_error = @ndr)
            """;
        command.Parameters.Add(new NpgsqlParameter("ndr", MailErrorCodes.NdrDeliveryFailed));
        command.Parameters.Add(new NpgsqlParameter("now", now));
        command.Parameters.Add(new NpgsqlParameter("department_id", departmentId));
        command.Parameters.Add(new NpgsqlParameter("ticket_message_id", ticketMessageId));
        Guid id;
        bool resend;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            id = reader.GetGuid(0);
            resend = !reader.IsDBNull(1) && reader.GetBoolean(1);
        }

        if (resend)
        {
            await auditWriter.WriteAsync(
                new AuditEntry(
                    AuditCategories.Mail, MailAuditActions.ResentAfterBounce, MailAudit.MessageObject, id, departmentId, current.UserId, current.DisplayName,
                    new { state = MessageStates.Failed, error = MailErrorCodes.NdrDeliveryFailed }, new { state = MessageStates.Queued, resend = true }),
                caller, cancellationToken);
        }

        return id;
    }
}

/// <summary>
/// Sends one queued outbound message from ITS mailbox. Retry-safe: the provider draft id is persisted before sending, so a retry
/// after a crash first looks for the sent copy instead of sending again. Failures are recorded and reported to the ticket message
/// (state Failed, short code), never swallowed.
/// </summary>
internal sealed class OutboundSender(
    MailDbContext db,
    Helpdesk.Modules.Attachments.Contracts.IAttachmentStore attachments,
    IMailProvider provider,
    IDeliveryStateSink delivery,
    MailAudit audit,
    TimeProvider clock,
    ILogger<OutboundSender> log)
{
    public const int MaxAttempts = 5;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task SendAsync(Guid outboundId, CancellationToken ct)
    {
        var now = clock.GetUtcNowMicroseconds();

        // Claim: only one worker moves a Queued, due row to Sending.
        var claimed = await db.Database.ExecuteSqlAsync(
            $"""
            UPDATE mail.messages SET state = 'Sending', attempts = attempts + 1, updated_at = {now}
            WHERE id = {outboundId} AND direction = 'Outbound' AND state = 'Queued' AND (next_attempt_at IS NULL OR next_attempt_at <= {now})
            """,
            ct);
        if (claimed != 1)
        {
            return;
        }

        var message = await db.Messages.SingleAsync(m => m.Id == outboundId, ct);
        try
        {
            var mailbox = await db.Mailboxes.AsNoTracking().SingleOrDefaultAsync(m => m.Id == message.MailboxId && m.DepartmentId == message.DepartmentId, ct);
            if (mailbox is not { IsActive: true })
            {
                throw new PermanentProviderException(MailErrorCodes.MailboxInactive);
            }

            var mref = new MailboxRef(mailbox.Id, mailbox.Address, mailbox.ProviderRef ?? mailbox.Address);
            var sentCopy = message.ProviderDraftId is { } existingDraft ? await provider.FindSentCopyAsync(mref, existingDraft, ct) : null;
            if (sentCopy is null)
            {
                var draftId = message.ProviderDraftId;
                if (draftId is null)
                {
                    string? replyTo = null;
                    if (message.ReplyToMessageId is { } replyToId)
                    {
                        replyTo = await db.Messages.AsNoTracking().Where(m => m.Id == replyToId && m.MailboxId == message.MailboxId)
                            .Select(m => m.ProviderMessageId).SingleOrDefaultAsync(ct);
                    }

                    var inline = await LoadInlineImagesAsync(message, ct);
                    var content = new OutboundContent(
                        replyTo,
                        replyTo is null ? OutboundSubject(message) : message.Subject,
                        BuildHtml(message),
                        Parse(message.ToJson),
                        Parse(message.CcJson),
                        message.OutboundToken!,
                        new EmailAddress(mailbox.Address, null), // Reply-To is always the helpdesk mailbox, never an agent's own address
                        MessageFlags.Has(message.Flags, "auto_reply"),
                        inline);
                    var draft = await provider.CreateDraftAsync(mref, content, ct);

                    // Persist the draft id (and the id the provider reports) BEFORE sending: a crash after the send must not send twice.
                    message.ProviderDraftId = draft.DraftId;
                    message.InternetMessageId = MailText.NormaliseMessageId(draft.InternetMessageId);
                    message.UpdatedAt = clock.GetUtcNowMicroseconds();
                    await db.SaveChangesAsync(ct);
                    draftId = draft.DraftId;
                }

                // From here the provider may have the message even if the call fails: remember that BEFORE asking, so a crash cannot turn into a blind resend.
                message.Flags = MessageFlags.With(message.Flags, MessageFlags.SubmitStarted);
                message.UpdatedAt = clock.GetUtcNowMicroseconds();
                await db.SaveChangesAsync(ct);
                sentCopy = await SubmitAsync(message, mref, draftId, ct);
            }

            await using var tx = await db.Database.BeginTransactionAsync(ct);
            if (sentCopy is not null)
            {
                message.ProviderMessageId = sentCopy.ProviderMessageId;

                // The id of the SENT copy is authoritative (Exchange may rewrite it on submission, E-12).
                message.InternetMessageId = MailText.NormaliseMessageId(sentCopy.InternetMessageId) ?? message.InternetMessageId;
            }

            message.State = MessageStates.Sent;
            message.LastError = null;
            message.NextAttemptAt = null;
            message.UpdatedAt = clock.GetUtcNowMicroseconds();
            await db.SaveChangesAsync(ct);
            await audit.WriteSystemAsync(MailAuditActions.Sent, MailAudit.MessageObject, message.Id, message.DepartmentId, null, new { ticketId = message.TicketId }, ct);
            await tx.CommitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (UncertainSubmissionException ex)
        {
            await MarkUncertainAsync(message, ex.InnerException ?? ex, ct);
        }
        catch (Exception ex)
        {
            await FailAsync(message, ex, ct);
        }

        await ReportAsync(message, ct);
    }

    /// <summary>
    /// The provider send call. A failure that leaves the outcome open (connection lost, timeout, 5xx) is NEVER retried blindly: the sent copy is looked up
    /// once, and without it the message becomes Uncertain for an operator. A 429 (the provider certainly did not act) and every 4xx are not ambiguous.
    /// </summary>
    private async Task<SentCopy?> SubmitAsync(MailMessage message, MailboxRef mref, string draftId, CancellationToken ct)
    {
        TransientProviderException? ambiguous = null;
        try
        {
            await provider.SendDraftAsync(mref, draftId, ct);
        }
        catch (TransientProviderException ex) when (ex.Ambiguous)
        {
            ambiguous = ex;
        }

        SentCopy? copy;
        try
        {
            copy = await provider.FindSentCopyAsync(mref, draftId, ct);
        }
        catch (Exception ex) when (ambiguous is not null && ex is not OperationCanceledException)
        {
            copy = null;
        }

        if (ambiguous is not null)
        {
            if (copy is null)
            {
                throw new UncertainSubmissionException(ambiguous);
            }

            log.OutboundReconciled(message.Id);
        }

        return copy;
    }

    /// <summary>
    /// Resolves a message whose send may have reached the provider (a crashed worker left it Sending, or an operator asks): the sent copy proves it was
    /// submitted; without one the message is Uncertain. Never sends. Returns true when the sent copy was found.
    /// </summary>
    public async Task<bool> ReconcileAsync(Guid outboundId, CancellationToken ct, bool automatic = false)
    {
        var message = await db.Messages.SingleOrDefaultAsync(
            m => m.Id == outboundId && m.Direction == MessageDirections.Outbound && (m.State == MessageStates.Sending || m.State == MessageStates.Uncertain), ct);
        if (message is null)
        {
            return false;
        }

        SentCopy? copy = null;
        try
        {
            var mailbox = await db.Mailboxes.AsNoTracking().SingleOrDefaultAsync(m => m.Id == message.MailboxId && m.DepartmentId == message.DepartmentId, ct);
            if (mailbox is not null && message.ProviderDraftId is { } draftId)
            {
                copy = await provider.FindSentCopyAsync(new MailboxRef(mailbox.Id, mailbox.Address, mailbox.ProviderRef ?? mailbox.Address), draftId, ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.OutboundUncertain(message.Id, MailText.ShortError(ex));
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (copy is not null)
        {
            message.ProviderMessageId = copy.ProviderMessageId;
            message.InternetMessageId = MailText.NormaliseMessageId(copy.InternetMessageId) ?? message.InternetMessageId;
            message.State = MessageStates.Sent;
            message.LastError = null;
            message.NextAttemptAt = null;
            message.DeliveryReportedAt = null;
            message.UpdatedAt = clock.GetUtcNowMicroseconds();
            await db.SaveChangesAsync(ct);
            await audit.WriteSystemAsync(MailAuditActions.SendReconciled, MailAudit.MessageObject, message.Id, message.DepartmentId, null, new { ticketId = message.TicketId }, ct);
            log.OutboundReconciled(message.Id);
        }
        else if (message.State == MessageStates.Sending)
        {
            await ApplyUncertainAsync(message, ct);
        }
        else if (automatic)
        {
            // Still not found: stay Uncertain for an operator, and do not look again by itself.
            message.Flags = MessageFlags.With(message.Flags, MessageFlags.ReconcileAttempted);
            await db.SaveChangesAsync(ct);
        }

        await tx.CommitAsync(ct);
        await ReportAsync(message, ct);
        return copy is not null;
    }

    private async Task MarkUncertainAsync(MailMessage message, Exception ex, CancellationToken ct)
    {
        log.OutboundUncertain(message.Id, MailText.ShortError(ex));
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await ApplyUncertainAsync(message, ct);
        await tx.CommitAsync(ct);
    }

    private async Task ApplyUncertainAsync(MailMessage message, CancellationToken ct)
    {
        message.State = MessageStates.Uncertain;
        message.Flags = MessageFlags.Without(message.Flags, MessageFlags.ReconcileAttempted);
        message.LastError = MailErrorCodes.OutcomeUncertain;
        message.NextAttemptAt = null;
        message.DeliveryReportedAt = null;
        message.UpdatedAt = clock.GetUtcNowMicroseconds();
        await db.SaveChangesAsync(ct);
        await audit.WriteSystemAsync(MailAuditActions.SendUncertain, MailAudit.MessageObject, message.Id, message.DepartmentId, null, new { attempts = message.Attempts }, ct);
    }

    /// <summary>Tells Ticketing how it ended (Sent/Failed/Uncertain/Bounced). Safe to repeat; rows still unreported are retried by the dispatcher.</summary>
    public async Task ReportAsync(MailMessage message, CancellationToken ct)
    {
        if (message.TicketMessageId is not { } ticketMessageId || message.State is not (MessageStates.Sent or MessageStates.Failed or MessageStates.Uncertain))
        {
            return;
        }

        try
        {
            // Ticketing only learns "submitted" or "not (confirmed) submitted" plus a safe category. Neither means delivered.
            var state = message.State switch
            {
                MessageStates.Sent => MailDeliveryStates.Sent,
                MessageStates.Uncertain => MailDeliveryStates.Uncertain,
                _ when message.LastError == MailErrorCodes.NdrDeliveryFailed => MailDeliveryStates.Bounced,
                _ => MailDeliveryStates.Failed,
            };
            await delivery.SetAsync(message.DepartmentId, ticketMessageId, state, MailErrors.Category(message.LastError), message.DeliverySeq, ct);
            message.DeliveryReportedAt = clock.GetUtcNowMicroseconds();
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.DeliveryReportFailed(message.Id, MailText.ShortError(ex));
        }
    }

    private async Task FailAsync(MailMessage message, Exception ex, CancellationToken ct)
    {
        var code = MailText.ShortError(ex);
        var transient = ex is TransientProviderException;
        log.OutboundFailed(message.Id, code, transient);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        message.LastError = code;
        var retry = transient && message.Attempts < MaxAttempts;
        message.State = retry ? MessageStates.Queued : MessageStates.Failed;
        message.NextAttemptAt = retry ? clock.GetUtcNowMicroseconds() + (ex is TransientProviderException { RetryAfter: { } after } ? after : TimeSpan.FromMinutes(Math.Pow(2, message.Attempts))) : null;
        message.UpdatedAt = clock.GetUtcNowMicroseconds();
        await db.SaveChangesAsync(ct);
        if (!retry)
        {
            await audit.WriteSystemAsync(MailAuditActions.SendFailed, MailAudit.MessageObject, message.Id, message.DepartmentId, null, new { attempts = message.Attempts, error = code }, ct);
        }

        await tx.CommitAsync(ct);
    }

    private static readonly System.Text.RegularExpressions.Regex CidRef = new(@"cid:([0-9a-f]{32})@helpdesk", System.Text.RegularExpressions.RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    /// <summary>
    /// The pasted images the stored HTML points at, read from attachment storage. Every one must be a stored, downloadable (clean when scanning is required) image of THIS ticket;
    /// otherwise the message is NOT sent (a permanent failure with a short code), never sent with a broken or unscanned image.
    /// </summary>
    private async Task<IReadOnlyList<OutboundInlineImage>?> LoadInlineImagesAsync(MailMessage message, CancellationToken ct)
    {
        var html = message.BodyHtmlOriginal;
        if (html is null || message.TicketId is not { } ticketId)
        {
            return null;
        }

        var ids = CidRef.Matches(html).Select(m => Guid.ParseExact(m.Groups[1].Value, "N")).Distinct().ToList();
        if (ids.Count == 0)
        {
            return null;
        }

        var images = new List<OutboundInlineImage>();
        foreach (var id in ids)
        {
            var opened = await attachments.OpenAsync(message.DepartmentId, ticketId, id, ct);
            if (opened.Download is not { } download || !download.ContentType.StartsWith("image/", StringComparison.Ordinal))
            {
                throw new PermanentProviderException("inline_image_unavailable");
            }

            await using var stream = download.Content;
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, ct);
            images.Add(new OutboundInlineImage(MailText.ImageContentId(id), download.FileName, download.ContentType, buffer.ToArray()));
        }

        return images;
    }

    private static string OutboundSubject(MailMessage message)
    {
        // The reference is a human-recognition hint only; inbound threading never relies on it.
        // Single line always (a ticket subject entered in the web UI may contain line breaks; never let them reach a mail header).
        var single = message.Subject.ReplaceLineEndings(" ").Trim();
        if (message.Flags is { } flags && flags.Contains("auto_reply", StringComparison.Ordinal))
        {
            return single; // an acknowledgement uses its template subject exactly
        }

        var subject = single.StartsWith("Re:", StringComparison.OrdinalIgnoreCase) ? single : "Re: " + single;
        return message.TicketReference is { } reference && !subject.Contains(reference, StringComparison.OrdinalIgnoreCase) ? $"{subject} [{reference}]" : subject;
    }

    private static string BuildHtml(MailMessage message)
    {
        // The HTML stored when the message was queued is what is sent (agent text + the signature of the sending agent at that moment). There is no department footer.
        var html = new StringBuilder(message.BodyHtmlOriginal ?? MailText.TextToHtml(message.BodyText ?? ""));
        html.Append("<p style=\"color:#777;font-size:11px;margin-top:16px\">");
        if (message.TicketReference is { } reference)
        {
            html.Append("Ticket ").Append(System.Net.WebUtility.HtmlEncode(reference)).Append(" &middot; ");
        }

        html.Append("[hd:").Append(message.OutboundToken).Append("]</p>");
        return html.ToString();
    }

    private static List<EmailAddress> Parse(string json) =>
        [.. (JsonSerializer.Deserialize<List<MailParticipant>>(json, Json) ?? []).Select(p => new EmailAddress(p.Address, p.Name))];
}

/// <summary>The provider call failed in a way that leaves it unknown whether the message was accepted.</summary>
internal sealed class UncertainSubmissionException(Exception inner) : Exception("outcome_uncertain", inner);
