using System.Globalization;
using System.Threading.RateLimiting;
using Helpdesk.SharedKernel.Errors;
using Helpdesk.SharedKernel.Security;
using Microsoft.AspNetCore.RateLimiting;

namespace Helpdesk.Host.Hosting;

/// <summary>One fixed-window limit: <see cref="Permits"/> requests per <see cref="WindowMinutes"/> per signed-in user.</summary>
internal sealed class RateLimit
{
    public int Permits { get; set; }

    public int WindowMinutes { get; set; }
}

/// <summary>
/// Bound from the <c>RateLimits</c> section. The defaults are PROVISIONAL starting values chosen to stop scripted abuse while leaving normal use untouched; tune them to
/// real traffic. They are per signed-in user, so one compromised account cannot exhaust the system, and they need no department membership to apply.
/// </summary>
internal sealed class RateLimitOptions
{
    public const string Section = "RateLimits";

    public RateLimit PortalCreate { get; set; } = new() { Permits = 10, WindowMinutes = 10 };

    public RateLimit PortalReply { get; set; } = new() { Permits = 30, WindowMinutes = 10 };

    public RateLimit Search { get; set; } = new() { Permits = 60, WindowMinutes = 1 };

    // Infrastructure limits (F-06). Per client address for the unauthenticated surface, per signed-in user otherwise. PROVISIONAL, tune to real traffic.

    /// <summary>Sign-in and OIDC endpoints under <c>/api/auth</c> (except the cheap provider probe), per client address.</summary>
    public RateLimit Authentication { get; set; } = new() { Permits = 60, WindowMinutes = 1 };

    /// <summary>Invitation preview and acceptance: the single-use token is the credential, so guessing is throttled per user.</summary>
    public RateLimit Invitations { get; set; } = new() { Permits = 20, WindowMinutes = 1 };

    /// <summary>State-changing administrative requests (<c>/api/admin</c> and Department administration, not ticket work), per signed-in user.</summary>
    public RateLimit Administration { get; set; } = new() { Permits = 120, WindowMinutes = 1 };

    /// <summary>Microsoft Graph webhooks (<c>/api/mail/graph</c>), per client address; generous because Graph batches notifications.</summary>
    public RateLimit Webhooks { get; set; } = new() { Permits = 600, WindowMinutes = 1 };
}

internal static class AbuseControls
{
    /// <summary>Largest JSON request body any endpoint accepts. Upload and webhook endpoints set their own explicit limit.</summary>
    public const long DefaultMaxRequestBodyBytes = 2 * 1024 * 1024;

    /// <summary>
    /// Outside Development and Testing the infrastructure limits are always on (fail-closed: there is no switch to turn them off). Development and Testing leave them off
    /// unless <c>RateLimits:Infrastructure:Enabled</c> is true, because test fixtures and local work sign in far more often than any real user.
    /// </summary>
    public static bool InfrastructureLimitsEnabled(IConfiguration configuration, IHostEnvironment environment) =>
        !(environment.IsDevelopment() || environment.IsEnvironment("Testing")) || configuration.GetValue<bool>("RateLimits:Infrastructure:Enabled");

    public static void AddAbuseControls(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var limits = configuration.GetSection(RateLimitOptions.Section).Get<RateLimitOptions>() ?? new RateLimitOptions();
        var infrastructure = InfrastructureLimitsEnabled(configuration, environment);
        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.OnRejected = async (context, ct) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                }

                context.HttpContext.Response.ContentType = "application/problem+json";
                await context.HttpContext.Response.WriteAsJsonAsync(
                    new { status = 429, title = "Too many requests", detail = "You are doing that too often. Wait a moment and try again.", code = ProblemCodes.RateLimited }, ct);
            };
            if (infrastructure)
            {
                o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context => InfrastructurePartition(context, limits));
            }

            o.AddPolicy(RateLimitPolicies.PortalCreate, context => PerUser(context, RateLimitPolicies.PortalCreate, limits.PortalCreate));
            o.AddPolicy(RateLimitPolicies.PortalReply, context => PerUser(context, RateLimitPolicies.PortalReply, limits.PortalReply));
            o.AddPolicy(RateLimitPolicies.Search, context =>
                context.Request.Query["q"].ToString().Length > 0 ? PerUser(context, RateLimitPolicies.Search, limits.Search) : RateLimitPartition.GetNoLimiter("no-search"));
        });
    }

    /// <summary>
    /// Chooses the limiter for a request: webhooks and sign-in per client address, invitations and administrative writes per signed-in user, everything else unlimited here
    /// (ordinary work is protected by the named per-endpoint policies and by authentication). Public so the rule can be tested without a host.
    /// </summary>
    public static RateLimitPartition<string> InfrastructurePartition(HttpContext context, RateLimitOptions limits)
    {
        var path = context.Request.Path;
        var address = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var user = context.User.Identity?.IsAuthenticated == true
            ? context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? context.User.Identity.Name ?? address
            : address;
        if (path.StartsWithSegments("/api/mail/graph", StringComparison.OrdinalIgnoreCase))
        {
            return Fixed("webhook:" + address, limits.Webhooks);
        }

        if (path.StartsWithSegments("/api/auth", StringComparison.OrdinalIgnoreCase) && !path.StartsWithSegments("/api/auth/providers", StringComparison.OrdinalIgnoreCase))
        {
            return Fixed("auth:" + address, limits.Authentication);
        }

        if (path.StartsWithSegments("/api/invitations", StringComparison.OrdinalIgnoreCase))
        {
            return Fixed("invitation:" + user, limits.Invitations);
        }

        if (IsAdministrativeWrite(context.Request))
        {
            return Fixed("admin:" + user, limits.Administration);
        }

        return RateLimitPartition.GetNoLimiter("infrastructure-none");
    }

    private static bool IsAdministrativeWrite(HttpRequest request)
    {
        if (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method) || HttpMethods.IsOptions(request.Method))
        {
            return false;
        }

        var path = request.Path;
        if (path.StartsWithSegments("/api/admin", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // /api/departments (create) and /api/departments/{id}/<area>/...: everything except day-to-day ticket work is administration.
        if (!path.StartsWithSegments("/api/departments", StringComparison.OrdinalIgnoreCase, out var rest))
        {
            return false;
        }

        var parts = (rest.Value ?? string.Empty).Split('/', StringSplitOptions.RemoveEmptyEntries);
        return !(parts.Length > 1 && string.Equals(parts[1], "tickets", StringComparison.OrdinalIgnoreCase));
    }

    private static RateLimitPartition<string> Fixed(string key, RateLimit limit) =>
        RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = limit.Permits,
            Window = TimeSpan.FromMinutes(limit.WindowMinutes),
            QueueLimit = 0,
            AutoReplenishment = true,
        });

    private static RateLimitPartition<string> PerUser(HttpContext context, string policy, RateLimit limit)
    {
        // The policy runs after authentication; an unauthenticated caller never reaches these endpoints, but is keyed by address rather than shared.
        var who = context.User.Identity?.IsAuthenticated == true
            ? context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? context.User.Identity.Name ?? "user"
            : context.Connection.RemoteIpAddress?.ToString() ?? "anonymous";
        return RateLimitPartition.GetFixedWindowLimiter($"{policy}:{who}", _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = limit.Permits,
            Window = TimeSpan.FromMinutes(limit.WindowMinutes),
            QueueLimit = 0,
            AutoReplenishment = true,
        });
    }
}
