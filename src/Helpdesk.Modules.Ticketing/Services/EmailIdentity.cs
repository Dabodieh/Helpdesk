using System.Net;
using System.Text.RegularExpressions;
using Helpdesk.Modules.Mail.Contracts;
using Helpdesk.Modules.Organisation.Contracts;
using Helpdesk.Modules.Ticketing.Domain;
using Helpdesk.Modules.Ticketing.Persistence;
using Helpdesk.SharedKernel.Database;
using Helpdesk.SharedKernel.Errors;
using Helpdesk.SharedKernel.Security;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Modules.Ticketing.Services;

/// <summary>
/// The only template mechanism of the email identity features: <c>{{name}}</c> placeholders from a fixed list, replaced in ONE pass (values are never scanned
/// again). No loops, conditionals, functions or property paths. Unknown placeholders are rejected when a template is saved.
/// </summary>
internal static partial class TemplateText
{
    public static readonly string[] SignaturePlaceholders = ["agent.name", "agent.email", "department.name", "mailbox.displayName", "mailbox.email"];
    public static readonly string[] AutoReplyPlaceholders = ["requester.name", "ticket.reference", "ticket.subject", "department.name", "mailbox.displayName", "mailbox.email"];

    [GeneratedRegex(@"\{\{\s*([^{}]*?)\s*\}\}")]
    private static partial Regex Token();

    /// <summary>Error messages for unknown placeholders or stray braces; empty when the template is valid.</summary>
    public static string? Validate(string template, IReadOnlyCollection<string> allowed)
    {
        var unknown = Token().Matches(template).Select(m => m.Groups[1].Value).Where(n => !allowed.Contains(n, StringComparer.Ordinal)).Distinct().ToList();
        if (unknown.Count > 0)
        {
            return $"Unknown placeholder: {string.Join(", ", unknown.Select(u => "{{" + u + "}}"))}. Available: {string.Join(", ", allowed.Select(a => "{{" + a + "}}"))}.";
        }

        var rest = Token().Replace(template, "");
        return rest.Contains("{{", StringComparison.Ordinal) || rest.Contains("}}", StringComparison.Ordinal) ? "Braces must form complete {{placeholders}}." : null;
    }

    public static string Render(string template, IReadOnlyDictionary<string, string> values, Func<string, string> encode) =>
        Token().Replace(template, m => values.TryGetValue(m.Groups[1].Value, out var v) ? encode(v) : "");

    public static string OneLine(string value) => value.ReplaceLineEndings(" ").Trim();
}

internal sealed record SignatureDto(Guid DepartmentId, string DepartmentName, string? Html, DateTimeOffset? UpdatedAt, IReadOnlyList<string> Placeholders);

internal sealed record SignatureRequest(string? Html);

internal sealed record SignaturePreviewDto(string Html);

/// <summary>
/// An agent's own signature for one department. Only the agent edits it (department administrators cannot change it); it is stored sanitised and tied to the
/// department membership by a cascading foreign key, so it disappears with the membership and is never used for another department. It is resolved when a reply
/// is actually sent, from the sending agent, and appended once by the mail outbox.
/// </summary>
internal sealed class SignatureService(
    TicketingDbContext db,
    TicketingAudit audit,
    IEmailHtmlSanitiser sanitiser,
    IMailboxDirectory mailboxes,
    IOrganisationDirectory organisation,
    ICurrentUser current,
    TimeProvider clock)
{
    public const int MaxLength = 8000;

    public async Task<SignatureDto> GetAsync(Guid departmentId, CancellationToken ct)
    {
        var department = await organisation.FindDepartmentAsync(departmentId, ct) ?? throw new NotFoundException();
        var me = current.UserId;
        var row = await db.AgentSignatures.AsNoTracking().SingleOrDefaultAsync(s => s.DepartmentId == departmentId && s.UserId == me, ct);
        return new SignatureDto(departmentId, department.Name, row?.SignatureHtml, row?.UpdatedAt, TemplateText.SignaturePlaceholders);
    }

    public async Task<SignatureDto> PutAsync(Guid departmentId, SignatureRequest request, CancellationToken ct)
    {
        var clean = Prepare(request.Html);
        var department = await organisation.FindDepartmentAsync(departmentId, ct) ?? throw new NotFoundException();
        var me = current.UserId;
        var now = clock.GetUtcNowMicroseconds();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (clean is null)
        {
            var removed = await db.AgentSignatures.Where(s => s.DepartmentId == departmentId && s.UserId == me).ExecuteDeleteAsync(ct);
            if (removed > 0)
            {
                await audit.WriteAsync(TicketAuditActions.SignatureChanged, "agent_signature", me, departmentId, null, new { change = "cleared" }, ct);
            }
        }
        else
        {
            await db.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO ticketing.agent_signatures (department_id, user_id, signature_html, updated_at)
                VALUES ({departmentId}, {me}, {clean}, {now})
                ON CONFLICT (department_id, user_id) DO UPDATE SET signature_html = EXCLUDED.signature_html, updated_at = EXCLUDED.updated_at
                """,
                ct);
            await audit.WriteAsync(TicketAuditActions.SignatureChanged, "agent_signature", me, departmentId, null, new { change = "saved" }, ct);
        }

        await tx.CommitAsync(ct);
        return new SignatureDto(departmentId, department.Name, clean, clean is null ? null : now, TemplateText.SignaturePlaceholders);
    }

    /// <summary>What would be appended to a reply in this department, for the caller (default mailbox of the department for the mailbox placeholders). Nothing is stored or sent.</summary>
    public async Task<SignaturePreviewDto> PreviewAsync(Guid departmentId, SignatureRequest request, CancellationToken ct)
    {
        var clean = Prepare(request.Html);
        var department = await organisation.FindDepartmentAsync(departmentId, ct) ?? throw new NotFoundException();
        var mailbox = await mailboxes.FindReplyMailboxAsync(departmentId, null, ct);
        return new SignaturePreviewDto(clean is null ? "" : Resolve(clean, department.Name, mailbox));
    }

    /// <summary>The signature of the SENDING agent for the department of the ticket, resolved now; null when none is configured (never another department's or agent's).</summary>
    public async Task<string?> ResolveForSendAsync(Guid departmentId, string departmentName, ReplyMailbox? mailbox, CancellationToken ct)
    {
        var me = current.UserId;
        var html = await db.AgentSignatures.AsNoTracking().Where(s => s.DepartmentId == departmentId && s.UserId == me).Select(s => s.SignatureHtml).SingleOrDefaultAsync(ct);
        return string.IsNullOrWhiteSpace(html) ? null : Resolve(html, departmentName, mailbox);
    }

    private string Resolve(string html, string departmentName, ReplyMailbox? mailbox) =>
        TemplateText.Render(
            html,
            new Dictionary<string, string>
            {
                ["agent.name"] = current.DisplayName,
                ["agent.email"] = current.Email ?? "",
                ["department.name"] = departmentName,
                ["mailbox.displayName"] = mailbox?.Name ?? "",
                ["mailbox.email"] = mailbox?.Address ?? "",
            },
            WebUtility.HtmlEncode);

    private string? Prepare(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        if (html.Length > MaxLength * 2 || TicketValidation.HasNul(html))
        {
            throw new RequestValidationException("html", "The signature is too long.");
        }

        var clean = sanitiser.Sanitise(html).Trim();
        if (clean.Length > MaxLength)
        {
            throw new RequestValidationException("html", $"The signature must be at most {MaxLength} characters.");
        }

        if (TemplateText.Validate(clean, TemplateText.SignaturePlaceholders) is { } problem)
        {
            throw new RequestValidationException("html", problem);
        }

        return clean.Length == 0 ? null : clean;
    }
}

internal sealed record AutoReplyDto(
    Guid DepartmentId, bool Configured, bool Enabled, bool ForEmail, bool ForPortal, string Subject, string Body, IReadOnlyList<string> Placeholders, string Version);

internal sealed record AutoReplyRequest(bool? Enabled, bool? ForEmail, bool? ForPortal, string? Subject, string? Body, string? Version);

internal sealed record AutoReplyPreviewRequest(string? Subject, string? Body);

internal sealed record AutoReplyPreviewDto(string Subject, string Body, string FromName, string FromAddress);

/// <summary>
/// Department-level automatic acknowledgement of a new ticket (disabled until a department configures it). The template belongs to the department; the sender
/// is the mailbox of the TICKET. No agent signature. It is queued inside the transaction that creates the ticket, so one creation yields at most one acknowledgement
/// (a replayed inbound message finds the ticket already there and never reaches this code). Mail the pipeline already ignores (auto-submitted, bounces, bulk, our own
/// addresses) never creates a ticket and so is never acknowledged.
/// </summary>
internal sealed class AutoReplyService(
    TicketingDbContext db,
    TicketingAudit audit,
    IMailboxDirectory mailboxes,
    IMailOutbox outbox,
    IOrganisationDirectory organisation,
    TimeProvider clock)
{
    public const string DefaultSubject = "We've received your request — {{ticket.reference}}";

    public const string DefaultBody =
        "Hi {{requester.name}},\n\nThanks for contacting {{department.name}}.\n\nYour request has been logged as {{ticket.reference}}.\n\nWe'll get back to you as soon as possible.";

    public async Task<AutoReplyDto> GetAsync(Guid departmentId, CancellationToken ct)
    {
        var row = await db.DepartmentAutoReplies.AsNoTracking().SingleOrDefaultAsync(a => a.DepartmentId == departmentId, ct);
        return row is null
            ? new AutoReplyDto(departmentId, false, false, true, false, DefaultSubject, DefaultBody, TemplateText.AutoReplyPlaceholders, "")
            : ToDto(row);
    }

    public async Task<AutoReplyDto> PutAsync(Guid departmentId, AutoReplyRequest request, CancellationToken ct)
    {
        var current = await db.DepartmentAutoReplies.SingleOrDefaultAsync(a => a.DepartmentId == departmentId, ct);
        var subject = (request.Subject ?? current?.SubjectTemplate ?? DefaultSubject).Trim();
        var body = (request.Body ?? current?.BodyTemplate ?? DefaultBody).Trim();
        var errors = new Dictionary<string, string[]>();
        Check(errors, "subject", subject, 200);
        Check(errors, "body", body, 5000);
        uint? version = null;
        if (current is not null)
        {
            var versionErrors = new Dictionary<string, string[]>();
            version = TicketValidation.Version(versionErrors, request.Version);
            foreach (var (k, v) in versionErrors)
            {
                errors[k] = v;
            }
        }

        TicketValidation.ThrowIfAny(errors);

        var now = clock.GetUtcNowMicroseconds();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var changes = new List<string>();
        if (current is null)
        {
            current = new DepartmentAutoReply { DepartmentId = departmentId, CreatedAt = now };
            db.DepartmentAutoReplies.Add(current);
            changes.Add("created");
        }
        else
        {
            if (current.Version != version)
            {
                throw new ConflictException("The setting was changed elsewhere. Reload and retry.", ProblemCodes.VersionConflict);
            }

            db.Entry(current).Property(a => a.Version).OriginalValue = version!.Value;
        }

        var enabled = request.Enabled ?? current.Enabled;
        var forEmail = request.ForEmail ?? current.ForEmail;
        var forPortal = request.ForPortal ?? current.ForPortal;
        if (enabled != current.Enabled)
        {
            changes.Add(enabled ? "enabled" : "disabled");
        }

        if (forEmail != current.ForEmail || forPortal != current.ForPortal)
        {
            changes.Add("applies_to_changed");
        }

        if (subject != current.SubjectTemplate || body != current.BodyTemplate)
        {
            changes.Add("template_changed");
        }

        current.Enabled = enabled;
        current.ForEmail = forEmail;
        current.ForPortal = forPortal;
        current.SubjectTemplate = subject;
        current.BodyTemplate = body;
        current.UpdatedAt = now;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictException("The setting was changed elsewhere. Reload and retry.", ProblemCodes.VersionConflict);
        }

        if (changes.Count > 0)
        {
            // Identifiers and the kind of change only: the template text itself is not copied into the audit stream.
            await audit.WriteAsync(TicketAuditActions.AutoReplyChanged, "auto_reply", departmentId, departmentId, null, new { changes, enabled, forEmail, forPortal }, ct);
        }

        await tx.CommitAsync(ct);
        return ToDto(current);
    }

    public async Task<AutoReplyPreviewDto> PreviewAsync(Guid departmentId, AutoReplyPreviewRequest request, CancellationToken ct)
    {
        var department = await organisation.FindDepartmentAsync(departmentId, ct) ?? throw new NotFoundException();
        var saved = await GetAsync(departmentId, ct);
        var subject = (request.Subject ?? saved.Subject).Trim();
        var body = (request.Body ?? saved.Body).Trim();
        var errors = new Dictionary<string, string[]>();
        Check(errors, "subject", subject, 200);
        Check(errors, "body", body, 5000);
        TicketValidation.ThrowIfAny(errors);
        var mailbox = await mailboxes.FindReplyMailboxAsync(departmentId, null, ct);
        var values = Values("Jane Smith", $"{department.Key}-1042", "Printer will not connect", department.Name, mailbox);
        return new AutoReplyPreviewDto(
            TemplateText.OneLine(TemplateText.Render(subject, values, x => x)), TemplateText.Render(body, values, x => x), mailbox?.Name ?? "(no mailbox)", mailbox?.Address ?? "");
    }

    /// <summary>
    /// Queues the acknowledgement for a ticket that is being created, on the caller's open transaction. Returns the outbound id to nudge AFTER the commit, or null
    /// when nothing is to be sent (disabled, not for this source, no mailbox). A department without a usable mailbox gets an audit entry, never a made-up sender.
    /// </summary>
    public async Task<Guid?> QueueForNewTicketAsync(Ticket ticket, Requester requester, string departmentName, string source, CancellationToken ct)
    {
        var config = await db.DepartmentAutoReplies.AsNoTracking().SingleOrDefaultAsync(a => a.DepartmentId == ticket.DepartmentId, ct);
        if (config is not { Enabled: true } || (source == TicketSources.Email && !config.ForEmail) || (source == TicketSources.Portal && !config.ForPortal)
            || source is not (TicketSources.Email or TicketSources.Portal))
        {
            return null;
        }

        var mailbox = await mailboxes.FindReplyMailboxAsync(ticket.DepartmentId, ticket.MailboxId, ct);
        if (mailbox is null)
        {
            await audit.WriteSystemAsync(TicketAuditActions.AutoReplyFailed, TicketingAudit.TicketObject, ticket.Id, ticket.DepartmentId, null, new { reason = "no_mailbox", source }, ct);
            return null;
        }

        // Never acknowledge ourselves (a requester address that is the sending mailbox would be a loop).
        if (string.Equals(requester.EmailNormalised, mailbox.Address.Trim().ToLowerInvariant(), StringComparison.Ordinal))
        {
            return null;
        }

        var values = Values(requester.DisplayName, ticket.Reference, ticket.Subject, departmentName, mailbox);
        var id = await outbox.QueueAutoReplyAsync(
            new OutboundAutoReplyRequest(
                ticket.DepartmentId, mailbox.MailboxId, ticket.Id, ticket.Reference, new MailParticipant(requester.Email, requester.DisplayName),
                TemplateText.OneLine(TemplateText.Render(config.SubjectTemplate, values, TemplateText.OneLine)), TemplateText.Render(config.BodyTemplate, values, x => x)),
            db, ct);
        await audit.WriteSystemAsync(TicketAuditActions.AutoReplyQueued, TicketingAudit.TicketObject, ticket.Id, ticket.DepartmentId, null, new { source, mailboxId = mailbox.MailboxId }, ct);
        return id;
    }

    private static Dictionary<string, string> Values(string requesterName, string reference, string subject, string departmentName, ReplyMailbox? mailbox) => new()
    {
        ["requester.name"] = requesterName,
        ["ticket.reference"] = reference,
        ["ticket.subject"] = subject,
        ["department.name"] = departmentName,
        ["mailbox.displayName"] = mailbox?.Name ?? "",
        ["mailbox.email"] = mailbox?.Address ?? "",
    };

    private static void Check(Dictionary<string, string[]> errors, string field, string value, int max)
    {
        if (value.Length == 0 || value.Length > max || TicketValidation.HasNul(value))
        {
            errors[field] = [$"{field} is required and at most {max} characters."];
        }
        else if (TemplateText.Validate(value, TemplateText.AutoReplyPlaceholders) is { } problem)
        {
            errors[field] = [problem];
        }
    }

    private static AutoReplyDto ToDto(DepartmentAutoReply r) =>
        new(r.DepartmentId, true, r.Enabled, r.ForEmail, r.ForPortal, r.SubjectTemplate, r.BodyTemplate, TemplateText.AutoReplyPlaceholders, ConcurrencyVersion.ToToken(r.Version));
}
