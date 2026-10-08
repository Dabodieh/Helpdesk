using Helpdesk.Modules.Mail.Contracts;
using Helpdesk.Modules.Ticketing.Automation;
using Helpdesk.Modules.Ticketing.Sla;
using Helpdesk.Modules.Ticketing.Contracts;
using Helpdesk.Modules.Ticketing.Endpoints;
using Helpdesk.Modules.Ticketing.Persistence;
using Helpdesk.Modules.Ticketing.Portal;
using Helpdesk.Modules.Ticketing.Services;
using Helpdesk.SharedKernel.Database;
using Helpdesk.SharedKernel.Authorization;
using Helpdesk.SharedKernel.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Helpdesk.Modules.Ticketing;

public static class TicketingModule
{
    public const int MigrationOrder = 40;

    public static IServiceCollection AddTicketingModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddModuleDbContext<TicketingDbContext>(
            TicketingDbContext.Schema, "ticketing", MigrationOrder,
            (sp, options) => options.AddInterceptors(new NotificationOutboxInterceptor(sp)));
        services.AddScoped<IDepartmentScope, DepartmentScope>();

        services.AddScoped<TicketingAudit>();
        services.AddScoped<TicketResolver>();
        services.AddScoped<TicketProjection>();
        services.AddScoped<TicketService>();
        services.AddScoped<TicketQueryService>();
        services.AddScoped<TicketMergeService>();
        services.AddScoped<TicketParties>();
        services.AddScoped<TicketLinkService>();
        services.AddScoped<TicketFollowerService>();
        services.AddScoped<SignatureService>();
        services.AddScoped<AutoReplyService>();
        services.AddScoped<MacroSummariser>();
        services.AddScoped<MacroAdminService>();
        services.AddScoped<MacroRunService>();
        services.AddScoped<RequesterService>();
        services.AddScoped<RequesterHistoryService>();
        services.AddScoped<Fields.FieldCatalog>();
        services.AddScoped<Fields.TicketTypesService>();
        services.AddScoped<Fields.TicketFieldsService>();
        services.AddScoped<Fields.TicketPropertiesService>();
        services.AddScoped<SavedViewService>();
        services.AddScoped<CannedResponseService>();
        services.AddSignalR();
        services.AddSingleton<ITicketEvents, TicketEventPublisher>();
        services.AddSingleton<HubAccess>();
        services.AddSingleton<PresenceService>();
        services.AddHostedService<PresenceSweeper>();
        services.AddSingleton<IAccessChangeListener>(sp => sp.GetRequiredService<HubAccess>());
        services.AddHostedService<HubAccessRefresher>();
        services.AddScoped<SlaEngine>();
        services.AddScoped<RuleReferenceChecker>();
        services.AddScoped<SystemTicketOps>();
        services.AddScoped<AutomationEngine>();
        services.AddScoped<TicketLifecycle>();
        services.AddScoped<ISlaMonitor, SlaMonitor>();
        services.AddScoped<ISlaAlertBackfill, Sla.SlaAlertBackfill>();
        services.AddScoped<AutomationOutbox>();
        services.AddScoped<IAutomationOutbox>(sp => sp.GetRequiredService<AutomationOutbox>());
        services.AddSingleton<SharedKernel.Operations.IOperationalSignal, AutomationOutboxAgeSignal>();
        services.AddScoped<BusinessHoursService>();
        services.AddScoped<SlaPolicyService>();
        services.AddScoped<AutomationAdminService>();
        services.AddScoped<SlaReadService>();
        services.AddScoped<RequesterIdentityService>();
        services.AddScoped<RequesterIdentityAdminService>();
        services.AddScoped<DepartmentDashboardService>();
        services.AddScoped<DepartmentVolumeReportService>();
        services.AddScoped<QueuePreferencesService>();
        services.AddScoped<ReplyLinkService>();
        services.AddScoped<HelpTopicsAdminService>();
        services.AddScoped<PortalHelpTopicsService>();
        services.AddScoped<MentionService>();
        services.AddScoped<NotificationRecorder>();
        services.AddScoped<NotificationOutboxDrainer>();
        services.AddScoped<INotificationOutbox>(sp => sp.GetRequiredService<NotificationOutboxDrainer>());
        services.AddSingleton<SharedKernel.Operations.IOperationalSignal, NotificationOutboxAgeSignal>();
        services.AddScoped<NotificationService>();
        services.AddScoped<Helpdesk.Modules.Knowledge.Contracts.IRequesterAudience, RequesterAudience>();
        services.AddScoped<PortalFormAccess>();
        services.AddScoped<PortalFormsService>();
        services.AddScoped<PortalRequestService>();
        services.AddScoped<FormsAdminService>();
        services.AddScoped<ITicketIngress, TicketIngressService>();
        services.AddScoped<IInboundTicketSink, InboundTicketSink>();
        services.AddScoped<IDeliveryStateSink, DeliveryStateSink>();
        return services;
    }

    public static IEndpointRouteBuilder MapTicketingEndpoints(this IEndpointRouteBuilder app)
    {
        TicketEndpoints.Map(app);
        app.MapHub<TicketHub>("/hubs/tickets").RequireAuthenticatedOnly();
        return app;
    }
}
