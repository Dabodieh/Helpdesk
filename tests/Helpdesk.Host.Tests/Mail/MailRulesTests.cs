using Helpdesk.Modules.Attachments.Services;
using Helpdesk.Modules.Mail.Providers;
using Helpdesk.Modules.Mail.Services;
using Microsoft.Extensions.Options;

namespace Helpdesk.Host.Tests.Mail;

/// <summary>Pure rules: Message-ID normalisation, subject handling, HTML to text, the noise guard, subject hints and the S3 store contract.</summary>
public sealed class MailRulesTests
{
    [Theory]
    [InlineData("<ABC@Example.COM>", "abc@example.com")]
    [InlineData("  <abc@example.com>  ", "abc@example.com")]
    [InlineData("abc@example.com", "abc@example.com")]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("<>", null)]
    public void Message_ids_are_normalised(string input, string? expected) =>
        Assert.Equal(expected, MailText.NormaliseMessageId(input));

    [Fact]
    public void Reference_headers_yield_ids_oldest_first_without_duplicates()
    {
        Assert.Equal(["a@x", "b@x", "c@x"], MailText.ParseMessageIds("<A@x>\r\n <b@x> <C@X> <a@x>"));
        Assert.Empty(MailText.ParseMessageIds(null));
        Assert.Equal(["only@x"], MailText.ParseMessageIds("only@x"));
    }

    [Theory]
    [InlineData("Re: Re: FW: Printer jam", "Printer jam")]
    [InlineData("AW: Drucker", "Drucker")]
    [InlineData("  line\r\nbreak  ", "line break")]
    [InlineData("", "(no subject)")]
    [InlineData(null, "(no subject)")]
    public void Ticket_subjects_are_single_line_without_reply_prefixes(string? input, string expected) =>
        Assert.Equal(expected, MailText.TicketSubject(input));

    [Fact]
    public void Very_long_subjects_are_capped_at_the_ticket_limit() =>
        Assert.Equal(200, MailText.TicketSubject(new string('s', 500)).Length);

    [Fact]
    public void Html_becomes_readable_text_without_scripts_or_styles()
    {
        var text = MailText.HtmlToText("<html><head><style>p{color:red}</style></head><body><p>Hello</p><p>World<br>Line two</p><script>evil()</script></body></html>");

        Assert.Equal("Hello\nWorld\nLine two", text);
    }

    [Fact]
    public void Bodies_are_capped_with_a_visible_marker_and_never_empty()
    {
        var capped = MailText.BodyTextOf(new string('x', 30000), null);
        Assert.Equal(MailText.MaxTicketBody, capped.Length);
        Assert.EndsWith("[message truncated]", capped, StringComparison.Ordinal);
        Assert.Equal("(empty message)", MailText.BodyTextOf("  ", null));
        Assert.DoesNotContain('\0', MailText.Tidy("a\0b"));
    }

    [Fact]
    public void Plain_text_replies_are_html_escaped_for_sending() =>
        Assert.Equal("a &lt;b&gt; &amp; c<br>\nnext", MailText.TextToHtml("a <b> & c\nnext"));

    [Fact]
    public void The_token_is_found_in_a_quoted_body() =>
        Assert.Equal("0123456789abcdef0123456789abcdef", MailText.ExtractToken("> earlier text\n> [hd:0123456789abcdef0123456789abcdef]"));

    [Theory]
    [InlineData("Re: [IT-42] Printer", new[] { "IT-42" })]
    [InlineData("it-7 and IT-8", new[] { "IT-7", "IT-8" })]
    [InlineData("HR-9 is another department", new string[0])]
    [InlineData("ITEM-3 and WIT-2", new string[0])]
    [InlineData("no reference", new string[0])]
    public void Subject_hints_only_name_references_of_the_receiving_department(string subject, string[] expected) =>
        Assert.Equal(expected, CorrelationEngine.SubjectReferences(subject, "IT"));

    private static InboundEmail Email(string from, IReadOnlyDictionary<string, string>? headers = null, bool draft = false) =>
        new("id", "<x@y>", null, "s", new EmailAddress(from, null), [], [], DateTimeOffset.UtcNow, "b", null, headers ?? new Dictionary<string, string>(), draft, []);

    private static readonly HashSet<string> Own = new(StringComparer.Ordinal) { "desk@helpdesk.test" };

    [Theory]
    [InlineData("Auto-Submitted", "auto-generated", "auto_submitted")]
    [InlineData("auto-submitted", "AUTO-REPLIED", "auto_submitted")]
    [InlineData("Precedence", "Junk", "precedence_bulk")]
    [InlineData("List-Id", "<list.example>", "mailing_list")]
    [InlineData("X-Auto-Response-Suppress", "OOF", "auto_response_suppress")]
    public void The_guard_recognises_machine_mail(string header, string value, string reason) =>
        Assert.Equal(reason, MailGuard.Evaluate(Email("p@c.test", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [header] = value }), Own));

    [Fact]
    public void The_guard_lets_normal_mail_through_and_stops_loops_drafts_and_bounces()
    {
        Assert.Null(MailGuard.Evaluate(Email("person@c.test", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Auto-Submitted"] = "no" }), Own));
        Assert.Equal("own_address", MailGuard.Evaluate(Email("DESK@helpdesk.test"), Own));
        Assert.Equal("draft", MailGuard.Evaluate(Email("p@c.test", draft: true), Own));
        Assert.Equal("bounce", MailGuard.Evaluate(Email("Postmaster@mx.test"), Own));
        Assert.Equal("no_sender", MailGuard.Evaluate(Email(""), Own));
    }

    [Fact]
    public void Executable_html_and_remote_images_are_removed_by_the_sanitiser()
    {
        var safe = MailText.SanitiseHtml("<div onclick=\"x()\"><iframe src=\"https://e.test\"></iframe><img src=\"cid:logo\"><img src=\"http://t.test/p.gif\"><style>@import url(x)</style>ok</div>");

        Assert.DoesNotContain("onclick", safe, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<iframe", safe, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("t.test", safe, StringComparison.Ordinal);
        Assert.Contains("cid:logo", safe, StringComparison.Ordinal);
        Assert.Contains("ok", safe, StringComparison.Ordinal);
    }

    /// <summary>
    /// The S3 store against a REAL S3-compatible endpoint (local SeaweedFS: `docker compose --profile storage up -d`). Runs only when
    /// HELPDESK_TEST_S3_ENDPOINT, HELPDESK_TEST_S3_ACCESS_KEY and HELPDESK_TEST_S3_SECRET_KEY are set, so CI without storage stays green.
    /// </summary>
    [Fact]
    public async Task The_s3_object_store_round_trips_bytes_against_a_real_endpoint_when_one_is_configured()
    {
        var endpoint = Environment.GetEnvironmentVariable("HELPDESK_TEST_S3_ENDPOINT");
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return; // no storage available in this run
        }

        using var store = new S3ObjectStore(Options.Create(new StorageOptions
        {
            Provider = "S3",
            S3 = new S3Options
            {
                Endpoint = endpoint,
                Bucket = "helpdesk-test-" + Guid.NewGuid().ToString("N")[..8],
                AccessKey = Environment.GetEnvironmentVariable("HELPDESK_TEST_S3_ACCESS_KEY"),
                SecretKey = Environment.GetEnvironmentVariable("HELPDESK_TEST_S3_SECRET_KEY"),
            },
        }));
        var key = $"{Guid.NewGuid():N}/{Guid.NewGuid():N}/{Guid.NewGuid():N}";
        var content = new byte[100_000];
        Random.Shared.NextBytes(content);

        await store.PutAsync(key, content, "application/octet-stream");
        await using var read = (await store.OpenReadAsync(key))!;
        using var buffer = new MemoryStream();
        await read.CopyToAsync(buffer);

        Assert.Equal(content, buffer.ToArray());
        Assert.Null(await store.OpenReadAsync("does/not/exist"));
    }
}
