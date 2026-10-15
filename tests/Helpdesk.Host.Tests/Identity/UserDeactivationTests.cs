using System.Net;
using Helpdesk.Host.Tests.Infrastructure;
using Helpdesk.Modules.Audit.Contracts;
using Helpdesk.Modules.Identity.Authentication;
using Helpdesk.Modules.Identity.Contracts;
using Helpdesk.Modules.Identity.Persistence;
using Helpdesk.Modules.Identity.Services;
using Helpdesk.Modules.Organisation.Contracts;
using Helpdesk.SharedKernel.Errors;
using Helpdesk.SharedKernel.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Helpdesk.Host.Tests.Identity;

/// <summary>S-01: authorised, audited deactivation; immediate and permanent session revocation; absolute session lifetime; no silent reactivation.</summary>
public sealed class UserDeactivationTests(HelpdeskFixture fixture) : IClassFixture<HelpdeskFixture>
{
    private static string Active(Guid id) => $"/api/platform/users/{id}/active";

    private sealed class AdjustableClock : TimeProvider
    {
        private TimeSpan _offset;

        public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + _offset;

        public void Advance(TimeSpan by) => _offset += by;
    }

    private sealed class FakeActor(Guid id) : ICurrentUser
    {
        public bool IsAuthenticated => true;

        public Guid UserId => id;

        public string DisplayName => "Test actor";

        public string? Email => null;

        public bool IsPlatformAdmin => true;
    }

    private static async Task<TestUser> MakePlatformAdminAsync(HelpdeskFixture fixture, string prefix)
    {
        var root = await fixture.PlatformAdminAsync();
        var user = await fixture.NewUserAsync(prefix);
        (await root.Client.PutAsync($"/api/platform/users/{user.Id}/platform-admin", new { value = true })).Expect(HttpStatusCode.OK);
        return user;
    }

    // ---- revocation ----

    [Fact]
    public async Task Deactivation_revokes_the_existing_session_at_once_and_blocks_new_sign_in()
    {
        var root = await fixture.PlatformAdminAsync();
        var subject = $"leaver-{Unique.Id()}";
        var leaver = await fixture.SignInAsync(subject);
        (await leaver.Client.GetAsync("/api/me")).Expect(HttpStatusCode.OK);

        var response = (await root.Client.PutAsync(Active(leaver.Id), new { value = false, reason = "left the organisation" })).Expect(HttpStatusCode.OK);
        Assert.False(response.Json.GetProperty("isActive").GetBoolean());

        (await leaver.Client.GetAsync("/api/me")).Expect(HttpStatusCode.Unauthorized);
        (await leaver.Client.GetAsync("/api/platform/users")).Expect(HttpStatusCode.Unauthorized);
        (await fixture.CreateAnonymousClient().GetAsync($"/api/auth/dev-login?subject={subject}")).Expect(HttpStatusCode.Forbidden);
        Assert.Equal(1, await fixture.CountAuditAsync("user.deactivated", leaver.Id));
    }

    [Fact]
    public async Task A_revoked_session_does_not_become_valid_again_after_reactivation()
    {
        var root = await fixture.PlatformAdminAsync();
        var subject = $"returner-{Unique.Id()}";
        var user = await fixture.SignInAsync(subject);

        (await root.Client.PutAsync(Active(user.Id), new { value = false })).Expect(HttpStatusCode.OK);
        (await user.Client.GetAsync("/api/me")).Expect(HttpStatusCode.Unauthorized);

        (await root.Client.PutAsync(Active(user.Id), new { value = true })).Expect(HttpStatusCode.OK);
        // The cookie issued before the deactivation stays dead; the person has to sign in again.
        (await user.Client.GetAsync("/api/me")).Expect(HttpStatusCode.Unauthorized);

        var fresh = await fixture.SignInAsync(subject);
        (await fresh.Client.GetAsync("/api/me")).Expect(HttpStatusCode.OK);
        Assert.Equal(user.Id, fresh.Id);
        Assert.Equal(1, await fixture.CountAuditAsync("user.reactivated", user.Id));
    }

    [Fact]
    public async Task Bumping_the_session_version_alone_revokes_a_session()
    {
        var user = await fixture.NewUserAsync("versioned");
        (await user.Client.GetAsync("/api/me")).Expect(HttpStatusCode.OK);
        await fixture.ExecuteAsync("UPDATE identity.users SET session_version = session_version + 1 WHERE id = @id", new NpgsqlParameter("id", user.Id));
        (await user.Client.GetAsync("/api/me")).Expect(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Provisioning_never_reactivates_a_deactivated_account_or_touches_it()
    {
        var root = await fixture.PlatformAdminAsync();
        var tenant = "tenant-s01";
        var subject = $"oid-{Unique.Id()}";
        ProvisionedUser first = null!;
        await fixture.WithScopeAsync(async sp => first = await sp.GetRequiredService<IUserProvisioner>()
            .ProvisionAsync(new ExternalIdentityClaims(ExternalIdentityProviders.Entra, tenant, subject, "Original Name", "orig@example.test")));
        (await root.Client.PutAsync(Active(first.UserId), new { value = false })).Expect(HttpStatusCode.OK);
        var lastSignIn = await fixture.ScalarAsync<DateTime?>("SELECT last_sign_in_at FROM identity.users WHERE id = @id", new NpgsqlParameter("id", first.UserId));

        ProvisionedUser again = null!;
        await fixture.WithScopeAsync(async sp => again = await sp.GetRequiredService<IUserProvisioner>()
            .ProvisionAsync(new ExternalIdentityClaims(ExternalIdentityProviders.Entra, tenant, subject, "Changed Name", "changed@example.test")));

        Assert.Equal(first.UserId, again.UserId);
        Assert.False(again.IsActive);
        Assert.Equal(0, await fixture.ScalarAsync<long>("SELECT count(*) FROM identity.users WHERE id = @id AND is_active", new NpgsqlParameter("id", first.UserId)));
        Assert.Equal("Original Name", await fixture.ScalarAsync<string>("SELECT display_name FROM identity.users WHERE id = @id", new NpgsqlParameter("id", first.UserId)));
        Assert.Equal(lastSignIn, await fixture.ScalarAsync<DateTime?>("SELECT last_sign_in_at FROM identity.users WHERE id = @id", new NpgsqlParameter("id", first.UserId)));
    }

    // ---- absolute lifetime ----

    private WebApplicationFactory<Program> WithClock(AdjustableClock clock, int absoluteHours) =>
        fixture.Factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Authentication:Session:AbsoluteLifetimeHours", absoluteHours.ToString(System.Globalization.CultureInfo.InvariantCulture));
            b.UseSetting("Authentication:Session:IdleTimeoutMinutes", "90");
            b.ConfigureServices(s => s.AddSingleton<TimeProvider>(clock));
        });

    [Fact]
    public async Task Sliding_renewal_cannot_extend_the_configured_absolute_lifetime()
    {
        var clock = new AdjustableClock();
        var factory = WithClock(clock, absoluteHours: 2);
        var user = await fixture.SignInAsync($"abs-{Unique.Id()}", factory: factory);

        (await user.Client.GetAsync("/api/me")).Expect(HttpStatusCode.OK);
        clock.Advance(TimeSpan.FromMinutes(70));
        (await user.Client.GetAsync("/api/me")).Expect(HttpStatusCode.OK);   // activity: the sliding window renews
        clock.Advance(TimeSpan.FromMinutes(40));                              // 110 minutes after sign-in, 40 after the last use
        (await user.Client.GetAsync("/api/me")).Expect(HttpStatusCode.OK);
        clock.Advance(TimeSpan.FromMinutes(11));                              // 121 minutes: past the absolute limit although the session was in use
        (await user.Client.GetAsync("/api/me")).Expect(HttpStatusCode.Unauthorized);

        var again = await fixture.SignInAsync($"abs2-{Unique.Id()}", factory: factory);
        (await again.Client.GetAsync("/api/me")).Expect(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_default_absolute_lifetime_is_twelve_hours()
    {
        var clock = new AdjustableClock();
        var factory = fixture.Factory.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton<TimeProvider>(clock)));
        var user = await fixture.SignInAsync($"def-{Unique.Id()}", factory: factory);
        clock.Advance(TimeSpan.FromHours(6));                                  // under the 8 hour idle timeout: renews
        (await user.Client.GetAsync("/api/me")).Expect(HttpStatusCode.OK);
        clock.Advance(TimeSpan.FromHours(5) + TimeSpan.FromMinutes(50));       // 11 h 50 after sign-in, in use
        (await user.Client.GetAsync("/api/me")).Expect(HttpStatusCode.OK);
        clock.Advance(TimeSpan.FromMinutes(15));                               // 12 h 05: past the absolute limit although in use
        (await user.Client.GetAsync("/api/me")).Expect(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData(0, 480)]
    [InlineData(200, 480)]
    [InlineData(12, 2)]
    [InlineData(1, 120)]
    public void Session_policy_values_are_validated(int absoluteHours, int idleMinutes)
    {
        var validator = new AuthenticationOptionsValidator(new FakeEnvironment());
        var result = validator.Validate(null, new HelpdeskAuthenticationOptions { Session = new SessionOptions { AbsoluteLifetimeHours = absoluteHours, IdleTimeoutMinutes = idleMinutes } });
        Assert.True(result.Failed);
    }

    [Fact]
    public void The_default_session_policy_is_valid()
    {
        var result = new AuthenticationOptionsValidator(new FakeEnvironment()).Validate(null, new HelpdeskAuthenticationOptions());
        Assert.True(result.Succeeded);
        Assert.Equal(12, new SessionOptions().AbsoluteLifetimeHours);
    }

    private sealed class FakeEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";

        public string ApplicationName { get; set; } = "t";

        public string ContentRootPath { get; set; } = "/";

        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    // ---- authorisation ----

    [Fact]
    public async Task Only_platform_administrators_can_deactivate_or_reactivate_and_unauthenticated_callers_get_401()
    {
        var root = await fixture.PlatformAdminAsync();
        var d = await fixture.CreateDepartmentAsync();
        var deptAdmin = await fixture.NewMemberAsync(d, DepartmentRoles.DepartmentAdmin);
        var agent = await fixture.NewMemberAsync(d, DepartmentRoles.Agent);
        var target = await fixture.NewUserAsync("target");

        foreach (var caller in new[] { deptAdmin, agent, await fixture.NewUserAsync("nobody") })
        {
            (await caller.Client.PutAsync(Active(target.Id), new { value = false })).Expect(HttpStatusCode.Forbidden);
            (await caller.Client.PutAsync(Active(target.Id), new { value = true })).Expect(HttpStatusCode.Forbidden);
        }

        var anonymous = fixture.CreateAnonymousClient();
        await anonymous.RefreshCsrfAsync();
        (await anonymous.PutAsync(Active(target.Id), new { value = false })).Expect(HttpStatusCode.Unauthorized);

        // Nothing changed.
        (await target.Client.GetAsync("/api/me")).Expect(HttpStatusCode.OK);
        Assert.Equal(0, await fixture.CountAuditAsync("user.deactivated", target.Id));
        (await root.Client.PutAsync(Active(Guid.NewGuid()), new { value = false })).Expect(HttpStatusCode.NotFound);
        (await root.Client.PutAsync(Active(target.Id), new { })).Expect(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task An_administrator_cannot_deactivate_themselves()
    {
        var admin = await MakePlatformAdminAsync(fixture, "selfadmin");
        var response = (await admin.Client.PutAsync(Active(admin.Id), new { value = false })).Expect(HttpStatusCode.Conflict);
        Assert.Equal(ProblemCodes.SelfDeactivation, response.Json.GetProperty("code").GetString());
        (await admin.Client.GetAsync("/api/me")).Expect(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_last_active_platform_administrator_cannot_be_deactivated()
    {
        // Through the API the actor is always an active administrator, so the guard is exercised at service level with an actor who is not the target.
        var onlyAdmin = await MakePlatformAdminAsync(fixture, "lastadmin");
        // Make onlyAdmin the single active administrator of this database.
        await fixture.ExecuteAsync("UPDATE identity.users SET is_platform_admin = false WHERE is_platform_admin AND id <> @id", new NpgsqlParameter("id", onlyAdmin.Id));

        await fixture.WithScopeAsync(async sp =>
        {
            var service = new PlatformUserService(
                sp.GetRequiredService<IdentityDbContext>(), sp.GetRequiredService<IAuditWriter>(), new FakeActor(Guid.NewGuid()), TimeProvider.System,
                [], [], NullLogger<PlatformUserService>.Instance);
            var ex = await Assert.ThrowsAsync<ConflictException>(() => service.SetActiveAsync(onlyAdmin.Id, false, null, CancellationToken.None));
            Assert.Equal(ProblemCodes.LastAdmin, ex.Code);
        });

        (await onlyAdmin.Client.GetAsync("/api/me")).Expect(HttpStatusCode.OK);
        Assert.Equal(0, await fixture.CountAuditAsync("user.deactivated", onlyAdmin.Id));
        // Restore the shared bootstrap administrator for later tests in this class.
        await fixture.ExecuteAsync("UPDATE identity.users SET is_platform_admin = true WHERE id IN (SELECT user_id FROM identity.external_identities WHERE subject = @s)",
            new NpgsqlParameter("s", HelpdeskFactory.PlatformAdminSubject));
    }

    [Fact]
    public async Task A_deactivated_platform_administrator_loses_the_privilege_and_the_session()
    {
        var root = await fixture.PlatformAdminAsync();
        var admin = await MakePlatformAdminAsync(fixture, "retiring");
        (await admin.Client.GetAsync("/api/platform/users")).Expect(HttpStatusCode.OK);

        (await root.Client.PutAsync(Active(admin.Id), new { value = false })).Expect(HttpStatusCode.OK);
        (await admin.Client.GetAsync("/api/platform/users")).Expect(HttpStatusCode.Unauthorized);
        (await admin.Client.GetAsync("/api/me")).Expect(HttpStatusCode.Unauthorized);
        (await root.Client.GetAsync("/api/me")).Expect(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Repeating_the_current_state_changes_nothing_and_writes_no_audit_entry()
    {
        var root = await fixture.PlatformAdminAsync();
        var user = await fixture.NewUserAsync("idem");
        (await root.Client.PutAsync(Active(user.Id), new { value = true })).Expect(HttpStatusCode.OK);
        (await user.Client.GetAsync("/api/me")).Expect(HttpStatusCode.OK);
        Assert.Equal(0, await fixture.CountAuditAsync("user.reactivated", user.Id));

        (await root.Client.PutAsync(Active(user.Id), new { value = false })).Expect(HttpStatusCode.OK);
        (await root.Client.PutAsync(Active(user.Id), new { value = false })).Expect(HttpStatusCode.OK);
        Assert.Equal(1, await fixture.CountAuditAsync("user.deactivated", user.Id));
    }

    [Fact]
    public async Task The_reason_is_length_limited()
    {
        var root = await fixture.PlatformAdminAsync();
        var user = await fixture.NewUserAsync("reason");
        (await root.Client.PutAsync(Active(user.Id), new { value = false, reason = new string('x', PlatformUserService.ReasonMaxLength + 1) })).Expect(HttpStatusCode.BadRequest);
        (await user.Client.GetAsync("/api/me")).Expect(HttpStatusCode.OK);
    }

    // ---- what happens to the person's work ----

    [Fact]
    public async Task Assigned_tickets_are_released_and_history_memberships_and_audit_are_preserved()
    {
        var root = await fixture.PlatformAdminAsync();
        var d = await fixture.CreateDepartmentAsync();
        var lead = await fixture.NewMemberAsync(d, DepartmentRoles.TeamLead);
        var agent = await fixture.NewMemberAsync(d, DepartmentRoles.Agent);
        var ticket = await lead.CreateTicketAsync(d);
        (await lead.Client.PutAsync($"/api/tickets/{ticket.Id}/assignment", new { version = ticket.Version, assigneeUserId = agent.Id })).Expect(HttpStatusCode.OK);
        var messages = await fixture.ScalarAsync<long>("SELECT count(*) FROM ticketing.messages WHERE ticket_id = @t", new NpgsqlParameter("t", ticket.Id));
        var audits = await fixture.ScalarAsync<long>("SELECT count(*) FROM audit.audit_events WHERE object_id = @t", new NpgsqlParameter("t", ticket.Id));

        (await root.Client.PutAsync(Active(agent.Id), new { value = false, reason = "left" })).Expect(HttpStatusCode.OK);

        // Released, in the same transaction, with a ticket audit entry naming the reason.
        Assert.Null(await fixture.ScalarAsync<Guid?>("SELECT assignee_user_id FROM ticketing.tickets WHERE id = @t", new NpgsqlParameter("t", ticket.Id)));
        Assert.Equal(1, await fixture.ScalarAsync<long>(
            "SELECT count(*) FROM audit.audit_events WHERE object_id = @t AND action = 'ticket.assignee_changed' AND next::text LIKE '%user_deactivated%'",
            new NpgsqlParameter("t", ticket.Id)));
        // Preserved: the ticket, its messages and every earlier audit entry; the membership; the requester.
        Assert.Equal(messages, await fixture.ScalarAsync<long>("SELECT count(*) FROM ticketing.messages WHERE ticket_id = @t", new NpgsqlParameter("t", ticket.Id)));
        Assert.True(await fixture.ScalarAsync<long>("SELECT count(*) FROM audit.audit_events WHERE object_id = @t", new NpgsqlParameter("t", ticket.Id)) >= audits);
        Assert.Equal(1, await fixture.ScalarAsync<long>("SELECT count(*) FROM organisation.department_memberships WHERE user_id = @u AND department_id = @d",
            new NpgsqlParameter("u", agent.Id), new NpgsqlParameter("d", d.Id)));
        (await lead.Client.GetAsync($"/api/tickets/{ticket.Id}")).Expect(HttpStatusCode.OK);

        // Reactivation does not re-assign anything and restores nothing silently.
        (await root.Client.PutAsync(Active(agent.Id), new { value = true })).Expect(HttpStatusCode.OK);
        Assert.Null(await fixture.ScalarAsync<Guid?>("SELECT assignee_user_id FROM ticketing.tickets WHERE id = @t", new NpgsqlParameter("t", ticket.Id)));
    }

    [Fact]
    public async Task Deactivation_in_one_department_does_not_change_another_departments_tickets_or_expose_content()
    {
        var root = await fixture.PlatformAdminAsync();
        var a = await fixture.CreateDepartmentAsync();
        var b = await fixture.CreateDepartmentAsync();
        var leadA = await fixture.NewMemberAsync(a, DepartmentRoles.TeamLead);
        var leadB = await fixture.NewMemberAsync(b, DepartmentRoles.TeamLead);
        var shared = await fixture.NewUserAsync("shared");
        await fixture.AddMemberAsync(a, shared, DepartmentRoles.Agent);
        await fixture.AddMemberAsync(b, shared, DepartmentRoles.Agent);
        var otherAgentB = await fixture.NewMemberAsync(b, DepartmentRoles.Agent);
        var tA = await leadA.CreateTicketAsync(a);
        var tB = await leadB.CreateTicketAsync(b);
        var tOther = await leadB.CreateTicketAsync(b);
        (await leadA.Client.PutAsync($"/api/tickets/{tA.Id}/assignment", new { version = tA.Version, assigneeUserId = shared.Id })).Expect(HttpStatusCode.OK);
        (await leadB.Client.PutAsync($"/api/tickets/{tB.Id}/assignment", new { version = tB.Version, assigneeUserId = shared.Id })).Expect(HttpStatusCode.OK);
        (await leadB.Client.PutAsync($"/api/tickets/{tOther.Id}/assignment", new { version = tOther.Version, assigneeUserId = otherAgentB.Id })).Expect(HttpStatusCode.OK);

        var response = (await root.Client.PutAsync(Active(shared.Id), new { value = false })).Expect(HttpStatusCode.OK);
        Assert.DoesNotContain("subject", response.Body, StringComparison.OrdinalIgnoreCase);

        Assert.Null(await fixture.ScalarAsync<Guid?>("SELECT assignee_user_id FROM ticketing.tickets WHERE id = @t", new NpgsqlParameter("t", tA.Id)));
        Assert.Null(await fixture.ScalarAsync<Guid?>("SELECT assignee_user_id FROM ticketing.tickets WHERE id = @t", new NpgsqlParameter("t", tB.Id)));
        Assert.Equal(otherAgentB.Id, await fixture.ScalarAsync<Guid?>("SELECT assignee_user_id FROM ticketing.tickets WHERE id = @t", new NpgsqlParameter("t", tOther.Id)));
        // The platform administrator got no ticket access from this (isolation unchanged).
        (await root.Client.GetAsync($"/api/tickets/{tA.Id}")).Expect(HttpStatusCode.NotFound);
        // Department staff still cannot see across departments.
        (await leadA.Client.GetAsync($"/api/tickets/{tB.Id}")).Expect(HttpStatusCode.NotFound);
    }

    // ---- regression ----

    [Fact]
    public async Task Normal_sign_in_and_privilege_changes_still_work()
    {
        var root = await fixture.PlatformAdminAsync();
        var user = await fixture.NewUserAsync("regular");
        (await user.Client.GetAsync("/api/me")).Expect(HttpStatusCode.OK);
        (await user.Client.PostAsync("/api/auth/logout")).Expect(HttpStatusCode.NoContent);
        (await user.Client.GetAsync("/api/me")).Expect(HttpStatusCode.Unauthorized);

        var again = await fixture.SignInAsync(user.Subject);
        (await again.Client.GetAsync("/api/me")).Expect(HttpStatusCode.OK);

        // A privilege change takes effect on the next request without re-login and without any session invalidation (the user is loaded on every request).
        (await root.Client.PutAsync($"/api/platform/users/{again.Id}/platform-admin", new { value = true })).Expect(HttpStatusCode.OK);
        (await again.Client.GetAsync("/api/platform/users")).Expect(HttpStatusCode.OK);
        (await root.Client.PutAsync($"/api/platform/users/{again.Id}/platform-admin", new { value = false })).Expect(HttpStatusCode.OK);
        (await again.Client.GetAsync("/api/platform/users")).Expect(HttpStatusCode.Forbidden);
        (await again.Client.GetAsync("/api/me")).Expect(HttpStatusCode.OK);
    }
}
