using System.Net;
using System.Text.Json;
using Helpdesk.Host.Tests.Infrastructure;
using Helpdesk.Host.Tests.Ticketing;

namespace Helpdesk.Host.Tests.Productivity;

/// <summary>Canned responses (personal + department scope rules) and ticket search (scope, fields, snippets, wildcards).</summary>
public sealed class CannedAndSearchTests(HelpdeskFixture fixture) : IClassFixture<HelpdeskFixture>
{
    // ----- canned responses -----

    [Fact]
    public async Task Personal_responses_belong_to_their_owner_alone()
    {
        var d = await fixture.NewDeptAsync();
        var title = $"Greeting {Unique.Id()}";
        var created = (await d.Agent.Client.PostAsync("/api/canned-responses", new { scope = "Personal", title, body = "Hello,\nthanks for contacting us." })).Expect(HttpStatusCode.Created);
        Assert.Equal("Personal", created.Json.Str("scope"));
        Assert.True(created.Json.GetProperty("canManage").GetBoolean());

        Assert.Contains((await d.Agent.Client.GetAsync("/api/canned-responses")).Json.EnumerateArray(), c => c.GetProperty("id").GetGuid() == created.Id);
        // Even the department admin neither sees nor edits somebody's personal response.
        Assert.DoesNotContain((await d.Admin.Client.GetAsync("/api/canned-responses?includeInactive=true")).Json.EnumerateArray(), c => c.GetProperty("id").GetGuid() == created.Id);
        (await d.Admin.Client.PatchAsync($"/api/canned-responses/{created.Id}", new { version = created.Version, title = "stolen" })).Expect(HttpStatusCode.NotFound);

        var edited = (await d.Agent.Client.PatchAsync($"/api/canned-responses/{created.Id}", new { version = created.Version, title = title + " v2", isActive = false })).Expect(HttpStatusCode.OK);
        Assert.False(edited.Json.GetProperty("isActive").GetBoolean());
        Assert.DoesNotContain((await d.Agent.Client.GetAsync("/api/canned-responses")).Json.EnumerateArray(), c => c.GetProperty("id").GetGuid() == created.Id); // inactive hidden from the picker
        Assert.Contains((await d.Agent.Client.GetAsync("/api/canned-responses?includeInactive=true")).Json.EnumerateArray(), c => c.GetProperty("id").GetGuid() == created.Id);
        (await d.Agent.Client.PatchAsync($"/api/canned-responses/{created.Id}", new { version = created.Version, title = "stale" })).ExpectCode(HttpStatusCode.Conflict, "version_conflict");
    }

    [Fact]
    public async Task Department_responses_are_managed_by_department_admins_and_visible_to_that_departments_agents_only()
    {
        var a = await fixture.NewDeptAsync();
        var b = await fixture.NewDeptAsync();
        var title = $"Reset password {Unique.Id()}";
        var body = new { scope = "Department", departmentId = a.Id, title, body = "Please reset it from the portal." };

        (await a.Agent.Client.PostAsync("/api/canned-responses", body)).Expect(HttpStatusCode.Forbidden); // can reply, cannot manage
        (await a.Viewer.Client.PostAsync("/api/canned-responses", body)).Expect(HttpStatusCode.Forbidden);
        (await b.Admin.Client.PostAsync("/api/canned-responses", body)).Expect(HttpStatusCode.NotFound); // admin of ANOTHER department: no access to this one
        (await (await fixture.NewUserAsync()).Client.PostAsync("/api/canned-responses", body)).Expect(HttpStatusCode.NotFound);
        var created = (await a.Admin.Client.PostAsync("/api/canned-responses", body)).Expect(HttpStatusCode.Created);
        Assert.Equal(a.Id, created.Json.GetProperty("departmentId").GetGuid());

        bool Seen(ApiResponse r) => r.Json.EnumerateArray().Any(c => c.GetProperty("id").GetGuid() == created.Id);
        Assert.True(Seen(await a.Agent.Client.GetAsync("/api/canned-responses")));
        Assert.True(Seen(await a.Lead.Client.GetAsync("/api/canned-responses")));
        Assert.False(Seen(await a.Viewer.Client.GetAsync("/api/canned-responses"))); // viewers cannot reply or note: nothing to insert
        Assert.False(Seen(await b.Agent.Client.GetAsync("/api/canned-responses")));
        Assert.False(Seen(await b.Admin.Client.GetAsync("/api/canned-responses?departmentId=" + a.Id)));

        // Agents cannot edit; the admin can, another department's admin gets 404, and department changes are audited.
        (await a.Agent.Client.PatchAsync($"/api/canned-responses/{created.Id}", new { version = created.Version, title = "x" })).Expect(HttpStatusCode.Forbidden);
        (await b.Admin.Client.PatchAsync($"/api/canned-responses/{created.Id}", new { version = created.Version, title = "x" })).Expect(HttpStatusCode.NotFound);
        var off = (await a.Admin.Client.PatchAsync($"/api/canned-responses/{created.Id}", new { version = created.Version, isActive = false })).Expect(HttpStatusCode.OK);
        Assert.False(Seen(await a.Agent.Client.GetAsync("/api/canned-responses")));
        Assert.True(Seen(await a.Admin.Client.GetAsync("/api/canned-responses?includeInactive=true")));
        Assert.False(Seen(await a.Agent.Client.GetAsync("/api/canned-responses?includeInactive=true"))); // inactive ones are for managers only
        Assert.Contains(await fixture.AuditOfAsync(created.Id), x => x is { Action: "canned_response.created", Category: "ticketing" } && x.DepartmentId == a.Id);
        Assert.Contains(await fixture.AuditOfAsync(created.Id), x => x.Action == "canned_response.updated");
        Assert.NotEmpty(off.Version);
    }

    [Fact]
    public async Task Canned_responses_are_validated_and_scope_shapes_are_enforced()
    {
        var d = await fixture.NewDeptAsync();
        string[] Errors(ApiResponse r) => [.. r.Expect(HttpStatusCode.BadRequest).Json.GetProperty("errors").EnumerateObject().Select(p => p.Name)];

        Assert.Contains("title", Errors(await d.Agent.Client.PostAsync("/api/canned-responses", new { scope = "Personal", title = " ", body = "x" })));
        Assert.Contains("body", Errors(await d.Agent.Client.PostAsync("/api/canned-responses", new { scope = "Personal", title = "t", body = new string('x', 5001) })));
        Assert.Contains("body", Errors(await d.Agent.Client.PostAsync("/api/canned-responses", new { scope = "Personal", title = "t", body = "nul\u0000" })));
        Assert.Contains("scope", Errors(await d.Agent.Client.PostAsync("/api/canned-responses", new { scope = "Global", title = "t", body = "x" })));
        Assert.Contains("departmentId", Errors(await d.Admin.Client.PostAsync("/api/canned-responses", new { scope = "Department", title = "t", body = "x" })));
        Assert.Contains("departmentId", Errors(await d.Agent.Client.PostAsync("/api/canned-responses", new { scope = "Personal", departmentId = d.Id, title = "t", body = "x" })));
        (await fixture.CreateAnonymousClient().GetAsync("/api/canned-responses")).Expect(HttpStatusCode.Unauthorized);
        // A user with no reply/note permission anywhere cannot keep personal responses either.
        (await (await fixture.NewUserAsync()).Client.PostAsync("/api/canned-responses", new { scope = "Personal", title = "t", body = "x" })).Expect(HttpStatusCode.Forbidden);
    }

    // ----- search -----

    private static async Task<List<JsonElement>> SearchAsync(TestUser user, string q, string extra = "") =>
        [.. (await user.Client.GetAsync($"/api/tickets?queue=all&q={Uri.EscapeDataString(q)}&status=Open&status=Pending&status=Resolved&status=Closed{extra}")).Expect(HttpStatusCode.OK).Json.GetProperty("items").EnumerateArray()];

    [Fact]
    public async Task Search_covers_reference_subject_requester_and_message_text_with_context()
    {
        var d = await fixture.NewDeptAsync();
        var token = $"zq{Unique.Id()}";
        var email = $"{token}.person@customer.test";
        var t = await d.Agent.CreateTicketAsync(d.Department, new { subject = $"Router {token} fault", description = $"The uplink port {token}-body drops packets all day.", requester = new { email, displayName = $"Ana {token}" } });
        var reference = t.Json.Str("reference");
        var plain = await d.Agent.CreateTicketAsync(d.Department, new { subject = "Plain subject", description = $"Only the body mentions {token}-needle here.", requester = new { email = $"other{Unique.Id()}@customer.test" } });

        var bySubject = await SearchAsync(d.Agent, $"Router {token}");
        Assert.Equal("subject", Assert.Single(bySubject).GetProperty("match").Str("field"));
        Assert.Equal(reference, Assert.Single(await SearchAsync(d.Agent, reference.ToLowerInvariant())).Str("reference"));
        Assert.Contains(await SearchAsync(d.Agent, email), r => r.GetProperty("id").GetGuid() == t.Id);
        Assert.Contains(await SearchAsync(d.Agent, $"Ana {token}"), r => r.GetProperty("id").GetGuid() == t.Id);

        var byMessage = Assert.Single(await SearchAsync(d.Agent, $"{token}-needle"));
        Assert.Equal(plain.Id, byMessage.GetProperty("id").GetGuid());
        Assert.Equal("message", byMessage.GetProperty("match").Str("field"));
        Assert.Contains("needle", byMessage.GetProperty("match").Str("snippet"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Search_never_crosses_departments_and_leaves_no_trace_of_hidden_tickets()
    {
        var a = await fixture.NewDeptAsync();
        var b = await fixture.NewDeptAsync();
        var secret = $"sx{Unique.Id()}";
        var inB = await b.Agent.CreateTicketAsync(b.Department, new { subject = $"Confidential {secret}", description = $"Body with {secret}-text", requester = new { email = $"{secret}@client.test", displayName = secret } });
        await b.Agent.PostMessageAsync(inB.Id, "Internal", $"Private note {secret}-note");

        foreach (var q in new[] { secret, inB.Json.Str("reference"), $"{secret}-text", $"{secret}-note", $"{secret}@client.test" })
        {
            Assert.Empty(await SearchAsync(a.Agent, q));
            Assert.Empty(await SearchAsync(a.Admin, q));
            Assert.Empty(await SearchAsync(await fixture.NewUserAsync(), q));
            Assert.Empty(await SearchAsync(await fixture.PlatformAdminAsync(), q)); // administration is not content access
        }

        // The same search for a member of B finds it, and the note only as a note hit for those who can read notes.
        Assert.Single(await SearchAsync(b.Agent, secret));
        var noteHit = Assert.Single(await SearchAsync(b.Agent, $"{secret}-note"));
        Assert.Equal("note", noteHit.GetProperty("match").Str("field"));
    }

    [Fact]
    public async Task Search_treats_wildcards_literally_validates_input_and_paginates()
    {
        var d = await fixture.NewDeptAsync();
        var marker = $"pg{Unique.Id()}";
        for (var i = 0; i < 4; i++)
        {
            await d.Agent.CreateTicketAsync(d.Department, new { subject = $"Page {marker} {i}", description = "x", requester = new { email = MailKitless.Email() } });
        }

        Assert.Empty(await SearchAsync(d.Agent, "%%"));
        Assert.Empty(await SearchAsync(d.Agent, "__"));
        Assert.Empty(await SearchAsync(d.Agent, "\\\\"));
        (await d.Agent.Client.GetAsync("/api/tickets?q=a")).Expect(HttpStatusCode.BadRequest);
        (await d.Agent.Client.GetAsync("/api/tickets?q=" + new string('a', 101))).Expect(HttpStatusCode.BadRequest);

        var first = (await d.Agent.Client.GetAsync($"/api/tickets?q={marker}&status=Open&limit=3")).Expect(HttpStatusCode.OK);
        Assert.Equal(3, first.Ids().Length);
        var second = (await d.Agent.Client.GetAsync($"/api/tickets?q={marker}&status=Open&limit=3&cursor={first.NextCursor()}")).Expect(HttpStatusCode.OK);
        Assert.Single(second.Ids());
        Assert.Null(second.NextCursor());
    }

    private static class MailKitless
    {
        public static string Email() => $"p{Unique.Id()}@customer.test";
    }
}
