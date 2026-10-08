using System.Text.Json.Nodes;
using Helpdesk.Modules.Mail.Contracts;
using Helpdesk.Modules.Mail.Persistence;
using Helpdesk.Modules.Mail.Providers;
using Helpdesk.Modules.Mail.Services;
using Helpdesk.SharedKernel.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Modules.Mail.Endpoints;

internal sealed record RetryRequest(bool? AcceptDuplicateRisk);

internal static class MailEndpoints
{
    private const int MaxWebhookBytes = 256 * 1024;

    public static void Map(IEndpointRouteBuilder app, bool enableDevEndpoints)
    {
        MapAdministration(app);
        MapWebhooks(app);
        if (enableDevEndpoints)
        {
            MapDevelopment(app);
        }
    }

    private static void MapAdministration(IEndpointRouteBuilder app)
    {
        var mailboxes = app.MapGroup("/api/departments/{departmentId:guid}/mailboxes").WithTags("Mail");

        mailboxes.MapGet("", async (Guid departmentId, MailboxService service, CancellationToken ct) => TypedResults.Ok(await service.ListAsync(departmentId, ct)))
            .RequirePermission(PermissionCodes.DepartmentMailboxesManage);

        mailboxes.MapPost("", async (Guid departmentId, CreateMailboxRequest body, MailboxService service, CancellationToken ct) =>
            {
                var created = await service.CreateAsync(departmentId, body, ct);
                return TypedResults.Created($"/api/departments/{departmentId}/mailboxes/{created.Id}", created);
            })
            .RequirePermission(PermissionCodes.DepartmentMailboxesManage);

        mailboxes.MapGet("{mailboxId:guid}", async (Guid departmentId, Guid mailboxId, MailboxService service, CancellationToken ct) =>
                TypedResults.Ok(await service.GetAsync(departmentId, mailboxId, ct)))
            .RequirePermission(PermissionCodes.DepartmentMailboxesManage);

        mailboxes.MapPatch("{mailboxId:guid}", async (Guid departmentId, Guid mailboxId, UpdateMailboxRequest body, MailboxService service, CancellationToken ct) =>
                TypedResults.Ok(await service.UpdateAsync(departmentId, mailboxId, body, ct)))
            .RequirePermission(PermissionCodes.DepartmentMailboxesManage);

        var mail = app.MapGroup("/api/departments/{departmentId:guid}/mail").WithTags("Mail");

        mail.MapGet("attention", async (Guid departmentId, string? cursor, int? limit, MailboxService service, CancellationToken ct) =>
                TypedResults.Ok(await service.AttentionAsync(departmentId, cursor, limit, ct)))
            .RequirePermission(PermissionCodes.DepartmentMailboxesManage);

        // D12: an audited "create ticket from this message" for ignored mail (not for delivery reports or loops).
        mail.MapPost("{mailMessageId:guid}/release", async (Guid departmentId, Guid mailMessageId, MailboxService service, CancellationToken ct) =>
                TypedResults.Ok(await service.ReleaseIgnoredAsync(departmentId, mailMessageId, ct)))
            .RequirePermission(PermissionCodes.DepartmentMailboxesManage);

        mail.MapPost("{mailMessageId:guid}/retry", async (Guid departmentId, Guid mailMessageId, RetryRequest? body, MailboxService service, CancellationToken ct) =>
            {
                var outcome = await service.RetryAsync(departmentId, mailMessageId, body?.AcceptDuplicateRisk == true, ct);
                return outcome is null ? Results.NoContent() : Results.Ok(outcome);
            })
            .RequirePermission(PermissionCodes.DepartmentMailboxesManage);
    }

    /// <summary>
    /// Microsoft Graph callbacks. Anonymous and CSRF-exempt by necessity: Graph cannot sign in. Authentication is the per-subscription
    /// client-state secret; the body is size-limited and strictly parsed; the response never reveals whether a subscription exists.
    /// </summary>
    private static void MapWebhooks(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/mail/graph/notifications", (HttpContext http, string? validationToken, NotificationService service, CancellationToken ct) =>
                HandleWebhookAsync(http, validationToken, service, lifecycle: false, ct))
            .AllowAnonymous()
            .WithMetadata(new IgnoreCsrfMetadata())
            .WithTags("Mail");

        app.MapPost("/api/mail/graph/lifecycle", (HttpContext http, string? validationToken, NotificationService service, CancellationToken ct) =>
                HandleWebhookAsync(http, validationToken, service, lifecycle: true, ct))
            .AllowAnonymous()
            .WithMetadata(new IgnoreCsrfMetadata())
            .WithTags("Mail");
    }

    private static async Task<IResult> HandleWebhookAsync(HttpContext http, string? validationToken, NotificationService service, bool lifecycle, CancellationToken ct)
    {
        // Subscription handshake: echo the (URL-decoded) token as text/plain within seconds.
        if (validationToken is not null)
        {
            return validationToken.Length > 2048 ? TypedResults.BadRequest() : TypedResults.Text(validationToken, "text/plain");
        }

        if (http.Request.ContentLength is > MaxWebhookBytes)
        {
            return TypedResults.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        var limit = http.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (limit is { IsReadOnly: false })
        {
            limit.MaxRequestBodySize = MaxWebhookBytes;
        }

        JsonNode? payload;
        try
        {
            payload = await JsonNode.ParseAsync(http.Request.Body, cancellationToken: ct);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or BadHttpRequestException)
        {
            return TypedResults.BadRequest();
        }

        if (payload is not JsonObject)
        {
            return TypedResults.BadRequest();
        }

        // 202 quickly; whatever was rejected (unknown subscription, wrong client state) is only logged.
        await service.HandleAsync(payload, lifecycle, ct);
        return TypedResults.Accepted((string?)null);
    }

    // ---- Development / Testing only, and only with the Fake provider: drive the whole loop without a tenant ----

    private sealed record DevAttachment(string Name, string ContentBase64, string? ContentType, bool IsInline, string? ContentId);

    private sealed record DevInbound(
        string Mailbox, string From, string Subject, string? FromName, string? BodyText, string? BodyHtml, string[]? To, string[]? Cc, string? InReplyTo,
        string[]? References, string? ConversationId, string? MessageId, Dictionary<string, string>? Headers, DevAttachment[]? Attachments);

    private sealed record DevCustomerReply(string Mailbox, string DraftId, string From, string BodyText, string? FromName);

    private static void MapDevelopment(IEndpointRouteBuilder app)
    {
        var dev = app.MapGroup("/api/dev/mail").WithTags("Mail (development)");

        dev.MapPost("inbound", async (DevInbound body, FakeMailProvider fake, MailDbContext db, IMailJobRunner runner, CancellationToken ct) =>
            {
                var id = fake.Inject(new FakeInbound(
                    body.Mailbox, body.From, body.Subject, body.FromName, body.BodyText, body.BodyHtml, body.To, body.Cc, body.InReplyTo, body.References,
                    body.ConversationId, body.MessageId, body.Headers,
                    [.. (body.Attachments ?? []).Select(a => new FakeAttachment(a.Name, Convert.FromBase64String(a.ContentBase64), a.ContentType, a.IsInline, a.ContentId))]));
                await SyncAsync(db, runner, body.Mailbox, ct);
                return TypedResults.Ok(new { providerMessageId = id });
            })
            .RequirePermission(PermissionCodes.PlatformUsersManage, departmentRouteParameter: null);

        // A customer answering one of our sent mails: builds the headers a mail client would set and delivers it.
        dev.MapPost("customer-reply", async (DevCustomerReply body, FakeMailProvider fake, MailDbContext db, IMailJobRunner runner, CancellationToken ct) =>
            {
                var sent = fake.SentMail(body.Mailbox).FirstOrDefault(s => s.DraftId == body.DraftId);
                if (sent is null)
                {
                    return Results.NotFound();
                }

                var id = fake.Inject(FakeMailProvider.CustomerReplyTo(sent, body.From, body.BodyText, body.FromName));
                await SyncAsync(db, runner, body.Mailbox, ct);
                return Results.Ok(new { providerMessageId = id });
            })
            .RequirePermission(PermissionCodes.PlatformUsersManage, departmentRouteParameter: null);

        dev.MapGet("sent", (string mailbox, FakeMailProvider fake) =>
                TypedResults.Ok(fake.SentMail(mailbox).Select(s => new
                {
                    s.DraftId, s.InternetMessageId, s.InReplyTo, s.Subject, s.SentAt, s.Token,
                    to = s.To.Select(a => a.Address), cc = s.Cc.Select(a => a.Address), bodyHtml = s.BodyHtml,
                })))
            .RequirePermission(PermissionCodes.PlatformUsersManage, departmentRouteParameter: null);

        dev.MapPost("dispatch", async (IMailJobRunner runner, CancellationToken ct) =>
            {
                await runner.RunDispatchAsync(ct);
                return TypedResults.NoContent();
            })
            .RequirePermission(PermissionCodes.PlatformUsersManage, departmentRouteParameter: null);
    }

    private static async Task SyncAsync(MailDbContext db, IMailJobRunner runner, string address, CancellationToken ct)
    {
        var normalised = address.Trim().ToLowerInvariant();
        var id = await db.Mailboxes.AsNoTracking().Where(m => m.AddressNormalised == normalised).Select(m => (Guid?)m.Id).SingleOrDefaultAsync(ct);
        if (id is { } mailboxId)
        {
            await runner.RunSyncAsync(mailboxId, ct);
        }
    }
}
