using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Helpdesk.Host.Tests.Infrastructure;
using Helpdesk.Modules.Organisation.Contracts;
using Npgsql;

namespace Helpdesk.Host.Tests.Organisation;

/// <summary>
/// Phase 1 department-isolation matrix (docs/architecture/PERMISSIONS.md rule 9; contract in docs/api/PHASE1_API.md).
/// Every department-scoped endpoint x every persona, through the real pipeline, asserting the exact status, that denied calls
/// change nothing in the database, that successful mutating calls do change it, and that every 404 is indistinguishable from the
/// 404 for a department that does not exist. Further tests cover id-guessing/mismatched ids, ids in body/query not overriding the route, list leakage and direct (UI-bypassing) API calls.
/// </summary>
public sealed class IsolationMatrixTests(HelpdeskFixture fixture) : IClassFixture<HelpdeskFixture>
{
    private const string Anonymous = "Anonymous";
    private const string NoMembership = "NoMembership";
    private const string PlatformAdminNoMembership = "PlatformAdminNoMembership";
    private const string MemberOfBOnly = "MemberOfBOnly";
    private const string AdminOfBAgainstA = "AdminOfBAgainstA";
    private const string ViewerA = "ViewerA";
    private const string AgentA = "AgentA";
    private const string LeadA = "LeadA";
    private const string AdminA = "AdminA";

    private static readonly string[] Personas =
        [Anonymous, NoMembership, PlatformAdminNoMembership, MemberOfBOnly, AdminOfBAgainstA, ViewerA, AgentA, LeadA, AdminA];

    private static readonly string[] AllRoles = [ViewerA, AgentA, LeadA, AdminA];
    private static readonly string[] AdminOnly = [AdminA];

    private sealed record Req(HttpMethod Method, string Url, object? Body = null);

    /// <param name="Roles">in-department personas that are allowed.</param>
    /// <param name="PlatformAdmin">whether a platform admin without membership is allowed (administrative permission).</param>
    private sealed record Endpoint(string Name, Func<World, Guid, Req> Build, string[] Roles, bool PlatformAdmin, HttpStatusCode Success);

    private sealed class World
    {
        public required DepartmentRef A { get; init; }
        public required DepartmentRef B { get; init; }
        public required TestUser Target { get; init; }   // Agent in A
        public required TestUser Target2 { get; init; }  // Agent in A
        public required TestUser UserB { get; init; }    // Agent in B only
        public required string DepartmentVersion { get; init; }
        public required Guid TicketA { get; init; }     // a ticket of department A
        public required Dictionary<string, ApiClient> Clients { get; init; }
        public Guid[] Ids => [A.Id, B.Id];
    }

    private static string D(Guid d) => $"/api/departments/{d}";

    private static readonly Endpoint[] Endpoints =
    [
        new("GET department", (w, d) => new(HttpMethod.Get, D(d)), AllRoles, true, HttpStatusCode.OK),
        new("PATCH department name", (w, d) => new(HttpMethod.Patch, D(d), new { version = w.DepartmentVersion, name = $"Renamed {Unique.Id()}" }), AdminOnly, true, HttpStatusCode.OK),
        new("PATCH department isActive", (w, d) => new(HttpMethod.Patch, D(d), new { version = w.DepartmentVersion, isActive = false }), [], true, HttpStatusCode.OK),
        new("GET members", (w, d) => new(HttpMethod.Get, $"{D(d)}/members"), AllRoles, true, HttpStatusCode.OK),
        new("PUT member role", (w, d) => new(HttpMethod.Put, $"{D(d)}/members/{w.Target.Id}", new { role = DepartmentRoles.Viewer }), AdminOnly, true, HttpStatusCode.OK),
        new("DELETE member", (w, d) => new(HttpMethod.Delete, $"{D(d)}/members/{w.Target.Id}"), AdminOnly, true, HttpStatusCode.NoContent),
        new("GET audit", (w, d) => new(HttpMethod.Get, $"{D(d)}/audit"), [LeadA, AdminA], true, HttpStatusCode.OK),
        // Content permission (tickets.read on the ticket's department): platform admin alone is NOT enough. The department id of the matrix is
        // the persisted department of the ticket; a random id stands for "does not exist" (404 for everyone without access).
        new("GET ticket", (w, d) => new(HttpMethod.Get, $"/api/tickets/{(d == w.A.Id ? w.TicketA : d)}"), AllRoles, false, HttpStatusCode.OK),
    ];

    public static TheoryData<string, string> Matrix()
    {
        var data = new TheoryData<string, string>();
        foreach (var endpoint in Endpoints)
        {
            foreach (var persona in Personas)
            {
                data.Add(endpoint.Name, persona);
            }
        }

        return data;
    }

    public static TheoryData<string> UnsafeEndpointNames()
    {
        var data = new TheoryData<string>();
        foreach (var endpoint in Endpoints.Where(e => e.Name.Split(' ')[0] != "GET"))
        {
            data.Add(endpoint.Name);
        }

        return data;
    }

    private static HttpStatusCode Expected(Endpoint endpoint, string persona) => persona switch
    {
        Anonymous => HttpStatusCode.Unauthorized,
        NoMembership or MemberOfBOnly or AdminOfBAgainstA => HttpStatusCode.NotFound,
        PlatformAdminNoMembership => endpoint.PlatformAdmin ? endpoint.Success : HttpStatusCode.NotFound,
        _ => endpoint.Roles.Contains(persona) ? endpoint.Success : HttpStatusCode.Forbidden,
    };

    private async Task<World> BuildWorldAsync()
    {
        var a = await fixture.CreateDepartmentAsync();
        var b = await fixture.CreateDepartmentAsync();
        var root = await fixture.PlatformAdminAsync();
        var viewer = await fixture.NewMemberAsync(a, DepartmentRoles.Viewer);
        var agent = await fixture.NewMemberAsync(a, DepartmentRoles.Agent);
        var lead = await fixture.NewMemberAsync(a, DepartmentRoles.TeamLead);
        var admin = await fixture.NewMemberAsync(a, DepartmentRoles.DepartmentAdmin);
        var target = await fixture.NewMemberAsync(a, DepartmentRoles.Agent);
        var target2 = await fixture.NewMemberAsync(a, DepartmentRoles.Agent);
        var userB = await fixture.NewMemberAsync(b, DepartmentRoles.Agent);
        var adminB = await fixture.NewMemberAsync(b, DepartmentRoles.DepartmentAdmin);
        var noMember = await fixture.NewUserAsync();
        var version = (await root.Client.GetAsync(a.Url)).Expect(HttpStatusCode.OK).Version;
        var ticketA = (await agent.CreateTicketAsync(a)).Id;

        return new World
        {
            A = a,
            B = b,
            Target = target,
            Target2 = target2,
            UserB = userB,
            DepartmentVersion = version,
            TicketA = ticketA,
            Clients = new Dictionary<string, ApiClient>
            {
                [Anonymous] = await AnonymousWithCsrfAsync(),
                [NoMembership] = noMember.Client,
                [PlatformAdminNoMembership] = root.Client,
                [MemberOfBOnly] = userB.Client,
                [AdminOfBAgainstA] = adminB.Client,
                [ViewerA] = viewer.Client,
                [AgentA] = agent.Client,
                [LeadA] = lead.Client,
                [AdminA] = admin.Client,
            },
        };
    }

    private async Task<ApiClient> AnonymousWithCsrfAsync()
    {
        var client = fixture.CreateAnonymousClient();
        await client.RefreshCsrfAsync();
        return client;
    }

    private Task<string?> SnapshotAsync(Guid[] ids) =>
        fixture.ScalarAsync<string>(
            """
            SELECT md5(coalesce(string_agg(t, '|' ORDER BY t), '')) FROM (
              SELECT 'd:'||id||':'||name||':'||coalesce(description,'')||':'||is_active::text||':'||xmin::text AS t FROM organisation.departments WHERE id = ANY(@ids)
              UNION ALL SELECT 'm:'||user_id||':'||department_id||':'||role FROM organisation.department_memberships WHERE department_id = ANY(@ids)
              UNION ALL SELECT 'a:'||count(*) FROM audit.audit_events WHERE department_id = ANY(@ids)
            ) x
            """,
            new NpgsqlParameter("ids", ids));

    private static Task<ApiResponse> SendAsync(ApiClient client, Req req) => client.SendAsync(req.Method, req.Url, req.Body);

    /// <summary>Status + header names/values (minus volatile ones) + problem body without trace/instance: what an outside observer could compare.</summary>
    private static string Fingerprint(ApiResponse response)
    {
        var volatileHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Date", "traceparent", "Request-Id", "X-Request-Id", "Server-Timing" };
        var headers = response.Message.Headers.Concat(response.Message.Content.Headers)
            .Where(h => !volatileHeaders.Contains(h.Key))
            .OrderBy(h => h.Key, StringComparer.OrdinalIgnoreCase)
            .Select(h => $"{h.Key}={string.Join(",", h.Value)}");
        var body = response.Body;
        if (body.Length > 0 && JsonNode.Parse(body) is JsonObject obj)
        {
            obj.Remove("traceId");
            obj.Remove("instance");
            body = obj.ToJsonString();
        }

        return $"{(int)response.Status}|{string.Join(";", headers)}|{body}";
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task Endpoint_by_persona_returns_the_contract_status(string endpointName, string persona)
    {
        var endpoint = Endpoints.Single(e => e.Name == endpointName);
        var world = await BuildWorldAsync();
        var client = world.Clients[persona];
        var expected = Expected(endpoint, persona);
        var request = endpoint.Build(world, world.A.Id);
        var mutating = request.Method != HttpMethod.Get;

        var before = await SnapshotAsync(world.Ids);
        var response = await SendAsync(client, request);
        var after = await SnapshotAsync(world.Ids);

        response.Expect(expected);
        if (mutating && (int)expected >= 400)
        {
            Assert.Equal(before, after); // denied calls change nothing
        }
        else if (mutating)
        {
            Assert.NotEqual(before, after); // the matrix really exercised a mutation
        }
        else
        {
            Assert.Equal(before, after);
        }

        if (expected == HttpStatusCode.NotFound)
        {
            // No existence oracle: identical to the response for a department that does not exist.
            var ghost = await SendAsync(client, endpoint.Build(world, Guid.NewGuid()));
            ghost.Expect(HttpStatusCode.NotFound);
            Assert.Equal(Fingerprint(ghost), Fingerprint(response));
        }

        if (expected == HttpStatusCode.Unauthorized)
        {
            Assert.DoesNotContain(world.A.Name, response.Body, StringComparison.Ordinal);
        }
    }

    [Theory]
    [MemberData(nameof(UnsafeEndpointNames))]
    public async Task Direct_unsafe_calls_without_csrf_or_session_are_rejected_and_change_nothing(string endpointName)
    {
        var endpoint = Endpoints.Single(e => e.Name == endpointName);
        var world = await BuildWorldAsync();
        var request = endpoint.Build(world, world.A.Id);
        var before = await SnapshotAsync(world.Ids);

        // No session, no CSRF token: CSRF validation runs first (400, contract: missing token = 400). Never 2xx.
        var noSessionNoCsrf = await fixture.CreateAnonymousClient().SendAsync(request.Method, request.Url, request.Body, includeCsrf: false);
        noSessionNoCsrf.Expect(HttpStatusCode.BadRequest);

        // Valid CSRF token but no session: 401.
        var anonymous = await AnonymousWithCsrfAsync();
        (await anonymous.SendAsync(request.Method, request.Url, request.Body)).Expect(HttpStatusCode.Unauthorized);

        // Valid session of a sufficient role but no CSRF token: 400, nothing changes.
        var noCsrf = await world.Clients[AdminA].SendAsync(request.Method, request.Url, request.Body, includeCsrf: false);
        noCsrf.Expect(HttpStatusCode.BadRequest);

        Assert.Equal(before, await SnapshotAsync(world.Ids));
    }

    [Fact]
    public async Task Guessed_and_mismatched_ids_are_indistinguishable_from_unknown_ids()
    {
        var w = await BuildWorldAsync();
        var adminA = w.Clients[AdminA];
        var before = await SnapshotAsync(w.Ids);

        // Each case: (client, request using a foreign/guessed object id, request using a random id). Responses must be identical.
        var cases = new List<(string Name, ApiClient Client, Req Foreign, Req Ghost)>();
        // Member user ids from another department / random ids.
        cases.Add(("adminA DELETE member of B only", adminA, new(HttpMethod.Delete, $"{D(w.A.Id)}/members/{w.UserB.Id}"), new(HttpMethod.Delete, $"{D(w.A.Id)}/members/{Guid.NewGuid()}")));

        foreach (var (name, client, foreign, ghost) in cases)
        {
            var f = await SendAsync(client, foreign);
            var g = await SendAsync(client, ghost);
            Assert.True(f.Status is HttpStatusCode.NotFound or HttpStatusCode.Conflict, $"{name}: unexpected {(int)f.Status}: {f.Body}");
            Assert.True(Fingerprint(f) == Fingerprint(g), $"{name}: foreign id distinguishable from random id.\nforeign: {Fingerprint(f)}\nghost:   {Fingerprint(g)}");
        }

        Assert.Equal(before, await SnapshotAsync(w.Ids));
    }

    [Fact]
    public async Task Memberships_of_other_departments_cannot_be_removed_or_changed_through_a_foreign_route()
    {
        var w = await BuildWorldAsync();
        var adminA = w.Clients[AdminA];
        var before = await SnapshotAsync(w.Ids);

        // user belongs to B only; the route says A: 404, B's membership intact. Role body naming dept B changes nothing either.
        (await adminA.DeleteAsync($"{D(w.A.Id)}/members/{w.UserB.Id}")).Expect(HttpStatusCode.NotFound);
        // and A's admin trying to act on B's routes with A's ids
        (await adminA.DeleteAsync($"{D(w.B.Id)}/members/{w.UserB.Id}")).Expect(HttpStatusCode.NotFound);
        (await adminA.PutAsync($"{D(w.B.Id)}/members/{w.Target.Id}", new { role = DepartmentRoles.DepartmentAdmin, departmentId = w.A.Id })).Expect(HttpStatusCode.NotFound);

        Assert.Equal(before, await SnapshotAsync(w.Ids));
    }

    [Fact]
    public async Task Department_ids_in_body_or_query_never_override_the_route()
    {
        var w = await BuildWorldAsync();
        var adminA = w.Clients[AdminA];
        var renamed = $"Own {Unique.Id()}";
        var patched = (await adminA.PatchAsync(D(w.A.Id), new { version = w.DepartmentVersion, name = renamed, id = w.B.Id, departmentId = w.B.Id })).Expect(HttpStatusCode.OK);
        Assert.Equal(w.A.Id, patched.Id);
        Assert.Equal(renamed, await fixture.ScalarAsync<string>("SELECT name FROM organisation.departments WHERE id = @d", new NpgsqlParameter("d", w.A.Id)));

        var newcomer = await fixture.NewUserAsync();
        (await adminA.PutAsync($"{D(w.A.Id)}/members/{newcomer.Id}", new { role = DepartmentRoles.Agent, departmentId = w.B.Id })).Expect(HttpStatusCode.OK);
        Assert.Equal(1, await fixture.ScalarAsync<long>("SELECT count(*) FROM organisation.department_memberships WHERE user_id = @u AND department_id = @d", new NpgsqlParameter("u", newcomer.Id), new NpgsqlParameter("d", w.A.Id)));
        Assert.Equal(0, await fixture.ScalarAsync<long>("SELECT count(*) FROM organisation.department_memberships WHERE user_id = @u AND department_id = @d", new NpgsqlParameter("u", newcomer.Id), new NpgsqlParameter("d", w.B.Id)));

        // Query-string department ids on list endpoints are ignored.
        foreach (var user in new[] { AdminA, AgentA, LeadA })
        {
            var client = w.Clients[user];
            foreach (var url in new[] { "/api/departments", $"{D(w.A.Id)}/members", $"{D(w.A.Id)}/audit" })
            {
                var plain = (await client.GetAsync(url)).Status;
                var probed = await client.GetAsync($"{url}?departmentId={w.B.Id}&department={w.B.Id}");
                Assert.Equal(plain, probed.Status);
                Assert.DoesNotContain(w.B.Id.ToString(), probed.Body, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(w.UserB.Id.ToString(), probed.Body, StringComparison.OrdinalIgnoreCase);
            }
        }

    }

    [Fact]
    public async Task Lists_never_leak_items_or_counts_of_other_departments()
    {
        var w = await BuildWorldAsync();
        var root = await fixture.PlatformAdminAsync();
        (await root.Client.PutAsync($"{D(w.B.Id)}/members/{w.Target.Id}", new { role = DepartmentRoles.Viewer })).Expect(HttpStatusCode.OK); // Target is in A and B

        var aOnlyUserIds = new[] { w.Target2.Id }; // member of A only
        foreach (var persona in new[] { ViewerA, AgentA, LeadA, AdminA })
        {
            var client = w.Clients[persona];
            var departments = (await client.GetAsync("/api/departments")).Expect(HttpStatusCode.OK).Json.EnumerateArray().ToList();
            var dept = Assert.Single(departments);
            Assert.Equal(w.A.Id, dept.GetProperty("id").GetGuid());

            var members = (await client.GetAsync($"{D(w.A.Id)}/members")).Expect(HttpStatusCode.OK);
            Assert.DoesNotContain(w.UserB.Id.ToString(), members.Body, StringComparison.OrdinalIgnoreCase);
            var target = Assert.Single(members.Json.EnumerateArray(), m => m.GetProperty("userId").GetGuid() == w.Target.Id);
            Assert.Equal(DepartmentRoles.Agent, target.GetProperty("role").GetString()); // role in A, not the Viewer role it has in B
        }

        foreach (var persona in new[] { LeadA, AdminA })
        {
            var audit = (await w.Clients[persona].GetAsync($"{D(w.A.Id)}/audit?limit=200")).Expect(HttpStatusCode.OK);
            var items = audit.Json.GetProperty("items").EnumerateArray().ToList();
            Assert.NotEmpty(items);
            Assert.DoesNotContain(w.B.Id.ToString(), audit.Body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(w.UserB.Id.ToString(), audit.Body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(items, i => i.GetProperty("objectId").GetGuid() == w.B.Id);
        }

        // B's view is the mirror image.
        var adminB = w.Clients[AdminOfBAgainstA];
        var bMembers = (await adminB.GetAsync($"{D(w.B.Id)}/members")).Expect(HttpStatusCode.OK).Body;
        Assert.DoesNotContain(w.Target2.Id.ToString(), bMembers, StringComparison.OrdinalIgnoreCase);
        _ = aOnlyUserIds;

        // A user with no membership sees nothing at all.
        Assert.Empty((await w.Clients[NoMembership].GetAsync("/api/departments")).Expect(HttpStatusCode.OK).Json.EnumerateArray());
        (await w.Clients[Anonymous].GetAsync("/api/departments")).Expect(HttpStatusCode.Unauthorized);
        (await w.Clients[Anonymous].GetAsync("/api/users/lookup?q=abc")).Expect(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task User_lookup_is_gated_and_returns_only_minimal_identity_fields()
    {
        var w = await BuildWorldAsync();
        var needle = $"lookup{Unique.Id()}";
        var seen = await fixture.SignInAsync($"{needle}-subject", $"{needle} Person", $"{needle}@example.test");
        await fixture.AddMemberAsync(w.B, seen, DepartmentRoles.Agent);

        foreach (var persona in new[] { NoMembership, MemberOfBOnly, ViewerA, AgentA, LeadA })
        {
            (await w.Clients[persona].GetAsync($"/api/users/lookup?q={needle}")).Expect(HttpStatusCode.Forbidden);
        }

        foreach (var persona in new[] { AdminA, AdminOfBAgainstA, PlatformAdminNoMembership })
        {
            var result = (await w.Clients[persona].GetAsync($"/api/users/lookup?q={needle}")).Expect(HttpStatusCode.OK);
            var item = Assert.Single(result.Json.EnumerateArray());
            Assert.Equal(["displayName", "email", "id"], item.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
            Assert.DoesNotContain(w.B.Id.ToString(), result.Body, StringComparison.OrdinalIgnoreCase); // no department/role info in the directory
            Assert.DoesNotContain(w.B.Key, result.Body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Platform_admin_without_membership_administers_but_never_reads_content()
    {
        var w = await BuildWorldAsync();
        var root = w.Clients[PlatformAdminNoMembership];

        (await root.GetAsync($"/api/tickets/{w.TicketA}")).Expect(HttpStatusCode.NotFound);
        (await root.GetAsync($"/api/tickets/{Guid.NewGuid()}")).Expect(HttpStatusCode.NotFound);
        // isActive is exclusively a platform permission; DepartmentAdmin cannot, platform admin can.
        (await w.Clients[AdminA].PatchAsync(D(w.A.Id), new { version = w.DepartmentVersion, isActive = false })).Expect(HttpStatusCode.Forbidden);
        (await root.PatchAsync(D(w.A.Id), new { version = w.DepartmentVersion, isActive = false })).Expect(HttpStatusCode.OK);
        // /api/me shows no memberships for the admin in these departments.
        var me = (await root.GetAsync("/api/me")).Expect(HttpStatusCode.OK).Json.GetProperty("memberships").EnumerateArray();
        Assert.DoesNotContain(me, m => m.GetProperty("departmentId").GetGuid() == w.A.Id);
    }
}
