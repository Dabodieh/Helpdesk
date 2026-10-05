namespace Helpdesk.GraphSpike.Tests;

/// <summary>Idempotency keys, webhook nudge handling, lifecycle decisions and loop-prevention hints.</summary>
public sealed class IngestionTests
{
    private const string It = "support@contoso.example";
    private const string Hr = "hr@contoso.example";

    private static SpikeMessage Load(string fixture, string mailbox) =>
        GraphPayloadParser.ParseMessage(Fixtures.Read(fixture), mailbox);

    private static NotificationProcessor Processor()
    {
        var processor = new NotificationProcessor();
        processor.Register("sub-it-0001", It, "test-client-state-it");
        processor.Register("sub-hr-0001", Hr, "test-client-state-hr");
        return processor;
    }

    // ---- idempotency -------------------------------------------------------------------------------------------

    [Fact]
    public void Same_message_delivered_twice_is_ingested_once()
    {
        var store = new IngestStore();
        var message = Load("inbound-new-it.json", It);
        Assert.Equal(IngestOutcome.Ingested, store.TryIngest(message));
        Assert.Equal(IngestOutcome.DuplicateImmutableId, store.TryIngest(message));
    }

    [Fact]
    public void Changed_graph_id_for_the_same_message_is_caught_by_internet_message_id_fallback()
    {
        var store = new IngestStore();
        var original = Load("inbound-new-it.json", It);
        Assert.Equal(IngestOutcome.Ingested, store.TryIngest(original));

        var reissued = original with { ImmutableId = "AAMkAIT-different-mutable-id" };
        Assert.Equal(IngestOutcome.DuplicateInternetMessageId, store.TryIngest(reissued));
    }

    [Fact]
    public void Reused_message_id_with_different_content_is_ingested_and_flagged_not_dropped()
    {
        var store = new IngestStore();
        var original = Load("inbound-new-it.json", It);
        store.TryIngest(original);

        var different = original with { ImmutableId = "AAMkAIT-other", Subject = "Something else entirely" };
        Assert.Equal(IngestOutcome.InternetMessageIdCollisionIngested, store.TryIngest(different));
    }

    [Fact]
    public void Idempotency_keys_are_scoped_per_mailbox()
    {
        var store = new IngestStore();
        var inIt = Load("inbound-new-it.json", It);
        var inHr = inIt with { MailboxId = Hr };
        Assert.Equal(IngestOutcome.Ingested, store.TryIngest(inIt));
        Assert.Equal(IngestOutcome.Ingested, store.TryIngest(inHr));
    }

    [Fact]
    public void Graph_ids_are_compared_case_sensitively()
    {
        var store = new IngestStore();
        var a = Load("inbound-new-it.json", It) with { InternetMessageId = null, ImmutableId = "AAMkAbCd" };
        var b = a with { ImmutableId = "AAMkAbcD" };
        Assert.Equal(IngestOutcome.Ingested, store.TryIngest(a));
        Assert.Equal(IngestOutcome.Ingested, store.TryIngest(b));
    }

    [Fact]
    public void Message_without_internet_message_id_relies_on_immutable_id_only()
    {
        var store = new IngestStore();
        var draftLike = Load("inbound-new-it.json", It) with { InternetMessageId = null };
        Assert.Equal(IngestOutcome.Ingested, store.TryIngest(draftLike));
        Assert.Equal(IngestOutcome.DuplicateImmutableId, store.TryIngest(draftLike));
    }

    // ---- notifications -----------------------------------------------------------------------------------------

    [Fact]
    public void Redelivered_notification_produces_a_fetch_target_only_once()
    {
        var processor = Processor();
        var json = Fixtures.Read("notification-created.json");
        Assert.Single(processor.Process(json));
        Assert.Empty(processor.Process(json));
    }

    [Fact]
    public void Out_of_order_and_duplicate_notifications_collapse_to_one_fetch_per_message()
    {
        var targets = Processor().Process(Fixtures.Read("notifications-batch-out-of-order.json"));

        Assert.Equal(3, targets.Count);
        Assert.Contains(new FetchTarget(It, "AAMkAIT-imm-001"), targets);
        Assert.Contains(new FetchTarget(It, "AAMkAIT-imm-002"), targets);
        Assert.Contains(new FetchTarget(Hr, "AAMkHR-imm-900"), targets);
    }

    [Fact]
    public void Mailbox_comes_from_the_subscription_record_not_the_payload()
    {
        // The payload resource path names user 3333..., but sub-it-0001 is OUR subscription for the IT mailbox.
        var json = Fixtures.Read("notification-created.json").Replace("1111", "3333", StringComparison.Ordinal);
        var targets = Processor().Process(json);
        Assert.Equal(It, Assert.Single(targets).MailboxId);
    }

    [Fact]
    public void Forged_client_state_and_unknown_subscriptions_are_rejected()
    {
        var processor = Processor();
        var parsed = NotificationProcessor.Parse(Fixtures.Read("notification-bad-clientstate.json"));
        Assert.Equal(NotificationValidity.ClientStateMismatch, processor.Validate(parsed[0]));
        Assert.Equal(NotificationValidity.UnknownSubscription, processor.Validate(parsed[1]));
        Assert.Empty(processor.Process(Fixtures.Read("notification-bad-clientstate.json")));
    }

    [Fact]
    public void Lifecycle_notifications_map_to_recovery_actions()
    {
        var actions = Processor().ProcessLifecycle(Fixtures.Read("lifecycle-notifications.json"));
        Assert.Equal(
            new[]
            {
                LifecycleAction.RenewSubscription,
                LifecycleAction.ReconcileViaDelta,
                LifecycleAction.RecreateSubscriptionAndReconcile,
            },
            actions.Select(a => a.Action).ToArray());
        Assert.Equal("sub-hr-0001", actions[2].SubscriptionId);
    }

    // ---- loop / noise prevention -------------------------------------------------------------------------------

    private static MailboxRegistry Registry() => new MailboxRegistry().Add(It, "dept-it").Add(Hr, "dept-hr");

    [Fact]
    public void Auto_reply_is_flagged()
    {
        var reason = MailLoopGuard.Evaluate(Load("inbound-auto-reply-it.json", It), Registry());
        Assert.Equal("auto-submitted", reason);
    }

    [Fact]
    public void Mail_from_another_helpdesk_mailbox_is_flagged_to_prevent_inter_department_loops()
    {
        var message = Load("inbound-reply-it.json", It) with { FromAddress = Hr };
        Assert.Equal("own-mailbox-sender", MailLoopGuard.Evaluate(message, Registry()));
    }

    [Fact]
    public void Ordinary_customer_mail_is_not_flagged()
    {
        Assert.Null(MailLoopGuard.Evaluate(Load("inbound-reply-it.json", It), Registry()));
    }

    [Theory]
    [InlineData("Precedence", "bulk", "precedence")]
    [InlineData("List-Id", "<list.example>", "mailing-list")]
    [InlineData("X-Auto-Response-Suppress", "OOF", "auto-response-suppress")]
    [InlineData("Content-Type", "multipart/report; report-type=delivery-status", "delivery-status-report")]
    public void Noise_headers_are_flagged(string header, string value, string expected)
    {
        var message = Load("inbound-reply-it.json", It);
        var headers = new Dictionary<string, string>(message.Headers, StringComparer.OrdinalIgnoreCase) { [header] = value };
        Assert.Equal(expected, MailLoopGuard.Evaluate(message with { Headers = headers }, Registry()));
    }

    [Fact]
    public void Bounce_sender_is_flagged()
    {
        var message = Load("inbound-reply-it.json", It) with { FromAddress = "MAILER-DAEMON@mail.fabrikam.example" };
        Assert.Equal("bounce-sender", MailLoopGuard.Evaluate(message, Registry()));
    }
}
