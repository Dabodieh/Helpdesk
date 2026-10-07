#pragma warning disable CA1861 // one-off request payloads
using System.Net;
using System.Text.Json;
using Helpdesk.Host.Tests.Infrastructure;
using Helpdesk.Host.Tests.Ticketing;
using Helpdesk.Modules.Organisation.Contracts;
using Npgsql;

namespace Helpdesk.Host.Tests.Portal;

/// <summary>Knowledge maintenance (owner, review date, overdue/unowned views) and the requester-safe article links offered in public replies.</summary>
public sealed class KnowledgeMaintenanceTests(HelpdeskFixture fixture) : IClassFixture<HelpdeskFixture>
{
    private static string Base(Dept d) => $"{d.Department.Url}/knowledge";

    private static async Task<ApiResponse> ArticleAsync(Dept d, string title, string? visibility = null, Guid? categoryId = null) =>
        (await d.Admin.Client.PostAsync($"{Base(d)}/articles", new { title, summary = "Summary.", body = "Body text.", visibility, categoryId })).Expect(HttpStatusCode.Created);

    private static async Task<ApiResponse> PublishAsync(Dept d, ApiResponse article) =>
        (await d.Admin.Client.PostAsync($"{Base(d)}/articles/{article.Id}/publish", new { version = article.Version })).Expect(HttpStatusCode.OK);

    private static Task<ApiResponse> MaintainAsync(Dept d, ApiResponse article, Guid? owner, string? reviewOn) =>
        d.Admin.Client.PutAsync($"{Base(d)}/articles/{article.Id}/maintenance", new { ownerUserId = owner, reviewOn, version = article.Version });

    private static async Task<Guid[]> ListIdsAsync(Dept d, string query) =>
        [.. (await d.Admin.Client.GetAsync($"{Base(d)}/articles?{query}")).Expect(HttpStatusCode.OK).Json.EnumerateArray().Select(a => a.GetProperty("id").GetGuid())];

    [Fact]
    public async Task Owner_and_review_date_are_saved_overdue_is_published_only_and_ownership_is_reassigned_to_eligible_people_only()
    {
        var d = await fixture.NewDeptAsync();
        var other = await fixture.NewDeptAsync();
        var token = Unique.Id();
        var live = await PublishAsync(d, await ArticleAsync(d, $"Live {token}"));
        var future = await PublishAsync(d, await ArticleAsync(d, $"Future {token}"));
        var undated = await PublishAsync(d, await ArticleAsync(d, $"Undated {token}"));
        var draft = await ArticleAsync(d, $"Draft {token}");

        // New articles are owned by their creator; no review date is set automatically.
        Assert.Equal(d.Admin.Id, live.Json.GetProperty("ownerUserId").GetGuid());
        Assert.Equal(JsonValueKind.Null, live.Json.GetProperty("reviewOn").ValueKind);
        Assert.False(live.Json.GetProperty("reviewOverdue").GetBoolean());

        // Review dates: a past date on a published article is overdue; a future date, no date, and a draft with a past date are not.
        var overdue = (await MaintainAsync(d, live, d.Admin.Id, "2020-01-31")).Expect(HttpStatusCode.OK);
        Assert.Equal("2020-01-31", overdue.Json.GetProperty("reviewOn").GetString());
        Assert.True(overdue.Json.GetProperty("reviewOverdue").GetBoolean());
        (await MaintainAsync(d, future, d.Admin.Id, "2999-01-01")).Expect(HttpStatusCode.OK);
        (await MaintainAsync(d, draft, d.Admin.Id, "2020-01-31")).Expect(HttpStatusCode.OK);
        Assert.Equal([live.Id], await ListIdsAsync(d, "review=overdue"));
        Assert.Contains(undated.Id, await ListIdsAsync(d, ""));

        // Saving a new date is "reviewed": the article leaves the overdue view. The saved values read back from the article.
        var reviewed = (await MaintainAsync(d, overdue, d.Admin.Id, "2999-06-01")).Expect(HttpStatusCode.OK);
        Assert.False(reviewed.Json.GetProperty("reviewOverdue").GetBoolean());
        Assert.Empty(await ListIdsAsync(d, "review=overdue"));
        Assert.Equal("2999-06-01", (await d.Admin.Client.GetAsync($"{Base(d)}/articles/{live.Id}")).Json.GetProperty("reviewOn").GetString());

        // Validation and concurrency.
        (await MaintainAsync(d, reviewed, d.Admin.Id, "31/01/2020")).Expect(HttpStatusCode.BadRequest);
        (await MaintainAsync(d, overdue, d.Admin.Id, null)).Expect(HttpStatusCode.Conflict); // stale version
        (await d.Admin.Client.GetAsync($"{Base(d)}/articles?review=sideways")).Expect(HttpStatusCode.BadRequest);

        // Reassignment: only active members who can manage the department's knowledge; never an agent, an outsider or another department's admin.
        var admin2 = await fixture.NewMemberAsync(d.Department, DepartmentRoles.DepartmentAdmin);
        var owners = (await d.Admin.Client.GetAsync($"{Base(d)}/owners")).Expect(HttpStatusCode.OK).Json.EnumerateArray().Select(o => o.GetProperty("id").GetGuid()).ToArray();
        Assert.Contains(admin2.Id, owners);
        Assert.DoesNotContain(d.Agent.Id, owners);
        var moved = (await MaintainAsync(d, reviewed, admin2.Id, "2999-06-01")).Expect(HttpStatusCode.OK);
        Assert.Equal(admin2.Id, moved.Json.GetProperty("ownerUserId").GetGuid());
        Assert.False(string.IsNullOrEmpty(moved.Json.GetProperty("owner").GetString()));
        (await MaintainAsync(d, moved, d.Agent.Id, null)).Expect(HttpStatusCode.BadRequest);
        (await MaintainAsync(d, moved, other.Admin.Id, null)).Expect(HttpStatusCode.BadRequest);
        (await d.Agent.Client.PutAsync($"{Base(d)}/articles/{live.Id}/maintenance", new { ownerUserId = d.Agent.Id, version = moved.Version })).Expect(HttpStatusCode.Forbidden); // maintaining needs knowledge.manage
        (await other.Admin.Client.PutAsync($"{Base(d)}/articles/{live.Id}/maintenance", new { ownerUserId = admin2.Id, version = moved.Version })).Expect(HttpStatusCode.NotFound);

        // Unowned: cleared by hand, or an owner who is no longer an eligible member; the article reads as unowned and shows in the unowned view.
        var cleared = (await MaintainAsync(d, moved, null, null)).Expect(HttpStatusCode.OK);
        Assert.Equal(JsonValueKind.Null, cleared.Json.GetProperty("ownerUserId").ValueKind);
        await fixture.ExecuteAsync("UPDATE knowledge.articles SET owner_user_id = @o WHERE id = @a", new NpgsqlParameter("o", Guid.NewGuid()), new NpgsqlParameter("a", future.Id));
        var unowned = await ListIdsAsync(d, "review=unowned");
        Assert.Contains(live.Id, unowned);
        Assert.Contains(future.Id, unowned);
        Assert.DoesNotContain(undated.Id, unowned); // still owned by its creator
        Assert.Equal(JsonValueKind.Null, (await d.Admin.Client.GetAsync($"{Base(d)}/articles/{future.Id}")).Json.GetProperty("ownerUserId").ValueKind);
    }

    private static async Task<string[]> LinkTitlesAsync(TestUser agent, Guid ticketId, string q) =>
        [.. (await agent.Client.GetAsync($"/api/tickets/{ticketId}/article-links?q={Uri.EscapeDataString(q)}")).Expect(HttpStatusCode.OK).Json.EnumerateArray().Select(a => a.GetProperty("title").GetString()!)];

    [Fact]
    public async Task Reply_links_offer_only_what_the_linked_requester_can_open_and_follow_article_changes_immediately()
    {
        var d = await fixture.NewDeptAsync();
        var foreign = await fixture.NewDeptAsync();
        var token = Unique.Id();
        var requester = await fixture.EmployeeAsync("linked");
        var linkedTicket = await d.TicketForAsync(requester.Email);
        var everyone = await PublishAsync(d, await ArticleAsync(d, $"Everyone {token}"));
        var audience = await PublishAsync(d, await ArticleAsync(d, $"Requesters {token}", visibility: "Requesters"));
        await ArticleAsync(d, $"Draft {token}");
        var toArchive = await PublishAsync(d, await ArticleAsync(d, $"Archived {token}"));
        (await d.Admin.Client.PostAsync($"{Base(d)}/articles/{toArchive.Id}/archive", new { version = toArchive.Version })).Expect(HttpStatusCode.OK);
        var category = (await d.Admin.Client.PostAsync($"{Base(d)}/categories", new { name = $"Hidden {token}" })).Expect(HttpStatusCode.Created);
        await PublishAsync(d, await ArticleAsync(d, $"Inactive category {token}", categoryId: category.Id));
        (await d.Admin.Client.PatchAsync($"{Base(d)}/categories/{category.Id}", new { isActive = false, version = category.Version })).Expect(HttpStatusCode.OK);
        await PublishAsync(foreign, await ArticleAsync(foreign, $"Foreign {token}"));

        // Only published, openable articles of the ticket's own department: no draft, archived, inactive-category or foreign-department title.
        var offered = await LinkTitlesAsync(d.Agent, linkedTicket.Id, token);
        Assert.Equal([$"Everyone {token}", $"Requesters {token}"], offered.Order().ToArray());
        var first = (await d.Agent.Client.GetAsync($"/api/tickets/{linkedTicket.Id}/article-links?q={token}")).Json.EnumerateArray().First(a => a.GetProperty("title").GetString()!.StartsWith("Everyone", StringComparison.Ordinal));
        Assert.Equal($"/portal/knowledge/{first.GetProperty("slug").GetString()}", first.GetProperty("path").GetString());
        foreach (var a in (await d.Agent.Client.GetAsync($"/api/tickets/{linkedTicket.Id}/article-links?q={token}")).Json.EnumerateArray())
        {
            (await requester.User.Client.GetAsync("/api/portal/knowledge/articles/" + a.GetProperty("slug").GetString())).Expect(HttpStatusCode.OK); // the offered route really opens for the requester
        }

        // Unlinked / external requesters get no articles at all, however visible they are to the agent.
        var unlinked = await d.Agent.CreateTicketAsync(d.Department, TicketHelpers.NewTicketBody(email: $"external-{Unique.Id()}@example.test"));
        Assert.Empty(await LinkTitlesAsync(d.Agent, unlinked.Id, token));

        // Changes apply at once: a new title, an audience change and archiving all show on the next search.
        var edited = (await d.Admin.Client.PatchAsync($"{Base(d)}/articles/{everyone.Id}", new { version = everyone.Version, title = $"Renamed {token}" })).Expect(HttpStatusCode.OK);
        Assert.Contains($"Everyone {token}", await LinkTitlesAsync(d.Agent, linkedTicket.Id, token)); // unpublished edit: the published title is still offered
        var republished = await PublishAsync(d, edited);
        Assert.Contains($"Renamed {token}", await LinkTitlesAsync(d.Agent, linkedTicket.Id, token));
        (await d.Admin.Client.PatchAsync($"{Base(d)}/articles/{audience.Id}", new { version = audience.Version, visibility = "Everyone" })).Expect(HttpStatusCode.OK);
        Assert.Equal(2, (await LinkTitlesAsync(d.Agent, linkedTicket.Id, token)).Length); // still offered: this requester is a requester of the department
        (await d.Admin.Client.PostAsync($"{Base(d)}/articles/{republished.Id}/archive", new { version = republished.Version })).Expect(HttpStatusCode.OK);
        Assert.Equal([$"Requesters {token}"], await LinkTitlesAsync(d.Agent, linkedTicket.Id, token));

        // Authorization: members who can reply only; other departments and requesters see the ticket as missing.
        var viewer = await fixture.NewMemberAsync(d.Department, DepartmentRoles.Viewer);
        (await viewer.Client.GetAsync($"/api/tickets/{linkedTicket.Id}/article-links")).Expect(HttpStatusCode.Forbidden);
        (await foreign.Agent.Client.GetAsync($"/api/tickets/{linkedTicket.Id}/article-links")).Expect(HttpStatusCode.NotFound);
        (await requester.User.Client.GetAsync($"/api/tickets/{linkedTicket.Id}/article-links")).Expect(HttpStatusCode.NotFound);
    }
}
