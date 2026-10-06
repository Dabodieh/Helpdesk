#pragma warning disable CA1861 // one-off request payloads
using System.Net;
using System.Text.Json;
using Helpdesk.Host.Tests.Infrastructure;
using Helpdesk.Host.Tests.Mail;
using Helpdesk.Host.Tests.Portal;
using Helpdesk.Host.Tests.Sla;
using Helpdesk.Modules.Mail.Providers;
using Helpdesk.SharedKernel.Authorization;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Helpdesk.Host.Tests.Ticketing;

/// <summary>Ticket merge (explicit canonical, read-only sources, preserved provenance) and informational ticket links, end to end through the real API.</summary>
public sealed class TicketMergeTests(HelpdeskFixture fixture) : IClassFixture<HelpdeskFixture>
{
    private static readonly byte[] Pdf = [.. "%PDF-1.7\n1 0 obj\n"u8];

    private static async Task<ApiResponse> MergeAsync(TestUser user, Guid canonicalId, params Guid[] sourceIds)
    {
        var canonical = (await user.GetTicketAsync(canonicalId)).Expect(HttpStatusCode.OK);
        var sources = new List<object>();
        foreach (var id in sourceIds)
        {
            sources.Add(new { ticketId = id, version = (await user.GetTicketAsync(id)).Expect(HttpStatusCode.OK).Version });
        }

        return await user.Client.PostAsync(TicketKit.Url(canonicalId, "/merge"), new { version = canonical.Version, sources });
    }

    private static Task<ApiResponse> LinkAsync(TestUser user, Guid ticketId, Guid otherId, string type) =>
        user.Client.PostAsync(TicketKit.Url(ticketId, "/links"), new { ticketId = otherId, type });

    private static async Task<JsonElement> LinksAsync(TestUser user, Guid ticketId) =>
        (await user.Client.GetAsync(TicketKit.Url(ticketId, "/links"))).Expect(HttpStatusCode.OK).Json;

    private static async Task<Guid[]> QueueIdsAsync(TestUser user, string query) =>
        (await user.Client.GetAsync("/api/tickets" + query)).Expect(HttpStatusCode.OK).Ids();

    // ----- merge -----

    [Fact]
    public async Task Several_tickets_merge_into_an_explicit_canonical_ticket_and_keep_their_identity_and_conversation()
    {
        var d = await fixture.NewDeptAsync();
        var canonical = (await d.TicketForAsync($"a-{Unique.Id()}@example.test", "VPN disconnecting")).Expect(HttpStatusCode.Created);
        var second = (await d.TicketForAsync($"b-{Unique.Id()}@example.test", "VPN not working")).Expect(HttpStatusCode.Created);
        var third = (await d.TicketForAsync($"c-{Unique.Id()}@example.test", "Still cannot connect")).Expect(HttpStatusCode.Created);
        (await d.Agent.PostMessageAsync(second.Id, "Public", "reply on the second")).Expect(HttpStatusCode.Created);
        (await d.Agent.PostMessageAsync(third.Id, "Internal", "private note on the third")).Expect(HttpStatusCode.Created);
        var requesterOfSecond = (await d.Agent.GetTicketAsync(second.Id)).Expect(HttpStatusCode.OK).Json.GetProperty("requester").Str("email");

        var merged = (await MergeAsync(d.Agent, canonical.Id, second.Id, third.Id)).Expect(HttpStatusCode.OK).Json;
        Assert.Equal([second.Reference(), third.Reference()], merged.GetProperty("mergedTickets").EnumerateArray().Select(t => t.Str("reference")).Order().ToArray());

        // The source keeps reference, subject, requester and status; it says where it went, and it is not Closed/Resolved/deleted.
        var source = (await d.Agent.GetTicketAsync(second.Id)).Expect(HttpStatusCode.OK).Json;
        Assert.Equal(second.Reference(), source.Str("reference"));
        Assert.Equal("VPN not working", source.Str("subject"));
        Assert.Equal(requesterOfSecond, source.GetProperty("requester").Str("email"));
        Assert.Equal("Open", source.Str("status"));
        Assert.Equal(canonical.Reference(), source.GetProperty("mergedInto").Str("reference"));
        Assert.Equal(canonical.Id, source.GetProperty("mergedInto").GetProperty("id").GetGuid());

        // The canonical conversation shows the merged messages with their provenance; the source's own messages are untouched.
        var messages = (await d.Agent.MessagesAsync(canonical.Id)).ToList();
        var imported = messages.Where(m => m.TryGetProperty("importedFrom", out var f) && f.ValueKind == JsonValueKind.String).ToList();
        Assert.Contains(imported, m => m.Str("body") == "reply on the second" && m.Str("importedFrom") == second.Reference());
        Assert.Contains(imported, m => m.Str("body") == "private note on the third" && m.Str("importedFrom") == third.Reference());
        Assert.Contains(messages, m => m.Str("body").Contains("Something is broken", StringComparison.Ordinal) && m.Str("importedFrom") == second.Reference());
        Assert.Contains(await d.Agent.MessagesAsync(second.Id), m => m.Str("body") == "reply on the second");

        // Internal notes stay protected in the merged conversation for a caller without tickets.notes.read.
        await using (var noNotes = fixture.Factory.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
        {
            var original = services.Single(s => s.ServiceType == typeof(IAuthorizer));
            services.AddScoped(original.ImplementationType!);
            services.AddScoped<IAuthorizer>(sp => new TicketIsolationLeakageTests.DenyingAuthorizer((IAuthorizer)sp.GetRequiredService(original.ImplementationType!), [PermissionCodes.TicketsNotesRead]));
        })))
        {
            var reader = await fixture.SignInAsync(d.Viewer.Subject, factory: noNotes);
            var viewerView = (await reader.Client.GetAsync(TicketKit.Url(canonical.Id, "/messages"))).Expect(HttpStatusCode.OK);
            Assert.DoesNotContain("private note on the third", viewerView.Body, StringComparison.Ordinal);
            Assert.Contains("reply on the second", viewerView.Body, StringComparison.Ordinal);
        }

        // Both sides have understandable history, naming the actor, without copying conversation text.
        var canonicalAudit = (await fixture.AuditOfAsync(canonical.Id)).Single(a => a.Action == "ticket.merged_in");
        Assert.Equal(d.Agent.Id, canonicalAudit.ActorUserId);
        Assert.Contains(second.Reference(), canonicalAudit.Next, StringComparison.Ordinal);
        Assert.DoesNotContain("reply on the second", canonicalAudit.Next, StringComparison.Ordinal);
        var sourceAudit = (await fixture.AuditOfAsync(second.Id)).Single(a => a.Action == "ticket.merged_into");
        Assert.Contains(canonical.Reference(), sourceAudit.Next, StringComparison.Ordinal);
        var activity = (await d.Agent.Client.GetAsync(TicketKit.Url(canonical.Id, "/activity"))).Expect(HttpStatusCode.OK).Json.GetProperty("items");
        Assert.Contains(activity.EnumerateArray(), a => a.Str("action") == "ticket.merged_in");
    }

    [Fact]
    public async Task Merged_tickets_are_read_only_leave_queues_and_stay_findable_by_reference()
    {
        var d = await fixture.NewDeptAsync();
        var canonical = (await d.TicketForAsync($"a-{Unique.Id()}@example.test", "Canonical one")).Expect(HttpStatusCode.Created);
        var source = (await d.TicketForAsync($"b-{Unique.Id()}@example.test", "Duplicate one")).Expect(HttpStatusCode.Created);
        var before = (await d.Agent.Client.GetAsync("/api/tickets/queue-counts")).Expect(HttpStatusCode.OK).Json.GetProperty("all").GetInt32();
        (await MergeAsync(d.Agent, canonical.Id, source.Id)).Expect(HttpStatusCode.OK);

        var current = (await d.Agent.GetTicketAsync(source.Id)).Expect(HttpStatusCode.OK);
        (await d.Agent.PatchTicketAsync(current, new { Status = "Closed" })).ExpectCode(HttpStatusCode.Conflict, "ticket_merged");
        (await d.Agent.PostMessageAsync(source.Id, "Public", "should not send")).ExpectCode(HttpStatusCode.Conflict, "ticket_merged");
        (await d.Agent.PostMessageAsync(source.Id, "Internal", "nor a note")).ExpectCode(HttpStatusCode.Conflict, "ticket_merged");
        (await d.Lead.AssignAsync(source.Id, current.Version, null, d.Agent.Id)).ExpectCode(HttpStatusCode.Conflict, "ticket_merged");
        (await d.Agent.Client.PatchAsync(TicketKit.Url(source.Id, "/properties"), new { version = current.Version, clearTicketType = true }))
            .ExpectCode(HttpStatusCode.Conflict, "ticket_merged");
        (await d.Agent.Client.PostAsync("/api/tickets/bulk", new { ticketIds = new[] { source.Id }, status = "Closed" })).Expect(HttpStatusCode.OK);
        Assert.Equal("Open", (await d.Agent.GetTicketAsync(source.Id)).Expect(HttpStatusCode.OK).Json.Str("status"));

        // Queues and counts exclude the merged source; a search by its reference still finds it, marked as merged.
        Assert.DoesNotContain(source.Id, await QueueIdsAsync(d.Agent, $"?departmentId={d.Id}"));
        Assert.Contains(canonical.Id, await QueueIdsAsync(d.Agent, $"?departmentId={d.Id}"));
        Assert.Equal(before - 1, (await d.Agent.Client.GetAsync("/api/tickets/queue-counts")).Expect(HttpStatusCode.OK).Json.GetProperty("all").GetInt32());
        var search = (await d.Agent.Client.GetAsync($"/api/tickets?q={source.Reference()}")).Expect(HttpStatusCode.OK).Json.GetProperty("items");
        var hit = Assert.Single(search.EnumerateArray());
        Assert.Equal(source.Id, hit.GetProperty("id").GetGuid());
        Assert.Equal(canonical.Reference(), hit.Str("mergedInto"));
    }

    [Fact]
    public async Task A_repeated_merge_is_harmless_and_stale_versions_or_concurrent_merges_change_nothing_twice()
    {
        var d = await fixture.NewDeptAsync();
        var a = (await d.TicketForAsync($"a-{Unique.Id()}@example.test")).Expect(HttpStatusCode.Created);
        var b = (await d.TicketForAsync($"b-{Unique.Id()}@example.test")).Expect(HttpStatusCode.Created);
        var c = (await d.TicketForAsync($"c-{Unique.Id()}@example.test")).Expect(HttpStatusCode.Created);

        // A stale source version is refused before anything changes.
        var staleCanonical = (await d.Agent.GetTicketAsync(a.Id)).Expect(HttpStatusCode.OK);
        var staleSource = (await d.Agent.GetTicketAsync(b.Id)).Expect(HttpStatusCode.OK);
        (await d.Agent.PostMessageAsync(b.Id, "Public", "changes the version")).Expect(HttpStatusCode.Created);
        (await d.Agent.Client.PostAsync(TicketKit.Url(a.Id, "/merge"), new { version = staleCanonical.Version, sources = new[] { new { ticketId = b.Id, version = staleSource.Version } } }))
            .ExpectCode(HttpStatusCode.Conflict, "version_conflict");
        Assert.Equal(JsonValueKind.Null, (await d.Agent.GetTicketAsync(b.Id)).Expect(HttpStatusCode.OK).Json.GetProperty("mergedInto").ValueKind);

        // Two agents merge the same source into different canonical tickets at once: exactly one wins, state stays coherent.
        var first = MergeAsync(d.Agent, a.Id, b.Id);
        var second = MergeAsync(d.Lead, c.Id, b.Id);
        var results = await Task.WhenAll(first, second);
        Assert.Single(results, r => r.Status == HttpStatusCode.OK);
        var loser = Assert.Single(results, r => r.Status != HttpStatusCode.OK);
        Assert.Equal(HttpStatusCode.Conflict, loser.Status);
        var winnerRef = results[0].Status == HttpStatusCode.OK ? a.Reference() : c.Reference();
        Assert.Equal(winnerRef, (await d.Agent.GetTicketAsync(b.Id)).Expect(HttpStatusCode.OK).Json.GetProperty("mergedInto").Str("reference"));

        // Repeating the winning merge reports success without changing anything.
        var winnerId = results[0].Status == HttpStatusCode.OK ? a.Id : c.Id;
        (await MergeAsync(d.Agent, winnerId, b.Id)).Expect(HttpStatusCode.OK);
        Assert.Single((await fixture.AuditOfAsync(b.Id)), x => x.Action == "ticket.merged_into");
    }

    [Fact]
    public async Task Merging_a_canonical_ticket_repoints_its_sources_so_there_are_no_chains()
    {
        var d = await fixture.NewDeptAsync();
        var a = (await d.TicketForAsync($"a-{Unique.Id()}@example.test")).Expect(HttpStatusCode.Created);
        var b = (await d.TicketForAsync($"b-{Unique.Id()}@example.test")).Expect(HttpStatusCode.Created);
        var c = (await d.TicketForAsync($"c-{Unique.Id()}@example.test")).Expect(HttpStatusCode.Created);
        (await MergeAsync(d.Agent, b.Id, a.Id)).Expect(HttpStatusCode.OK);
        (await MergeAsync(d.Agent, c.Id, b.Id)).Expect(HttpStatusCode.OK);

        Assert.Equal(c.Reference(), (await d.Agent.GetTicketAsync(a.Id)).Expect(HttpStatusCode.OK).Json.GetProperty("mergedInto").Str("reference"));
        Assert.Equal(c.Reference(), (await d.Agent.GetTicketAsync(b.Id)).Expect(HttpStatusCode.OK).Json.GetProperty("mergedInto").Str("reference"));
        Assert.Equal(2, (await d.Agent.GetTicketAsync(c.Id)).Expect(HttpStatusCode.OK).Json.GetProperty("mergedTickets").GetArrayLength());

        // A merged ticket cannot be a canonical ticket.
        var d2 = (await d.TicketForAsync($"d-{Unique.Id()}@example.test")).Expect(HttpStatusCode.Created);
        (await MergeAsync(d.Agent, a.Id, d2.Id)).ExpectCode(HttpStatusCode.Conflict, "ticket_merged");
    }

    [Fact]
    public async Task Merging_is_limited_to_one_department_and_to_tickets_the_agent_can_see_and_manage()
    {
        var it = await fixture.NewDeptAsync();
        var hr = await fixture.NewDeptAsync();
        var itTicket = (await it.TicketForAsync($"a-{Unique.Id()}@example.test", "IT ticket")).Expect(HttpStatusCode.Created);
        var itOther = (await it.TicketForAsync($"b-{Unique.Id()}@example.test", "IT other")).Expect(HttpStatusCode.Created);
        var hrTicket = (await hr.TicketForAsync($"c-{Unique.Id()}@example.test", "HR ticket")).Expect(HttpStatusCode.Created);

        // An agent who can read both departments still cannot merge across them: structured error, nothing changes (also not the same-department part).
        var both = await fixture.NewMemberAsync(it.Department, "Agent");
        await fixture.AddMemberAsync(hr.Department, both, "Agent");
        (await MergeAsync(both, itTicket.Id, itOther.Id, hrTicket.Id)).ExpectCode(HttpStatusCode.Conflict, "cross_department");
        Assert.Equal(JsonValueKind.Null, (await it.Agent.GetTicketAsync(itOther.Id)).Expect(HttpStatusCode.OK).Json.GetProperty("mergedInto").ValueKind);
        Assert.Equal(JsonValueKind.Null, (await hr.Agent.GetTicketAsync(hrTicket.Id)).Expect(HttpStatusCode.OK).Json.GetProperty("mergedInto").ValueKind);

        // A ticket of a department the caller cannot read is indistinguishable from a missing one.
        var hrVersion = (await hr.Agent.GetTicketAsync(hrTicket.Id)).Expect(HttpStatusCode.OK).Version;
        var itVersion = (await it.Agent.GetTicketAsync(itTicket.Id)).Expect(HttpStatusCode.OK).Version;
        (await it.Agent.Client.PostAsync(TicketKit.Url(itTicket.Id, "/merge"), new { version = itVersion, sources = new[] { new { ticketId = hrTicket.Id, version = hrVersion } } }))
            .Expect(HttpStatusCode.NotFound);
        (await it.Agent.Client.PostAsync(TicketKit.Url(itTicket.Id, "/merge"), new { version = itVersion, sources = new[] { new { ticketId = Guid.NewGuid(), version = hrVersion } } }))
            .Expect(HttpStatusCode.NotFound);

        // Canonical ticket of another department, or one the caller cannot update, is refused.
        (await it.Agent.Client.PostAsync(TicketKit.Url(hrTicket.Id, "/merge"), new { version = hrVersion, sources = new[] { new { ticketId = itTicket.Id, version = itVersion } } })).Expect(HttpStatusCode.NotFound);
        (await MergeAsync(it.Viewer, itTicket.Id, itOther.Id)).Expect(HttpStatusCode.Forbidden);

        // Validation: nothing to merge, itself, duplicates.
        (await it.Agent.Client.PostAsync(TicketKit.Url(itTicket.Id, "/merge"), new { version = itVersion, sources = Array.Empty<object>() })).Expect(HttpStatusCode.BadRequest);
        (await it.Agent.Client.PostAsync(TicketKit.Url(itTicket.Id, "/merge"), new { version = itVersion, sources = new[] { new { ticketId = itTicket.Id, version = itVersion } } })).Expect(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Candidate_search_shows_only_accessible_unmerged_tickets_of_the_same_department()
    {
        var it = await fixture.NewDeptAsync();
        var hr = await fixture.NewDeptAsync();
        var word = $"zebra{Unique.Id()}";
        var itTicket = (await it.TicketForAsync($"a-{Unique.Id()}@example.test", $"{word} printer")).Expect(HttpStatusCode.Created);
        var itMatch = (await it.TicketForAsync($"b-{Unique.Id()}@example.test", $"{word} scanner")).Expect(HttpStatusCode.Created);
        var itGone = (await it.TicketForAsync($"c-{Unique.Id()}@example.test", $"{word} merged away")).Expect(HttpStatusCode.Created);
        var hrMatch = (await hr.TicketForAsync($"d-{Unique.Id()}@example.test", $"{word} hr secret")).Expect(HttpStatusCode.Created);
        (await MergeAsync(it.Agent, itMatch.Id, itGone.Id)).Expect(HttpStatusCode.OK);

        var found = (await it.Agent.Client.GetAsync(TicketKit.Url(itTicket.Id, $"/link-candidates?q={word}"))).Expect(HttpStatusCode.OK);
        var ids = found.Json.EnumerateArray().Select(c => c.GetProperty("id").GetGuid()).ToArray();
        Assert.Equal([itMatch.Id], ids);
        Assert.DoesNotContain("hr secret", found.Body, StringComparison.Ordinal);
        Assert.False(string.IsNullOrEmpty(found.Json[0].Str("version")));
        Assert.Equal(itMatch.Reference(), (await it.Agent.Client.GetAsync(TicketKit.Url(itTicket.Id, $"/link-candidates?q={itMatch.Reference()}"))).Expect(HttpStatusCode.OK).Json[0].Str("reference"));

        // An agent of another department cannot even ask about this ticket; too-short searches are rejected.
        (await hr.Agent.Client.GetAsync(TicketKit.Url(itTicket.Id, $"/link-candidates?q={word}"))).Expect(HttpStatusCode.NotFound);
        (await it.Agent.Client.GetAsync(TicketKit.Url(itTicket.Id, "/link-candidates?q=a"))).Expect(HttpStatusCode.BadRequest);
        Assert.NotEqual(Guid.Empty, hrMatch.Id);
    }

    [Fact]
    public async Task The_merged_source_sla_stops_and_the_canonical_sla_is_neither_reset_nor_combined()
    {
        await using var w = await TimedWorld.CreateAsync(fixture, "2026-01-12T10:00:00Z");
        var calendar = await w.CalendarAsync();
        await w.PolicyAsync("Standard", calendar, firstResponse: null, resolution: 240);
        var canonical = await w.NewTicketAsync();
        var source = await w.NewTicketAsync();
        var dueBefore = TimedWorld.At(await w.SlaAsync(canonical.Id), "resolution", "dueAt");

        await w.GoToAsync("2026-01-12T11:00:00Z");
        (await MergeAsync(w.Lead, canonical.Id, source.Id)).Expect(HttpStatusCode.OK);
        var sourceSla = await w.SlaAsync(source.Id);
        Assert.Equal("None", sourceSla.GetProperty("resolution").GetProperty("state").GetString());
        Assert.Contains(sourceSla.GetProperty("events").EnumerateArray(), e => e.GetProperty("kind").GetString() == "Stopped");
        Assert.Equal(dueBefore, TimedWorld.At(await w.SlaAsync(canonical.Id), "resolution", "dueAt"));

        // Long after both deadlines: only the canonical ticket breaches.
        await w.GoToAsync("2026-01-13T09:00:00Z");
        await w.RunMonitorAsync();
        Assert.Equal("Breached", (await w.SlaAsync(canonical.Id)).GetProperty("resolution").GetProperty("state").GetString());
        Assert.DoesNotContain((await w.SlaAsync(source.Id)).GetProperty("events").EnumerateArray(), e => e.GetProperty("kind").GetString() == "Breached");
        Assert.Equal(0, await w.CountAsync("SELECT count(*) FROM ticketing.ticket_sla WHERE ticket_id = @t AND next_event_at IS NOT NULL", TicketKit.P("t", source.Id)));
    }

    [Fact]
    public async Task Attachments_stay_on_their_original_ticket_and_remain_downloadable_from_the_canonical_ticket_and_old_thread_mail_continues_there()
    {
        var d = await fixture.NewDeptAsync();
        var box = await fixture.CreateMailboxAsync(d);
        var sender = MailKit.NewSender();
        var subject = $"Merge mail {Unique.Id()}";
        var internetId = $"<{Guid.NewGuid():N}@client.test>";
        await fixture.ReceiveAsync(box, MailKit.Mail(sender, subject) with { InternetMessageId = internetId, Attachments = [new FakeAttachment("Report.PDF", Pdf)] });
        var source = Assert.Single(await d.Agent.TicketsAsync(d, subject)).GetProperty("id").GetGuid();
        var canonical = (await d.TicketForAsync($"keep-{Unique.Id()}@example.test", "Keep this one")).Expect(HttpStatusCode.Created);
        var attachmentId = Assert.Single((await d.Agent.MessagesAsync(source)).Single().GetProperty("attachments").EnumerateArray()).GetProperty("id").GetGuid();
        var storedBefore = await fixture.CountAsync("SELECT count(*) FROM attachments.attachments WHERE department_id = @d", TicketKit.P("d", d.Id));

        (await MergeAsync(d.Agent, canonical.Id, source)).Expect(HttpStatusCode.OK);

        // No copy: the same attachment row, reachable through the canonical ticket's conversation and download route.
        Assert.Equal(storedBefore, await fixture.CountAsync("SELECT count(*) FROM attachments.attachments WHERE department_id = @d", TicketKit.P("d", d.Id)));
        var imported = (await d.Agent.MessagesAsync(canonical.Id)).Single(m => m.TryGetProperty("importedFrom", out var f) && f.ValueKind == JsonValueKind.String);
        Assert.Equal(attachmentId, imported.GetProperty("attachments")[0].GetProperty("id").GetGuid());
        var download = await d.Agent.Client.Http.GetAsync($"/api/tickets/{canonical.Id}/attachments/{attachmentId}");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(Pdf, await download.Content.ReadAsByteArrayAsync());
        (await d.Agent.Client.GetAsync($"/api/tickets/{canonical.Id}/attachments/{Guid.NewGuid()}")).Expect(HttpStatusCode.NotFound);

        // A reply to the OLD thread lands on the canonical ticket: no new ticket, the merged one is not resurrected.
        var ticketsBefore = await fixture.CountAsync("SELECT count(*) FROM ticketing.tickets WHERE department_id = @d", TicketKit.P("d", d.Id));
        await fixture.ReceiveAsync(box, MailKit.Mail(sender, "Re: whatever", "Following up on the old thread") with { InReplyTo = internetId });
        Assert.Equal(ticketsBefore, await fixture.CountAsync("SELECT count(*) FROM ticketing.tickets WHERE department_id = @d", TicketKit.P("d", d.Id)));
        Assert.Contains(await d.Agent.MessagesAsync(canonical.Id), m => m.Str("body").Contains("Following up on the old thread", StringComparison.Ordinal));
        Assert.DoesNotContain(await d.Agent.MessagesAsync(source), m => m.Str("body").Contains("Following up on the old thread", StringComparison.Ordinal));
        Assert.Equal("Open", (await d.Agent.GetTicketAsync(source)).Expect(HttpStatusCode.OK).Json.Str("status"));
    }

    [Fact]
    public async Task Requester_history_keeps_merged_tickets_marked_without_inflating_open_counts()
    {
        var d = await fixture.NewDeptAsync();
        var email = $"dana-{Unique.Id()}@example.test";
        var keep = (await d.TicketForAsync(email, "VPN drops")).Expect(HttpStatusCode.Created);
        var dup = (await d.TicketForAsync(email, "VPN drops again")).Expect(HttpStatusCode.Created);
        var requesterId = (await d.Agent.GetTicketAsync(keep.Id)).Expect(HttpStatusCode.OK).Json.GetProperty("requester").GetProperty("id").GetGuid();
        (await MergeAsync(d.Agent, keep.Id, dup.Id)).Expect(HttpStatusCode.OK);

        var profile = (await d.Agent.Client.GetAsync($"/api/requesters/{requesterId}")).Expect(HttpStatusCode.OK).Json;
        Assert.Equal(2, profile.GetProperty("totalCount").GetInt32()); // history includes merged tickets
        Assert.Equal(1, profile.GetProperty("openCount").GetInt32()); // ...the open count does not
        Assert.Equal([keep.Reference()], profile.GetProperty("open").EnumerateArray().Select(t => t.Str("reference")).ToArray());
        var previous = Assert.Single(profile.GetProperty("previous").EnumerateArray());
        Assert.Equal(dup.Reference(), previous.Str("reference"));
        Assert.Equal(keep.Reference(), previous.Str("mergedInto"));
        Assert.Single((await d.Agent.Client.GetAsync($"/api/requesters/{requesterId}/tickets?state=active")).Expect(HttpStatusCode.OK).Json.GetProperty("items").EnumerateArray());
        Assert.Equal(dup.Reference(), (await d.Agent.Client.GetAsync($"/api/requesters/{requesterId}/tickets?state=resolved")).Expect(HttpStatusCode.OK).Json.GetProperty("items")[0].Str("reference"));
        var header = (await d.Agent.GetTicketAsync(keep.Id)).Expect(HttpStatusCode.OK).Json.GetProperty("requester");
        Assert.Equal(1, header.GetProperty("openTicketCount").GetInt32());
        Assert.Equal(2, header.GetProperty("totalTicketCount").GetInt32());
    }

    [Fact]
    public async Task The_portal_points_a_requester_to_their_active_ticket_and_never_exposes_another_requesters_ticket()
    {
        var d = await fixture.NewDeptAsync();
        var alice = await fixture.EmployeeAsync("alice");
        var bob = await fixture.EmployeeAsync("bob");
        var first = (await d.TicketForAsync(alice.Email, "Alice first")).Expect(HttpStatusCode.Created);
        var second = (await d.TicketForAsync(alice.Email, "Alice second")).Expect(HttpStatusCode.Created);
        var bobs = (await d.TicketForAsync(bob.Email, "Bob private problem")).Expect(HttpStatusCode.Created);
        (await d.Agent.PostMessageAsync(bobs.Id, "Public", "Bob only text")).Expect(HttpStatusCode.Created);

        (await MergeAsync(d.Agent, first.Id, second.Id, bobs.Id)).Expect(HttpStatusCode.OK);

        // Alice: the merged request leaves her list and opens as a transition to the one she still has; replying to it is refused.
        var list = await alice.PortalListAsync();
        Assert.Equal([first.Reference()], list.GetProperty("items").EnumerateArray().Select(i => i.Str("reference")).ToArray());
        var transition = (await alice.User.Client.GetAsync($"/api/portal/requests/{second.Reference()}")).Expect(HttpStatusCode.OK).Json;
        Assert.Equal(first.Reference(), transition.Str("mergedInto"));
        Assert.Equal("merged", transition.Str("status"));
        Assert.Equal(0, transition.GetProperty("messages").GetArrayLength());
        (await alice.User.Client.PostAsync($"/api/portal/requests/{second.Reference()}/replies", new { body = "hello?" })).Expect(HttpStatusCode.Conflict);

        // The canonical ticket's requester never sees the merged requester's conversation.
        var canonical = (await alice.User.Client.GetAsync($"/api/portal/requests/{first.Reference()}")).Expect(HttpStatusCode.OK);
        Assert.DoesNotContain("Bob only text", canonical.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("Bob private problem", canonical.Body, StringComparison.Ordinal);

        // Bob gains nothing from the merge: his merged request fails closed and the canonical one is not his.
        Assert.Empty((await bob.PortalListAsync()).GetProperty("items").EnumerateArray());
        (await bob.User.Client.GetAsync($"/api/portal/requests/{bobs.Reference()}")).Expect(HttpStatusCode.NotFound);
        (await bob.User.Client.GetAsync($"/api/portal/requests/{first.Reference()}")).Expect(HttpStatusCode.NotFound);
    }

    // ----- links -----

    [Fact]
    public async Task Related_links_are_symmetric_and_duplicates_of_the_pair_or_self_links_are_rejected()
    {
        var d = await fixture.NewDeptAsync();
        var a = (await d.TicketForAsync($"a-{Unique.Id()}@example.test", "Broadband A")).Expect(HttpStatusCode.Created);
        var b = (await d.TicketForAsync($"b-{Unique.Id()}@example.test", "Broadband B")).Expect(HttpStatusCode.Created);

        var link = (await LinkAsync(d.Agent, a.Id, b.Id, "Related")).Expect(HttpStatusCode.Created).Json;
        Assert.Equal("Related", link.Str("relationship"));
        Assert.Equal(b.Reference(), link.Str("reference"));
        Assert.Equal("Broadband B", link.Str("subject"));

        var fromA = Assert.Single((await LinksAsync(d.Agent, a.Id)).EnumerateArray());
        var fromB = Assert.Single((await LinksAsync(d.Viewer, b.Id)).EnumerateArray());
        Assert.Equal((b.Reference(), "Related"), (fromA.Str("reference"), fromA.Str("relationship")));
        Assert.Equal((a.Reference(), "Related"), (fromB.Str("reference"), fromB.Str("relationship")));

        // Both stay independently active.
        Assert.Equal("Open", (await d.Agent.GetTicketAsync(a.Id)).Expect(HttpStatusCode.OK).Json.Str("status"));
        Assert.Equal("Open", (await d.Agent.GetTicketAsync(b.Id)).Expect(HttpStatusCode.OK).Json.Str("status"));

        (await LinkAsync(d.Agent, a.Id, b.Id, "Related")).ExpectCode(HttpStatusCode.Conflict, "duplicate");
        (await LinkAsync(d.Agent, b.Id, a.Id, "Related")).ExpectCode(HttpStatusCode.Conflict, "duplicate");
        (await LinkAsync(d.Agent, b.Id, a.Id, "DuplicateOf")).ExpectCode(HttpStatusCode.Conflict, "duplicate");
        (await LinkAsync(d.Agent, a.Id, a.Id, "Related")).Expect(HttpStatusCode.BadRequest);
        (await LinkAsync(d.Agent, a.Id, b.Id, "Sibling")).Expect(HttpStatusCode.BadRequest);
        (await LinkAsync(d.Viewer, a.Id, b.Id, "Related")).Expect(HttpStatusCode.Forbidden);

        (await d.Agent.Client.DeleteAsync(TicketKit.Url(a.Id, $"/links/{link.GetProperty("id").GetGuid()}"))).Expect(HttpStatusCode.NoContent);
        Assert.Empty((await LinksAsync(d.Agent, b.Id)).EnumerateArray());
        Assert.Contains((await fixture.AuditOfAsync(a.Id)), x => x.Action == "ticket.link_added");
        Assert.Contains((await fixture.AuditOfAsync(b.Id)), x => x.Action == "ticket.link_removed");
    }

    [Fact]
    public async Task Parent_child_and_duplicate_links_are_directional_and_circular_hierarchies_are_refused()
    {
        var d = await fixture.NewDeptAsync();
        var p = (await d.TicketForAsync($"p-{Unique.Id()}@example.test", "Parent")).Expect(HttpStatusCode.Created);
        var q = (await d.TicketForAsync($"q-{Unique.Id()}@example.test", "Child")).Expect(HttpStatusCode.Created);
        var r = (await d.TicketForAsync($"r-{Unique.Id()}@example.test", "Grandchild")).Expect(HttpStatusCode.Created);
        var dup = (await d.TicketForAsync($"x-{Unique.Id()}@example.test", "Copy of parent")).Expect(HttpStatusCode.Created);

        (await LinkAsync(d.Agent, p.Id, q.Id, "Child")).Expect(HttpStatusCode.Created); // q is a child of p
        (await LinkAsync(d.Agent, r.Id, q.Id, "Parent")).Expect(HttpStatusCode.Created); // q is the parent of r
        Assert.Equal("Child", Assert.Single((await LinksAsync(d.Agent, p.Id)).EnumerateArray()).Str("relationship"));
        Assert.Equal(["Child", "Parent"], (await LinksAsync(d.Agent, q.Id)).EnumerateArray().Select(l => l.Str("relationship")).Order().ToArray());
        Assert.Equal("Parent", Assert.Single((await LinksAsync(d.Agent, r.Id)).EnumerateArray()).Str("relationship"));

        // p -> q -> r: making r a parent of p (directly or through q) would be circular.
        (await LinkAsync(d.Agent, p.Id, r.Id, "Parent")).ExpectCode(HttpStatusCode.Conflict, "invalid_link");
        (await LinkAsync(d.Agent, r.Id, p.Id, "Child")).ExpectCode(HttpStatusCode.Conflict, "invalid_link");

        (await LinkAsync(d.Agent, dup.Id, p.Id, "DuplicateOf")).Expect(HttpStatusCode.Created);
        Assert.Equal("DuplicateOf", Assert.Single((await LinksAsync(d.Agent, dup.Id)).EnumerateArray()).Str("relationship"));
        Assert.Contains((await LinksAsync(d.Agent, p.Id)).EnumerateArray(), l => l.Str("relationship") == "DuplicatedBy" && l.Str("reference") == dup.Reference());
    }

    [Fact]
    public async Task Links_cannot_cross_departments_leak_hidden_tickets_or_involve_merged_tickets()
    {
        var it = await fixture.NewDeptAsync();
        var hr = await fixture.NewDeptAsync();
        var itTicket = (await it.TicketForAsync($"a-{Unique.Id()}@example.test", "IT")).Expect(HttpStatusCode.Created);
        var itOther = (await it.TicketForAsync($"b-{Unique.Id()}@example.test", "IT other")).Expect(HttpStatusCode.Created);
        var hrTicket = (await hr.TicketForAsync($"c-{Unique.Id()}@example.test", "HR confidential")).Expect(HttpStatusCode.Created);

        // Crafted cross-department ids: invisible = 404; visible-but-foreign = structured 409; nothing is stored.
        (await LinkAsync(it.Agent, itTicket.Id, hrTicket.Id, "Related")).Expect(HttpStatusCode.NotFound);
        (await LinkAsync(it.Agent, itTicket.Id, Guid.NewGuid(), "Related")).Expect(HttpStatusCode.NotFound);
        (await LinkAsync(it.Agent, hrTicket.Id, itTicket.Id, "Related")).Expect(HttpStatusCode.NotFound);
        var both = await fixture.NewMemberAsync(it.Department, "Agent");
        await fixture.AddMemberAsync(hr.Department, both, "Agent");
        (await LinkAsync(both, itTicket.Id, hrTicket.Id, "Related")).ExpectCode(HttpStatusCode.Conflict, "cross_department");
        Assert.Empty((await LinksAsync(both, itTicket.Id)).EnumerateArray());
        Assert.Equal(0, await fixture.CountAsync("SELECT count(*) FROM ticketing.ticket_links WHERE department_id = ANY(@d)", TicketKit.P("d", new[] { it.Id, hr.Id })));

        // The database itself refuses a link whose ends are in different departments.
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => fixture.CountAsync(
            "INSERT INTO ticketing.ticket_links (id, department_id, source_ticket_id, target_ticket_id, type, created_at) VALUES (gen_random_uuid(), @d, @s, @t, 'Related', now()) RETURNING 1",
            TicketKit.P("d", it.Id), TicketKit.P("s", itTicket.Id), TicketKit.P("t", hrTicket.Id)));
        Assert.Contains("fk_ticket_links_target_department", ex.ToString(), StringComparison.Ordinal);

        // A merged ticket can no longer be linked.
        (await MergeAsync(it.Agent, itTicket.Id, itOther.Id)).Expect(HttpStatusCode.OK);
        (await LinkAsync(it.Agent, itTicket.Id, itOther.Id, "Related")).ExpectCode(HttpStatusCode.Conflict, "ticket_merged");
        (await LinkAsync(it.Agent, itOther.Id, itTicket.Id, "Related")).ExpectCode(HttpStatusCode.Conflict, "ticket_merged");
    }

    [Fact]
    public async Task A_link_to_a_ticket_that_is_later_merged_shows_where_it_went()
    {
        var d = await fixture.NewDeptAsync();
        var a = (await d.TicketForAsync($"a-{Unique.Id()}@example.test", "Linked ticket")).Expect(HttpStatusCode.Created);
        var b = (await d.TicketForAsync($"b-{Unique.Id()}@example.test", "Will be merged")).Expect(HttpStatusCode.Created);
        var c = (await d.TicketForAsync($"c-{Unique.Id()}@example.test", "Canonical")).Expect(HttpStatusCode.Created);
        (await LinkAsync(d.Agent, a.Id, b.Id, "Related")).Expect(HttpStatusCode.Created);
        (await MergeAsync(d.Agent, c.Id, b.Id)).Expect(HttpStatusCode.OK);

        var link = Assert.Single((await LinksAsync(d.Agent, a.Id)).EnumerateArray());
        Assert.Equal(b.Reference(), link.Str("reference"));
        Assert.Equal(c.Reference(), link.Str("mergedInto"));
    }
}
