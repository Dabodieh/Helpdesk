using System.Net;
using System.Text.Json;
using Helpdesk.Host.Tests.Infrastructure;
using Helpdesk.Modules.Organisation.Contracts;
using Npgsql;

namespace Helpdesk.Host.Tests.Ticketing;

/// <summary>One department with one member per role, built through the real API.</summary>
public sealed record Dept(DepartmentRef Department, TestUser Admin, TestUser Lead, TestUser Agent, TestUser Viewer)
{
    public Guid Id => Department.Id;

    public string Key => Department.Key;
}

public static class TicketKit
{
    public static async Task<Dept> NewDeptAsync(this HelpdeskFixture fixture)
    {
        var department = await fixture.CreateDepartmentAsync();
        return new Dept(
            department,
            await fixture.NewMemberAsync(department, DepartmentRoles.DepartmentAdmin),
            await fixture.NewMemberAsync(department, DepartmentRoles.TeamLead),
            await fixture.NewMemberAsync(department, DepartmentRoles.Agent),
            await fixture.NewMemberAsync(department, DepartmentRoles.Viewer));
    }

    public static string Url(Guid ticketId, string suffix = "") => $"/api/tickets/{ticketId}{suffix}";

    public static Task<ApiResponse> GetTicketAsync(this TestUser user, Guid ticketId) => user.Client.GetAsync(Url(ticketId));

    public static async Task<ApiResponse> PatchTicketAsync(this TestUser user, ApiResponse ticket, object fields)
    {
        var body = new Dictionary<string, object?> { ["version"] = ticket.Version };
        foreach (var property in fields.GetType().GetProperties())
        {
            body[char.ToLowerInvariant(property.Name[0]) + property.Name[1..]] = property.GetValue(fields);
        }

        return await user.Client.PatchAsync(Url(ticket.Id), body);
    }

    public static Task<ApiResponse> AssignAsync(this TestUser user, Guid ticketId, string version, Guid? assigneeUserId) =>
        user.Client.PutAsync(Url(ticketId, "/assignment"), new { version, assigneeUserId });

    public static Task<ApiResponse> PostMessageAsync(this TestUser user, Guid ticketId, string kind, string body, string? status = null) =>
        user.Client.PostAsync(Url(ticketId, "/messages"), status is null ? new { kind, body } : new { kind, body, status });

    public static string Code(this ApiResponse response) => response.Json.GetProperty("code").GetString()!;

    public static ApiResponse ExpectCode(this ApiResponse response, HttpStatusCode status, string code)
    {
        response.Expect(status);
        Assert.Equal(code, response.Code());
        return response;
    }

    public static List<string> Errors(this ApiResponse response, string field) =>
        response.Json.GetProperty("errors").GetProperty(field).EnumerateArray().Select(e => e.GetString()!).ToList();

    public static string Str(this JsonElement element, string property) => element.GetProperty(property).GetString()!;

    public static Guid[] Ids(this ApiResponse page) =>
        page.Json.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToArray();

    public static string? NextCursor(this ApiResponse page) =>
        page.Json.GetProperty("nextCursor").ValueKind == JsonValueKind.Null ? null : page.Json.GetProperty("nextCursor").GetString();

    public static NpgsqlParameter P(string name, object value) => new(name, value);

    /// <summary>Audit rows of a ticket (action, actor, source, department, previous, next), oldest first.</summary>
    public static async Task<List<AuditRow>> AuditOfAsync(this HelpdeskFixture fixture, Guid objectId)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT action, actor_user_id, actor_display_name, source, department_id, previous::text, next::text, category, object_type " +
            "FROM audit.audit_events WHERE object_id = @o ORDER BY occurred_at, id",
            connection);
        command.Parameters.AddWithValue("o", objectId);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<AuditRow>();
        while (await reader.ReadAsync())
        {
            rows.Add(new AuditRow(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetGuid(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetGuid(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.GetString(7),
                reader.GetString(8)));
        }

        return rows;
    }
}

public sealed record AuditRow(
    string Action, Guid? ActorUserId, string? ActorName, string Source, Guid? DepartmentId, string? Previous, string? Next, string Category, string ObjectType);
