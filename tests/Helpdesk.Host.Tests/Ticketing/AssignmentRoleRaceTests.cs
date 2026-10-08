#pragma warning disable CA1861 // one-off request payloads
using System.Net;
using System.Text.Json;
using Helpdesk.Host.Tests.Infrastructure;
using Helpdesk.Modules.Ticketing.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Helpdesk.Host.Tests.Ticketing;

/// <summary>
/// An assignment and a demotion of the assignee to a role that cannot work tickets must serialise in both orders, so no ticket ends up assigned to a member who lost
/// <c>tickets.update</c>. The in-flight side is held open on a raw connection (what a request does between its checks and its commit); the other side is a real API call.
/// </summary>
public sealed class AssignmentRoleRaceTests(HelpdeskFixture fixture) : IClassFixture<HelpdeskFixture>
{
    private Task DrainAsync() => fixture.WithScopeAsync(sp => sp.GetRequiredService<INotificationOutbox>().DrainAsync(CancellationToken.None));

    /// <summary>A demotion mid-flight: it has locked the membership row and changed the role, and has not committed.</summary>
    private static async Task<NpgsqlTransaction> DemotionInFlightAsync(NpgsqlConnection connection, Guid departmentId, Guid userId)
    {
        var tx = await connection.BeginTransactionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT 1 FROM organisation.department_memberships WHERE department_id = @d AND user_id = @u FOR UPDATE; " +
            "UPDATE organisation.department_memberships SET role = 'Viewer', updated_at = now() WHERE department_id = @d AND user_id = @u", connection, tx);
        cmd.Parameters.AddWithValue("d", departmentId);
        cmd.Parameters.AddWithValue("u", userId);
        await cmd.ExecuteNonQueryAsync();
        return tx;
    }

    private async Task<long> TicketsHeldByAsync(Guid departmentId, Guid userId) => await fixture.ScalarAsync<long>(
        "SELECT count(*) FROM ticketing.tickets WHERE department_id = @d AND assignee_user_id = @u", TicketKit.P("d", departmentId), TicketKit.P("u", userId));

    [Theory]
    [InlineData("assign")]
    [InlineData("bulk")]
    [InlineData("create")]
    public async Task An_assignment_that_starts_after_a_demotion_has_locked_the_role_waits_and_is_then_refused(string entryPoint)
    {
        var d = await fixture.NewDeptAsync();
        var member = await fixture.NewMemberAsync(d.Department, "Agent");
        var ticket = await d.Lead.CreateTicketAsync(d.Department);

        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var demotion = await DemotionInFlightAsync(connection, d.Id, member.Id);

        var attempt = entryPoint switch
        {
            "assign" => d.Lead.AssignAsync(ticket.Id, ticket.Version, member.Id),
            "bulk" => d.Lead.Client.PostAsync("/api/tickets/bulk", new { ticketIds = new[] { ticket.Id }, assignee = new { id = member.Id } }),
            _ => d.Lead.Client.PostAsync($"{d.Department.Url}/tickets", TicketHelpers.NewTicketBody(extra: new { AssigneeUserId = member.Id })),
        };
        await Task.Delay(1500);
        Assert.False(attempt.IsCompleted, "the assignment must wait for the demotion that holds the membership row");
        await demotion.CommitAsync();

        var response = await attempt;
        if (entryPoint == "bulk")
        {
            var item = response.Expect(HttpStatusCode.OK).Json.GetProperty("results")[0];
            Assert.Equal("invalid", item.Str("outcome"));
        }
        else
        {
            response.Expect(HttpStatusCode.Conflict);
        }

        Assert.Equal(0L, await TicketsHeldByAsync(d.Id, member.Id)); // nothing ended up assigned to the demoted member
    }

    [Fact]
    public async Task A_demotion_that_starts_while_an_assignment_is_in_flight_waits_for_it_and_then_unassigns_the_ticket()
    {
        var d = await fixture.NewDeptAsync();
        var member = await fixture.NewMemberAsync(d.Department, "Agent");
        var ticket = await d.Lead.CreateTicketAsync(d.Department);
        await d.Lead.Client.PostAsync("/api/notifications/read-all");

        // The assignment is mid-transaction: it holds a key-share lock on the membership row and has changed the ticket, and has not committed.
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var assignment = await connection.BeginTransactionAsync();
        await using (var cmd = new NpgsqlCommand(
            "SELECT 1 FROM organisation.department_memberships WHERE department_id = @d AND user_id = @u FOR KEY SHARE; UPDATE ticketing.tickets SET assignee_user_id = @u WHERE id = @t", connection, assignment))
        {
            cmd.Parameters.AddWithValue("d", d.Id);
            cmd.Parameters.AddWithValue("u", member.Id);
            cmd.Parameters.AddWithValue("t", ticket.Id);
            await cmd.ExecuteNonQueryAsync();
        }

        var demotion = d.Admin.Client.PutAsync($"{d.Department.Url}/members/{member.Id}", new { role = "Viewer" });
        await Task.Delay(1500);
        Assert.False(demotion.IsCompleted, "the demotion must wait for the in-flight assignment");
        await assignment.CommitAsync();
        (await demotion).Expect(HttpStatusCode.OK);
        await DrainAsync();

        Assert.Equal(0L, await TicketsHeldByAsync(d.Id, member.Id));
        var activity = (await d.Lead.Client.GetAsync($"/api/tickets/{ticket.Id}/activity?limit=200")).Expect(HttpStatusCode.OK).Json.GetProperty("items").EnumerateArray().ToList();
        Assert.Single(activity, e => e.Str("action") == "ticket.assignee_changed" && e.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String && r.GetString() == "department_role_changed");
    }
}
