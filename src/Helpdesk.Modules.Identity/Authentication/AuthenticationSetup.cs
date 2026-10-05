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
using Microsoft.IdentityModel.Tokens;

namespace Helpdesk.Modules.Identity.Authentication;

internal static class AuthenticationSetup
{
    public const string CsrfHeaderName = "X-CSRF-TOKEN";

    public static void Configure(IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var entra = configuration.GetSection(HelpdeskAuthenticationOptions.Section).GetSection("Entra").Get<EntraOptions>() ?? new EntraOptions();
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

        if (entra.IsConfigured)
        {
            // Registered only when configured; options validation (HelpdeskAuthenticationOptionsValidator) fails startup on partial config.
            auth.AddOpenIdConnect(AuthSchemes.Entra, o => ConfigureEntra(o, entra));
        }

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

    private static void ConfigureEntra(OpenIdConnectOptions o, EntraOptions entra)
    {
        var tenant = entra.TenantId ?? "";
        var authority = $"https://login.microsoftonline.com/{tenant}/v2.0";

        o.SignInScheme = AuthSchemes.Cookie;
        o.Authority = authority;
        o.ClientId = entra.ClientId;
        o.ClientSecret = entra.ClientSecret;
        o.ResponseType = "code";
        o.ResponseMode = "query";
        o.UsePkce = true;
        o.SaveTokens = false;
        o.GetClaimsFromUserInfoEndpoint = false;
        o.MapInboundClaims = false;
        o.CallbackPath = "/api/auth/signin-oidc";
        o.SignedOutCallbackPath = "/api/auth/signout-callback-oidc";
        o.Scope.Clear();
        o.Scope.Add("openid");
        o.Scope.Add("profile");
        o.Scope.Add("email");
        // State and nonce protection stay at their (enabled) defaults. Single tenant: issuer is pinned, tid is checked below.
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = authority,
            ValidateAudience = true,
            NameClaimType = "name",
        };
        o.Events = new OpenIdConnectEvents
        {
            OnTokenValidated = async ctx =>
            {
                if (ctx.Principal is null
                    || !EntraClaimsReader.TryRead(ctx.Principal, tenant, out var identity, out _))
                {
                    ctx.Fail("denied");
                    return;
                }

                var provisioner = ctx.HttpContext.RequestServices.GetRequiredService<IUserProvisioner>();
                var user = await provisioner.ProvisionAsync(identity!, ctx.HttpContext.RequestAborted);
                if (!user.IsActive)
                {
                    ctx.Fail("inactive");
                    return;
                }

                ctx.Principal = HelpdeskPrincipal.Create(user, identity!.Provider);
            },
            OnRemoteFailure = ctx =>
            {
                // Never surface exception details to the browser.
                var reason = ctx.Failure?.Message == "inactive" ? "inactive" : "failed";
                ctx.Response.Redirect($"/?authError={reason}");
                ctx.HandleResponse();
                return Task.CompletedTask;
            },
        };
    }
}
