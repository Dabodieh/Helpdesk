namespace Helpdesk.GraphSpike.Tests;

/// <summary>
/// Ticket/message correlation precedence and department isolation (EMAIL_ARCHITECTURE.md, "Graph spike findings").
/// Fixtures are hand-built from the documented Graph message shape; they do not prove real Exchange behaviour.
/// </summary>
public sealed class CorrelationTests
{
    private const string It = "support@contoso.example";
    private const string Hr = "hr@contoso.example";
    private const string Token = "tok9f3a1c7d5e2b4a60";

    private readonly MailboxRegistry _registry = new MailboxRegistry().Add(It, "dept-it").Add(Hr, "dept-hr");
    private readonly TicketCorrelator _correlator;

    public CorrelationTests()
    {
        _correlator = new TicketCorrelator(_registry);
        _correlator.AddTicket(new TicketInfo("t-it-42", "dept-it", It, "IT-42", ["alice@fabrikam.example"]));
        _correlator.Record("t-it-42", Load("inbound-new-it.json", It));
        _correlator.Record("t-it-42", Load("outbound-reply-it.json", It));
        _correlator.RegisterOutboundToken(Token, "t-it-42");
    }

    private static SpikeMessage Load(string fixture, string mailbox) =>
        GraphPayloadParser.ParseMessage(Fixtures.Read(fixture), mailbox);

    [Fact]
    public void Outbound_sent_item_is_parsed_as_outbound_with_threading_headers()
    {
        var sent = Load("outbound-reply-it.json", It);
        Assert.Equal(Direction.Outbound, sent.Direction);
        Assert.Equal("CAF1abc123@mail.fabrikam.example", sent.InReplyTo);
        Assert.Equal("MW4PR01MB1234-0a1b2c3d@MW4PR01MB1234.namprd01.prod.outlook.com", sent.InternetMessageId);
    }

    [Fact]
    public void Reply_matches_stored_outbound_message_by_in_reply_to()
    {
        var result = _correlator.Correlate(Load("inbound-reply-it.json", It));
        Assert.Equal("t-it-42", result.TicketId);
        Assert.Equal(CorrelationMethod.InReplyTo, result.Method);
        Assert.False(result.CrossDepartmentMatchIgnored);
    }

    [Fact]
    public void Subject_change_does_not_break_header_correlation_even_though_conversation_id_changed()
    {
        var message = Load("inbound-subject-changed-it.json", It);
        Assert.NotEqual("AAQkIT-conv-A", message.ConversationId);

        var result = _correlator.Correlate(message);
        Assert.Equal("t-it-42", result.TicketId);
        Assert.Equal(CorrelationMethod.InReplyTo, result.Method);
    }

    [Fact]
    public void References_are_used_when_in_reply_to_is_unknown()
    {
        var message = Load("inbound-reply-it.json", It) with { InReplyTo = "unknown-parent@elsewhere.example" };
        var result = _correlator.Correlate(message);
        Assert.Equal("t-it-42", result.TicketId);
        Assert.Equal(CorrelationMethod.References, result.Method);
    }

    [Fact]
    public void Headers_beat_conversation_id_when_they_disagree()
    {
        _correlator.AddTicket(new TicketInfo("t-it-99", "dept-it", It, "IT-99", []));
        _correlator.Record("t-it-99", Load("inbound-new-it.json", It) with { InternetMessageId = "other@x.example", ConversationId = "conv-other" });

        // Message threads by headers to IT-42 but carries IT-99's conversation id.
        var message = Load("inbound-reply-it.json", It) with { ConversationId = "conv-other" };
        var result = _correlator.Correlate(message);
        Assert.Equal("t-it-42", result.TicketId);
        Assert.Equal(CorrelationMethod.InReplyTo, result.Method);
    }

    [Fact]
    public void Outbound_token_matches_when_no_headers_survive()
    {
        var message = Load("inbound-reply-no-headers-it.json", It) with { ConversationId = "unrelated", BodyText = "See [hd:" + Token + "]" };
        var result = _correlator.Correlate(message);
        Assert.Equal("t-it-42", result.TicketId);
        Assert.Equal(CorrelationMethod.OutboundToken, result.Method);
    }

    [Fact]
    public void Conversation_id_correlates_when_headers_are_stripped_within_the_same_mailbox()
    {
        var result = _correlator.Correlate(Load("inbound-reply-no-headers-it.json", It));
        Assert.Equal("t-it-42", result.TicketId);
        Assert.Equal(CorrelationMethod.ConversationId, result.Method);
    }

    [Fact]
    public void Subject_key_alone_is_a_suggestion_and_does_not_attach()
    {
        var result = _correlator.Correlate(Load("inbound-forwarded-it.json", It));
        Assert.Null(result.TicketId);
        Assert.Equal(CorrelationMethod.None, result.Method);
        Assert.Equal("t-it-42", result.SubjectHintTicketId);
        Assert.True(result.NeedsReview);
    }

    [Fact]
    public void Subject_key_plus_known_participant_sender_still_does_not_attach()
    {
        var forwarded = Load("inbound-forwarded-it.json", It) with { FromAddress = "alice@fabrikam.example" };
        var result = _correlator.Correlate(forwarded);
        Assert.Null(result.TicketId);
        Assert.Equal(CorrelationMethod.None, result.Method);
        Assert.Equal("t-it-42", result.SubjectHintTicketId);
        Assert.True(result.NeedsReview);
    }

    [Fact]
    public void Subject_key_plus_strong_evidence_attaches_by_the_strong_evidence()
    {
        var message = Load("inbound-forwarded-it.json", It) with { InReplyTo = "CAF1abc123@mail.fabrikam.example" };
        var result = _correlator.Correlate(message);
        Assert.Equal("t-it-42", result.TicketId);
        Assert.Equal(CorrelationMethod.InReplyTo, result.Method);
        Assert.False(result.NeedsReview);
    }

    [Fact]
    public void Strong_evidence_wins_over_a_subject_key_naming_another_ticket()
    {
        _correlator.AddTicket(new TicketInfo("t-it-99", "dept-it", It, "IT-99", []));
        var message = Load("inbound-reply-it.json", It) with { Subject = "Re: [IT-99] something else" };
        var result = _correlator.Correlate(message);
        Assert.Equal("t-it-42", result.TicketId);
        Assert.Equal(CorrelationMethod.InReplyTo, result.Method);
    }

    [Fact]
    public void Reply_landing_in_another_departments_mailbox_never_attaches_across_departments()
    {
        // Carries In-Reply-To/References of IT's outbound mail, IT's body token and IT's subject key.
        var result = _correlator.Correlate(Load("inbound-cross-mailbox-hr.json", Hr));
        Assert.Null(result.TicketId);
        Assert.Equal(CorrelationMethod.None, result.Method);
        Assert.Null(result.SubjectHintTicketId);
        Assert.True(result.CrossDepartmentMatchIgnored);
    }

    [Fact]
    public void Wrong_department_subject_key_never_attaches_and_is_not_even_suggested()
    {
        var message = Load("inbound-forwarded-it.json", Hr);
        var result = _correlator.Correlate(message);
        Assert.Null(result.TicketId);
        Assert.Null(result.SubjectHintTicketId);
        Assert.False(result.NeedsReview);
        Assert.True(result.CrossDepartmentMatchIgnored);
    }

    [Fact]
    public void Same_message_id_in_two_departments_resolves_within_the_receiving_department()
    {
        // The customer put both mailboxes in To/Cc, so the same Message-ID is stored in IT and HR tickets.
        _correlator.AddTicket(new TicketInfo("t-hr-7", "dept-hr", Hr, "HR-7", ["alice@fabrikam.example"]));
        _correlator.Record("t-hr-7", Load("inbound-new-it.json", Hr));

        var reply = Load("inbound-reply-it.json", Hr) with { InReplyTo = "CAF1abc123@mail.fabrikam.example", References = [] };
        var result = _correlator.Correlate(reply);
        Assert.Equal("t-hr-7", result.TicketId);
        Assert.Equal(CorrelationMethod.InReplyTo, result.Method);
    }

    [Fact]
    public void Same_mailbox_is_preferred_when_a_department_has_the_message_in_several_mailboxes()
    {
        const string itSecond = "support2@contoso.example";
        var registry = new MailboxRegistry().Add(It, "dept-it").Add(itSecond, "dept-it");
        var correlator = new TicketCorrelator(registry);
        correlator.AddTicket(new TicketInfo("t-a", "dept-it", It, "IT-1", []));
        correlator.AddTicket(new TicketInfo("t-b", "dept-it", itSecond, "IT-2", []));
        correlator.Record("t-a", Load("inbound-new-it.json", It));
        correlator.Record("t-b", Load("inbound-new-it.json", itSecond));

        var reply = Load("inbound-reply-it.json", itSecond) with { InReplyTo = "CAF1abc123@mail.fabrikam.example", References = [] };
        var result = correlator.Correlate(reply);
        Assert.Equal("t-b", result.TicketId);
        Assert.False(result.MailboxMismatch);
    }

    [Fact]
    public void Conversation_id_in_a_different_mailbox_of_the_same_department_is_not_used()
    {
        const string itSecond = "support2@contoso.example";
        var registry = new MailboxRegistry().Add(It, "dept-it").Add(itSecond, "dept-it");
        var correlator = new TicketCorrelator(registry);
        correlator.AddTicket(new TicketInfo("t-a", "dept-it", It, "IT-1", []));
        correlator.Record("t-a", Load("inbound-new-it.json", It));

        var result = correlator.Correlate(Load("inbound-reply-no-headers-it.json", itSecond));
        Assert.Null(result.TicketId);
        Assert.False(result.CrossDepartmentMatchIgnored);
    }

    [Fact]
    public void Unknown_mailbox_is_rejected_department_is_never_taken_from_content()
    {
        Assert.Throws<InvalidOperationException>(() =>
            _correlator.Correlate(Load("inbound-reply-it.json", "stranger@contoso.example")));
    }

    [Theory]
    [InlineData("<abc@x.example>", "abc@x.example")]
    [InlineData("  <ABC@x.example>  ", "ABC@x.example")]
    [InlineData("abc@x.example", "abc@x.example")]
    [InlineData("<abc@x.example> (comment)", "abc@x.example")]
    [InlineData("", null)]
    [InlineData("<>", null)]
    public void Internet_message_ids_are_normalised(string raw, string? expected) =>
        Assert.Equal(expected, InternetMessageId.Normalize(raw));

    [Fact]
    public void References_header_is_parsed_oldest_first()
    {
        var ids = InternetMessageId.ParseList("<a@x.example>\r\n <b@x.example> <c@x.example>");
        Assert.Equal(new[] { "a@x.example", "b@x.example", "c@x.example" }, ids.ToArray());
    }
}
