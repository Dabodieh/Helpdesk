using System.Net;
using System.Text.Json;
using Helpdesk.Host.Tests.Infrastructure;
using Helpdesk.Host.Tests.Ticketing;
using Helpdesk.Modules.Mail.Contracts;
using Helpdesk.Modules.Mail.Providers;
using Microsoft.Extensions.DependencyInjection;

namespace Helpdesk.Host.Tests.Mail;

/// <summary>Helpers for the email loop tests: the in-process mailbox simulator, the job runner and a few API shortcuts.</summary>
internal static class MailKit
{
    public static FakeMailProvider Fake(this HelpdeskFixture fixture) => fixture.Factory.Services.GetRequiredService<FakeMailProvider>();

    public static string NewAddress() => $"mb{Unique.Id()}@helpdesk.test";

    public static string NewSender() => $"person{Unique.Id()}@customer.test";

    /// <summary>Creates a mailbox through the admin API (the department admin of <paramref name="d"/>).</summary>
    public static async Task<Mailbox> CreateMailboxAsync(this HelpdeskFixture fixture, Dept d, string? address = null, string? signature = "Regards,\nThe Service Desk", bool? isDefault = null)
    {
        _ = fixture;
        address ??= NewAddress();
        var response = (await d.Admin.Client.PostAsync(
            $"{d.Department.Url}/mailboxes", new { address, displayName = $"Desk {Unique.Id()}", signature, isDefault })).Expect(HttpStatusCode.Created);
        return new Mailbox(response.Id, address, response.Json.GetProperty("displayName").GetString()!, d);
    }

    public static Task SyncAsync(this HelpdeskFixture fixture, Mailbox mailbox) =>
        fixture.WithScopeAsync(sp => sp.GetRequiredService<IMailJobRunner>().RunSyncAsync(mailbox.Id));

    public static Task DispatchAsync(this HelpdeskFixture fixture) =>
        fixture.WithScopeAsync(sp => sp.GetRequiredService<IMailJobRunner>().RunDispatchAsync());

    /// <summary>Delivers a message to the mailbox and runs a delta sync, like a notification followed by the job would.</summary>
    public static async Task<string> ReceiveAsync(this HelpdeskFixture fixture, Mailbox mailbox, FakeInbound mail)
    {
        var id = fixture.Fake().Inject(mail with { Mailbox = mailbox.Address });
        await fixture.SyncAsync(mailbox);
        return id;
    }

    public static FakeInbound Mail(string from, string subject, string body = "Hello, I need help.", string? fromName = null) =>
        new("unset@helpdesk.test", from, subject, fromName ?? "Some Person", body);

    /// <summary>Tickets of the department (any status) whose subject contains the text, newest first.</summary>
    public static async Task<List<JsonElement>> TicketsAsync(this TestUser user, Dept d, string subjectContains)
    {
        var response = (await user.Client.GetAsync(
            $"/api/tickets?queue=all&departmentId={d.Id}&q={Uri.EscapeDataString(subjectContains)}&status=Open&status=Pending&status=Resolved&status=Closed&limit=100"))
            .Expect(HttpStatusCode.OK);
        return [.. response.Json.GetProperty("items").EnumerateArray()];
    }

    public static async Task<List<JsonElement>> MessagesAsync(this TestUser user, Guid ticketId)
    {
        var response = (await user.Client.GetAsync($"/api/tickets/{ticketId}/messages?limit=200")).Expect(HttpStatusCode.OK);
        return [.. response.Json.GetProperty("items").EnumerateArray().Reverse()]; // oldest first
    }

    public static async Task<long> CountAsync(this HelpdeskFixture fixture, string sql, params Npgsql.NpgsqlParameter[] parameters) =>
        await fixture.ScalarAsync<long>(sql, parameters);
}

internal sealed record Mailbox(Guid Id, string Address, string DisplayName, Dept Dept);
