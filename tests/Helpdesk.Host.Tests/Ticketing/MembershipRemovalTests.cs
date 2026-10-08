#pragma warning disable CA1861 // one-off request payloads
using System.Data.Common;
using System.Net;
using System.Text.Json;
using Helpdesk.Host.Tests.Infrastructure;
using Helpdesk.Modules.Ticketing.Contracts;
using Helpdesk.SharedKernel.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Helpdesk.Host.Tests.Ticketing;

/// <summary>Removing a department member clears the assignee of their tickets in the database; this proves it is now recorded, audited and told to the people who can reassign.</summary>
public sealed class MembershipRemovalTests(HelpdeskFixture fixture) : IClassFixture<HelpdeskFixture>
{
    private const string Reason = "department_membership_removed";

    private Task DrainAsync() => fixture.WithScopeAsync(sp => sp.GetRequiredService<INotificationOutbox>().DrainAsync(CancellationToken.None));

    private static async Task<ApiResponse> AssignedTicketAsync(Dept d, Guid assignee)
    {
        var t = await d.Lead.CreateTicketAsync(d.Department);
        (await d.Lead.AssignAsync(t.Id, t.Version, null, assignee)).Expect(HttpStatusCode.OK);
        return t;
    }

    private static async Task<List<JsonElement>> ActivityAsync(TestUser user, Guid ticketId) =>
        [.. (await user.Client.GetAsync($"/api/tickets/{ticketId}/activity?limit=200")).Expect(HttpStatusCode.OK).Json.GetProperty("items").EnumerateArray()];

    private static bool IsRemovalEntry(JsonElement e) => e.Str("action") == "ticket.assignee_changed" && e.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String && r.GetString() == Reason;

    private static async Task<string[]> UnreadAsync(TestUser user) =>
        [.. (await user.Client.GetAsync("/api/notifications")).Expect(HttpStatusCode.OK).Json.GetProperty("items").EnumerateArray()
            .Where(i => !i.GetProperty("read").GetBoolean()).Select(i => $"{i.GetProperty("type").GetString()}:{i.GetProperty("reference").GetString()}")];

    private async Task<long> RemovalAuditCountAsync(Guid departmentId) => await fixture.ScalarAsync<long>(
        "SELECT count(*) FROM audit.audit_events WHERE action = 'ticket.assignee_changed' AND department_id = @d AND next->>'reason' = @r",
        TicketKit.P("d", departmentId), TicketKit.P("r", Reason));

    [Fact]
    public async Task Each_affected_ticket_gets_an_activity_and_audit_entry_and_leads_get_a_capped_notification_without_duplicates()
    {
        var d = await fixture.NewDeptAsync();
        var leaving = await fixture.NewMemberAsync(d.Department, "Agent");
        var open = new List<ApiResponse>();
        for (var i = 0; i < 7; i++)
        {
            open.Add(await AssignedTicketAsync(d, leaving.Id));
        }

        var resolved = await AssignedTicketAsync(d, leaving.Id);
        var current = (await d.Lead.GetTicketAsync(resolved.Id)).Expect(HttpStatusCode.OK);
        (await d.Lead.PatchTicketAsync(current, new { Status = "Resolved" })).Expect(HttpStatusCode.OK);
        var someoneElses = await AssignedTicketAsync(d, d.Agent.Id);
        var unassigned = await d.Lead.CreateTicketAsync(d.Department);
        await d.Lead.Client.PostAsync("/api/notifications/read-all"); // start from a clean list
        await d.Agent.Client.PostAsync("/api/notifications/read-all");

        (await d.Admin.Client.DeleteAsync($"{d.Department.Url}/members/{leaving.Id}")).Expect(HttpStatusCode.NoContent);
        await DrainAsync();

        // Activity: reason, previous assignee, nothing assigned afterwards, the removing administrator as actor, a timestamp.
        foreach (var ticket in open.Append(resolved))
        {
            var entry = Assert.Single(await ActivityAsync(d.Lead, ticket.Id), IsRemovalEntry);
            Assert.Equal(leaving.Id, entry.GetProperty("previous").GetProperty("assigneeUserId").GetGuid());
            Assert.Equal(JsonValueKind.Null, entry.GetProperty("next").GetProperty("assigneeUserId").ValueKind);
            Assert.Equal(d.Admin.Id, entry.GetProperty("actor").GetProperty("id").GetGuid());
            Assert.True(entry.TryGetProperty("occurredAt", out _));
            Assert.Equal(JsonValueKind.Null, (await d.Lead.GetTicketAsync(ticket.Id)).Expect(HttpStatusCode.OK).Json.GetProperty("assignee").ValueKind);
        }

        // Untouched tickets: another agent's assignment stays, an unassigned ticket gets no entry.
        Assert.DoesNotContain(await ActivityAsync(d.Lead, someoneElses.Id), IsRemovalEntry);
        Assert.Equal(d.Agent.Id, (await d.Lead.GetTicketAsync(someoneElses.Id)).Expect(HttpStatusCode.OK).Json.GetProperty("assignee").GetProperty("id").GetGuid());
        Assert.DoesNotContain(await ActivityAsync(d.Lead, unassigned.Id), IsRemovalEntry);

        // Audit trail: one reassignment record per affected ticket (8), carrying the reason.
        Assert.Equal(8L, await RemovalAuditCountAsync(d.Id));

        // Notifications: leads only, for at most five OPEN tickets (not the resolved one), never the administrator who acted, agents or the removed member.
        var lead = await UnreadAsync(d.Lead);
        Assert.Equal(5, lead.Length);
        Assert.All(lead, n => Assert.StartsWith("unassigned:", n, StringComparison.Ordinal));
        Assert.DoesNotContain($"unassigned:{resolved.Json.Str("reference")}", lead);
        Assert.Empty(await UnreadAsync(d.Admin));
        Assert.Empty(await UnreadAsync(d.Agent));

        // Repeating the operation records nothing more.
        (await d.Admin.Client.DeleteAsync($"{d.Department.Url}/members/{leaving.Id}")).Expect(HttpStatusCode.NotFound);
        await DrainAsync();
        Assert.Equal(8L, await RemovalAuditCountAsync(d.Id));
        Assert.Equal(5, (await UnreadAsync(d.Lead)).Length);
    }

    [Fact]
    public async Task A_member_without_assigned_tickets_leaves_no_trace_and_other_departments_are_untouched()
    {
        var a = await fixture.NewDeptAsync();
        var b = await fixture.NewDeptAsync();
        var idle = await fixture.NewMemberAsync(a.Department, "Agent");
        await a.Lead.Client.PostAsync("/api/notifications/read-all");
        (await a.Admin.Client.DeleteAsync($"{a.Department.Url}/members/{idle.Id}")).Expect(HttpStatusCode.NoContent);
        await DrainAsync();
        Assert.Equal(0L, await RemovalAuditCountAsync(a.Id));
        Assert.Empty(await UnreadAsync(a.Lead));

        // Someone who belongs to both departments: leaving A does not touch their tickets in B, and B's leads hear nothing.
        var both = await fixture.NewUserAsync("both");
        await fixture.AddMemberAsync(a.Department, both, "Agent");
        await fixture.AddMemberAsync(b.Department, both, "Agent");
        var inA = await AssignedTicketAsync(a, both.Id);
        var inB = await AssignedTicketAsync(b, both.Id);
        await b.Lead.Client.PostAsync("/api/notifications/read-all");
        (await a.Admin.Client.DeleteAsync($"{a.Department.Url}/members/{both.Id}")).Expect(HttpStatusCode.NoContent);
        await DrainAsync();

        Assert.Single(await ActivityAsync(a.Lead, inA.Id), IsRemovalEntry);
        Assert.Equal(both.Id, (await b.Lead.GetTicketAsync(inB.Id)).Expect(HttpStatusCode.OK).Json.GetProperty("assignee").GetProperty("id").GetGuid());
        Assert.DoesNotContain(await ActivityAsync(b.Lead, inB.Id), IsRemovalEntry);
        Assert.Equal(0L, await RemovalAuditCountAsync(b.Id));
        Assert.Empty(await UnreadAsync(b.Lead));
        // The other department's staff cannot see this department's activity.
        (await b.Lead.Client.GetAsync($"/api/tickets/{inA.Id}/activity")).Expect(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_removal_that_is_refused_records_nothing()
    {
        var d = await fixture.NewDeptAsync();
        // The only department administrator cannot be removed; the refusal happens before anything is captured or recorded.
        var t = await AssignedTicketAsync(d, d.Admin.Id);
        (await d.Admin.Client.DeleteAsync($"{d.Department.Url}/members/{d.Admin.Id}")).Expect(HttpStatusCode.Conflict);
        await DrainAsync();
        Assert.Equal(0L, await RemovalAuditCountAsync(d.Id));
        Assert.Equal(d.Admin.Id, (await d.Lead.GetTicketAsync(t.Id)).Expect(HttpStatusCode.OK).Json.GetProperty("assignee").GetProperty("id").GetGuid());
    }

    /// <summary>Registered after Ticketing's own listener, so it runs after the ticket work and fails the removal transaction.</summary>
    private sealed class FailingListener : IMembershipRemovalListener
    {
        public Task MembershipRemovingAsync(Guid departmentId, Guid userId, DbTransaction transaction, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("simulated failure inside the removal transaction");

        public Task MembershipRemovedAsync(Guid departmentId, Guid userId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private async Task<long> UnassignedIntentCountAsync(Guid ticketId) =>
        await fixture.ScalarAsync<long>("SELECT count(*) FROM ticketing.notification_outbox WHERE ticket_id = @t AND kind = 'unassigned'", TicketKit.P("t", ticketId));

    [Fact]
    public async Task A_failure_inside_the_removal_transaction_undoes_everything_and_the_retry_records_exactly_once()
    {
        var d = await fixture.NewDeptAsync();
        var leaving = await fixture.NewMemberAsync(d.Department, "Agent");
        var ticket = await AssignedTicketAsync(d, leaving.Id);
        await d.Lead.Client.PostAsync("/api/notifications/read-all");

        await using var failing = new HelpdeskFactory(fixture.ConnectionString).WithWebHostBuilder(b => b.ConfigureServices(s => s.AddScoped<IMembershipRemovalListener, FailingListener>()));
        var admin = await fixture.SignInAsync(d.Admin.Subject, factory: failing);
        (await admin.Client.DeleteAsync($"{d.Department.Url}/members/{leaving.Id}")).Expect(HttpStatusCode.InternalServerError);
        await DrainAsync();

        // Nothing was committed: the member is still a member, the ticket is still assigned, no audit entry, no notification intent, nothing told to the leads.
        Assert.Equal(1L, await fixture.ScalarAsync<long>("SELECT count(*) FROM organisation.department_memberships WHERE department_id = @d AND user_id = @u", TicketKit.P("d", d.Id), TicketKit.P("u", leaving.Id)));
        Assert.Equal(leaving.Id, (await d.Lead.GetTicketAsync(ticket.Id)).Expect(HttpStatusCode.OK).Json.GetProperty("assignee").GetProperty("id").GetGuid());
        Assert.Equal(0L, await RemovalAuditCountAsync(d.Id));
        Assert.Equal(0L, await UnassignedIntentCountAsync(ticket.Id));
        Assert.Empty(await UnreadAsync(d.Lead));

        // The retry (a healthy host) performs the whole thing once.
        (await d.Admin.Client.DeleteAsync($"{d.Department.Url}/members/{leaving.Id}")).Expect(HttpStatusCode.NoContent);
        await DrainAsync();
        Assert.Equal(1L, await RemovalAuditCountAsync(d.Id));
        Assert.Equal(1L, await UnassignedIntentCountAsync(ticket.Id));
        Assert.Single(await ActivityAsync(d.Lead, ticket.Id), IsRemovalEntry);
        Assert.Single(await UnreadAsync(d.Lead));

        // Draining again (background recovery) creates nothing new.
        await DrainAsync();
        await DrainAsync();
        Assert.Single(await UnreadAsync(d.Lead));
        Assert.Equal(1L, await RemovalAuditCountAsync(d.Id));
    }

    [Fact]
    public async Task An_assignment_in_flight_when_the_removal_starts_is_waited_for_and_recorded_not_silently_cleared()
    {
        var d = await fixture.NewDeptAsync();
        var leaving = await fixture.NewMemberAsync(d.Department, "Agent");
        var early = await AssignedTicketAsync(d, leaving.Id);
        var late = await d.Lead.CreateTicketAsync(d.Department);
        await d.Lead.Client.PostAsync("/api/notifications/read-all");

        // Another request assigns the member (not yet committed): it holds a key-share lock on the membership through the foreign key.
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var inFlight = await connection.BeginTransactionAsync();
        await using (var assign = new NpgsqlCommand("UPDATE ticketing.tickets SET assignee_user_id = @u WHERE id = @t", connection, inFlight))
        {
            assign.Parameters.AddWithValue("u", leaving.Id);
            assign.Parameters.AddWithValue("t", late.Id);
            Assert.Equal(1, await assign.ExecuteNonQueryAsync());
        }

        var removal = d.Admin.Client.DeleteAsync($"{d.Department.Url}/members/{leaving.Id}");
        await Task.Delay(1500);
        Assert.False(removal.IsCompleted, "the removal must wait for the in-flight assignment");
        await inFlight.CommitAsync();
        (await removal).Expect(HttpStatusCode.NoContent);
        await DrainAsync();

        foreach (var ticket in new[] { early, late })
        {
            Assert.Single(await ActivityAsync(d.Lead, ticket.Id), IsRemovalEntry);
            Assert.Equal(JsonValueKind.Null, (await d.Lead.GetTicketAsync(ticket.Id)).Expect(HttpStatusCode.OK).Json.GetProperty("assignee").ValueKind);
        }

        Assert.Equal(2L, await RemovalAuditCountAsync(d.Id));
        Assert.Equal(2, (await UnreadAsync(d.Lead)).Length);
    }
}
