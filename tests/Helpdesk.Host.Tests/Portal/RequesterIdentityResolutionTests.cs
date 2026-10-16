#pragma warning disable CA1861 // one-off request payloads
using System.Net;
using System.Text.Json;
using Helpdesk.Host.Tests.Infrastructure;
using Helpdesk.Host.Tests.Mail;
using Helpdesk.Host.Tests.Ticketing;

namespace Helpdesk.Host.Tests.Portal;

/// <summary>
/// Administrative requester identity resolution: explicit, department-scoped, audited. An address never proves ownership, and linking grants
/// requester ownership only (no department access, no form audience).
/// </summary>
public sealed class RequesterIdentityResolutionTests(HelpdeskFixture fixture) : IClassFixture<HelpdeskFixture>
{
    private static object Request(Guid formId, string subject = "Need help") => new { formId, subject, description = "details", submissionId = Guid.NewGuid() };

    /// <summary>A signed-in account that is NOT registered as a trusted test owner (so nothing links it implicitly).</summary>
    private Task<TestUser> AccountAsync(string email, string? name = null) => fixture.SignInAsync($"acct-{Unique.Id()}", name ?? $"Account {Unique.Id()}", email);

    private static string Base(Dept d) => $"{d.Department.Url}/requester-identities";

    private async Task<Guid> RequesterOfAsync(string reference) =>
        await fixture.ScalarAsync<Guid>("SELECT requester_id FROM ticketing.tickets WHERE reference = @r", TicketKit.P("r", reference));

    private async Task<long> LinksAsync(Guid requesterId) =>
        await fixture.CountAsync("SELECT count(*) FROM ticketing.requester_links WHERE requester_id = @r", TicketKit.P("r", requesterId));

    private static Guid[] CaseIds(ApiResponse list) => [.. list.Json.EnumerateArray().Select(c => c.GetProperty("requesterId").GetGuid())];

    [Fact]
    public async Task An_administrator_resolves_the_portal_blocking_ambiguity_and_the_account_gets_its_history()
    {
        var d = await fixture.NewDeptAsync();
        var (formId, _) = await d.FormAsync(fixture, "General");
        var email = $"jane-{Unique.Id()}@example.test";
        var legacy = (await d.TicketForAsync(email, "Emailed in")).Expect(HttpStatusCode.Created);
        var requesterId = await RequesterOfAsync(legacy.Reference());
        var jane = await AccountAsync(email, "Jane Smith");

        // The secure default: blocked and empty until an administrator decides.
        (await jane.Client.PostAsync("/api/portal/requests", Request(formId))).Expect(HttpStatusCode.Conflict);
        Assert.Empty((await jane.Client.GetAsync("/api/portal/requests")).Expect(HttpStatusCode.OK).Json.GetProperty("items").EnumerateArray());

        // The case is listed with the coincidence flagged (a suggestion, not proof) and safe context.
        var list = (await d.Admin.Client.GetAsync(Base(d))).Expect(HttpStatusCode.OK);
        var item = list.Json.EnumerateArray().Single(c => c.GetProperty("requesterId").GetGuid() == requesterId);
        Assert.True(item.GetProperty("needsAttention").GetBoolean());
        Assert.Equal(jane.Id, item.GetProperty("matchingAccount").GetProperty("userId").GetGuid());
        Assert.Equal(1, item.GetProperty("ticketCount").GetInt32());
        var detail = (await d.Admin.Client.GetAsync($"{Base(d)}/{requesterId}")).Expect(HttpStatusCode.OK).Json;
        Assert.Equal("Unlinked", detail.GetProperty("status").GetString());
        Assert.Equal(legacy.Reference(), detail.GetProperty("tickets")[0].GetProperty("reference").GetString());

        // Explicit link; repeating it is idempotent.
        var linked = (await d.Admin.Client.PostAsync($"{Base(d)}/{requesterId}/link", new { userId = jane.Id })).Expect(HttpStatusCode.OK).Json;
        Assert.False(linked.GetProperty("alreadyLinked").GetBoolean());
        Assert.True((await d.Admin.Client.PostAsync($"{Base(d)}/{requesterId}/link", new { userId = jane.Id })).Expect(HttpStatusCode.OK).Json.GetProperty("alreadyLinked").GetBoolean());
        Assert.Equal(1, await LinksAsync(requesterId));
        Assert.DoesNotContain(requesterId, CaseIds((await d.Admin.Client.GetAsync(Base(d))).Expect(HttpStatusCode.OK))); // resolved cases stop being actionable

        // Portal effect: history appears, submission works and reuses the link.
        Assert.Equal(legacy.Reference(), Assert.Single((await jane.Client.GetAsync("/api/portal/requests")).Expect(HttpStatusCode.OK).Json.GetProperty("items").EnumerateArray()).GetProperty("reference").GetString());
        var fresh = (await jane.Client.PostAsync("/api/portal/requests", Request(formId, "Now works"))).Expect(HttpStatusCode.Created).Json.GetProperty("reference").GetString()!;
        Assert.Equal(requesterId, await RequesterOfAsync(fresh));

        // Linking grants requester ownership only: no staff access, no configuration.
        Assert.NotEqual(HttpStatusCode.OK, (await jane.Client.GetAsync($"/api/tickets/{legacy.Reference()}")).Status);
        Assert.NotEqual(HttpStatusCode.OK, (await jane.Client.GetAsync($"{d.Department.Url}/request-forms")).Status);
        Assert.NotEqual(HttpStatusCode.OK, (await jane.Client.GetAsync(Base(d))).Status);

        // Audit: identifiers and change type, none of the personal data.
        Assert.Equal(1, await fixture.CountAuditAsync("requester.identity_linked_by_admin", requesterId));
        Assert.Equal(0, await fixture.CountAsync("SELECT count(*) FROM audit.audit_events a WHERE a.action = 'requester.identity_linked_by_admin' AND a.object_id = @o AND to_jsonb(a)::text ILIKE @p",
            TicketKit.P("o", requesterId), TicketKit.P("p", $"%{email}%")));
    }

    [Fact]
    public async Task The_same_address_is_never_proof_and_a_changed_address_does_not_prevent_the_correct_link()
    {
        var d = await fixture.NewDeptAsync();
        var email = $"alex-{Unique.Id()}@example.test";
        var legacy = (await d.TicketForAsync(email, "Old Alex")).Expect(HttpStatusCode.Created);
        var requesterId = await RequesterOfAsync(legacy.Reference());

        // Two different accounts now hold that address: nothing is suggested and nobody owns anything.
        var first = await AccountAsync(email, "Alex One");
        var second = await AccountAsync(email, "Alex Two");
        var item = (await d.Admin.Client.GetAsync(Base(d))).Expect(HttpStatusCode.OK).Json.EnumerateArray().Single(c => c.GetProperty("requesterId").GetGuid() == requesterId);
        Assert.False(item.GetProperty("needsAttention").GetBoolean());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("matchingAccount").ValueKind);
        Assert.Empty((await first.Client.GetAsync("/api/portal/requests")).Expect(HttpStatusCode.OK).Json.GetProperty("items").EnumerateArray());
        Assert.Equal(0, await LinksAsync(requesterId));

        // The administrator knows who it really is (an account with a different address): explicit link works, the other account still sees nothing.
        var renamed = await AccountAsync($"alex-new-{Unique.Id()}@example.test", "Alex Renamed");
        (await d.Admin.Client.PostAsync($"{Base(d)}/{requesterId}/link", new { userId = renamed.Id })).Expect(HttpStatusCode.OK);
        Assert.Single((await renamed.Client.GetAsync("/api/portal/requests")).Expect(HttpStatusCode.OK).Json.GetProperty("items").EnumerateArray());
        Assert.Empty((await second.Client.GetAsync("/api/portal/requests")).Expect(HttpStatusCode.OK).Json.GetProperty("items").EnumerateArray());

        // An existing link is never silently replaced; an unknown or missing account is a validation error.
        (await d.Admin.Client.PostAsync($"{Base(d)}/{requesterId}/link", new { userId = second.Id })).Expect(HttpStatusCode.Conflict);
        Assert.Equal(renamed.Id, await fixture.ScalarAsync<Guid>("SELECT user_id FROM ticketing.requester_links WHERE requester_id = @r", TicketKit.P("r", requesterId)));
        var other = (await d.TicketForAsync($"v-{Unique.Id()}@vendor.test", "Vendor")).Expect(HttpStatusCode.Created);
        var otherId = await RequesterOfAsync(other.Reference());
        (await d.Admin.Client.PostAsync($"{Base(d)}/{otherId}/link", new { userId = Guid.NewGuid() })).Expect(HttpStatusCode.BadRequest);
        (await d.Admin.Client.PostAsync($"{Base(d)}/{otherId}/link", new { })).Expect(HttpStatusCode.BadRequest);
        // An account owns at most one requester per department: a second one is refused, not merged.
        (await d.Admin.Client.PostAsync($"{Base(d)}/{otherId}/link", new { userId = renamed.Id })).Expect(HttpStatusCode.Conflict);
        Assert.Equal(0, await LinksAsync(otherId));
    }

    [Fact]
    public async Task Other_departments_platform_administrators_and_agents_cannot_see_or_resolve_requesters_and_audiences_still_apply()
    {
        var it = await fixture.NewDeptAsync();
        var hr = await fixture.NewDeptAsync();
        var (restricted, _) = await it.FormAsync(fixture, "Restricted", new { Audience = "Selected" });
        var email = $"sam-{Unique.Id()}@example.test";
        var legacy = (await it.TicketForAsync(email, "Sam's IT problem")).Expect(HttpStatusCode.Created);
        var requesterId = await RequesterOfAsync(legacy.Reference());
        var sam = await AccountAsync(email, "Sam");

        // HR's administrator: nothing listed, and every route answers like a missing object (also through HR's own path with IT's id).
        Assert.DoesNotContain(requesterId, CaseIds((await hr.Admin.Client.GetAsync(Base(hr))).Expect(HttpStatusCode.OK)));
        (await hr.Admin.Client.GetAsync($"{Base(hr)}/{requesterId}")).Expect(HttpStatusCode.NotFound);
        (await hr.Admin.Client.PostAsync($"{Base(hr)}/{requesterId}/link", new { userId = sam.Id })).Expect(HttpStatusCode.NotFound);
        (await hr.Admin.Client.GetAsync($"{Base(it)}/{requesterId}")).Expect(HttpStatusCode.NotFound);
        (await hr.Admin.Client.PostAsync($"{Base(it)}/{requesterId}/link", new { userId = sam.Id })).Expect(HttpStatusCode.NotFound);

        // A platform administrator sees every department's requesters by policy (S-02); linking is left to the department administrator below.
        var platform = await fixture.PlatformAdminAsync();
        Assert.Contains(requesterId, CaseIds((await platform.Client.GetAsync(Base(it))).Expect(HttpStatusCode.OK)));
        (await platform.Client.GetAsync($"{Base(it)}/{requesterId}")).Expect(HttpStatusCode.OK);
        Assert.Equal(HttpStatusCode.Forbidden, (await it.Agent.Client.GetAsync(Base(it))).Status); // not a people manager
        Assert.Equal(0, await LinksAsync(requesterId));

        // Linked: Sam owns the requester, but a restricted form still does not exist for Sam and no department access appears.
        (await it.Admin.Client.PostAsync($"{Base(it)}/{requesterId}/link", new { userId = sam.Id })).Expect(HttpStatusCode.OK);
        (await sam.Client.GetAsync($"/api/portal/forms/{restricted}")).Expect(HttpStatusCode.NotFound);
        (await sam.Client.PostAsync("/api/portal/requests", Request(restricted))).Expect(HttpStatusCode.NotFound);
        Assert.NotEqual(HttpStatusCode.OK, (await sam.Client.GetAsync($"/api/tickets/{legacy.Reference()}")).Status);
        Assert.Equal(0, await fixture.CountAsync("SELECT count(*) FROM organisation.department_memberships WHERE user_id = @u", TicketKit.P("u", sam.Id)));
    }

    [Fact]
    public async Task Two_administrators_linking_at_once_leave_exactly_one_association()
    {
        var d = await fixture.NewDeptAsync();
        var email = $"race-{Unique.Id()}@example.test";
        var legacy = (await d.TicketForAsync(email, "Race")).Expect(HttpStatusCode.Created);
        var requesterId = await RequesterOfAsync(legacy.Reference());
        var a = await AccountAsync($"a-{Unique.Id()}@example.test");
        var b = await AccountAsync($"b-{Unique.Id()}@example.test");

        var results = await Task.WhenAll(
            d.Admin.Client.PostAsync($"{Base(d)}/{requesterId}/link", new { userId = a.Id }),
            d.Admin.Client.PostAsync($"{Base(d)}/{requesterId}/link", new { userId = b.Id }));
        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.Conflict], results.Select(r => r.Status).OrderBy(s => s).ToArray());
        Assert.Equal(1, await LinksAsync(requesterId));
        Assert.Equal(1, await fixture.CountAuditAsync("requester.identity_linked_by_admin", requesterId));
    }

    [Fact]
    public async Task The_list_can_be_searched_by_name_or_address_and_paged()
    {
        var d = await fixture.NewDeptAsync();
        var tag = Unique.Id();
        var first = (await d.TicketForAsync($"findme-{tag}@example.test", $"One {tag}")).Expect(HttpStatusCode.Created);
        (await d.TicketForAsync($"other-{tag}@example.test", $"Two {tag}")).Expect(HttpStatusCode.Created);
        var firstRequester = await RequesterOfAsync(first.Reference());

        var found = (await d.Admin.Client.GetAsync($"{Base(d)}?q=findme-{tag}")).Expect(HttpStatusCode.OK);
        Assert.Equal([firstRequester], CaseIds(found));
        Assert.Equal(2, CaseIds((await d.Admin.Client.GetAsync($"{Base(d)}?q={tag}")).Expect(HttpStatusCode.OK)).Length);
        Assert.Empty(CaseIds((await d.Admin.Client.GetAsync($"{Base(d)}?q=no-such-person-{tag}")).Expect(HttpStatusCode.OK)));
        Assert.Empty(CaseIds((await d.Admin.Client.GetAsync($"{Base(d)}?skip=2")).Expect(HttpStatusCode.OK))); // a second page past the two cases
        (await d.Admin.Client.GetAsync($"{Base(d)}?skip=-1")).Expect(HttpStatusCode.BadRequest);
        (await d.Admin.Client.GetAsync($"{Base(d)}?q=%25")).Expect(HttpStatusCode.OK); // a wildcard is plain text, not a pattern
    }
}
