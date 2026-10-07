using System.Net;
using Helpdesk.Host.Tests.Infrastructure;
using Helpdesk.Host.Tests.Mail;
using Helpdesk.Host.Tests.Ticketing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Helpdesk.Host.Tests.Platform;

/// <summary>
/// Administrative permissions pass for a Platform Admin in every Department without a membership, but Department content (ticket references and subjects, requester
/// addresses, correspondents) needs a membership with ticket read. This probe calls every Department-level GET endpoint as a Platform Admin who is not a member and checks
/// that none of a Department's content comes back, so a new administrative endpoint that returns content cannot slip through unnoticed (SEC-001, SEC-005, ARC-002).
/// </summary>
public sealed class AdministrativeContentProbeTests(HelpdeskFixture fixture) : IClassFixture<HelpdeskFixture>
{
    private sealed record Seed(Dept D, string TicketSubject, string TicketReference, string RequesterEmail, string MailSender, string MailSubject);

    private async Task<Seed> SeedAsync()
    {
        var d = await fixture.NewDeptAsync();
        var box = await fixture.CreateMailboxAsync(d);

        // A rule that fires on the ticket below, so the department has an execution history that names it.
        (await d.Admin.Client.PostAsync(
            $"{d.Department.Url}/automations",
            new { name = "Probe rule", trigger = "TicketCreated", isActive = true, stopProcessing = false, conditions = Array.Empty<object>(), actions = new object[] { new { type = "setPriority", value = "High" } } }))
            .Expect(HttpStatusCode.Created);

        var subject = $"Probe-subject-{Unique.Id()}";
        var email = $"probe-{Unique.Id()}@probe-domain-{Unique.Id()}.example.test";
        var ticket = await d.Agent.CreateTicketAsync(d.Department, TicketHelpers.NewTicketBody(subject, email));

        // Mail needing attention: an ignored inbound message from a correspondent with a distinctive address and subject.
        var sender = MailKit.NewSender();
        var mailSubject = $"Probe-mail-{Unique.Id()}";
        await fixture.ReceiveAsync(box, MailKit.Mail(sender, mailSubject) with { Headers = new Dictionary<string, string> { ["Precedence"] = "bulk" } });
        return new Seed(d, subject, ticket.Json.GetProperty("reference").GetString()!, email, sender, mailSubject);
    }

    [Fact]
    public async Task A_platform_admin_without_a_membership_gets_no_department_content_from_mail_attention_or_automation_history_while_members_still_do()
    {
        var s = await SeedAsync();
        var platformAdmin = await fixture.PlatformAdminAsync();
        var url = s.D.Department.Url;

        // The caller really is not a member: the department dashboard does not exist for them.
        (await platformAdmin.Client.GetAsync($"{url}/dashboard")).Expect(HttpStatusCode.NotFound);

        // Mail attention: still answers (the failed and ignored mail is operational information), but sender, subject and ticket link are withheld.
        var items = (await platformAdmin.Client.GetAsync($"{url}/mail/attention")).Expect(HttpStatusCode.OK).Json.GetProperty("items").EnumerateArray().ToList();
        var ignored = Assert.Single(items, i => i.Str("state") == "Ignored");
        Assert.Equal("precedence_bulk", ignored.Str("ignoreReason"));
        Assert.Equal("", ignored.Str("address"));
        Assert.Equal("", ignored.Str("subject"));
        Assert.Equal(System.Text.Json.JsonValueKind.Null, ignored.GetProperty("ticketId").ValueKind);

        // Automation history names tickets and requester data: not available without a membership (non-disclosing, like the dashboard).
        (await platformAdmin.Client.GetAsync($"{url}/automations/history")).Expect(HttpStatusCode.NotFound);

        // Members with the same administrative permission keep the existing behaviour.
        var memberItems = (await s.D.Admin.Client.GetAsync($"{url}/mail/attention")).Expect(HttpStatusCode.OK).Json.GetProperty("items").EnumerateArray().ToList();
        var memberIgnored = Assert.Single(memberItems, i => i.Str("state") == "Ignored");
        Assert.Equal(s.MailSubject, memberIgnored.Str("subject"));
        Assert.Equal(s.MailSender, memberIgnored.Str("address"));
        var history = (await s.D.Admin.Client.GetAsync($"{url}/automations/history")).Expect(HttpStatusCode.OK).Json.GetProperty("items");
        Assert.Contains(history.EnumerateArray(), e => e.Str("ticketReference") == s.TicketReference);
    }

    [Fact]
    public async Task A_platform_admin_without_a_membership_cannot_retry_department_mail_while_the_department_administrator_still_can()
    {
        var d = await fixture.NewDeptAsync();
        var box = await fixture.CreateMailboxAsync(d);
        var subject = $"Retry-probe-{Unique.Id()}";
        await fixture.ReceiveAsync(box, MailKit.Mail(MailKit.NewSender(), subject));
        var ticketId = Assert.Single(await d.Agent.TicketsAsync(d, subject)).GetProperty("id").GetGuid();
        fixture.Fake().FailNextSends(1, permanently: true);
        (await d.Agent.PostMessageAsync(ticketId, "Public", "Reply that fails")).Expect(HttpStatusCode.Created);
        await fixture.DispatchAsync();
        var mailId = (await d.Admin.Client.GetAsync($"{d.Department.Url}/mail/attention")).Expect(HttpStatusCode.OK).Json.GetProperty("items").EnumerateArray()
            .Single(i => i.Str("state") == "Failed" && i.Str("direction") == "Outbound").GetProperty("id").GetGuid();
        var platformAdmin = await fixture.PlatformAdminAsync();

        // Not a member: the same answer as a department that does not exist, and nothing is re-queued.
        (await platformAdmin.Client.PostAsync($"{d.Department.Url}/mail/{mailId}/retry")).Expect(HttpStatusCode.NotFound);
        Assert.Equal("Failed", await fixture.ScalarAsync<string>("SELECT state FROM mail.messages WHERE id = @m", TicketKit.P("m", mailId)));

        (await d.Admin.Client.PostAsync($"{d.Department.Url}/mail/{mailId}/retry")).Expect(HttpStatusCode.NoContent);
        await fixture.DispatchAsync();
        Assert.Equal("Sent", await fixture.ScalarAsync<string>("SELECT state FROM mail.messages WHERE id = @m", TicketKit.P("m", mailId)));
    }

    [Fact]
    public async Task A_platform_admin_without_a_membership_cannot_read_or_change_knowledge_drafts_but_keeps_the_knowledge_configuration()
    {
        var d = await fixture.NewDeptAsync();
        var title = $"Draft-probe-{Unique.Id()}";
        var article = (await d.Admin.Client.PostAsync($"{d.Department.Url}/knowledge/articles", new { title, summary = "Summary.", body = "Draft body." })).Expect(HttpStatusCode.Created);
        var platformAdmin = await fixture.PlatformAdminAsync();
        var articles = $"{d.Department.Url}/knowledge/articles";

        (await platformAdmin.Client.GetAsync(articles)).Expect(HttpStatusCode.NotFound);
        (await platformAdmin.Client.GetAsync($"{articles}/{article.Id}")).Expect(HttpStatusCode.NotFound);
        (await platformAdmin.Client.PatchAsync($"{articles}/{article.Id}", new { version = article.Version, title = "Changed", summary = "S", body = "B" })).Expect(HttpStatusCode.NotFound);
        (await platformAdmin.Client.PostAsync($"{articles}/{article.Id}/publish", new { version = article.Version })).Expect(HttpStatusCode.NotFound);
        (await platformAdmin.Client.PostAsync(articles, new { title = "New", summary = "S", body = "B" })).Expect(HttpStatusCode.NotFound);

        // Configuration stays with the administrative permission.
        (await platformAdmin.Client.GetAsync($"{d.Department.Url}/knowledge/categories")).Expect(HttpStatusCode.OK);

        // The department administrator is unchanged, and the draft was not touched.
        var read = (await d.Admin.Client.GetAsync($"{articles}/{article.Id}")).Expect(HttpStatusCode.OK);
        Assert.Contains(title, read.Body, StringComparison.Ordinal);
        Assert.Equal(article.Version, read.Version);
        Assert.Contains(title, (await d.Admin.Client.GetAsync(articles)).Expect(HttpStatusCode.OK).Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_department_level_get_endpoint_returns_department_content_to_a_platform_admin_without_a_membership()
    {
        var s = await SeedAsync();
        var platformAdmin = await fixture.PlatformAdminAsync();
        string[] content = [s.TicketSubject, s.TicketReference, s.RequesterEmail, s.RequesterEmail.Split('@')[1], s.MailSender, s.MailSubject];

        // Every GET endpoint whose only route value is the department id, found from the real routing table so new endpoints are covered automatically.
        var routes = fixture.Factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.Contains("GET") == true)
            .Select(e => e.RoutePattern.RawText ?? "")
            .Where(p => p.StartsWith("/api/departments/{departmentId", StringComparison.Ordinal))
            .Select(p => System.Text.RegularExpressions.Regex.Replace(p, @"\{departmentId(:[^}]*)?\}", s.D.Id.ToString()))
            .Where(p => !p.Contains('{', StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        // A probe that quietly covers nothing proves nothing: the two endpoints this task fixed must be among the routes it calls.
        Assert.Contains(routes, r => r.EndsWith("/mail/attention", StringComparison.Ordinal));
        Assert.Contains(routes, r => r.EndsWith("/automations/history", StringComparison.Ordinal));
        Assert.True(routes.Count >= 8, $"Only {routes.Count} department-level GET endpoints were found.");

        var leaks = new List<string>();
        foreach (var route in routes)
        {
            var response = await platformAdmin.Client.GetAsync(route);
            Assert.True((int)response.Status < 500, $"{route} answered {(int)response.Status}.");
            foreach (var value in content)
            {
                if (response.Body.Contains(value, StringComparison.OrdinalIgnoreCase))
                {
                    leaks.Add($"{route} returned '{value}'");
                }
            }
        }

        Assert.True(leaks.Count == 0, string.Join(Environment.NewLine, leaks));
    }
}
