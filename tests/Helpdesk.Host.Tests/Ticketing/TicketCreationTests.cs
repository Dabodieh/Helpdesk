using System.Net;
using Helpdesk.Host.Tests.Infrastructure;
using Npgsql;

namespace Helpdesk.Host.Tests.Ticketing;

/// <summary>Ticket creation, requester upsert, reference numbering and creation rules (docs/api/PHASE2_API.md, TICKETING.md).</summary>
public sealed class TicketCreationTests(HelpdeskFixture fixture) : IClassFixture<HelpdeskFixture>
{
    [Fact]
    public async Task Create_returns_the_detail_with_reference_defaults_and_the_first_public_requester_message()
    {
        var d = await fixture.NewDeptAsync();
        var email = $"Req-{Unique.Id()}@Example.Test";
        var response = (await d.Agent.Client.PostAsync($"{d.Department.Url}/tickets", TicketHelpers.NewTicketBody("Printer on fire", email))).Expect(HttpStatusCode.Created);

        var t = response.Json;
        Assert.Equal($"{d.Key}-1", t.Str("reference"));
        Assert.Equal(d.Key, t.Str("departmentKey"));
        Assert.Equal(d.Department.Name, t.Str("departmentName"));
        Assert.Equal("Open", t.Str("status"));
        Assert.Equal("Normal", t.Str("priority"));
        Assert.Equal("Printer on fire", t.Str("subject"));
        Assert.False(t.TryGetProperty("team", out _)); // Departments are the workgroups: there is no team
        Assert.Equal(JsonValueKindNull, t.GetProperty("assignee").ValueKind.ToString());
        Assert.Equal(JsonValueKindNull, t.GetProperty("resolvedAt").ValueKind.ToString());
        Assert.Equal(JsonValueKindNull, t.GetProperty("closedAt").ValueKind.ToString());
        Assert.False(string.IsNullOrEmpty(response.Version));
        Assert.Equal($"/api/tickets/{response.Id}", response.Message.Headers.Location?.ToString());
        var requester = t.GetProperty("requester");
        Assert.Equal(email, requester.Str("email"));
        Assert.Equal("Some Requester", requester.Str("displayName"));
        Assert.Equal(1, requester.GetProperty("openTicketCount").GetInt32());
        Assert.Equal(1, requester.GetProperty("totalTicketCount").GetInt32());

        var messages = (await d.Agent.Client.GetAsync(TicketKit.Url(response.Id, "/messages"))).Expect(HttpStatusCode.OK).Json.GetProperty("items").EnumerateArray().ToList();
        var first = Assert.Single(messages);
        Assert.Equal("Public", first.Str("kind"));
        Assert.Equal("Requester", first.Str("authorType"));
        Assert.Equal("manual", first.Str("source"));
        Assert.Equal("None", first.Str("deliveryState"));
        Assert.Equal("Something is broken.", first.Str("body"));
        Assert.Equal("Some Requester", first.GetProperty("author").Str("displayName"));
        Assert.Equal(requester.GetProperty("id").GetGuid(), first.GetProperty("author").GetProperty("id").GetGuid());

        Assert.Equal(d.Agent.Id, await fixture.ScalarAsync<Guid>("SELECT created_by_user_id FROM ticketing.tickets WHERE id = @t", TicketKit.P("t", response.Id)));
    }

    private const string JsonValueKindNull = "Null";

    [Fact]
    public async Task Numbers_are_sequential_per_department_and_independent_between_departments()
    {
        var a = await fixture.NewDeptAsync();
        var b = await fixture.NewDeptAsync();

        var a1 = await a.Agent.CreateTicketAsync(a.Department);
        var b1 = await b.Agent.CreateTicketAsync(b.Department);
        var a2 = await a.Agent.CreateTicketAsync(a.Department);
        var b2 = await b.Agent.CreateTicketAsync(b.Department);

        Assert.Equal($"{a.Key}-1", a1.Json.Str("reference"));
        Assert.Equal($"{a.Key}-2", a2.Json.Str("reference"));
        Assert.Equal($"{b.Key}-1", b1.Json.Str("reference"));
        Assert.Equal($"{b.Key}-2", b2.Json.Str("reference"));
    }

    [Fact]
    public async Task Concurrent_creation_yields_unique_gap_free_numbers_per_department()
    {
        var a = await fixture.NewDeptAsync();
        var b = await fixture.NewDeptAsync();

        var createsA = Enumerable.Range(0, 30).Select(_ => a.Agent.Client.PostAsync($"{a.Department.Url}/tickets", TicketHelpers.NewTicketBody()));
        var createsB = Enumerable.Range(0, 10).Select(_ => b.Agent.Client.PostAsync($"{b.Department.Url}/tickets", TicketHelpers.NewTicketBody()));
        var all = await Task.WhenAll(createsA.Concat(createsB));
        Assert.All(all, r => r.Expect(HttpStatusCode.Created));

        var numbersA = await Numbers(a.Id);
        var numbersB = await Numbers(b.Id);
        Assert.Equal(Enumerable.Range(1, 30), numbersA);
        Assert.Equal(Enumerable.Range(1, 10), numbersB);
        Assert.Equal(2, await fixture.ScalarAsync<int>("SELECT next_number FROM ticketing.department_counters WHERE department_id = @d", TicketKit.P("d", b.Id)) - 9);
    }

    private async Task<List<int>> Numbers(Guid departmentId)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT number FROM ticketing.tickets WHERE department_id = @d ORDER BY number", connection);
        command.Parameters.AddWithValue("d", departmentId);
        await using var reader = await command.ExecuteReaderAsync();
        var numbers = new List<int>();
        while (await reader.ReadAsync())
        {
            numbers.Add(reader.GetInt32(0));
        }

        return numbers;
    }

    [Fact]
    public async Task A_rejected_creation_does_not_consume_a_number()
    {
        var d = await fixture.NewDeptAsync();
        var first = await d.Agent.CreateTicketAsync(d.Department);

        var ok = await d.Agent.CreateTicketAsync(d.Department);
        Assert.Equal($"{d.Key}-1", first.Json.Str("reference"));
        Assert.Equal($"{d.Key}-2", ok.Json.Str("reference"));

        // A request that fails validation never reaches the allocator.
        (await d.Agent.Client.PostAsync($"{d.Department.Url}/tickets", TicketHelpers.NewTicketBody(subject: ""))).Expect(HttpStatusCode.BadRequest);
        var next = await d.Agent.CreateTicketAsync(d.Department);
        Assert.Equal($"{d.Key}-3", next.Json.Str("reference"));
    }

    [Fact]
    public async Task Renaming_the_department_never_changes_references()
    {
        var d = await fixture.NewDeptAsync();
        var before = await d.Agent.CreateTicketAsync(d.Department);
        var current = (await d.Admin.Client.GetAsync(d.Department.Url)).Expect(HttpStatusCode.OK);
        (await d.Admin.Client.PatchAsync(d.Department.Url, new { version = current.Version, name = $"Renamed {Unique.Id()}" })).Expect(HttpStatusCode.OK);

        var reloaded = (await d.Agent.GetTicketAsync(before.Id)).Expect(HttpStatusCode.OK);
        Assert.Equal(before.Json.Str("reference"), reloaded.Json.Str("reference"));
        var after = await d.Agent.CreateTicketAsync(d.Department);
        Assert.Equal($"{d.Key}-2", after.Json.Str("reference"));

        // The database refuses to rewrite a reference to something that is not key-number either.
        await Assert.ThrowsAsync<PostgresException>(() => fixture.ExecuteAsync("UPDATE ticketing.tickets SET reference = 'ZZ-99' WHERE id = @t", TicketKit.P("t", before.Id)));
    }

    [Fact]
    public async Task Requester_is_upserted_by_normalised_email_within_the_department_only()
    {
        var a = await fixture.NewDeptAsync();
        var b = await fixture.NewDeptAsync();
        var email = $"person-{Unique.Id()}@example.test";

        var t1 = await a.Agent.CreateTicketAsync(a.Department, TicketHelpers.NewTicketBody(email: email));
        var t2 = await a.Agent.CreateTicketAsync(a.Department, TicketHelpers.NewTicketBody(email: $"  {email.ToUpperInvariant()} "));
        var tb = await b.Agent.CreateTicketAsync(b.Department, TicketHelpers.NewTicketBody(email: email));

        var r1 = t1.Json.GetProperty("requester").GetProperty("id").GetGuid();
        Assert.Equal(r1, t2.Json.GetProperty("requester").GetProperty("id").GetGuid());
        Assert.NotEqual(r1, tb.Json.GetProperty("requester").GetProperty("id").GetGuid());
        Assert.Equal(2, t2.Json.GetProperty("requester").GetProperty("totalTicketCount").GetInt32());
        Assert.Equal(1, tb.Json.GetProperty("requester").GetProperty("totalTicketCount").GetInt32()); // counts are per department
        Assert.Equal(1, await fixture.ScalarAsync<long>("SELECT count(*) FROM ticketing.requesters WHERE department_id = @d AND email_normalised = @e", TicketKit.P("d", a.Id), TicketKit.P("e", email.ToLowerInvariant())));
        Assert.Equal(1, await fixture.CountAuditAsync("requester.created", r1));
    }

    [Fact]
    public async Task Requester_can_be_referenced_by_id_but_only_from_the_same_department()
    {
        var a = await fixture.NewDeptAsync();
        var b = await fixture.NewDeptAsync();
        var first = await a.Agent.CreateTicketAsync(a.Department);
        var requesterId = first.Json.GetProperty("requester").GetProperty("id").GetGuid();

        var again = (await a.Agent.Client.PostAsync($"{a.Department.Url}/tickets", new { subject = "again", description = "text", requesterId })).Expect(HttpStatusCode.Created);
        Assert.Equal(requesterId, again.Json.GetProperty("requester").GetProperty("id").GetGuid());

        var foreign = (await b.Agent.Client.PostAsync($"{b.Department.Url}/tickets", new { subject = "x", description = "text", requesterId })).Expect(HttpStatusCode.BadRequest);
        Assert.Contains("requesterId", foreign.Json.GetProperty("errors").EnumerateObject().Select(p => p.Name));
        Assert.Equal(0, await fixture.ScalarAsync<long>("SELECT count(*) FROM ticketing.tickets WHERE department_id = @d", TicketKit.P("d", b.Id)));
    }

    [Fact]
    public async Task Requester_lookup_is_department_scoped_min_two_chars_and_limited_to_ten()
    {
        var a = await fixture.NewDeptAsync();
        var b = await fixture.NewDeptAsync();
        var tag = Unique.Id();
        for (var i = 0; i < 12; i++)
        {
            await a.Agent.CreateTicketAsync(a.Department, TicketHelpers.NewTicketBody(email: $"find{i:00}-{tag}@example.test"));
        }

        await b.Agent.CreateTicketAsync(b.Department, TicketHelpers.NewTicketBody(email: $"find-other-{tag}@example.test"));

        var found = (await a.Agent.Client.GetAsync($"{a.Department.Url}/requesters/lookup?q=find")).Expect(HttpStatusCode.OK).Json.EnumerateArray().ToList();
        Assert.Equal(10, found.Count);
        Assert.All(found, r => Assert.DoesNotContain("other", r.Str("email"), StringComparison.Ordinal));
        Assert.Equal(["displayName", "email", "id"], found[0].EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));

        (await a.Agent.Client.GetAsync($"{a.Department.Url}/requesters/lookup?q=f")).Expect(HttpStatusCode.BadRequest);
        (await a.Agent.Client.GetAsync($"{a.Department.Url}/requesters/lookup")).Expect(HttpStatusCode.BadRequest);
        // The other department's address is invisible; a viewer lacks tickets.create; outsiders get 404.
        Assert.Empty((await a.Agent.Client.GetAsync($"{a.Department.Url}/requesters/lookup?q=find-other")).Expect(HttpStatusCode.OK).Json.EnumerateArray());
        (await a.Viewer.Client.GetAsync($"{a.Department.Url}/requesters/lookup?q=find")).Expect(HttpStatusCode.Forbidden);
        (await b.Agent.Client.GetAsync($"{a.Department.Url}/requesters/lookup?q=find")).Expect(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Validation_errors_are_reported_per_field()
    {
        var d = await fixture.NewDeptAsync();
        var url = $"{d.Department.Url}/tickets";
        var good = new { subject = "ok", description = "ok", requester = new { email = "a@example.test" } };

        var bad = (await d.Agent.Client.PostAsync(url, new { subject = " ", description = "", requester = new { email = "nope", displayName = new string('x', 201) }, priority = "Huge" })).Expect(HttpStatusCode.BadRequest);
        foreach (var field in new[] { "subject", "description", "requester.email", "requester.displayName", "priority" })
        {
            Assert.NotEmpty(bad.Errors(field));
        }

        (await d.Agent.Client.PostAsync(url, new { subject = new string('s', 201), description = "ok", requester = good.requester })).Expect(HttpStatusCode.BadRequest);
        (await d.Agent.Client.PostAsync(url, new { subject = new string('s', 200), description = "ok", requester = good.requester })).Expect(HttpStatusCode.Created);
        (await d.Agent.Client.PostAsync(url, new { subject = "ok", description = new string('b', 20001), requester = good.requester })).Expect(HttpStatusCode.BadRequest);
        (await d.Agent.Client.PostAsync(url, new { subject = "ok", description = new string('b', 20000), requester = good.requester })).Expect(HttpStatusCode.Created);
        (await d.Agent.Client.PostAsync(url, new { subject = "ok", description = "ok", requester = new { email = new string('a', 320) + "@x.test" } })).Expect(HttpStatusCode.BadRequest);

        var neither = (await d.Agent.Client.PostAsync(url, new { subject = "ok", description = "ok" })).Expect(HttpStatusCode.BadRequest);
        Assert.NotEmpty(neither.Errors("requester"));
        var both = (await d.Agent.Client.PostAsync(url, new { subject = "ok", description = "ok", requester = good.requester, requesterId = Guid.NewGuid() })).Expect(HttpStatusCode.BadRequest);
        Assert.NotEmpty(both.Errors("requester"));
        var priority = (await d.Agent.Client.PostAsync(url, new { subject = "ok", description = "ok", requester = good.requester, priority = "urgent" })).Expect(HttpStatusCode.Created);
        Assert.Equal("Urgent", priority.Json.Str("priority")); // case-insensitive input, PascalCase output
    }

    [Fact]
    public async Task Create_requires_tickets_create_and_assignment_rights_are_checked()
    {
        var d = await fixture.NewDeptAsync();
        var other = await fixture.NewDeptAsync();
        var url = $"{d.Department.Url}/tickets";

        (await d.Viewer.Client.PostAsync(url, TicketHelpers.NewTicketBody())).Expect(HttpStatusCode.Forbidden);
        (await other.Agent.Client.PostAsync(url, TicketHelpers.NewTicketBody())).Expect(HttpStatusCode.NotFound);
        (await fixture.CreateAnonymousClient().PostAsync(url, TicketHelpers.NewTicketBody())).Expect(HttpStatusCode.BadRequest); // CSRF first
        (await (await fixture.PlatformAdminAsync()).Client.PostAsync(url, TicketHelpers.NewTicketBody())).Expect(HttpStatusCode.NotFound);

        // assign.self only: may take the ticket for themself, nothing else.
        var self = (await d.Agent.Client.PostAsync(url, TicketHelpers.NewTicketBody(extra: new { AssigneeUserId = d.Agent.Id }))).Expect(HttpStatusCode.Created);
        Assert.Equal(d.Agent.Id, self.Json.GetProperty("assignee").GetProperty("id").GetGuid());
        (await d.Agent.Client.PostAsync(url, TicketHelpers.NewTicketBody(extra: new { AssigneeUserId = d.Lead.Id }))).Expect(HttpStatusCode.Forbidden);

        // assign.any: any eligible assignee.
        var full = (await d.Lead.Client.PostAsync(url, TicketHelpers.NewTicketBody(extra: new { AssigneeUserId = d.Agent.Id, Priority = "High" }))).Expect(HttpStatusCode.Created);
        Assert.Equal(d.Agent.Id, full.Json.GetProperty("assignee").GetProperty("id").GetGuid());
        Assert.Equal("High", full.Json.Str("priority"));
    }

    [Fact]
    public async Task Create_rejects_invalid_assignee_with_invalid_state_and_inactive_department_with_inactive()
    {
        var d = await fixture.NewDeptAsync();
        var other = await fixture.NewDeptAsync();
        var admin = await fixture.PlatformAdminAsync();
        var url = $"{d.Department.Url}/tickets";
        var outsider = await fixture.NewUserAsync();

        (await d.Lead.Client.PostAsync(url, TicketHelpers.NewTicketBody(extra: new { AssigneeUserId = d.Viewer.Id }))).ExpectCode(HttpStatusCode.Conflict, "invalid_state"); // viewers cannot work tickets
        (await d.Lead.Client.PostAsync(url, TicketHelpers.NewTicketBody(extra: new { AssigneeUserId = other.Agent.Id }))).ExpectCode(HttpStatusCode.Conflict, "invalid_state"); // member of another department only
        (await d.Lead.Client.PostAsync(url, TicketHelpers.NewTicketBody(extra: new { AssigneeUserId = outsider.Id }))).ExpectCode(HttpStatusCode.Conflict, "invalid_state");
        Assert.Equal(0, await fixture.ScalarAsync<long>("SELECT count(*) FROM ticketing.tickets WHERE department_id = @d", TicketKit.P("d", d.Id)));

        var current = (await admin.Client.GetAsync(d.Department.Url)).Expect(HttpStatusCode.OK);
        (await admin.Client.PatchAsync(d.Department.Url, new { version = current.Version, isActive = false })).Expect(HttpStatusCode.OK);
        (await d.Agent.Client.PostAsync(url, TicketHelpers.NewTicketBody())).ExpectCode(HttpStatusCode.Conflict, "inactive");
    }

    [Fact]
    public async Task Creation_is_audited_with_actor_department_and_without_message_text()
    {
        var d = await fixture.NewDeptAsync();
        var secret = $"secret-body-{Unique.Id()}";
        var created = (await d.Agent.Client.PostAsync($"{d.Department.Url}/tickets",
            new { subject = "Audit me", description = secret, requester = new { email = $"a-{Unique.Id()}@example.test" } })).Expect(HttpStatusCode.Created);

        var rows = await fixture.AuditOfAsync(created.Id);
        Assert.Equal([ "ticket.created", "ticket.message_added" ], rows.Select(r => r.Action).ToArray());
        Assert.All(rows, r =>
        {
            Assert.Equal(d.Agent.Id, r.ActorUserId);
            Assert.Equal("ticketing", r.Category);
            Assert.Equal("ticket", r.ObjectType);
            Assert.Equal(d.Id, r.DepartmentId);
            Assert.Equal("web", r.Source);
        });
        Assert.Contains($"{d.Key}-1", rows[0].Next);
        Assert.Equal(0, await fixture.ScalarAsync<long>("SELECT count(*) FROM audit.audit_events WHERE previous::text LIKE @s OR next::text LIKE @s", TicketKit.P("s", $"%{secret}%")));
    }
}
