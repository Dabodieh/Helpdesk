using Helpdesk.Modules.Identity.Authentication;
using Helpdesk.Modules.Identity.Contracts;
using Helpdesk.Modules.Identity.Endpoints;
using Helpdesk.Modules.Identity.Persistence;
using Helpdesk.Modules.Identity.Services;
using Helpdesk.SharedKernel.Database;
using Helpdesk.SharedKernel.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Helpdesk.Modules.Identity;

public static class IdentityModule
{
    public const int MigrationOrder = 20;

    public static IServiceCollection AddIdentityModule(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        services.AddOptions<HelpdeskAuthenticationOptions>()
            .Bind(configuration.GetSection(HelpdeskAuthenticationOptions.Section))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<HelpdeskAuthenticationOptions>>(new AuthenticationOptionsValidator(environment));

        services.AddModuleDbContext<IdentityDbContext>(IdentityDbContext.Schema, "identity", MigrationOrder);
        services.AddHttpContextAccessor();
        services.AddScoped<CurrentUser>();
        services.AddScoped<ICurrentUser>(sp => sp.GetRequiredService<CurrentUser>());
        services.AddScoped<IUserProvisioner, UserProvisioner>();
        services.AddScoped<IUserSessionValidator, UserSessionValidator>();
        services.AddScoped<IUserDirectory, UserDirectory>();
        services.AddScoped<PlatformUserService>();
        services.AddScoped<EntraSetupService>();
        services.AddSingleton<EntraSetupSessions>();
        services.AddOptions<EntraSetupOptions>().BindConfiguration(EntraSetupOptions.Section);
        services.AddHttpClient(EntraSetupService.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(30));

        DataProtectionKeyStore.Add(services, configuration);
        AuthenticationSetup.Configure(services, configuration, environment);
        return services;
    }

    /// <summary>Adds authentication and CSRF validation. Call after UseRouting and before UseAuthorization.</summary>
    public static IApplicationBuilder UseIdentityModule(this IApplicationBuilder app)
    {
        app.UseAuthentication();
        app.UseMiddleware<CsrfMiddleware>();
        return app;
    }

    public static IEndpointRouteBuilder MapIdentityEndpoints(this IEndpointRouteBuilder app)
    {
        AuthEndpoints.Map(app);
        UserEndpoints.Map(app);
        EntraEndpoints.Map(app);
        return app;
    }
}
