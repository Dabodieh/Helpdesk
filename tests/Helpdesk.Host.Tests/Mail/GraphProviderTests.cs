using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Azure.Core;
using Helpdesk.Modules.Mail.Providers;

namespace Helpdesk.Host.Tests.Mail;

/// <summary>
/// Contract tests of the Graph provider against hand-built payloads in the DOCUMENTED shapes (no tenant). They prove request
/// construction, response mapping and error classification; they do NOT prove Exchange's behaviour (see the runbook, E-n).
/// </summary>
public sealed class GraphProviderTests
{
    private static readonly MailboxRef Mailbox = new(Guid.NewGuid(), "desk@helpdesk.test", "11111111-2222-3333-4444-555555555555");
    private static readonly Uri BaseUri = new("https://graph.test/v1.0/");

    private sealed class FixedCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) => new("test-token", DateTimeOffset.UtcNow.AddHours(1));

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) => new(GetToken(requestContext, cancellationToken));
    }

    private sealed record Recorded(HttpMethod Method, Uri Uri, string Body, IReadOnlyDictionary<string, string> Headers);

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpResponseMessage>> _responses = new();

        public List<Recorded> Requests { get; } = [];

        public ScriptedHandler Json(HttpStatusCode status, object? body = null, Action<HttpResponseMessage>? configure = null)
        {
            _responses.Enqueue(() =>
            {
                var response = new HttpResponseMessage(status);
                if (body is not null)
                {
                    response.Content = new StringContent(body is string s ? s : System.Text.Json.JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
                }

                configure?.Invoke(response);
                return response;
            });
            return this;
        }

        public ScriptedHandler Bytes(byte[] content)
        {
            _responses.Enqueue(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) });
            return this;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join("; ", h.Value), StringComparer.OrdinalIgnoreCase);
            Requests.Add(new Recorded(request.Method, request.RequestUri!, request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken), headers));
            return _responses.Dequeue()();
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static (MicrosoftGraphMailProvider Provider, ScriptedHandler Handler) Create()
    {
        var handler = new ScriptedHandler();
        return (new MicrosoftGraphMailProvider(new Factory(handler), new FixedCredential(), BaseUri), handler);
    }

    private static string Path(Recorded r) => Uri.UnescapeDataString(r.Uri.PathAndQuery);

    [Fact]
    public async Task The_registration_probe_reads_the_inbox_folder_and_classifies_an_inaccessible_mailbox_as_permanent()
    {
        var (provider, handler) = Create();
        handler.Json(HttpStatusCode.OK, new { id = "inbox" }).Json(HttpStatusCode.Forbidden, new { error = new { code = "ErrorAccessDenied" } }).Json(HttpStatusCode.ServiceUnavailable, new { error = new { code = "x" } });

        await provider.ProbeMailboxAsync(Mailbox, CancellationToken.None);
        Assert.Equal(HttpMethod.Get, handler.Requests[0].Method);
        Assert.Contains("/users/11111111-2222-3333-4444-555555555555/mailFolders/inbox", Path(handler.Requests[0]), StringComparison.Ordinal);

        Assert.Equal("graph_403", (await Assert.ThrowsAsync<PermanentProviderException>(() => provider.ProbeMailboxAsync(Mailbox, CancellationToken.None))).Code);
        await Assert.ThrowsAsync<TransientProviderException>(() => provider.ProbeMailboxAsync(Mailbox, CancellationToken.None));
    }

    [Fact]
    public async Task Subscription_is_created_without_resource_data_with_lifecycle_url_client_state_and_immutable_ids()
    {
        var (provider, handler) = Create();
        handler.Json(HttpStatusCode.Created, new { id = "sub-1", expirationDateTime = "2026-10-08T10:00:00Z" });
        var expires = new DateTimeOffset(2026, 10, 8, 10, 0, 0, TimeSpan.Zero);

        var created = await provider.CreateSubscriptionAsync(Mailbox, new SubscriptionRequest("https://h.test/api/mail/graph/notifications", "https://h.test/api/mail/graph/lifecycle", "state-secret", expires), default);

        Assert.Equal("sub-1", created.Id);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/v1.0/subscriptions", request.Uri.AbsolutePath);
        Assert.Equal("Bearer test-token", request.Headers["Authorization"]);
        Assert.Contains("IdType=\"ImmutableId\"", request.Headers["Prefer"], StringComparison.Ordinal);
        var body = JsonNode.Parse(request.Body)!;
        Assert.Equal("created,updated,deleted", body["changeType"]!.GetValue<string>());
        Assert.Equal("https://h.test/api/mail/graph/lifecycle", body["lifecycleNotificationUrl"]!.GetValue<string>());
        Assert.Equal($"users/{Mailbox.Reference}/mailFolders('inbox')/messages", body["resource"]!.GetValue<string>());
        Assert.Equal("state-secret", body["clientState"]!.GetValue<string>());
        Assert.Null(body["includeResourceData"]); // no bodies through webhooks (ADR-011)
    }

    [Fact]
    public async Task Renewal_patches_only_the_expiry()
    {
        var (provider, handler) = Create();
        handler.Json(HttpStatusCode.OK, new { id = "sub-1", expirationDateTime = "2026-10-09T10:00:00Z" });

        var expiry = await provider.RenewSubscriptionAsync(Mailbox, "sub-1", new DateTimeOffset(2026, 10, 9, 10, 0, 0, TimeSpan.Zero), default);

        Assert.Equal(new DateTimeOffset(2026, 10, 9, 10, 0, 0, TimeSpan.Zero), expiry);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Patch, request.Method);
        Assert.Equal("/v1.0/subscriptions/sub-1", request.Uri.AbsolutePath);
        Assert.Equal(["expirationDateTime"], JsonNode.Parse(request.Body)!.AsObject().Select(p => p.Key));
    }

    [Fact]
    public async Task Delta_follows_next_links_flags_removals_returns_the_delta_link_and_filters_the_first_call_by_date()
    {
        var (provider, handler) = Create();
        handler
            .Json(HttpStatusCode.OK, "{\"value\":[{\"id\":\"m1\"},{\"id\":\"m2\"}],\"@odata.nextLink\":\"https://graph.test/v1.0/next-page\"}")
            .Json(HttpStatusCode.OK, "{\"value\":[{\"id\":\"m3\",\"@removed\":{\"reason\":\"deleted\"}}],\"@odata.deltaLink\":\"https://graph.test/v1.0/delta-final?token=abc\"}");

        var result = await provider.FetchChangesAsync(Mailbox, null, new DateTimeOffset(2026, 10, 1, 8, 30, 0, TimeSpan.Zero), default);

        Assert.Equal(["m1", "m2", "m3"], result.Changes.Select(c => c.ProviderMessageId));
        Assert.True(result.Changes.Single(c => c.ProviderMessageId == "m3").Removed);
        Assert.Equal("https://graph.test/v1.0/delta-final?token=abc", result.DeltaToken);
        Assert.Contains("/mailFolders/inbox/messages/delta?$select=id&$filter=receivedDateTime ge 2026-10-01T08:30:00Z", Path(handler.Requests[0]), StringComparison.Ordinal);
        Assert.Equal("https://graph.test/v1.0/next-page", handler.Requests[1].Uri.ToString());
        Assert.Contains("odata.maxpagesize=100", handler.Requests[0].Headers["Prefer"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_stored_delta_link_is_called_as_is_and_a_gone_state_means_reset()
    {
        var (provider, handler) = Create();
        handler.Json(HttpStatusCode.OK, "{\"value\":[],\"@odata.deltaLink\":\"https://graph.test/v1.0/delta-2\"}");
        await provider.FetchChangesAsync(Mailbox, "https://graph.test/v1.0/delta-1?token=t", DateTimeOffset.UtcNow, default);
        Assert.Equal("https://graph.test/v1.0/delta-1?token=t", handler.Requests[0].Uri.ToString());

        var (second, gone) = Create();
        gone.Json(HttpStatusCode.Gone, new { error = new { code = "syncStateNotFound", message = "private text" } });
        await Assert.ThrowsAsync<DeltaResetException>(() => second.FetchChangesAsync(Mailbox, "https://graph.test/v1.0/old", DateTimeOffset.UtcNow, default));
    }

    [Fact]
    public async Task Links_pointing_outside_the_configured_graph_host_are_never_followed()
    {
        var (provider, handler) = Create();

        var ex = await Assert.ThrowsAsync<PermanentProviderException>(() => provider.FetchChangesAsync(Mailbox, "https://evil.test/steal?token=1", DateTimeOffset.UtcNow, default));

        Assert.Equal("link_outside_graph_host", ex.Code);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task A_message_is_mapped_to_the_normalised_email_with_headers_attachments_and_sender_fallback()
    {
        var (provider, handler) = Create();
        handler
            .Json(HttpStatusCode.OK, new
            {
                id = "AAMk-1",
                internetMessageId = "<abc@client.test>",
                conversationId = "conv-1",
                subject = "Printer",
                sender = new { emailAddress = new { address = "Boss@Client.Test", name = "Boss" } },
                toRecipients = new[] { new { emailAddress = new { address = "desk@helpdesk.test", name = "Desk" } } },
                ccRecipients = new[] { new { emailAddress = new { address = "cc@client.test" } } },
                receivedDateTime = "2026-10-05T10:00:00Z",
                body = new { contentType = "html", content = "<p>Hi</p>" },
                isDraft = false,
                hasAttachments = true,
                internetMessageHeaders = new object[]
                {
                    new { name = "In-Reply-To", value = "<prev@x.test>" },
                    new { name = "References", value = "<a@x.test> <prev@x.test>" },
                    new { name = "Auto-Submitted", value = "no" },
                },
            })
            .Json(HttpStatusCode.OK, new { value = new[] { new { id = "att-1", name = "a.pdf", contentType = "application/pdf", size = 123, isInline = false, contentId = (string?)null } } });

        var email = (await provider.GetMessageAsync(Mailbox, "AAMk-1", default))!;

        Assert.Equal("<abc@client.test>", email.InternetMessageId);
        Assert.Equal("Boss@Client.Test", email.From!.Address);
        Assert.Equal("<p>Hi</p>", email.BodyHtml);
        Assert.Null(email.BodyText);
        Assert.Equal("<prev@x.test>", email.Headers["in-reply-to"]);
        Assert.Equal("cc@client.test", Assert.Single(email.Cc).Address);
        var attachment = Assert.Single(email.Attachments);
        Assert.Equal(("att-1", "a.pdf", 123L), (attachment.ProviderAttachmentId, attachment.FileName, attachment.Size));
        Assert.Contains("$select=", handler.Requests[0].Uri.Query, StringComparison.Ordinal);
        Assert.Contains("internetMessageHeaders", Uri.UnescapeDataString(handler.Requests[0].Uri.Query), StringComparison.Ordinal);
        Assert.Contains("outlook.body-content-type=\"html\"", handler.Requests[0].Headers["Prefer"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_message_is_null_and_attachment_bytes_come_from_the_value_endpoint()
    {
        var (provider, handler) = Create();
        handler.Json(HttpStatusCode.NotFound, new { error = new { code = "ErrorItemNotFound" } });
        Assert.Null(await provider.GetMessageAsync(Mailbox, "gone", default));

        var (second, bytes) = Create();
        bytes.Bytes([1, 2, 3]);
        Assert.Equal([1, 2, 3], await second.GetAttachmentAsync(Mailbox, "m 1", "a/1", default));
        Assert.EndsWith("/messages/m%201/attachments/a%2F1/$value", bytes.Requests[0].Uri.AbsolutePath + "", StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_reply_uses_create_reply_with_recipients_and_html_and_no_unvalidated_custom_header()
    {
        var (provider, handler) = Create();
        handler.Json(HttpStatusCode.Created, new { id = "draft-1", internetMessageId = "<draft@helpdesk.test>" });

        var draft = await provider.CreateDraftAsync(
            Mailbox, new OutboundContent("AAMk-1", "Re: Printer", "<p>Hello</p>", [new EmailAddress("req@client.test", "Req")], [new EmailAddress("cc@client.test", null)], "tok"), default);

        Assert.Equal(("draft-1", "<draft@helpdesk.test>"), (draft.DraftId, draft.InternetMessageId));
        var request = Assert.Single(handler.Requests);
        Assert.EndsWith("/messages/AAMk-1/createReply", request.Uri.AbsolutePath, StringComparison.Ordinal);
        var message = JsonNode.Parse(request.Body)!["message"]!;
        Assert.Equal("HTML", message["body"]!["contentType"]!.GetValue<string>());
        Assert.Equal("req@client.test", message["toRecipients"]![0]!["emailAddress"]!["address"]!.GetValue<string>());
        Assert.Null(message["internetMessageHeaders"]); // creation-time x- headers are only documented for new drafts (E-28)
    }

    [Fact]
    public async Task A_new_message_is_a_draft_with_the_x_helpdesk_token_header_then_sent_by_id()
    {
        var (provider, handler) = Create();
        handler.Json(HttpStatusCode.Created, new { id = "draft-2", internetMessageId = "<d2@helpdesk.test>" }).Json(HttpStatusCode.Accepted);

        var draft = await provider.CreateDraftAsync(Mailbox, new OutboundContent(null, "Hello [IT-1]", "<p>x</p>", [new EmailAddress("req@client.test", null)], [], "tok-123"), default);
        await provider.SendDraftAsync(Mailbox, draft.DraftId, default);

        var create = JsonNode.Parse(handler.Requests[0].Body)!;
        Assert.EndsWith($"/users/{Mailbox.Reference}/messages", handler.Requests[0].Uri.AbsolutePath, StringComparison.Ordinal);
        Assert.Equal("x-helpdesk-token", create["internetMessageHeaders"]![0]!["name"]!.GetValue<string>());
        Assert.Equal("tok-123", create["internetMessageHeaders"]![0]!["value"]!.GetValue<string>());
        Assert.EndsWith("/messages/draft-2/send", handler.Requests[1].Uri.AbsolutePath, StringComparison.Ordinal);
        Assert.Equal(HttpMethod.Post, handler.Requests[1].Method);
    }

    [Fact]
    public async Task The_sent_copy_is_found_by_the_draft_id_only_once_it_is_no_longer_a_draft()
    {
        var (provider, handler) = Create();
        handler
            .Json(HttpStatusCode.OK, new { id = "draft-3", internetMessageId = "<d3@x>", isDraft = true })
            .Json(HttpStatusCode.OK, new { id = "draft-3", internetMessageId = "<final@x>", isDraft = false })
            .Json(HttpStatusCode.NotFound, new { error = new { code = "ErrorItemNotFound" } });

        Assert.Null(await provider.FindSentCopyAsync(Mailbox, "draft-3", default));
        Assert.Equal("<final@x>", (await provider.FindSentCopyAsync(Mailbox, "draft-3", default))!.InternetMessageId);
        Assert.Null(await provider.FindSentCopyAsync(Mailbox, "draft-3", default));
    }

    [Fact]
    public async Task Throttling_and_server_errors_are_transient_and_other_client_errors_are_permanent_without_leaking_provider_text()
    {
        var (provider, handler) = Create();
        handler
            .Json(HttpStatusCode.TooManyRequests, new { error = new { code = "ApplicationThrottled", message = "secret mailbox detail" } }, r => r.Headers.RetryAfter = new(TimeSpan.FromSeconds(42)))
            .Json(HttpStatusCode.ServiceUnavailable, new { error = new { code = "MailboxInfoStale" } })
            .Json(HttpStatusCode.Forbidden, new { error = new { code = "ErrorAccessDenied", message = "customer-subject-should-not-appear" } });

        var throttled = await Assert.ThrowsAsync<TransientProviderException>(() => provider.SendDraftAsync(Mailbox, "d", default));
        Assert.Equal(TimeSpan.FromSeconds(42), throttled.RetryAfter);
        Assert.DoesNotContain("secret", throttled.Message + throttled.Code, StringComparison.Ordinal);

        await Assert.ThrowsAsync<TransientProviderException>(() => provider.SendDraftAsync(Mailbox, "d", default));

        var denied = await Assert.ThrowsAsync<PermanentProviderException>(() => provider.SendDraftAsync(Mailbox, "d", default));
        Assert.Equal("graph_403", denied.Code);
        Assert.DoesNotContain("customer-subject", denied.Message + denied.Code, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Identifiers_are_escaped_in_resource_paths()
    {
        var (provider, handler) = Create();
        handler.Json(HttpStatusCode.OK, "{\"value\":[],\"@odata.deltaLink\":\"https://graph.test/v1.0/d\"}");

        await provider.FetchChangesAsync(new MailboxRef(Guid.NewGuid(), "x", "a b/c"), null, DateTimeOffset.UtcNow, default);

        Assert.Contains("/users/a%20b%2Fc/mailFolders/inbox/messages/delta", handler.Requests[0].Uri.AbsolutePath, StringComparison.Ordinal);
    }
}
