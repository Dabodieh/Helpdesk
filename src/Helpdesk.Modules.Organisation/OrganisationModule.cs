using Helpdesk.Modules.Organisation.Authorization;
using Helpdesk.Modules.Organisation.Endpoints;
using Helpdesk.Modules.Organisation.Persistence;
using Helpdesk.Modules.Organisation.Services;
using Helpdesk.SharedKernel.Authorization;
using Helpdesk.SharedKernel.Database;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Helpdesk.Modules.Organisation;

public static class OrganisationModule
{
    public const int MigrationOrder = 30;

    public static IServiceCollection AddOrganisationModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddModuleDbContext<OrganisationDbContext>(OrganisationDbContext.Schema, "organisation", MigrationOrder);
        services.AddHttpContextAccessor();

        services.AddScoped<MembershipCache>();
        services.AddScoped<IAuthorizer, Authorizer>();
        services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();
        services.AddAuthorization(options =>
        {
            // Everything requires a signed-in user unless it is explicitly AllowAnonymous.
            options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
        });

        services.AddScoped<OrganisationAudit>();
        services.AddScoped<DepartmentService>();
        services.AddScoped<TeamService>();
        services.AddScoped<MembershipService>();
        services.AddScoped<MeService>();
        return services;
    }

    public static IEndpointRouteBuilder MapOrganisationEndpoints(this IEndpointRouteBuilder app)
    {
        OrganisationEndpoints.Map(app);
        return app;
    }
}
