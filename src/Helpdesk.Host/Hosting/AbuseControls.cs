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
}

internal static class AbuseControls
{
    /// <summary>Largest JSON request body any endpoint accepts. Upload and webhook endpoints set their own explicit limit.</summary>
    public const long DefaultMaxRequestBodyBytes = 2 * 1024 * 1024;

    public static void AddAbuseControls(this IServiceCollection services, IConfiguration configuration)
    {
        var limits = configuration.GetSection(RateLimitOptions.Section).Get<RateLimitOptions>() ?? new RateLimitOptions();
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
            o.AddPolicy(RateLimitPolicies.PortalCreate, context => PerUser(context, RateLimitPolicies.PortalCreate, limits.PortalCreate));
            o.AddPolicy(RateLimitPolicies.PortalReply, context => PerUser(context, RateLimitPolicies.PortalReply, limits.PortalReply));
            o.AddPolicy(RateLimitPolicies.Search, context =>
                context.Request.Query["q"].ToString().Length > 0 ? PerUser(context, RateLimitPolicies.Search, limits.Search) : RateLimitPartition.GetNoLimiter("no-search"));
        });
    }

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
