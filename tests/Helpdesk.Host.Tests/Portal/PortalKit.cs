#pragma warning disable CA1861 // one-off request payloads
using System.Net;
using System.Text.Json;
using Helpdesk.Host.Tests.Infrastructure;
using Helpdesk.Host.Tests.Ticketing;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;

namespace Helpdesk.Host.Tests.Portal;

/// <summary>Helpers for the requester portal tests: employees with a verified address, request forms, and a portal SignalR client.</summary>
public static class PortalKit
{
    public sealed record Employee(TestUser User, string Email);

    /// <summary>A signed-in employee (no department membership) whose verified address is unique to the test.</summary>
    public static async Task<Employee> EmployeeAsync(this HelpdeskFixture fixture, string prefix = "emp")
    {
        var id = Unique.Id();
        var email = $"{prefix}-{id}@example.test";
        var user = await fixture.SignInAsync($"{prefix}-{id}", $"{prefix} {id}", email);
        Trusted[email] = (fixture, user.Id);
        return new Employee(user, email);
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (HelpdeskFixture Fixture, Guid UserId)> Trusted = new();

    /// <summary>
    /// Stands in for an explicit, trusted requester link (one an administrator or the portal itself established). An address alone never claims a requester,
    /// so tests that want an employee to own a requester created another way must say so.
    /// </summary>
    public static async Task LinkRequesterAsync(this HelpdeskFixture fixture, Guid departmentId, string requesterEmail, Guid userId) =>
        await fixture.ScalarAsync<long>(
            """
            WITH ins AS (
              INSERT INTO ticketing.requester_links (id, requester_id, department_id, user_id, linked_email_normalised, created_at)
              SELECT gen_random_uuid(), r.id, r.department_id, @u, r.email_normalised, now() FROM ticketing.requesters r
              WHERE r.department_id = @d AND r.email_normalised = @e AND NOT EXISTS (SELECT 1 FROM ticketing.requester_links l WHERE l.requester_id = r.id)
              RETURNING 1)
            SELECT count(*) FROM ins
            """,
            TicketKit.P("u", userId), TicketKit.P("d", departmentId), TicketKit.P("e", requesterEmail.Trim().ToLowerInvariant()));

    /// <summary>An agent creates a ticket for the requester address (as the email channel or an agent would), and, when the address belongs to a registered test employee, links that employee as its trusted owner (an address alone never claims a requester).</summary>
    public static async Task<ApiResponse> TicketForAsync(this Dept d, string requesterEmail, string? subject = null, object? extra = null) =>
        await d.CreateForAsync(requesterEmail, subject, extra);

    private static async Task<ApiResponse> CreateForAsync(this Dept d, string requesterEmail, string? subject, object? extra)
    {
        var created = await d.Agent.CreateTicketAsync(d.Department, TicketHelpers.NewTicketBody(subject, requesterEmail, extra));
        if (Trusted.TryGetValue(requesterEmail, out var owner))
        {
            await owner.Fixture.LinkRequesterAsync(d.Id, requesterEmail, owner.UserId);
        }

        return created;
    }

    public static async Task<JsonElement> PortalListAsync(this Employee e, string query = "") =>
        (await e.User.Client.GetAsync("/api/portal/requests" + query)).Expect(HttpStatusCode.OK).Json;

    public static string Reference(this ApiResponse ticket) => ticket.Json.GetProperty("reference").GetString()!;

    /// <summary>
    /// Creates a request form (and optional fields) through the real admin API. A form can only be used while its department has a usable default mailbox (D22), so one is
    /// registered first when the department has none.
    /// </summary>
    public static async Task<(Guid FormId, string Version)> FormAsync(this Dept d, HelpdeskFixture fixture, string name, object? extra = null, params object[] fields)
    {
        if ((await d.Admin.Client.GetAsync($"{d.Department.Url}/mailboxes")).Expect(HttpStatusCode.OK).Json.GetArrayLength() == 0)
        {
            await Mail.MailKit.CreateMailboxAsync(fixture, d);
        }

        var body = new Dictionary<string, object?> { ["name"] = name, ["audience"] = "AllEmployees" };
        if (extra is not null)
        {
            foreach (var p in extra.GetType().GetProperties())
            {
                body[char.ToLowerInvariant(p.Name[0]) + p.Name[1..]] = p.GetValue(extra);
            }
        }

        var form = (await d.Admin.Client.PostAsync($"{d.Department.Url}/request-forms", body)).Expect(HttpStatusCode.Created);
        var linked = new List<object>();
        foreach (var field in fields)
        {
            var created = (await d.Admin.Client.PostAsync($"{d.Department.Url}/ticket-fields", field)).Expect(HttpStatusCode.Created);
            linked.Add(new { fieldId = created.Id });
        }

        if (linked.Count > 0)
        {
            (await d.Admin.Client.PutAsync($"{d.Department.Url}/request-forms/{form.Id}/fields", new { fields = linked })).Expect(HttpStatusCode.OK);
        }

        return (form.Id, form.Version);
    }

    public static object Field(string label, string type, bool required = false, object[]? options = null, bool requesterVisible = true, bool agentVisible = true, string? key = null, bool appliesToAllTypes = false, bool requesterEditable = false) =>
        new { key, label, type, required, options, requesterVisible, agentVisible, appliesToAllTypes, requesterEditable };

    /// <summary>A portal SignalR connection for the employee through the real hub (cookie session + CSRF header).</summary>
    public static async Task<(HubConnection Connection, List<JsonElement> Events)> ConnectPortalAsync(this HelpdeskFixture fixture, Employee e, List<HubConnection> sink)
    {
        using var http = fixture.Factory.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = false });
        var login = await http.GetAsync($"/api/auth/dev-login?subject={Uri.EscapeDataString(e.User.Subject)}&name=x&email={Uri.EscapeDataString(e.Email)}&returnUrl=/");
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
        connection.On<JsonElement>("requestChanged", ev =>
        {
            lock (events)
            {
                events.Add(ev.Clone());
            }
        });
        connection.On<JsonElement>("ticketChanged", ev =>
        {
            lock (events)
            {
                events.Add(ev.Clone());
            }
        });
        sink.Add(connection);
        await connection.StartAsync();
        return (connection, events);
    }

    private static List<string> Pairs(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values) ? [.. values.Select(v => v.Split(';')[0])] : [];

    public static async Task<bool> WaitForAsync(List<JsonElement> events, Func<JsonElement, bool> match, int milliseconds = 6000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (DateTime.UtcNow < until)
        {
            lock (events)
            {
                if (events.Any(match))
                {
                    return true;
                }
            }

            await Task.Delay(50);
        }

        return false;
    }

    public static async Task<bool> SilentAsync(List<JsonElement> events, int milliseconds = 1200)
    {
        await Task.Delay(milliseconds);
        lock (events)
        {
            return events.Count == 0;
        }
    }
}
