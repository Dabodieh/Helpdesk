#pragma warning disable CA1861 // one-off request payloads
using System.Net;
using Hangfire;
using Hangfire.Common;
using Helpdesk.Host.Jobs;
using Helpdesk.Host.Tests.Infrastructure;
using Helpdesk.Host.Tests.Portal;
using Helpdesk.Host.Tests.Ticketing;
using Helpdesk.Modules.Mail.Providers;

namespace Helpdesk.Host.Tests.Mail;

/// <summary>Recovery and concurrency paths production depends on (WS-11): stuck sends, recurring jobs, merged tickets in bulk, ownership by explicit link only.</summary>
public sealed class RecoveryPathTests(HelpdeskFixture fixture) : IClassFixture<HelpdeskFixture>
{
    private async Task<(Dept D, Mailbox Box, Guid TicketId)> StartAsync()
    {
        var d = await fixture.NewDeptAsync();
        var box = await fixture.CreateMailboxAsync(d);
        var subject = $"Stuck {Unique.Id()}";
        await fixture.ReceiveAsync(box, MailKit.Mail(MailKit.NewSender(), subject));
        return (d, box, Assert.Single(await d.Agent.TicketsAsync(d, subject)).GetProperty("id").GetGuid());
    }

    private Task<int> MakeStuckAsync(Guid ticketId) =>
        fixture.ExecuteAsync("UPDATE mail.messages SET state = 'Sending', updated_at = now() - interval '20 minutes' WHERE ticket_id = @t AND direction = 'Outbound'", TicketKit.P("t", ticketId));

    private Task<string?> OutboundStateAsync(Guid ticketId) =>
        fixture.ScalarAsync<string>("SELECT state FROM mail.messages WHERE ticket_id = @t AND direction = 'Outbound'", TicketKit.P("t", ticketId));

    [Fact]
    public async Task A_crashed_worker_before_the_submit_call_requeues_the_send_and_it_goes_out_exactly_once()
    {
        var s = await StartAsync();
        (await s.D.Agent.PostMessageAsync(s.TicketId, "Public", "Before submit")).Expect(HttpStatusCode.Created);
        await MakeStuckAsync(s.TicketId); // claimed, no submit_started flag: nothing reached the provider

        await fixture.DispatchAsync();

        Assert.Equal("Sent", await OutboundStateAsync(s.TicketId));
        Assert.Single(fixture.Fake().SentMail(s.Box.Address));
    }

    [Fact]
    public async Task A_crashed_worker_after_the_submit_call_is_reconciled_against_the_sent_copy_and_never_blindly_resent()
    {
        // The real path leaves the marker: a timeout after submit_started is recorded.
        var s = await StartAsync();
        fixture.Fake().TimeoutNextSends(1, reachedProvider: false);
        (await s.D.Agent.PostMessageAsync(s.TicketId, "Public", "Never reached")).Expect(HttpStatusCode.Created);
        await fixture.DispatchAsync();
        Assert.Equal(Helpdesk.Modules.Mail.Domain.MessageFlags.SubmitStarted, await fixture.ScalarAsync<string>("SELECT flags FROM mail.messages WHERE ticket_id = @t AND direction = 'Outbound'", TicketKit.P("t", s.TicketId)));

        // Branch 1: no sent copy exists, so it becomes Uncertain for an operator and nothing is sent.
        await MakeStuckAsync(s.TicketId);
        await fixture.DispatchAsync();
        Assert.Equal("Uncertain", await OutboundStateAsync(s.TicketId));
        Assert.Empty(fixture.Fake().SentMail(s.Box.Address));

        // Branch 2: the provider did accept it (the copy appears late), so it is marked Sent and still sent exactly once.
        var t = await StartAsync();
        fixture.Fake().TimeoutNextSends(1, reachedProvider: true);
        fixture.Fake().HideNextSentCopyLookups(1);
        (await t.D.Agent.PostMessageAsync(t.TicketId, "Public", "Accepted")).Expect(HttpStatusCode.Created);
        await fixture.DispatchAsync();
        Assert.Equal("Uncertain", await OutboundStateAsync(t.TicketId));
        await MakeStuckAsync(t.TicketId);
        await fixture.DispatchAsync();
        Assert.Equal("Sent", await OutboundStateAsync(t.TicketId));
        Assert.Single(fixture.Fake().SentMail(t.Box.Address));
    }

    private sealed class RecordingManager : IRecurringJobManager
    {
        public Dictionary<string, string> Registered { get; } = [];

        public void AddOrUpdate(string recurringJobId, Job job, string cronExpression, RecurringJobOptions options) => Registered[recurringJobId] = cronExpression;

        public void Trigger(string recurringJobId)
        {
        }

        public void RemoveIfExists(string recurringJobId)
        {
        }
    }

    [Fact]
    public async Task The_recurring_jobs_the_system_depends_on_are_registered_when_the_host_starts()
    {
        var manager = new RecordingManager();
        await new RecurringJobsRegistration(manager).StartAsync(CancellationToken.None);

        Assert.Equal(["mail-dispatch", "mail-subscriptions", "mail-sync-all", "notification-outbox", "sla-monitor"], manager.Registered.Keys.Order(StringComparer.Ordinal).ToArray());
        Assert.Equal("* * * * *", manager.Registered["mail-dispatch"]); // outbound mail leaves within a minute
        Assert.Equal("* * * * *", manager.Registered["sla-monitor"]);
    }

    [Fact]
    public async Task A_bulk_edit_of_a_merged_source_is_refused_for_that_ticket_and_changes_nothing()
    {
        var d = await fixture.NewDeptAsync();
        var email = $"dana-{Unique.Id()}@example.test";
        var main = (await d.TicketForAsync(email, "Main")).Expect(HttpStatusCode.Created);
        var source = (await d.TicketForAsync(email, "Duplicate")).Expect(HttpStatusCode.Created);
        var mainDetail = (await d.Agent.GetTicketAsync(main.Id)).Expect(HttpStatusCode.OK);
        var sourceDetail = (await d.Agent.GetTicketAsync(source.Id)).Expect(HttpStatusCode.OK);
        (await d.Agent.Client.PostAsync(TicketKit.Url(main.Id, "/merge"), new { version = mainDetail.Version, sources = new[] { new { ticketId = source.Id, version = sourceDetail.Version } } })).Expect(HttpStatusCode.OK);

        var result = (await d.Lead.Client.PostAsync("/api/tickets/bulk", new { ticketIds = new[] { source.Id, main.Id }, priority = "Urgent" })).Expect(HttpStatusCode.OK).Json;
        string Outcome(Guid id) => result.GetProperty("results").EnumerateArray().Single(r => r.GetProperty("ticketId").GetGuid() == id).GetProperty("outcome").GetString()!;
        Assert.Equal("invalid", Outcome(source.Id));
        Assert.Equal("updated", Outcome(main.Id));
        Assert.NotEqual("Urgent", (await d.Agent.GetTicketAsync(source.Id)).Json.GetProperty("priority").GetString());
    }

    [Fact]
    public async Task A_ticket_created_for_a_registered_employees_address_is_not_theirs_until_an_explicit_link_exists()
    {
        // Negative control for the raw-SQL link the other portal tests use: without it, the same address owns nothing (email is not proof of ownership).
        var d = await fixture.NewDeptAsync();
        var alice = await fixture.EmployeeAsync("alice");
        (await d.Agent.CreateTicketAsync(d.Department, TicketHelpers.NewTicketBody($"Unlinked {Unique.Id()}", alice.Email))).Expect(HttpStatusCode.Created);

        Assert.Empty((await alice.PortalListAsync()).GetProperty("items").EnumerateArray());
    }
}
