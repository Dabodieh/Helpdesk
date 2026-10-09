using System.Net;
using Helpdesk.Host.Tests.Infrastructure;
using Helpdesk.Host.Tests.Ticketing;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Helpdesk.Host.Tests.Mail;

/// <summary>The platform-level Microsoft 365 page: platform administration only, identifiers never secrets, read-only checks, and a guided (not automatic) new-mailbox plan.</summary>
public sealed class Microsoft365Tests(HelpdeskFixture fixture) : IClassFixture<HelpdeskFixture>
{
    private const string Base = "/api/admin/microsoft-365";

    [Fact]
    public async Task Only_a_platform_administrator_can_read_or_run_anything_on_the_page()
    {
        var d = await fixture.NewDeptAsync();
        (await fixture.CreateAnonymousClient().GetAsync(Base)).Expect(HttpStatusCode.Unauthorized);
        foreach (var user in new[] { d.Admin, d.Lead, d.Agent, d.Viewer, await fixture.NewUserAsync() })
        {
            (await user.Client.GetAsync(Base)).Expect(HttpStatusCode.Forbidden);
            (await user.Client.PostAsync($"{Base}/check-connection", new { })).Expect(HttpStatusCode.Forbidden);
            (await user.Client.PostAsync($"{Base}/check-mailbox", new { address = MailKit.NewAddress() })).Expect(HttpStatusCode.Forbidden);
            (await user.Client.PostAsync($"{Base}/new-mailbox-plan", new { displayName = "x", alias = "x", domain = "example.org", departmentId = d.Department.Id })).Expect(HttpStatusCode.Forbidden);
        }
    }

    [Fact]
    public async Task Status_lists_registered_mailboxes_with_their_department_and_never_returns_a_secret()
    {
        var d = await fixture.NewDeptAsync();
        var box = await fixture.CreateMailboxAsync(d);
        var platform = await fixture.PlatformAdminAsync();

        var status = (await platform.Client.GetAsync(Base)).Expect(HttpStatusCode.OK);
        Assert.True(status.Json.GetProperty("simulated").GetBoolean());
        Assert.Contains("simulated_provider", status.Json.GetProperty("issues").EnumerateArray().Select(i => i.GetString()));
        Assert.False(status.Json.GetProperty("provisioning").GetProperty("available").GetBoolean());
        var row = Assert.Single(status.Json.GetProperty("mailboxes").EnumerateArray(), m => m.GetProperty("address").GetString() == box.Address);
        Assert.Equal(d.Department.Name, row.GetProperty("departmentName").GetString());
        Assert.Contains(status.Json.GetProperty("requiredPermissions").EnumerateArray(), p => !p.GetProperty("required").GetBoolean());

        var raw = status.Json.GetRawText();
        Assert.DoesNotContain("clientSecret", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("certificatePassword", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", raw.Replace("tokenize", "", StringComparison.OrdinalIgnoreCase), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Checking_a_mailbox_reads_only_and_reports_invalid_duplicate_denied_and_reachable()
    {
        var d = await fixture.NewDeptAsync();
        var existing = await fixture.CreateMailboxAsync(d);
        var platform = await fixture.PlatformAdminAsync();
        var denied = MailKit.NewAddress();
        fixture.Fake().DenyMailbox(denied);
        var reachable = MailKit.NewAddress();
        var before = await fixture.CountAsync("SELECT count(*) FROM mail.mailboxes");

        var invalid = (await platform.Client.PostAsync($"{Base}/check-mailbox", new { address = "not an address" })).Expect(HttpStatusCode.OK).Json;
        Assert.False(invalid.GetProperty("valid").GetBoolean());
        Assert.Equal("skipped", invalid.GetProperty("access").GetString());

        var duplicate = (await platform.Client.PostAsync($"{Base}/check-mailbox", new { address = existing.Address.ToUpperInvariant() })).Expect(HttpStatusCode.OK).Json;
        Assert.Equal("already_registered", duplicate.GetProperty("problem").GetString());
        Assert.Equal(d.Department.Name, duplicate.GetProperty("registeredDepartmentName").GetString());

        Assert.Equal("denied", (await platform.Client.PostAsync($"{Base}/check-mailbox", new { address = denied })).Expect(HttpStatusCode.OK).Json.GetProperty("access").GetString());
        Assert.Equal("ok", (await platform.Client.PostAsync($"{Base}/check-mailbox", new { address = reachable })).Expect(HttpStatusCode.OK).Json.GetProperty("access").GetString());

        Assert.Equal(before, await fixture.CountAsync("SELECT count(*) FROM mail.mailboxes")); // checks register nothing
        Assert.True(await fixture.CountAsync("SELECT count(*) FROM audit.audit_events WHERE action = 'm365.mailbox_checked'") >= 3);
    }

    [Fact]
    public async Task The_connection_check_reports_a_short_code()
    {
        var platform = await fixture.PlatformAdminAsync();
        var result = (await platform.Client.PostAsync($"{Base}/check-connection", new { })).Expect(HttpStatusCode.OK).Json;
        Assert.True(result.GetProperty("ok").GetBoolean());
        Assert.Equal("ok", result.GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_new_mailbox_plan_is_a_validated_script_for_a_tenant_administrator_and_creates_nothing()
    {
        var d = await fixture.NewDeptAsync();
        var existing = await fixture.CreateMailboxAsync(d);
        var platform = await fixture.PlatformAdminAsync();
        var before = await fixture.CountAsync("SELECT count(*) FROM mail.mailboxes");
        var alias = $"desk-{Unique.Id()}";

        var plan = (await platform.Client.PostAsync($"{Base}/new-mailbox-plan", new { displayName = "IT Support Desk", alias, domain = "Example.org", departmentId = d.Department.Id })).Expect(HttpStatusCode.OK).Json;
        Assert.True(plan.GetProperty("valid").GetBoolean());
        Assert.Equal($"{alias}@example.org", plan.GetProperty("address").GetString());
        Assert.False(plan.GetProperty("provisioning").GetProperty("available").GetBoolean()); // Helpdesk never creates the mailbox
        var script = string.Join('\n', plan.GetProperty("script").EnumerateArray().Select(l => l.GetString()));
        Assert.Contains("New-Mailbox -Shared", script, StringComparison.Ordinal);
        Assert.Contains($"-PrimarySmtpAddress \"{alias}@example.org\"", script, StringComparison.Ordinal);

        // Anything that PowerShell would treat as syntax is refused, so the script cannot be made to run something else.
        foreach (var hostile in new[] { "Desk\"; Remove-Mailbox x", "Desk `n", "$(whoami)", "a|b", "a;b", "Desk\nName" })
        {
            var refused = (await platform.Client.PostAsync($"{Base}/new-mailbox-plan", new { displayName = hostile, alias, domain = "example.org", departmentId = d.Department.Id })).Expect(HttpStatusCode.OK).Json;
            Assert.False(refused.GetProperty("valid").GetBoolean());
            Assert.Empty(refused.GetProperty("script").EnumerateArray());
            Assert.True(refused.GetProperty("problems").TryGetProperty("displayName", out _));
        }

        var local = existing.Address.Split('@');
        var clash = (await platform.Client.PostAsync($"{Base}/new-mailbox-plan", new { displayName = "Desk", alias = local[0], domain = local[1], departmentId = d.Department.Id })).Expect(HttpStatusCode.OK).Json;
        Assert.False(clash.GetProperty("valid").GetBoolean());
        Assert.True(clash.GetProperty("problems").TryGetProperty("alias", out _));

        var noDepartment = (await platform.Client.PostAsync($"{Base}/new-mailbox-plan", new { displayName = "Desk", alias, domain = "example.org", departmentId = Guid.NewGuid() })).Expect(HttpStatusCode.OK).Json;
        Assert.True(noDepartment.GetProperty("problems").TryGetProperty("departmentId", out _));

        Assert.Equal(before, await fixture.CountAsync("SELECT count(*) FROM mail.mailboxes"));
    }

    [Fact]
    public async Task When_approved_domains_are_configured_a_new_mailbox_plan_must_use_one()
    {
        var d = await fixture.NewDeptAsync();
        await using var restricted = new HelpdeskFactory(fixture.ConnectionString).WithWebHostBuilder(b => b.UseSetting("Mail:ApprovedDomains:0", "samh.example"));
        var platform = await fixture.SignInAsync(HelpdeskFactory.PlatformAdminSubject, "Platform Admin", factory: restricted);

        var wrong = (await platform.Client.PostAsync($"{Base}/new-mailbox-plan", new { displayName = "Desk", alias = $"d-{Unique.Id()}", domain = "other.example", departmentId = d.Department.Id })).Expect(HttpStatusCode.OK).Json;
        Assert.False(wrong.GetProperty("valid").GetBoolean());
        Assert.True(wrong.GetProperty("problems").TryGetProperty("domain", out _));

        var right = (await platform.Client.PostAsync($"{Base}/new-mailbox-plan", new { displayName = "Desk", alias = $"d-{Unique.Id()}", domain = "samh.example", departmentId = d.Department.Id })).Expect(HttpStatusCode.OK).Json;
        Assert.True(right.GetProperty("valid").GetBoolean());
    }
}
