using Hangfire;
using Hangfire.Dashboard;
using Hangfire.PostgreSql;
using Helpdesk.Modules.Mail.Contracts;
using Helpdesk.SharedKernel.Security;

namespace Helpdesk.Host.Jobs;

internal static class HangfireSetup
{
    /// <summary>The recurring jobs whose last start is exported as a lag metric (<c>helpdesk_job_&lt;suffix&gt;_lag_seconds</c>); a job that stops starting shows as growing lag.</summary>
    internal static readonly (string JobId, string MetricSuffix)[] MonitoredJobs =
    [
        ("mail-sync-all", "mail_sync"), ("mail-dispatch", "mail_dispatch"), ("mail-subscriptions", "mail_subscriptions"), ("sla-monitor", "sla_monitor"), ("notification-outbox", "notification_drain"),
    ];

    /// <summary>Background jobs run in this process (ROLE=all). <c>Jobs:Enabled=false</c> (tests) registers no server and a no-op scheduler.</summary>
    public static IServiceCollection AddBackgroundJobs(this IServiceCollection services, IConfiguration configuration)
    {
        if (!configuration.GetValue("Jobs:Enabled", true))
        {
            return services;
        }

        var connectionString = configuration["Database:ConnectionString"]!;
        services.AddHangfire(c => c
            .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
            .UseSimpleAssemblyNameTypeSerializer()
            .UseRecommendedSerializerSettings()
            .UsePostgreSqlStorage(o => o.UseNpgsqlConnection(connectionString), new PostgreSqlStorageOptions { SchemaName = "hangfire" }));
        services.AddHangfireServer(o => o.WorkerCount = 4);
        services.AddScoped<MailJobs>();
        services.AddScoped<SlaJobs>();
        services.AddScoped<NotificationJobs>();
        services.AddSingleton<IMailJobScheduler, HangfireMailJobScheduler>();
        services.AddHostedService<RecurringJobsRegistration>();
        services.AddSingleton<Helpdesk.SharedKernel.Operations.IOperationalSignal, Operations.FailedJobsSignal>();
        foreach (var (jobId, suffix) in MonitoredJobs)
        {
            services.AddSingleton<Helpdesk.SharedKernel.Operations.IOperationalSignal>(sp => new Operations.RecurringJobLagSignal(jobId, suffix, sp.GetRequiredService<TimeProvider>()));
        }

        return services;
    }

    /// <summary>Read-only dashboard for platform administrators only (read-only avoids unsafe dashboard actions outside the CSRF pipeline).</summary>
    public static IApplicationBuilder UseJobDashboard(this IApplicationBuilder app, IConfiguration configuration)
    {
        if (configuration.GetValue("Jobs:Enabled", true))
        {
            app.UseHangfireDashboard("/hangfire", new DashboardOptions
            {
                Authorization = [new PlatformAdminDashboardFilter()],
                IsReadOnlyFunc = _ => true,
                DashboardTitle = "Helpdesk jobs",
            });
        }

        return app;
    }
}

internal sealed class PlatformAdminDashboardFilter : IDashboardAuthorizationFilter
{
    public bool Authorize(DashboardContext context)
    {
        var http = context.GetHttpContext();
        var user = http.RequestServices.GetRequiredService<ICurrentUser>();
        return user.IsAuthenticated && user.IsPlatformAdmin;
    }
}

/// <summary>Registers the periodic jobs once the host has started (reconciliation, dispatch, subscription renewal).</summary>
internal sealed class RecurringJobsRegistration(IRecurringJobManager recurring) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Delta reconciliation is the source of truth: it covers missed webhooks and expired subscriptions.
        recurring.AddOrUpdate<MailJobs>("mail-sync-all", j => j.SyncAll(CancellationToken.None), "*/5 * * * *");
        recurring.AddOrUpdate<MailJobs>("mail-dispatch", j => j.Dispatch(CancellationToken.None), "* * * * *");
        recurring.AddOrUpdate<MailJobs>("mail-subscriptions", j => j.MaintainSubscriptions(CancellationToken.None), "0 */6 * * *");
        recurring.AddOrUpdate<SlaJobs>("sla-monitor", j => j.Monitor(CancellationToken.None), "* * * * *");
        recurring.AddOrUpdate<NotificationJobs>("notification-outbox", j => j.Drain(CancellationToken.None), "* * * * *");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
