#pragma warning disable CA1861 // one-off request payloads
using System.Net;
using Helpdesk.Host.Tests.Infrastructure;

namespace Helpdesk.Host.Tests.Ticketing;

/// <summary>The ticket types an agent may choose when creating a ticket: only active ones, only of the department, readable without the forms-administration permission.</summary>
public sealed class TicketTypeOptionsTests(HelpdeskFixture fixture) : IClassFixture<HelpdeskFixture>
{
    [Fact]
    public async Task Agents_see_the_active_types_of_their_department_in_order_and_the_chosen_type_is_stored_on_the_new_ticket()
    {
        var d = await fixture.NewDeptAsync();
        var incident = (await d.Admin.Client.PostAsync($"{d.Department.Url}/ticket-types", new { name = "Incident", isDefault = true })).Expect(HttpStatusCode.Created);
        var request = (await d.Admin.Client.PostAsync($"{d.Department.Url}/ticket-types", new { name = "Service request" })).Expect(HttpStatusCode.Created);
        var retired = (await d.Admin.Client.PostAsync($"{d.Department.Url}/ticket-types", new { name = "Retired" })).Expect(HttpStatusCode.Created);
        (await d.Admin.Client.PatchAsync($"{d.Department.Url}/ticket-types/{retired.Id}", new { name = "Retired", version = retired.Json.GetProperty("version").GetString(), isActive = false })).Expect(HttpStatusCode.OK);

        var options = (await d.Agent.Client.GetAsync($"{d.Department.Url}/ticket-types/options")).Expect(HttpStatusCode.OK).Json.EnumerateArray().ToList();
        Assert.Equal(["Incident", "Service request"], options.Select(o => o.Str("name")).ToArray()); // the retired one is not offered
        Assert.Equal(incident.Id, options.Single(o => o.GetProperty("isDefault").GetBoolean()).GetProperty("id").GetGuid());

        var created = (await d.Agent.CreateTicketAsync(d.Department, TicketHelpers.NewTicketBody(extra: new { TicketTypeId = request.Id }))).Expect(HttpStatusCode.Created);
        Assert.Equal(request.Id, (await d.Agent.GetTicketAsync(created.Id)).Expect(HttpStatusCode.OK).Json.GetProperty("type").GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task The_options_need_ticket_creation_and_never_show_another_departments_types()
    {
        var d = await fixture.NewDeptAsync();
        var other = await fixture.NewDeptAsync();
        (await other.Admin.Client.PostAsync($"{other.Department.Url}/ticket-types", new { name = $"Other {Unique.Id()}" })).Expect(HttpStatusCode.Created);

        Assert.DoesNotContain("Other", string.Join(',', (await d.Agent.Client.GetAsync($"{d.Department.Url}/ticket-types/options")).Expect(HttpStatusCode.OK).Json.EnumerateArray().Select(o => o.Str("name"))), StringComparison.Ordinal);
        (await d.Viewer.Client.GetAsync($"{d.Department.Url}/ticket-types/options")).Expect(HttpStatusCode.Forbidden); // read-only members cannot create tickets
        (await other.Agent.Client.GetAsync($"{d.Department.Url}/ticket-types/options")).Expect(HttpStatusCode.NotFound);
        (await (await fixture.PlatformAdminAsync()).Client.GetAsync($"{d.Department.Url}/ticket-types/options")).Expect(HttpStatusCode.NotFound);
        (await fixture.CreateAnonymousClient().GetAsync($"{d.Department.Url}/ticket-types/options")).Expect(HttpStatusCode.Unauthorized);
    }
}
