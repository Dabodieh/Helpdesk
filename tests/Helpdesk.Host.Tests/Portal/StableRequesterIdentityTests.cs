#pragma warning disable CA1861 // one-off request payloads
using System.Net;
using Helpdesk.Host.Tests.Infrastructure;
using Helpdesk.Host.Tests.Mail;
using Helpdesk.Host.Tests.Ticketing;

namespace Helpdesk.Host.Tests.Portal;

/// <summary>
/// Stable requester identity: the authenticated account owns its Department-local requester rows; the address is contact data and never claims history.
/// </summary>
public sealed class StableRequesterIdentityTests(HelpdeskFixture fixture) : IClassFixture<HelpdeskFixture>
{
    private static object Request(Guid formId, string subject = "Need help", object? extra = null) => new { formId, subject, description = "details", submissionId = Guid.NewGuid(), extra };

    private Task<Guid> RequesterOfAsync(string reference) =>
        fixture.ScalarAsync<Guid>("SELECT requester_id FROM ticketing.tickets WHERE reference = @r", TicketKit.P("r", reference));

    private async Task<long> LinksAsync(Guid requesterId) =>
        await fixture.CountAsync("SELECT count(*) FROM ticketing.requester_links WHERE requester_id = @r", TicketKit.P("r", requesterId));

    [Fact]
    public async Task A_changed_address_keeps_ownership_and_the_same_requester_serves_future_requests()
    {
        var d = await fixture.NewDeptAsync();
        var (formId, _) = await d.FormAsync("General");
        var jane = await fixture.EmployeeAsync("jane");
        var first = (await jane.User.Client.PostAsync("/api/portal/requests", Request(formId, "First"))).Expect(HttpStatusCode.Created).Json.GetProperty("reference").GetString()!;
        var requesterId = await RequesterOfAsync(first);
        Assert.Equal(1, await LinksAsync(requesterId));
        Assert.True(await fixture.CountAuditAsync("requester.identity_linked", requesterId) == 1); // identifiers only, once

        // Same account (same Entra subject), new address and name.
        var newEmail = $"renamed-{Unique.Id()}@example.test";
        var renamed = await fixture.SignInAsync(jane.User.Subject, "Jane Jones", newEmail);
        Assert.Equal(first, Assert.Single((await renamed.Client.GetAsync("/api/portal/requests")).Expect(HttpStatusCode.OK).Json.GetProperty("items").EnumerateArray()).GetProperty("reference").GetString());
        var second = (await renamed.Client.PostAsync("/api/portal/requests", Request(formId, "Second"))).Expect(HttpStatusCode.Created).Json.GetProperty("reference").GetString()!;

        Assert.Equal(requesterId, await RequesterOfAsync(second)); // no duplicate requester for a new address
        Assert.Equal(newEmail, await fixture.ScalarAsync<string>("SELECT email_normalised FROM ticketing.requesters WHERE id = @r", TicketKit.P("r", requesterId)));
        Assert.Equal("Jane Jones", await fixture.ScalarAsync<string>("SELECT display_name FROM ticketing.requesters WHERE id = @r", TicketKit.P("r", requesterId)));
        Assert.Equal(1, await fixture.CountAuditAsync("requester.identity_linked", requesterId)); // sign-in and later requests are not audited
    }

    [Fact]
    public async Task A_reused_address_never_inherits_history_and_an_address_alone_claims_nothing()
    {
        var d = await fixture.NewDeptAsync();
        var (formId, _) = await d.FormAsync("General");
        var alex = await fixture.EmployeeAsync("alex");
        var old = (await alex.User.Client.PostAsync("/api/portal/requests", Request(formId, "Old alex"))).Expect(HttpStatusCode.Created).Json.GetProperty("reference").GetString()!;

        // Alex moves to another address; the old address is later issued to a different person (different Entra subject).
        var moved = await fixture.SignInAsync(alex.User.Subject, "Alex", $"moved-{Unique.Id()}@example.test");
        (await moved.Client.PostAsync("/api/portal/requests", Request(formId, "Alex later"))).Expect(HttpStatusCode.Created); // the account's contact address follows
        var other = await fixture.SignInAsync($"alex2-{Unique.Id()}", "Alex Two", alex.Email);
        (await other.Client.PostAsync("/api/portal/requests", Request(formId, "Mine now"))).Expect(HttpStatusCode.Created);
        var list = (await other.Client.GetAsync("/api/portal/requests")).Expect(HttpStatusCode.OK).Json.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("subject").GetString()!).ToArray();
        Assert.Equal(["Mine now"], list);
        (await other.Client.GetAsync($"/api/portal/requests/{old}")).Expect(HttpStatusCode.NotFound);

        // Unlinked history under an address (email-created): signing in with that address neither lists nor inherits it, and submitting is refused for resolution.
        var email = $"legacy-{Unique.Id()}@example.test";
        var legacy = (await d.TicketForAsync(email, "Emailed in")).Expect(HttpStatusCode.Created);
        var claimant = await fixture.SignInAsync($"claimant-{Unique.Id()}", "Claimant", email);
        Assert.Empty((await claimant.Client.GetAsync("/api/portal/requests")).Expect(HttpStatusCode.OK).Json.GetProperty("items").EnumerateArray());
        (await claimant.Client.GetAsync($"/api/portal/requests/{legacy.Reference()}")).Expect(HttpStatusCode.NotFound);
        (await claimant.Client.PostAsync("/api/portal/requests", Request(formId))).Expect(HttpStatusCode.Conflict);
        var legacyRequester = await RequesterOfAsync(legacy.Reference());
        Assert.Equal(0, await LinksAsync(legacyRequester));
        Assert.Equal(1, await fixture.CountAuditAsync("requester.link_ambiguous", legacyRequester));
    }

    [Fact]
    public async Task One_account_spans_departments_without_widening_any_agents_view_and_unlinked_senders_keep_working()
    {
        var it = await fixture.NewDeptAsync();
        var hr = await fixture.NewDeptAsync();
        var (itForm, _) = await it.FormAsync("IT help");
        var (hrForm, _) = await hr.FormAsync("HR help");
        var jane = await fixture.EmployeeAsync("jane");
        var itRef = (await jane.User.Client.PostAsync("/api/portal/requests", Request(itForm, "IT problem"))).Expect(HttpStatusCode.Created).Json.GetProperty("reference").GetString()!;
        var hrRef = (await jane.User.Client.PostAsync("/api/portal/requests", Request(hrForm, "HR question"))).Expect(HttpStatusCode.Created).Json.GetProperty("reference").GetString()!;

        // Two department-local requesters, one account; the tickets land in the form's department with the caller as requester.
        var itRequester = await RequesterOfAsync(itRef);
        var hrRequester = await RequesterOfAsync(hrRef);
        Assert.NotEqual(itRequester, hrRequester);
        Assert.Equal(2, await fixture.CountAsync("SELECT count(*) FROM ticketing.requester_links WHERE user_id = @u AND requester_id IN (@a, @b)", TicketKit.P("u", jane.User.Id), TicketKit.P("a", itRequester), TicketKit.P("b", hrRequester)));

        // An IT agent sees IT history only (counts included); HR's agent likewise; the account link grants no department access.
        var itProfile = (await it.Agent.Client.GetAsync($"/api/requesters/{itRequester}")).Expect(HttpStatusCode.OK).Json;
        Assert.Equal(1, itProfile.GetProperty("totalCount").GetInt32());
        Assert.DoesNotContain("HR question", itProfile.GetRawText(), StringComparison.Ordinal);
        (await it.Agent.Client.GetAsync($"/api/requesters/{hrRequester}")).Expect(HttpStatusCode.NotFound);
        (await hr.Agent.Client.GetAsync($"/api/requesters/{itRequester}")).Expect(HttpStatusCode.NotFound);
        Assert.Equal(1, (await hr.Agent.Client.GetAsync($"/api/requesters/{hrRequester}")).Expect(HttpStatusCode.OK).Json.GetProperty("totalCount").GetInt32());
        Assert.NotEqual(HttpStatusCode.OK, (await jane.User.Client.GetAsync($"/api/requesters/{itRequester}")).Status);
        Assert.NotEqual(HttpStatusCode.OK, (await jane.User.Client.GetAsync($"/api/tickets/{itRef}")).Status);

        // A platform administrator without a membership reads no ticket content through the link either.
        var admin = await fixture.PlatformAdminAsync();
        Assert.NotEqual(HttpStatusCode.OK, (await admin.Client.GetAsync($"/api/requesters/{itRequester}")).Status);
        Assert.NotEqual(HttpStatusCode.OK, (await admin.Client.GetAsync($"/api/tickets/{itRef}")).Status);

        // An external sender with no account: the ticket works, the requester is simply unlinked, and nobody portal-side owns it.
        var external = (await it.TicketForAsync($"vendor-{Unique.Id()}@vendor.test", "From a vendor")).Expect(HttpStatusCode.Created);
        Assert.Equal(0, await LinksAsync(await RequesterOfAsync(external.Reference())));
        (await jane.User.Client.GetAsync($"/api/portal/requests/{external.Reference()}")).Expect(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Owning_a_requester_never_bypasses_the_form_audience_or_lets_crafted_ids_take_over_another_ticket()
    {
        var d = await fixture.NewDeptAsync();
        var (formId, _) = await d.FormAsync("Open form");
        var (restricted, _) = await d.FormAsync("Restricted form", new { Audience = "Selected" });
        var jane = await fixture.EmployeeAsync("jane");
        var bob = await fixture.EmployeeAsync("bob");
        var janes = (await jane.User.Client.PostAsync("/api/portal/requests", Request(formId, "Janes"))).Expect(HttpStatusCode.Created).Json.GetProperty("reference").GetString()!;

        // Jane already owns a requester in the department; the restricted form still does not exist for her.
        (await jane.User.Client.GetAsync($"/api/portal/forms/{restricted}")).Expect(HttpStatusCode.NotFound);
        (await jane.User.Client.PostAsync("/api/portal/requests", Request(restricted))).Expect(HttpStatusCode.NotFound);

        // Crafted requester/identity values in the body are ignored: the ticket belongs to the caller; Jane's stays hers.
        var requesterId = await RequesterOfAsync(janes);
        var crafted = (await bob.User.Client.PostAsync("/api/portal/requests", new { formId, subject = "Crafted", description = "x", submissionId = Guid.NewGuid(), requesterId, userId = jane.User.Id, email = jane.Email })).Expect(HttpStatusCode.Created).Json.GetProperty("reference").GetString()!;
        Assert.NotEqual(requesterId, await RequesterOfAsync(crafted));
        Assert.Equal(bob.User.Id, await fixture.ScalarAsync<Guid>("SELECT user_id FROM ticketing.requester_links WHERE requester_id = @r", TicketKit.P("r", await RequesterOfAsync(crafted))));
        (await bob.User.Client.GetAsync($"/api/portal/requests/{janes}")).Expect(HttpStatusCode.NotFound);
        (await bob.User.Client.PutAsync($"/api/portal/requests/{janes}/fields", new { fields = new { anything = "x" } })).Expect(HttpStatusCode.NotFound);
        Assert.Equal(1, await LinksAsync(requesterId));
    }
}
