using Helpdesk.Modules.Mail.Contracts;
using Helpdesk.Modules.Ticketing.Contracts;
using Helpdesk.Modules.Ticketing.Endpoints;
using Helpdesk.Modules.Ticketing.Persistence;
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

        services.AddModuleDbContext<TicketingDbContext>(TicketingDbContext.Schema, "ticketing", MigrationOrder);
        services.AddScoped<IDepartmentScope, DepartmentScope>();

        services.AddScoped<TicketingAudit>();
        services.AddScoped<TicketResolver>();
        services.AddScoped<TicketProjection>();
        services.AddScoped<TicketService>();
        services.AddScoped<TicketQueryService>();
        services.AddScoped<RequesterService>();
        services.AddScoped<SavedViewService>();
        services.AddScoped<CannedResponseService>();
        services.AddSignalR();
        services.AddSingleton<ITicketEvents, TicketEventPublisher>();
        services.AddSingleton<HubAccess>();
        services.AddSingleton<IAccessChangeListener>(sp => sp.GetRequiredService<HubAccess>());
        services.AddHostedService<HubAccessRefresher>();
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
