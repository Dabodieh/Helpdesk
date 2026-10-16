using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Helpdesk.Host.Tests.Infrastructure;
using Helpdesk.Modules.Organisation.Contracts;
using Npgsql;

namespace Helpdesk.Host.Tests.Ticketing;

/// <summary>A caller of the matrix: a name, its cookie-jar client (with CSRF token) and, unless anonymous, the signed-in user.</summary>
public sealed record Actor(string Name, ApiClient Client, TestUser? User)
{
    public Guid UserId => User?.Id ?? Guid.Empty;
}

/// <summary>A ticket of department A seeded with a public message and an internal note carrying unique tokens.</summary>
public sealed record Fresh(Guid Id, string Reference, string Version, string Subject, string NoteToken, string PublicToken);

/// <summary>
/// Two departments, every role in A, a member/admin of B, a user without membership and the platform admin (all through the real API; since S-02 the platform admin is NOT an outsider and has its own tests),
/// shared by the tests of one class. A fresh ticket per case keeps the cases independent (xUnit runs a class sequentially).
/// </summary>
public sealed class IsoWorld
{
    public const string Anonymous = "Anonymous";
    public const string NoMembership = "NoMembership";
    public const string MemberOfBOnly = "MemberOfBOnly";
    public const string AdminOfBAgainstA = "AdminOfBAgainstA";
    public const string ViewerA = "ViewerA";
    public const string AgentA = "AgentA";
    public const string LeadA = "LeadA";
    public const string AdminA = "AdminA";

    public static readonly string[] Personas =
        [Anonymous, NoMembership, MemberOfBOnly, AdminOfBAgainstA, ViewerA, AgentA, LeadA, AdminA];

    public static readonly string[] Outsiders = [NoMembership, MemberOfBOnly, AdminOfBAgainstA];

    private static readonly ConditionalWeakTable<HelpdeskFixture, Task<IsoWorld>> Cache = new();

    public required HelpdeskFixture Fixture { get; init; }
    public required DepartmentRef A { get; init; }
    public required DepartmentRef B { get; init; }
    public required TestUser Viewer { get; init; }
    public required TestUser Agent { get; init; }
    public required TestUser Lead { get; init; }
    public required TestUser Admin { get; init; }
    public required TestUser Target { get; init; }
    public required TestUser UserB { get; init; }
    public required TestUser AdminB { get; init; }
    public required TestUser NoMember { get; init; }
    public required TestUser Root { get; init; }
    public required ApiClient AnonymousClient { get; init; }
    public required ApiResponse TicketB { get; init; }
    public required Dictionary<string, Actor> Actors { get; init; }

    public Guid[] Ids => [A.Id, B.Id];

    public static Task<IsoWorld> For(HelpdeskFixture fixture) => Cache.GetValue(fixture, BuildAsync);

    private static async Task<IsoWorld> BuildAsync(HelpdeskFixture f)
    {
        var a = await f.CreateDepartmentAsync();
        var b = await f.CreateDepartmentAsync();
        var root = await f.PlatformAdminAsync();
        var viewer = await f.NewMemberAsync(a, DepartmentRoles.Viewer);
        var agent = await f.NewMemberAsync(a, DepartmentRoles.Agent);
        var lead = await f.NewMemberAsync(a, DepartmentRoles.TeamLead);
        var admin = await f.NewMemberAsync(a, DepartmentRoles.DepartmentAdmin);
        var target = await f.NewMemberAsync(a, DepartmentRoles.Agent);
        var userB = await f.NewMemberAsync(b, DepartmentRoles.Agent);
        var adminB = await f.NewMemberAsync(b, DepartmentRoles.DepartmentAdmin);
        var noMember = await f.NewUserAsync();
        var anonymous = f.CreateAnonymousClient();
        await anonymous.RefreshCsrfAsync();
        var ticketB = await userB.CreateTicketAsync(b, TicketHelpers.NewTicketBody($"Bsubject {Unique.Id()}"));

        return new IsoWorld
        {
            Fixture = f,
            A = a,
            B = b,
            Viewer = viewer,
            Agent = agent,
            Lead = lead,
            Admin = admin,
            Target = target,
            UserB = userB,
            AdminB = adminB,
            NoMember = noMember,
            Root = root,
            AnonymousClient = anonymous,
            TicketB = ticketB,
            Actors = new Dictionary<string, Actor>
            {
                [Anonymous] = new(Anonymous, anonymous, null),
                [NoMembership] = new(NoMembership, noMember.Client, noMember),
                [MemberOfBOnly] = new(MemberOfBOnly, userB.Client, userB),
                [AdminOfBAgainstA] = new(AdminOfBAgainstA, adminB.Client, adminB),
                [ViewerA] = new(ViewerA, viewer.Client, viewer),
                [AgentA] = new(AgentA, agent.Client, agent),
                [LeadA] = new(LeadA, lead.Client, lead),
                [AdminA] = new(AdminA, admin.Client, admin),
            },
        };
    }

    /// <summary>A new ticket of department A (public message + internal note) created by the A agent.</summary>
    public async Task<Fresh> NewTicketAsync(TestUser? creator = null)
    {
        var tag = Unique.Id();
        var subject = $"Subj {tag}";
        var author = creator ?? Agent;
        var t = await author.CreateTicketAsync(A, TicketHelpers.NewTicketBody(subject, $"req-{tag}@example.test"));
        var pub = $"PUBTOKEN-{tag}";
        var note = $"NOTETOKEN-{tag}";
        (await Agent.PostMessageAsync(t.Id, "Public", pub)).Expect(HttpStatusCode.Created);
        (await Agent.PostMessageAsync(t.Id, "Internal", note)).Expect(HttpStatusCode.Created);
        var current = (await Agent.GetTicketAsync(t.Id)).Expect(HttpStatusCode.OK);
        return new Fresh(t.Id, current.Json.Str("reference"), current.Version, subject, note, pub);
    }

    public static string RandomReference(string? key = null) => $"{key ?? ("Q" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant())}-{Random.Shared.Next(100000, 999999999)}";
}

public static class IsoKit
{
    private static readonly HashSet<string> VolatileHeaders = new(StringComparer.OrdinalIgnoreCase) { "Date", "traceparent", "Request-Id", "X-Request-Id", "Server-Timing" };

    /// <summary>The body with trace id and instance (the request path) removed; non-JSON bodies are returned as they are.</summary>
    public static string NormalisedBody(ApiResponse response)
    {
        if (response.Body.Length > 0)
        {
            try
            {
                if (JsonNode.Parse(response.Body) is JsonObject obj)
                {
                    obj.Remove("traceId");
                    obj.Remove("instance");
                    return obj.ToJsonString();
                }
            }
            catch (JsonException)
            {
            }
        }

        return response.Body;
    }

    /// <summary>Status + header names/values (minus volatile ones) + normalised body: everything an outside observer could compare.</summary>
    public static string Fingerprint(ApiResponse response)
    {
        var headers = response.Message.Headers.Concat(response.Message.Content.Headers)
            .Where(h => !VolatileHeaders.Contains(h.Key))
            .OrderBy(h => h.Key, StringComparer.OrdinalIgnoreCase)
            .Select(h => $"{h.Key}={string.Join(",", h.Value)}");
        return $"{(int)response.Status}|{string.Join(";", headers)}|{NormalisedBody(response)}";
    }

    public static bool HasHeader(ApiResponse response, string name) =>
        response.Message.Headers.Concat(response.Message.Content.Headers).Any(h => string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Hash of every row the ticketing and audit modules hold for the departments: any change shows up.</summary>
    public static Task<string?> SnapshotAsync(HelpdeskFixture fixture, params Guid[] departmentIds) => SnapshotCoreAsync(fixture, departmentIds, true);

    /// <summary>Snapshot of one department only (no global audit count), for "nothing of B changed" while A is being changed.</summary>
    public static Task<string?> DepartmentSnapshotAsync(HelpdeskFixture fixture, Guid departmentId) => SnapshotCoreAsync(fixture, [departmentId], false);

    private static Task<string?> SnapshotCoreAsync(HelpdeskFixture fixture, Guid[] departmentIds, bool includeGlobalAudit) =>
        fixture.ScalarAsync<string>(
            """
            SELECT md5(coalesce(string_agg(t, '|' ORDER BY t), '')) FROM (
              SELECT 'tk:'||id||':'||xmin::text||':'||subject||':'||status||':'||priority::text||':'||coalesce(assignee_user_id::text,'')||':'||requester_id||':'||reference AS t
                FROM ticketing.tickets WHERE department_id = ANY(@ids)
              UNION ALL SELECT 'm:'||id||':'||kind||':'||author_type||':'||delivery_state||':'||md5(body) FROM ticketing.messages WHERE department_id = ANY(@ids)
              UNION ALL SELECT 'r:'||id||':'||email_normalised||':'||display_name FROM ticketing.requesters WHERE department_id = ANY(@ids)
              UNION ALL SELECT 'c:'||department_id||':'||next_number::text FROM ticketing.department_counters WHERE department_id = ANY(@ids)
              UNION ALL SELECT 'a:'||count(*)::text FROM audit.audit_events WHERE department_id = ANY(@ids)
              UNION ALL SELECT 'a2:'||count(*)::text FROM audit.audit_events WHERE category = 'ticketing' AND @global
            ) x
            """,
            new NpgsqlParameter("ids", departmentIds),
            new NpgsqlParameter("global", includeGlobalAudit));

    public static async Task<List<Guid>> GuidsAsync(HelpdeskFixture fixture, string sql, params NpgsqlParameter[] parameters)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddRange(parameters);
        await using var reader = await command.ExecuteReaderAsync();
        var ids = new List<Guid>();
        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetGuid(0));
        }

        return ids;
    }

    public static Task<List<Guid>> TicketIdsOfAsync(HelpdeskFixture fixture, Guid departmentId) =>
        GuidsAsync(fixture, "SELECT id FROM ticketing.tickets WHERE department_id = @d", new NpgsqlParameter("d", departmentId));

    /// <summary>Sends a request with an explicit (possibly wrong or missing) CSRF header, bypassing <see cref="ApiClient"/>'s own token.</summary>
    public static async Task<ApiResponse> SendRawAsync(ApiClient client, HttpMethod method, string url, object? body, string? csrfHeader = null, string? csrfToken = null)
    {
        using var request = new HttpRequestMessage(method, url);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }

        if (csrfHeader is not null)
        {
            request.Headers.TryAddWithoutValidation(csrfHeader, csrfToken ?? string.Empty);
        }

        var message = await client.Http.SendAsync(request);
        return new ApiResponse(message.StatusCode, await message.Content.ReadAsStringAsync(), message);
    }

    public static async Task<string> CsrfTokenOfAsync(ApiClient client) =>
        (await client.GetAsync("/api/csrf")).Expect(HttpStatusCode.OK).Json.GetProperty("token").GetString()!;

    public static bool EmptyPage(ApiResponse page) =>
        page.Json.GetProperty("items").GetArrayLength() == 0 && page.Json.GetProperty("nextCursor").ValueKind == JsonValueKind.Null;
}
