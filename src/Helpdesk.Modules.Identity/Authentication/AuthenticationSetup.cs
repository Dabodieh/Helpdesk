using Helpdesk.Modules.Identity.Contracts;
using Helpdesk.Modules.Identity.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Helpdesk.Modules.Identity.Authentication;

internal static class AuthenticationSetup
{
    public const string CsrfHeaderName = "X-CSRF-TOKEN";

    public static void Configure(IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var relaxedCookie = environment.IsDevelopment() || environment.IsEnvironment("Testing");

        var auth = services.AddAuthentication(o =>
        {
            o.DefaultScheme = AuthSchemes.Cookie;
            o.DefaultChallengeScheme = AuthSchemes.Cookie;
        });

        auth.AddCookie(AuthSchemes.Cookie, o =>
        {
            o.Cookie.Name = "helpdesk.session";
            o.Cookie.HttpOnly = true;
            o.Cookie.SameSite = SameSiteMode.Lax;
            o.Cookie.SecurePolicy = relaxedCookie ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
            o.ExpireTimeSpan = TimeSpan.FromHours(8);
            o.SlidingExpiration = true;
            // This is an API: never redirect to a login page.
            o.Events.OnRedirectToLogin = ctx =>
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            };
            o.Events.OnRedirectToAccessDenied = ctx =>
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                return Task.CompletedTask;
            };
            o.Events.OnValidatePrincipal = async ctx =>
            {
                var current = ctx.HttpContext.RequestServices.GetRequiredService<CurrentUser>();
                if (!await current.LoadAsync(ctx.Principal, ctx.HttpContext.RequestAborted))
                {
                    ctx.RejectPrincipal();
                    await ctx.HttpContext.SignOutAsync(AuthSchemes.Cookie);
                }
            };
        });

        // Always registered: the sign-in settings can come from server settings or from the setup wizard (database) and may change while the app runs.
        // EntraOpenIdConnectConfigurer builds the options from them, and /api/auth/login refuses to challenge while nothing is configured.
        auth.AddOpenIdConnect(AuthSchemes.Entra, _ => { });
        services.AddSingleton<EntraSettingsStore>();
        services.AddSingleton<IConfigureOptions<OpenIdConnectOptions>, EntraOpenIdConnectConfigurer>();

        services.AddAntiforgery(o =>
        {
            o.HeaderName = CsrfHeaderName;
            o.Cookie.Name = "helpdesk.csrf";
            o.Cookie.HttpOnly = true;
            o.Cookie.SameSite = SameSiteMode.Strict;
            o.Cookie.SecurePolicy = relaxedCookie ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
            o.SuppressXFrameOptionsHeader = false;
        });
    }
}
