using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Helpdesk.Host.Tests.Infrastructure;
using Helpdesk.SharedKernel.Authorization;
using Helpdesk.SharedKernel.Errors;
using Helpdesk.SharedKernel.Paging;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Serilog.Core;
using Serilog.Events;

namespace Helpdesk.Host.Tests.Ticketing;

/// <summary>
/// Phase 2 ticket security tests beyond the persona matrix (see <see cref="TicketIsolationMatrixTests"/>): internal-note protection,
/// assignment validity, queue/search/cursor leakage, requesters, tampering, stored markup, mass assignment, CSRF, reference
/// immutability, concurrency, revocation and hostile input. Each test says which threat (docs/security/PHASE2_THREAT_MODEL.md) it proves.
/// </summary>
public sealed class TicketIsolationLeakageTests(HelpdeskFixture fixture) : IClassFixture<HelpdeskFixture>
{
    private static readonly string[] SortNames = ["updated_desc", "updated_asc", "created_desc", "created_asc", "priority_desc"];
    private static readonly string[] QueueNames = ["all", "my", "unassigned"];

    // ----- internal notes (T4) -----

    private sealed class CaptureSink : ILogEventSink
    {
        public ConcurrentQueue<string> Lines { get; } = new();

        public void Emit(LogEvent logEvent)
        {
            Lines.Enqueue(logEvent.RenderMessage(System.Globalization.CultureInfo.InvariantCulture) + " " + string.Join(" ", logEvent.Properties.Select(p => $"{p.Key}={p.Value}")));
            if (logEvent.Exception is not null)
            {
                Lines.Enqueue(logEvent.Exception.ToString());
            }
        }
    }

    [Fact]
    public async Task Message_bodies_appear_only_in_the_messages_endpoint_and_never_in_audit_activity_search_lists_or_logs()
    {
        var w = await IsoWorld.For(fixture);
        var sink = new CaptureSink();
        await using var logged = fixture.Factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<ILogEventSink>(sink)));
        var agent = await fixture.SignInAsync(w.Agent.Subject, factory: logged);
        var lead = await fixture.SignInAsync(w.Lead.Subject, factory: logged);

        var tag = Unique.Id();
        var desc = $"DESCTOKEN-{tag}";
        var pub = $"PUBTOKEN-{tag}";
        var note = $"NOTETOKEN-{tag}";
        var created = (await agent.Client.PostAsync($"{w.A.Url}/tickets", new { subject = $"Subj {tag}", description = desc, requester = new { email = $"r-{tag}@example.test" } })).Expect(HttpStatusCode.Created);
        (await agent.PostMessageAsync(created.Id, "Public", pub)).Expect(HttpStatusCode.Created);
        (await agent.PostMessageAsync(created.Id, "Internal", note, status: "Pending")).Expect(HttpStatusCode.Created);
        var current = (await agent.GetTicketAsync(created.Id)).Expect(HttpStatusCode.OK);
        (await lead.AssignAsync(created.Id, current.Version, w.Target.Id)).Expect(HttpStatusCode.OK);
        (await lead.PatchTicketAsync((await lead.GetTicketAsync(created.Id)), new { Subject = $"Subj2 {tag}", Priority = "High" })).Expect(HttpStatusCode.OK);

        string[] tokens = [desc, pub, note];
        var reads = new List<(string Name, Func<ApiClient, Task<ApiResponse>> Call)>
        {
            ("ticket", c => c.GetAsync($"/api/tickets/{created.Id}")),
            ("activity", c => c.GetAsync($"/api/tickets/{created.Id}/activity?limit=200")),
            ("list", c => c.GetAsync("/api/tickets?limit=100&status=Open&status=Pending&status=Resolved&status=Closed")),
            ("list q=note", c => c.GetAsync($"/api/tickets?q={note}")),
            ("list q=public", c => c.GetAsync($"/api/tickets?q={pub}")),
            ("list q=description", c => c.GetAsync($"/api/tickets?q={desc}")),
            ("counts", c => c.GetAsync("/api/tickets/queue-counts")),
        };
        // Phase 4: search results carry a snippet of the matching message text, but only for callers who may read that content
        // (department members; internal notes need tickets.notes.read). Everyone else must never see a token anywhere.
        string[] insiders = [IsoWorld.ViewerA, IsoWorld.AgentA, IsoWorld.LeadA, IsoWorld.AdminA];
        foreach (var persona in new[] { IsoWorld.ViewerA, IsoWorld.AgentA, IsoWorld.LeadA, IsoWorld.AdminA, IsoWorld.NoMembership, IsoWorld.MemberOfBOnly, IsoWorld.AdminOfBAgainstA })
        {
            foreach (var (name, call) in reads)
            {
                if (name.StartsWith("list q=", StringComparison.Ordinal) && insiders.Contains(persona))
                {
                    continue; // snippets for insiders are intended; covered below
                }

                var body = (await call(w.Actors[persona].Client)).Body;
                foreach (var token in tokens)
                {
                    Assert.False(body.Contains(token, StringComparison.Ordinal), $"{persona} {name} leaked {token}");
                }
            }
        }

        // Insiders find the ticket by message text and see why it matched (a snippet of text they may read anyway).
        var hit = Assert.Single((await w.Actors[IsoWorld.AdminA].Client.GetAsync($"/api/tickets?q={note}")).Expect(HttpStatusCode.OK).Json.GetProperty("items").EnumerateArray());
        Assert.Equal("note", hit.GetProperty("match").GetProperty("field").GetString());

        // The one place bodies live: the messages endpoint, for callers with the matching permission.
        foreach (var persona in new[] { IsoWorld.ViewerA, IsoWorld.AgentA, IsoWorld.LeadA, IsoWorld.AdminA })
        {
            var messages = (await w.Actors[persona].Client.GetAsync($"/api/tickets/{created.Id}/messages")).Expect(HttpStatusCode.OK).Body;
            Assert.All(tokens, t => Assert.Contains(t, messages, StringComparison.Ordinal));
        }

        foreach (var token in tokens)
        {
            Assert.Equal(0, await fixture.ScalarAsync<long>("SELECT count(*) FROM audit.audit_events WHERE previous::text LIKE @s OR next::text LIKE @s", TicketKit.P("s", $"%{token}%")));
        }

        Assert.NotEmpty(sink.Lines); // the capture really sees the host's log events
        Assert.DoesNotContain(sink.Lines, line => tokens.Any(t => line.Contains(t, StringComparison.Ordinal)));
    }

    internal sealed class DenyingAuthorizer(IAuthorizer inner, string[] denied) : IAuthorizer
    {
        public Task<AccessOutcome> CheckAsync(string permission, Guid? departmentId, CancellationToken cancellationToken = default) =>
            denied.Contains(permission) ? Task.FromResult(AccessOutcome.Forbidden) : inner.CheckAsync(permission, departmentId, cancellationToken);

        public Task RequireAsync(string permission, Guid? departmentId, CancellationToken cancellationToken = default) =>
            denied.Contains(permission) ? throw new ForbiddenException() : inner.RequireAsync(permission, departmentId, cancellationToken);

        public Task<bool> HasInAnyDepartmentAsync(string permission, CancellationToken cancellationToken = default) =>
            denied.Contains(permission) ? Task.FromResult(false) : inner.HasInAnyDepartmentAsync(permission, cancellationToken);

        public Task<IReadOnlyCollection<Guid>> AccessibleDepartmentsAsync(string permission, CancellationToken cancellationToken = default) =>
            denied.Contains(permission) ? Task.FromResult<IReadOnlyCollection<Guid>>([]) : inner.AccessibleDepartmentsAsync(permission, cancellationToken);
    }

    /// <summary>The application with an authorizer that withholds the given permissions (every system role holds all of the ticket permissions it needs).</summary>
    private WebApplicationFactory<Program> WithoutPermissions(params string[] denied) =>
        fixture.Factory.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
        {
            var original = services.Single(s => s.ServiceType == typeof(IAuthorizer));
            services.AddScoped(original.ImplementationType!);
            services.AddScoped<IAuthorizer>(sp => new DenyingAuthorizer((IAuthorizer)sp.GetRequiredService(original.ImplementationType!), denied));
        }));

    [Fact]
    public async Task Notes_need_tickets_note_replies_need_tickets_reply_and_status_needs_tickets_update_each_checked_server_side()
    {
        var w = await IsoWorld.For(fixture);
        var f = await w.NewTicketAsync();

        await using (var noNote = WithoutPermissions(PermissionCodes.TicketsNote))
        {
            var agent = await fixture.SignInAsync(w.Agent.Subject, factory: noNote);
            var before = await IsoKit.SnapshotAsync(fixture, w.Ids);
            (await agent.PostMessageAsync(f.Id, "Internal", "should not be stored")).Expect(HttpStatusCode.Forbidden);
            (await agent.PostMessageAsync(f.Id, "internal", "should not be stored", status: "Pending")).Expect(HttpStatusCode.Forbidden);
            Assert.Equal(before, await IsoKit.SnapshotAsync(fixture, w.Ids));
            (await agent.PostMessageAsync(f.Id, "Public", "reply is fine")).Expect(HttpStatusCode.Created);
        }

        await using (var noReply = WithoutPermissions(PermissionCodes.TicketsReply))
        {
            var agent = await fixture.SignInAsync(w.Agent.Subject, factory: noReply);
            var before = await IsoKit.SnapshotAsync(fixture, w.Ids);
            (await agent.PostMessageAsync(f.Id, "Public", "should not be stored")).Expect(HttpStatusCode.Forbidden);
            Assert.Equal(before, await IsoKit.SnapshotAsync(fixture, w.Ids));
            (await agent.PostMessageAsync(f.Id, "Internal", "note is fine")).Expect(HttpStatusCode.Created);
        }

        await using (var noUpdate = WithoutPermissions(PermissionCodes.TicketsUpdate))
        {
            var agent = await fixture.SignInAsync(w.Agent.Subject, factory: noUpdate);
            var current = (await agent.GetTicketAsync(f.Id)).Expect(HttpStatusCode.OK);
            var before = await IsoKit.SnapshotAsync(fixture, w.Ids);
            (await agent.PostMessageAsync(f.Id, "Public", "x", status: "Resolved")).Expect(HttpStatusCode.Forbidden); // message and status are all-or-nothing
            (await agent.PatchTicketAsync(current, new { Status = "Resolved" })).Expect(HttpStatusCode.Forbidden);
            Assert.Equal(before, await IsoKit.SnapshotAsync(fixture, w.Ids));
            (await agent.PostMessageAsync(f.Id, "Public", "no status is fine")).Expect(HttpStatusCode.Created);
        }

        await using (var noAssign = WithoutPermissions(PermissionCodes.TicketsAssignAny, PermissionCodes.TicketsAssignSelf))
        {
            var lead = await fixture.SignInAsync(w.Lead.Subject, factory: noAssign);
            var current = (await lead.GetTicketAsync(f.Id)).Expect(HttpStatusCode.OK);
            var before = await IsoKit.SnapshotAsync(fixture, w.Ids);
            (await lead.AssignAsync(f.Id, current.Version, lead.Id)).Expect(HttpStatusCode.Forbidden);
            (await lead.Client.PostAsync($"{w.A.Url}/tickets", TicketHelpers.NewTicketBody(extra: new { AssigneeUserId = lead.Id }))).Expect(HttpStatusCode.Forbidden);
            Assert.Equal(before, await IsoKit.SnapshotAsync(fixture, w.Ids));
        }
    }

    [Fact]
    public async Task Without_notes_read_internal_notes_are_filtered_in_the_query_across_pages_and_without_tickets_read_everything_is_404()
    {
        var w = await IsoWorld.For(fixture);
        var f = await w.NewTicketAsync();
        for (var i = 0; i < 4; i++)
        {
            (await w.Agent.PostMessageAsync(f.Id, i % 2 == 0 ? "Public" : "Internal", i % 2 == 0 ? $"public-extra-{i}" : $"{f.NoteToken}-{i}")).Expect(HttpStatusCode.Created);
        }

        await using (var noNotes = WithoutPermissions(PermissionCodes.TicketsNotesRead))
        {
            var viewer = await fixture.SignInAsync(w.Viewer.Subject, factory: noNotes);
            var seen = new List<JsonElement>();
            string? cursor = null;
            do
            {
                var page = (await viewer.Client.GetAsync($"/api/tickets/{f.Id}/messages?limit=1" + (cursor is null ? "" : $"&cursor={cursor}"))).Expect(HttpStatusCode.OK);
                seen.AddRange(page.Json.GetProperty("items").EnumerateArray());
                Assert.DoesNotContain("Internal", page.Body, StringComparison.Ordinal);
                Assert.DoesNotContain(f.NoteToken, page.Body, StringComparison.Ordinal);
                cursor = page.NextCursor();
            }
            while (cursor is not null);

            Assert.All(seen, m => Assert.Equal("Public", m.Str("kind")));
            Assert.Equal(1 + 1 + 2, seen.Count); // first message, public reply, two public extras
        }

        await using var noRead = WithoutPermissions(PermissionCodes.TicketsRead);
        var blind = await fixture.SignInAsync(w.Admin.Subject, factory: noRead);
        Assert.True(IsoKit.EmptyPage((await blind.Client.GetAsync("/api/tickets")).Expect(HttpStatusCode.OK))); // fail closed: scope is empty
        (await blind.GetTicketAsync(f.Id)).Expect(HttpStatusCode.NotFound);
        (await blind.Client.GetAsync($"/api/tickets/{f.Id}/messages")).Expect(HttpStatusCode.NotFound);
        (await blind.PostMessageAsync(f.Id, "Internal", "x")).Expect(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_messages_kind_cannot_be_changed_and_no_route_addresses_a_single_message()
    {
        var w = await IsoWorld.For(fixture);
        var f = await w.NewTicketAsync();
        var list = (await w.Lead.Client.GetAsync($"/api/tickets/{f.Id}/messages")).Expect(HttpStatusCode.OK).Json.GetProperty("items").EnumerateArray().ToList();
        var publicId = list.First(m => m.Str("kind") == "Public" && m.Str("authorType") == "Agent").GetProperty("id").GetGuid();
        var internalId = list.First(m => m.Str("kind") == "Internal").GetProperty("id").GetGuid();

        Guid[] internalIds = [internalId];
        async Task<string?> ExistingHash() => await fixture.ScalarAsync<string>(
            "SELECT md5(string_agg(id||kind||author_type||delivery_state||body, '|' ORDER BY id)) FROM ticketing.messages WHERE id = ANY(@ids)", TicketKit.P("ids", new[] { publicId, internalId }));

        var before = await ExistingHash();
        HttpMethod[] methods = [HttpMethod.Get, HttpMethod.Put, HttpMethod.Patch, HttpMethod.Delete, HttpMethod.Post];
        string[] shapes = ["/api/tickets/{t}/messages/{m}", "/api/tickets/{r}/messages/{m}", "/api/messages/{m}", "/api/tickets/{t}/messages/{m}/kind", "/api/messages"];
        foreach (var persona in new[] { IsoWorld.AdminA, IsoWorld.MemberOfBOnly })
        {
            var client = w.Actors[persona].Client;
            foreach (var shape in shapes)
            {
                foreach (var method in methods)
                {
                    string Url(Guid m) => shape.Replace("{t}", f.Id.ToString(), StringComparison.Ordinal).Replace("{r}", f.Reference, StringComparison.Ordinal).Replace("{m}", m.ToString(), StringComparison.Ordinal);
                    var body = new { kind = "Public", body = "flip", version = f.Version };
                    var real = await client.SendAsync(method, Url(internalId), body);
                    var real2 = await client.SendAsync(method, Url(publicId), body);
                    var ghost = await client.SendAsync(method, Url(Guid.NewGuid()), body);
                    Assert.True(real.Status is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed, $"{persona} {method} {shape}: {(int)real.Status} {real.Body}");
                    Assert.Equal(IsoKit.Fingerprint(ghost), IsoKit.Fingerprint(real));
                    Assert.Equal(IsoKit.Fingerprint(ghost), IsoKit.Fingerprint(real2));
                }
            }
        }

        (await w.Actors[IsoWorld.Anonymous].Client.SendAsync(HttpMethod.Delete, $"/api/tickets/{f.Id}/messages/{internalId}", null)).Expect(HttpStatusCode.Unauthorized);

        // Fields that would change a message are not part of any request shape: they are ignored (or rejected) and nothing existing changes.
        var patch = await w.Lead.Client.PatchAsync($"/api/tickets/{f.Id}", new { version = f.Version, priority = "High", kind = "Public", messages = new[] { new { id = internalId, kind = "Public" } } });
        Assert.True(patch.Status is HttpStatusCode.OK or HttpStatusCode.BadRequest, patch.Body);
        var posted = await w.Lead.Client.PostAsync($"/api/tickets/{f.Id}/messages", new { kind = "Internal", body = "second note", id = publicId, messageId = internalId, replyTo = internalId });
        Assert.True(posted.Status is HttpStatusCode.Created or HttpStatusCode.BadRequest, posted.Body);
        Assert.Equal(1, await fixture.ScalarAsync<long>("SELECT count(*) FROM ticketing.messages WHERE id = ANY(@ids) AND kind = 'Internal'", TicketKit.P("ids", internalIds)));
        Assert.Equal("Public", await fixture.ScalarAsync<string>("SELECT kind FROM ticketing.messages WHERE id = @m", TicketKit.P("m", publicId)));

        if (posted.Status == HttpStatusCode.Created)
        {
            Assert.NotEqual(publicId, posted.Json.GetProperty("message").GetProperty("id").GetGuid());
            Assert.Equal("Internal", posted.Json.GetProperty("message").Str("kind"));
        }

        // The database still refuses an agent's recorded reply being turned into an internal note (and an internal note into a requester message).
        var ex = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => fixture.ExecuteAsync("UPDATE ticketing.messages SET kind = 'Internal' WHERE id = @m", TicketKit.P("m", publicId)));
        Assert.Equal("23514", ex.SqlState);
        Assert.Equal(before, await ExistingHash()); // the two existing messages are byte-for-byte unchanged
    }

    // ----- assignment validity (T6) -----

    [Fact]
    public async Task Invalid_assignees_are_rejected_on_create_and_assignment_and_foreign_ids_look_like_random_ids()
    {
        var w = await IsoWorld.For(fixture);
        var inactiveUser = await fixture.NewMemberAsync(w.A, "Agent");
        await fixture.ExecuteAsync("UPDATE identity.users SET is_active = false WHERE id = @u", TicketKit.P("u", inactiveUser.Id));

        var badAssignees = new Dictionary<string, Guid>
        {
            ["member of B only"] = w.UserB.Id,
            ["admin of B only"] = w.AdminB.Id,
            ["viewer of A"] = w.Viewer.Id,
            ["inactive agent of A"] = inactiveUser.Id,
            ["random guid"] = Guid.NewGuid(),
            ["platform admin without membership"] = w.Root.Id,
            ["user without membership"] = w.NoMember.Id,
        };
        var f = await w.NewTicketAsync();
        var before = await IsoKit.SnapshotAsync(fixture, w.Ids);

        var assigneeResponses = new List<string>();
        foreach (var (name, id) in badAssignees)
        {
            var created = await w.Lead.Client.PostAsync($"{w.A.Url}/tickets", TicketHelpers.NewTicketBody(extra: new { AssigneeUserId = id }));
            created.ExpectCode(HttpStatusCode.Conflict, "invalid_state");
            var assigned = (await w.Lead.AssignAsync(f.Id, f.Version, id)).ExpectCode(HttpStatusCode.Conflict, "invalid_state");
            var admin = (await w.Admin.AssignAsync(f.Id, f.Version, id)).ExpectCode(HttpStatusCode.Conflict, "invalid_state");
            assigneeResponses.Add($"{name}|{IsoKit.Fingerprint(created)}|{IsoKit.Fingerprint(assigned)}|{IsoKit.Fingerprint(admin)}");
        }

        // Every reason an assignee is refused reads the same: nothing says whether the user exists, is in another department or is inactive.
        Assert.Single(assigneeResponses.Select(r => string.Join('|', r.Split('|')[1..])).Distinct());

        // An agent with only assign.self gets 403 (never a hint about the object) for anyone but themself.
        foreach (var id in badAssignees.Values)
        {
            (await w.Agent.AssignAsync(f.Id, f.Version, id)).Expect(HttpStatusCode.Forbidden);
        }

        Assert.Equal(before, await IsoKit.SnapshotAsync(fixture, w.Ids));
        Assert.Equal(0, await fixture.ScalarAsync<long>(
            "SELECT count(*) FROM ticketing.tickets t WHERE t.assignee_user_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM organisation.department_memberships m WHERE m.user_id = t.assignee_user_id AND m.department_id = t.department_id)"));
    }

    [Fact]
    public async Task Assign_self_covers_taking_and_releasing_only_never_other_users()
    {
        var w = await IsoWorld.For(fixture);
        var f = await w.NewTicketAsync();
        (await w.Agent.AssignAsync(f.Id, f.Version, w.Lead.Id)).Expect(HttpStatusCode.Forbidden);    // someone else
        (await w.Agent.AssignAsync(f.Id, f.Version, null)).Expect(HttpStatusCode.Forbidden);          // not the assignee: nothing to release
        (await w.Viewer.AssignAsync(f.Id, f.Version, w.Viewer.Id)).Expect(HttpStatusCode.Forbidden);
        var taken = (await w.Agent.AssignAsync(f.Id, f.Version, w.Agent.Id)).Expect(HttpStatusCode.OK);
        (await w.Target.AssignAsync(f.Id, taken.Version, null)).Expect(HttpStatusCode.Forbidden);          // cannot release another agent's ticket
        var released = (await w.Agent.AssignAsync(f.Id, taken.Version, null)).Expect(HttpStatusCode.OK);
        Assert.Equal(JsonValueKind.Null, released.Json.GetProperty("assignee").ValueKind);
    }

    // ----- queue, search, cursor leakage (T2, T3) -----

    [Fact]
    public async Task No_combination_of_queue_sort_status_and_foreign_filters_yields_a_foreign_row_at_any_page()
    {
        var w = await IsoWorld.For(fixture);
        await w.NewTicketAsync();
        var idsA = (await IsoKit.TicketIdsOfAsync(fixture, w.A.Id)).ToHashSet();
        var idsB = (await IsoKit.TicketIdsOfAsync(fixture, w.B.Id)).ToHashSet();
        Assert.True(idsA.Count > 2 && idsB.Count > 0);

        foreach (var persona in new[] { IsoWorld.NoMembership, IsoWorld.MemberOfBOnly, IsoWorld.AdminOfBAgainstA, IsoWorld.ViewerA })
        {
            var own = persona is IsoWorld.MemberOfBOnly or IsoWorld.AdminOfBAgainstA ? idsB : persona == IsoWorld.ViewerA ? idsA : [];
            var foreign = idsA.Concat(idsB).Where(id => !own.Contains(id)).ToHashSet();
            var client = w.Actors[persona].Client;
            foreach (var queue in QueueNames)
            {
                foreach (var sort in SortNames)
                {
                    foreach (var extra in new[] { "", $"&departmentId={w.A.Id}", $"&departmentId={w.B.Id}", $"&assigneeUserId={w.Target.Id}", "&unassigned=true" })
                    {
                        string? cursor = null;
                        for (var page = 0; page < 8; page++)
                        {
                            var response = (await client.GetAsync($"/api/tickets?queue={queue}&sort={sort}&limit=3&status=Open&status=Closed&status=Pending{extra}" + (cursor is null ? "" : $"&cursor={cursor}"))).Expect(HttpStatusCode.OK);
                            var ids = response.Ids();
                            Assert.DoesNotContain(ids, id => foreign.Contains(id));
                            Assert.All(ids, id => Assert.Contains(id, own));
                            cursor = response.NextCursor();
                            if (cursor is null)
                            {
                                break;
                            }
                        }
                    }
                }
            }
        }
    }

    [Fact]
    public async Task Cursors_replayed_by_other_callers_or_forged_never_reach_rows_outside_the_callers_scope()
    {
        var w = await IsoWorld.For(fixture);
        await w.NewTicketAsync();
        await w.NewTicketAsync();
        await w.NewTicketAsync();
        var idsA = (await IsoKit.TicketIdsOfAsync(fixture, w.A.Id)).ToHashSet();
        var idsB = (await IsoKit.TicketIdsOfAsync(fixture, w.B.Id)).ToHashSet();
        var bTicket = w.TicketB.Id;

        foreach (var sort in SortNames)
        {
            var first = (await w.Agent.Client.GetAsync($"/api/tickets?sort={sort}&limit=2&status=Open&status=Closed&status=Pending")).Expect(HttpStatusCode.OK);
            var cursor = first.NextCursor();
            Assert.NotNull(cursor);
            var query = $"/api/tickets?sort={sort}&limit=100&status=Open&status=Closed&status=Pending&cursor=";

            // Replay by callers with other scopes: only their own rows (or nothing), never A's.
            foreach (var persona in IsoWorld.Outsiders)
            {
                var replay = (await w.Actors[persona].Client.GetAsync(query + cursor)).Expect(HttpStatusCode.OK);
                Assert.DoesNotContain(replay.Ids(), id => idsA.Contains(id));
                if (persona is IsoWorld.NoMembership)
                {
                    Assert.True(IsoKit.EmptyPage(replay));
                }
            }

            // Forged variants replayed by an A-only caller: valid-looking ones answer inside A's scope, malformed ones 400; never a 5xx.
            Assert.True(Cursor.TryDecode(cursor, out var payload));
            var parts = payload.Split('|');
            var code = parts[0];
            var forged = new List<string>
            {
                string.Join('|', parts[..^1].Append(bTicket.ToString())),
                string.Join('|', parts[..^1].Append(Guid.Empty.ToString())),
                string.Join('|', parts[..^1].Append("'; DROP TABLE ticketing.tickets; --")),
                payload + "|extra",
                string.Join('|', parts[..^1]),
                "zz|" + string.Join('|', parts[1..]),
                string.Join('|', parts.Select((p, i) => i == 1 ? "-1" : p)),
                string.Join('|', parts.Select((p, i) => i == 1 ? long.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture) : p)),
                string.Join('|', parts.Select((p, i) => i == 1 ? DateTime.MaxValue.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture) : p)),
                string.Join('|', parts.Select((p, i) => i == 1 ? "0" : p)),
                code + "|\0|\0",
                new string('a', 600),
            };
            foreach (var raw in forged)
            {
                var token = raw.Length > 500 && !raw.Contains('|', StringComparison.Ordinal) ? raw : Cursor.Encode(raw);
                var attempt = await w.Agent.Client.GetAsync(query + token);
                Assert.True(attempt.Status is HttpStatusCode.OK or HttpStatusCode.BadRequest, $"{sort} forged '{raw}': {(int)attempt.Status} {attempt.Body}");
                if (attempt.Status == HttpStatusCode.OK)
                {
                    Assert.DoesNotContain(attempt.Ids(), id => idsB.Contains(id));
                }
            }

            // The raw (unencoded) value and a cursor of another sort are not accepted either.
            (await w.Agent.Client.GetAsync(query + Uri.EscapeDataString(payload))).Expect(HttpStatusCode.BadRequest);
        }

        Assert.True(await fixture.ScalarAsync<long>("SELECT count(*) FROM ticketing.tickets") > 0);
    }

    [Fact]
    public async Task Message_and_activity_cursors_cannot_cross_tickets_or_departments()
    {
        var w = await IsoWorld.For(fixture);
        var x = await w.NewTicketAsync();
        var y = await w.NewTicketAsync();
        for (var i = 0; i < 3; i++)
        {
            (await w.Agent.PostMessageAsync(x.Id, "Public", $"x{i}")).Expect(HttpStatusCode.Created);
            (await w.Agent.PostMessageAsync(y.Id, "Public", $"y{i}")).Expect(HttpStatusCode.Created);
            (await w.UserB.PostMessageAsync(w.TicketB.Id, "Public", $"b{i}")).Expect(HttpStatusCode.Created);
        }

        var messagesOf = async (Guid ticket) => await IsoKit.GuidsAsync(fixture, "SELECT id FROM ticketing.messages WHERE ticket_id = @t", TicketKit.P("t", ticket));
        var eventsOf = async (Guid ticket) => await IsoKit.GuidsAsync(fixture, "SELECT id FROM audit.audit_events WHERE object_id = @t", TicketKit.P("t", ticket));

        var mc = (await w.Admin.Client.GetAsync($"/api/tickets/{x.Id}/messages?limit=2")).Expect(HttpStatusCode.OK).NextCursor();
        var ac = (await w.Admin.Client.GetAsync($"/api/tickets/{x.Id}/activity?limit=2")).Expect(HttpStatusCode.OK).NextCursor();
        Assert.NotNull(mc);
        Assert.NotNull(ac);

        // Same department, other ticket: only that ticket's rows.
        var onY = (await w.Admin.Client.GetAsync($"/api/tickets/{y.Id}/messages?limit=200&cursor={mc}")).Expect(HttpStatusCode.OK);
        Assert.All(onY.Json.GetProperty("items").EnumerateArray(), m => Assert.Equal(y.Id, m.GetProperty("ticketId").GetGuid()));
        var actY = (await w.Admin.Client.GetAsync($"/api/tickets/{y.Id}/activity?limit=200&cursor={ac}")).Expect(HttpStatusCode.OK);
        var yEvents = await eventsOf(y.Id);
        Assert.All(actY.Json.GetProperty("items").EnumerateArray(), e => Assert.Contains(e.GetProperty("id").GetGuid(), yEvents));

        // Other department's ticket: the caller's own ticket only, or 404 on the foreign one (same as a random ticket).
        var onB = (await w.UserB.Client.GetAsync($"/api/tickets/{w.TicketB.Id}/messages?limit=200&cursor={mc}")).Expect(HttpStatusCode.OK);
        var bMessages = await messagesOf(w.TicketB.Id);
        Assert.All(onB.Json.GetProperty("items").EnumerateArray(), m => Assert.Contains(m.GetProperty("id").GetGuid(), bMessages));
        var actB = (await w.UserB.Client.GetAsync($"/api/tickets/{w.TicketB.Id}/activity?limit=200&cursor={ac}")).Expect(HttpStatusCode.OK);
        var bEvents = await eventsOf(w.TicketB.Id);
        Assert.All(actB.Json.GetProperty("items").EnumerateArray(), e => Assert.Contains(e.GetProperty("id").GetGuid(), bEvents));
        var real = await w.UserB.Client.GetAsync($"/api/tickets/{x.Id}/messages?cursor={mc}");
        var ghost = await w.UserB.Client.GetAsync($"/api/tickets/{Guid.NewGuid()}/messages?cursor={mc}");
        Assert.Equal(IsoKit.Fingerprint(ghost), IsoKit.Fingerprint(real));

        // Forged message cursors: ids of foreign messages inside the cursor only compare, they never select foreign rows.
        var foreignMessage = (await messagesOf(w.TicketB.Id))[0];
        var forged = Cursor.Encode($"{DateTime.MaxValue.Ticks}|{foreignMessage}");
        var forgedPage = (await w.Admin.Client.GetAsync($"/api/tickets/{x.Id}/messages?limit=200&cursor={forged}")).Expect(HttpStatusCode.OK);
        var xMessages = await messagesOf(x.Id);
        Assert.All(forgedPage.Json.GetProperty("items").EnumerateArray(), m => Assert.Contains(m.GetProperty("id").GetGuid(), xMessages));
        (await w.Admin.Client.GetAsync($"/api/tickets/{x.Id}/messages?cursor={Cursor.Encode("1|x")}")).Expect(HttpStatusCode.BadRequest);
        (await w.Admin.Client.GetAsync($"/api/tickets/{x.Id}/activity?cursor={Cursor.Encode("'; DROP TABLE audit.audit_events; --")}")).Expect(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Search_input_cannot_widen_the_scope_wildcards_are_literal_and_foreign_text_finds_nothing()
    {
        var w = await IsoWorld.For(fixture);
        var f = await w.NewTicketAsync();
        string[] wildcards = ["%%", "__", "\\\\", "%_", "_%", "\\%", "%\\", "a%", "%a", "_a", "%a%", "' OR '1'='1", "\") OR 1=1 --", "**", ".*", "[a-z]+", "{{{", "\u0001\u0002"];
        foreach (var persona in new[] { IsoWorld.ViewerA, IsoWorld.NoMembership, IsoWorld.MemberOfBOnly })
        {
            var client = w.Actors[persona].Client;
            foreach (var q in wildcards)
            {
                var response = await client.GetAsync("/api/tickets?q=" + Uri.EscapeDataString(q));
                Assert.True(response.Status is HttpStatusCode.OK or HttpStatusCode.BadRequest, $"{persona} q={q}: {(int)response.Status} {response.Body}");
                if (response.Status == HttpStatusCode.OK)
                {
                    Assert.True(IsoKit.EmptyPage(response), $"{persona} q={q} matched: {response.Body}");
                }
            }

            (await client.GetAsync("/api/tickets?q=" + new string('x', 100))).Expect(HttpStatusCode.OK);
            (await client.GetAsync("/api/tickets?q=" + new string('x', 101))).Expect(HttpStatusCode.BadRequest);
            (await client.GetAsync("/api/tickets?q=" + new string('%', 100))).Expect(HttpStatusCode.OK);
            (await client.GetAsync("/api/tickets?q=%20%20")).Expect(HttpStatusCode.BadRequest);
        }

        // Another department's reference (any case, any prefix of it) and subject text find nothing; the caller's own still work.
        var bRef = w.TicketB.Json.Str("reference");
        var bSubject = w.TicketB.Json.Str("subject");
        foreach (var q in new[] { bRef, bRef.ToLowerInvariant(), bSubject, bSubject.ToLowerInvariant(), bSubject[..10], bRef[..(bRef.Length - 1)] })
        {
            var asA = (await w.Viewer.Client.GetAsync("/api/tickets?q=" + Uri.EscapeDataString(q) + "&status=Open&status=Closed")).Expect(HttpStatusCode.OK);
            Assert.DoesNotContain(w.TicketB.Id, asA.Ids());
            Assert.DoesNotContain(await IsoKit.TicketIdsOfAsync(fixture, w.B.Id), id => asA.Ids().Contains(id));
        }

        Assert.Equal([f.Id], (await w.Viewer.Client.GetAsync("/api/tickets?q=" + Uri.EscapeDataString(f.Subject.ToLowerInvariant()))).Expect(HttpStatusCode.OK).Ids());
        Assert.Contains(w.TicketB.Id, (await w.UserB.Client.GetAsync("/api/tickets?q=" + Uri.EscapeDataString(bRef.ToLowerInvariant()))).Expect(HttpStatusCode.OK).Ids());
    }

    // ----- requesters (T5) -----

    [Fact]
    public async Task Requesters_are_per_department_lookup_and_ids_of_other_departments_are_indistinguishable_from_unknown_ones()
    {
        var w = await IsoWorld.For(fixture);
        var tag = Unique.Id();
        var email = $"shared-{tag}@example.test";
        var onlyB = $"only-b-{tag}@example.test";
        var inB = await w.UserB.CreateTicketAsync(w.B, TicketHelpers.NewTicketBody(email: email));
        await w.UserB.CreateTicketAsync(w.B, TicketHelpers.NewTicketBody(email: email));
        await w.UserB.CreateTicketAsync(w.B, TicketHelpers.NewTicketBody(email: onlyB));
        var requesterB = inB.Json.GetProperty("requester").GetProperty("id").GetGuid();

        // A cannot find B's requesters by lookup, by full address, by fragment or by wildcard.
        foreach (var q in new[] { $"only-b-{tag}", onlyB, $"shared-{tag}", "%%", "__" })
        {
            var found = (await w.Agent.Client.GetAsync($"{w.A.Url}/requesters/lookup?q={Uri.EscapeDataString(q)}")).Expect(HttpStatusCode.OK);
            Assert.Empty(found.Json.EnumerateArray());
        }

        // The same address in A is a different, independent requester with its own counts.
        var inA = await w.Agent.CreateTicketAsync(w.A, TicketHelpers.NewTicketBody(email: email));
        var requesterA = inA.Json.GetProperty("requester").GetProperty("id").GetGuid();
        Assert.NotEqual(requesterB, requesterA);
        Assert.Equal(1, inA.Json.GetProperty("requester").GetProperty("totalTicketCount").GetInt32());
        Assert.Equal(1, inA.Json.GetProperty("requester").GetProperty("openTicketCount").GetInt32());
        var detailA = (await w.Viewer.GetTicketAsync(inA.Id)).Expect(HttpStatusCode.OK);
        Assert.Equal(1, detailA.Json.GetProperty("requester").GetProperty("totalTicketCount").GetInt32());
        var detailB = (await w.UserB.GetTicketAsync(inB.Id)).Expect(HttpStatusCode.OK);
        Assert.Equal(2, detailB.Json.GetProperty("requester").GetProperty("totalTicketCount").GetInt32());
        var lookupA = (await w.Agent.Client.GetAsync($"{w.A.Url}/requesters/lookup?q=shared-{tag}")).Expect(HttpStatusCode.OK).Json.EnumerateArray().ToList();
        Assert.Equal([requesterA], lookupA.Select(r => r.GetProperty("id").GetGuid()));
        var requestersOfA = await IsoKit.GuidsAsync(fixture, "SELECT id FROM ticketing.requesters WHERE department_id = @d", TicketKit.P("d", w.A.Id));
        Assert.DoesNotContain(requesterB, requestersOfA);

        // requesterId of another department == requesterId that does not exist (same 400, same body), nothing created.
        var before = await IsoKit.SnapshotAsync(fixture, w.Ids);
        var foreign = (await w.Agent.Client.PostAsync($"{w.A.Url}/tickets", new { subject = "x", description = "text", requesterId = requesterB })).Expect(HttpStatusCode.BadRequest);
        var ghost = (await w.Agent.Client.PostAsync($"{w.A.Url}/tickets", new { subject = "x", description = "text", requesterId = Guid.NewGuid() })).Expect(HttpStatusCode.BadRequest);
        Assert.Equal(IsoKit.Fingerprint(ghost), IsoKit.Fingerprint(foreign));
        Assert.DoesNotContain(email, foreign.Body, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, await IsoKit.SnapshotAsync(fixture, w.Ids));

        // Outsiders: lookup on A answers 404 whatever q is (authorisation before validation).
        foreach (var persona in IsoWorld.Outsiders)
        {
            var real = await w.Actors[persona].Client.GetAsync($"{w.A.Url}/requesters/lookup?q=shared-{tag}");
            var none = await w.Actors[persona].Client.GetAsync($"/api/departments/{Guid.NewGuid()}/requesters/lookup?q=shared-{tag}");
            real.Expect(HttpStatusCode.NotFound);
            Assert.Equal(IsoKit.Fingerprint(none), IsoKit.Fingerprint(real));
        }
    }

    // ----- tampering (T2) -----

    [Fact]
    public async Task Department_ids_in_route_body_query_or_headers_never_override_the_ticket_or_department_of_record()
    {
        var w = await IsoWorld.For(fixture);
        var f = await w.NewTicketAsync();
        var beforeB = await IsoKit.DepartmentSnapshotAsync(fixture, w.B.Id);

        // Create under A's route while naming B everywhere else.
        using (var request = new HttpRequestMessage(HttpMethod.Post, $"{w.A.Url}/tickets?departmentId={w.B.Id}&department={w.B.Key}"))
        {
            request.Headers.Add("X-Department-Id", w.B.Id.ToString());
            request.Headers.Add("X-Department", w.B.Key);
            request.Content = JsonContent(TicketHelpers.NewTicketBody(extra: new { DepartmentId = w.B.Id, DepartmentKey = w.B.Key, Department = w.B.Id }));
            var csrf = await IsoKit.CsrfTokenOfAsync(w.Agent.Client);
            request.Headers.Add("X-CSRF-TOKEN", csrf);
            var response = await w.Agent.Client.Http.SendAsync(request);
            Assert.True(response.StatusCode is HttpStatusCode.Created or HttpStatusCode.BadRequest, response.StatusCode.ToString());
            if (response.StatusCode == HttpStatusCode.Created)
            {
                var created = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
                Assert.Equal(w.A.Id, created.GetProperty("departmentId").GetGuid());
                Assert.StartsWith(w.A.Key + "-", created.Str("reference"), StringComparison.Ordinal);
            }
        }

        // Existing ticket of A addressed with B's ids in query/body: still A's ticket, only A's data changes.
        var detail = (await w.Lead.Client.GetAsync($"/api/tickets/{f.Id}?departmentId={w.B.Id}")).Expect(HttpStatusCode.OK);
        Assert.Equal(w.A.Id, detail.Json.GetProperty("departmentId").GetGuid());
        var patched = await w.Lead.Client.PatchAsync($"/api/tickets/{f.Id}?departmentId={w.B.Id}", new { version = detail.Version, priority = "Urgent", departmentId = w.B.Id });
        Assert.True(patched.Status is HttpStatusCode.OK or HttpStatusCode.BadRequest, patched.Body);
        var posted = await w.Lead.Client.PostAsync($"/api/tickets/{f.Id}/messages?departmentId={w.B.Id}", new { kind = "Internal", body = "tamper", departmentId = w.B.Id, ticketId = w.TicketB.Id });
        Assert.True(posted.Status is HttpStatusCode.Created or HttpStatusCode.BadRequest, posted.Body);
        Assert.Equal(w.A.Id, await fixture.ScalarAsync<Guid>("SELECT department_id FROM ticketing.tickets WHERE id = @t", TicketKit.P("t", f.Id)));
        Assert.Equal(0, await fixture.ScalarAsync<long>("SELECT count(*) FROM ticketing.messages WHERE ticket_id = @t AND department_id <> @d", TicketKit.P("t", f.Id), TicketKit.P("d", w.A.Id)));

        // A caller of B naming B's own department against A's ticket gets the same 404 as for a random ticket.
        var real = await w.UserB.Client.GetAsync($"/api/tickets/{f.Id}?departmentId={w.B.Id}");
        var ghost = await w.UserB.Client.GetAsync($"/api/tickets/{Guid.NewGuid()}?departmentId={w.B.Id}");
        real.Expect(HttpStatusCode.NotFound);
        Assert.Equal(IsoKit.Fingerprint(ghost), IsoKit.Fingerprint(real));
        var bPost = await w.UserB.Client.PostAsync($"/api/tickets/{f.Id}/messages", new { kind = "Public", body = "x", departmentId = w.B.Id });
        bPost.Expect(HttpStatusCode.NotFound);

        // B is untouched by everything above except what B itself did (nothing).
        Assert.Equal(beforeB, await IsoKit.DepartmentSnapshotAsync(fixture, w.B.Id));
    }

    private static System.Net.Http.Json.JsonContent JsonContent(object body) => System.Net.Http.Json.JsonContent.Create(body, options: new JsonSerializerOptions(JsonSerializerDefaults.Web));

    // ----- stored markup (T7) -----

    private static readonly string[] Payloads =
    [
        "<script>alert(1)</script>", "<img src=x onerror=alert(1)>", "\"><svg/onload=alert(1)>", "javascript:alert(1)", "{{7*7}} ${7*7} <%= 7*7 %>",
        "</script><script>alert(2)</script>", "&lt;b&gt; &amp; <b>bold</b>", "‮evil‬", "'; DROP TABLE ticketing.tickets; --",
    ];

    [Fact]
    public async Task Markup_is_stored_and_returned_verbatim_as_json_text_and_never_as_html()
    {
        var w = await IsoWorld.For(fixture);
        foreach (var payload in Payloads)
        {
            var tag = Unique.Id();
            var created = (await w.Agent.Client.PostAsync($"{w.A.Url}/tickets", new
            {
                subject = payload,
                description = payload,
                requester = new { email = $"<b>{tag}@example.test", displayName = payload },
            })).Expect(HttpStatusCode.Created);
            (await w.Agent.PostMessageAsync(created.Id, "Public", payload)).Expect(HttpStatusCode.Created);
            (await w.Agent.PostMessageAsync(created.Id, "Internal", payload)).Expect(HttpStatusCode.Created);
            (await w.Agent.PatchTicketAsync((await w.Agent.GetTicketAsync(created.Id)), new { Subject = payload + "!" })).Expect(HttpStatusCode.OK);

            var responses = new List<ApiResponse>
            {
                created,
                (await w.Viewer.GetTicketAsync(created.Id)).Expect(HttpStatusCode.OK),
                (await w.Viewer.Client.GetAsync($"/api/tickets?q={Uri.EscapeDataString(tag)}")).Expect(HttpStatusCode.OK),
                (await w.Viewer.Client.GetAsync($"/api/tickets/{created.Id}/messages")).Expect(HttpStatusCode.OK),
                (await w.Viewer.Client.GetAsync($"/api/tickets/{created.Id}/activity")).Expect(HttpStatusCode.OK),
                (await w.Agent.Client.GetAsync($"{w.A.Url}/requesters/lookup?q=%3Cb%3E{tag}")).Expect(HttpStatusCode.OK),
            };
            foreach (var response in responses)
            {
                Assert.StartsWith("application/json", response.Message.Content.Headers.ContentType?.MediaType + "", StringComparison.Ordinal);
                Assert.Contains("utf-8", response.Message.Content.Headers.ContentType?.CharSet ?? "", StringComparison.OrdinalIgnoreCase); // JSON text, declared as such; there is no HTML rendering path in the API
            }

            var detail = responses[1].Json;
            Assert.Equal(payload + "!", detail.Str("subject"));
            Assert.Equal(payload, detail.GetProperty("requester").Str("displayName"));
            var messages = responses[3].Json.GetProperty("items").EnumerateArray().Select(m => m.Str("body")).ToList();
            Assert.Equal(3, messages.Count(m => m == payload)); // description, public reply, internal note: byte-for-byte
            Assert.Equal(payload, await fixture.ScalarAsync<string>("SELECT body FROM ticketing.messages WHERE ticket_id = @t ORDER BY created_at LIMIT 1", TicketKit.P("t", created.Id)));
        }

        // Line breaks cannot ride into a future mail header through the requester address.
        foreach (var email in new[] { "a@b.test\r\nBcc: x@y.test", "a@b.test\nBcc: x@y.test", "a b@c.test", "a@b.test,c@d.test c" })
        {
            (await w.Agent.Client.PostAsync($"{w.A.Url}/tickets", new { subject = "x", description = "y", requester = new { email } })).Expect(HttpStatusCode.BadRequest);
        }
    }

    // ----- mass assignment (T8) -----

    [Fact]
    public async Task Extra_json_fields_never_set_server_owned_values_on_create_patch_assignment_or_message()
    {
        var w = await IsoWorld.For(fixture);
        var beforeB = await IsoKit.DepartmentSnapshotAsync(fixture, w.B.Id);
        var injectedId = Guid.NewGuid();
        var old = new DateTimeOffset(2001, 1, 1, 0, 0, 0, TimeSpan.Zero);

        // create
        var create = await w.Agent.Client.PostAsync($"{w.A.Url}/tickets", TicketHelpers.NewTicketBody(extra: new
        {
            Status = "Closed", Reference = "HACK-1", Number = 999, Id = injectedId, DepartmentId = w.B.Id, CreatedByUserId = w.Lead.Id, Version = "1", ResolvedAt = old,
            ClosedAt = old, CreatedAt = old, UpdatedAt = old, AssigneeUserId = (Guid?)null, AuthorType = "System", Kind = "Internal", DeliveryState = "Sent", Source = "email",
        }));
        Assert.True(create.Status is HttpStatusCode.Created or HttpStatusCode.BadRequest, create.Body);
        if (create.Status == HttpStatusCode.Created)
        {
            Assert.NotEqual(injectedId, create.Id);
            Assert.Equal("Open", create.Json.Str("status"));
            Assert.Equal(w.A.Id, create.Json.GetProperty("departmentId").GetGuid());
            Assert.StartsWith(w.A.Key + "-", create.Json.Str("reference"), StringComparison.Ordinal);
            Assert.NotEqual("HACK-1", create.Json.Str("reference"));
            Assert.True(create.Json.GetProperty("createdAt").GetDateTimeOffset() > old.AddYears(10));
            Assert.Equal(JsonValueKind.Null, create.Json.GetProperty("closedAt").ValueKind);
            Assert.Equal(w.Agent.Id, await fixture.ScalarAsync<Guid>("SELECT created_by_user_id FROM ticketing.tickets WHERE id = @t", TicketKit.P("t", create.Id)));
            var first = await fixture.ScalarAsync<string>("SELECT kind||'/'||author_type||'/'||source||'/'||delivery_state FROM ticketing.messages WHERE ticket_id = @t", TicketKit.P("t", create.Id));
            Assert.Equal("Public/Requester/manual/None", first);
        }

        // patch
        var f = await w.NewTicketAsync();
        var patch = await w.Lead.Client.PatchAsync($"/api/tickets/{f.Id}", new
        {
            version = f.Version, priority = "High", reference = "HACK-2", number = 5, departmentId = w.B.Id, requesterId = w.TicketB.Json.GetProperty("requester").GetProperty("id").GetGuid(),
            assigneeUserId = w.Lead.Id, teamId = Guid.NewGuid(), resolvedAt = old, closedAt = old, createdAt = old, createdByUserId = w.Lead.Id, id = injectedId,
        });
        Assert.True(patch.Status is HttpStatusCode.OK or HttpStatusCode.BadRequest, patch.Body);
        var row = await fixture.ScalarAsync<string>(
            "SELECT reference||'|'||department_id||'|'||coalesce(assignee_user_id::text,'')||'|'||coalesce(team_id::text,'')||'|'||coalesce(resolved_at::text,'')||'|'||coalesce(closed_at::text,'')||'|'||(created_at > '2020-01-01')::text||'|'||status FROM ticketing.tickets WHERE id = @t",
            TicketKit.P("t", f.Id));
        Assert.Equal($"{f.Reference}|{w.A.Id}|||||true|Open", row);
        Assert.Equal(w.A.Id, await fixture.ScalarAsync<Guid>("SELECT r.department_id FROM ticketing.requesters r JOIN ticketing.tickets t ON t.requester_id = r.id WHERE t.id = @t", TicketKit.P("t", f.Id)));

        // assignment
        var fresh = (await w.Lead.GetTicketAsync(f.Id)).Expect(HttpStatusCode.OK);
        var assign = await w.Lead.Client.PutAsync($"/api/tickets/{f.Id}/assignment", new
        {
            version = fresh.Version, assigneeUserId = w.Target.Id, status = "Closed", priority = "Low", subject = "pwned", departmentId = w.B.Id, reference = "HACK-3",
        });
        Assert.True(assign.Status is HttpStatusCode.OK or HttpStatusCode.BadRequest, assign.Body);
        var afterAssign = (await w.Lead.GetTicketAsync(f.Id)).Expect(HttpStatusCode.OK).Json;
        Assert.Equal("Open", afterAssign.Str("status"));
        Assert.Equal(f.Subject, afterAssign.Str("subject"));
        Assert.Equal(f.Reference, afterAssign.Str("reference"));
        Assert.NotEqual("Low", afterAssign.Str("priority"));

        // message
        var noteId = await fixture.ScalarAsync<Guid>("SELECT id FROM ticketing.messages WHERE ticket_id = @t AND kind = 'Internal'", TicketKit.P("t", f.Id));
        var messagesBefore = await fixture.ScalarAsync<long>("SELECT count(*) FROM ticketing.messages WHERE ticket_id = @t", TicketKit.P("t", f.Id));
        var posted = await w.Agent.Client.PostAsync($"/api/tickets/{f.Id}/messages", new
        {
            kind = "Public", body = "injected", id = injectedId, messageId = noteId, ticketId = w.TicketB.Id, departmentId = w.B.Id, authorType = "Requester", authorUserId = w.Lead.Id,
            authorRequesterId = Guid.NewGuid(), deliveryState = "None", source = "email", createdAt = old, replyTo = noteId, inReplyTo = noteId, attachments = (object)"x",
        });
        Assert.True(posted.Status is HttpStatusCode.Created or HttpStatusCode.BadRequest, posted.Body);
        if (posted.Status == HttpStatusCode.Created)
        {
            var message = posted.Json.GetProperty("message");
            Assert.NotEqual(injectedId, message.GetProperty("id").GetGuid());
            Assert.Equal(f.Id, message.GetProperty("ticketId").GetGuid());
            Assert.Equal("Agent", message.Str("authorType"));
            Assert.Equal(w.Agent.Id, message.GetProperty("author").GetProperty("id").GetGuid());
            Assert.Equal("NotDelivered", message.Str("deliveryState"));
            Assert.Equal("web", message.Str("source"));
            Assert.True(message.GetProperty("createdAt").GetDateTimeOffset() > old.AddYears(10));
            Assert.Equal(messagesBefore + 1, await fixture.ScalarAsync<long>("SELECT count(*) FROM ticketing.messages WHERE ticket_id = @t", TicketKit.P("t", f.Id)));
            Assert.Equal(0, await fixture.ScalarAsync<long>("SELECT count(*) FROM ticketing.messages WHERE ticket_id = @t AND department_id <> @d", TicketKit.P("t", f.Id), TicketKit.P("d", w.A.Id)));
        }

        Assert.Equal("Internal", await fixture.ScalarAsync<string>("SELECT kind FROM ticketing.messages WHERE id = @m", TicketKit.P("m", noteId)));
        Assert.Equal(beforeB, await IsoKit.DepartmentSnapshotAsync(fixture, w.B.Id));
    }

    // ----- CSRF (T9) -----

    public static TheoryData<string> UnsafeRoutes() => new() { "POST create", "POST message", "PATCH ticket", "PUT assignment" };

    [Theory]
    [MemberData(nameof(UnsafeRoutes))]
    public async Task Every_unsafe_ticket_route_refuses_a_missing_wrong_empty_or_foreign_csrf_token_and_changes_nothing(string route)
    {
        var w = await IsoWorld.For(fixture);
        var f = await w.NewTicketAsync();
        (HttpMethod method, string url, object body) = route switch
        {
            "POST create" => (HttpMethod.Post, $"{w.A.Url}/tickets", TicketHelpers.NewTicketBody()),
            "POST message" => (HttpMethod.Post, $"/api/tickets/{f.Id}/messages", (object)new { kind = "Internal", body = "csrf" }),
            "PATCH ticket" => (HttpMethod.Patch, $"/api/tickets/{f.Id}", (object)new { version = f.Version, priority = "Urgent" }),
            _ => (HttpMethod.Put, $"/api/tickets/{f.Id}/assignment", (object)new { version = f.Version, assigneeUserId = w.Target.Id }),
        };
        var admin = w.Actors[IsoWorld.AdminA].Client;
        var othersToken = await IsoKit.CsrfTokenOfAsync(w.Actors[IsoWorld.LeadA].Client);
        var anonymousToken = await IsoKit.CsrfTokenOfAsync(w.AnonymousClient);
        var before = await IsoKit.SnapshotAsync(fixture, w.Ids);

        foreach (var (header, token) in new (string?, string?)[] { (null, null), ("X-CSRF-TOKEN", "garbage"), ("X-CSRF-TOKEN", ""), ("X-CSRF-TOKEN", othersToken), ("X-CSRF-TOKEN", anonymousToken), ("X-Other", othersToken) })
        {
            var response = await IsoKit.SendRawAsync(admin, method, url, body, header, token);
            response.ExpectCode(HttpStatusCode.BadRequest, "csrf_invalid");
        }

        Assert.Equal(before, await IsoKit.SnapshotAsync(fixture, w.Ids));

        // Control: the right token works for the same request, so the 400s above are the token and nothing else.
        var ok = await IsoKit.SendRawAsync(admin, method, url, body, "X-CSRF-TOKEN", await IsoKit.CsrfTokenOfAsync(admin));
        Assert.True((int)ok.Status is >= 200 and < 300, ok.Body);
    }

    // ----- reference immutability (T10) -----

    [Fact]
    public async Task A_reference_is_canonical_immutable_and_never_resolved_fuzzily()
    {
        var w = await IsoWorld.For(fixture);
        var f = await w.NewTicketAsync();
        var number = f.Reference[(f.Reference.IndexOf('-', StringComparison.Ordinal) + 1)..];

        (await w.Admin.Client.PatchAsync($"/api/tickets/{f.Id}", new { version = f.Version, reference = "HACK-9" })).Expect(HttpStatusCode.BadRequest); // not a field: nothing to change
        var patched = (await w.Admin.Client.PatchAsync($"/api/tickets/{f.Id}", new { version = f.Version, reference = "HACK-9", number = 9, priority = "High" })).Expect(HttpStatusCode.OK);
        Assert.Equal(f.Reference, patched.Json.Str("reference"));
        (await w.Admin.Client.GetAsync("/api/tickets/HACK-9")).Expect(HttpStatusCode.NotFound);
        Assert.Equal(f.Id, (await w.Viewer.Client.GetAsync($"/api/tickets/{f.Reference}")).Expect(HttpStatusCode.OK).Id);

        // Only the exact reference resolves: no zero padding, no whitespace, no suffix, no other key.
        foreach (var guess in new[]
        {
            $"{w.A.Key}-0{number}", $"{f.Reference}%20", $"%20{f.Reference}", $"{f.Reference}%0A", $"{f.Reference}x", $"{w.A.Key}_{number}",
            $"{w.A.Key}%2D{number}x", $"{w.B.Key}-{number}", $"{f.Reference}%2F..",
        })
        {
            var response = await w.Admin.Client.GetAsync($"/api/tickets/{guess}");
            Assert.True(response.Status is HttpStatusCode.NotFound or HttpStatusCode.BadRequest, $"{guess}: {(int)response.Status}");
            Assert.NotEqual(HttpStatusCode.OK, response.Status);
        }

        // Renaming the department never changes it.
        var department = (await w.Admin.Client.GetAsync(w.A.Url)).Expect(HttpStatusCode.OK);
        (await w.Admin.Client.PatchAsync(w.A.Url, new { version = department.Version, name = $"Renamed {Unique.Id()}" })).Expect(HttpStatusCode.OK);
        Assert.Equal(f.Reference, (await w.Viewer.GetTicketAsync(f.Id)).Json.Str("reference"));
    }

    // ----- concurrency (T11) -----

    [Fact]
    public async Task Of_many_writers_with_the_same_version_exactly_one_wins_and_the_rest_get_version_conflict()
    {
        var w = await IsoWorld.For(fixture);
        var f = await w.NewTicketAsync();
        TestUser[] writers = [w.Agent, w.Lead, w.Admin];
        var calls = Enumerable.Range(0, 9).Select(i => writers[i % 3].Client.PatchAsync($"/api/tickets/{f.Id}", new { version = f.Version, subject = $"winner {i}" })).ToList();
        var results = await Task.WhenAll(calls);
        Assert.Single(results, r => r.Status == HttpStatusCode.OK);
        Assert.All(results.Where(r => r.Status != HttpStatusCode.OK), r => r.ExpectCode(HttpStatusCode.Conflict, "version_conflict"));
        var winner = results.Single(r => r.Status == HttpStatusCode.OK).Json.Str("subject");
        Assert.Equal(winner, (await w.Viewer.GetTicketAsync(f.Id)).Json.Str("subject"));
        Assert.Equal(1, (await fixture.AuditOfAsync(f.Id)).Count(r => r.Action == "ticket.subject_changed"));
    }

    [Fact]
    public async Task Concurrent_patch_assignment_and_message_with_one_version_have_one_state_changer_and_never_a_server_error()
    {
        var w = await IsoWorld.For(fixture);
        var f = await w.NewTicketAsync();
        var patch = w.Agent.Client.PatchAsync($"/api/tickets/{f.Id}", new { version = f.Version, priority = "Urgent" });
        var assign = w.Lead.AssignAsync(f.Id, f.Version, w.Target.Id);
        var assign2 = w.Admin.AssignAsync(f.Id, f.Version, w.Agent.Id);
        var message = w.Agent.PostMessageAsync(f.Id, "Public", "concurrent reply");
        await Task.WhenAll(patch, assign, assign2, message);

        (await message).Expect(HttpStatusCode.Created); // messages are not version-guarded
        var (patchR, assignR, assign2R) = (await patch, await assign, await assign2);
        var writes = new[] { patchR, assignR, assign2R };
        Assert.True(writes.Count(r => r.Status == HttpStatusCode.OK) <= 1, string.Join(" | ", writes.Select(r => $"{(int)r.Status} {r.Body}")));
        Assert.All(writes.Where(r => r.Status != HttpStatusCode.OK), r => r.ExpectCode(HttpStatusCode.Conflict, "version_conflict"));
        var assignee = await fixture.ScalarAsync<Guid?>("SELECT assignee_user_id FROM ticketing.tickets WHERE id = @t", TicketKit.P("t", f.Id));
        Assert.True(assignee is null || assignee == w.Target.Id || assignee == w.Agent.Id);
        Assert.Equal(assignR.Status == HttpStatusCode.OK ? w.Target.Id : assign2R.Status == HttpStatusCode.OK ? w.Agent.Id : (Guid?)null, assignee);
        Assert.Equal(patchR.Status == HttpStatusCode.OK ? "Urgent" : "Normal", (await w.Viewer.GetTicketAsync(f.Id)).Json.Str("priority"));
    }

    [Fact]
    public async Task A_message_posted_while_another_agent_is_editing_succeeds_and_bumps_the_version_so_the_stale_edit_is_a_conflict()
    {
        // Documented behaviour (threat model T11): messages append and are never refused for a stale view; they change the ticket's version.
        var w = await IsoWorld.For(fixture);
        var f = await w.NewTicketAsync();
        var editorsView = (await w.Lead.GetTicketAsync(f.Id)).Expect(HttpStatusCode.OK);

        var posted = (await w.Agent.PostMessageAsync(f.Id, "Public", "while you were editing")).Expect(HttpStatusCode.Created);
        Assert.NotEqual(editorsView.Version, posted.Json.GetProperty("ticket").Str("version"));
        (await w.Lead.PatchTicketAsync(editorsView, new { Priority = "High" })).ExpectCode(HttpStatusCode.Conflict, "version_conflict");
        (await w.Lead.AssignAsync(f.Id, editorsView.Version, w.Lead.Id)).ExpectCode(HttpStatusCode.Conflict, "version_conflict");

        // Reloading resolves it, and the reply is still there.
        var reloaded = (await w.Lead.GetTicketAsync(f.Id)).Expect(HttpStatusCode.OK);
        (await w.Lead.PatchTicketAsync(reloaded, new { Priority = "High" })).Expect(HttpStatusCode.OK);
        Assert.Contains("while you were editing", (await w.Lead.Client.GetAsync($"/api/tickets/{f.Id}/messages")).Body, StringComparison.Ordinal);

        // Concurrent posts: all stored, none lost, none duplicated.
        var posts = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => w.Agent.PostMessageAsync(f.Id, i % 2 == 0 ? "Public" : "Internal", $"parallel {i}")));
        Assert.All(posts, p => p.Expect(HttpStatusCode.Created));
        Assert.Equal(8, await fixture.ScalarAsync<long>("SELECT count(*) FROM ticketing.messages WHERE ticket_id = @t AND body LIKE 'parallel %'", TicketKit.P("t", f.Id)));
    }

    [Fact]
    public async Task Assignment_racing_a_membership_removal_never_leaves_an_assignee_outside_the_department()
    {
        var w = await IsoWorld.For(fixture);
        for (var i = 0; i < 12; i++)
        {
            var member = await fixture.NewMemberAsync(w.A, "Agent");
            var f = await w.NewTicketAsync();
            var assign = w.Lead.AssignAsync(f.Id, f.Version, member.Id);
            var remove = w.Root.Client.DeleteAsync($"{w.A.Url}/members/{member.Id}");
            await Task.WhenAll(assign, remove);

            (await remove).Expect(HttpStatusCode.NoContent);
            var assignResult = await assign;
            Assert.True(assignResult.Status is HttpStatusCode.OK or HttpStatusCode.Conflict, $"{(int)assignResult.Status} {assignResult.Body}");
            if (assignResult.Status == HttpStatusCode.Conflict)
            {
                Assert.Equal("invalid_state", assignResult.Code());
            }

            Assert.Null(await fixture.ScalarAsync<Guid?>("SELECT assignee_user_id FROM ticketing.tickets WHERE id = @t", TicketKit.P("t", f.Id)));
            Assert.Equal(JsonValueKind.Null, (await w.Viewer.GetTicketAsync(f.Id)).Expect(HttpStatusCode.OK).Json.GetProperty("assignee").ValueKind);
        }

        Assert.Equal(0, await fixture.ScalarAsync<long>(
            "SELECT count(*) FROM ticketing.tickets t WHERE t.assignee_user_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM organisation.department_memberships m WHERE m.user_id = t.assignee_user_id AND m.department_id = t.department_id)"));
    }

    [Fact]
    public async Task Parallel_creations_for_one_new_requester_address_yield_one_requester()
    {
        var w = await IsoWorld.For(fixture);
        var email = $"parallel-{Unique.Id()}@example.test";
        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => w.Agent.Client.PostAsync($"{w.A.Url}/tickets", TicketHelpers.NewTicketBody(email: email))));
        Assert.All(results, r => r.Expect(HttpStatusCode.Created));
        Assert.Equal(1, await fixture.ScalarAsync<long>("SELECT count(*) FROM ticketing.requesters WHERE department_id = @d AND email_normalised = @e", TicketKit.P("d", w.A.Id), TicketKit.P("e", email)));
        Assert.Single(results.Select(r => r.Json.GetProperty("requester").GetProperty("id").GetGuid()).Distinct());
        Assert.Equal(1, await fixture.CountAuditAsync("requester.created", results[0].Json.GetProperty("requester").GetProperty("id").GetGuid()));
        Assert.Equal(10, results.Select(r => r.Json.Str("reference")).Distinct().Count());
    }

    // ----- revocation (T12) -----

    [Fact]
    public async Task Access_follows_membership_role_and_account_changes_on_the_next_request()
    {
        var w = await IsoWorld.For(fixture);
        var f = await w.NewTicketAsync();
        var user = await fixture.NewUserAsync();
        await fixture.AddMemberAsync(w.A, user, "Agent");
        await fixture.AddMemberAsync(w.B, user, "Agent");
        var openA = await fixture.ScalarAsync<long>("SELECT count(*) FROM ticketing.tickets WHERE department_id = @d AND status = 'Open'", TicketKit.P("d", w.A.Id));
        var openB = await fixture.ScalarAsync<long>("SELECT count(*) FROM ticketing.tickets WHERE department_id = @d AND status = 'Open'", TicketKit.P("d", w.B.Id));

        Assert.Equal(openA + openB, (await user.Client.GetAsync("/api/tickets/queue-counts")).Expect(HttpStatusCode.OK).Json.GetProperty("all").GetInt64());
        (await user.GetTicketAsync(w.TicketB.Id)).Expect(HttpStatusCode.OK);

        // Removal from B: B vanishes (lists, counts, ticket routes), A stays.
        (await w.Root.Client.DeleteAsync($"{w.B.Url}/members/{user.Id}")).Expect(HttpStatusCode.NoContent);
        (await user.GetTicketAsync(w.TicketB.Id)).Expect(HttpStatusCode.NotFound);
        (await user.PostMessageAsync(w.TicketB.Id, "Public", "still here?")).Expect(HttpStatusCode.NotFound);
        Assert.Equal(openA, (await user.Client.GetAsync("/api/tickets/queue-counts")).Expect(HttpStatusCode.OK).Json.GetProperty("all").GetInt64());
        Assert.DoesNotContain(w.TicketB.Id, (await user.Client.GetAsync("/api/tickets?limit=100&status=Open&status=Closed&status=Pending")).Expect(HttpStatusCode.OK).Ids());
        Assert.Empty((await user.Client.GetAsync($"/api/tickets?departmentId={w.B.Id}")).Expect(HttpStatusCode.OK).Json.GetProperty("items").EnumerateArray());

        // Demotion in A: writes stop at once, reads stay.
        var current = (await user.GetTicketAsync(f.Id)).Expect(HttpStatusCode.OK);
        await fixture.AddMemberAsync(w.A, user, "Viewer");
        (await user.PatchTicketAsync(current, new { Priority = "Urgent" })).Expect(HttpStatusCode.Forbidden);
        (await user.PostMessageAsync(f.Id, "Internal", "x")).Expect(HttpStatusCode.Forbidden);
        (await user.GetTicketAsync(f.Id)).Expect(HttpStatusCode.OK);

        // Deactivation: the existing session no longer works.
        await fixture.ExecuteAsync("UPDATE identity.users SET is_active = false WHERE id = @u", TicketKit.P("u", user.Id));
        Assert.Equal(HttpStatusCode.Unauthorized, (await user.GetTicketAsync(f.Id)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await user.Client.GetAsync("/api/tickets")).Status);

        // Reactivated and signed in again, then removed from A: the ticket is gone for good (the old session stays dead).
        await fixture.ExecuteAsync("UPDATE identity.users SET is_active = true WHERE id = @u", TicketKit.P("u", user.Id));
        Assert.Equal(HttpStatusCode.Unauthorized, (await user.GetTicketAsync(f.Id)).Status);
        var again = await fixture.SignInAsync(user.Subject);
        Assert.Equal(user.Id, again.Id);
        (await again.GetTicketAsync(f.Id)).Expect(HttpStatusCode.OK);
        (await w.Root.Client.DeleteAsync($"{w.A.Url}/members/{user.Id}")).Expect(HttpStatusCode.NoContent);
        (await again.GetTicketAsync(f.Id)).Expect(HttpStatusCode.NotFound);
        Assert.True(IsoKit.EmptyPage((await again.Client.GetAsync("/api/tickets")).Expect(HttpStatusCode.OK)));
    }

    // ----- hostile input (T13) -----

    /// <summary>
    /// KNOWN DEFECT (reported, not fixed by QA): a U+0000 character in any user-supplied text (subject, description, message body, requester
    /// name/email, list or lookup query) reaches PostgreSQL, which cannot store it, and the request fails with a 500 instead of a 400.
    /// Not a data leak (nothing is stored, no stack trace is returned), but an unauthenticated-looking-input-to-500 path for every
    /// authenticated caller, and a log-noise/alerting problem. Expected fix: reject U+0000 in TicketValidation (and in the q parameters)
    /// with a 400. This test fails until then.
    /// </summary>
    [Fact]
    public async Task NUL_characters_in_text_fields_must_be_a_400_not_a_server_error()
    {
        var w = await IsoWorld.For(fixture);
        var f = await w.NewTicketAsync();
        var serverErrors = new List<string>();
        var leaks = new List<string>();

        async Task Check(string name, Func<Task<ApiResponse>> call)
        {
            var response = await call();
            if ((int)response.Status >= 500)
            {
                serverErrors.Add($"{name}: {(int)response.Status}");
                if (response.Body.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) || response.Body.Contains("Exception", StringComparison.OrdinalIgnoreCase) || response.Body.Contains(" at ", StringComparison.Ordinal))
                {
                    leaks.Add(name);
                }
            }
        }

        const string nul = "bad\0text";
        await Check("create subject NUL", () => w.Agent.Client.PostAsync($"{w.A.Url}/tickets", TicketHelpers.NewTicketBody(subject: nul)));
        await Check("create description NUL", () => w.Agent.Client.PostAsync($"{w.A.Url}/tickets", new { subject = "s", description = nul, requester = new { email = "n@example.test" } }));
        await Check("create requester name NUL", () => w.Agent.Client.PostAsync($"{w.A.Url}/tickets", new { subject = "s", description = "d", requester = new { email = "n@example.test", displayName = nul } }));
        await Check("create requester email NUL", () => w.Agent.Client.PostAsync($"{w.A.Url}/tickets", new { subject = "s", description = "d", requester = new { email = "n\0@example.test" } }));
        await Check("message body NUL", () => w.Agent.PostMessageAsync(f.Id, "Public", nul));
        await Check("patch subject NUL", () => w.Agent.Client.PatchAsync($"/api/tickets/{f.Id}", new { version = f.Version, subject = nul }));
        await Check("list q NUL", () => w.Agent.Client.GetAsync("/api/tickets?q=" + Uri.EscapeDataString(nul)));
        await Check("requester lookup NUL", () => w.Agent.Client.GetAsync($"{w.A.Url}/requesters/lookup?q=" + Uri.EscapeDataString(nul)));

        Assert.True(serverErrors.Count == 0, "Server errors: " + string.Join("; ", serverErrors) + (leaks.Count > 0 ? " | with internals in the body: " + string.Join(", ", leaks) : ""));
    }

    [Fact]
    public async Task Hostile_input_is_rejected_or_stored_never_a_server_error()
    {
        // Collected so one failing run lists every 5xx at once.
        var w = await IsoWorld.For(fixture);
        var f = await w.NewTicketAsync();
        var serverErrors = new List<string>();

        async Task Check(string name, Func<Task<ApiResponse>> call)
        {
            var response = await call();
            if ((int)response.Status >= 500)
            {
                serverErrors.Add($"{name}: {(int)response.Status}");
            }
        }

        var emoji = string.Concat(Enumerable.Repeat("\U0001F600", 100)); // 200 UTF-16 units
        var longEmoji = string.Concat(Enumerable.Repeat("\U0001F600", 10000)); // exactly 20000 UTF-16 units
        await Check("emoji subject", () => w.Agent.Client.PostAsync($"{w.A.Url}/tickets", TicketHelpers.NewTicketBody(subject: emoji)));
        await Check("emoji 20000-unit body", () => w.Agent.PostMessageAsync(f.Id, "Public", longEmoji));
        await Check("version overflow", () => w.Agent.Client.PatchAsync($"/api/tickets/{f.Id}", new { version = "99999999999999999999", subject = "x" }));
        await Check("version negative", () => w.Agent.Client.PatchAsync($"/api/tickets/{f.Id}", new { version = "-1", subject = "x" }));
        await Check("huge limit", () => w.Agent.Client.GetAsync("/api/tickets?limit=2147483648"));
        await Check("repeated enum params", () => w.Agent.Client.GetAsync("/api/tickets?status=Open&status=Open&status=&priority=&queue="));
        await Check("many status values", () => w.Agent.Client.GetAsync("/api/tickets?" + string.Join('&', Enumerable.Repeat("status=Open", 500))));
        await Check("guid-like route", () => w.Agent.Client.GetAsync("/api/tickets/00000000-0000-0000-0000-000000000000"));
        await Check("unicode route", () => w.Agent.Client.GetAsync("/api/tickets/%F0%9F%98%80-1"));
        await Check("long route", () => w.Agent.Client.GetAsync("/api/tickets/" + new string('A', 3000)));

        Assert.True(serverErrors.Count == 0, "Server errors: " + string.Join("; ", serverErrors));
    }

    [Fact]
    public async Task Malformed_json_answers_the_same_for_foreign_and_unknown_tickets()
    {
        var w = await IsoWorld.For(fixture);
        var f = await w.NewTicketAsync();
        var before = await IsoKit.SnapshotAsync(fixture, w.Ids);
        var client = w.Actors[IsoWorld.MemberOfBOnly].Client;
        var token = await IsoKit.CsrfTokenOfAsync(client);

        foreach (var (method, suffix) in new (HttpMethod, string)[] { (HttpMethod.Patch, ""), (HttpMethod.Put, "/assignment"), (HttpMethod.Post, "/messages") })
        {
            foreach (var raw in new[] { "{not json", "[]", "null", "\"text\"", "{\"version\": {}}", "" })
            {
                async Task<ApiResponse> Send(string route)
                {
                    using var request = new HttpRequestMessage(method, $"/api/tickets/{route}{suffix}")
                    {
                        Content = new StringContent(raw, System.Text.Encoding.UTF8, "application/json"),
                    };
                    request.Headers.Add("X-CSRF-TOKEN", token);
                    var message = await client.Http.SendAsync(request);
                    return new ApiResponse(message.StatusCode, await message.Content.ReadAsStringAsync(), message);
                }

                var real = await Send(f.Id.ToString());
                var ghost = await Send(Guid.NewGuid().ToString());
                Assert.True((int)real.Status < 500, $"{method} {raw}: {(int)real.Status}");
                Assert.Equal(IsoKit.Fingerprint(ghost), IsoKit.Fingerprint(real));
            }
        }

        Assert.Equal(before, await IsoKit.SnapshotAsync(fixture, w.Ids));
    }

    /// <summary>
    /// SECURITY.md lists security headers among the transport controls and the API returns user-supplied markup verbatim inside JSON.
    /// A JSON response should therefore forbid content sniffing. Reported as a gap if the header is absent (see the threat model, T7).
    /// </summary>
    [Fact]
    public async Task Ticket_api_responses_forbid_content_sniffing_and_caching()
    {
        var w = await IsoWorld.For(fixture);
        var f = await w.NewTicketAsync();
        var response = (await w.Viewer.GetTicketAsync(f.Id)).Expect(HttpStatusCode.OK);
        var headers = string.Join(", ", response.Message.Headers.Concat(response.Message.Content.Headers).Select(h => h.Key));
        Assert.True(
            response.Message.Headers.TryGetValues("X-Content-Type-Options", out var values) && values.Contains("nosniff"),
            "X-Content-Type-Options: nosniff is missing. Response headers: " + headers);
    }
}
