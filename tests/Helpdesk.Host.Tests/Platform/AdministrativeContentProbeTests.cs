using System.Net;
using Helpdesk.Host.Tests.Infrastructure;
using Helpdesk.Host.Tests.Mail;
using Helpdesk.Host.Tests.Ticketing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Helpdesk.Host.Tests.Platform;

/// <summary>
/// Policy S-02: the Platform Administrator holds every Department permission everywhere, so Department content IS returned to them without a membership. What stays
/// pinned is the other side of the boundary: an administrator of ANOTHER Department (the highest Department role) must get none of a Department's content from any
/// Department-level GET endpoint. The route list comes from the real routing table, so a new endpoint is covered automatically (SEC-001, SEC-005, ARC-002).
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
    public async Task The_platform_admin_sees_mail_attention_and_automation_history_content_that_another_departments_admin_cannot()
    {
        var s = await SeedAsync();
        var platformAdmin = await fixture.PlatformAdminAsync();
        var otherAdmin = (await fixture.NewDeptAsync()).Admin;
        var url = s.D.Department.Url;

        (await platformAdmin.Client.GetAsync($"{url}/dashboard")).Expect(HttpStatusCode.OK);
        var items = (await platformAdmin.Client.GetAsync($"{url}/mail/attention")).Expect(HttpStatusCode.OK).Json.GetProperty("items").EnumerateArray().ToList();
        var ignored = Assert.Single(items, i => i.Str("state") == "Ignored");
        Assert.Equal(s.MailSubject, ignored.Str("subject"));
        Assert.Equal(s.MailSender, ignored.Str("address"));
        var history = (await platformAdmin.Client.GetAsync($"{url}/automations/history")).Expect(HttpStatusCode.OK).Json.GetProperty("items");
        Assert.Contains(history.EnumerateArray(), e => e.Str("ticketReference") == s.TicketReference);

        // The administrator of another department learns nothing: non-disclosing 404.
        (await otherAdmin.Client.GetAsync($"{url}/dashboard")).Expect(HttpStatusCode.NotFound);
        (await otherAdmin.Client.GetAsync($"{url}/mail/attention")).Expect(HttpStatusCode.NotFound);
        (await otherAdmin.Client.GetAsync($"{url}/automations/history")).Expect(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_platform_admin_can_retry_department_mail_without_a_membership_and_another_departments_admin_cannot()
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
        var otherAdmin = (await fixture.NewDeptAsync()).Admin;
        var platformAdmin = await fixture.PlatformAdminAsync();

        (await otherAdmin.Client.PostAsync($"{d.Department.Url}/mail/{mailId}/retry")).Expect(HttpStatusCode.NotFound);
        Assert.Equal("Failed", await fixture.ScalarAsync<string>("SELECT state FROM mail.messages WHERE id = @m", TicketKit.P("m", mailId)));

        (await platformAdmin.Client.PostAsync($"{d.Department.Url}/mail/{mailId}/retry")).Expect(HttpStatusCode.NoContent);
        await fixture.DispatchAsync();
        Assert.Equal("Sent", await fixture.ScalarAsync<string>("SELECT state FROM mail.messages WHERE id = @m", TicketKit.P("m", mailId)));
    }

    [Fact]
    public async Task The_platform_admin_manages_knowledge_drafts_in_any_department_and_another_departments_admin_cannot()
    {
        var d = await fixture.NewDeptAsync();
        var title = $"Draft-probe-{Unique.Id()}";
        var article = (await d.Admin.Client.PostAsync($"{d.Department.Url}/knowledge/articles", new { title, summary = "Summary.", body = "Draft body." })).Expect(HttpStatusCode.Created);
        var platformAdmin = await fixture.PlatformAdminAsync();
        var otherAdmin = (await fixture.NewDeptAsync()).Admin;
        var articles = $"{d.Department.Url}/knowledge/articles";

        (await otherAdmin.Client.GetAsync(articles)).Expect(HttpStatusCode.NotFound);
        (await otherAdmin.Client.GetAsync($"{articles}/{article.Id}")).Expect(HttpStatusCode.NotFound);
        (await otherAdmin.Client.PatchAsync($"{articles}/{article.Id}", new { version = article.Version, title = "Changed", summary = "S", body = "B" })).Expect(HttpStatusCode.NotFound);

        Assert.Contains(title, (await platformAdmin.Client.GetAsync($"{articles}/{article.Id}")).Expect(HttpStatusCode.OK).Body, StringComparison.Ordinal);
        (await platformAdmin.Client.PostAsync(articles, new { title = $"New-{Unique.Id()}", summary = "S", body = "B" })).Expect(HttpStatusCode.Created);

        // The draft the other administrator tried to change is untouched.
        Assert.Equal(article.Version, (await d.Admin.Client.GetAsync($"{articles}/{article.Id}")).Expect(HttpStatusCode.OK).Version);
    }

    [Fact]
    public async Task No_department_level_get_endpoint_returns_department_content_to_the_administrator_of_another_department()
    {
        var s = await SeedAsync();
        var outsider = (await fixture.NewDeptAsync()).Admin;
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

        // A probe that quietly covers nothing proves nothing.
        Assert.Contains(routes, r => r.EndsWith("/mail/attention", StringComparison.Ordinal));
        Assert.Contains(routes, r => r.EndsWith("/automations/history", StringComparison.Ordinal));
        Assert.True(routes.Count >= 8, $"Only {routes.Count} department-level GET endpoints were found.");

        var leaks = new List<string>();
        foreach (var route in routes)
        {
            var response = await outsider.Client.GetAsync(route);
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
