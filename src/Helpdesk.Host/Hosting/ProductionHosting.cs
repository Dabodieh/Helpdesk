using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;

namespace Helpdesk.Host.Hosting;

/// <summary>
/// Production hosting (ADR-020, decision D1): the host serves the built SPA, the API and SignalR as one deployable behind an Nginx reverse proxy.
/// The application owns the response security headers of the application page; the proxy terminates TLS and is the only trusted forwarder.
/// </summary>
internal static class ProductionHosting
{
    /// <summary>The page policy. Scripts and connections are same-origin only; inline styles are allowed because the UI sets style attributes.</summary>
    internal const string PageContentSecurityPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; font-src 'self'; connect-src 'self'; " +
        "object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'";

    /// <summary>Paths that belong to the server, never to the SPA fallback.</summary>
    private static readonly string[] ServerPaths = ["/api", "/hubs", "/health", "/hangfire", "/openapi"];

    /// <summary>
    /// Forwarded headers are trusted ONLY from <c>Proxy:KnownProxies</c> (addresses) and <c>Proxy:KnownNetworks</c> (CIDR, e.g. the compose network of the proxy).
    /// The framework's default loopback trust is removed: no configuration means no forwarded header is honoured.
    /// </summary>
    public static void ConfigureForwardedHeaders(ForwardedHeadersOptions options, IConfiguration configuration)
    {
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();
        foreach (var ip in Configured(configuration, "Proxy:KnownProxies"))
        {
            options.KnownProxies.Add(IPAddress.Parse(ip));
        }

        foreach (var cidr in Configured(configuration, "Proxy:KnownNetworks"))
        {
            options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(cidr));
        }

        // The framework trusts EVERY caller when both lists are empty (verified, SRE-006), so with nothing configured the middleware must do nothing at all.
        options.ForwardedHeaders = options.KnownProxies.Count + options.KnownIPNetworks.Count == 0
            ? ForwardedHeaders.None
            : ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    }

    private static string[] Configured(IConfiguration configuration, string section) =>
        [.. (configuration.GetSection(section).Get<string[]>() ?? []).Where(v => !string.IsNullOrWhiteSpace(v))];

    /// <summary>Registers the start-up check below; it runs with the other start-up validations (identity, mail and storage report their own problems first).</summary>
    public static void AddProductionSettingsGuard(this IServiceCollection services)
    {
        services.AddOptions<ProductionSettingsMarker>().ValidateOnStart();
        services.AddSingleton<IValidateOptions<ProductionSettingsMarker>, ProductionSettingsValidator>();
    }

    internal sealed class ProductionSettingsMarker;

    private sealed class ProductionSettingsValidator(IConfiguration configuration, IHostEnvironment environment) : IValidateOptions<ProductionSettingsMarker>
    {
        public ValidateOptionsResult Validate(string? name, ProductionSettingsMarker options)
        {
            try
            {
                RequireSafeProductionSettings(configuration, environment);
                return ValidateOptionsResult.Success;
            }
            catch (InvalidOperationException ex)
            {
                return ValidateOptionsResult.Fail(ex.Message);
            }
        }
    }

    /// <summary>Refuses to start outside Development/Testing with settings that would silently weaken production (SRE-006, SRE-010, SRE-014).</summary>
    public static void RequireSafeProductionSettings(IConfiguration configuration, IHostEnvironment environment)
    {
        if (environment.IsDevelopment() || environment.IsEnvironment("Testing"))
        {
            return;
        }

        var problems = new List<string>();
        if (Configured(configuration, "Proxy:KnownProxies").Length + Configured(configuration, "Proxy:KnownNetworks").Length == 0)
        {
            problems.Add("Proxy:KnownProxies or Proxy:KnownNetworks must name the reverse proxy (forwarded headers are trusted from nothing else).");
        }

        var allowedHosts = configuration["AllowedHosts"];
        if (string.IsNullOrWhiteSpace(allowedHosts) || allowedHosts.Split(';').Any(h => h.Trim() == "*"))
        {
            problems.Add("AllowedHosts must list the public host name(s), not '*'.");
        }

        if (string.Equals(configuration["Storage:Provider"], "InMemory", StringComparison.OrdinalIgnoreCase))
        {
            problems.Add("Storage:Provider=InMemory loses attachments on restart; use S3.");
        }

        if (!bool.TryParse(configuration["Attachments:RequireCleanScan"], out var requireScan) || !requireScan)
        {
            problems.Add("Attachments:RequireCleanScan must be true.");
        }

        // No malware scanning engine exists yet (D-08), so attachments stay off in production: nothing is uploaded, fetched from mail, stored or served.
        if (!bool.TryParse(configuration["Attachments:Enabled"], out var attachmentsEnabled) || attachmentsEnabled)
        {
            problems.Add("Attachments:Enabled must be false until a malware scanner is integrated (D-08).");
        }

        if (problems.Count > 0)
        {
            throw new InvalidOperationException("Unsafe production configuration: " + string.Join(" ", problems));
        }
    }

    /// <summary>Security headers on every response; the page policy on everything that is not API, the job dashboard keeps only framing protection.</summary>
    public static void UseResponseSecurityHeaders(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                var headers = context.Response.Headers;
                headers.XContentTypeOptions = "nosniff";
                headers["Referrer-Policy"] = "no-referrer";
                headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
                headers["Cross-Origin-Opener-Policy"] = "same-origin";
                headers["Cross-Origin-Resource-Policy"] = "same-origin";
                var path = context.Request.Path;
                if (path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
                {
                    headers.CacheControl = "no-store";
                    headers["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'";
                }
                else if (path.StartsWithSegments("/hangfire", StringComparison.OrdinalIgnoreCase))
                {
                    headers["Content-Security-Policy"] = "frame-ancestors 'none'";
                    headers.XFrameOptions = "DENY";
                }
                else
                {
                    headers["Content-Security-Policy"] = PageContentSecurityPolicy;
                    headers.XFrameOptions = "DENY";
                }

                return Task.CompletedTask;
            });
            await next();
        });
    }

    /// <summary>
    /// A browser socket carries the session cookie, so its Origin must be this site (the Host header) or one of <c>Realtime:AllowedOrigins</c>. A request without an Origin
    /// (not a browser) is left to authentication. Only the upgrade is checked: negotiate and polling are ordinary requests protected by CSRF and SameSite (SEC-009).
    /// </summary>
    public static void UseRealtimeOriginCheck(this WebApplication app)
    {
        var allowed = Configured(app.Configuration, "Realtime:AllowedOrigins")
            .Select(o => Uri.TryCreate(o, UriKind.Absolute, out var u) ? u.GetLeftPart(UriPartial.Authority) : o)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/hubs", StringComparison.OrdinalIgnoreCase)
                && string.Equals(context.Request.Headers.Upgrade, "websocket", StringComparison.OrdinalIgnoreCase)
                && context.Request.Headers.Origin.ToString() is { Length: > 0 } origin
                && !(Uri.TryCreate(origin, UriKind.Absolute, out var uri)
                    && (string.Equals(uri.Authority, context.Request.Host.Value, StringComparison.OrdinalIgnoreCase) || allowed.Contains(uri.GetLeftPart(UriPartial.Authority)))))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            await next();
        });
    }

    private static PhysicalFileProvider? SpaFiles(WebApplication app)
    {
        var root = app.Configuration["Spa:Root"] ?? app.Environment.WebRootPath;
        return root is not null && File.Exists(Path.Combine(root, "index.html")) ? new PhysicalFileProvider(root) : null;
    }

    /// <summary>Serves the built SPA files (before authorization: the page and its assets are public). A no-op when there is no build (development uses Vite).</summary>
    public static void UseSpaStaticFiles(this WebApplication app)
    {
        if (SpaFiles(app) is not { } files)
        {
            return;
        }

        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = files,
            OnPrepareResponse = ctx =>
            {
                // Vite fingerprints everything under /assets; index.html must always be revalidated so a release is picked up.
                ctx.Context.Response.Headers.CacheControl = ctx.Context.Request.Path.StartsWithSegments("/assets", StringComparison.OrdinalIgnoreCase)
                    ? "public, max-age=31536000, immutable"
                    : "no-cache";
            },
        });
    }

    /// <summary>Client routes fall back to index.html. API, hub, health and job paths are never answered with the page.</summary>
    public static void MapSinglePageApp(this WebApplication app)
    {
        if (SpaFiles(app) is not { } files)
        {
            return;
        }

        app.MapFallback(async context =>
        {
            if (ServerPaths.Any(p => context.Request.Path.StartsWithSegments(p, StringComparison.OrdinalIgnoreCase))
                || (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method)))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            context.Response.Headers.CacheControl = "no-cache";
            context.Response.ContentType = "text/html; charset=utf-8";
            await context.Response.SendFileAsync(files.GetFileInfo("index.html"));
        }).AllowAnonymous();
    }
}
