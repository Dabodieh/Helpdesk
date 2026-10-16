#pragma warning disable CA1861 // one-off request payloads
using System.Net;
using System.Text.Json;
using Helpdesk.Host.Tests.Infrastructure;
using Helpdesk.Host.Tests.Ticketing;

namespace Helpdesk.Host.Tests.Mail;

/// <summary>What an administrator sees of late and failed outbound email: the waiting list, the nature of a problem, the audit of a manual retry, and the job-lag signal.</summary>
public sealed class DeliveryMonitoringTests(HelpdeskFixture fixture) : IClassFixture<HelpdeskFixture>
{
    private async Task<(Dept D, Guid TicketId, Guid MailId)> QueuedReplyAsync()
    {
        var d = await fixture.NewDeptAsync();
        var box = await fixture.CreateMailboxAsync(d);
        var subject = $"Monitor {Unique.Id()}";
        await fixture.ReceiveAsync(box, MailKit.Mail(MailKit.NewSender(), subject));
        var ticketId = Assert.Single(await d.Agent.TicketsAsync(d, subject)).GetProperty("id").GetGuid();
        (await d.Agent.PostMessageAsync(ticketId, "Public", "Waiting reply")).Expect(HttpStatusCode.Created);
        var mailId = await fixture.ScalarAsync<Guid>("SELECT id FROM mail.messages WHERE ticket_id = @t AND direction = 'Outbound'", TicketKit.P("t", ticketId));
        return (d, ticketId, mailId);
    }

    private static async Task<JsonElement> WaitingAsync(TestUser user, Dept d, string query = "") =>
        (await user.Client.GetAsync($"{d.Department.Url}/mail/waiting{query}")).Expect(HttpStatusCode.OK).Json;

    private Task<int> SetAsync(Guid mailId, string assignments) => fixture.ExecuteAsync($"UPDATE mail.messages SET {assignments} WHERE id = @m", TicketKit.P("m", mailId));

    [Fact]
    public async Task Late_outbound_mail_is_listed_with_its_reason_and_nature_and_fresh_or_finished_mail_is_not()
    {
        var (d, ticketId, mailId) = await QueuedReplyAsync();

        // Just queued: not late.
        Assert.Equal(0, (await WaitingAsync(d.Admin, d)).GetProperty("total").GetInt32());

        // Queued longer than expected.
        await SetAsync(mailId, "created_at = now() - interval '10 minutes'");
        var page = await WaitingAsync(d.Admin, d);
        var item = Assert.Single(page.GetProperty("items").EnumerateArray());
        Assert.Equal((mailId, "Queued", "overdue", 5), (item.GetProperty("id").GetGuid(), item.Str("state"), item.Str("reason"), page.GetProperty("waitingAfterMinutes").GetInt32()));
        Assert.Equal(ticketId, item.GetProperty("ticketId").GetGuid());

        // A failed send that will be tried again: retrying, with the temporary nature of a provider outage.
        await SetAsync(mailId, "attempts = 2, last_error = 'graph_503', next_attempt_at = now() + interval '5 minutes'");
        item = Assert.Single((await WaitingAsync(d.Admin, d)).GetProperty("items").EnumerateArray());
        Assert.Equal(("retrying", "provider_unavailable", "temporary", 2), (item.Str("reason"), item.Str("category"), item.Str("nature"), item.GetProperty("attempts").GetInt32()));

        // Claimed by a worker that never finished: a fresh claim is not late, a stale one is stalled.
        await SetAsync(mailId, "state = 'Sending', last_error = NULL, updated_at = now()");
        Assert.Equal(0, (await WaitingAsync(d.Admin, d)).GetProperty("total").GetInt32());
        await SetAsync(mailId, "updated_at = now() - interval '8 minutes'");
        Assert.Equal("sending_stalled", Assert.Single((await WaitingAsync(d.Admin, d)).GetProperty("items").EnumerateArray()).Str("reason"));

        // Finished mail is not "waiting".
        await SetAsync(mailId, "state = 'Sent'");
        Assert.Equal(0, (await WaitingAsync(d.Admin, d)).GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task The_waiting_list_needs_mailbox_administration_is_department_scoped_and_hides_content_from_non_members()
    {
        var (d, _, mailId) = await QueuedReplyAsync();
        var other = await fixture.NewDeptAsync();
        await SetAsync(mailId, "created_at = now() - interval '10 minutes'");

        (await d.Agent.Client.GetAsync($"{d.Department.Url}/mail/waiting")).Expect(HttpStatusCode.Forbidden);
        (await fixture.CreateAnonymousClient().GetAsync($"{d.Department.Url}/mail/waiting")).Expect(HttpStatusCode.Unauthorized);
        (await other.Admin.Client.GetAsync($"{d.Department.Url}/mail/waiting")).Expect(HttpStatusCode.NotFound);
        Assert.Equal(0, (await WaitingAsync(other.Admin, other)).GetProperty("total").GetInt32()); // another department's late mail is invisible
        (await d.Admin.Client.GetAsync($"{d.Department.Url}/mail/waiting?limit=0")).Expect(HttpStatusCode.BadRequest);

        // A platform administrator sees late mail in every department with its address and subject (S-02).
        var platform = await WaitingAsync(await fixture.PlatformAdminAsync(), d);
        var item = Assert.Single(platform.GetProperty("items").EnumerateArray());
        Assert.Equal(mailId, item.GetProperty("id").GetGuid());
        Assert.NotEqual("", item.Str("address"));
        Assert.NotEqual("", item.Str("subject"));
    }

    [Fact]
    public async Task The_attention_list_says_whether_a_problem_is_temporary_permanent_uncertain_or_needs_action()
    {
        var (d, _, mailId) = await QueuedReplyAsync();
        async Task<JsonElement> ItemAsync(string error, string state = "Failed")
        {
            await SetAsync(mailId, $"state = '{state}', last_error = '{error}'");
            return (await d.Admin.Client.GetAsync($"{d.Department.Url}/mail/attention")).Expect(HttpStatusCode.OK).Json.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == mailId);
        }

        Assert.Equal(("temporary", "rate_limited"), ((await ItemAsync("graph_429")).Str("nature"), (await ItemAsync("graph_429")).Str("category")));
        Assert.Equal("temporary", (await ItemAsync("graph_503")).Str("nature"));
        Assert.Equal("permanent", (await ItemAsync("graph_400")).Str("nature"));
        Assert.Equal("permanent", (await ItemAsync("ndr_delivery_failed")).Str("nature"));
        Assert.Equal("action_required", (await ItemAsync("graph_403")).Str("nature"));
        Assert.Equal("action_required", (await ItemAsync("mailbox_inactive")).Str("nature"));
        Assert.Equal("uncertain", (await ItemAsync("outcome_uncertain", "Uncertain")).Str("nature"));
    }

    [Fact]
    public async Task A_manual_retry_of_a_failed_send_is_audited_once_and_queues_it_without_resending_twice()
    {
        var (d, _, mailId) = await QueuedReplyAsync();
        await SetAsync(mailId, "state = 'Failed', attempts = 5, last_error = 'graph_503'");

        Assert.Null((await d.Admin.Client.PostAsync($"{d.Department.Url}/mail/{mailId}/retry")).ExpectOrNoContent());
        Assert.Equal("Queued", await fixture.ScalarAsync<string>("SELECT state FROM mail.messages WHERE id = @m", TicketKit.P("m", mailId)));
        Assert.Equal(1, await fixture.CountAuditAsync("mail.failed_retried", mailId));

        // It is queued now, so a second retry finds nothing to retry and records nothing more.
        (await d.Admin.Client.PostAsync($"{d.Department.Url}/mail/{mailId}/retry")).Expect(HttpStatusCode.NotFound);
        Assert.Equal(1, await fixture.CountAuditAsync("mail.failed_retried", mailId));
    }

    [Fact]
    public void The_mail_subscription_maintenance_job_is_watched_like_the_other_jobs()
    {
        // Subscriptions expire silently if this job stops (webhooks stop; only the five-minute delta sync keeps mail flowing), so its lag is exported too.
        Assert.Contains(Helpdesk.Host.Jobs.HangfireSetup.MonitoredJobs, j => j is { JobId: "mail-subscriptions", MetricSuffix: "mail_subscriptions" });
        Assert.Contains(Helpdesk.Host.Jobs.HangfireSetup.MonitoredJobs, j => j.JobId == "mail-dispatch");
    }
}

internal static class ApiResponseExtensions
{
    /// <summary>The retry endpoint answers 204 (queued) or 200 with an outcome; this asserts success and returns the outcome text or null.</summary>
    public static string? ExpectOrNoContent(this ApiResponse response)
    {
        Assert.True(response.Status is HttpStatusCode.NoContent or HttpStatusCode.OK, $"unexpected {response.Status}: {response.Body}");
        return response.Status == HttpStatusCode.NoContent ? null : response.Json.Str("outcome");
    }
}
