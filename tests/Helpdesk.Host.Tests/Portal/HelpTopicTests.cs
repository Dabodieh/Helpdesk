#pragma warning disable CA1861 // one-off request payloads
using System.Net;
using System.Text.Json;
using Helpdesk.Host.Tests.Infrastructure;
using Helpdesk.Host.Tests.Ticketing;

namespace Helpdesk.Host.Tests.Portal;

/// <summary>Help topics are a thin catalogue layer: usable exactly when their request form is usable by the caller; department-owned; never a way around a form audience.</summary>
public sealed class HelpTopicTests(HelpdeskFixture fixture) : IClassFixture<HelpdeskFixture>
{
    private static async Task<ApiResponse> TopicAsync(Dept d, string name, Guid formId, string? description = null, bool? isActive = null) =>
        (await d.Admin.Client.PostAsync($"{d.Department.Url}/help-topics", new { name, description, formId, isActive })).Expect(HttpStatusCode.Created);

    private static async Task<JsonElement> CatalogueAsync(TestUser user) => (await user.Client.GetAsync("/api/portal/help-topics")).Expect(HttpStatusCode.OK).Json;

    private static string[] Names(JsonElement list) => [.. list.EnumerateArray().Select(t => t.Str("name"))];

    /// <summary>The shared test database holds other tests' topics: look only at this department's.</summary>
    private static async Task<JsonElement[]> MineAsync(TestUser user, Dept d) => [.. (await CatalogueAsync(user)).EnumerateArray().Where(t => t.Str("department") == d.Department.Name)];

    [Fact]
    public async Task A_topic_is_offered_only_while_it_is_active_and_its_form_is_usable_and_opens_that_form()
    {
        var d = await fixture.NewDeptAsync();
        var alice = await fixture.EmployeeAsync("alice");
        var (formId, _) = await d.FormAsync("Hardware issue");
        var topic = await TopicAsync(d, "Report an IT problem", formId, "Something is broken");

        var list = await MineAsync(alice.User, d);
        Assert.Equal(["Report an IT problem"], list.Select(t => t.Str("name")).ToArray());
        var item = list[0];
        Assert.Equal(formId, item.GetProperty("formId").GetGuid());
        Assert.Equal(d.Department.Name, item.Str("department"));
        Assert.DoesNotContain("audience", item.GetRawText(), StringComparison.OrdinalIgnoreCase); // no form or routing detail beyond the form id

        // Direct access goes through the same decision, then the form itself opens and accepts the submission (the existing workflow).
        (await alice.User.Client.GetAsync($"/api/portal/help-topics/{topic.Id}")).Expect(HttpStatusCode.OK);
        (await alice.User.Client.GetAsync($"/api/portal/forms/{formId}")).Expect(HttpStatusCode.OK);
        (await alice.User.Client.PostAsync("/api/portal/requests", new { formId, subject = "Via topic", description = "x", submissionId = Guid.NewGuid() })).Expect(HttpStatusCode.Created);

        // An inactive topic is hidden, and so is a topic whose form is deactivated or no longer offered.
        var current = (await d.Admin.Client.GetAsync($"{d.Department.Url}/help-topics")).Expect(HttpStatusCode.OK).Json[0];
        (await d.Admin.Client.PatchAsync($"{d.Department.Url}/help-topics/{topic.Id}", new { version = current.Str("version"), isActive = false })).Expect(HttpStatusCode.OK);
        Assert.Empty(await MineAsync(alice.User, d));
        (await alice.User.Client.GetAsync($"/api/portal/help-topics/{topic.Id}")).Expect(HttpStatusCode.NotFound);
        current = (await d.Admin.Client.GetAsync($"{d.Department.Url}/help-topics")).Json[0];
        (await d.Admin.Client.PatchAsync($"{d.Department.Url}/help-topics/{topic.Id}", new { version = current.Str("version"), isActive = true })).Expect(HttpStatusCode.OK);
        Assert.Single(await MineAsync(alice.User, d));

        var forms = (await d.Admin.Client.GetAsync($"{d.Department.Url}/request-forms")).Json[0];
        (await d.Admin.Client.PatchAsync($"{d.Department.Url}/request-forms/{formId}", new { version = forms.Str("version"), isActive = false })).Expect(HttpStatusCode.OK);
        Assert.Empty(await MineAsync(alice.User, d));
        (await alice.User.Client.GetAsync($"/api/portal/help-topics/{topic.Id}")).Expect(HttpStatusCode.NotFound);
        Assert.False((await d.Admin.Client.GetAsync($"{d.Department.Url}/help-topics")).Json[0].GetProperty("formAvailable").GetBoolean()); // the administrator is told
    }

    [Fact]
    public async Task A_topic_never_widens_a_form_audience_and_changing_the_audience_hides_it_at_once()
    {
        var d = await fixture.NewDeptAsync();
        var alice = await fixture.EmployeeAsync("alice");
        var bob = await fixture.EmployeeAsync("bob");
        var (formId, _) = await d.FormAsync("Privileged access", new { Audience = "Selected", AudienceUserIds = new[] { alice.User.Id } });
        var topic = await TopicAsync(d, "Request access", formId);

        Assert.Equal(["Request access"], (await MineAsync(alice.User, d)).Select(t => t.Str("name")).ToArray());
        Assert.Empty(await MineAsync(bob.User, d)); // the topic does not exist for someone outside the form's audience
        var denied = (await bob.User.Client.GetAsync($"/api/portal/help-topics/{topic.Id}")).Expect(HttpStatusCode.NotFound);
        var missing = (await bob.User.Client.GetAsync($"/api/portal/help-topics/{Guid.NewGuid()}")).Expect(HttpStatusCode.NotFound);
        Assert.Equal(System.Text.RegularExpressions.Regex.Replace(missing.Body, "\"traceId\":\"[^\"]*\"", ""), System.Text.RegularExpressions.Regex.Replace(denied.Body, "\"traceId\":\"[^\"]*\"", ""));
        (await bob.User.Client.GetAsync($"/api/portal/forms/{formId}")).Expect(HttpStatusCode.NotFound);

        // The audience is withdrawn: Alice loses the topic immediately.
        var form = (await d.Admin.Client.GetAsync($"{d.Department.Url}/request-forms")).Json[0];
        (await d.Admin.Client.PatchAsync($"{d.Department.Url}/request-forms/{formId}", new { version = form.Str("version"), audience = "Selected", audienceUserIds = Array.Empty<Guid>() })).Expect(HttpStatusCode.OK);
        Assert.Empty(await MineAsync(alice.User, d));
        (await alice.User.Client.GetAsync($"/api/portal/help-topics/{topic.Id}")).Expect(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Topics_belong_to_one_department_and_are_ordered_and_managed_only_by_its_forms_administrators()
    {
        var it = await fixture.NewDeptAsync();
        var hr = await fixture.NewDeptAsync();
        var (itForm, _) = await it.FormAsync("IT form");
        var (hrForm, _) = await hr.FormAsync("HR form");
        var a = await TopicAsync(it, "First", itForm);
        var b = await TopicAsync(it, "Second", itForm);
        var c = await TopicAsync(it, "Third", itForm);

        // A topic can only point at a form of its own department; another department's administrator cannot see or change it.
        (await it.Admin.Client.PostAsync($"{it.Department.Url}/help-topics", new { name = "Crossed", formId = hrForm })).Expect(HttpStatusCode.BadRequest);
        (await hr.Admin.Client.GetAsync($"{it.Department.Url}/help-topics")).Expect(HttpStatusCode.NotFound);
        (await hr.Admin.Client.PatchAsync($"{it.Department.Url}/help-topics/{a.Id}", new { version = a.Version, name = "hijack" })).Expect(HttpStatusCode.NotFound);
        (await hr.Admin.Client.PatchAsync($"{hr.Department.Url}/help-topics/{a.Id}", new { version = a.Version, name = "hijack" })).Expect(HttpStatusCode.NotFound);
        Assert.Equal(HttpStatusCode.Forbidden, (await it.Agent.Client.GetAsync($"{it.Department.Url}/help-topics")).Status);
        (await it.Admin.Client.PostAsync($"{it.Department.Url}/help-topics", new { name = "First", formId = itForm })).Expect(HttpStatusCode.Conflict); // names are unique per department

        // Reordering: every topic exactly once; the requester catalogue follows the order.
        (await it.Admin.Client.PutAsync($"{it.Department.Url}/help-topics/order", new { ids = new[] { c.Id, a.Id } })).Expect(HttpStatusCode.BadRequest);
        var ordered = (await it.Admin.Client.PutAsync($"{it.Department.Url}/help-topics/order", new { ids = new[] { c.Id, a.Id, b.Id } })).Expect(HttpStatusCode.OK).Json;
        Assert.Equal(["Third", "First", "Second"], Names(ordered));
        var alice = await fixture.EmployeeAsync("alice");
        var mine = (await MineAsync(alice.User, it)).Select(t => t.Str("name")).ToArray();
        Assert.Equal(["Third", "First", "Second"], mine);

        // A requester sees topics of other departments too (their forms are usable) but never anything about this department's management.
        Assert.DoesNotContain("version", (await alice.User.Client.GetAsync("/api/portal/help-topics")).Body, StringComparison.Ordinal);
    }
}
