#pragma warning disable CA1861 // one-off request payloads
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Helpdesk.Host.Tests.Infrastructure;
using Helpdesk.Host.Tests.Mail;
using Helpdesk.Host.Tests.Portal;
using Helpdesk.Modules.Mail.Providers;
using Helpdesk.SharedKernel.Authorization;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.SignalR.Client;

namespace Helpdesk.Host.Tests.Ticketing;

/// <summary>
/// Department isolation matrix for the surfaces added after the original matrix (search, history, merge, links, followers, types, fields, forms, mailboxes,
/// attachments, realtime). Three departments (IT, HR, Finance); the questions are always "what does an outsider learn?". The strongest check is
/// indistinguishability: a request naming a real object of another department must answer exactly like one naming an object that does not exist.
/// </summary>
public sealed class DepartmentIsolationMatrixTests(HelpdeskFixture fixture) : IClassFixture<HelpdeskFixture>, IAsyncLifetime
{
    private static readonly byte[] Pdf = [.. "%PDF-1.7\n1 0 obj\n"u8];
    private readonly List<HubConnection> _connections = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var c in _connections)
        {
            await c.DisposeAsync();
        }
    }

    private sealed record World(
        Dept It, Dept Hr, Dept Fin, string Token, string RequesterEmail,
        ApiResponse ItTicket, ApiResponse ItMerged, ApiResponse ItAttachmentTicket, Guid ItAttachmentId, ApiResponse HrTicket,
        ApiResponse ItType, ApiResponse ItField, Guid ItForm, TestUser Both)
    {
        public Guid[] ItTicketIds => [ItTicket.Id, ItMerged.Id, ItAttachmentTicket.Id];

        public string[] Secrets => [Token];
    }

    private async Task<World> WorldAsync()
    {
        var it = await fixture.NewDeptAsync();
        var hr = await fixture.NewDeptAsync();
        var fin = await fixture.NewDeptAsync();
        var token = $"zq{Unique.Id()}";
        var email = $"dana-{token}@example.test";

        var main = (await it.TicketForAsync(email, $"{token} VPN outage")).Expect(HttpStatusCode.Created);
        var merged = (await it.TicketForAsync(email, $"{token} merged duplicate")).Expect(HttpStatusCode.Created);
        (await it.Agent.PostMessageAsync(main.Id, "Public", $"public-body-{token}")).Expect(HttpStatusCode.Created);
        (await it.Agent.PostMessageAsync(main.Id, "Internal", $"note-body-{token}")).Expect(HttpStatusCode.Created);
        (await it.Agent.Client.PostAsync(TicketKit.Url(main.Id, "/followers/me"))).Expect(HttpStatusCode.OK);
        var mainDetail = (await it.Agent.GetTicketAsync(main.Id)).Expect(HttpStatusCode.OK);
        var mergedDetail = (await it.Agent.GetTicketAsync(merged.Id)).Expect(HttpStatusCode.OK);
        (await it.Agent.Client.PostAsync(TicketKit.Url(main.Id, "/merge"), new { version = mainDetail.Version, sources = new[] { new { ticketId = merged.Id, version = mergedDetail.Version } } })).Expect(HttpStatusCode.OK);

        var box = await fixture.CreateMailboxAsync(it);
        var attSubject = $"{token} attachment";
        await fixture.ReceiveAsync(box, MailKit.Mail(MailKit.NewSender(), attSubject) with { Attachments = [new FakeAttachment("secret.pdf", Pdf)] });
        var attTicketId = Assert.Single(await it.Agent.TicketsAsync(it, attSubject)).GetProperty("id").GetGuid();
        var attTicket = (await it.Agent.GetTicketAsync(attTicketId)).Expect(HttpStatusCode.OK);
        var attachmentId = (await it.Agent.MessagesAsync(attTicketId)).Single().GetProperty("attachments")[0].GetProperty("id").GetGuid();

        var type = (await it.Admin.Client.PostAsync($"{it.Department.Url}/ticket-types", new { name = $"{token} Incident" })).Expect(HttpStatusCode.Created);
        var field = (await it.Admin.Client.PostAsync($"{it.Department.Url}/ticket-fields", PortalKit.Field($"{token} Asset tag", "ShortText", key: "asset_tag"))).Expect(HttpStatusCode.Created);
        var form = (await it.FormAsync(fixture, $"{token} Request form")).FormId;
        var hrTicket = (await hr.TicketForAsync($"hr-{Unique.Id()}@example.test", "HR matter")).Expect(HttpStatusCode.Created);
        var both = await fixture.NewMemberAsync(it.Department, "Agent");
        await fixture.AddMemberAsync(hr.Department, both, "Agent");
        return new World(it, hr, fin, token, email, main, merged, attTicket, attachmentId, hrTicket, type, field,
            form, both);
    }

    // ----- helpers -----

    private static string Normalised(ApiResponse r)
    {
        try
        {
            if (JsonNode.Parse(r.Body) is JsonObject o)
            {
                o.Remove("traceId");
                return o.ToJsonString();
            }
        }
        catch (JsonException)
        {
        }

        return r.Body;
    }

    /// <summary>The call with a real foreign id and the same call with a random id must be indistinguishable.</summary>
    private static async Task SameAsRandomAsync(string what, Func<Guid, Task<ApiResponse>> call, Guid real)
    {
        var foreign = await call(real);
        var missing = await call(Guid.NewGuid());
        Assert.False(foreign.Status is >= HttpStatusCode.OK and < HttpStatusCode.MultipleChoices, $"{what}: succeeded for a foreign object ({foreign.Status})");
        Assert.True(foreign.Status == missing.Status && Normalised(foreign) == Normalised(missing), $"{what}: differs from a missing object: {foreign.Status} {foreign.Body} vs {missing.Status} {missing.Body}");
    }

    private static void NoSecrets(World w, string what, params ApiResponse[] responses)
    {
        foreach (var r in responses)
        {
            foreach (var s in w.Secrets)
            {
                Assert.False(r.Body.Contains(s, StringComparison.OrdinalIgnoreCase), $"{what}: leaked {s} in {r.Body}");
            }
        }
    }

    private static Guid[] Ids(ApiResponse page) => page.Ids();

    // ----- tickets -----

    [Fact]
    public async Task A_user_of_another_department_gets_the_same_404_as_for_a_missing_ticket_on_every_ticket_route()
    {
        var w = await WorldAsync();
        var hr = w.Hr.Agent;
        var someId = Guid.NewGuid();
        (string Method, string Suffix, object? Body)[] routes =
        [
            ("GET", "", null), ("GET", "/messages", null), ("GET", "/activity", null), ("GET", "/properties", null), ("GET", "/sla", null),
            ("GET", "/followers", null), ("GET", "/links", null), ("GET", "/link-candidates?q=ab", null), ("GET", "/follower-candidates?q=a", null),
            ("GET", $"/attachments/{w.ItAttachmentId}", null),
            ("POST", "/messages", new { kind = "Public", body = "x" }), ("POST", "/messages", new { kind = "Internal", body = "x" }),
            ("POST", "/followers/me", null), ("POST", $"/followers/{w.Hr.Agent.Id}", null),
            ("POST", "/merge", new { version = "1", sources = new[] { new { ticketId = w.HrTicket.Id, version = "1" } } }),
            ("POST", "/links", new { ticketId = w.HrTicket.Id, type = "Related" }),
            ("PATCH", "", new { version = "1", subject = "x" }), ("PUT", "/assignment", new { version = "1", assigneeUserId = (Guid?)null }),
        ];
        foreach (var target in new[] { w.ItTicket, w.ItMerged, w.ItAttachmentTicket })
        {
            foreach (var (method, suffix, body) in routes)
            {
                foreach (var key in new[] { target.Id.ToString(), target.Reference() })
                {
                    var real = await hr.Client.SendAsync(new HttpMethod(method), $"/api/tickets/{key}{suffix}", body);
                    Assert.True(real.Status == HttpStatusCode.NotFound, $"{method} {suffix} {key}: {real.Status} {real.Body}");
                    NoSecrets(w, $"{method} {suffix}", real);
                }

                var missing = await hr.Client.SendAsync(new HttpMethod(method), $"/api/tickets/{someId}{suffix}", body);
                var viaId = await hr.Client.SendAsync(new HttpMethod(method), $"/api/tickets/{target.Id}{suffix}", body);
                Assert.Equal(Normalised(missing), Normalised(viaId));
            }
        }

        // Bulk reports a foreign ticket exactly like a missing one.
        var bulk = (await hr.Client.PostAsync("/api/tickets/bulk", new { ticketIds = new[] { w.ItTicket.Id, someId }, priority = "High" })).Expect(HttpStatusCode.OK);
        Assert.All(bulk.Json.GetProperty("results").EnumerateArray(), r => Assert.Equal("not_found", r.Str("outcome")));
        Assert.Equal("Normal", (await w.It.Agent.GetTicketAsync(w.ItTicket.Id)).Expect(HttpStatusCode.OK).Json.Str("priority"));
    }

    [Fact]
    public async Task Queues_filters_counts_views_and_search_inside_an_unreadable_department_return_nothing()
    {
        var w = await WorldAsync();
        var hr = w.Hr.Agent;
        var before = (await hr.Client.GetAsync("/api/tickets/queue-counts")).Expect(HttpStatusCode.OK).Body;

        foreach (var queue in new[] { "my", "unassigned", "all", "following" })
        {
            var page = (await hr.Client.GetAsync($"/api/tickets?queue={queue}&status=Open&status=Pending&status=Resolved&status=Closed&limit=100")).Expect(HttpStatusCode.OK);
            Assert.Empty(Ids(page).Intersect(w.ItTicketIds));
            NoSecrets(w, queue, page);
            var forced = (await hr.Client.GetAsync($"/api/tickets?queue={queue}&departmentId={w.It.Id}")).Expect(HttpStatusCode.OK);
            Assert.Empty(Ids(forced));
            Assert.Null(forced.NextCursor());
        }

        // Search: reference, requester email, subject, public body, internal note body and a merged ticket's reference, all server-side inside the caller's scope.
        string[] terms = [w.ItTicket.Reference(), w.ItMerged.Reference(), w.RequesterEmail, w.Token, $"public-body-{w.Token}", $"note-body-{w.Token}", $"{w.Token} merged duplicate"];
        foreach (var term in terms)
        {
            var found = (await hr.Client.GetAsync($"/api/tickets?q={Uri.EscapeDataString(term)}&status=Open&status=Pending&status=Resolved&status=Closed")).Expect(HttpStatusCode.OK);
            Assert.Empty(Ids(found));
            Assert.Null(found.NextCursor());
            NoSecrets(w, $"search {term}", found);
        }

        // Saved views cannot be used to reach other departments, and counts do not move with another department's activity.
        var view = (await hr.Client.PostAsync("/api/views", new { name = "IT", filter = new { queue = "all", departmentId = w.It.Id, q = w.Token } })).Expect(HttpStatusCode.Created);
        Assert.Empty(Ids((await hr.Client.GetAsync($"/api/tickets?view={view.Id}")).Expect(HttpStatusCode.OK)));
        await w.It.TicketForAsync($"x-{Unique.Id()}@example.test", "another IT ticket");
        Assert.Equal(before, (await hr.Client.GetAsync("/api/tickets/queue-counts")).Expect(HttpStatusCode.OK).Body);

        // The same searches work for the department that owns the data (the matrix is not just "everything is empty").
        Assert.Contains(w.ItTicket.Id, Ids((await w.It.Agent.Client.GetAsync($"/api/tickets?q={Uri.EscapeDataString($"note-body-{w.Token}")}&status=Open")).Expect(HttpStatusCode.OK)));
        Assert.Contains(w.ItMerged.Id, Ids((await w.It.Agent.Client.GetAsync($"/api/tickets?q={w.ItMerged.Reference()}")).Expect(HttpStatusCode.OK)));

        // Someone with IT + HR sees both (and still nothing of Finance); the platform administrator (S-02: every permission in every department) sees them all.
        var finTicket = (await w.Fin.TicketForAsync($"f-{Unique.Id()}@example.test", $"{w.Token} payroll")).Expect(HttpStatusCode.Created);
        var seen = Ids((await w.Both.Client.GetAsync($"/api/tickets?q={w.Token}&status=Open&status=Pending&status=Resolved&status=Closed")).Expect(HttpStatusCode.OK));
        Assert.Contains(w.ItTicket.Id, seen);
        Assert.DoesNotContain(finTicket.Id, seen);
        (await w.Both.GetTicketAsync(finTicket.Id)).Expect(HttpStatusCode.NotFound);
        var admin = await fixture.PlatformAdminAsync();
        var adminSeen = Ids((await admin.Client.GetAsync($"/api/tickets?q={w.Token}")).Expect(HttpStatusCode.OK));
        Assert.Contains(w.ItTicket.Id, adminSeen);
        Assert.Contains(finTicket.Id, adminSeen);
        (await admin.GetTicketAsync(w.ItTicket.Id)).Expect(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Requester_history_counts_and_ordering_never_include_other_departments()
    {
        var w = await WorldAsync();
        var email = $"dana-{Unique.Id()}@example.test";
        var itTickets = new List<ApiResponse>();
        for (var i = 0; i < 4; i++)
        {
            itTickets.Add((await w.It.TicketForAsync(email, $"IT request {i}")).Expect(HttpStatusCode.Created));
        }

        var hrTickets = new List<ApiResponse>();
        for (var i = 0; i < 2; i++)
        {
            hrTickets.Add((await w.Hr.TicketForAsync(email, $"HR request {i}")).Expect(HttpStatusCode.Created));
        }

        var itRequester = (await w.It.Agent.GetTicketAsync(itTickets[0].Id)).Json.GetProperty("requester").GetProperty("id").GetGuid();
        var hrRequester = (await w.Hr.Agent.GetTicketAsync(hrTickets[0].Id)).Json.GetProperty("requester").GetProperty("id").GetGuid();

        var profile = (await w.Hr.Agent.Client.GetAsync($"/api/requesters/{hrRequester}")).Expect(HttpStatusCode.OK);
        Assert.Equal(2, profile.Json.GetProperty("totalCount").GetInt32());
        Assert.Equal(2, profile.Json.GetProperty("openCount").GetInt32());
        Assert.Equal(0, profile.Json.GetProperty("resolvedCount").GetInt32());
        var list = (await w.Hr.Agent.Client.GetAsync($"/api/requesters/{hrRequester}/tickets?limit=1")).Expect(HttpStatusCode.OK);
        Assert.Single(list.Json.GetProperty("items").EnumerateArray());
        var rest = (await w.Hr.Agent.Client.GetAsync($"/api/requesters/{hrRequester}/tickets?limit=1&cursor={list.NextCursor()}")).Expect(HttpStatusCode.OK);
        Assert.Null(rest.NextCursor());
        foreach (var body in new[] { profile.Body, list.Body, rest.Body })
        {
            Assert.DoesNotContain("IT request", body, StringComparison.Ordinal);
        }

        Assert.Equal(2, (await w.Hr.Agent.GetTicketAsync(hrTickets[0].Id)).Json.GetProperty("requester").GetProperty("totalTicketCount").GetInt32());
        (await w.Hr.Agent.Client.GetAsync($"/api/requesters/{itRequester}")).Expect(HttpStatusCode.NotFound);
        (await w.Hr.Agent.Client.GetAsync($"/api/requesters/{itRequester}/tickets")).Expect(HttpStatusCode.NotFound);
        await SameAsRandomAsync("requester", id => w.Hr.Agent.Client.GetAsync($"/api/requesters/{id}"), itRequester);
        await SameAsRandomAsync("requester tickets", id => w.Hr.Agent.Client.GetAsync($"/api/requesters/{id}/tickets"), itRequester);
        (await w.Hr.Agent.Client.GetAsync($"{w.Hr.Department.Url}/requesters/lookup?q={email}")).Expect(HttpStatusCode.OK);
        var lookupIt = await w.Hr.Agent.Client.GetAsync($"{w.It.Department.Url}/requesters/lookup?q={email}");
        Assert.Equal(HttpStatusCode.NotFound, lookupIt.Status);

        // The person holding both departments sees the combined history; nobody else does.
        Assert.Equal(6, (await w.Both.Client.GetAsync($"/api/requesters/{hrRequester}")).Expect(HttpStatusCode.OK).Json.GetProperty("totalCount").GetInt32());
    }

    // ----- configuration -----

    [Fact]
    public async Task Administration_endpoints_of_another_department_answer_like_a_department_that_does_not_exist()
    {
        var w = await WorldAsync();
        var hrAdmin = w.Hr.Admin;
        string[] collections = ["ticket-types", "ticket-fields", "request-forms", "mailboxes", "sla-policies", "business-hours", "automations", "automations/history", "knowledge/categories", "knowledge/articles", "mail/attention", "members", "teams"];
        foreach (var collection in collections)
        {
            await SameAsRandomAsync($"GET {collection}", id => hrAdmin.Client.GetAsync($"/api/departments/{id}/{collection}"), w.It.Id);
        }

        string[] mutable = ["ticket-types", "ticket-fields", "request-forms", "mailboxes", "sla-policies", "business-hours", "automations", "knowledge/categories", "knowledge/articles"];
        foreach (var collection in mutable)
        {
            await SameAsRandomAsync($"POST {collection}", id => hrAdmin.Client.PostAsync($"/api/departments/{id}/{collection}", new { name = "x", title = "x" }), w.It.Id);
        }

        // Objects of another department by id, through the caller's own department route.
        await SameAsRandomAsync("type patch", id => hrAdmin.Client.PatchAsync($"{w.Hr.Department.Url}/ticket-types/{id}", new { version = "1", name = "x" }), w.ItType.Id);
        await SameAsRandomAsync("field patch", id => hrAdmin.Client.PatchAsync($"{w.Hr.Department.Url}/ticket-fields/{id}", new { version = "1", label = "x" }), w.ItField.Id);
        await SameAsRandomAsync("form patch", id => hrAdmin.Client.PatchAsync($"{w.Hr.Department.Url}/request-forms/{id}", new { version = "1", name = "x" }), w.ItForm);
        await SameAsRandomAsync("mailbox get", id => hrAdmin.Client.GetAsync($"{w.Hr.Department.Url}/mailboxes/{id}"), Guid.NewGuid());
        var itMailbox = (await w.It.Admin.Client.GetAsync($"{w.It.Department.Url}/mailboxes")).Expect(HttpStatusCode.OK).Json[0].GetProperty("id").GetGuid();
        await SameAsRandomAsync("mailbox get", id => hrAdmin.Client.GetAsync($"{w.Hr.Department.Url}/mailboxes/{id}"), itMailbox);
        await SameAsRandomAsync("mailbox patch", id => hrAdmin.Client.PatchAsync($"{w.Hr.Department.Url}/mailboxes/{id}", new { version = "1", name = "x" }), itMailbox);
        await SameAsRandomAsync("mail retry", id => hrAdmin.Client.PostAsync($"{w.Hr.Department.Url}/mail/{id}/retry"), Guid.NewGuid());

        // Platform admin may administer departments but the same rule applies to what the HR administrator can reach.
        Assert.DoesNotContain(w.Token, (await hrAdmin.Client.GetAsync($"{w.Hr.Department.Url}/ticket-types")).Expect(HttpStatusCode.OK).Body, StringComparison.Ordinal);
        Assert.DoesNotContain(w.Token, (await hrAdmin.Client.GetAsync($"{w.Hr.Department.Url}/ticket-fields")).Expect(HttpStatusCode.OK).Body, StringComparison.Ordinal);
        Assert.DoesNotContain(w.Token, (await hrAdmin.Client.GetAsync($"{w.Hr.Department.Url}/request-forms")).Expect(HttpStatusCode.OK).Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_automation_dry_run_and_history_of_the_own_department_cannot_be_pointed_at_a_foreign_ticket()
    {
        var w = await WorldAsync();
        var hrAdmin = w.Hr.Admin;
        var real = await hrAdmin.Client.PostAsync($"{w.Hr.Department.Url}/automations/test", new { ticket = w.ItTicket.Reference(), trigger = "TicketCreated" });
        var missing = await hrAdmin.Client.PostAsync($"{w.Hr.Department.Url}/automations/test", new { ticket = "ZZZ-999999", trigger = "TicketCreated" });
        Assert.Equal(HttpStatusCode.NotFound, real.Status);
        Assert.Equal(Normalised(missing), Normalised(real));
        var history = await hrAdmin.Client.GetAsync($"{w.Hr.Department.Url}/automations/history?ticket={w.ItTicket.Reference()}");
        Assert.DoesNotContain(w.Token, history.Body, StringComparison.Ordinal);
        Assert.NotEqual(HttpStatusCode.InternalServerError, history.Status);
        var sla = await w.Hr.Agent.Client.GetAsync($"/api/tickets/{w.ItTicket.Reference()}/sla");
        Assert.Equal(HttpStatusCode.NotFound, sla.Status);
    }

    [Fact]
    public async Task Crafted_ids_from_another_department_in_request_bodies_answer_like_ids_that_do_not_exist()
    {
        var w = await WorldAsync();
        var hr = w.Hr;
        var hrType = (await hr.Admin.Client.PostAsync($"{hr.Department.Url}/ticket-types", new { name = "HR type" })).Expect(HttpStatusCode.Created);
        var hrForm = (await hr.FormAsync(fixture, "HR form")).FormId;

        await SameAsRandomAsync("form -> type", id => hr.Admin.Client.PostAsync($"{hr.Department.Url}/request-forms", new { name = $"F {Unique.Id()}", ticketTypeId = id }), w.ItType.Id);
        await SameAsRandomAsync("form patch -> type", id => hr.Admin.Client.PatchAsync($"{hr.Department.Url}/request-forms/{hrForm}", new { version = "1", ticketTypeId = id }), w.ItType.Id);
        await SameAsRandomAsync("form -> field", id => hr.Admin.Client.PutAsync($"{hr.Department.Url}/request-forms/{hrForm}/fields", new { fields = new[] { new { fieldId = id } } }), w.ItField.Id);
        await SameAsRandomAsync("type -> field", id => hr.Admin.Client.PutAsync($"{hr.Department.Url}/ticket-types/{hrType.Id}/fields", new { fields = new[] { new { fieldId = id } } }), w.ItField.Id);
        await SameAsRandomAsync("create ticket with type", id => hr.Agent.Client.PostAsync($"{hr.Department.Url}/tickets", TicketHelpers.NewTicketBody("t", $"r-{Unique.Id()}@example.test", new { TicketTypeId = id })), w.ItType.Id);
        var hrDetail = (await hr.Agent.GetTicketAsync(w.HrTicket.Id)).Expect(HttpStatusCode.OK);
        await SameAsRandomAsync("properties type", id => hr.Agent.Client.PatchAsync(TicketKit.Url(w.HrTicket.Id, "/properties"), new { version = hrDetail.Version, ticketTypeId = id }), w.ItType.Id);
        await SameAsRandomAsync("assign agent", id => hr.Lead.AssignAsync(w.HrTicket.Id, hrDetail.Version, id), w.It.Agent.Id);
        await SameAsRandomAsync("create with assignee", id => hr.Lead.Client.PostAsync($"{hr.Department.Url}/tickets", TicketHelpers.NewTicketBody("t", $"r-{Unique.Id()}@example.test", new { AssigneeUserId = id })), w.It.Agent.Id);
        await SameAsRandomAsync("rule agent", id => hr.Admin.Client.PostAsync($"{hr.Department.Url}/automations", new { name = "x", trigger = "TicketCreated", conditions = Array.Empty<object>(), actions = new[] { new { type = "assignAgent", value = id.ToString(), text = (string?)null } } }), w.It.Agent.Id);
        await SameAsRandomAsync("sla policy type", id => hr.Admin.Client.PostAsync($"{hr.Department.Url}/sla-policies", new { name = "x", resolutionMinutes = 10, conditions = new[] { new { field = "ticketType", @operator = "in", values = new[] { id.ToString() } } } }), w.ItType.Id);

        // A field key belonging to another department's catalogue is just an unknown key here.
        var unknown = await hr.Agent.Client.PostAsync($"{hr.Department.Url}/tickets", TicketHelpers.NewTicketBody("t", $"r-{Unique.Id()}@example.test", new { Fields = new Dictionary<string, object> { ["asset_tag"] = "A1" } }));
        var random = await hr.Agent.Client.PostAsync($"{hr.Department.Url}/tickets", TicketHelpers.NewTicketBody("t", $"r-{Unique.Id()}@example.test", new { Fields = new Dictionary<string, object> { ["zzz_unknown"] = "A1" } }));
        Assert.Equal(unknown.Status, random.Status);
        Assert.DoesNotContain(w.Token, unknown.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("Asset tag", unknown.Body, StringComparison.Ordinal);
        Assert.Equal(Normalised(unknown).Replace("asset_tag", "K"), Normalised(random).Replace("zzz_unknown", "K"));
    }

    // ----- merge, links, followers -----

    [Fact]
    public async Task Merge_links_followers_and_their_candidate_searches_cannot_reach_another_department()
    {
        var w = await WorldAsync();
        var hr = w.Hr.Agent;
        var hrDetail = (await hr.GetTicketAsync(w.HrTicket.Id)).Expect(HttpStatusCode.OK);

        // An HR ticket route with a crafted IT ticket as the other side.
        await SameAsRandomAsync("merge source", id => hr.Client.PostAsync(TicketKit.Url(w.HrTicket.Id, "/merge"), new { version = hrDetail.Version, sources = new[] { new { ticketId = id, version = "1" } } }), w.ItTicket.Id);
        await SameAsRandomAsync("link", id => hr.Client.PostAsync(TicketKit.Url(w.HrTicket.Id, "/links"), new { ticketId = id, type = "Related" }), w.ItTicket.Id);
        await SameAsRandomAsync("link parent", id => hr.Client.PostAsync(TicketKit.Url(w.HrTicket.Id, "/links"), new { ticketId = id, type = "Parent" }), w.ItMerged.Id);
        await SameAsRandomAsync("follower", id => hr.Client.PostAsync(TicketKit.Url(w.HrTicket.Id, $"/followers/{id}")), w.It.Agent.Id);

        // Candidate searches only list the ticket's own department (by reference, subject or person).
        string[] queries = [w.ItTicket.Reference()[..3], w.Token, w.ItMerged.Reference(), "VPN"];
        foreach (var q in queries)
        {
            var candidates = (await hr.Client.GetAsync(TicketKit.Url(w.HrTicket.Id, $"/link-candidates?q={Uri.EscapeDataString(q)}"))).Expect(HttpStatusCode.OK);
            Assert.DoesNotContain(candidates.Json.EnumerateArray(), c => w.ItTicketIds.Contains(c.GetProperty("id").GetGuid()));
            NoSecrets(w, "link candidates", candidates);
        }

        var people = (await hr.Client.GetAsync(TicketKit.Url(w.HrTicket.Id, "/follower-candidates?q="))).Expect(HttpStatusCode.OK);
        Assert.DoesNotContain(people.Json.EnumerateArray(), c => c.GetProperty("userId").GetGuid() == w.It.Agent.Id);
        Assert.Contains(people.Json.EnumerateArray(), c => c.GetProperty("userId").GetGuid() == w.Hr.Agent.Id);

        // Even someone who can read both departments cannot cross them with these actions; and following never opened IT to the HR-only agent.
        (await w.Both.Client.PostAsync(TicketKit.Url(w.HrTicket.Id, "/links"), new { ticketId = w.ItTicket.Id, type = "Related" })).ExpectCode(HttpStatusCode.Conflict, "cross_department");
        (await w.Both.Client.PostAsync(TicketKit.Url(w.HrTicket.Id, $"/followers/{w.It.Agent.Id}"))).Expect(HttpStatusCode.BadRequest);
        Assert.Empty(Ids((await hr.Client.GetAsync("/api/tickets?queue=following")).Expect(HttpStatusCode.OK)));
        (await hr.Client.PostAsync(TicketKit.Url(w.HrTicket.Id, "/followers/me"))).Expect(HttpStatusCode.OK);
        (await hr.GetTicketAsync(w.ItTicket.Id)).Expect(HttpStatusCode.NotFound);
        Assert.Equal([w.HrTicket.Id], Ids((await hr.Client.GetAsync("/api/tickets?queue=following")).Expect(HttpStatusCode.OK)));

        // The merged ticket's canonical reference is never shown outside the department: a merged ticket is not reachable at all.
        (await hr.Client.GetAsync($"/api/tickets/{w.ItMerged.Reference()}")).Expect(HttpStatusCode.NotFound);
    }

    // ----- attachments, notes -----

    [Fact]
    public async Task Attachments_follow_the_ticket_boundary_and_internal_notes_keep_their_second_layer()
    {
        var w = await WorldAsync();
        var hr = w.Hr.Agent;
        var itDownload = await w.It.Agent.Client.Http.GetAsync($"/api/tickets/{w.ItAttachmentTicket.Id}/attachments/{w.ItAttachmentId}");
        Assert.Equal(HttpStatusCode.OK, itDownload.StatusCode);

        // The outsider tries the attachment id on the IT ticket (id or reference) and on one of their own tickets.
        foreach (var url in new[]
        {
            $"/api/tickets/{w.ItAttachmentTicket.Id}/attachments/{w.ItAttachmentId}",
            $"/api/tickets/{w.ItAttachmentTicket.Reference()}/attachments/{w.ItAttachmentId}",
            $"/api/tickets/{w.HrTicket.Id}/attachments/{w.ItAttachmentId}",
            $"/api/tickets/{w.HrTicket.Reference()}/attachments/{w.ItAttachmentId}",
            $"/api/tickets/{w.ItTicket.Id}/attachments/{w.ItAttachmentId}",
        })
        {
            var response = await hr.Client.Http.GetAsync(url);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        // Inside the department, notes still need tickets.notes.read: without it the merged conversation has no note content and no placeholder.
        await using var noNotes = WithoutPermissions(Helpdesk.SharedKernel.Authorization.PermissionCodes.TicketsNotesRead);
        var limited = await fixture.SignInAsync(w.It.Viewer.Subject, factory: noNotes);
        var messages = (await limited.Client.GetAsync(TicketKit.Url(w.ItTicket.Id, "/messages"))).Expect(HttpStatusCode.OK);
        Assert.DoesNotContain($"note-body-{w.Token}", messages.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("Internal", messages.Body, StringComparison.Ordinal);
        Assert.Contains($"public-body-{w.Token}", messages.Body, StringComparison.Ordinal);
        var search = (await limited.Client.GetAsync($"/api/tickets?q=note-body-{w.Token}&status=Open")).Expect(HttpStatusCode.OK);
        Assert.Empty(Ids(search));
    }

    // ----- realtime -----

    [Fact]
    public async Task An_outsider_receives_no_realtime_events_for_ticket_message_note_follower_link_or_merge_activity()
    {
        var w = await WorldAsync();
        var (outsider, outsiderEvents) = await ConnectAsync(w.Hr.Agent);
        var (insider, insiderEvents) = await ConnectAsync(w.It.Agent);
        _ = (outsider, insider);

        var other = (await w.It.TicketForAsync($"x-{Unique.Id()}@example.test", $"{w.Token} live")).Expect(HttpStatusCode.Created);
        (await w.It.Agent.PostMessageAsync(other.Id, "Public", "public activity")).Expect(HttpStatusCode.Created);
        (await w.It.Agent.PostMessageAsync(other.Id, "Internal", "note activity")).Expect(HttpStatusCode.Created);
        (await w.It.Lead.Client.PostAsync(TicketKit.Url(other.Id, "/followers/me"))).Expect(HttpStatusCode.OK);
        (await w.It.Agent.Client.PostAsync(TicketKit.Url(other.Id, "/links"), new { ticketId = w.ItTicket.Id, type = "Related" })).Expect(HttpStatusCode.Created);
        var current = (await w.It.Agent.GetTicketAsync(w.ItTicket.Id)).Expect(HttpStatusCode.OK);
        var otherDetail = (await w.It.Agent.GetTicketAsync(other.Id)).Expect(HttpStatusCode.OK);
        (await w.It.Agent.Client.PostAsync(TicketKit.Url(w.ItTicket.Id, "/merge"), new { version = current.Version, sources = new[] { new { ticketId = other.Id, version = otherDetail.Version } } })).Expect(HttpStatusCode.OK);

        Assert.True(await PortalKit.WaitForAsync(insiderEvents, e => e.GetProperty("kind").GetString() == "note", 6000));
        Assert.True(await PortalKit.SilentAsync(outsiderEvents, 1500));
        lock (outsiderEvents)
        {
            Assert.Empty(outsiderEvents);
        }

        // What the entitled client receives carries ids and a kind only.
        lock (insiderEvents)
        {
            foreach (var e in insiderEvents)
            {
                Assert.DoesNotContain("body", e.GetRawText(), StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(w.Token, e.GetRawText(), StringComparison.Ordinal);
            }
        }
    }

    private WebApplicationFactory<Program> WithoutPermissions(params string[] denied) =>
        fixture.Factory.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
        {
            var original = services.Single(s => s.ServiceType == typeof(IAuthorizer));
            services.AddScoped(original.ImplementationType!);
            services.AddScoped<IAuthorizer>(sp => new TicketIsolationLeakageTests.DenyingAuthorizer((IAuthorizer)sp.GetRequiredService(original.ImplementationType!), denied));
        }));

    private async Task<(HubConnection Connection, List<JsonElement> Events)> ConnectAsync(TestUser user)
    {
        using var http = fixture.Factory.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = false });
        var login = await http.GetAsync($"/api/auth/dev-login?subject={Uri.EscapeDataString(user.Subject)}&name={Uri.EscapeDataString(user.Subject)}&email={Uri.EscapeDataString(user.Subject)}@example.test&returnUrl=/");
        var cookies = Pairs(login);
        using var csrfRequest = new HttpRequestMessage(HttpMethod.Get, "/api/csrf");
        csrfRequest.Headers.Add("Cookie", string.Join("; ", cookies));
        var csrf = await http.SendAsync(csrfRequest);
        cookies.AddRange(Pairs(csrf));
        var token = JsonDocument.Parse(await csrf.Content.ReadAsStringAsync()).RootElement.GetProperty("token").GetString()!;
        var events = new List<JsonElement>();
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(fixture.Factory.Server.BaseAddress, "hubs/tickets"), o =>
            {
                o.Transports = HttpTransportType.LongPolling;
                o.HttpMessageHandlerFactory = _ => fixture.Factory.Server.CreateHandler();
                o.Headers["Cookie"] = string.Join("; ", cookies);
                o.Headers["X-CSRF-TOKEN"] = token;
            })
            .Build();
        connection.On<JsonElement>("ticketChanged", e =>
        {
            lock (events)
            {
                events.Add(e.Clone());
            }
        });
        _connections.Add(connection);
        await connection.StartAsync();
        return (connection, events);
    }

    private static List<string> Pairs(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values) ? [.. values.Select(v => v.Split(';')[0])] : [];
}
