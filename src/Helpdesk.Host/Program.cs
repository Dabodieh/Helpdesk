using Helpdesk.Host.Errors;
using Helpdesk.Modules.Audit;
using Helpdesk.Modules.Identity;
using Helpdesk.Modules.Organisation;
using Helpdesk.SharedKernel.Database;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;

Log.Logger = new LoggerConfiguration().WriteTo.Console(formatProvider: System.Globalization.CultureInfo.InvariantCulture).CreateBootstrapLogger();

try
{
    // `dotnet run --project src/Helpdesk.Host -- migrate` applies all module migrations and exits (no web server).
    var migrateOnly = args.Length > 0 && string.Equals(args[0], "migrate", StringComparison.OrdinalIgnoreCase);
    var builder = WebApplication.CreateBuilder(migrateOnly ? args[1..] : args);

    builder.Host.UseSerilog((ctx, services, cfg) => cfg
        .ReadFrom.Configuration(ctx.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext(),
        // Keep the bootstrap logger usable: several hosts per process (integration tests) must not freeze it.
        preserveStaticLogger: true);

    builder.Services.AddOptions<DatabaseOptions>()
        .Bind(builder.Configuration.GetSection(DatabaseOptions.Section))
        .ValidateDataAnnotations()
        .ValidateOnStart();

    var connectionString = builder.Configuration.GetSection(DatabaseOptions.Section)[nameof(DatabaseOptions.ConnectionString)];

    builder.Services.AddHealthChecks()
        .AddCheck("self", () => HealthCheckResult.Healthy(), tags: ["live"])
        .AddNpgSql(connectionString!, name: "postgres", tags: ["ready"]);

    builder.Services.AddAuditModule();
    builder.Services.AddIdentityModule(builder.Configuration, builder.Environment);
    builder.Services.AddOrganisationModule();

    builder.Services.AddProblemDetails();
    builder.Services.AddExceptionHandler<HelpdeskExceptionHandler>();
    builder.Services.AddOpenApi();

    builder.Services.Configure<ForwardedHeadersOptions>(o =>
    {
        o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        // Trust only the configured reverse proxies (Proxy:KnownProxies); see docs/security/SECURITY.md.
        o.KnownProxies.Clear();
        foreach (var ip in builder.Configuration.GetSection("Proxy:KnownProxies").Get<string[]>() ?? [])
        {
            o.KnownProxies.Add(System.Net.IPAddress.Parse(ip));
        }
    });

    builder.Services.AddOpenTelemetry()
        .ConfigureResource(r => r.AddService("helpdesk-host"))
        .WithTracing(t =>
        {
            t.AddAspNetCoreInstrumentation();
            if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
            {
                t.AddOtlpExporter();
            }
        });

    var app = builder.Build();

    if (migrateOnly)
    {
        await app.Services.GetRequiredService<ModuleMigrationRunner>().RunAsync();
        Log.Information("All module migrations applied");
        return;
    }

    if (app.Services.GetRequiredService<IOptions<DatabaseOptions>>().Value.MigrateOnStartup)
    {
        await app.Services.GetRequiredService<ModuleMigrationRunner>().RunAsync();
    }

    app.UseForwardedHeaders();
    app.UseExceptionHandler();
    app.UseSerilogRequestLogging();

    app.UseRouting();
    app.UseIdentityModule();   // authentication + CSRF validation
    app.UseAuthorization();

    // Probes and OpenAPI are anonymous on purpose; everything else falls under the authenticated-user fallback policy.
    app.MapHealthChecks("/health/live", new() { Predicate = h => h.Tags.Contains("live") }).AllowAnonymous();
    app.MapHealthChecks("/health/ready", new() { Predicate = h => h.Tags.Contains("ready") }).AllowAnonymous();

    if (app.Environment.IsDevelopment())
    {
        app.MapOpenApi().AllowAnonymous();
    }

    app.MapIdentityEndpoints();
    app.MapOrganisationEndpoints();
    app.MapAuditEndpoints();

    app.Run();
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "Host terminated unexpectedly");
    throw;
}
finally
{
    Log.CloseAndFlush();
}

public partial class Program;
