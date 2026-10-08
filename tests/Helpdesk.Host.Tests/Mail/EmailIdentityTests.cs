#pragma warning disable CA1861 // one-off request payloads
using System.Net;
using System.Text.Json.Nodes;
using Helpdesk.Host.Tests.Infrastructure;
using Helpdesk.Host.Tests.Portal;
using Helpdesk.Host.Tests.Ticketing;
using Microsoft.Extensions.DependencyInjection;

namespace Helpdesk.Host.Tests.Mail;

/// <summary>
/// Email identity: the ticket's mailbox is the only sender (and Reply-To), the sending agent's own per-department signature is the only footer, and each department
/// decides its own acknowledgement of new tickets.
/// </summary>
public sealed class EmailIdentityTests(HelpdeskFixture fixture) : IClassFixture<HelpdeskFixture>
{
    private static string Normalised(ApiResponse r)
    {
        try
        {
            if (JsonNode.Parse(r.Body) is JsonObject o)
            {
                o.Remove("traceId");
                return o.ToJsonString();
            }
        }
        catch (System.Text.Json.JsonException)
        {
        }

        return r.Body;
    }

    private static async Task SameAsRandomAsync(string what, Func<Guid, Task<ApiResponse>> call, Guid real)
    {
        var foreign = await call(real);
        var missing = await call(Guid.NewGuid());
        Assert.False(foreign.Status is >= HttpStatusCode.OK and < HttpStatusCode.MultipleChoices, $"{what}: succeeded ({foreign.Status})");
        Assert.True(foreign.Status == missing.Status && Normalised(foreign) == Normalised(missing), $"{what}: {foreign.Status} vs {missing.Status}");
    }

    private async Task<(Dept D, Mailbox Box, Guid TicketId, string Requester)> StartAsync(Mailbox? box = null, Dept? dept = null, string name = "Jane Smith")
    {
        var d = dept ?? box?.Dept ?? await fixture.NewDeptAsync();
        box ??= await fixture.CreateMailboxAsync(d);
        var requester = MailKit.NewSender();
        var subject = $"Identity {Unique.Id()}";
        await fixture.ReceiveAsync(box, MailKit.Mail(requester, subject, fromName: name));
        var ticket = Assert.Single(await d.Agent.TicketsAsync(d, subject));
        return (d, box, ticket.GetProperty("id").GetGuid(), requester);
    }

    private static Task<ApiResponse> SetSignatureAsync(TestUser user, Dept d, string html) =>
        user.Client.PutAsync($"{d.Department.Url}/signature/me", new { html });

    private static Task<ApiResponse> SetAutoReplyAsync(Dept d, object body) => d.Admin.Client.PutAsync($"{d.Department.Url}/auto-reply", body);

    private async Task<long> OutboundCountAsync(Dept d, string? flag = null) =>
        await fixture.CountAsync("SELECT count(*) FROM mail.messages WHERE department_id = @d AND direction = 'Outbound' AND (@f::text IS NULL OR @f = ANY(string_to_array(flags, ',')))", TicketKit.P("d", d.Id), new Npgsql.NpgsqlParameter("f", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)flag ?? DBNull.Value });

    // ----- sender identity -----

    [Fact]
    public async Task One_mailbox_sends_only_a_capped_number_of_acknowledgements_per_window_but_still_creates_every_ticket()
    {
        var d = await fixture.NewDeptAsync();
        var box = await fixture.CreateMailboxAsync(d);
        (await SetAutoReplyAsync(d, new { enabled = true, forEmail = true, forPortal = false, subject = "Got it {{ticket.reference}}", body = "Hi {{requester.name}}" })).Expect(HttpStatusCode.OK);
        var options = fixture.Factory.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<Helpdesk.Modules.Mail.Services.MailOptions>>().Value;
        var original = options.AcknowledgementLimitPerWindow;
        options.AcknowledgementLimitPerWindow = 2;
        try
        {
            // Four different (possibly forged) senders: the per-sender limit never trips, the per-mailbox cap does.
            var marker = Unique.Id();
            for (var i = 0; i < 4; i++)
            {
                await fixture.ReceiveAsync(box, MailKit.Mail(MailKit.NewSender(), $"Flood {marker} {i}"));
            }

            await fixture.DispatchAsync();
            Assert.Equal(4, (await d.Agent.TicketsAsync(d, $"Flood {marker}")).Count);
            Assert.Equal(2, fixture.Fake().SentMail(box.Address).Count);
            Assert.Equal(2, await fixture.CountAsync("SELECT count(*) FROM audit.audit_events WHERE action = 'ticket.auto_reply_failed' AND department_id = @d", TicketKit.P("d", d.Department.Id)));
        }
        finally
        {
            options.AcknowledgementLimitPerWindow = original;
        }
    }

    [Fact]
    public async Task Every_reply_leaves_from_the_ticket_mailbox_with_that_mailbox_as_reply_to_whoever_sends_it_and_whoever_is_assigned_to_the_ticket()
    {
        var d = await fixture.NewDeptAsync();
        var boxA = await fixture.CreateMailboxAsync(d, isDefault: true);
        var boxB = await fixture.CreateMailboxAsync(d);
        var s = await StartAsync(boxB, d); // the ticket arrives at the NON-default mailbox

        // A forged sender in the request is not even a field; the agent's own address never becomes the sender.
        (await d.Agent.Client.PostAsync(TicketKit.Url(s.TicketId, "/messages"), new { kind = "Public", body = "First reply", from = "agent.personal@example.test", mailboxId = boxA.Id })).Expect(HttpStatusCode.Created);
        var current = (await d.Lead.GetTicketAsync(s.TicketId)).Expect(HttpStatusCode.OK);
        (await d.Lead.AssignAsync(s.TicketId, current.Version, d.Agent.Id)).Expect(HttpStatusCode.OK);
        (await d.Lead.PostMessageAsync(s.TicketId, "Public", "Second reply after the assignee changed")).Expect(HttpStatusCode.Created);
        await fixture.DispatchAsync();

        Assert.Empty(fixture.Fake().SentMail(boxA.Address));
        var sent = fixture.Fake().SentMail(boxB.Address);
        Assert.Equal(2, sent.Count);
        Assert.All(sent, m =>
        {
            Assert.Equal(boxB.Address, m.Mailbox);
            Assert.Equal(boxB.Address, m.ReplyTo?.Address);
            Assert.DoesNotContain("agent.personal@example.test", m.BodyHtml, StringComparison.Ordinal);
        });
        Assert.Equal(boxB.Address, (await d.Agent.GetTicketAsync(s.TicketId)).Expect(HttpStatusCode.OK).Json.GetProperty("mailbox").Str("address"));
    }

    [Fact]
    public async Task A_ticket_created_by_hand_stores_a_valid_mailbox_of_its_department_and_a_foreign_one_is_refused()
    {
        var it = await fixture.NewDeptAsync();
        var hr = await fixture.NewDeptAsync();
        var itBox = await fixture.CreateMailboxAsync(it);
        var itBox2 = await fixture.CreateMailboxAsync(it);
        var hrBox = await fixture.CreateMailboxAsync(hr);

        // Default: with no choice the department's default mailbox is stored (two mailboxes, one default).
        (await it.Admin.Client.PatchAsync($"{it.Department.Url}/mailboxes/{itBox2.Id}", new { version = (await it.Admin.Client.GetAsync($"{it.Department.Url}/mailboxes/{itBox2.Id}")).Version, isDefault = true })).Expect(HttpStatusCode.OK);
        var defaulted = (await it.Agent.CreateTicketAsync(it.Department, TicketHelpers.NewTicketBody())).Json;
        Assert.Equal(itBox2.Id, defaulted.GetProperty("mailbox").GetProperty("id").GetGuid());

        // An explicit choice of an active mailbox of the department is stored; the choices are listed for the agent.
        var chosen = (await it.Agent.CreateTicketAsync(it.Department, TicketHelpers.NewTicketBody(extra: new { MailboxId = itBox.Id }))).Json;
        Assert.Equal(itBox.Id, chosen.GetProperty("mailbox").GetProperty("id").GetGuid());
        var choices = (await it.Agent.Client.GetAsync($"{it.Department.Url}/outbound-mailboxes")).Expect(HttpStatusCode.OK).Json;
        Assert.Equal(2, choices.GetArrayLength());
        Assert.DoesNotContain(hrBox.Address, choices.GetRawText(), StringComparison.Ordinal);

        // Another department's mailbox answers like one that does not exist, for ticket creation and for the choice list.
        await SameAsRandomAsync("create with foreign mailbox", id => it.Agent.Client.PostAsync($"{it.Department.Url}/tickets", TicketHelpers.NewTicketBody(extra: new { MailboxId = id })), hrBox.Id);
        await SameAsRandomAsync("foreign mailbox list", id => it.Agent.Client.GetAsync($"/api/departments/{id}/outbound-mailboxes"), hr.Id);

        // The stored mailbox is what replies use.
        var ticketId = chosen.GetProperty("id").GetGuid();
        (await it.Agent.PostMessageAsync(ticketId, "Public", "Hello from a hand-made ticket")).Expect(HttpStatusCode.Created);
        await fixture.DispatchAsync();
        Assert.Single(fixture.Fake().SentMail(itBox.Address));
        Assert.Empty(fixture.Fake().SentMail(itBox2.Address));
    }

    [Fact]
    public async Task The_sender_display_name_of_inbound_mail_is_kept_and_a_blank_name_never_replaces_a_real_one()
    {
        var d = await fixture.NewDeptAsync();
        var box = await fixture.CreateMailboxAsync(d);
        var from = MailKit.NewSender();
        await fixture.ReceiveAsync(box, MailKit.Mail(from, $"Names {Unique.Id()} one", fromName: "Jane Smith"));
        var first = Assert.Single(await d.Agent.TicketsAsync(d, "one"));
        var requester = first.GetProperty("requester");
        Assert.Equal("Jane Smith", requester.Str("displayName"));
        Assert.Equal(from, requester.Str("email"));

        await fixture.ReceiveAsync(box, MailKit.Mail(from, $"Names {Unique.Id()} two", fromName: "Jane A. Smith"));
        await fixture.ReceiveAsync(box, MailKit.Mail(from, $"Names {Unique.Id()} three", fromName: ""));
        var all = await d.Agent.TicketsAsync(d, "Names");
        Assert.All(all, t => Assert.Equal("Jane A. Smith", t.GetProperty("requester").Str("displayName"))); // newest real name wins; the empty one changed nothing
    }

    // ----- signatures -----

    [Fact]
    public async Task A_signature_is_per_agent_and_per_department_sanitised_validated_and_never_editable_by_somebody_else()
    {
        var it = await fixture.NewDeptAsync();
        var hr = await fixture.NewDeptAsync();

        var saved = (await SetSignatureAsync(it.Agent, it, "<p><b>{{agent.name}}</b><script>alert(1)</script></p><p onclick=\"x()\"><a href=\"javascript:alert(1)\">bad</a> <a href=\"https://example.test/p\">site</a></p>")).Expect(HttpStatusCode.OK).Json;
        var html = saved.Str("html");
        Assert.Contains("<b>{{agent.name}}</b>", html, StringComparison.Ordinal);
        Assert.Contains("https://example.test/p", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onclick", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("javascript:", html, StringComparison.OrdinalIgnoreCase);

        // Unknown placeholders are refused on save; the preview shows exactly what would be appended, resolved for this department.
        (await SetSignatureAsync(it.Agent, it, "{{agent.password}}")).Expect(HttpStatusCode.BadRequest);
        (await SetSignatureAsync(it.Agent, it, "{{ticket.subject}}")).Expect(HttpStatusCode.BadRequest);
        var preview = (await it.Agent.Client.PostAsync($"{it.Department.Url}/signature/me/preview", new { html = "<i>{{department.name}}</i> {{agent.name}}" })).Expect(HttpStatusCode.OK).Json.Str("html");
        Assert.Contains(it.Department.Name, preview, StringComparison.Ordinal);
        Assert.DoesNotContain("{{", preview, StringComparison.Ordinal);

        // Mine only: another agent of the same department has none; a read-only viewer cannot use signatures; HR cannot touch IT's.
        Assert.Equal(JsonValueNull, (await it.Lead.Client.GetAsync($"{it.Department.Url}/signature/me")).Expect(HttpStatusCode.OK).Json.GetProperty("html").ValueKind);
        (await it.Viewer.Client.GetAsync($"{it.Department.Url}/signature/me")).Expect(HttpStatusCode.Forbidden);
        await SameAsRandomAsync("foreign get", id => hr.Agent.Client.GetAsync($"/api/departments/{id}/signature/me"), it.Id);
        await SameAsRandomAsync("foreign put", id => hr.Agent.Client.PutAsync($"/api/departments/{id}/signature/me", new { html = "<p>x</p>" }), it.Id);
        await SameAsRandomAsync("foreign preview", id => hr.Agent.Client.PostAsync($"/api/departments/{id}/signature/me/preview", new { html = "<p>x</p>" }), it.Id);
        Assert.Equal(JsonValueNull, (await hr.Agent.Client.GetAsync($"{hr.Department.Url}/signature/me")).Expect(HttpStatusCode.OK).Json.GetProperty("html").ValueKind);

        // Clearing removes it; audit records the kind of change, not the content.
        (await SetSignatureAsync(it.Agent, it, "<p>Anton secret title</p>")).Expect(HttpStatusCode.OK);
        var audit = await fixture.AuditOfAsync(it.Agent.Id);
        Assert.Contains(audit, a => a.Action == "agent_signature.changed");
        Assert.DoesNotContain(audit, a => (a.Next ?? "").Contains("secret title", StringComparison.Ordinal));
        (await SetSignatureAsync(it.Agent, it, "  ")).Expect(HttpStatusCode.OK);
        Assert.Equal(0, await fixture.CountAsync("SELECT count(*) FROM ticketing.agent_signatures WHERE user_id = @u", TicketKit.P("u", it.Agent.Id)));
    }

    private const System.Text.Json.JsonValueKind JsonValueNull = System.Text.Json.JsonValueKind.Null;

    [Fact]
    public async Task The_sending_agents_signature_is_appended_once_to_public_replies_only_and_the_sent_copy_never_changes()
    {
        var d = await fixture.NewDeptAsync();
        var other = await fixture.NewDeptAsync();
        var s = await StartAsync(dept: d);
        await SetSignatureAsync(d.Agent, d, "<p>Anton Bodell<br>IT Support Engineer<br>{{mailbox.email}}</p>");
        await SetSignatureAsync(d.Lead, d, "<p>Lena Lead, Team Lead</p>");

        // Alice (agent) prepares, Bob (lead) sends: the sender's signature is used, resolved at send time. Notes get none; no Department footer exists.
        var canned = (await d.Admin.Client.PostAsync("/api/canned-responses", new { scope = "Department", departmentId = d.Id, title = "Done", body = "Your request is done." })).Expect(HttpStatusCode.Created);
        var macro = (await d.Admin.Client.PostAsync($"{d.Department.Url}/macros", new { name = "Done", actions = new[] { new { type = "publicReply", value = canned.Id.ToString() } } })).Expect(HttpStatusCode.Created);
        var run = (await d.Agent.Client.PostAsync(TicketKit.Url(s.TicketId, $"/macros/{macro.Id}/run"), new { version = (await d.Agent.GetTicketAsync(s.TicketId)).Version })).Expect(HttpStatusCode.OK).Json;
        Assert.Equal("Your request is done.", run.GetProperty("draft").Str("body")); // the draft carries no signature
        (await d.Agent.PostMessageAsync(s.TicketId, "Internal", "private note")).Expect(HttpStatusCode.Created);
        (await d.Lead.PostMessageAsync(s.TicketId, "Public", "Your request is done.")).Expect(HttpStatusCode.Created);
        (await d.Agent.PostMessageAsync(s.TicketId, "Public", "Anything else?")).Expect(HttpStatusCode.Created);
        await fixture.DispatchAsync();

        var sent = fixture.Fake().SentMail(s.Box.Address);
        Assert.Equal(2, sent.Count); // the note was not emailed
        var leadMail = sent.Single(m => m.BodyHtml.Contains("Your request is done.", StringComparison.Ordinal));
        var agentMail = sent.Single(m => m.BodyHtml.Contains("Anything else?", StringComparison.Ordinal));
        Assert.Contains("Lena Lead, Team Lead", leadMail.BodyHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("Anton Bodell", leadMail.BodyHtml, StringComparison.Ordinal);
        Assert.Equal(1, CountOf(agentMail.BodyHtml, "Anton Bodell"));
        Assert.Contains(s.Box.Address, agentMail.BodyHtml, StringComparison.Ordinal); // {{mailbox.email}} resolved
        Assert.DoesNotContain("{{", agentMail.BodyHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("private note", string.Join(' ', sent.Select(m => m.BodyHtml)), StringComparison.Ordinal);

        // Editing the signature afterwards does not touch what was sent (stored with the outbound message).
        await SetSignatureAsync(d.Agent, d, "<p>Changed later</p>");
        var stored = await fixture.ScalarAsync<string>("SELECT body_html_original FROM mail.messages WHERE ticket_id = @t AND direction = 'Outbound' AND body_text LIKE '%Anything else?%'", TicketKit.P("t", s.TicketId));
        Assert.Contains("Anton Bodell", stored, StringComparison.Ordinal);
        Assert.DoesNotContain("Changed later", stored, StringComparison.Ordinal);

        // No signature for this department, or a signature only for another department: the reply goes out without any.
        var third = await fixture.NewMemberAsync(d.Department, "Agent");
        await fixture.AddMemberAsync(other.Department, third, "Agent");
        await SetSignatureAsync(third, other, "<p>Other department only</p>");
        (await third.PostMessageAsync(s.TicketId, "Public", "From the agent with no IT signature")).Expect(HttpStatusCode.Created);
        await fixture.DispatchAsync();
        var plain = fixture.Fake().SentMail(s.Box.Address).Single(m => m.BodyHtml.Contains("no IT signature", StringComparison.Ordinal));
        Assert.DoesNotContain("Other department only", plain.BodyHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("-- ", plain.BodyHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("Anton", plain.BodyHtml, StringComparison.Ordinal);

        // Losing the membership removes the signature with it.
        var admin = await fixture.PlatformAdminAsync();
        (await admin.Client.DeleteAsync($"{other.Department.Url}/members/{third.Id}")).Expect(HttpStatusCode.NoContent);
        Assert.Equal(0, await fixture.CountAsync("SELECT count(*) FROM ticketing.agent_signatures WHERE user_id = @u", TicketKit.P("u", third.Id)));
    }

    private static int CountOf(string text, string needle)
    {
        var count = 0;
        for (var i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    // ----- auto-replies -----

    [Fact]
    public async Task Each_department_has_its_own_acknowledgement_disabled_until_configured_sent_from_the_ticket_mailbox_with_no_agent_signature()
    {
        var it = await fixture.NewDeptAsync();
        var hr = await fixture.NewDeptAsync();
        var itBox = await fixture.CreateMailboxAsync(it, isDefault: true);
        var itBox2 = await fixture.CreateMailboxAsync(it);
        var hrBox = await fixture.CreateMailboxAsync(hr);
        await SetSignatureAsync(it.Agent, it, "<p>Agent sign-off must not appear</p>");

        // Nothing is configured: nothing is sent, and the defaults are visible but off.
        var defaults = (await it.Admin.Client.GetAsync($"{it.Department.Url}/auto-reply")).Expect(HttpStatusCode.OK).Json;
        Assert.False(defaults.GetProperty("enabled").GetBoolean());
        Assert.False(defaults.GetProperty("configured").GetBoolean());
        await StartAsync(itBox, it);
        await fixture.DispatchAsync();
        Assert.Equal(0, await OutboundCountAsync(it, "auto_reply"));

        // Validation and preview; unknown placeholders are refused.
        (await SetAutoReplyAsync(it, new { enabled = true, subject = "x {{agent.name}}", body = "y" })).Expect(HttpStatusCode.BadRequest);
        (await SetAutoReplyAsync(it, new { enabled = true, subject = "", body = "y" })).Expect(HttpStatusCode.BadRequest);
        var preview = (await it.Admin.Client.PostAsync($"{it.Department.Url}/auto-reply/preview", new { subject = "Got it — {{ticket.reference}}", body = "Hi {{requester.name}}" })).Expect(HttpStatusCode.OK).Json;
        Assert.Equal($"Got it — {it.Key}-1042", preview.Str("subject"));
        Assert.Equal("Hi Jane Smith", preview.Str("body"));

        // IT and HR configure different texts; HR keeps email acknowledgements off.
        var saved = (await SetAutoReplyAsync(it, new { enabled = true, forEmail = true, forPortal = false, subject = "We've received your request — {{ticket.reference}}", body = "Hi {{requester.name}},\n\nThanks for contacting IT Support.\n\nYour request has been logged as {{ticket.reference}}." })).Expect(HttpStatusCode.OK);
        Assert.True(saved.Json.GetProperty("configured").GetBoolean());
        (await SetAutoReplyAsync(hr, new { enabled = true, forEmail = false, forPortal = true, subject = "HR here — {{ticket.reference}}", body = "Hello {{requester.name}}, HR will reply." })).Expect(HttpStatusCode.OK);

        // An email to the SECOND IT mailbox: acknowledged once, from that mailbox, using IT's template, no agent signature.
        var s = await StartAsync(itBox2, it);
        await fixture.DispatchAsync();
        var reference = (await it.Agent.GetTicketAsync(s.TicketId)).Json.Str("reference");
        var ack = Assert.Single(fixture.Fake().SentMail(itBox2.Address));
        Assert.Empty(fixture.Fake().SentMail(itBox.Address));
        Assert.Equal($"We've received your request — {reference}", ack.Subject);
        Assert.Equal(s.Requester, Assert.Single(ack.To).Address);
        Assert.Equal(itBox2.Address, ack.ReplyTo?.Address);
        Assert.True(ack.AutoResponseSuppress); // marked automatic so a peer auto-responder does not answer it (MSG-006)
        Assert.Contains("Thanks for contacting IT Support.", ack.BodyHtml, StringComparison.Ordinal);
        Assert.Contains($"Hi Jane Smith,", ack.BodyHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("Agent sign-off", ack.BodyHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("HR", ack.BodyHtml, StringComparison.Ordinal);

        // HR: email is off, so an email ticket gets none; its text never leaks into IT's.
        await StartAsync(hrBox, hr);
        await fixture.DispatchAsync();
        Assert.Empty(fixture.Fake().SentMail(hrBox.Address));
        Assert.Equal(1, await OutboundCountAsync(it, "auto_reply"));
    }

    [Fact]
    public async Task A_replayed_or_automated_inbound_message_never_produces_a_second_or_a_wrong_acknowledgement()
    {
        var d = await fixture.NewDeptAsync();
        var box = await fixture.CreateMailboxAsync(d);
        (await SetAutoReplyAsync(d, new { enabled = true, forEmail = true, subject = "Ack {{ticket.reference}}", body = "Thanks {{requester.name}}" })).Expect(HttpStatusCode.OK);
        var requester = MailKit.NewSender();
        var subject = $"Replay {Unique.Id()}";

        fixture.Fake().Inject(MailKit.Mail(requester, subject, fromName: "Jane Smith") with { Mailbox = box.Address });
        await fixture.SyncAsync(box);
        await fixture.SyncAsync(box); // the same delta delivered again
        await fixture.ExecuteAsync("UPDATE mail.messages SET state = 'Received', ticket_id = NULL, ticket_message_id = NULL WHERE department_id = @d AND direction = 'Inbound'", TicketKit.P("d", d.Id));
        await fixture.WithScopeAsync(sp => Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<Helpdesk.Modules.Mail.Contracts.IMailJobRunner>(sp).RunDispatchAsync()); // reprocessing the same stored message
        await fixture.DispatchAsync();

        Assert.Single(await d.Agent.TicketsAsync(d, subject));
        Assert.Equal(1, await OutboundCountAsync(d, "auto_reply"));
        Assert.Single(fixture.Fake().SentMail(box.Address));

        // Automatic mail (Auto-Submitted) is ignored by the existing guard: no ticket, so no acknowledgement.
        var robot = $"Robot {Unique.Id()}";
        fixture.Fake().Inject(MailKit.Mail(MailKit.NewSender(), robot) with { Mailbox = box.Address, Headers = new Dictionary<string, string> { ["Auto-Submitted"] = "auto-replied" } });
        await fixture.SyncAsync(box);
        await fixture.DispatchAsync();
        Assert.Empty(await d.Agent.TicketsAsync(d, robot));
        Assert.Equal(1, await OutboundCountAsync(d, "auto_reply"));
    }

    [Fact]
    public async Task Portal_tickets_are_acknowledged_only_when_the_department_enables_it_from_the_ticket_mailbox_and_a_department_without_a_mailbox_takes_no_portal_requests()
    {
        var withBox = await fixture.NewDeptAsync();
        var box = await fixture.CreateMailboxAsync(withBox);
        var noBox = await fixture.NewDeptAsync();
        var emp = await fixture.EmployeeAsync("pat");
        var formA = (await withBox.FormAsync(fixture, "Portal form A")).FormId;
        var formB = (await noBox.Admin.Client.PostAsync($"{noBox.Department.Url}/request-forms", new { name = "Portal form B", audience = "AllEmployees" })).Expect(HttpStatusCode.Created).Id; // no mailbox registered

        async Task<ApiResponse> SubmitAsync(Guid formId) =>
            await emp.User.Client.PostAsync("/api/portal/requests", new { formId, subject = $"Portal {Unique.Id()}", description = "Please help", fields = new Dictionary<string, object>(), submissionId = Guid.NewGuid() });

        // Disabled (and email-only) means none: portal creation itself is unaffected.
        (await SubmitAsync(formA)).Expect(HttpStatusCode.Created);
        (await SetAutoReplyAsync(withBox, new { enabled = true, forEmail = true, forPortal = false, subject = "Ack {{ticket.reference}}", body = "Hello {{requester.name}}" })).Expect(HttpStatusCode.OK);
        (await SubmitAsync(formA)).Expect(HttpStatusCode.Created);
        await fixture.DispatchAsync();
        Assert.Empty(fixture.Fake().SentMail(box.Address));

        var current = (await withBox.Admin.Client.GetAsync($"{withBox.Department.Url}/auto-reply")).Json;
        (await SetAutoReplyAsync(withBox, new { forPortal = true, version = current.Str("version") })).Expect(HttpStatusCode.OK);
        var created = (await SubmitAsync(formA)).Expect(HttpStatusCode.Created).Json.Str("reference");
        await fixture.DispatchAsync();
        var ack = Assert.Single(fixture.Fake().SentMail(box.Address));
        Assert.Equal($"Ack {created}", ack.Subject);
        Assert.Equal(emp.Email, Assert.Single(ack.To).Address);
        Assert.Equal(box.Address, ack.ReplyTo?.Address);
        var ticketId = await fixture.ScalarAsync<Guid>("SELECT id FROM ticketing.tickets WHERE reference = @r", TicketKit.P("r", created));
        Assert.Equal(box.Id, await fixture.ScalarAsync<Guid>("SELECT mailbox_id FROM ticketing.tickets WHERE id = @t", TicketKit.P("t", ticketId))); // the department mailbox is now the ticket's

        // D22-A: a department without a usable default mailbox takes no portal requests at all (no mailbox-less ticket is created), and the form is not offered.
        (await SetAutoReplyAsync(noBox, new { enabled = true, forPortal = true, subject = "Ack {{ticket.reference}}", body = "Hello" })).Expect(HttpStatusCode.OK);
        (await SubmitAsync(formB)).Expect(HttpStatusCode.NotFound);
        Assert.DoesNotContain(formB.ToString(), (await emp.User.Client.GetAsync("/api/portal/forms")).Expect(HttpStatusCode.OK).Body, StringComparison.OrdinalIgnoreCase);
        (await emp.User.Client.GetAsync($"/api/portal/forms/{formB}")).Expect(HttpStatusCode.NotFound);
        Assert.Equal(0, await OutboundCountAsync(noBox));
    }

    [Fact]
    public async Task Auto_reply_settings_belong_to_their_department_and_changes_are_audited_without_copying_the_text()
    {
        var it = await fixture.NewDeptAsync();
        var hr = await fixture.NewDeptAsync();
        (await SetAutoReplyAsync(it, new { enabled = true, subject = "IT secret subject {{ticket.reference}}", body = "IT secret body" })).Expect(HttpStatusCode.OK);

        await SameAsRandomAsync("get", id => hr.Admin.Client.GetAsync($"/api/departments/{id}/auto-reply"), it.Id);
        await SameAsRandomAsync("put", id => hr.Admin.Client.PutAsync($"/api/departments/{id}/auto-reply", new { enabled = false, subject = "x", body = "y" }), it.Id);
        await SameAsRandomAsync("preview", id => hr.Admin.Client.PostAsync($"/api/departments/{id}/auto-reply/preview", new { subject = "x", body = "y" }), it.Id);
        Assert.DoesNotContain("IT secret", (await hr.Admin.Client.GetAsync($"{hr.Department.Url}/auto-reply")).Expect(HttpStatusCode.OK).Body, StringComparison.Ordinal);
        (await it.Agent.Client.GetAsync($"{it.Department.Url}/auto-reply")).Expect(HttpStatusCode.Forbidden); // agents do not manage mailboxes

        var current = (await it.Admin.Client.GetAsync($"{it.Department.Url}/auto-reply")).Json;
        (await SetAutoReplyAsync(it, new { enabled = false, version = "stale" })).Expect(HttpStatusCode.BadRequest);
        (await SetAutoReplyAsync(it, new { enabled = false, version = current.Str("version") })).Expect(HttpStatusCode.OK);
        var audit = (await fixture.AuditOfAsync(it.Id)).Where(a => a.Action == "auto_reply.changed").ToList();
        Assert.Equal(2, audit.Count);
        Assert.DoesNotContain(audit, a => (a.Next ?? "").Contains("secret", StringComparison.Ordinal));
        Assert.Contains(audit, a => (a.Next ?? "").Contains("disabled", StringComparison.Ordinal));
    }
}
