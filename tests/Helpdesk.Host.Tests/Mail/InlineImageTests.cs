using System.Net;
using System.Net.Http.Headers;
using Helpdesk.Host.Tests.Infrastructure;
using Helpdesk.Host.Tests.Ticketing;
using Helpdesk.Modules.Mail.Providers;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Helpdesk.Host.Tests.Mail;

/// <summary>
/// Images pasted into a reply: stored through the attachment pipeline (switch, size, type from the bytes), claimed only by their author, sent inline with a Content-ID,
/// never emailed for internal notes, and refused outright while attachments are switched off.
/// </summary>
public sealed class InlineImageTests(HelpdeskFixture fixture) : IClassFixture<HelpdeskFixture>
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4, 5, 6, 7, 8];

    private static Task<ApiResponse> UploadAsync(TestUser user, Guid ticketId, byte[] bytes, string type = "image/png")
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(type);
        return user.Client.PostMultipartAsync($"/api/tickets/{ticketId}/inline-images", content);
    }

    private async Task<(Dept D, Mailbox Box, Guid TicketId)> TicketAsync()
    {
        var d = await fixture.NewDeptAsync();
        var box = await fixture.CreateMailboxAsync(d);
        var subject = $"Screenshot {Unique.Id()}";
        await fixture.ReceiveAsync(box, MailKit.Mail(MailKit.NewSender(), subject));
        var id = Assert.Single(await d.Agent.TicketsAsync(d, subject)).GetProperty("id").GetGuid();
        return (d, box, id);
    }

    [Fact]
    public async Task A_pasted_image_is_stored_attached_to_the_reply_and_sent_inline_with_its_content_id()
    {
        var (d, box, ticketId) = await TicketAsync();
        var up = (await UploadAsync(d.Agent, ticketId, Png)).Expect(HttpStatusCode.OK).Json;
        var imageId = up.GetProperty("id").GetGuid();
        Assert.Equal("image/png", up.GetProperty("contentType").GetString());

        var posted = (await d.Agent.PostMessageAsync(ticketId, "Public", $"This is what I see:\n[image:{imageId}]\nPlease try again.")).Expect(HttpStatusCode.Created);
        Assert.Equal("Public", posted.Json.GetProperty("message").Str("kind"));
        await fixture.DispatchAsync();

        var sent = Assert.Single(fixture.Fake().SentMail(box.Address), m => m.BodyHtml.Contains("cid:", StringComparison.Ordinal));
        var cid = $"{imageId:N}@helpdesk";
        Assert.Contains($"<img src=\"cid:{cid}\"", sent.BodyHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("data:image", sent.BodyHtml, StringComparison.Ordinal); // never a base64 data URI
        Assert.DoesNotContain("[image:", sent.BodyHtml, StringComparison.Ordinal);
        var inline = Assert.Single(sent.InlineImages!);
        Assert.Equal(cid, inline.ContentId);
        Assert.Equal("image/png", inline.ContentType);
        Assert.Equal(Png, inline.Bytes);

        // The agent sees it on the message and can open it; it is an attachment of that message now.
        var message = (await d.Agent.MessagesAsync(ticketId)).Last(m => m.Str("kind") == "Public" && m.Str("authorType") == "Agent");
        Assert.Contains(message.GetProperty("attachments").EnumerateArray(), a => a.GetProperty("id").GetGuid() == imageId);
        var download = await d.Agent.Client.Http.GetAsync($"/api/tickets/{ticketId}/attachments/{imageId}");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(Png, await download.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task An_internal_note_may_hold_an_image_but_nothing_is_emailed()
    {
        var (d, box, ticketId) = await TicketAsync();
        var imageId = (await UploadAsync(d.Agent, ticketId, Png)).Expect(HttpStatusCode.OK).Json.GetProperty("id").GetGuid();
        var before = fixture.Fake().SentMail(box.Address).Count;

        (await d.Agent.PostMessageAsync(ticketId, "Internal", $"Note [image:{imageId}]")).Expect(HttpStatusCode.Created);
        await fixture.DispatchAsync();

        Assert.Equal(before, fixture.Fake().SentMail(box.Address).Count);
        Assert.Equal(1, await fixture.CountAsync("SELECT count(*) FROM attachments.attachments a JOIN ticketing.messages m ON m.id = a.message_id WHERE a.id = @a AND m.kind = 'Internal'", TicketKit.P("a", imageId)));
    }

    [Theory]
    [InlineData("text")]
    [InlineData("svg")]
    [InlineData("html")]
    [InlineData("exe")]
    public async Task Anything_that_is_not_a_real_png_jpeg_or_gif_is_refused_whatever_the_client_claims(string kind)
    {
        var (d, _, ticketId) = await TicketAsync();
        var bytes = kind switch
        {
            "svg" => "<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>"u8.ToArray(),
            "html" => "<html><script>alert(1)</script></html>"u8.ToArray(),
            "exe" => "MZ\u0090\0\u0003\0\0\0"u8.ToArray(),
            _ => "just some text"u8.ToArray(),
        };

        (await UploadAsync(d.Agent, ticketId, bytes, "image/png")).Expect(HttpStatusCode.BadRequest);
        (await UploadAsync(d.Agent, ticketId, [])).Expect(HttpStatusCode.BadRequest);
        (await UploadAsync(d.Agent, ticketId, [.. Png, .. new byte[4 * 1024 * 1024]])).Expect(HttpStatusCode.BadRequest); // over the 3 MB limit
        Assert.Equal(0, await fixture.CountAsync("SELECT count(*) FROM attachments.attachments WHERE ticket_id = @t", TicketKit.P("t", ticketId)));
    }

    [Fact]
    public async Task Only_the_author_can_attach_a_pasted_image_and_only_once_and_not_across_tickets_or_departments()
    {
        var (d, _, ticketId) = await TicketAsync();
        var imageId = (await UploadAsync(d.Agent, ticketId, Png)).Expect(HttpStatusCode.OK).Json.GetProperty("id").GetGuid();

        // A colleague cannot use somebody else's pending image.
        (await d.Lead.PostMessageAsync(ticketId, "Internal", $"stolen [image:{imageId}]")).Expect(HttpStatusCode.BadRequest);
        // An unknown id is refused the same way.
        (await d.Agent.PostMessageAsync(ticketId, "Internal", $"[image:{Guid.NewGuid()}]")).Expect(HttpStatusCode.BadRequest);
        // Another department's ticket never accepts it.
        var other = await TicketAsync();
        (await other.D.Agent.PostMessageAsync(other.TicketId, "Internal", $"[image:{imageId}]")).Expect(HttpStatusCode.BadRequest);

        (await d.Agent.PostMessageAsync(ticketId, "Internal", $"mine [image:{imageId}]")).Expect(HttpStatusCode.Created);
        // Used once: a second message cannot claim it again.
        (await d.Agent.PostMessageAsync(ticketId, "Internal", $"again [image:{imageId}]")).Expect(HttpStatusCode.BadRequest);
        // Too many images in one message.
        var many = string.Concat(Enumerable.Range(0, 7).Select(_ => $"[image:{Guid.NewGuid()}]"));
        (await d.Agent.PostMessageAsync(ticketId, "Internal", many)).Expect(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Uploads_follow_ticket_permissions_and_department_isolation()
    {
        var (d, _, ticketId) = await TicketAsync();
        var other = await fixture.NewDeptAsync();
        (await UploadAsync(d.Viewer, ticketId, Png)).Expect(HttpStatusCode.Forbidden);
        (await UploadAsync(other.Agent, ticketId, Png)).Expect(HttpStatusCode.NotFound);
        (await UploadAsync(await fixture.PlatformAdminAsync(), ticketId, Png)).Expect(HttpStatusCode.OK); // S-02: global access
        (await fixture.CreateAnonymousClient().PostMultipartAsync($"/api/tickets/{ticketId}/inline-images", new ByteArrayContent(Png))).Expect(HttpStatusCode.BadRequest); // CSRF before authentication, as everywhere
    }

    [Fact]
    public async Task While_attachments_are_switched_off_nothing_is_stored_and_a_message_with_an_image_cannot_be_posted()
    {
        var (d, _, ticketId) = await TicketAsync();
        await using var off = new HelpdeskFactory(fixture.ConnectionString).WithWebHostBuilder(b => b.UseSetting("Attachments:Enabled", "false"));
        var agent = await fixture.SignInAsync(d.Agent.Subject, factory: off);

        var capabilities = (await agent.Client.GetAsync("/api/ticket-capabilities")).Expect(HttpStatusCode.OK).Json;
        Assert.False(capabilities.GetProperty("attachmentsEnabled").GetBoolean());
        (await UploadAsync(agent, ticketId, Png)).ExpectCode(HttpStatusCode.Conflict, "invalid_state");
        (await agent.PostMessageAsync(ticketId, "Public", $"[image:{Guid.NewGuid()}]")).ExpectCode(HttpStatusCode.Conflict, "invalid_state");
        Assert.Equal(0, await fixture.CountAsync("SELECT count(*) FROM attachments.attachments WHERE ticket_id = @t", TicketKit.P("t", ticketId)));
    }

    [Fact]
    public async Task With_a_clean_scan_required_an_unscanned_pasted_image_is_not_stored_or_sent()
    {
        var (d, _, ticketId) = await TicketAsync();
        await using var strict = new HelpdeskFactory(fixture.ConnectionString).WithWebHostBuilder(b => b.UseSetting("Attachments:RequireCleanScan", "true"));
        var agent = await fixture.SignInAsync(d.Agent.Subject, factory: strict);

        (await UploadAsync(agent, ticketId, Png)).ExpectCode(HttpStatusCode.Conflict, "invalid_state"); // no scanner: it can never count as clean
    }
}
