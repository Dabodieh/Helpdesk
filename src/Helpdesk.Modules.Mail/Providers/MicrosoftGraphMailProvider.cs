using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;
using Azure.Identity;
using Helpdesk.Modules.Mail.Services;
using Microsoft.Extensions.Options;

namespace Helpdesk.Modules.Mail.Providers;

/// <summary>
/// Microsoft Graph (v1.0) implementation using the application identity (client credentials). It only calls the configured Graph
/// base URL (no SSRF surface: delta links are checked against it) and never logs message content or tokens.
/// <para>
/// STATUS: written against the DOCUMENTED Graph contract (docs/architecture/EMAIL_ARCHITECTURE.md, "Graph spike findings") and
/// tested with recorded-shape payloads. It has NOT been validated against a real Microsoft 365 tenant: that is the open gate of
/// ADR-011 / docs/guides/GRAPH_TENANT_VALIDATION.md. Every `E-n` marker below names the experiment that must confirm the assumption.
/// </para>
/// </summary>
internal sealed class MicrosoftGraphMailProvider : IMailProvider
{
    public const string HttpClientName = "graph";
    private const string Scope = "https://graph.microsoft.com/.default";
    private const int MaxDeltaPages = 200;

    private readonly IHttpClientFactory _httpClients;
    private readonly TokenCredential _credential;
    private readonly Uri _baseUri;

    public MicrosoftGraphMailProvider(IHttpClientFactory httpClients, IOptions<MailOptions> options)
    {
        _httpClients = httpClients;
        var graph = options.Value.Graph;
        _baseUri = new Uri(graph.BaseUrl.TrimEnd('/') + "/");
        _credential = CreateCredential(graph);
    }

    /// <summary>Test seam: a provider with a fixed credential and a caller-supplied client factory.</summary>
    internal MicrosoftGraphMailProvider(IHttpClientFactory httpClients, TokenCredential credential, Uri baseUri)
    {
        _httpClients = httpClients;
        _credential = credential;
        _baseUri = baseUri;
    }

    public string Kind => Contracts.MailProviderKinds.Graph;

    private static TokenCredential CreateCredential(GraphOptions graph)
    {
        if (string.IsNullOrWhiteSpace(graph.TenantId) || string.IsNullOrWhiteSpace(graph.ClientId))
        {
            throw new InvalidOperationException("Mail:Graph:TenantId and ClientId are required for the Graph provider.");
        }

        if (!string.IsNullOrWhiteSpace(graph.CertificatePath))
        {
            var certificate = X509CertificateLoader.LoadPkcs12FromFile(graph.CertificatePath, graph.CertificatePassword);
            return new ClientCertificateCredential(graph.TenantId, graph.ClientId, certificate);
        }

        if (string.IsNullOrWhiteSpace(graph.ClientSecret))
        {
            throw new InvalidOperationException("Mail:Graph needs a ClientSecret or a CertificatePath.");
        }

        return new ClientSecretCredential(graph.TenantId, graph.ClientId, graph.ClientSecret);
    }

    // ---- subscriptions (E-30..E-35) ----

    public async Task<ProviderSubscription> CreateSubscriptionAsync(MailboxRef mailbox, SubscriptionRequest request, CancellationToken cancellationToken)
    {
        var body = new JsonObject
        {
            ["changeType"] = "created,updated,deleted",
            ["notificationUrl"] = request.NotificationUrl,
            ["lifecycleNotificationUrl"] = request.LifecycleUrl,
            ["resource"] = $"users/{Escape(mailbox.Reference)}/mailFolders('inbox')/messages",
            ["expirationDateTime"] = request.ExpiresAt.UtcDateTime.ToString("o", CultureInfo.InvariantCulture),
            ["clientState"] = request.ClientState,
        };
        using var response = await SendAsync(HttpMethod.Post, "subscriptions", body, immutableIds: true, cancellationToken);
        var json = await ReadJsonAsync(response, cancellationToken);
        return new ProviderSubscription(
            json["id"]!.GetValue<string>(),
            DateTimeOffset.Parse(json["expirationDateTime"]!.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal));
    }

    public async Task<DateTimeOffset> RenewSubscriptionAsync(MailboxRef mailbox, string subscriptionId, DateTimeOffset newExpiry, CancellationToken cancellationToken)
    {
        var body = new JsonObject { ["expirationDateTime"] = newExpiry.UtcDateTime.ToString("o", CultureInfo.InvariantCulture) };
        using var response = await SendAsync(HttpMethod.Patch, $"subscriptions/{Escape(subscriptionId)}", body, immutableIds: false, cancellationToken);
        var json = await ReadJsonAsync(response, cancellationToken);
        return DateTimeOffset.Parse(json["expirationDateTime"]!.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
    }

    public async Task DeleteSubscriptionAsync(MailboxRef mailbox, string subscriptionId, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await SendAsync(HttpMethod.Delete, $"subscriptions/{Escape(subscriptionId)}", null, immutableIds: false, cancellationToken);
        }
        catch (PermanentProviderException ex) when (ex.Code == "graph_404")
        {
            // Already gone.
        }
    }

    // ---- registration probe (D10; scoping proof is D-16 E-2/E-3) ----

    public async Task<ConnectionCheck> CheckConnectionAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _credential.GetTokenAsync(new TokenRequestContext([Scope]), cancellationToken);
            return new ConnectionCheck(true, "ok");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (AuthenticationFailedException)
        {
            return new ConnectionCheck(false, "authentication"); // wrong tenant, client id, secret or certificate: the provider's text is deliberately not passed on
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TimeoutException or CredentialUnavailableException)
        {
            return new ConnectionCheck(false, "unavailable");
        }
    }

    public async Task ProbeMailboxAsync(MailboxRef mailbox, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, $"users/{Escape(mailbox.Reference)}/mailFolders/inbox?$select=id", null, immutableIds: false, cancellationToken);
    }

    // ---- delta (E-7..E-9) ----

    public async Task<DeltaResult> FetchChangesAsync(MailboxRef mailbox, string? deltaToken, DateTimeOffset notBefore, CancellationToken cancellationToken)
    {
        // The first call may filter on receivedDateTime (documented for delta); the filter is encoded into the delta link afterwards.
        var next = deltaToken ?? $"users/{Escape(mailbox.Reference)}/mailFolders/inbox/messages/delta?$select=id&$filter=receivedDateTime ge {notBefore.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)}";
        var changes = new List<MailChange>();
        for (var page = 0; page < MaxDeltaPages; page++)
        {
            JsonNode json;
            try
            {
                using var response = await SendAsync(HttpMethod.Get, next, null, immutableIds: true, cancellationToken, "odata.maxpagesize=100");
                json = await ReadJsonAsync(response, cancellationToken);
            }
            catch (PermanentProviderException ex) when (ex.Code == "graph_410")
            {
                throw new DeltaResetException();
            }

            foreach (var item in json["value"]?.AsArray() ?? [])
            {
                var id = item?["id"]?.GetValue<string>();
                if (id is not null)
                {
                    changes.Add(new MailChange(id, item!["@removed"] is not null));
                }
            }

            if (json["@odata.nextLink"]?.GetValue<string>() is { } nextLink)
            {
                next = nextLink;
                continue;
            }

            return new DeltaResult(changes, json["@odata.deltaLink"]?.GetValue<string>() ?? throw new PermanentProviderException("delta_without_link"));
        }

        throw new TransientProviderException("delta_too_many_pages");
    }

    // ---- reading mail (E-1, E-13, E-19) ----

    public async Task<InboundEmail?> GetMessageAsync(MailboxRef mailbox, string providerMessageId, CancellationToken cancellationToken)
    {
        const string select = "id,internetMessageId,conversationId,subject,from,sender,toRecipients,ccRecipients,receivedDateTime,body,isDraft,hasAttachments,internetMessageHeaders";
        JsonNode json;
        try
        {
            using var response = await SendAsync(
                HttpMethod.Get, $"users/{Escape(mailbox.Reference)}/messages/{Escape(providerMessageId)}?$select={select}", null, immutableIds: true,
                cancellationToken, preferExtra: "outlook.body-content-type=\"html\"");
            json = await ReadJsonAsync(response, cancellationToken);
        }
        catch (PermanentProviderException ex) when (ex.Code == "graph_404")
        {
            return null;
        }

        var attachments = new List<InboundAttachmentInfo>();
        if (json["hasAttachments"]?.GetValue<bool>() == true)
        {
            using var list = await SendAsync(
                HttpMethod.Get, $"users/{Escape(mailbox.Reference)}/messages/{Escape(providerMessageId)}/attachments?$select=id,name,contentType,size,isInline,contentId",
                null, immutableIds: true, cancellationToken);
            foreach (var a in (await ReadJsonAsync(list, cancellationToken))["value"]?.AsArray() ?? [])
            {
                if (a?["id"]?.GetValue<string>() is { } id)
                {
                    attachments.Add(new InboundAttachmentInfo(
                        id,
                        a["name"]?.GetValue<string>() ?? "attachment",
                        a["contentType"]?.GetValue<string>(),
                        a["size"]?.GetValue<long>() ?? 0,
                        a["isInline"]?.GetValue<bool>() ?? false,
                        a["contentId"]?.GetValue<string>()));
                }
            }
        }

        var bodyType = json["body"]?["contentType"]?.GetValue<string>();
        var bodyContent = json["body"]?["content"]?.GetValue<string>();
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in json["internetMessageHeaders"]?.AsArray() ?? [])
        {
            var name = h?["name"]?.GetValue<string>();
            var value = h?["value"]?.GetValue<string>();
            if (name is not null && value is not null)
            {
                headers[name] = headers.TryGetValue(name, out var existing) ? existing + ", " + value : value;
            }
        }

        var from = ParseAddress(json["from"]) ?? ParseAddress(json["sender"]);
        return new InboundEmail(
            providerMessageId,
            json["internetMessageId"]?.GetValue<string>(),
            json["conversationId"]?.GetValue<string>(),
            json["subject"]?.GetValue<string>() ?? "",
            from,
            ParseAddresses(json["toRecipients"]),
            ParseAddresses(json["ccRecipients"]),
            DateTimeOffset.Parse(json["receivedDateTime"]?.GetValue<string>() ?? DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal),
            string.Equals(bodyType, "text", StringComparison.OrdinalIgnoreCase) ? bodyContent : null,
            string.Equals(bodyType, "html", StringComparison.OrdinalIgnoreCase) ? bodyContent : null,
            headers,
            json["isDraft"]?.GetValue<bool>() ?? false,
            attachments);
    }

    public async Task<byte[]> GetAttachmentAsync(MailboxRef mailbox, string providerMessageId, string providerAttachmentId, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(
            HttpMethod.Get, $"users/{Escape(mailbox.Reference)}/messages/{Escape(providerMessageId)}/attachments/{Escape(providerAttachmentId)}/$value",
            null, immutableIds: true, cancellationToken);
        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    // ---- sending (E-21..E-29) ----

    public async Task<DraftResult> CreateDraftAsync(MailboxRef mailbox, OutboundContent content, CancellationToken cancellationToken)
    {
        JsonObject message;
        string path;
        if (content.ReplyToProviderMessageId is { } replyTo)
        {
            // createReply with `message`: Exchange builds the reply (threading headers, E-23); we supply recipients and body.
            // The x- token header is NOT sent here (not documented for createReply, E-28); the body footer token carries it.
            path = $"users/{Escape(mailbox.Reference)}/messages/{Escape(replyTo)}/createReply";
            message = new JsonObject
            {
                ["body"] = new JsonObject { ["contentType"] = "HTML", ["content"] = content.BodyHtml },
                ["toRecipients"] = Recipients(content.To),
                ["ccRecipients"] = Recipients(content.Cc),
            };
            if (content.ReplyTo is { } replyToAddress)
            {
                message["replyTo"] = Recipients([replyToAddress]);
            }

            message = new JsonObject { ["message"] = message };
        }
        else
        {
            path = $"users/{Escape(mailbox.Reference)}/messages";
            message = new JsonObject
            {
                ["subject"] = content.Subject,
                ["body"] = new JsonObject { ["contentType"] = "HTML", ["content"] = content.BodyHtml },
                ["toRecipients"] = Recipients(content.To),
                ["ccRecipients"] = Recipients(content.Cc),
                ["internetMessageHeaders"] = Headers(content),
            };
            if (content.ReplyTo is { } replyToAddress)
            {
                message["replyTo"] = Recipients([replyToAddress]);
            }
        }

        using var response = await SendAsync(HttpMethod.Post, path, message, immutableIds: true, cancellationToken);
        var json = await ReadJsonAsync(response, cancellationToken);
        var draftId = json["id"]!.GetValue<string>();

        // Inline images are separate file attachments of the draft with isInline and the Content-ID the HTML points at (cid:...). Graph accepts a file attachment of up to
        // 3 MB in one request, which is the largest image Helpdesk stores. The body alone never produces an embedded image.
        foreach (var image in content.InlineImages ?? [])
        {
            var attachment = new JsonObject
            {
                ["@odata.type"] = "#microsoft.graph.fileAttachment",
                ["name"] = image.FileName,
                ["contentType"] = image.ContentType,
                ["contentBytes"] = Convert.ToBase64String(image.Bytes),
                ["isInline"] = true,
                ["contentId"] = image.ContentId,
            };
            using var added = await SendAsync(HttpMethod.Post, $"users/{Escape(mailbox.Reference)}/messages/{Escape(draftId)}/attachments", attachment, immutableIds: true, cancellationToken);
        }

        return new DraftResult(draftId, json["internetMessageId"]?.GetValue<string>());
    }

    public async Task SendDraftAsync(MailboxRef mailbox, string draftId, CancellationToken cancellationToken)
    {
        // 202 Accepted without a body; the item is saved to Sent Items.
        using var response = await SendAsync(HttpMethod.Post, $"users/{Escape(mailbox.Reference)}/messages/{Escape(draftId)}/send", null, immutableIds: true, cancellationToken);
    }

    public async Task<SentCopy?> FindSentCopyAsync(MailboxRef mailbox, string draftId, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await SendAsync(
                HttpMethod.Get, $"users/{Escape(mailbox.Reference)}/messages/{Escape(draftId)}?$select=id,internetMessageId,isDraft", null, immutableIds: true, cancellationToken);
            var json = await ReadJsonAsync(response, cancellationToken);
            return json["isDraft"]?.GetValue<bool>() == true ? null : new SentCopy(json["id"]!.GetValue<string>(), json["internetMessageId"]?.GetValue<string>());
        }
        catch (PermanentProviderException ex) when (ex.Code == "graph_404")
        {
            return null;
        }
    }

    // ---- plumbing ----

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string pathOrUrl, JsonNode? body, bool immutableIds, CancellationToken cancellationToken, string? preferExtra = null)
    {
        var uri = Resolve(pathOrUrl);
        using var request = new HttpRequestMessage(method, uri);
        var token = await _credential.GetTokenAsync(new TokenRequestContext([Scope]), cancellationToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        var prefer = new List<string>();
        if (immutableIds)
        {
            prefer.Add("IdType=\"ImmutableId\"");
        }

        if (preferExtra is not null)
        {
            prefer.Add(preferExtra);
        }

        foreach (var p in prefer)
        {
            request.Headers.TryAddWithoutValidation("Prefer", p);
        }

        if (body is not null)
        {
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        }

        HttpResponseMessage response;
        try
        {
            response = await _httpClients.CreateClient(HttpClientName).SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new TransientProviderException("graph_network", ex) { Ambiguous = true };
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TransientProviderException("graph_timeout", ex) { Ambiguous = true };
        }

        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        var status = (int)response.StatusCode;
        var (code, detail) = await ErrorAsync(response, cancellationToken);
        var retryAfter = response.Headers.RetryAfter?.Delta;
        response.Dispose();
        if (status is 429 or 408 || status >= 500)
        {
            throw new TransientProviderException($"graph_{status}:{code}") { RetryAfter = retryAfter, Ambiguous = status != 429 };
        }

        throw new PermanentProviderException($"graph_{status}", new InvalidOperationException(detail));
    }

    private Uri Resolve(string pathOrUrl)
    {
        if (!Uri.TryCreate(pathOrUrl, UriKind.Absolute, out var absolute))
        {
            return new Uri(_baseUri, pathOrUrl);
        }

        // Delta/next links are followed only when they point at the configured Graph host over HTTPS (or the same scheme as configured).
        if (!string.Equals(absolute.Host, _baseUri.Host, StringComparison.OrdinalIgnoreCase) || absolute.Scheme != _baseUri.Scheme)
        {
            throw new PermanentProviderException("link_outside_graph_host");
        }

        return absolute;
    }

    private static async Task<JsonNode> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonNode.ParseAsync(stream, cancellationToken: cancellationToken) ?? throw new PermanentProviderException("empty_response");
    }

    /// <summary>Only the Graph error CODE (e.g. ErrorItemNotFound) is kept: never the message text, which may echo content.</summary>
    /// <summary>Graph's <c>error.code</c> and a short <c>error.message</c> ("code: message"; the message is only ever shown to administrators, never logged with content).</summary>
    private static async Task<(string Code, string Detail)> ErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var json = await JsonNode.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            var code = json?["error"]?["code"]?.GetValue<string>() ?? "unknown";
            var message = json?["error"]?["message"]?.GetValue<string>();
            return (code, string.IsNullOrWhiteSpace(message) ? code : $"{code}: {message}");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return ("unknown", "unknown");
        }
    }

    private static string Escape(string value) => Uri.EscapeDataString(value);

    private static JsonArray Headers(OutboundContent content)
    {
        var headers = new JsonArray(new JsonObject { ["name"] = "x-helpdesk-token", ["value"] = content.Token });
        if (content.IsAutomatic)
        {
            headers.Add(new JsonObject { ["name"] = "x-auto-response-suppress", ["value"] = "All" });
        }

        return headers;
    }

    private static JsonArray Recipients(IReadOnlyList<EmailAddress> addresses) =>
        new([.. addresses.Select(a => (JsonNode)new JsonObject
        {
            ["emailAddress"] = new JsonObject { ["address"] = a.Address, ["name"] = a.Name },
        })]);

    private static EmailAddress? ParseAddress(JsonNode? node)
    {
        var address = node?["emailAddress"]?["address"]?.GetValue<string>();
        return string.IsNullOrWhiteSpace(address) ? null : new EmailAddress(address, node?["emailAddress"]?["name"]?.GetValue<string>());
    }

    private static IReadOnlyList<EmailAddress> ParseAddresses(JsonNode? array) =>
        [.. (array?.AsArray() ?? []).Select(ParseAddress).OfType<EmailAddress>()];
}
