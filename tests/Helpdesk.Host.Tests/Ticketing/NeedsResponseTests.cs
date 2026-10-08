using System.Net;
using Helpdesk.Host.Tests.Infrastructure;
using Helpdesk.Modules.Ticketing.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Helpdesk.Host.Tests.Ticketing;

/// <summary>D20: the queue says who spoke last in the public conversation; internal notes never count and no status changes.</summary>
public sealed class NeedsResponseTests(HelpdeskFixture fixture) : IClassFixture<HelpdeskFixture>
{
    private static async Task<string?> LastSpeakerAsync(Dept d, Guid ticketId)
    {
        var items = (await d.Agent.Client.GetAsync($"/api/tickets?queue=all&departmentId={d.Department.Id}&limit=100")).Expect(HttpStatusCode.OK).Json.GetProperty("items").EnumerateArray();
        var item = items.Single(i => i.GetProperty("id").GetGuid() == ticketId);
        return item.TryGetProperty("lastPublicBy", out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() : null;
    }

    [Fact]
    public async Task The_queue_tells_whose_turn_it_is_from_the_last_public_message_only()
    {
        var d = await fixture.NewDeptAsync();
        var ticket = await d.Agent.CreateTicketAsync(d.Department);
        Assert.Equal("Requester", await LastSpeakerAsync(d, ticket.Id)); // the request itself

        (await d.Agent.PostMessageAsync(ticket.Id, "Public", "We are on it")).Expect(HttpStatusCode.Created);
        Assert.Equal("Agent", await LastSpeakerAsync(d, ticket.Id));

        (await d.Agent.PostMessageAsync(ticket.Id, "Internal", "private musing")).Expect(HttpStatusCode.Created);
        Assert.Equal("Agent", await LastSpeakerAsync(d, ticket.Id)); // a note says nothing about the conversation

        var requesterId = Guid.Parse((await fixture.ScalarAsync<string>("SELECT requester_id::text FROM ticketing.tickets WHERE id = @t", TicketKit.P("t", ticket.Id)))!);
        await fixture.WithScopeAsync(sp => sp.GetRequiredService<ITicketIngress>().AddRequesterMessageAsync(d.Id, ticket.Id, requesterId, "Any news?", "manual"));
        Assert.Equal("Requester", await LastSpeakerAsync(d, ticket.Id));
        Assert.Equal("Open", (await d.Agent.GetTicketAsync(ticket.Id)).Json.GetProperty("status").GetString()); // no status was invented
    }
}
