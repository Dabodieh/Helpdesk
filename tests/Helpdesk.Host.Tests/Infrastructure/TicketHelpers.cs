using System.Net;
using Helpdesk.Modules.Organisation.Contracts;

namespace Helpdesk.Host.Tests.Infrastructure;

/// <summary>Helpers that drive the real ticket API (used by the Ticketing tests and by tests that need "a ticket" to probe tickets.read).</summary>
public static class TicketHelpers
{
    public static object NewTicketBody(string? subject = null, string? email = null, object? extra = null)
    {
        var body = new Dictionary<string, object?>
        {
            ["subject"] = subject ?? $"Subject {Unique.Id()}",
            ["description"] = "Something is broken.",
            ["requester"] = new { email = email ?? $"req-{Unique.Id()}@example.test", displayName = "Some Requester" },
        };
        if (extra is not null)
        {
            foreach (var property in extra.GetType().GetProperties())
            {
                body[char.ToLowerInvariant(property.Name[0]) + property.Name[1..]] = property.GetValue(extra);
            }
        }

        return body;
    }

    /// <summary>Creates a ticket as <paramref name="user"/> (must hold tickets.create) and returns the 201 response.</summary>
    public static async Task<ApiResponse> CreateTicketAsync(this TestUser user, DepartmentRef department, object? body = null) =>
        (await user.Client.PostAsync($"{department.Url}/tickets", body ?? NewTicketBody())).Expect(HttpStatusCode.Created);

    /// <summary>Creates a ticket in <paramref name="department"/> through a new Agent member and returns the response.</summary>
    public static async Task<ApiResponse> CreateTicketAsync(this HelpdeskFixture fixture, DepartmentRef department, object? body = null)
    {
        var agent = await fixture.NewMemberAsync(department, DepartmentRoles.Agent);
        return await agent.CreateTicketAsync(department, body);
    }
}
