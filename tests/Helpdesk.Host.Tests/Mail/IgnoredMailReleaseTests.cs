using System.Net;
using Helpdesk.Host.Tests.Infrastructure;
using Helpdesk.Host.Tests.Ticketing;
using Helpdesk.Modules.Mail.Providers;

namespace Helpdesk.Host.Tests.Mail;

/// <summary>D12: an administrator can turn ordinary ignored mail into a ticket (audited, never acknowledged); delivery reports, loops and mail without a sender can never be released.</summary>
public sealed class IgnoredMailReleaseTests(HelpdeskFixture fixture) : IClassFixture<HelpdeskFixture>
{
    private static async Task<Guid> IgnoredIdAsync(Dept d, string subject) =>
        (await d.Admin.Client.GetAsync($"{d.Department.Url}/mail/attention")).Expect(HttpStatusCode.OK).Json.GetProperty("items").EnumerateArray()
            .Single(i => i.Str("state") == "Ignored" && i.Str("subject") == subject).GetProperty("id").GetGuid();

    [Fact]
    public async Task A_mailing_list_message_can_be_released_into_a_ticket_without_an_acknowledgement_and_only_by_someone_who_may_read_tickets()
    {
        var d = await fixture.NewDeptAsync();
        var box = await fixture.CreateMailboxAsync(d);
        (await d.Admin.Client.PutAsync($"{d.Department.Url}/auto-reply", new { enabled = true, forEmail = true, forPortal = false, subject = "Ack {{ticket.reference}}", body = "Hello" })).Expect(HttpStatusCode.OK);
        var subject = $"Vendor notice {Unique.Id()}";
        await fixture.ReceiveAsync(box, MailKit.Mail(MailKit.NewSender(), subject) with { Headers = new Dictionary<string, string> { ["List-Id"] = "<vendor.example.test>" } });
        Assert.Empty(await d.Agent.TicketsAsync(d, subject));
        var id = await IgnoredIdAsync(d, subject);

        var platform = await fixture.PlatformAdminAsync();
        (await platform.Client.PostAsync($"{d.Department.Url}/mail/{id}/release")).Expect(HttpStatusCode.NotFound); // administrative permission without membership: no content access
        (await d.Agent.Client.PostAsync($"{d.Department.Url}/mail/{id}/release")).Expect(HttpStatusCode.Forbidden);

        var released = (await d.Admin.Client.PostAsync($"{d.Department.Url}/mail/{id}/release")).Expect(HttpStatusCode.OK).Json;
        Assert.NotEqual(Guid.Empty, released.GetProperty("ticketId").GetGuid());
        Assert.Single(await d.Agent.TicketsAsync(d, subject));
        await fixture.DispatchAsync();
        Assert.Empty(fixture.Fake().SentMail(box.Address)); // never acknowledged
        Assert.Equal(1, await fixture.CountAuditAsync("mail.ignored_released", id));
        (await d.Admin.Client.PostAsync($"{d.Department.Url}/mail/{id}/release")).Expect(HttpStatusCode.NotFound); // no longer ignored
    }

    [Fact]
    public async Task Delivery_reports_and_loops_are_never_released()
    {
        var d = await fixture.NewDeptAsync();
        var box = await fixture.CreateMailboxAsync(d);
        var subject = $"Undeliverable {Unique.Id()}";
        await fixture.ReceiveAsync(box, MailKit.Mail("mailer-daemon@mx.example.test", subject));
        var id = await IgnoredIdAsync(d, subject);

        (await d.Admin.Client.PostAsync($"{d.Department.Url}/mail/{id}/release")).ExpectCode(HttpStatusCode.Conflict, "invalid_state");
        Assert.Empty(await d.Agent.TicketsAsync(d, subject));
    }
}
