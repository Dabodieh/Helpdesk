using System.Net;
using System.Text.Json;
using Helpdesk.Host.Tests.Infrastructure;
using Helpdesk.Host.Tests.Ticketing;

namespace Helpdesk.Host.Tests.Mail;

/// <summary>Mailbox administration: permissions, department isolation, validation, defaults, concurrency, audit.</summary>
public sealed class MailAdminApiTests(HelpdeskFixture fixture) : IClassFixture<HelpdeskFixture>
{
    [Fact]
    public async Task Only_department_admins_and_platform_admins_manage_mailboxes_and_outsiders_get_404()
    {
        var a = await fixture.NewDeptAsync();
        var b = await fixture.NewDeptAsync();
        var box = await fixture.CreateMailboxAsync(a);
        var url = $"{a.Department.Url}/mailboxes";
        var outsider = await fixture.NewUserAsync();
        var platform = await fixture.PlatformAdminAsync();

        (await fixture.CreateAnonymousClient().GetAsync(url)).Expect(HttpStatusCode.Unauthorized);
        (await outsider.Client.GetAsync(url)).Expect(HttpStatusCode.NotFound);
        (await b.Admin.Client.GetAsync(url)).Expect(HttpStatusCode.NotFound);
        (await b.Admin.Client.GetAsync($"{url}/{box.Id}")).Expect(HttpStatusCode.NotFound);
        foreach (var user in new[] { a.Viewer, a.Agent, a.Lead })
        {
            (await user.Client.GetAsync(url)).Expect(HttpStatusCode.Forbidden);
            (await user.Client.PostAsync(url, new { address = MailKit.NewAddress(), displayName = "x" })).Expect(HttpStatusCode.Forbidden);
        }

        (await a.Admin.Client.GetAsync(url)).Expect(HttpStatusCode.OK);
        (await platform.Client.GetAsync(url)).Expect(HttpStatusCode.OK); // administrative permission: satisfied by the platform-admin flag
    }

    [Fact]
    public async Task Only_a_platform_admin_registers_a_mailbox_and_a_failed_access_probe_registers_nothing()
    {
        var d = await fixture.NewDeptAsync();
        var platform = await fixture.PlatformAdminAsync();
        var url = $"{d.Department.Url}/mailboxes";
        var address = MailKit.NewAddress();

        // A Department Admin cannot introduce an address (they keep managing registered mailboxes).
        (await d.Admin.Client.PostAsync(url, new { address, displayName = "Desk" })).Expect(HttpStatusCode.Forbidden);
        Assert.Equal(0, await fixture.CountAsync("SELECT count(*) FROM mail.mailboxes WHERE address_normalised = @a", TicketKit.P("a", address)));

        // The application cannot reach the mailbox (outside its Exchange scope): refused for everybody, a Platform Admin included, and nothing is created.
        fixture.Fake().DenyMailbox(address);
        var refused = (await platform.Client.PostAsync(url, new { address, displayName = "Desk" })).Expect(HttpStatusCode.BadRequest);
        Assert.Contains("address", refused.Json.GetProperty("errors").EnumerateObject().Select(p => p.Name));
        Assert.Equal(0, await fixture.CountAsync("SELECT count(*) FROM mail.mailboxes WHERE address_normalised = @a", TicketKit.P("a", address)));

        // An accessible mailbox registers.
        (await platform.Client.PostAsync(url, new { address = MailKit.NewAddress(), displayName = "Desk" })).Expect(HttpStatusCode.Created);
    }

    [Fact]
    public async Task A_mailbox_of_another_department_is_not_reachable_through_a_foreign_route()
    {
        var a = await fixture.NewDeptAsync();
        var b = await fixture.NewDeptAsync();
        var boxA = await fixture.CreateMailboxAsync(a);
        var dto = (await a.Admin.Client.GetAsync($"{a.Department.Url}/mailboxes/{boxA.Id}")).Expect(HttpStatusCode.OK);

        (await b.Admin.Client.PatchAsync($"{b.Department.Url}/mailboxes/{boxA.Id}", new { version = dto.Version, displayName = "Hijacked" })).Expect(HttpStatusCode.NotFound);
        (await b.Admin.Client.PatchAsync($"{b.Department.Url}/mailboxes/{Guid.NewGuid()}", new { version = dto.Version, displayName = "Ghost" })).Expect(HttpStatusCode.NotFound);

        Assert.Equal(boxA.DisplayName, (await a.Admin.Client.GetAsync($"{a.Department.Url}/mailboxes/{boxA.Id}")).Json.Str("displayName"));
        var listB = (await b.Admin.Client.GetAsync($"{b.Department.Url}/mailboxes")).Expect(HttpStatusCode.OK);
        Assert.Empty(listB.Json.EnumerateArray());
    }

    [Fact]
    public async Task Create_validates_input_and_addresses_are_unique_across_departments()
    {
        var a = await fixture.NewDeptAsync();
        var b = await fixture.NewDeptAsync();
        var box = await fixture.CreateMailboxAsync(a);
        var url = $"{a.Department.Url}/mailboxes";

        var platform = await fixture.PlatformAdminAsync();
        Assert.Contains("address", (await platform.Client.PostAsync(url, new { address = "not-an-address", displayName = "x" })).Expect(HttpStatusCode.BadRequest).Json.GetProperty("errors").EnumerateObject().Select(p => p.Name));
        Assert.Contains("displayName", (await platform.Client.PostAsync(url, new { address = MailKit.NewAddress(), displayName = "  " })).Expect(HttpStatusCode.BadRequest).Json.GetProperty("errors").EnumerateObject().Select(p => p.Name));

        // One mailbox belongs to exactly one department: the same address (any case) is refused everywhere.
        (await platform.Client.PostAsync(url, new { address = box.Address.ToUpperInvariant(), displayName = "dup" })).ExpectCode(HttpStatusCode.Conflict, "duplicate");
        (await platform.Client.PostAsync($"{b.Department.Url}/mailboxes", new { address = box.Address, displayName = "dup" })).ExpectCode(HttpStatusCode.Conflict, "duplicate");
    }

    [Fact]
    public async Task The_first_mailbox_is_the_default_and_the_default_can_move_but_only_to_an_active_mailbox()
    {
        var d = await fixture.NewDeptAsync();
        var first = await fixture.CreateMailboxAsync(d);
        var second = await fixture.CreateMailboxAsync(d);
        var url = $"{d.Department.Url}/mailboxes";

        var list = (await d.Admin.Client.GetAsync(url)).Expect(HttpStatusCode.OK).Json.EnumerateArray().ToList();
        Assert.True(list.Single(m => m.GetProperty("id").GetGuid() == first.Id).GetProperty("isDefault").GetBoolean());
        Assert.False(list.Single(m => m.GetProperty("id").GetGuid() == second.Id).GetProperty("isDefault").GetBoolean());

        var secondDto = (await d.Admin.Client.GetAsync($"{url}/{second.Id}")).Expect(HttpStatusCode.OK);
        var moved = (await d.Admin.Client.PatchAsync($"{url}/{second.Id}", new { version = secondDto.Version, isDefault = true })).Expect(HttpStatusCode.OK);
        Assert.True(moved.Json.GetProperty("isDefault").GetBoolean());
        Assert.False((await d.Admin.Client.GetAsync($"{url}/{first.Id}")).Json.GetProperty("isDefault").GetBoolean());

        var firstDto = (await d.Admin.Client.GetAsync($"{url}/{first.Id}")).Expect(HttpStatusCode.OK);
        var deactivated = (await d.Admin.Client.PatchAsync($"{url}/{first.Id}", new { version = firstDto.Version, isActive = false })).Expect(HttpStatusCode.OK);
        (await d.Admin.Client.PatchAsync($"{url}/{first.Id}", new { version = deactivated.Version, isDefault = true })).ExpectCode(HttpStatusCode.Conflict, "inactive");
    }

    [Fact]
    public async Task Updates_need_the_current_version_and_changes_are_audited_without_content()
    {
        var d = await fixture.NewDeptAsync();
        var box = await fixture.CreateMailboxAsync(d);
        var url = $"{d.Department.Url}/mailboxes/{box.Id}";
        var current = (await d.Admin.Client.GetAsync(url)).Expect(HttpStatusCode.OK);

        var renamed = (await d.Admin.Client.PatchAsync(url, new { version = current.Version, displayName = "New name", signature = "New signature" })).Expect(HttpStatusCode.OK);
        Assert.Equal("New name", renamed.Json.Str("displayName"));
        (await d.Admin.Client.PatchAsync(url, new { version = current.Version, displayName = "Stale" })).ExpectCode(HttpStatusCode.Conflict, "version_conflict");
        (await d.Admin.Client.PatchAsync(url, new { displayName = "No version" })).Expect(HttpStatusCode.BadRequest);
        (await d.Admin.Client.PatchAsync(url, new { version = renamed.Version })).Expect(HttpStatusCode.BadRequest);

        var audit = await fixture.AuditOfAsync(box.Id);
        Assert.Contains(audit, a => a is { Action: "mailbox.created", Category: "mail" } && a.DepartmentId == d.Id);
        Assert.Contains(audit, a => a is { Action: "mailbox.updated", ActorUserId: not null });
        Assert.DoesNotContain(audit, a => (a.Next ?? "").Contains("New signature", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_attention_list_and_retry_are_scoped_to_the_department()
    {
        var a = await fixture.NewDeptAsync();
        var b = await fixture.NewDeptAsync();
        var boxA = await fixture.CreateMailboxAsync(a);
        var subject = $"Bulk {Unique.Id()}";
        await fixture.ReceiveAsync(boxA, MailKit.Mail(MailKit.NewSender(), subject) with { Headers = new Dictionary<string, string> { ["Precedence"] = "bulk" } });

        var items = (await a.Admin.Client.GetAsync($"{a.Department.Url}/mail/attention")).Expect(HttpStatusCode.OK).Json.GetProperty("items").EnumerateArray().ToList();
        var ignored = Assert.Single(items, i => i.Str("subject") == subject);
        Assert.Equal("Ignored", ignored.Str("state"));
        Assert.Equal("precedence_bulk", ignored.Str("ignoreReason"));

        var otherItems = (await b.Admin.Client.GetAsync($"{b.Department.Url}/mail/attention")).Expect(HttpStatusCode.OK).Json.GetProperty("items");
        Assert.Empty(otherItems.EnumerateArray());
        (await a.Agent.Client.GetAsync($"{a.Department.Url}/mail/attention")).Expect(HttpStatusCode.Forbidden);
        var id = ignored.GetProperty("id").GetGuid();
        (await b.Admin.Client.PostAsync($"{b.Department.Url}/mail/{id}/retry")).Expect(HttpStatusCode.NotFound);
        (await a.Admin.Client.PostAsync($"{a.Department.Url}/mail/{id}/retry")).Expect(HttpStatusCode.NotFound); // ignored mail is not retryable
        (await a.Admin.Client.GetAsync($"{a.Department.Url}/mail/attention?limit=0")).Expect(HttpStatusCode.BadRequest);
        (await a.Admin.Client.GetAsync($"{a.Department.Url}/mail/attention?cursor=garbage")).Expect(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Mailbox_status_shows_the_subscription_and_sync_health()
    {
        var d = await fixture.NewDeptAsync();
        var box = await fixture.CreateMailboxAsync(d);
        await fixture.SyncAsync(box);

        var dto = (await d.Admin.Client.GetAsync($"{d.Department.Url}/mailboxes/{box.Id}")).Expect(HttpStatusCode.OK);

        Assert.Equal("Active", dto.Json.GetProperty("subscription").Str("state"));
        Assert.NotEqual(JsonValueKind.Null, dto.Json.GetProperty("lastSyncAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, dto.Json.GetProperty("lastSyncError").ValueKind);
        Assert.Equal(0, dto.Json.GetProperty("attentionCount").GetInt32());
    }
}
