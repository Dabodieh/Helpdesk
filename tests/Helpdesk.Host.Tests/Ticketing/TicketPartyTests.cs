#pragma warning disable CA1861 // one-off request payloads
using System.Net;
using System.Text.Json;
using Helpdesk.Host.Tests.Infrastructure;
using Helpdesk.Host.Tests.Mail;
using Helpdesk.Host.Tests.Portal;
using Helpdesk.Modules.Mail.Providers;

namespace Helpdesk.Host.Tests.Ticketing;

/// <summary>
/// Who is a party to a ticket (decisions D8 and D9): reply evidence never lets a stranger join a ticket, copied people are capped and removable, and a merge keeps the
/// requester of the merged ticket in the conversation. Simulator evidence (FakeMailProvider); nothing here proves Microsoft Graph behaviour (D-16).
/// </summary>
public sealed class TicketPartyTests(HelpdeskFixture fixture) : IClassFixture<HelpdeskFixture>
{
    private sealed record Start(Dept D, Mailbox Box, string Requester, string Subject, Guid TicketId, string InternetId, string Reference);

    private async Task<Start> StartAsync(string[]? cc = null, string? conversation = null)
    {
        var d = await fixture.NewDeptAsync();
        var box = await fixture.CreateMailboxAsync(d);
        var requester = MailKit.NewSender();
        var subject = $"Party {Unique.Id()}";
        var internetId = $"<{Guid.NewGuid():N}@client.test>";
        await fixture.ReceiveAsync(box, MailKit.Mail(requester, subject) with { InternetMessageId = internetId, Cc = cc, ConversationId = conversation });
        var ticket = Assert.Single(await d.Agent.TicketsAsync(d, subject));
        var id = ticket.GetProperty("id").GetGuid();
        return new Start(d, box, requester, subject, id, internetId, ticket.Str("reference"));
    }

    private static async Task<List<JsonElement>> ParticipantsAsync(Dept d, Guid ticketId) =>
        [.. (await d.Agent.GetTicketAsync(ticketId)).Expect(HttpStatusCode.OK).Json.GetProperty("participants").EnumerateArray()];

    private static string Person() => $"cc{Unique.Id()}@customer.test";

    // ----- inbound: strangers never join -----

    [Fact]
    public async Task A_stranger_with_a_valid_reply_header_gets_a_new_ticket_and_the_original_is_left_exactly_as_it_was()
    {
        var s = await StartAsync();
        var resolved = await s.D.Agent.GetTicketAsync(s.TicketId);
        (await s.D.Agent.PatchTicketAsync(resolved, new { status = "Resolved" })).Expect(HttpStatusCode.OK);
        var stranger = MailKit.NewSender();
        var copied = Person();
        var strangerSubject = $"Stranger {Unique.Id()}";

        await fixture.ReceiveAsync(s.Box, MailKit.Mail(stranger, strangerSubject, "I would like to add myself") with { InReplyTo = s.InternetId, Cc = [copied], To = [s.Box.Address, Person()] });

        // The original: same messages, still Resolved, nobody added, no requester row for the stranger's copied people.
        Assert.Single(await s.D.Agent.MessagesAsync(s.TicketId));
        Assert.Equal("Resolved", (await s.D.Agent.GetTicketAsync(s.TicketId)).Json.Str("status"));
        Assert.Empty(await ParticipantsAsync(s.D, s.TicketId));

        // A new ticket of the stranger exists; its internal note names the ticket it looked like a reply to; the stranger's text is not on the original.
        var created = Assert.Single(await s.D.Agent.TicketsAsync(s.D, strangerSubject));
        var newId = created.GetProperty("id").GetGuid();
        Assert.NotEqual(s.TicketId, newId);
        var messages = await s.D.Agent.MessagesAsync(newId);
        Assert.Contains(messages, m => m.Str("kind") == "Internal" && m.Str("body").Contains(s.Reference, StringComparison.Ordinal));
        Assert.Equal(1, await fixture.CountAsync("SELECT count(*) FROM mail.messages WHERE mailbox_id = @m AND subject_hint_reference = @r", TicketKit.P("m", s.Box.Id), TicketKit.P("r", s.Reference)));
    }

    [Fact]
    public async Task A_stranger_with_the_correlation_token_or_only_the_conversation_id_cannot_attach_either()
    {
        var conversation = $"conv-{Unique.Id()}";
        var s = await StartAsync(conversation: conversation);
        (await s.D.Agent.PostMessageAsync(s.TicketId, "Public", "We are on it")).Expect(HttpStatusCode.Created);
        await fixture.DispatchAsync();
        var sent = Assert.Single(fixture.Fake().SentMail(s.Box.Address));

        var byToken = $"ByToken {Unique.Id()}";
        await fixture.ReceiveAsync(s.Box, MailKit.Mail(MailKit.NewSender(), byToken, $"Quoted footer [hd:{sent.Token}]"));
        var byConversation = $"ByConversation {Unique.Id()}";
        await fixture.ReceiveAsync(s.Box, MailKit.Mail(MailKit.NewSender(), byConversation) with { ConversationId = conversation });

        Assert.Equal(2, (await s.D.Agent.MessagesAsync(s.TicketId)).Count); // the first email and our reply: nothing from either stranger
        Assert.Empty(await ParticipantsAsync(s.D, s.TicketId));
        Assert.Single(await s.D.Agent.TicketsAsync(s.D, byToken));
        Assert.Single(await s.D.Agent.TicketsAsync(s.D, byConversation));
    }

    [Fact]
    public async Task The_requester_and_an_existing_participant_still_attach_with_the_same_evidence()
    {
        var participant = Person();
        var s = await StartAsync(cc: [participant]);

        await fixture.ReceiveAsync(s.Box, MailKit.Mail(s.Requester, "Re: more", "From the requester") with { InReplyTo = s.InternetId });
        await fixture.ReceiveAsync(s.Box, MailKit.Mail(participant, "Re: from the colleague", "From the participant") with { InReplyTo = s.InternetId });

        Assert.Equal(3, (await s.D.Agent.MessagesAsync(s.TicketId)).Count);
        Assert.Single(await s.D.Agent.TicketsAsync(s.D, s.Subject)); // no second ticket
    }

    // ----- participants: cap, requester, removal -----

    [Fact]
    public async Task Copied_people_stop_at_twenty_the_requester_is_never_one_of_them_and_the_agents_are_told_in_an_internal_note()
    {
        var many = Enumerable.Range(0, 24).Select(_ => Person()).ToList();
        var d = await fixture.NewDeptAsync();
        var box = await fixture.CreateMailboxAsync(d);
        var requester = MailKit.NewSender();
        var subject = $"Cap {Unique.Id()}";
        var internetId = $"<{Guid.NewGuid():N}@client.test>";

        // The requester copies themselves and 24 colleagues on the first email.
        await fixture.ReceiveAsync(box, MailKit.Mail(requester, subject) with { InternetMessageId = internetId, Cc = [requester, .. many] });
        var ticketId = Assert.Single(await d.Agent.TicketsAsync(d, subject)).GetProperty("id").GetGuid();

        var participants = await ParticipantsAsync(d, ticketId);
        Assert.Equal(20, participants.Count);
        Assert.DoesNotContain(participants, p => string.Equals(p.Str("email"), requester, StringComparison.OrdinalIgnoreCase));
        var note = Assert.Single(await d.Agent.MessagesAsync(ticketId), m => m.Str("kind") == "Internal" && m.Str("body").Contains("limit of 20", StringComparison.Ordinal));
        Assert.Contains("4 address(es)", note.Str("body"), StringComparison.Ordinal);

        // The note never reaches the requester side: a reply is emailed to the requester and the 20 only.
        (await d.Agent.PostMessageAsync(ticketId, "Public", "Reply to everyone")).Expect(HttpStatusCode.Created);
        await fixture.DispatchAsync();
        Assert.Equal(20, Assert.Single(fixture.Fake().SentMail(box.Address)).Cc.Count);

        // A later email copying a 21st person adds nobody and notes it again.
        await fixture.ReceiveAsync(box, MailKit.Mail(requester, "Re: again", "More") with { InReplyTo = internetId, Cc = [Person()] });
        Assert.Equal(20, (await ParticipantsAsync(d, ticketId)).Count);
    }

    [Fact]
    public async Task An_agent_can_remove_an_ordinary_participant_from_future_replies_and_the_removal_is_final_and_guarded()
    {
        var keep = Person();
        var drop = Person();
        var s = await StartAsync(cc: [keep, drop]);
        var url = $"/api/tickets/{s.TicketId}/participants/remove";
        (await s.D.Agent.PostMessageAsync(s.TicketId, "Public", "Before removal")).Expect(HttpStatusCode.Created);
        await fixture.DispatchAsync();
        Assert.Equal(2, Assert.Single(fixture.Fake().SentMail(s.Box.Address)).Cc.Count);

        // Guards: the requester cannot be removed, an unknown address is not found, a viewer cannot, and another department's admin sees nothing.
        (await s.D.Agent.Client.PostAsync(url, new { email = s.Requester })).ExpectCode(HttpStatusCode.Conflict, "participant_protected");
        (await s.D.Agent.Client.PostAsync(url, new { email = Person() })).Expect(HttpStatusCode.NotFound);
        (await s.D.Viewer.Client.PostAsync(url, new { email = drop })).Expect(HttpStatusCode.Forbidden);
        var other = await fixture.NewDeptAsync();
        (await other.Admin.Client.PostAsync(url, new { email = drop })).Expect(HttpStatusCode.NotFound);
        Assert.Equal(2, (await ParticipantsAsync(s.D, s.TicketId)).Count);

        var left = (await s.D.Agent.Client.PostAsync(url, new { email = drop })).Expect(HttpStatusCode.OK).Json.EnumerateArray().ToList();
        Assert.Equal(keep, Assert.Single(left).Str("email"));
        Assert.Equal(1, await fixture.CountAuditAsync("ticket.participant_removed", s.TicketId));

        // Future replies leave out the removed person; the reply sent before still shows both (history untouched).
        (await s.D.Agent.PostMessageAsync(s.TicketId, "Public", "After removal")).Expect(HttpStatusCode.Created);
        await fixture.DispatchAsync();
        var sent = fixture.Fake().SentMail(s.Box.Address);
        Assert.Equal(2, sent.Count);
        Assert.Equal(2, sent[0].Cc.Count);
        Assert.Equal(keep, Assert.Single(sent[1].Cc).Address);

        // Final for this ticket: the requester copying the same person again does not bring them back, and they are no longer a party who could attach by email.
        await fixture.ReceiveAsync(s.Box, MailKit.Mail(s.Requester, "Re: copy again", "Adding them back") with { InReplyTo = s.InternetId, Cc = [drop] });
        Assert.DoesNotContain(await ParticipantsAsync(s.D, s.TicketId), p => p.Str("email") == drop);
        var removedSubject = $"Removed person {Unique.Id()}";
        await fixture.ReceiveAsync(s.Box, MailKit.Mail(drop, removedSubject) with { InReplyTo = s.InternetId });
        Assert.Single(await s.D.Agent.TicketsAsync(s.D, removedSubject)); // a new ticket, not an attachment
    }

    // ----- merge across requesters -----

    private async Task<(Dept D, Mailbox Box, ApiResponse Canonical, ApiResponse Source, PortalKit.Employee Keeper, PortalKit.Employee Owner)> MergedAsync()
    {
        var d = await fixture.NewDeptAsync();
        var box = await fixture.CreateMailboxAsync(d);
        var owner = await fixture.EmployeeAsync("owner");
        var keeper = await fixture.EmployeeAsync("keeper");
        var canonical = await d.TicketForAsync(owner.Email, $"Canonical {Unique.Id()}");
        var source = await d.TicketForAsync(keeper.Email, $"Source {Unique.Id()}");
        var merged = (await d.Agent.Client.PostAsync(
            $"/api/tickets/{canonical.Reference()}/merge",
            new { version = canonical.Version, sources = new[] { new { ticketId = source.Id, version = source.Version } } })).Expect(HttpStatusCode.OK);
        _ = merged;
        return (d, box, canonical, source, keeper, owner);
    }

    [Fact]
    public async Task Merging_across_requesters_keeps_the_source_requester_as_a_protected_participant_who_can_see_the_canonical_request_and_is_copied_on_replies()
    {
        var m = await MergedAsync();

        var participants = await ParticipantsAsync(m.D, m.Canonical.Id);
        var kept = Assert.Single(participants);
        Assert.Equal(m.Keeper.Email, kept.Str("email"));
        Assert.True(kept.GetProperty("isProtected").GetBoolean());
        (await m.D.Agent.Client.PostAsync($"/api/tickets/{m.Canonical.Id}/participants/remove", new { email = m.Keeper.Email })).ExpectCode(HttpStatusCode.Conflict, "participant_protected");

        // Portal: the kept requester now lists the canonical request, reads it, but cannot reply to it; the source request points them there.
        var listed = (await m.Keeper.PortalListAsync()).GetProperty("items").EnumerateArray().Select(i => i.Str("reference")).ToList();
        Assert.Contains(m.Canonical.Reference(), listed);
        var view = (await m.Keeper.User.Client.GetAsync($"/api/portal/requests/{m.Canonical.Reference()}")).Expect(HttpStatusCode.OK).Json;
        Assert.False(view.GetProperty("canReply").GetBoolean());
        Assert.Empty(view.GetProperty("editableFields").EnumerateArray());
        (await m.Keeper.User.Client.PostAsync($"/api/portal/requests/{m.Canonical.Reference()}/replies", new { body = "Can I answer?" })).Expect(HttpStatusCode.NotFound);
        var sourceView = (await m.Keeper.User.Client.GetAsync($"/api/portal/requests/{m.Source.Reference()}")).Expect(HttpStatusCode.OK).Json;
        Assert.Equal(m.Canonical.Reference(), sourceView.GetProperty("mergedInto").GetString());

        // The primary requester still replies from the portal; a stranger employee sees none of it.
        Assert.True((await m.Owner.User.Client.GetAsync($"/api/portal/requests/{m.Canonical.Reference()}")).Expect(HttpStatusCode.OK).Json.GetProperty("canReply").GetBoolean());
        var stranger = await fixture.EmployeeAsync("stranger");
        (await stranger.User.Client.GetAsync($"/api/portal/requests/{m.Canonical.Reference()}")).Expect(HttpStatusCode.NotFound);

        // Future public replies go to the primary requester and copy the kept requester.
        (await m.D.Agent.PostMessageAsync(m.Canonical.Id, "Public", "Answer for both")).Expect(HttpStatusCode.Created);
        await fixture.DispatchAsync();
        var sent = Assert.Single(fixture.Fake().SentMail(m.Box.Address));
        Assert.Equal(m.Keeper.Email, Assert.Single(sent.Cc).Address);

        // And the kept requester can still answer by email, because they are a participant.
        var internetId = $"<{Guid.NewGuid():N}@client.test>";
        await fixture.ReceiveAsync(m.Box, MailKit.Mail(m.Keeper.Email, "Re: canonical", "Thanks") with { InReplyTo = sent.InternetMessageId, InternetMessageId = internetId });
        Assert.Equal(4, (await m.D.Agent.MessagesAsync(m.Canonical.Id)).Count); // both original requests (the conversation is the union), our answer and theirs
        Assert.Empty(await m.D.Agent.TicketsAsync(m.D, "Re: canonical"));
    }

    [Fact]
    public async Task Merged_requesters_do_not_count_against_the_cap_and_merging_tickets_of_the_same_requester_changes_nothing()
    {
        var d = await fixture.NewDeptAsync();
        var box = await fixture.CreateMailboxAsync(d);
        var requester = $"same-{Unique.Id()}@example.test";
        var first = await d.TicketForAsync(requester, $"One {Unique.Id()}");
        var second = await d.TicketForAsync(requester, $"Two {Unique.Id()}");
        (await d.Agent.Client.PostAsync($"/api/tickets/{first.Reference()}/merge", new { version = first.Version, sources = new[] { new { ticketId = second.Id, version = second.Version } } }))
            .Expect(HttpStatusCode.OK);
        Assert.Empty(await ParticipantsAsync(d, first.Id)); // same requester: nobody added

        // 20 ordinary participants on a ticket, then a different requester's ticket is merged into it: the 21st person is the protected one, the cap is unaffected.
        var subject = $"Full {Unique.Id()}";
        var owner = MailKit.NewSender();
        var internetId = $"<{Guid.NewGuid():N}@client.test>";
        await fixture.ReceiveAsync(box, MailKit.Mail(owner, subject) with { InternetMessageId = internetId, Cc = Enumerable.Range(0, 20).Select(_ => Person()).ToList() });
        var full = Assert.Single(await d.Agent.TicketsAsync(d, subject));
        var fullId = full.GetProperty("id").GetGuid();
        var other = await d.TicketForAsync($"other-{Unique.Id()}@example.test", $"Other {Unique.Id()}");
        var fullDetail = await d.Agent.GetTicketAsync(fullId);
        (await d.Agent.Client.PostAsync($"/api/tickets/{full.Str("reference")}/merge", new { version = fullDetail.Version, sources = new[] { new { ticketId = other.Id, version = other.Version } } }))
            .Expect(HttpStatusCode.OK);

        var participants = await ParticipantsAsync(d, fullId);
        Assert.Equal(21, participants.Count);
        Assert.Equal(1, participants.Count(p => p.GetProperty("isProtected").GetBoolean()));

        await fixture.ReceiveAsync(box, MailKit.Mail(owner, "Re: more people", "Adding someone") with { InReplyTo = internetId, Cc = [Person()] });
        Assert.Equal(21, (await ParticipantsAsync(d, fullId)).Count); // still 20 ordinary + 1 protected
    }
}
