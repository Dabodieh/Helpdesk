#pragma warning disable CA1861 // one-off request payloads
using System.Net;
using Helpdesk.Host.Tests.Infrastructure;
using Helpdesk.Host.Tests.Mail;
using Helpdesk.Host.Tests.Portal;
using Helpdesk.Host.Tests.Ticketing;

namespace Helpdesk.Host.Tests.Productivity;

/// <summary>Placeholders in canned responses and rendering one for a ticket (for insertion into a draft). Rendering is read-only: nothing is sent or stored.</summary>
public sealed class CannedPlaceholderTests(HelpdeskFixture fixture) : IClassFixture<HelpdeskFixture>
{
    private static string[] Errors(ApiResponse r) => [.. r.Expect(HttpStatusCode.BadRequest).Json.GetProperty("errors").EnumerateObject().Select(p => p.Name)];

    private static Task<ApiResponse> Department(TestUser user, Dept d, string title, string body) =>
        user.Client.PostAsync("/api/canned-responses", new { scope = "Department", departmentId = d.Id, title, body });

    private static async Task<ApiResponse> TicketAsync(Dept d, string requesterName = "Ana Customer") =>
        await d.Agent.CreateTicketAsync(d.Department, new { subject = $"Printer {Unique.Id()}", description = "It jams.", requester = new { email = $"ana{Unique.Id()}@customer.test", displayName = requesterName } });

    private static Task<ApiResponse> Render(TestUser user, string ticket, Guid id) => user.Client.GetAsync($"/api/tickets/{ticket}/canned-responses/{id}/render");

    [Fact]
    public async Task Only_the_fixed_placeholders_are_accepted_when_a_response_is_saved()
    {
        var d = await fixture.NewDeptAsync();
        var list = (await d.Agent.Client.GetAsync("/api/canned-responses/placeholders")).Expect(HttpStatusCode.OK).Json.EnumerateArray().Select(e => e.GetString()!).ToArray();
        Assert.Equal(["requester.name", "ticket.reference", "ticket.subject", "department.name", "agent.name"], list);
        (await fixture.CreateAnonymousClient().GetAsync("/api/canned-responses/placeholders")).Expect(HttpStatusCode.Unauthorized);

        Assert.Contains("body", Errors(await d.Agent.Client.PostAsync("/api/canned-responses", new { scope = "Personal", title = "t", body = "Hi {{requester.email}}" })));
        Assert.Contains("body", Errors(await d.Agent.Client.PostAsync("/api/canned-responses", new { scope = "Personal", title = "t", body = "Hi {{requester.name" })));
        (await d.Agent.Client.PostAsync("/api/canned-responses", new { scope = "Personal", title = "ok", body = "Hi {{ requester.name }}, re {{ticket.reference}} from {{agent.name}} at {{department.name}} about {{ticket.subject}}" })).Expect(HttpStatusCode.Created);

        // Editing the body is validated the same way; a title-only edit never re-validates an existing body.
        var created = (await Department(d.Admin, d, $"Dept {Unique.Id()}", "Hello {{requester.name}}")).Expect(HttpStatusCode.Created);
        Assert.Contains("body", Errors(await d.Admin.Client.PatchAsync($"/api/canned-responses/{created.Id}", new { version = created.Version, body = "{{nope}}" })));
        (await d.Admin.Client.PatchAsync($"/api/canned-responses/{created.Id}", new { version = created.Version, title = "renamed" })).Expect(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_department_response_renders_for_a_ticket_of_that_department_and_sends_nothing()
    {
        var d = await fixture.NewDeptAsync();
        var ticket = await TicketAsync(d);
        var response = (await Department(d.Admin, d, $"Greeting {Unique.Id()}", "Hi {{requester.name}},\nre {{ticket.reference}} ({{ticket.subject}}) at {{department.name}}.\nRegards, {{agent.name}}")).Expect(HttpStatusCode.Created);
        var before = (await d.Agent.MessagesAsync(ticket.Id)).Count;

        var rendered = (await Render(d.Agent, ticket.Json.Str("reference"), response.Id)).Expect(HttpStatusCode.OK).Json;
        var body = rendered.Str("body");
        Assert.StartsWith("Hi Ana Customer,", body, StringComparison.Ordinal);
        Assert.Contains(ticket.Json.Str("reference"), body, StringComparison.Ordinal);
        Assert.Contains(ticket.Json.Str("subject"), body, StringComparison.Ordinal);
        Assert.Contains(d.Department.Name, body, StringComparison.Ordinal);
        Assert.DoesNotContain("{{", body, StringComparison.Ordinal);
        Assert.Empty(rendered.GetProperty("emptyPlaceholders").EnumerateArray());

        // Read-only: no message is added and the stored template is unchanged.
        Assert.Equal(before, (await d.Agent.MessagesAsync(ticket.Id)).Count);
        var stored = (await d.Agent.Client.GetAsync("/api/canned-responses")).Json.EnumerateArray().Single(c => c.GetProperty("id").GetGuid() == response.Id);
        Assert.Contains("{{requester.name}}", stored.Str("body"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Placeholder_values_are_inserted_literally_in_one_pass()
    {
        var d = await fixture.NewDeptAsync();
        var ticket = await TicketAsync(d, requesterName: "{{agent.name}}");
        var response = (await Department(d.Admin, d, $"Once {Unique.Id()}", "Dear {{requester.name}}")).Expect(HttpStatusCode.Created);
        Assert.Equal("Dear {{agent.name}}", (await Render(d.Agent, ticket.Id.ToString(), response.Id)).Expect(HttpStatusCode.OK).Json.Str("body"));
    }

    [Fact]
    public async Task Personal_responses_render_only_for_their_owner_and_archived_ones_do_not_render()
    {
        var d = await fixture.NewDeptAsync();
        var ticket = await TicketAsync(d);
        var mine = (await d.Agent.Client.PostAsync("/api/canned-responses", new { scope = "Personal", title = $"Mine {Unique.Id()}", body = "Hello {{requester.name}}" })).Expect(HttpStatusCode.Created);
        Assert.Equal("Hello Ana Customer", (await Render(d.Agent, ticket.Id.ToString(), mine.Id)).Expect(HttpStatusCode.OK).Json.Str("body"));
        (await Render(d.Lead, ticket.Id.ToString(), mine.Id)).Expect(HttpStatusCode.NotFound); // somebody else's personal response behaves as missing

        var shared = (await Department(d.Admin, d, $"Shared {Unique.Id()}", "Hi")).Expect(HttpStatusCode.Created);
        (await Render(d.Agent, ticket.Id.ToString(), shared.Id)).Expect(HttpStatusCode.OK);
        (await d.Admin.Client.PatchAsync($"/api/canned-responses/{shared.Id}", new { version = shared.Version, isActive = false })).Expect(HttpStatusCode.OK);
        (await Render(d.Agent, ticket.Id.ToString(), shared.Id)).Expect(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Rendering_respects_department_isolation_and_role_permissions()
    {
        var a = await fixture.NewDeptAsync();
        var b = await fixture.NewDeptAsync();
        var ticketA = await TicketAsync(a);
        var ticketB = await TicketAsync(b);
        var responseA = (await Department(a.Admin, a, $"A {Unique.Id()}", "Hi {{requester.name}}")).Expect(HttpStatusCode.Created);

        // A department response is never usable on another department's ticket, and another department's ticket is invisible.
        (await Render(b.Agent, ticketB.Id.ToString(), responseA.Id)).Expect(HttpStatusCode.NotFound);
        (await Render(b.Agent, ticketA.Id.ToString(), responseA.Id)).Expect(HttpStatusCode.NotFound);
        (await Render(b.Admin, ticketA.Json.Str("reference"), responseA.Id)).Expect(HttpStatusCode.NotFound);
        (await Render(await fixture.PlatformAdminAsync(), ticketA.Id.ToString(), responseA.Id)).Expect(HttpStatusCode.OK); // S-02: global access
        (await fixture.CreateAnonymousClient().GetAsync($"/api/tickets/{ticketA.Id}/canned-responses/{responseA.Id}/render")).Expect(HttpStatusCode.Unauthorized);
        (await Render(a.Agent, ticketA.Id.ToString(), Guid.NewGuid())).Expect(HttpStatusCode.NotFound);

        // A read-only member can see the ticket but may neither reply nor note, so cannot draw a response for it.
        (await Render(a.Viewer, ticketA.Id.ToString(), responseA.Id)).Expect(HttpStatusCode.Forbidden);
        (await Render(a.Lead, ticketA.Id.ToString(), responseA.Id)).Expect(HttpStatusCode.OK);
    }
}
