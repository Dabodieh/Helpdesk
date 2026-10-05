using System.Net;
using Helpdesk.Host.Tests.Infrastructure;
using Npgsql;

namespace Helpdesk.Host.Tests.Identity;

public sealed class AuthEndpointTests(HelpdeskFixture fixture) : IClassFixture<HelpdeskFixture>
{
    [Fact]
    public async Task Dev_login_issues_an_httponly_lax_session_cookie_and_redirects_locally()
    {
        var client = fixture.CreateAnonymousClient();
        var response = (await client.GetAsync($"/api/auth/dev-login?subject=cookie-{Unique.Id()}&returnUrl=/tickets")).Expect(HttpStatusCode.Redirect);

        Assert.Equal("/tickets", response.Message.Headers.Location?.OriginalString);
        var cookie = Assert.Single(response.Message.Headers.GetValues("Set-Cookie"), c => c.StartsWith("helpdesk.session=", StringComparison.Ordinal));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("https://evil.test/")]
    [InlineData("//evil.test")]
    [InlineData("/\\evil.test")]
    public async Task Dev_login_ignores_non_local_return_urls(string returnUrl)
    {
        var client = fixture.CreateAnonymousClient();
        var response = (await client.GetAsync($"/api/auth/dev-login?subject=redir-{Unique.Id()}&returnUrl={Uri.EscapeDataString(returnUrl)}")).Expect(HttpStatusCode.Redirect);
        Assert.Equal("/", response.Message.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Dev_login_requires_a_subject()
    {
        (await fixture.CreateAnonymousClient().GetAsync("/api/auth/dev-login")).Expect(HttpStatusCode.BadRequest);
        (await fixture.CreateAnonymousClient().GetAsync("/api/auth/dev-login?subject=%20")).Expect(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Signing_in_again_refreshes_name_and_email()
    {
        var subject = $"refresh-{Unique.Id()}";
        var first = await fixture.SignInAsync(subject, "First Name", "first@example.test");
        var second = await fixture.SignInAsync(subject, "Second Name", "second@example.test");

        Assert.Equal(first.Id, second.Id);
        var me = (await second.Client.GetAsync("/api/me")).Json;
        Assert.Equal("Second Name", me.GetProperty("displayName").GetString());
        Assert.Equal("second@example.test", me.GetProperty("email").GetString());
    }

    [Fact]
    public async Task New_users_have_no_access_and_no_platform_rights()
    {
        var user = await fixture.NewUserAsync();
        var me = (await user.Client.GetAsync("/api/me")).Expect(HttpStatusCode.OK).Json;
        Assert.False(me.GetProperty("isPlatformAdmin").GetBoolean());
        Assert.Empty(me.GetProperty("memberships").EnumerateArray());
        Assert.Empty((await user.Client.GetAsync("/api/departments")).Json.EnumerateArray());
    }

    [Fact]
    public async Task Logout_ends_the_session_and_needs_authentication_and_csrf()
    {
        var user = await fixture.NewUserAsync();
        var noCsrf = await user.Client.SendAsync(HttpMethod.Post, "/api/auth/logout", null, includeCsrf: false);
        noCsrf.Expect(HttpStatusCode.BadRequest);

        (await user.Client.PostAsync("/api/auth/logout")).Expect(HttpStatusCode.NoContent);
        (await user.Client.GetAsync("/api/me")).Expect(HttpStatusCode.Unauthorized);

        var anonymous = fixture.CreateAnonymousClient();
        await anonymous.RefreshCsrfAsync();
        (await anonymous.PostAsync("/api/auth/logout")).Expect(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Deactivated_users_lose_their_session_immediately_and_cannot_sign_in()
    {
        var subject = $"deactivate-{Unique.Id()}";
        var user = await fixture.SignInAsync(subject);
        (await user.Client.GetAsync("/api/me")).Expect(HttpStatusCode.OK);

        await fixture.ExecuteAsync("UPDATE identity.users SET is_active = false WHERE id = @id", new NpgsqlParameter("id", user.Id));

        (await user.Client.GetAsync("/api/me")).Expect(HttpStatusCode.Unauthorized);
        (await fixture.CreateAnonymousClient().GetAsync($"/api/auth/dev-login?subject={subject}")).Expect(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Revoking_platform_admin_takes_effect_on_the_next_request_without_re_login()
    {
        var root = await fixture.PlatformAdminAsync();
        var user = await fixture.NewUserAsync("tmpadmin");
        (await root.Client.PutAsync($"/api/platform/users/{user.Id}/platform-admin", new { value = true })).Expect(HttpStatusCode.OK);
        (await user.Client.GetAsync("/api/platform/users")).Expect(HttpStatusCode.OK);

        (await root.Client.PutAsync($"/api/platform/users/{user.Id}/platform-admin", new { value = false })).Expect(HttpStatusCode.OK);
        (await user.Client.GetAsync("/api/platform/users")).Expect(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Unsafe_requests_need_a_csrf_token_and_it_is_bound_to_the_signed_in_user()
    {
        var user = await fixture.NewUserAsync();
        (await user.Client.SendAsync(HttpMethod.Post, "/api/auth/logout", null, includeCsrf: false)).Expect(HttpStatusCode.BadRequest);

        // A token fetched while anonymous is not valid for a later session: fetch it after signing in.
        var client = fixture.CreateAnonymousClient();
        await client.RefreshCsrfAsync();
        (await client.GetAsync($"/api/auth/dev-login?subject=csrf-{Unique.Id()}")).Expect(HttpStatusCode.Redirect);
        (await client.PostAsync("/api/auth/logout")).Expect(HttpStatusCode.BadRequest);
        await client.RefreshCsrfAsync();
        (await client.PostAsync("/api/auth/logout")).Expect(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Csrf_endpoint_is_anonymous_and_returns_header_name_and_token()
    {
        var response = (await fixture.CreateAnonymousClient().GetAsync("/api/csrf")).Expect(HttpStatusCode.OK);
        Assert.Equal("X-CSRF-TOKEN", response.Json.GetProperty("headerName").GetString());
        Assert.False(string.IsNullOrEmpty(response.Json.GetProperty("token").GetString()));
    }

    [Fact]
    public async Task Safe_methods_never_need_a_csrf_token()
    {
        var user = await fixture.NewUserAsync();
        (await user.Client.SendAsync(HttpMethod.Get, "/api/me", null, includeCsrf: false)).Expect(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Login_without_entra_configuration_is_404_and_does_not_throw()
    {
        (await fixture.CreateAnonymousClient().GetAsync("/api/auth/login")).Expect(HttpStatusCode.NotFound);
    }
}
