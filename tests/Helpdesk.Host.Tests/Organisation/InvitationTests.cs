using System.Net;
using System.Collections.Concurrent;
using Microsoft.AspNetCore.TestHost;
using Serilog.Core;
using Serilog.Events;
using System.Text.RegularExpressions;
using Helpdesk.Host.Tests.Infrastructure;
using Helpdesk.Host.Tests.Mail;
using Helpdesk.Host.Tests.Ticketing;
using Helpdesk.Modules.Mail.Providers;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Helpdesk.Host.Tests.Organisation;

/// <summary>
/// Account invitations: platform administrators only, an emailed single-use link, activation by signing in as the invited address, and nothing granted before that.
/// </summary>
public sealed partial class InvitationTests(HelpdeskFixture fixture) : IClassFixture<HelpdeskFixture>
{
    private const string Admin = "/api/admin/invitations";
    private const string SystemMailbox = "system@helpdesk.test";

    [GeneratedRegex(@"/activate#([A-Za-z0-9_-]+)")]
    private static partial Regex TokenPattern();

    private static string TokenOf(ApiResponse issued) => TokenPattern().Match(issued.Json.GetProperty("activationUrl").GetString()!).Groups[1].Value;

    private (WebApplicationFactory<Program> Factory, FakeMailProvider Fake) WithSystemMailbox() =>
        Mailed(new HelpdeskFactory(fixture.ConnectionString).WithWebHostBuilder(b => b.UseSetting("Mail:SystemMailbox", SystemMailbox)));

    private static (WebApplicationFactory<Program>, FakeMailProvider) Mailed(WebApplicationFactory<Program> f) => (f, f.Services.GetRequiredService<FakeMailProvider>());

    private async Task<TestUser> InviteeAsync(string email) => await fixture.SignInAsync($"invitee-{Unique.Id()}", "Invited Person", email);

    private static string NewEmail() => $"new.{Unique.Id()}@example.test";

    private static async Task<ApiResponse> InviteAsync(TestUser platform, Dept d, string email, string role = "Agent") =>
        await platform.Client.PostAsync(Admin, new { displayName = "Invited Person", email, departmentId = d.Department.Id, role });

    [Fact]
    public async Task Only_a_platform_administrator_can_invite_list_resend_or_revoke()
    {
        var d = await fixture.NewDeptAsync();
        var platform = await fixture.PlatformAdminAsync();
        var created = (await InviteAsync(platform, d, NewEmail())).Expect(HttpStatusCode.Created);
        var id = created.Json.GetProperty("invitation").GetProperty("id").GetGuid();

        (await fixture.CreateAnonymousClient().GetAsync(Admin)).Expect(HttpStatusCode.Unauthorized);
        foreach (var user in new[] { d.Admin, d.Lead, d.Agent, d.Viewer, await fixture.NewUserAsync() })
        {
            (await user.Client.GetAsync(Admin)).Expect(HttpStatusCode.Forbidden);
            (await InviteAsync(user, d, NewEmail())).Expect(HttpStatusCode.Forbidden);
            (await user.Client.PostAsync($"{Admin}/{id}/resend", new { })).Expect(HttpStatusCode.Forbidden);
            (await user.Client.PostAsync($"{Admin}/{id}/revoke", new { })).Expect(HttpStatusCode.Forbidden);
        }
    }

    [Fact]
    public async Task Inviting_sends_one_email_from_the_system_mailbox_and_stores_only_a_hash_of_the_link()
    {
        var (factory, fake) = WithSystemMailbox();
        await using var _ = factory;
        var d = await fixture.NewDeptAsync();
        var platform = await fixture.SignInAsync(HelpdeskFactory.PlatformAdminSubject, "Platform Admin", factory: factory);
        var email = NewEmail();

        var created = (await InviteAsync(platform, d, email, "TeamLead")).Expect(HttpStatusCode.Created);
        var token = TokenOf(created);
        Assert.Equal("Accepted", created.Json.GetProperty("invitation").GetProperty("emailState").GetString());

        var sent = Assert.Single(fake.SentMail(SystemMailbox), m => m.To.Any(t => t.Address == email));
        Assert.Contains($"/activate#{token}", sent.BodyHtml, StringComparison.Ordinal);
        Assert.Contains(d.Department.Name, sent.BodyHtml, StringComparison.Ordinal);
        Assert.Empty(fake.SentMail(d.Department.Key)); // never from a department mailbox

        Assert.Equal(0, await fixture.CountAsync("SELECT count(*) FROM organisation.invitations WHERE encode(token_hash, 'escape') = @t", TicketKit.P("t", token)));
        Assert.Equal(1, await fixture.CountAsync("SELECT count(*) FROM organisation.invitations WHERE email_normalised = @e AND length(token_hash) = 32", TicketKit.P("e", email.ToLowerInvariant())));
        Assert.Equal(0, await fixture.CountAsync("SELECT count(*) FROM audit.audit_events WHERE next::text LIKE @t OR previous::text LIKE @t", TicketKit.P("t", $"%{token}%")));
        Assert.True(await fixture.CountAsync("SELECT count(*) FROM audit.audit_events WHERE action = 'invitation.created' AND object_id = @i", TicketKit.P("i", created.Json.GetProperty("invitation").GetProperty("id").GetGuid())) == 1);
    }

    [Fact]
    public async Task Without_a_system_mailbox_the_invitation_is_made_but_says_no_email_was_sent()
    {
        var d = await fixture.NewDeptAsync();
        var platform = await fixture.PlatformAdminAsync();
        var created = (await InviteAsync(platform, d, NewEmail())).Expect(HttpStatusCode.Created);
        Assert.Equal("NotConfigured", created.Json.GetProperty("invitation").GetProperty("emailState").GetString());
        Assert.False(string.IsNullOrEmpty(TokenOf(created)));
    }

    [Fact]
    public async Task Signing_in_as_the_invited_address_activates_it_once_and_adds_the_member()
    {
        var d = await fixture.NewDeptAsync();
        var platform = await fixture.PlatformAdminAsync();
        var email = NewEmail();
        var token = TokenOf((await InviteAsync(platform, d, email, "Agent")).Expect(HttpStatusCode.Created));
        var person = await InviteeAsync(email.ToUpperInvariant()); // the address is compared case-insensitively

        var preview = (await person.Client.PostAsync("/api/invitations/preview", new { token })).Expect(HttpStatusCode.OK).Json;
        Assert.Equal(d.Department.Name, preview.GetProperty("departmentName").GetString());
        Assert.Equal(0, await fixture.CountAsync("SELECT count(*) FROM organisation.department_memberships WHERE user_id = @u", TicketKit.P("u", person.Id))); // a preview grants nothing

        var accepted = (await person.Client.PostAsync("/api/invitations/accept", new { token })).Expect(HttpStatusCode.OK).Json;
        Assert.Equal("Agent", accepted.GetProperty("role").GetString());
        Assert.False(accepted.GetProperty("alreadyMember").GetBoolean());
        var members = (await platform.Client.GetAsync($"{d.Department.Url}/members")).Expect(HttpStatusCode.OK).Json;
        Assert.Contains(members.EnumerateArray(), m => m.GetProperty("userId").GetGuid() == person.Id && m.GetProperty("role").GetString() == "Agent");

        // Single use.
        (await person.Client.PostAsync("/api/invitations/accept", new { token })).Expect(HttpStatusCode.NotFound);
        (await person.Client.PostAsync("/api/invitations/preview", new { token })).Expect(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_link_used_by_a_different_account_grants_nothing_and_stays_usable_by_the_right_one()
    {
        var d = await fixture.NewDeptAsync();
        var platform = await fixture.PlatformAdminAsync();
        var email = NewEmail();
        var token = TokenOf((await InviteAsync(platform, d, email)).Expect(HttpStatusCode.Created));

        var stranger = await InviteeAsync(NewEmail());
        var noAddress = await fixture.SignInAsync($"noaddr-{Unique.Id()}", "No Address");
        foreach (var other in new[] { stranger, noAddress, d.Admin })
        {
            (await other.Client.PostAsync("/api/invitations/accept", new { token })).Expect(HttpStatusCode.Conflict);
        }

        Assert.Equal(0, await fixture.CountAsync("SELECT count(*) FROM organisation.department_memberships WHERE user_id = @u", TicketKit.P("u", stranger.Id)));
        var right = await InviteeAsync(email);
        (await right.Client.PostAsync("/api/invitations/accept", new { token })).Expect(HttpStatusCode.OK);
        (await fixture.CreateAnonymousClient().PostAsync("/api/invitations/accept", new { token })).Expect(HttpStatusCode.BadRequest); // CSRF is checked before sign-in, as everywhere
    }

    [Fact]
    public async Task Unknown_revoked_expired_and_replaced_links_are_all_just_not_found()
    {
        var d = await fixture.NewDeptAsync();
        var platform = await fixture.PlatformAdminAsync();
        var email = NewEmail();
        var person = await InviteeAsync(email);

        (await person.Client.PostAsync("/api/invitations/accept", new { token = "nonsense" })).Expect(HttpStatusCode.NotFound);
        (await person.Client.PostAsync("/api/invitations/accept", new { token = (string?)null })).Expect(HttpStatusCode.NotFound);

        var first = (await InviteAsync(platform, d, email)).Expect(HttpStatusCode.Created);
        var id = first.Json.GetProperty("invitation").GetProperty("id").GetGuid();
        var oldToken = TokenOf(first);

        // Resending issues a new link; the old one stops working.
        var resent = (await platform.Client.PostAsync($"{Admin}/{id}/resend", new { })).Expect(HttpStatusCode.OK);
        var newToken = TokenOf(resent);
        Assert.NotEqual(oldToken, newToken);
        (await person.Client.PostAsync("/api/invitations/preview", new { token = oldToken })).Expect(HttpStatusCode.NotFound);
        (await person.Client.PostAsync("/api/invitations/preview", new { token = newToken })).Expect(HttpStatusCode.OK);

        // Expired.
        await fixture.ExecuteAsync("UPDATE organisation.invitations SET expires_at = now() - interval '1 minute' WHERE id = @i", TicketKit.P("i", id));
        (await person.Client.PostAsync("/api/invitations/accept", new { token = newToken })).Expect(HttpStatusCode.NotFound);
        var listed = (await platform.Client.GetAsync(Admin)).Expect(HttpStatusCode.OK).Json;
        Assert.Equal("Expired", listed.EnumerateArray().First(i => i.GetProperty("id").GetGuid() == id).GetProperty("status").GetString());

        // An expired invitation can be re-issued and then used.
        var reissued = TokenOf((await platform.Client.PostAsync($"{Admin}/{id}/resend", new { })).Expect(HttpStatusCode.OK));
        // Revoked.
        (await platform.Client.PostAsync($"{Admin}/{id}/revoke", new { })).Expect(HttpStatusCode.NoContent);
        (await person.Client.PostAsync("/api/invitations/accept", new { token = reissued })).Expect(HttpStatusCode.NotFound);
        (await platform.Client.PostAsync($"{Admin}/{id}/resend", new { })).Expect(HttpStatusCode.Conflict);
        Assert.Equal(0, await fixture.CountAsync("SELECT count(*) FROM organisation.department_memberships WHERE user_id = @u", TicketKit.P("u", person.Id)));
    }

    [Fact]
    public async Task An_existing_member_keeps_the_role_they_have_and_a_second_open_invitation_is_refused()
    {
        var d = await fixture.NewDeptAsync();
        var platform = await fixture.PlatformAdminAsync();
        var email = NewEmail();
        var person = await InviteeAsync(email);
        await fixture.AddMemberAsync(d.Department, person, "Viewer");

        var token = TokenOf((await InviteAsync(platform, d, email, "DepartmentAdmin")).Expect(HttpStatusCode.Created));
        (await InviteAsync(platform, d, email, "Agent")).Expect(HttpStatusCode.Conflict); // already open

        var accepted = (await person.Client.PostAsync("/api/invitations/accept", new { token })).Expect(HttpStatusCode.OK).Json;
        Assert.True(accepted.GetProperty("alreadyMember").GetBoolean());
        Assert.Equal("Viewer", accepted.GetProperty("role").GetString()); // not promoted by an invitation
    }

    [Fact]
    public async Task Invalid_requests_are_refused_and_create_nothing()
    {
        var d = await fixture.NewDeptAsync();
        var platform = await fixture.PlatformAdminAsync();
        var before = await fixture.CountAsync("SELECT count(*) FROM organisation.invitations");

        (await platform.Client.PostAsync(Admin, new { displayName = "", email = "x", departmentId = d.Department.Id, role = "Agent" })).Expect(HttpStatusCode.BadRequest);
        (await platform.Client.PostAsync(Admin, new { displayName = "A", email = NewEmail(), departmentId = d.Department.Id, role = "Owner" })).Expect(HttpStatusCode.BadRequest);
        (await platform.Client.PostAsync(Admin, new { displayName = "A", email = NewEmail(), departmentId = Guid.NewGuid(), role = "Agent" })).Expect(HttpStatusCode.BadRequest);
        (await platform.Client.PostAsync(Admin, new { displayName = "A", email = NewEmail(), role = "Agent" })).Expect(HttpStatusCode.BadRequest);

        Assert.Equal(before, await fixture.CountAsync("SELECT count(*) FROM organisation.invitations"));
    }
    [Fact]
    public async Task Membership_failure_rolls_back_acceptance_and_audit_and_the_same_link_can_retry()
    {
        var d = await fixture.NewDeptAsync();
        var platform = await fixture.PlatformAdminAsync();
        var email = NewEmail();
        var person = await InviteeAsync(email);
        var issued = (await InviteAsync(platform, d, email)).Expect(HttpStatusCode.Created);
        var id = issued.Json.GetProperty("invitation").GetProperty("id").GetGuid();
        var token = TokenOf(issued);
        // A real database failure at membership insertion, after the invitation was claimed.
        await fixture.ExecuteAsync($"""
            CREATE FUNCTION organisation.fail_invited_membership() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
              IF NEW.user_id = '{person.Id}'::uuid THEN RAISE EXCEPTION 'injected membership failure'; END IF;
              RETURN NEW;
            END $$;
            CREATE TRIGGER fail_invited_membership BEFORE INSERT ON organisation.department_memberships
            FOR EACH ROW EXECUTE FUNCTION organisation.fail_invited_membership();
            """);
        try
        {
            (await person.Client.PostAsync("/api/invitations/accept", new { token })).Expect(HttpStatusCode.InternalServerError);
            Assert.Equal(1, await fixture.CountAsync("SELECT count(*) FROM organisation.invitations WHERE id = @i AND status = 'Pending' AND accepted_at IS NULL AND accepted_user_id IS NULL", TicketKit.P("i", id)));
            Assert.Equal(0, await fixture.CountAsync("SELECT count(*) FROM organisation.department_memberships WHERE user_id = @u", TicketKit.P("u", person.Id)));
            Assert.Equal(0, await fixture.CountAsync("SELECT count(*) FROM audit.audit_events WHERE action = 'invitation.accepted' AND object_id = @i", TicketKit.P("i", id)));
        }
        finally
        {
            await fixture.ExecuteAsync("DROP TRIGGER fail_invited_membership ON organisation.department_memberships; DROP FUNCTION organisation.fail_invited_membership()");
        }

        (await person.Client.PostAsync("/api/invitations/accept", new { token })).Expect(HttpStatusCode.OK);
        Assert.Equal(1, await fixture.CountAsync("SELECT count(*) FROM audit.audit_events WHERE action = 'invitation.accepted' AND object_id = @i", TicketKit.P("i", id)));
    }

    [Fact]
    public async Task Concurrent_activation_grants_membership_and_records_acceptance_once()
    {
        var d = await fixture.NewDeptAsync();
        var platform = await fixture.PlatformAdminAsync();
        var email = NewEmail();
        var person = await InviteeAsync(email);
        var issued = (await InviteAsync(platform, d, email)).Expect(HttpStatusCode.Created);
        var id = issued.Json.GetProperty("invitation").GetProperty("id").GetGuid();
        var token = TokenOf(issued);
        var replies = await Task.WhenAll(
            person.Client.PostAsync("/api/invitations/accept", new { token }),
            person.Client.PostAsync("/api/invitations/accept", new { token }));
        Assert.Single(replies, r => r.Status == HttpStatusCode.OK);
        Assert.Single(replies, r => r.Status == HttpStatusCode.NotFound);
        Assert.Equal(1, await fixture.CountAsync("SELECT count(*) FROM organisation.department_memberships WHERE user_id = @u AND department_id = @d", TicketKit.P("u", person.Id), TicketKit.P("d", d.Department.Id)));
        Assert.Equal(1, await fixture.CountAsync("SELECT count(*) FROM audit.audit_events WHERE action = 'invitation.accepted' AND object_id = @i", TicketKit.P("i", id)));
    }

    private sealed class CaptureSink : ILogEventSink
    {
        public ConcurrentQueue<string> Lines { get; } = new();
        public void Emit(LogEvent e) => Lines.Enqueue(e.RenderMessage(System.Globalization.CultureInfo.InvariantCulture) + " " + string.Join(" ", e.Properties));
    }

    [Fact]
    public async Task Legacy_activation_path_is_redacted_in_application_request_logs()
    {
        var sink = new CaptureSink();
        await using var logged = fixture.Factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<ILogEventSink>(sink)));
        using var client = logged.CreateClient();
        var token = "private-invitation-" + Unique.Id();
        await client.GetAsync("/activate/" + token);
        Assert.Contains(sink.Lines, l => l.Contains("/activate", StringComparison.Ordinal));
        Assert.DoesNotContain(sink.Lines, l => l.Contains(token, StringComparison.Ordinal));
    }

}
