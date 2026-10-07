using Helpdesk.Modules.Mail.Contracts;
using Helpdesk.Modules.Mail.Endpoints;
using Helpdesk.Modules.Mail.Persistence;
using Helpdesk.Modules.Mail.Providers;
using Helpdesk.Modules.Mail.Services;
using Helpdesk.SharedKernel.Database;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Helpdesk.Modules.Mail;

public static class MailModule
{
    /// <summary>After Organisation (30), before Ticketing (40): tickets reference mailboxes through a composite foreign key.</summary>
    public const int MigrationOrder = 35;

    public static IServiceCollection AddMailModule(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        services.AddModuleDbContext<MailDbContext>(MailDbContext.Schema, "mail", MigrationOrder);

        var devLike = environment.IsDevelopment() || environment.IsEnvironment("Testing");
        services.AddOptions<MailOptions>()
            .Bind(configuration.GetSection(MailOptions.Section))
            .ValidateDataAnnotations()
            .Validate(o => o.Provider is "Graph" or "Fake", "Mail:Provider must be Graph or Fake.")
            .Validate(o => o.Provider != "Fake" || devLike, "Mail:Provider=Fake is only allowed in Development and Testing.")
            .Validate(
                o => o.Provider != "Graph" || (!string.IsNullOrWhiteSpace(o.Graph.TenantId) && !string.IsNullOrWhiteSpace(o.Graph.ClientId)
                    && (!string.IsNullOrWhiteSpace(o.Graph.ClientSecret) || !string.IsNullOrWhiteSpace(o.Graph.CertificatePath))),
                "Mail:Graph needs TenantId, ClientId and a ClientSecret or CertificatePath (secrets via user-secrets or environment).")
            .Validate(
                o => o.Provider != "Graph" || (Uri.TryCreate(o.NotificationBaseUrl, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps),
                "Mail:NotificationBaseUrl must be a public https URL for Graph webhooks.")
            .ValidateOnStart();

        var provider = configuration.GetSection(MailOptions.Section)[nameof(MailOptions.Provider)] ?? "Graph";
        if (string.Equals(provider, "Fake", StringComparison.OrdinalIgnoreCase))
        {
            services.TryAddSingleton<FakeMailProvider>();
            services.TryAddSingleton<IMailProvider>(sp => sp.GetRequiredService<FakeMailProvider>());
        }
        else
        {
            services.AddHttpClient(MicrosoftGraphMailProvider.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(60));
            services.TryAddSingleton<IMailProvider, MicrosoftGraphMailProvider>();
        }

        services.TryAddSingleton<IMailJobScheduler, NullMailJobScheduler>();

        services.AddScoped<MailAudit>();
        services.AddScoped<CorrelationEngine>();
        services.AddScoped<InboundProcessor>();
        services.AddScoped<OutboundSender>();
        services.AddScoped<MailSyncService>();
        services.AddScoped<SubscriptionService>();
        services.AddScoped<NotificationService>();
        services.AddScoped<MailboxService>();
        services.AddScoped<IMailJobRunner, MailJobRunner>();
        services.AddScoped<IMailboxDirectory, MailboxDirectory>();
        services.AddScoped<IMailOutbox, MailOutbox>();
        services.AddSingleton<IEmailHtmlSanitiser, EmailHtmlSanitiser>();
        return services;
    }

    public static IEndpointRouteBuilder MapMailEndpoints(this IEndpointRouteBuilder app, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(environment);

        var options = app.ServiceProvider.GetRequiredService<IOptions<MailOptions>>().Value;
        var enableDev = (environment.IsDevelopment() || environment.IsEnvironment("Testing")) && options.Provider == "Fake";
        MailEndpoints.Map(app, enableDev);
        return app;
    }
}

/// <summary>Used when no scheduler is registered (tests drive the runner directly): nudges are dropped, the periodic jobs still cover everything.</summary>
internal sealed class NullMailJobScheduler : IMailJobScheduler
{
    public void EnqueueSync(Guid mailboxId)
    {
    }

    public void EnqueueSend(Guid outboundId)
    {
    }
}
