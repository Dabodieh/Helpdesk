using Helpdesk.Host.Configuration;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;

Log.Logger = new LoggerConfiguration().WriteTo.Console(formatProvider: System.Globalization.CultureInfo.InvariantCulture).CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog((ctx, services, cfg) => cfg
        .ReadFrom.Configuration(ctx.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext());

    builder.Services.AddOptions<DatabaseOptions>()
        .Bind(builder.Configuration.GetSection(DatabaseOptions.Section))
        .ValidateDataAnnotations()
        .ValidateOnStart();

    var connectionString = builder.Configuration.GetSection(DatabaseOptions.Section)[nameof(DatabaseOptions.ConnectionString)];

    builder.Services.AddHealthChecks()
        .AddCheck("self", () => HealthCheckResult.Healthy(), tags: ["live"])
        .AddNpgSql(connectionString!, name: "postgres", tags: ["ready"]);

    builder.Services.AddProblemDetails();
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

    app.UseForwardedHeaders();
    app.UseExceptionHandler();
    app.UseSerilogRequestLogging();

    app.MapHealthChecks("/health/live", new() { Predicate = h => h.Tags.Contains("live") });
    app.MapHealthChecks("/health/ready", new() { Predicate = h => h.Tags.Contains("ready") });

    if (app.Environment.IsDevelopment())
    {
        app.MapOpenApi();
    }

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
