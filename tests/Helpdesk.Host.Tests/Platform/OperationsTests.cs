using System.Net;
using System.Reflection;
using Helpdesk.Host.Tests.Infrastructure;
using Helpdesk.Modules.Attachments.Contracts;
using Helpdesk.SharedKernel.Database;
using Helpdesk.SharedKernel.Operations;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Helpdesk.Host.Tests.Mail;
using Helpdesk.Host.Tests.Ticketing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Helpdesk.Host.Tests.Platform;

/// <summary>Operations rules (WS-7): readiness (D4), the stuck-work signals (SRE-005) and log privacy (SRE-012).</summary>
public sealed class OperationsTests(HelpdeskFixture fixture) : IClassFixture<HelpdeskFixture>
{
    private sealed class UnreachableStore : IObjectStore
    {
        public Task PutAsync(string key, byte[] content, string contentType, CancellationToken cancellationToken = default) => throw new HttpRequestException("down");

        public Task<Stream?> OpenReadAsync(string key, CancellationToken cancellationToken = default) => throw new HttpRequestException("down");
    }

    [Fact]
    public async Task Readiness_fails_when_the_object_store_is_down_and_not_because_of_job_backlog()
    {
        (await fixture.CreateAnonymousClient().GetAsync("/health/ready")).Expect(HttpStatusCode.OK);

        var down = fixture.Factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.RemoveAll<IObjectStore>();
            s.AddSingleton<IObjectStore, UnreachableStore>();
        }));
        (await fixture.CreateAnonymousClient(down).GetAsync("/health/ready")).Expect(HttpStatusCode.ServiceUnavailable);
        (await fixture.CreateAnonymousClient(down).GetAsync("/health/live")).Expect(HttpStatusCode.OK); // alive, just not ready
    }

    [Fact]
    public async Task Every_module_reports_its_schema_up_to_date_after_migration()
    {
        foreach (var migration in fixture.Factory.Services.GetServices<IModuleMigration>())
        {
            Assert.True(await migration.IsUpToDateAsync(CancellationToken.None), migration.ModuleName);
        }
    }

    private async Task<double> ReadAsync(string name) =>
        (await fixture.Factory.Services.GetServices<IOperationalSignal>().Single(s => s.Name == name).ReadAsync(CancellationToken.None)) ?? -1;

    [Fact]
    public async Task The_stuck_work_signals_grow_with_the_oldest_waiting_item()
    {
        var d = await fixture.NewDeptAsync();
        var box = await fixture.CreateMailboxAsync(d);
        var subject = $"Signals {Unique.Id()}";
        await fixture.ReceiveAsync(box, MailKit.Mail(MailKit.NewSender(), subject));
        var ticketId = Assert.Single(await d.Agent.TicketsAsync(d, subject)).GetProperty("id").GetGuid();

        // Nothing outstanding after the mailbox is synchronised and the queue is drained.
        await fixture.DispatchAsync();
        Assert.True(await ReadAsync("helpdesk_mail_sync_oldest_age_seconds") >= 0);

        // A queued reply that has waited two hours.
        (await d.Agent.PostMessageAsync(ticketId, "Public", "Waiting")).Expect(HttpStatusCode.Created);
        await fixture.ExecuteAsync("UPDATE mail.messages SET created_at = now() - interval '2 hours' WHERE ticket_id = @t AND direction = 'Outbound' AND state = 'Queued'", TicketKit.P("t", ticketId));
        Assert.True(await ReadAsync("helpdesk_mail_outbound_oldest_queued_age_seconds") >= 7000);
        await fixture.DispatchAsync();

        // An undrained notification outbox item that has waited two hours.
        await fixture.ExecuteAsync(
            "UPDATE ticketing.notification_outbox SET processed_at = NULL, created_at = now() - interval '2 hours' WHERE id = (SELECT id FROM ticketing.notification_outbox WHERE ticket_id = @t LIMIT 1)",
            TicketKit.P("t", ticketId));
        Assert.True(await ReadAsync("helpdesk_notification_outbox_oldest_age_seconds") >= 7000);
    }

    [Fact]
    public void Log_templates_never_carry_addresses_subjects_bodies_or_secrets()
    {
        string[] denied = ["address", "email", "subject", "body", "token", "secret", "password", "sender", "recipient", "displayname"];
        var assemblies = typeof(Program).Assembly.GetReferencedAssemblies().Where(a => a.Name!.StartsWith("Helpdesk.", StringComparison.Ordinal)).Select(Assembly.Load).Append(typeof(Program).Assembly);

        var offenders = new List<string>();
        foreach (var method in assemblies.SelectMany(a => a.GetTypes()).SelectMany(t => t.GetMethods(BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)))
        {
            var attribute = method.GetCustomAttribute<LoggerMessageAttribute>();
            if (attribute is null)
            {
                continue;
            }

            var names = method.GetParameters().Select(p => p.Name!).Concat(System.Text.RegularExpressions.Regex.Matches(attribute.Message ?? "", @"\{(\w+)").Select(m => m.Groups[1].Value));
            offenders.AddRange(names.Where(n => denied.Any(d => n.Contains(d, StringComparison.OrdinalIgnoreCase))).Select(n => $"{method.DeclaringType?.Name}.{method.Name}: {n}"));
        }

        Assert.Empty(offenders);
    }
}
