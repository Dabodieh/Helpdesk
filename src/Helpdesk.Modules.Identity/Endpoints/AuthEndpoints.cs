using Helpdesk.Modules.Identity.Authentication;
using Helpdesk.Modules.Identity.Contracts;
using Helpdesk.SharedKernel.Security;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Helpdesk.SharedKernel.Authorization;

namespace Helpdesk.Modules.Identity.Endpoints;

internal static class AuthEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var options = app.ServiceProvider.GetRequiredService<IOptions<HelpdeskAuthenticationOptions>>().Value;

        app.MapGet("/api/csrf", (HttpContext http, IAntiforgery antiforgery) =>
            {
                var tokens = antiforgery.GetAndStoreTokens(http);
                return TypedResults.Ok(new { headerName = AuthenticationSetup.CsrfHeaderName, token = tokens.RequestToken });
            })
            .AllowAnonymous()
            .WithTags("Auth");

        app.MapGet("/api/auth/login", async (HttpContext http, string? returnUrl, EntraSettingsStore entra, ILoggerFactory loggers) =>
            {
                if (!entra.Current.IsConfigured)
                {
                    return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Entra sign-in is not configured");
                }

                try
                {
                    await http.ChallengeAsync(AuthSchemes.Entra, new AuthenticationProperties { RedirectUri = ReturnUrl.Sanitize(returnUrl) });
                    return Results.Empty;
                }
                catch (Exception ex) when (!http.RequestAborted.IsCancellationRequested)
                {
                    // Microsoft could not be reached or answered something unusable (metadata discovery fails here). Fail closed: no session is created and nothing
                    // falls back. The browser gets a clear, safe message with a reference; the detail stays in the log under the same reference.
                    var reference = http.TraceIdentifier;
                    SignInLog.StartFailed(loggers.CreateLogger(SignInLog.Category), ex, reference);
                    return Results.Redirect($"/?authError=unavailable&ref={Uri.EscapeDataString(reference)}");
                }
            })
            .AllowAnonymous()
            .WithTags("Auth");

        // Whether staff can sign in with Microsoft yet (the sign-in page says setup is needed when not). Reveals nothing else.
        app.MapGet("/api/auth/providers", (EntraSettingsStore entra) => TypedResults.Ok(new { entra = entra.Current.IsConfigured }))
            .AllowAnonymous()
            .WithTags("Auth");

        app.MapPost("/api/auth/logout", async (HttpContext http) =>
            {
                await http.SignOutAsync(AuthSchemes.Cookie);
                return TypedResults.NoContent();
            })
            .RequireAuthenticatedOnly()
            .WithTags("Auth");

        // The route does not exist unless explicitly enabled (options validation already restricts this to Development/Testing).
        if (options.DevSignIn.Enabled)
        {
            app.MapGet("/api/auth/dev-login", async (HttpContext http, string? subject, string? name, string? email, string? returnUrl, IUserProvisioner provisioner) =>
                {
                    if (string.IsNullOrWhiteSpace(subject) || subject.Length > 200)
                    {
                        return Results.ValidationProblem(new Dictionary<string, string[]> { ["subject"] = ["subject is required (max 200 characters)."] });
                    }

                    var trimmed = subject.Trim();
                    var user = await provisioner.ProvisionAsync(
                        new ExternalIdentityClaims(ExternalIdentityProviders.Dev, "dev", trimmed, string.IsNullOrWhiteSpace(name) ? trimmed : name.Trim(), email),
                        http.RequestAborted);
                    if (!user.IsActive)
                    {
                        return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Forbidden");
                    }

                    await http.SignInAsync(AuthSchemes.Cookie, HelpdeskPrincipal.Create(user, ExternalIdentityProviders.Dev, http.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow()));
                    return Results.LocalRedirect(ReturnUrl.Sanitize(returnUrl));
                })
                .AllowAnonymous()
                .WithTags("Auth");
        }
    }
}
