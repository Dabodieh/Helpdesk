using System.Net;
using System.Text;
using Helpdesk.Host.Tests.Infrastructure;
using Helpdesk.Host.Tests.Ticketing;
using Helpdesk.Modules.Attachments.Services;
using Helpdesk.Modules.Mail.Providers;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Helpdesk.Host.Tests.Mail;

/// <summary>Attachments end to end (email in, validation, storage, authorised download) and the validator rules.</summary>
public sealed class AttachmentTests(HelpdeskFixture fixture) : IClassFixture<HelpdeskFixture>
{
    private static readonly byte[] Pdf = [.. "%PDF-1.7\n1 0 obj\n"u8];

    private async Task<(Dept D, Mailbox Box, Guid TicketId, List<System.Text.Json.JsonElement> Attachments)> ReceiveWithAsync(params FakeAttachment[] files)
    {
        var d = await fixture.NewDeptAsync();
        var box = await fixture.CreateMailboxAsync(d);
        var subject = $"Files {Unique.Id()}";
        await fixture.ReceiveAsync(box, MailKit.Mail(MailKit.NewSender(), subject) with { Attachments = files });
        var ticketId = Assert.Single(await d.Agent.TicketsAsync(d, subject)).GetProperty("id").GetGuid();
        var message = Assert.Single(await d.Agent.MessagesAsync(ticketId));
        return (d, box, ticketId, [.. message.GetProperty("attachments").EnumerateArray()]);
    }

    [Fact]
    public async Task A_valid_attachment_is_stored_and_downloaded_as_an_attachment_never_inline()
    {
        var s = await ReceiveWithAsync(new FakeAttachment("Report.PDF", Pdf, "application/x-evil"));

        var info = Assert.Single(s.Attachments);
        Assert.Equal("Stored", info.Str("state"));
        Assert.Equal("application/pdf", info.Str("contentType")); // our table decides, not the sender's header
        Assert.True(info.GetProperty("downloadable").GetBoolean());

        var response = await s.D.Agent.Client.Http.GetAsync($"/api/tickets/{s.TicketId}/attachments/{info.GetProperty("id").GetGuid()}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Pdf, await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal("Report.PDF", response.Content.Headers.ContentDisposition.FileName?.Trim('"') ?? response.Content.Headers.ContentDisposition.FileNameStar);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
    }

    [Theory]
    [InlineData("setup.exe", "MZ-not-really", "blocked_type")]
    [InlineData("invoice.exe.pdf", "%PDF-1.4", "blocked_type")]
    [InlineData("script.ps1", "Write-Host hi", "blocked_type")]
    [InlineData("photo.png", "this is not a png", "content_mismatch")]
    [InlineData("empty.txt", "", "empty")]
    public async Task Dangerous_or_inconsistent_attachments_are_recorded_as_blocked_and_never_stored_or_downloadable(string name, string content, string reason)
    {
        var s = await ReceiveWithAsync(new FakeAttachment(name, Encoding.UTF8.GetBytes(content)));

        var info = Assert.Single(s.Attachments);
        Assert.Equal("Blocked", info.Str("state"));
        Assert.Equal(reason, info.Str("blockReason"));
        Assert.False(info.GetProperty("downloadable").GetBoolean());
        var response = await s.D.Agent.Client.GetAsync($"/api/tickets/{s.TicketId}/attachments/{info.GetProperty("id").GetGuid()}");
        response.ExpectCode(HttpStatusCode.Conflict, "invalid_state");
        Assert.Equal(0, await fixture.CountAsync("SELECT count(*) FROM attachments.attachments WHERE ticket_id = @t AND storage_key IS NOT NULL", TicketKit.P("t", s.TicketId)));
    }

    [Fact]
    public async Task An_oversized_attachment_is_blocked_without_downloading_its_bytes()
    {
        var s = await ReceiveWithAsync(new FakeAttachment("huge.zip", new byte[26 * 1024 * 1024]));

        var info = Assert.Single(s.Attachments);
        Assert.Equal("Blocked", info.Str("state"));
        Assert.Equal("too_large", info.Str("blockReason"));
    }

    [Fact]
    public async Task Attachments_are_only_downloadable_by_users_who_can_read_the_ticket()
    {
        var s = await ReceiveWithAsync(new FakeAttachment("secret.txt", "confidential"u8.ToArray()));
        var attachmentId = Assert.Single(s.Attachments).GetProperty("id").GetGuid();
        var other = await fixture.NewDeptAsync();
        var outsider = await fixture.NewUserAsync();
        var platform = await fixture.PlatformAdminAsync();
        var url = $"/api/tickets/{s.TicketId}/attachments/{attachmentId}";

        (await fixture.CreateAnonymousClient().GetAsync(url)).Expect(HttpStatusCode.Unauthorized);
        (await other.Agent.Client.GetAsync(url)).Expect(HttpStatusCode.NotFound);
        (await outsider.Client.GetAsync(url)).Expect(HttpStatusCode.NotFound);
        (await platform.Client.GetAsync(url)).Expect(HttpStatusCode.NotFound); // administration is not content access
        (await s.D.Viewer.Client.GetAsync(url)).Expect(HttpStatusCode.OK);

        // The attachment id never works under another ticket, nor does a random id.
        var otherTicket = (await s.D.Agent.Client.PostAsync(
            $"{s.D.Department.Url}/tickets", new { subject = "Other", description = "x", requester = new { email = MailKit.NewSender() } })).Expect(HttpStatusCode.Created);
        (await s.D.Agent.Client.GetAsync($"/api/tickets/{otherTicket.Id}/attachments/{attachmentId}")).Expect(HttpStatusCode.NotFound);
        (await s.D.Agent.Client.GetAsync($"/api/tickets/{s.TicketId}/attachments/{Guid.NewGuid()}")).Expect(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Inline_images_are_kept_with_their_content_id()
    {
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 };
        var s = await ReceiveWithAsync(new FakeAttachment("logo.png", png, "image/png", IsInline: true, ContentId: "logo@mail"));

        var info = Assert.Single(s.Attachments);
        Assert.True(info.GetProperty("isInline").GetBoolean());
    }

    [Fact]
    public async Task When_a_clean_scan_is_required_unscanned_attachments_are_not_downloadable()
    {
        await using var strict = new HelpdeskFactory(fixture.ConnectionString).WithWebHostBuilder(b => b.UseSetting("Attachments:RequireCleanScan", "true"));
        var d = await fixture.NewDeptAsync();
        var box = await fixture.CreateMailboxAsync(d);
        var subject = $"Scan {Unique.Id()}";
        await fixture.ReceiveAsync(box, MailKit.Mail(MailKit.NewSender(), subject) with { Attachments = [new FakeAttachment("a.txt", "x"u8.ToArray())] });
        var ticketId = Assert.Single(await d.Agent.TicketsAsync(d, subject)).GetProperty("id").GetGuid();
        var attachmentId = await fixture.ScalarAsync<Guid>("SELECT id FROM attachments.attachments WHERE ticket_id = @t", TicketKit.P("t", ticketId));
        var agent = await fixture.SignInAsync(d.Agent.Subject, factory: strict);

        var response = await agent.Client.GetAsync($"/api/tickets/{ticketId}/attachments/{attachmentId}");

        response.ExpectCode(HttpStatusCode.Conflict, "invalid_state");
        Assert.Contains("scan_required", response.Json.GetProperty("detail").GetString(), StringComparison.Ordinal);
    }

    // ----- validator rules (pure) -----

    [Theory]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("C:\\Users\\x\\evil.txt", "evil.txt")]
    [InlineData("  .hidden.  ", "hidden")]
    [InlineData("na\u0000me.txt", "name.txt")]
    [InlineData("", "attachment")]
    public void File_names_are_sanitised(string input, string expected) =>
        Assert.Equal(expected, AttachmentValidator.SanitiseFileName(input));

    [Fact]
    public void Very_long_names_are_capped_and_keep_their_extension()
    {
        var name = AttachmentValidator.SanitiseFileName(new string('a', 400) + ".pdf");
        Assert.Equal(255, name.Length);
        Assert.EndsWith(".pdf", name, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("a.docx", new byte[] { 0x50, 0x4B, 0x03, 0x04 }, true)]
    [InlineData("a.docx", new byte[] { 0x00, 0x01 }, false)]
    [InlineData("a.xls", new byte[] { 0xD0, 0xCF, 0x11, 0xE0 }, true)]
    [InlineData("a.jpg", new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, true)]
    [InlineData("a.txt", new byte[] { 0x01 }, true)]
    [InlineData("a.unknownext", new byte[] { 0x01 }, true)]
    [InlineData("a.sh", new byte[] { 0x01 }, false)]
    public void Signatures_and_blocked_types_are_enforced(string name, byte[] content, bool accepted) =>
        Assert.Equal(accepted, AttachmentValidator.Validate(name, content, 1024).Accepted);
}
