using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Helpdesk.Modules.Mail.Contracts;
using Helpdesk.Modules.Mail.Domain;
using Helpdesk.Modules.Mail.Persistence;
using Helpdesk.Modules.Mail.Providers;
using Helpdesk.SharedKernel.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace Helpdesk.Modules.Mail.Services;

/// <summary>
/// The outbox side used by Ticketing: the reply row is inserted on the caller's own connection and transaction, so the ticket
/// message and its outbound mail commit or roll back together (nothing is "sent" that the ticket does not show, and vice versa).
/// </summary>
internal sealed class MailOutbox(IMailJobScheduler scheduler, TimeProvider clock) : IMailOutbox
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private const string InsertSql = """
        INSERT INTO mail.messages
            (id, mailbox_id, department_id, direction, state, subject, from_address, from_name, to_addresses, cc_addresses, occurred_at,
             body_text, headers, ticket_id, ticket_message_id, ticket_reference, outbound_token, reply_to_message_id, requested_by_user_id,
             attempts, next_attempt_at, created_at, updated_at)
        SELECT @id, mb.id, mb.department_id, 'Outbound', 'Queued', @subject, mb.address, mb.display_name, @to::jsonb, @cc::jsonb, @now,
               @body, jsonb_build_object(), @ticket_id, @ticket_message_id, @reference, @token,
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
        var inserted = await caller.Database.ExecuteSqlRawAsync(
            InsertSql,
            [
                new NpgsqlParameter("id", id),
                new NpgsqlParameter("subject", request.Subject.Length > 998 ? request.Subject[..998] : request.Subject),
                new NpgsqlParameter("to", NpgsqlDbType.Text) { Value = JsonSerializer.Serialize(new[] { request.To }, Json) },
                new NpgsqlParameter("cc", NpgsqlDbType.Text) { Value = JsonSerializer.Serialize(request.Cc, Json) },
                new NpgsqlParameter("now", now),
                new NpgsqlParameter("body", request.BodyText),
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
        return await caller.Database.SqlQueryRaw<Guid>(
            """
            UPDATE mail.messages SET state = 'Queued', attempts = 0, last_error = NULL, next_attempt_at = @now, delivery_reported_at = NULL, updated_at = @now
            WHERE department_id = @department_id AND ticket_message_id = @ticket_message_id AND direction = 'Outbound' AND state = 'Failed'
            RETURNING id AS "Value"
            """,
            new NpgsqlParameter("now", now), new NpgsqlParameter("department_id", departmentId), new NpgsqlParameter("ticket_message_id", ticketMessageId))
            .Select(id => (Guid?)id).FirstOrDefaultAsync(cancellationToken);
    }
}

/// <summary>
/// Sends one queued outbound message from ITS mailbox. Retry-safe: the provider draft id is persisted before sending, so a retry
/// after a crash first looks for the sent copy instead of sending again. Failures are recorded and reported to the ticket message
/// (state Failed, short code), never swallowed.
/// </summary>
internal sealed class OutboundSender(
    MailDbContext db,
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
                throw new PermanentProviderException("mailbox_inactive");
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

                    var content = new OutboundContent(
                        replyTo,
                        replyTo is null ? OutboundSubject(message) : message.Subject,
                        BuildHtml(message, mailbox),
                        Parse(message.ToJson),
                        Parse(message.CcJson),
                        message.OutboundToken!);
                    var draft = await provider.CreateDraftAsync(mref, content, ct);

                    // Persist the draft id (and the id the provider reports) BEFORE sending: a crash after the send must not send twice.
                    message.ProviderDraftId = draft.DraftId;
                    message.InternetMessageId = MailText.NormaliseMessageId(draft.InternetMessageId);
                    message.UpdatedAt = clock.GetUtcNowMicroseconds();
                    await db.SaveChangesAsync(ct);
                    draftId = draft.DraftId;
                }

                await provider.SendDraftAsync(mref, draftId, ct);
                sentCopy = await provider.FindSentCopyAsync(mref, draftId, ct);
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
        catch (Exception ex)
        {
            await FailAsync(message, ex, ct);
        }

        await ReportAsync(message, ct);
    }

    /// <summary>Tells Ticketing how it ended (Sent/Failed). Safe to repeat; rows still unreported are retried by the dispatcher.</summary>
    public async Task ReportAsync(MailMessage message, CancellationToken ct)
    {
        if (message.TicketMessageId is not { } ticketMessageId || message.State is not (MessageStates.Sent or MessageStates.Failed))
        {
            return;
        }

        try
        {
            await delivery.SetAsync(
                message.DepartmentId, ticketMessageId, message.State == MessageStates.Sent ? MailDeliveryStates.Sent : MailDeliveryStates.Failed, message.LastError, ct);
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

    private static string OutboundSubject(MailMessage message)
    {
        // The reference is a human-recognition hint only; inbound threading never relies on it.
        var subject = message.Subject.StartsWith("Re:", StringComparison.OrdinalIgnoreCase) ? message.Subject : "Re: " + message.Subject;
        return message.TicketReference is { } reference && !subject.Contains(reference, StringComparison.OrdinalIgnoreCase) ? $"{subject} [{reference}]" : subject;
    }

    private static string BuildHtml(MailMessage message, Mailbox mailbox)
    {
        var text = message.BodyText ?? "";
        if (!string.IsNullOrWhiteSpace(mailbox.Signature))
        {
            text += "\n\n-- \n" + mailbox.Signature.Trim();
        }

        var html = new StringBuilder(MailText.TextToHtml(text));
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
