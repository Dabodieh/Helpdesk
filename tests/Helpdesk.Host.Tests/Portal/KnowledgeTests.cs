#pragma warning disable CA1861 // one-off request payloads
using System.Net;
using System.Text.Json;
using Helpdesk.Host.Tests.Infrastructure;
using Helpdesk.Host.Tests.Mail;
using Helpdesk.Host.Tests.Ticketing;

namespace Helpdesk.Host.Tests.Portal;

/// <summary>Knowledge base: draft/published/archived lifecycle, safe editing of published articles, PostgreSQL search, audiences, feedback, administration and audit.</summary>
public sealed class KnowledgeTests(HelpdeskFixture fixture) : IClassFixture<HelpdeskFixture>
{
    private static string Base(Dept d) => $"{d.Department.Url}/knowledge";

    private static async Task<ApiResponse> ArticleAsync(Dept d, string title, string body = "Body text.", string summary = "A summary.", string? visibility = null, Guid? categoryId = null) =>
        (await d.Admin.Client.PostAsync($"{Base(d)}/articles", new { title, summary, body, visibility, categoryId })).Expect(HttpStatusCode.Created);

    private static async Task<ApiResponse> PublishAsync(Dept d, ApiResponse article) =>
        (await d.Admin.Client.PostAsync($"{Base(d)}/articles/{article.Id}/publish", new { version = article.Version })).Expect(HttpStatusCode.OK);

    private static async Task<JsonElement> SearchAsync(PortalKit.Employee e, string query) =>
        (await e.User.Client.GetAsync($"/api/portal/knowledge/articles?q={Uri.EscapeDataString(query)}")).Expect(HttpStatusCode.OK).Json;

    private static string[] Titles(JsonElement page) => [.. page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("title").GetString()!)];

    [Fact]
    public async Task Drafts_are_invisible_published_edits_stay_private_until_published_and_archived_articles_disappear()
    {
        var d = await fixture.NewDeptAsync();
        var reader = await fixture.EmployeeAsync("reader");
        var token = Unique.Id();
        var draft = await ArticleAsync(d, $"Printer setup {token}", "Install the driver.");
        var slug = draft.Json.GetProperty("slug").GetString()!;

        // A draft does not exist for requesters: not by direct address, not in search, not in browse, not in feedback.
        (await reader.User.Client.GetAsync($"/api/portal/knowledge/articles/{slug}")).Expect(HttpStatusCode.NotFound);
        Assert.Empty(Titles(await SearchAsync(reader, $"printer setup {token}")));
        Assert.DoesNotContain(token, (await reader.User.Client.GetAsync("/api/portal/knowledge/articles?limit=25")).Expect(HttpStatusCode.OK).Body, StringComparison.Ordinal);
        (await reader.User.Client.PutAsync($"/api/portal/knowledge/articles/{slug}/feedback", new { helpful = true })).Expect(HttpStatusCode.NotFound);

        // Publish: now readable and searchable.
        var published = await PublishAsync(d, draft);
        Assert.Equal("Published", published.Json.GetProperty("status").GetString());
        Assert.Equal("Install the driver.", (await reader.User.Client.GetAsync($"/api/portal/knowledge/articles/{slug}")).Expect(HttpStatusCode.OK).Json.GetProperty("body").GetString());
        Assert.Contains($"Printer setup {token}", Titles(await SearchAsync(reader, $"printer setup {token}")));

        // Editing a published article changes only the draft: requesters keep reading the published text, and search matches only published words.
        var edited = (await d.Admin.Client.PatchAsync($"{Base(d)}/articles/{draft.Id}", new { version = published.Version, title = $"Printer setup v2 {token}", body = "UNPUBLISHEDWORD secret edit" })).Expect(HttpStatusCode.OK);
        Assert.True(edited.Json.GetProperty("hasUnpublishedChanges").GetBoolean());
        var live = (await reader.User.Client.GetAsync($"/api/portal/knowledge/articles/{slug}")).Expect(HttpStatusCode.OK).Json;
        Assert.Equal($"Printer setup {token}", live.GetProperty("title").GetString());
        Assert.DoesNotContain("UNPUBLISHEDWORD", live.GetRawText(), StringComparison.Ordinal);
        Assert.Empty(Titles(await SearchAsync(reader, "UNPUBLISHEDWORD")));

        // Republish: the new text goes live in one step.
        var again = await PublishAsync(d, edited);
        Assert.False(again.Json.GetProperty("hasUnpublishedChanges").GetBoolean());
        Assert.Contains("UNPUBLISHEDWORD", (await reader.User.Client.GetAsync($"/api/portal/knowledge/articles/{slug}")).Expect(HttpStatusCode.OK).Body, StringComparison.Ordinal);

        // Archive: gone for requesters, still there for administrators; a draft cannot be archived.
        var archived = (await d.Admin.Client.PostAsync($"{Base(d)}/articles/{draft.Id}/archive", new { version = again.Version })).Expect(HttpStatusCode.OK);
        (await reader.User.Client.GetAsync($"/api/portal/knowledge/articles/{slug}")).Expect(HttpStatusCode.NotFound);
        Assert.Empty(Titles(await SearchAsync(reader, "UNPUBLISHEDWORD")));
        Assert.Equal("Archived", (await d.Admin.Client.GetAsync($"{Base(d)}/articles/{draft.Id}")).Expect(HttpStatusCode.OK).Json.GetProperty("status").GetString());
        var second = await ArticleAsync(d, $"Second {token}");
        (await d.Admin.Client.PostAsync($"{Base(d)}/articles/{second.Id}/archive", new { version = second.Version })).Expect(HttpStatusCode.Conflict);
        Assert.NotNull(archived);
    }

    [Fact]
    public async Task Search_uses_the_published_text_ranks_by_relevance_pages_and_is_injection_safe()
    {
        var d = await fixture.NewDeptAsync();
        var reader = await fixture.EmployeeAsync("reader");
        var token = Unique.Id();
        var a = await PublishAsync(d, await ArticleAsync(d, $"Reset your password {token}", "Use the self service page.", "Password reset steps"));
        await PublishAsync(d, await ArticleAsync(d, $"VPN {token}", $"If you cannot reset access, see the password article {token}.", "Remote access"));
        await PublishAsync(d, await ArticleAsync(d, $"Unrelated {token}", "Printing guide.", "Paper"));
        Assert.NotNull(a);

        var results = Titles(await SearchAsync(reader, $"passw {token}")); // partial word while typing
        Assert.Equal([$"Reset your password {token}", $"VPN {token}"], results.OrderBy(t => t.StartsWith("Reset", StringComparison.Ordinal) ? 0 : 1).ToArray());
        Assert.DoesNotContain($"Unrelated {token}", results);
        Assert.Equal($"Reset your password {token}", results[0]); // title and summary match outrank a body-only match

        // Cards carry the summary only: no body fragment is ever returned by search.
        var first = (await SearchAsync(reader, $"password {token}")).GetProperty("items")[0];
        Assert.Equal("slug summary title department category".Split(' ').Order().ToArray(), first.EnumerateObject().Select(p => p.Name).Order().ToArray());

        // Pagination and bad input.
        var page1 = (await reader.User.Client.GetAsync($"/api/portal/knowledge/articles?q={token}&limit=1")).Expect(HttpStatusCode.OK).Json;
        Assert.Single(page1.GetProperty("items").EnumerateArray());
        var cursor = page1.GetProperty("nextCursor").GetString()!;
        var page2 = (await reader.User.Client.GetAsync($"/api/portal/knowledge/articles?q={token}&limit=1&cursor={cursor}")).Expect(HttpStatusCode.OK).Json;
        Assert.NotEqual(page1.GetProperty("items")[0].GetProperty("slug").GetString(), page2.GetProperty("items")[0].GetProperty("slug").GetString());
        (await reader.User.Client.GetAsync("/api/portal/knowledge/articles?limit=1000")).Expect(HttpStatusCode.BadRequest);
        (await reader.User.Client.GetAsync("/api/portal/knowledge/articles?cursor=abc")).Expect(HttpStatusCode.BadRequest);
        (await reader.User.Client.GetAsync($"/api/portal/knowledge/articles?q={new string('x', 101)}")).Expect(HttpStatusCode.BadRequest);
        foreach (var hostile in new[] { "'; DROP TABLE x;--", "a & b | !c", "(((", ":*", "\\", "\"quoted\"", "!!", "&", "<script>alert(1)</script>" })
        {
            Assert.Equal(HttpStatusCode.OK, (await reader.User.Client.GetAsync($"/api/portal/knowledge/articles?q={Uri.EscapeDataString(hostile)}")).Status);
        }
    }

    [Fact]
    public async Task Audiences_restrict_requester_only_articles_to_requesters_of_that_department()
    {
        var a = await fixture.NewDeptAsync();
        var b = await fixture.NewDeptAsync();
        var token = Unique.Id();
        await PublishAsync(a, await ArticleAsync(a, $"Everyone A {token}"));
        await PublishAsync(a, await ArticleAsync(a, $"Requesters of A {token}", visibility: "Requesters"));
        var stranger = await fixture.EmployeeAsync("stranger");
        var requesterOfA = await fixture.EmployeeAsync("reqa");
        var requesterOfB = await fixture.EmployeeAsync("reqb");
        await a.TicketForAsync(requesterOfA.Email);
        await b.TicketForAsync(requesterOfB.Email);

        Assert.Equal([$"Everyone A {token}"], Titles(await SearchAsync(stranger, token)));
        Assert.Equal([$"Everyone A {token}"], Titles(await SearchAsync(requesterOfB, token)));
        Assert.Equal(2, Titles(await SearchAsync(requesterOfA, token)).Length);
        var slug = (await a.Admin.Client.GetAsync($"{Base(a)}/articles?q=Requesters")).Json.EnumerateArray().First(i => i.GetProperty("title").GetString()!.Contains(token, StringComparison.Ordinal)).GetProperty("slug").GetString();
        (await stranger.User.Client.GetAsync($"/api/portal/knowledge/articles/{slug}")).Expect(HttpStatusCode.NotFound);
        (await requesterOfB.User.Client.GetAsync($"/api/portal/knowledge/articles/{slug}")).Expect(HttpStatusCode.NotFound);
        (await requesterOfA.User.Client.GetAsync($"/api/portal/knowledge/articles/{slug}")).Expect(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Categories_group_visible_articles_and_inactive_categories_hide_their_articles()
    {
        var d = await fixture.NewDeptAsync();
        var reader = await fixture.EmployeeAsync("reader");
        var cat = (await d.Admin.Client.PostAsync($"{Base(d)}/categories", new { name = $"Accounts {Unique.Id()}", description = "Passwords and access" })).Expect(HttpStatusCode.Created).Json;
        var categoryId = cat.GetProperty("id").GetGuid();
        var article = await PublishAsync(d, await ArticleAsync(d, $"In a category {Unique.Id()}", categoryId: categoryId));
        await ArticleAsync(d, $"Draft in category {Unique.Id()}", categoryId: categoryId);

        var visible = (await reader.User.Client.GetAsync("/api/portal/knowledge/categories")).Expect(HttpStatusCode.OK).Json.EnumerateArray()
            .Single(c => c.GetProperty("id").GetGuid() == categoryId);
        Assert.Equal(1, visible.GetProperty("articleCount").GetInt32()); // the draft is not counted
        Assert.Equal(d.Department.Name, visible.GetProperty("department").GetString());
        Assert.Single((await reader.User.Client.GetAsync($"/api/portal/knowledge/articles?categoryId={categoryId}")).Expect(HttpStatusCode.OK).Json.GetProperty("items").EnumerateArray());

        (await d.Admin.Client.PatchAsync($"{Base(d)}/categories/{categoryId}", new { version = cat.GetProperty("version").GetString(), isActive = false })).Expect(HttpStatusCode.OK);
        Assert.DoesNotContain((await reader.User.Client.GetAsync("/api/portal/knowledge/categories")).Expect(HttpStatusCode.OK).Json.EnumerateArray(), c => c.GetProperty("id").GetGuid() == categoryId);
        (await reader.User.Client.GetAsync($"/api/portal/knowledge/articles/{article.Json.GetProperty("slug").GetString()}")).Expect(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Feedback_is_one_latest_answer_per_person_and_counted_for_administrators()
    {
        var d = await fixture.NewDeptAsync();
        var one = await fixture.EmployeeAsync("one");
        var two = await fixture.EmployeeAsync("two");
        var article = await PublishAsync(d, await ArticleAsync(d, $"Useful {Unique.Id()}"));
        var slug = article.Json.GetProperty("slug").GetString();

        (await one.User.Client.PutAsync($"/api/portal/knowledge/articles/{slug}/feedback", new { helpful = false })).Expect(HttpStatusCode.OK);
        var changed = (await one.User.Client.PutAsync($"/api/portal/knowledge/articles/{slug}/feedback", new { helpful = true })).Expect(HttpStatusCode.OK).Json;
        Assert.True(changed.GetProperty("myFeedback").GetBoolean());
        (await two.User.Client.PutAsync($"/api/portal/knowledge/articles/{slug}/feedback", new { helpful = false })).Expect(HttpStatusCode.OK);
        (await two.User.Client.PutAsync($"/api/portal/knowledge/articles/{slug}/feedback", new { })).Expect(HttpStatusCode.BadRequest);

        var admin = (await d.Admin.Client.GetAsync($"{Base(d)}/articles/{article.Id}")).Expect(HttpStatusCode.OK).Json;
        Assert.Equal(1, admin.GetProperty("helpful").GetInt32());
        Assert.Equal(1, admin.GetProperty("notHelpful").GetInt32());
    }

    [Fact]
    public async Task Knowledge_administration_needs_the_permission_stays_in_its_department_validates_and_is_audited()
    {
        var d = await fixture.NewDeptAsync();
        var other = await fixture.NewDeptAsync();
        var article = await ArticleAsync(d, $"Admin {Unique.Id()}");

        // Only the department administrator (not leads, agents or viewers) manages knowledge; other departments' administrators see nothing.
        foreach (var user in new[] { d.Lead, d.Agent, d.Viewer })
        {
            (await user.Client.GetAsync($"{Base(d)}/articles")).Expect(HttpStatusCode.Forbidden);
            (await user.Client.PostAsync($"{Base(d)}/articles", new { title = "x" })).Expect(HttpStatusCode.Forbidden);
        }

        (await other.Admin.Client.GetAsync($"{Base(d)}/articles")).Expect(HttpStatusCode.NotFound);
        (await other.Admin.Client.GetAsync($"{Base(other)}/articles/{article.Id}")).Expect(HttpStatusCode.NotFound);
        (await other.Admin.Client.PatchAsync($"{Base(other)}/articles/{article.Id}", new { version = article.Version, title = "hijack" })).Expect(HttpStatusCode.NotFound);

        // Validation, optimistic concurrency, publishing needs content, categories belong to the department.
        (await d.Admin.Client.PostAsync($"{Base(d)}/articles", new { title = "" })).Expect(HttpStatusCode.BadRequest);
        (await d.Admin.Client.PostAsync($"{Base(d)}/articles", new { title = "t", visibility = "Public" })).Expect(HttpStatusCode.BadRequest);
        (await d.Admin.Client.PostAsync($"{Base(d)}/articles", new { title = "t", categoryId = Guid.NewGuid() })).Expect(HttpStatusCode.BadRequest);
        (await d.Admin.Client.PatchAsync($"{Base(d)}/articles/{article.Id}", new { version = "stale", title = "x" })).Expect(HttpStatusCode.BadRequest);
        var saved = (await d.Admin.Client.PatchAsync($"{Base(d)}/articles/{article.Id}", new { version = article.Version, summary = "s" })).Expect(HttpStatusCode.OK);
        (await d.Admin.Client.PatchAsync($"{Base(d)}/articles/{article.Id}", new { version = article.Version, summary = "again" })).Expect(HttpStatusCode.Conflict);
        var empty = (await d.Admin.Client.PostAsync($"{Base(d)}/articles", new { title = "No body" })).Expect(HttpStatusCode.Created);
        (await d.Admin.Client.PostAsync($"{Base(d)}/articles/{empty.Id}/publish", new { version = empty.Version })).Expect(HttpStatusCode.BadRequest);
        (await d.Admin.Client.PostAsync($"{Base(d)}/categories", new { name = "" })).Expect(HttpStatusCode.BadRequest);
        (await d.Admin.Client.PostAsync($"{Base(d)}/categories", new { name = "Dup" })).Expect(HttpStatusCode.Created);
        (await d.Admin.Client.PostAsync($"{Base(d)}/categories", new { name = "Dup" })).Expect(HttpStatusCode.Conflict);
        await PublishAsync(d, saved);

        // Slugs are unique and stable; administrative history lives in the audit stream, not in tickets.
        var twin = await ArticleAsync(d, article.Json.GetProperty("draft").GetProperty("title").GetString()!);
        Assert.NotEqual(article.Json.GetProperty("slug").GetString(), twin.Json.GetProperty("slug").GetString());
        Assert.True(await fixture.CountAsync("SELECT count(*) FROM audit.audit_events WHERE department_id = @d AND category = 'knowledge' AND action IN ('knowledge.article_created','knowledge.article_updated','knowledge.article_published','knowledge.category_created')", TicketKit.P("d", d.Id)) >= 5);
    }
}
