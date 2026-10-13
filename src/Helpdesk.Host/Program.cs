using Helpdesk.Host.Errors;
using Helpdesk.Host.Hosting;
using Helpdesk.Host.Operations;
using Helpdesk.Host.Jobs;
using Helpdesk.Modules.Attachments;
using Helpdesk.Modules.Audit;
using Helpdesk.Modules.Identity;
using Helpdesk.Modules.Knowledge;
using Helpdesk.Modules.Mail;
using Helpdesk.Modules.Organisation;
using Helpdesk.Modules.Ticketing;
using Helpdesk.SharedKernel.Database;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;

Log.Logger = new LoggerConfiguration().WriteTo.Console(formatProvider: System.Globalization.CultureInfo.InvariantCulture).CreateBootstrapLogger();

try
{
    // `dotnet run --project src/Helpdesk.Host -- migrate` applies all module migrations and exits (no web server).
    var migrateOnly = args.Length > 0 && string.Equals(args[0], "migrate", StringComparison.OrdinalIgnoreCase);
    // `-- sla-alert-backfill [--apply]` fills in the 80% SLA alert instants of older tickets and exits. Dry run unless --apply; never runs on startup.
    var backfillSlaAlerts = args.Length > 0 && string.Equals(args[0], "sla-alert-backfill", StringComparison.OrdinalIgnoreCase);
    var applyBackfill = backfillSlaAlerts && args.Contains("--apply", StringComparer.OrdinalIgnoreCase);
    var builder = WebApplication.CreateBuilder(migrateOnly || backfillSlaAlerts ? args[1..] : args);

    builder.Host.UseSerilog((ctx, services, cfg) => cfg
        .ReadFrom.Configuration(ctx.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext(),
        // Keep the bootstrap logger usable: several hosts per process (integration tests) must not freeze it.
        // writeToProviders forwards events to the OpenTelemetry log provider below (the central sink, decision D7d); console output stays Serilog's.
        preserveStaticLogger: true,
        writeToProviders: true);

    var otlpEndpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
    builder.Logging.ClearProviders();
    if (!string.IsNullOrWhiteSpace(otlpEndpoint))
    {
        builder.Logging.AddOpenTelemetry(o => o.AddOtlpExporter());
    }

    builder.Services.AddOptions<DatabaseOptions>()
        .Bind(builder.Configuration.GetSection(DatabaseOptions.Section))
        .ValidateDataAnnotations()
        .ValidateOnStart();

    var connectionString = builder.Configuration.GetSection(DatabaseOptions.Section)[nameof(DatabaseOptions.ConnectionString)];

    builder.Services.AddHealthChecks()
        .AddCheck("self", () => HealthCheckResult.Healthy(), tags: ["live"])
        .AddNpgSql(connectionString!, name: "postgres", tags: ["ready"])
        .AddCheck<ObjectStoreHealthCheck>("object-store", tags: ["ready"])
        .AddCheck<MigrationsHealthCheck>("migrations", tags: ["ready"]);
    builder.Services.AddHostedService<OperationalMetrics>();

    builder.Services.AddAuditModule();
    builder.Services.AddIdentityModule(builder.Configuration, builder.Environment);
    builder.Services.AddOrganisationModule();
    builder.Services.AddAttachmentsModule(builder.Configuration);
    builder.Services.AddMailModule(builder.Configuration, builder.Environment);
    builder.Services.AddBackgroundJobs(builder.Configuration);
    builder.Services.AddKnowledgeModule();
    builder.Services.AddTicketingModule();

    builder.Services.AddProblemDetails();
    builder.Services.AddExceptionHandler<HelpdeskExceptionHandler>();
    builder.Services.AddOpenApi();

    builder.Services.Configure<ForwardedHeadersOptions>(o => ProductionHosting.ConfigureForwardedHeaders(o, builder.Configuration));
    builder.Services.AddProductionSettingsGuard();
    builder.Services.AddAbuseControls(builder.Configuration);
    builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = AbuseControls.DefaultMaxRequestBodyBytes);

    builder.Services.AddOpenTelemetry()
        .ConfigureResource(r => r.AddService("helpdesk-host"))
        .WithTracing(t =>
        {
            t.AddAspNetCoreInstrumentation();
            if (!string.IsNullOrWhiteSpace(otlpEndpoint))
            {
                t.AddOtlpExporter();
            }
        })
        .WithMetrics(m =>
        {
            m.AddAspNetCoreInstrumentation().AddMeter(OperationalMetrics.MeterName);
            if (!string.IsNullOrWhiteSpace(otlpEndpoint))
            {
                m.AddOtlpExporter();
            }
        });

    var app = builder.Build();

    if (migrateOnly)
    {
        await app.Services.GetRequiredService<ModuleMigrationRunner>().RunAsync();
        Log.Information("All module migrations applied");
        return;
    }

    if (backfillSlaAlerts)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<Helpdesk.Modules.Ticketing.Contracts.ISlaAlertBackfill>().RunAsync(applyBackfill, CancellationToken.None);
        Log.Information(
            "SLA alert backfill {Mode}: {Tickets} tickets, {Scheduled} alerts scheduled, {Past} clocks already past 80% (marked, no notification)",
            result.Applied ? "applied" : "dry run (add --apply to write)", result.Tickets, result.ClocksScheduled, result.ClocksAlreadyPast);
        return;
    }

    if (app.Services.GetRequiredService<IOptions<DatabaseOptions>>().Value.MigrateOnStartup)
    {
        await app.Services.GetRequiredService<ModuleMigrationRunner>().RunAsync();
    }

    app.UseForwardedHeaders();
    if (!app.Environment.IsDevelopment() && !app.Environment.IsEnvironment("Testing"))
    {
        app.UseHsts();
    }

    app.UseResponseSecurityHeaders();
    app.UseSpaStaticFiles();

    app.UseExceptionHandler();
    app.UseSerilogRequestLogging(o =>
    {
        o.Logger = app.Services.GetRequiredService<Serilog.ILogger>();
        var properties = o.GetMessageTemplateProperties;
        // Legacy invitation URLs can still arrive from old emails. Do not put their token in request logs.
        o.GetMessageTemplateProperties = (context, path, elapsed, status) =>
            properties(context, context.Request.Path.StartsWithSegments("/activate") ? "/activate" : path, elapsed, status);
    });

    app.UseRealtimeOriginCheck();
    app.UseRouting();
    app.UseIdentityModule();   // authentication + CSRF validation
    app.UseRateLimiter();      // per-user policies need the signed-in user
    app.UseAuthorization();
    app.UseJobDashboard(builder.Configuration);

    // Probes and OpenAPI are anonymous on purpose; everything else falls under the authenticated-user fallback policy.
    app.MapHealthChecks("/health/live", new() { Predicate = h => h.Tags.Contains("live") }).AllowAnonymous();
    app.MapHealthChecks("/health/ready", new() { Predicate = h => h.Tags.Contains("ready") }).AllowAnonymous();

    if (app.Environment.IsDevelopment())
    {
        app.MapOpenApi().AllowAnonymous();
    }

    app.MapIdentityEndpoints();
    app.MapOrganisationEndpoints();
    app.MapTicketingEndpoints();
    app.MapKnowledgeEndpoints();
    app.MapMailEndpoints(app.Environment);
    app.MapAuditEndpoints();
    app.MapSinglePageApp();

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
