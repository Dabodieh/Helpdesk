using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Helpdesk.SharedKernel.Database;

public static class ModuleDbContextExtensions
{
    public const string MigrationsHistoryTable = "__ef_migrations_history";

    /// <summary>
    /// Registers a module DbContext against the shared PostgreSQL database with its own schema, its own migrations history
    /// table inside that schema, and the module's migration step for the <c>migrate</c> command.
    /// </summary>
    public static IServiceCollection AddModuleDbContext<TContext>(this IServiceCollection services, string schema, string moduleName, int migrationOrder)
        where TContext : DbContext
    {
        services.AddDbContext<TContext>((sp, options) =>
            options.UseNpgsql(
                sp.GetRequiredService<IOptions<DatabaseOptions>>().Value.ConnectionString,
                npgsql => npgsql.MigrationsHistoryTable(MigrationsHistoryTable, schema)));
        services.AddSingleton<IModuleMigration>(sp => new DbContextModuleMigration<TContext>(
            sp.GetRequiredService<IServiceScopeFactory>(), moduleName, migrationOrder));
        services.TryAddSingleton<ModuleMigrationRunner>();
        services.TryAddSingleton(TimeProvider.System);
        return services;
    }

    /// <summary>Design-time options (EF tooling only). The connection string is only used to pick the provider.</summary>
    public static DbContextOptions<TContext> DesignTimeOptions<TContext>(string schema)
        where TContext : DbContext
    {
        var connection = Environment.GetEnvironmentVariable("HELPDESK_DESIGN_DB") ?? "Host=localhost;Database=helpdesk_design";
        return new DbContextOptionsBuilder<TContext>()
            .UseNpgsql(connection, npgsql => npgsql.MigrationsHistoryTable(MigrationsHistoryTable, schema))
            .Options;
    }
}
