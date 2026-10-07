#pragma warning disable CA1861 // one-off request payloads
using System.Net;
using Helpdesk.Host.Tests.Infrastructure;
using Helpdesk.Host.Tests.Mail;
using Helpdesk.Host.Tests.Ticketing;

namespace Helpdesk.Host.Tests.Portal;

/// <summary>
/// Request form requester audiences: one server-side decision for discovery, metadata and submission; an out-of-audience form is indistinguishable
/// from a missing one; and being allowed to use a form grants no department access.
/// </summary>
public sealed class FormAudienceTests(HelpdeskFixture fixture) : IClassFixture<HelpdeskFixture>
{
    private static object Request(Guid formId, string subject = "Need help", object? fields = null) =>
        new { formId, subject, description = "details", submissionId = Guid.NewGuid(), fields };

    /// <summary>PATCHes the audience; <paramref name="userIds"/> null leaves the user list untouched.</summary>
    private static async Task<ApiResponse> SetAudienceAsync(Dept d, Guid formId, string? audience, Guid[]? userIds = null)
    {
        var form = (await d.Admin.Client.GetAsync($"{d.Department.Url}/request-forms")).Expect(HttpStatusCode.OK).Json.EnumerateArray().Single(f => f.GetProperty("id").GetGuid() == formId);
        return await d.Admin.Client.PatchAsync($"{d.Department.Url}/request-forms/{formId}", new { version = form.GetProperty("version").GetString(), audience, audienceUserIds = userIds });
    }

    [Fact]
    public async Task A_new_form_is_restricted_and_an_out_of_audience_form_is_indistinguishable_from_a_missing_one()
    {
        var d = await fixture.NewDeptAsync();
        var alice = await fixture.EmployeeAsync("alice");
        var name = $"Secret service {Unique.Id()}";
        var created = (await d.Admin.Client.PostAsync($"{d.Department.Url}/request-forms", new { name, description = "Hidden description" })).Expect(HttpStatusCode.Created);
        Assert.Equal("Selected", created.Json.GetProperty("audience").GetString()); // nobody until someone chooses
        var formId = created.Id;
        var client = alice.User.Client;

        // Discovery: no name, description, department or id leaks.
        var list = (await client.GetAsync("/api/portal/forms")).Expect(HttpStatusCode.OK);
        Assert.DoesNotContain(name, list.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(formId.ToString(), list.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Hidden description", list.Body, StringComparison.Ordinal);

        // Direct access, metadata and submission (even with invalid input that would reveal field rules) answer exactly like a random id.
        var random = Guid.NewGuid();
        var missing = (await client.GetAsync($"/api/portal/forms/{random}")).Expect(HttpStatusCode.NotFound);
        var denied = (await client.GetAsync($"/api/portal/forms/{formId}")).Expect(HttpStatusCode.NotFound);
        Assert.Equal(Normalise(missing.Body, random), Normalise(denied.Body, formId));
        var missingPost = (await client.PostAsync("/api/portal/requests", Request(random, fields: new { nonsense = 1 }))).Expect(HttpStatusCode.NotFound);
        var deniedPost = (await client.PostAsync("/api/portal/requests", Request(formId, fields: new { nonsense = 1 }))).Expect(HttpStatusCode.NotFound);
        Assert.Equal(Normalise(missingPost.Body, random), Normalise(deniedPost.Body, formId));
        Assert.Empty((await client.GetAsync("/api/portal/requests")).Expect(HttpStatusCode.OK).Json.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task All_employees_may_submit_without_gaining_department_access_and_changing_the_audience_takes_effect_immediately()
    {
        var it = await fixture.NewDeptAsync();
        var alice = await fixture.EmployeeAsync("alice");
        var bob = await fixture.EmployeeAsync("bob");
        var (formId, _) = await it.FormAsync("Report an IT issue");
        var client = alice.User.Client;

        // Published to all employees: any employee (here with no membership anywhere) discovers, reads and submits it.
        Assert.Contains((await client.GetAsync("/api/portal/forms")).Expect(HttpStatusCode.OK).Json.EnumerateArray(), f => f.GetProperty("id").GetGuid() == formId);
        (await client.GetAsync($"/api/portal/forms/{formId}")).Expect(HttpStatusCode.OK);
        var reference = (await client.PostAsync("/api/portal/requests", Request(formId))).Expect(HttpStatusCode.Created).Json.GetProperty("reference").GetString()!;

        // The ticket lands in the form's department and the requester sees only the requester-facing view.
        Assert.Equal(it.Id, (await it.Agent.Client.GetAsync($"/api/tickets/{reference}")).Expect(HttpStatusCode.OK).Json.GetProperty("departmentId").GetGuid());
        (await client.GetAsync($"/api/portal/requests/{reference}")).Expect(HttpStatusCode.OK);

        // Requester audience is not department membership: no agent ticket access, no configuration, no queues.
        Assert.NotEqual(HttpStatusCode.OK, (await client.GetAsync($"/api/tickets/{reference}")).Status);
        Assert.NotEqual(HttpStatusCode.OK, (await client.GetAsync($"{it.Department.Url}/request-forms")).Status);
        Assert.NotEqual(HttpStatusCode.OK, (await client.GetAsync($"{it.Department.Url}/ticket-types")).Status);
        Assert.NotEqual(HttpStatusCode.OK, (await client.GetAsync($"{it.Department.Url}/ticket-fields")).Status);
        (await bob.User.Client.GetAsync($"/api/portal/requests/{reference}")).Expect(HttpStatusCode.NotFound);

        // Restrict to bob only: alice loses discovery, metadata and submission at once, including a stale submit of a form she had open.
        (await SetAudienceAsync(it, formId, "Selected", [bob.User.Id])).Expect(HttpStatusCode.OK);
        Assert.DoesNotContain((await client.GetAsync("/api/portal/forms")).Expect(HttpStatusCode.OK).Json.EnumerateArray(), f => f.GetProperty("id").GetGuid() == formId);
        (await client.GetAsync($"/api/portal/forms/{formId}")).Expect(HttpStatusCode.NotFound);
        (await client.PostAsync("/api/portal/requests", Request(formId))).Expect(HttpStatusCode.NotFound);
        (await bob.User.Client.GetAsync($"/api/portal/forms/{formId}")).Expect(HttpStatusCode.OK);
        (await bob.User.Client.PostAsync("/api/portal/requests", Request(formId))).Expect(HttpStatusCode.Created);

        // Her existing ticket stays hers under the normal portal rules.
        (await client.GetAsync($"/api/portal/requests/{reference}")).Expect(HttpStatusCode.OK);

        // Removing bob again closes it for him; the audit trail records identifiers only.
        (await SetAudienceAsync(it, formId, "Selected")).Expect(HttpStatusCode.OK); // list omitted: unchanged
        (await bob.User.Client.GetAsync($"/api/portal/forms/{formId}")).Expect(HttpStatusCode.OK);
        var cleared = (await SetAudienceAsync(it, formId, null, [])).Expect(HttpStatusCode.OK);
        Assert.Empty(cleared.Json.GetProperty("audienceUsers").EnumerateArray());
        (await bob.User.Client.GetAsync($"/api/portal/forms/{formId}")).Expect(HttpStatusCode.NotFound);
        Assert.True(await fixture.CountAsync("SELECT count(*) FROM audit.audit_events WHERE department_id = @d AND action = 'request_form.audience_changed'", TicketKit.P("d", it.Id)) >= 2);
    }

    [Fact]
    public async Task Audience_configuration_is_validated_and_stays_inside_the_department()
    {
        var it = await fixture.NewDeptAsync();
        var hr = await fixture.NewDeptAsync();
        var alice = await fixture.EmployeeAsync("alice");
        var (formId, _) = await it.FormAsync("Request access");

        (await SetAudienceAsync(it, formId, "Everyone")).Expect(HttpStatusCode.BadRequest);
        (await SetAudienceAsync(it, formId, "Selected", [Guid.NewGuid()])).Expect(HttpStatusCode.BadRequest); // unknown user
        var set = (await SetAudienceAsync(it, formId, "Selected", [alice.User.Id, alice.User.Id])).Expect(HttpStatusCode.OK); // duplicates collapse
        Assert.Equal(alice.User.Id, set.Json.GetProperty("audienceUsers").EnumerateArray().Single().GetProperty("userId").GetGuid());

        // Another department's administrator cannot read or change this form's audience; an agent without forms.manage cannot either.
        (await hr.Admin.Client.PatchAsync($"{it.Department.Url}/request-forms/{formId}", new { version = set.Version, audience = "AllEmployees" })).Expect(HttpStatusCode.NotFound);
        (await hr.Admin.Client.PatchAsync($"{hr.Department.Url}/request-forms/{formId}", new { version = set.Version, audience = "AllEmployees" })).Expect(HttpStatusCode.NotFound);
        Assert.Equal(HttpStatusCode.Forbidden, (await it.Agent.Client.PatchAsync($"{it.Department.Url}/request-forms/{formId}", new { version = set.Version, audience = "AllEmployees" })).Status);
        (await alice.User.Client.GetAsync($"/api/portal/forms/{formId}")).Expect(HttpStatusCode.OK); // unchanged by the failed attempts
    }

    private static string Normalise(string body, Guid id) =>
        System.Text.RegularExpressions.Regex.Replace(body.Replace(id.ToString(), "<id>", StringComparison.OrdinalIgnoreCase), "\"traceId\":\"[^\"]*\"", "\"traceId\":\"-\"");
}
