using System.Net;
using Helpdesk.Host.Tests.Infrastructure;

namespace Helpdesk.Host.Tests.Ticketing;

/// <summary>
/// Phase 2 ticket isolation matrix (docs/security/PHASE2_THREAT_MODEL.md, PERMISSIONS.md rule 9; contract in docs/api/PHASE2_API.md).
/// Every ticket endpoint x every persona (anonymous, no membership, platform admin without membership, member of B only, admin of B
/// against A's ticket, and the four roles inside A), through the real pipeline with dev-login cookies and CSRF. Asserts the exact
/// contract status; that denied calls change nothing (tickets, messages, requesters, counters, audit rows); that allowed mutating calls
/// do change something; and that every 404 is byte-identical (status, headers, problem body minus trace id and instance) to the 404 for
/// a random GUID, for a guessed reference with the real key and for a random-key reference, in every case form.
/// </summary>
public sealed class TicketIsolationMatrixTests(HelpdeskFixture fixture) : IClassFixture<HelpdeskFixture>
{
    private enum Scope
    {
        Ticket,
        Department,
    }

    private enum Form
    {
        Guid,
        RefUpper,
        RefLower,
    }

    private sealed record Req(HttpMethod Method, string Url, object? Body = null);

    private sealed record Endpoint(string Name, Scope Scope, Form Form, Func<IsoWorld, Fresh, Actor, string, Req> Build, string[] Roles, HttpStatusCode Success);

    private static readonly string[] AbsentHeaders = ["Location", "ETag", "Content-Location", "Last-Modified"];
    private static readonly Form[] AllForms = [Form.Guid, Form.RefUpper, Form.RefLower];

    private static readonly string[] AllRoles = [IsoWorld.ViewerA, IsoWorld.AgentA, IsoWorld.LeadA, IsoWorld.AdminA];
    private static readonly string[] Working = [IsoWorld.AgentA, IsoWorld.LeadA, IsoWorld.AdminA];
    private static readonly string[] Assigners = [IsoWorld.LeadA, IsoWorld.AdminA];

    private static string Tk(string route, string suffix = "") => $"/api/tickets/{route}{suffix}";

    private static readonly Endpoint[] Endpoints =
    [
        new("POST create", Scope.Department, Form.Guid, (w, f, a, r) => new(HttpMethod.Post, $"/api/departments/{r}/tickets", TicketHelpers.NewTicketBody()), Working, HttpStatusCode.Created),
        new("POST create (invalid body)", Scope.Department, Form.Guid, (w, f, a, r) => new(HttpMethod.Post, $"/api/departments/{r}/tickets", new { subject = "" }), Working, HttpStatusCode.BadRequest),
        new("GET requester lookup", Scope.Department, Form.Guid, (w, f, a, r) => new(HttpMethod.Get, $"/api/departments/{r}/requesters/lookup?q=re"), Working, HttpStatusCode.OK),
        new("GET requester lookup (missing q)", Scope.Department, Form.Guid, (w, f, a, r) => new(HttpMethod.Get, $"/api/departments/{r}/requesters/lookup"), Working, HttpStatusCode.BadRequest),
        new("GET ticket", Scope.Ticket, Form.Guid, (w, f, a, r) => new(HttpMethod.Get, Tk(r)), AllRoles, HttpStatusCode.OK),
        new("GET ticket by reference (upper)", Scope.Ticket, Form.RefUpper, (w, f, a, r) => new(HttpMethod.Get, Tk(r)), AllRoles, HttpStatusCode.OK),
        new("GET ticket by reference (lower)", Scope.Ticket, Form.RefLower, (w, f, a, r) => new(HttpMethod.Get, Tk(r)), AllRoles, HttpStatusCode.OK),
        new("PATCH ticket", Scope.Ticket, Form.Guid, (w, f, a, r) => new(HttpMethod.Patch, Tk(r), new { version = f.Version, priority = "High" }), Working, HttpStatusCode.OK),
        new("PATCH ticket by reference", Scope.Ticket, Form.RefLower, (w, f, a, r) => new(HttpMethod.Patch, Tk(r), new { version = f.Version, status = "Pending" }), Working, HttpStatusCode.OK),
        new("PUT assignment to another agent", Scope.Ticket, Form.Guid, (w, f, a, r) => new(HttpMethod.Put, Tk(r, "/assignment"), new { version = f.Version, teamId = (Guid?)null, assigneeUserId = w.Target.Id }), Assigners, HttpStatusCode.OK),
        new("PUT assignment to self", Scope.Ticket, Form.Guid, (w, f, a, r) => new(HttpMethod.Put, Tk(r, "/assignment"), new { version = f.Version, teamId = (Guid?)null, assigneeUserId = a.UserId }), Working, HttpStatusCode.OK),
        new("PUT assignment of a team", Scope.Ticket, Form.RefUpper, (w, f, a, r) => new(HttpMethod.Put, Tk(r, "/assignment"), new { version = f.Version, teamId = w.TeamA.Id, assigneeUserId = (Guid?)null }), Assigners, HttpStatusCode.OK),
        new("GET messages", Scope.Ticket, Form.Guid, (w, f, a, r) => new(HttpMethod.Get, Tk(r, "/messages")), AllRoles, HttpStatusCode.OK),
        new("GET messages (invalid limit)", Scope.Ticket, Form.Guid, (w, f, a, r) => new(HttpMethod.Get, Tk(r, "/messages?limit=0")), AllRoles, HttpStatusCode.BadRequest),
        new("POST message (public)", Scope.Ticket, Form.Guid, (w, f, a, r) => new(HttpMethod.Post, Tk(r, "/messages"), new { kind = "Public", body = "matrix reply" }), Working, HttpStatusCode.Created),
        new("POST message (internal)", Scope.Ticket, Form.RefLower, (w, f, a, r) => new(HttpMethod.Post, Tk(r, "/messages"), new { kind = "Internal", body = "matrix note" }), Working, HttpStatusCode.Created),
        new("POST message (public with status)", Scope.Ticket, Form.Guid, (w, f, a, r) => new(HttpMethod.Post, Tk(r, "/messages"), new { kind = "Public", body = "matrix reply", status = "Pending" }), Working, HttpStatusCode.Created),
        new("GET activity", Scope.Ticket, Form.Guid, (w, f, a, r) => new(HttpMethod.Get, Tk(r, "/activity")), AllRoles, HttpStatusCode.OK),
        new("GET activity (invalid cursor)", Scope.Ticket, Form.Guid, (w, f, a, r) => new(HttpMethod.Get, Tk(r, "/activity?cursor=not-a-cursor")), AllRoles, HttpStatusCode.BadRequest),
    ];

    public static TheoryData<string, string> Matrix()
    {
        var data = new TheoryData<string, string>();
        foreach (var endpoint in Endpoints)
        {
            foreach (var persona in IsoWorld.Personas)
            {
                data.Add(endpoint.Name, persona);
            }
        }

        return data;
    }

    public static TheoryData<string> TicketEndpointNames()
    {
        var data = new TheoryData<string>();
        foreach (var endpoint in Endpoints.Where(e => e.Scope == Scope.Ticket))
        {
            data.Add(endpoint.Name);
        }

        return data;
    }

    private static HttpStatusCode Expected(Endpoint endpoint, string persona) => persona switch
    {
        IsoWorld.Anonymous => HttpStatusCode.Unauthorized,
        IsoWorld.NoMembership or IsoWorld.MemberOfBOnly or IsoWorld.AdminOfBAgainstA or IsoWorld.PlatformAdminNoMembership => HttpStatusCode.NotFound,
        _ => endpoint.Roles.Contains(persona) ? endpoint.Success : HttpStatusCode.Forbidden,
    };

    private static string Route(IsoWorld w, Fresh f, Endpoint e, Form? form = null) => e.Scope == Scope.Department
        ? w.A.Id.ToString()
        : (form ?? e.Form) switch
        {
            Form.RefUpper => f.Reference.ToUpperInvariant(),
            Form.RefLower => f.Reference.ToLowerInvariant(),
            _ => f.Id.ToString(),
        };

    /// <summary>Routes that do not exist: random GUID, guessed reference with the real key, reference with a random key (case form as the real route).</summary>
    private static string[] GhostRoutes(IsoWorld w, Endpoint e, Form? form = null)
    {
        if (e.Scope == Scope.Department)
        {
            return [Guid.NewGuid().ToString()];
        }

        string Case(string reference) => (form ?? e.Form) switch
        {
            Form.RefUpper => reference.ToUpperInvariant(),
            Form.RefLower => reference.ToLowerInvariant(),
            _ => reference,
        };

        return [Guid.NewGuid().ToString(), Case(IsoWorld.RandomReference(w.A.Key)), Case(IsoWorld.RandomReference())];
    }

    private static Task<ApiResponse> SendAsync(Actor actor, Req req) => actor.Client.SendAsync(req.Method, req.Url, req.Body);

    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task Endpoint_by_persona_returns_the_contract_status_and_leaks_nothing(string endpointName, string persona)
    {
        var endpoint = Endpoints.Single(e => e.Name == endpointName);
        var w = await IsoWorld.For(fixture);
        var f = await w.NewTicketAsync();
        var actor = w.Actors[persona];
        var expected = Expected(endpoint, persona);
        var request = endpoint.Build(w, f, actor, Route(w, f, endpoint));
        var mutating = request.Method != HttpMethod.Get;

        var before = await IsoKit.SnapshotAsync(fixture, w.Ids);
        var response = await SendAsync(actor, request);
        var after = await IsoKit.SnapshotAsync(fixture, w.Ids);

        response.Expect(expected);
        if (mutating && expected == endpoint.Success && (int)expected < 300)
        {
            Assert.NotEqual(before, after); // the matrix really exercised a mutation
        }
        else
        {
            Assert.Equal(before, after); // denied, invalid and read calls change nothing at all
        }

        if (expected is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized)
        {
            var body = IsoKit.NormalisedBody(response);
            foreach (var secret in new[] { f.Id.ToString(), f.Reference, w.A.Key, w.A.Name, f.Subject, f.NoteToken, f.PublicToken, w.A.Id.ToString() })
            {
                Assert.DoesNotContain(secret, body, StringComparison.OrdinalIgnoreCase);
            }

            Assert.All(AbsentHeaders, h => Assert.False(IsoKit.HasHeader(response, h), $"{h} present on {(int)expected}"));
        }

        if (expected == HttpStatusCode.NotFound)
        {
            // No existence oracle: identical to the response for objects that do not exist, whatever the guess looks like.
            foreach (var ghost in GhostRoutes(w, endpoint))
            {
                var g = await SendAsync(actor, endpoint.Build(w, f, actor, ghost));
                g.Expect(HttpStatusCode.NotFound);
                Assert.True(IsoKit.Fingerprint(g) == IsoKit.Fingerprint(response), $"404 for the real object differs from the 404 for '{ghost}'.\nreal:  {IsoKit.Fingerprint(response)}\nghost: {IsoKit.Fingerprint(g)}");
            }

            Assert.Equal(after, await IsoKit.SnapshotAsync(fixture, w.Ids));
        }
    }

    [Theory]
    [MemberData(nameof(TicketEndpointNames))]
    public async Task Every_ticket_route_resolves_all_reference_forms_for_insiders_and_hides_them_from_outsiders(string endpointName)
    {
        var endpoint = Endpoints.Single(e => e.Name == endpointName);
        var w = await IsoWorld.For(fixture);

        foreach (var form in AllForms)
        {
            var f = await w.NewTicketAsync();

            foreach (var persona in IsoWorld.Outsiders)
            {
                var actor = w.Actors[persona];
                var before = await IsoKit.SnapshotAsync(fixture, w.Ids);
                var real = await SendAsync(actor, endpoint.Build(w, f, actor, Route(w, f, endpoint, form)));
                real.Expect(HttpStatusCode.NotFound);
                Assert.Equal(before, await IsoKit.SnapshotAsync(fixture, w.Ids));
                var ghost = await SendAsync(actor, endpoint.Build(w, f, actor, Guid.NewGuid().ToString()));
                Assert.True(IsoKit.Fingerprint(ghost) == IsoKit.Fingerprint(real), $"{persona} {form}: reference/GUID form distinguishable.\nreal:  {IsoKit.Fingerprint(real)}\nghost: {IsoKit.Fingerprint(ghost)}");
            }

            // Anonymous: 401 whatever the form.
            var anon = w.Actors[IsoWorld.Anonymous];
            Assert.Equal(
                IsoKit.Fingerprint(await SendAsync(anon, endpoint.Build(w, f, anon, Guid.NewGuid().ToString()))),
                IsoKit.Fingerprint(await SendAsync(anon, endpoint.Build(w, f, anon, Route(w, f, endpoint, form)))));

            // Insiders: the same ticket whichever way it is addressed (success status of the endpoint, or its validation error).
            var insider = w.Actors[IsoWorld.AdminA];
            (await SendAsync(insider, endpoint.Build(w, f, insider, Route(w, f, endpoint, form)))).Expect(endpoint.Success);
        }
    }

    // ----- lists and counts: scoped in the query, never an error -----

    private static readonly string[] ListQueries =
    [
        "", "?queue=all", "?queue=unassigned", "?queue=my", "?queue=team", "?sort=priority_desc&limit=100", "?sort=created_asc",
        "?status=Open&status=Pending&status=Resolved&status=Closed&limit=100",
    ];

    public static TheoryData<string> PersonaNames()
    {
        var data = new TheoryData<string>();
        foreach (var persona in IsoWorld.Personas)
        {
            data.Add(persona);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(PersonaNames))]
    public async Task Lists_and_counts_contain_only_the_personas_own_departments(string persona)
    {
        var w = await IsoWorld.For(fixture);
        var f = await w.NewTicketAsync();
        var actor = w.Actors[persona];
        var idsA = await IsoKit.TicketIdsOfAsync(fixture, w.A.Id);
        var idsB = await IsoKit.TicketIdsOfAsync(fixture, w.B.Id);
        var ownOnly = persona is IsoWorld.ViewerA or IsoWorld.AgentA or IsoWorld.LeadA or IsoWorld.AdminA ? idsA
            : persona is IsoWorld.MemberOfBOnly or IsoWorld.AdminOfBAgainstA ? idsB
            : [];
        var foreign = idsA.Concat(idsB).Except(ownOnly).ToHashSet();

        var queries = ListQueries.Concat([
            $"?departmentId={w.A.Id}", $"?departmentId={w.B.Id}", $"?teamId={w.TeamA.Id}", $"?teamId={w.TeamB.Id}",
            $"?assigneeUserId={w.Target.Id}", $"?assigneeUserId={w.UserB.Id}", $"?q={f.Reference}", $"?q={w.A.Key}", $"?q={w.B.Key}", $"?q={w.TicketB.Json.Str("subject")[..8]}",
        ]);
        var before = await IsoKit.SnapshotAsync(fixture, w.Ids);
        foreach (var query in queries)
        {
            var response = await actor.Client.GetAsync("/api/tickets" + query);
            if (persona == IsoWorld.Anonymous)
            {
                response.Expect(HttpStatusCode.Unauthorized);
                continue;
            }

            response.Expect(HttpStatusCode.OK);
            var ids = response.Ids();
            Assert.DoesNotContain(ids, id => foreign.Contains(id));
            Assert.All(ids, id => Assert.Contains(id, ownOnly));
            if (ownOnly.Count == 0)
            {
                Assert.True(IsoKit.EmptyPage(response), $"{persona} {query}: {response.Body}");
            }
        }

        if (persona is IsoWorld.ViewerA or IsoWorld.AgentA or IsoWorld.LeadA or IsoWorld.AdminA)
        {
            Assert.Contains(f.Id, (await actor.Client.GetAsync("/api/tickets?queue=unassigned&limit=100")).Expect(HttpStatusCode.OK).Ids());
            Assert.Equal([f.Id], (await actor.Client.GetAsync($"/api/tickets?q={f.Reference.ToLowerInvariant()}")).Expect(HttpStatusCode.OK).Ids());
        }

        var counts = await actor.Client.GetAsync("/api/tickets/queue-counts");
        if (persona == IsoWorld.Anonymous)
        {
            counts.Expect(HttpStatusCode.Unauthorized);
        }
        else
        {
            counts.Expect(HttpStatusCode.OK);
            var openA = await fixture.ScalarAsync<long>("SELECT count(*) FROM ticketing.tickets WHERE department_id = @d AND status = 'Open'", TicketKit.P("d", w.A.Id));
            var openB = await fixture.ScalarAsync<long>("SELECT count(*) FROM ticketing.tickets WHERE department_id = @d AND status = 'Open'", TicketKit.P("d", w.B.Id));
            var expectedAll = ownOnly.Count == 0 ? 0 : ownOnly == idsA ? openA : openB; // exact: any foreign ticket would change the number
            Assert.Equal(expectedAll, counts.Json.GetProperty("all").GetInt64());
            Assert.True(counts.Json.GetProperty("unassigned").GetInt64() <= expectedAll);
            Assert.True(counts.Json.GetProperty("my").GetInt64() <= expectedAll);
            Assert.True(counts.Json.GetProperty("team").GetInt64() <= expectedAll);
        }

        Assert.Equal(before, await IsoKit.SnapshotAsync(fixture, w.Ids));
    }

    [Fact]
    public async Task Foreign_filters_are_indistinguishable_from_random_filters_and_from_each_other()
    {
        var w = await IsoWorld.For(fixture);
        var f = await w.NewTicketAsync();
        var foreignRef = f.Reference;
        var foreignSubject = f.Subject;

        // Pairs of (query built from a real object of A, same query with a random value). Callers outside A must get the same 200 for both.
        (string Foreign, string Ghost)[] pairs =
        [
            ($"?departmentId={w.A.Id}", $"?departmentId={Guid.NewGuid()}"),
            ($"?departmentId={w.A.Id}&queue=unassigned", $"?departmentId={Guid.NewGuid()}&queue=unassigned"),
            ($"?teamId={w.TeamA.Id}", $"?teamId={Guid.NewGuid()}"),
            ($"?assigneeUserId={w.Target.Id}", $"?assigneeUserId={Guid.NewGuid()}"),
            ($"?assigneeUserId={w.Agent.Id}&status=Open&status=Closed", $"?assigneeUserId={Guid.NewGuid()}&status=Open&status=Closed"),
            ($"?q={foreignRef}", $"?q={IsoWorld.RandomReference(w.A.Key)}"),
            ($"?q={foreignRef.ToLowerInvariant()}", $"?q={IsoWorld.RandomReference(w.A.Key).ToLowerInvariant()}"),
            ($"?q={Uri.EscapeDataString(foreignSubject)}", $"?q={Uri.EscapeDataString("Subj " + Unique.Id())}"),
        ];

        // Callers with no access to A: NoMembership, platform admin, member of B, admin of B. Only filters that cannot match B's own rows
        // are compared byte-for-byte; B members keep seeing their own rows in both.
        foreach (var persona in IsoWorld.Outsiders)
        {
            var actor = w.Actors[persona];
            foreach (var (foreign, ghost) in pairs)
            {
                var real = (await actor.Client.GetAsync("/api/tickets" + foreign)).Expect(HttpStatusCode.OK);
                var none = (await actor.Client.GetAsync("/api/tickets" + ghost)).Expect(HttpStatusCode.OK);
                Assert.True(IsoKit.Fingerprint(real) == IsoKit.Fingerprint(none), $"{persona}: {foreign}\nreal:  {real.Body}\nghost: {none.Body}");
                Assert.DoesNotContain(f.Id.ToString(), real.Body, StringComparison.OrdinalIgnoreCase);
            }
        }

        // The same holds for an insider of A asking about B's objects.
        var viewer = w.Actors[IsoWorld.ViewerA];
        (string Foreign, string Ghost)[] viewerPairs =
        [
            ($"?departmentId={w.B.Id}", $"?departmentId={Guid.NewGuid()}"),
            ($"?teamId={w.TeamB.Id}", $"?teamId={Guid.NewGuid()}"),
            ($"?assigneeUserId={w.UserB.Id}", $"?assigneeUserId={Guid.NewGuid()}"),
            ($"?q={w.TicketB.Json.Str("reference")}", $"?q={IsoWorld.RandomReference(w.B.Key)}"),
            ($"?q={Uri.EscapeDataString(w.TicketB.Json.Str("subject"))}", $"?q={Uri.EscapeDataString("Bsubject " + Unique.Id())}"),
        ];
        foreach (var (foreign, ghost) in viewerPairs)
        {
            var real = (await viewer.Client.GetAsync("/api/tickets" + foreign)).Expect(HttpStatusCode.OK);
            var none = (await viewer.Client.GetAsync("/api/tickets" + ghost)).Expect(HttpStatusCode.OK);
            Assert.True(IsoKit.EmptyPage(real) && IsoKit.EmptyPage(none), $"viewer: {foreign}\nreal:  {real.Body}\nghost: {none.Body}");
            Assert.Equal(IsoKit.Fingerprint(none), IsoKit.Fingerprint(real));
        }
    }
}
