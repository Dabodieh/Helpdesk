using Helpdesk.Modules.Audit.Contracts;
using Helpdesk.Modules.Audit.Endpoints;
using Helpdesk.Modules.Audit.Persistence;
using Helpdesk.Modules.Audit.Services;
using Helpdesk.SharedKernel.Database;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Helpdesk.Modules.Audit;

public static class AuditModule
{
    public const int MigrationOrder = 10;

    public static IServiceCollection AddAuditModule(this IServiceCollection services)
    {
        services.AddModuleDbContext<AuditDbContext>(AuditDbContext.Schema, "audit", MigrationOrder);
        services.AddScoped<IAuditWriter, AuditWriter>();
        services.AddScoped<AuditQueries>();
        return services;
    }

    public static IEndpointRouteBuilder MapAuditEndpoints(this IEndpointRouteBuilder app)
    {
        AuditEndpoints.Map(app);
        return app;
    }
}
