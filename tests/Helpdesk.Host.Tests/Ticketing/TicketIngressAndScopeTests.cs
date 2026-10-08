using System.Net;
using System.Reflection;
using Helpdesk.Host.Tests.Infrastructure;
using Helpdesk.Modules.Organisation.Authorization;
using Helpdesk.Modules.Organisation.Contracts;
using Helpdesk.Modules.Ticketing.Contracts;
using Helpdesk.Modules.Ticketing.Persistence;
using Helpdesk.SharedKernel.Authorization;
using Helpdesk.SharedKernel.Errors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Helpdesk.Host.Tests.Ticketing;

/// <summary>ITicketIngress (D-15 reopen rules), the fail-closed department scope and the ticket permission model.</summary>
public sealed class TicketIngressAndScopeTests(HelpdeskFixture fixture) : IClassFixture<HelpdeskFixture>
{
    private static readonly string[] AgentTicketPermissions = ["tickets.create", "tickets.note", "tickets.notes.read", "tickets.read", "tickets.reply", "tickets.assign.self", "tickets.assign.team", "tickets.update"];

    private static Guid RequesterOf(ApiResponse ticket) => ticket.Json.GetProperty("requester").GetProperty("id").GetGuid();

    private async Task<RequesterMessageResult> IngestAsync(Guid departmentId, Guid ticketId, Guid requesterId, string body = "late reply", string source = "manual")
    {
        RequesterMessageResult? result = null;
        await fixture.WithScopeAsync(async sp => result = await sp.GetRequiredService<ITicketIngress>().AddRequesterMessageAsync(departmentId, ticketId, requesterId, body, source));
        return result!;
    }

    [Theory]
    [InlineData("Closed")]
    [InlineData("Resolved")]
    [InlineData("Pending")]
    public async Task A_requester_reply_reopens_the_same_ticket_and_keeps_team_and_assignee(string status)
    {
        var d = await fixture.NewDeptAsync();
        var team = await fixture.CreateTeamAsync(d.Department);
        var t = await d.Lead.CreateTicketAsync(d.Department, TicketHelpers.NewTicketBody(extra: new { TeamId = team.Id, AssigneeUserId = d.Agent.Id }));
        var changed = (await d.Lead.PatchTicketAsync(t, new { Status = status })).Expect(HttpStatusCode.OK);

        var result = await IngestAsync(d.Id, t.Id, RequesterOf(t));
        Assert.True(result.Reopened);

        var after = (await d.Agent.GetTicketAsync(t.Id)).Expect(HttpStatusCode.OK).Json;
        Assert.Equal("Open", after.Str("status"));
        Assert.Equal(t.Json.Str("reference"), after.Str("reference"));
        Assert.Equal(d.Id, after.GetProperty("departmentId").GetGuid());
        Assert.Equal(team.Id, after.GetProperty("team").GetProperty("id").GetGuid());
        Assert.Equal(d.Agent.Id, after.GetProperty("assignee").GetProperty("id").GetGuid());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, after.GetProperty("closedAt").ValueKind);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, after.GetProperty("resolvedAt").ValueKind);
        Assert.Equal(1, await fixture.ScalarAsync<long>("SELECT count(*) FROM ticketing.tickets WHERE department_id = @d", TicketKit.P("d", d.Id))); // no follow-up ticket
        Assert.NotEqual(changed.Version, after.Str("version"));

        var message = (await d.Agent.Client.GetAsync(TicketKit.Url(t.Id, "/messages"))).Json.GetProperty("items").EnumerateArray().First();
        Assert.Equal(result.MessageId, message.GetProperty("id").GetGuid());
        Assert.Equal("Requester", message.Str("authorType"));
        Assert.Equal("Public", message.Str("kind"));
        Assert.Equal("None", message.Str("deliveryState"));

        var reopen = (await fixture.AuditOfAsync(t.Id)).Single(r => r.Action == "ticket.status_changed" && r.Source == "system");
        Assert.Null(reopen.ActorUserId);
        Assert.Null(reopen.ActorName);
        Assert.Equal(d.Id, reopen.DepartmentId);
        Assert.Equal($$"""{"status": "{{status}}"}""", reopen.Previous);
        Assert.Contains("requester_reply", reopen.Next, StringComparison.Ordinal);

        var activity = (await d.Viewer.Client.GetAsync(TicketKit.Url(t.Id, "/activity"))).Expect(HttpStatusCode.OK).Json.GetProperty("items").EnumerateArray()
            .Single(i => i.Str("action") == "ticket.status_changed" && i.GetProperty("actor").ValueKind == System.Text.Json.JsonValueKind.Null);
        Assert.Equal("requester_reply", activity.Str("reason"));
        Assert.Equal("Open", activity.GetProperty("next").Str("status"));
    }

    [Fact]
    public async Task A_requester_reply_on_an_open_ticket_stays_open_and_writes_no_status_event()
    {
        var d = await fixture.NewDeptAsync();
        var t = await d.Agent.CreateTicketAsync(d.Department);
        var result = await IngestAsync(d.Id, t.Id, RequesterOf(t), source: "web");
        Assert.False(result.Reopened);
        var rows = await fixture.AuditOfAsync(t.Id);
        Assert.DoesNotContain(rows, r => r.Action == "ticket.status_changed");
        var added = rows.Last(r => r.Action == "ticket.message_added");
        Assert.Equal("system", added.Source);
        Assert.Null(added.ActorUserId);
        Assert.DoesNotContain("late reply", added.Next, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ingress_is_bound_to_the_department_and_validates_input()
    {
        var a = await fixture.NewDeptAsync();
        var b = await fixture.NewDeptAsync();
        var t = await a.Agent.CreateTicketAsync(a.Department);
        var tb = await b.Agent.CreateTicketAsync(b.Department);

        await Assert.ThrowsAsync<NotFoundException>(() => IngestAsync(b.Id, t.Id, RequesterOf(t)));       // ticket of another department
        await Assert.ThrowsAsync<NotFoundException>(() => IngestAsync(a.Id, t.Id, RequesterOf(tb)));      // requester of another department
        await Assert.ThrowsAsync<NotFoundException>(() => IngestAsync(a.Id, Guid.NewGuid(), RequesterOf(t)));
        await Assert.ThrowsAsync<RequestValidationException>(() => IngestAsync(a.Id, t.Id, RequesterOf(t), body: " "));
        await Assert.ThrowsAsync<RequestValidationException>(() => IngestAsync(a.Id, t.Id, RequesterOf(t), source: "email"));
        Assert.Equal(1, await fixture.ScalarAsync<long>("SELECT count(*) FROM ticketing.messages WHERE ticket_id = @t", TicketKit.P("t", t.Id)));
    }

    // ----- fail-closed scope -----

    [Fact]
    public async Task A_context_whose_scope_was_never_loaded_sees_nothing()
    {
        var d = await fixture.NewDeptAsync();
        var t = await d.Agent.CreateTicketAsync(d.Department);
        Assert.Equal(1, await fixture.ScalarAsync<long>("SELECT count(*) FROM ticketing.tickets WHERE id = @t", TicketKit.P("t", t.Id)));

        await fixture.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<TicketingDbContext>();
            Assert.False(sp.GetRequiredService<IDepartmentScope>().IsLoaded);
            Assert.Equal(0, await db.Tickets.CountAsync());
            Assert.Equal(0, await db.Messages.CountAsync());
            Assert.Equal(0, await db.Requesters.CountAsync());
            Assert.Empty(await db.Tickets.Where(x => x.Id == t.Id).ToListAsync());
            // The named bypass is the only way past the filter.
            Assert.Equal(1, await db.Tickets.IgnoreDepartmentScope().CountAsync(x => x.Id == t.Id));
        });
    }

    [Fact]
    public async Task A_loaded_scope_limits_every_ticketing_set_to_the_given_departments()
    {
        var a = await fixture.NewDeptAsync();
        var b = await fixture.NewDeptAsync();
        var ta = await a.Agent.CreateTicketAsync(a.Department);
        var tb = await b.Agent.CreateTicketAsync(b.Department);

        await using var db = new TicketingDbContext(
            new DbContextOptionsBuilder<TicketingDbContext>().UseNpgsql(fixture.ConnectionString).Options,
            new FixedScope([a.Id]));
        var tickets = await db.Tickets.Select(x => x.Id).ToListAsync();
        Assert.Contains(ta.Id, tickets);
        Assert.DoesNotContain(tb.Id, tickets);
        Assert.DoesNotContain(await db.Messages.Select(m => m.TicketId).ToListAsync(), id => id == tb.Id);
        Assert.DoesNotContain(await db.Requesters.Select(r => r.Id).ToListAsync(), id => id == RequesterOf(tb));
        Assert.Empty(await db.Tickets.Include(x => x.Requester).Where(x => x.Id == tb.Id).ToListAsync());
    }

    private sealed class FixedScope(Guid[] ids) : IDepartmentScope
    {
        public bool IsLoaded => true;

        public Guid[] DepartmentIds => ids;

        public Task EnsureLoadedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    [Fact]
    public async Task Ticket_routes_never_reveal_tickets_of_other_departments()
    {
        var a = await fixture.NewDeptAsync();
        var b = await fixture.NewDeptAsync();
        var tb = await b.Agent.CreateTicketAsync(b.Department);
        var ghost = Guid.NewGuid();
        var u = a.Admin;

        async Task<string> Fingerprint(Func<Guid, Task<ApiResponse>> call, Guid id)
        {
            var response = (await call(id)).Expect(HttpStatusCode.NotFound);
            var node = System.Text.Json.Nodes.JsonNode.Parse(response.Body)!.AsObject();
            node.Remove("traceId");
            node.Remove("instance");
            return node.ToJsonString().Replace(id.ToString(), "ID", StringComparison.Ordinal);
        }

        var calls = new List<Func<Guid, Task<ApiResponse>>>
        {
            id => u.GetTicketAsync(id),
            id => u.Client.PatchAsync(TicketKit.Url(id), new { version = "1", status = "Closed" }),
            id => u.AssignAsync(id, "1", null, u.Id),
            id => u.PostMessageAsync(id, "Public", "x"),
            id => u.Client.GetAsync(TicketKit.Url(id, "/messages")),
            id => u.Client.GetAsync(TicketKit.Url(id, "/activity")),
        };
        foreach (var call in calls)
        {
            Assert.Equal(await Fingerprint(call, ghost), await Fingerprint(call, tb.Id));
        }

        // Nothing changed.
        Assert.Equal(tb.Version, (await b.Agent.GetTicketAsync(tb.Id)).Version);
        Assert.Equal(1, await fixture.ScalarAsync<long>("SELECT count(*) FROM ticketing.messages WHERE ticket_id = @t", TicketKit.P("t", tb.Id)));
        // A department id in the body or query never overrides the ticket's persisted department.
        (await a.Agent.Client.PostAsync($"{b.Department.Url}/tickets", TicketHelpers.NewTicketBody())).Expect(HttpStatusCode.NotFound);
        var smuggled = (await a.Agent.Client.PostAsync($"{a.Department.Url}/tickets", TicketHelpers.NewTicketBody(extra: new { DepartmentId = b.Id }))).Expect(HttpStatusCode.Created);
        Assert.Equal(a.Id, smuggled.Json.GetProperty("departmentId").GetGuid());
    }

    // ----- permission model -----

    [Fact]
    public void Ticket_permissions_are_content_permissions_mapped_to_roles_as_documented()
    {
        string[] codes =
        [
            PermissionCodes.TicketsRead, PermissionCodes.TicketsCreate, PermissionCodes.TicketsReply, PermissionCodes.TicketsNote,
            PermissionCodes.TicketsNotesRead, PermissionCodes.TicketsUpdate, PermissionCodes.TicketsAssignSelf, PermissionCodes.TicketsAssignTeam, PermissionCodes.TicketsAssignAny,
        ];
        Assert.All(codes, c => Assert.Equal(PermissionKind.Content, PermissionCatalogue.Get(c).Kind));
        Assert.All(codes, c => Assert.Equal(EvaluationOutcome.NotFound, PermissionEvaluator.Evaluate(PermissionCatalogue.Get(c), true, null, true))); // never via the platform flag

        string[] viewer = [PermissionCodes.TicketsRead, PermissionCodes.TicketsNotesRead];
        string[] agent = [.. viewer, PermissionCodes.TicketsCreate, PermissionCodes.TicketsReply, PermissionCodes.TicketsNote, PermissionCodes.TicketsUpdate, PermissionCodes.TicketsAssignSelf, PermissionCodes.TicketsAssignTeam];
        string[] lead = [.. agent, PermissionCodes.TicketsAssignAny];
        foreach (var (role, expected) in new[] { (DepartmentRoles.Viewer, viewer), (DepartmentRoles.Agent, agent), (DepartmentRoles.TeamLead, lead), (DepartmentRoles.DepartmentAdmin, lead) })
        {
            Assert.Equal(expected.Order(), RoleCatalogue.PermissionsOf(role).Where(p => p.StartsWith("tickets.", StringComparison.Ordinal)).Order());
        }
    }

    [Fact]
    public async Task Me_lists_the_ticket_permissions_per_membership_and_none_for_platform_admins_without_membership()
    {
        var d = await fixture.NewDeptAsync();
        var perms = (await d.Agent.Client.GetAsync("/api/me")).Json.GetProperty("memberships").EnumerateArray().Single()
            .GetProperty("permissions").EnumerateArray().Select(p => p.GetString()!).Where(p => p.StartsWith("tickets.", StringComparison.Ordinal)).ToList();
        Assert.Equal(
            AgentTicketPermissions.Order(StringComparer.Ordinal),
            perms.Order(StringComparer.Ordinal));

        var admin = await fixture.PlatformAdminAsync();
        var adminPerms = (await admin.Client.GetAsync("/api/me")).Json.GetProperty("memberships").EnumerateArray()
            .SelectMany(m => m.GetProperty("permissions").EnumerateArray()).Select(p => p.GetString()!);
        Assert.DoesNotContain(adminPerms, p => p.StartsWith("tickets.", StringComparison.Ordinal));
    }
}
