using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Helpdesk.SharedKernel.Database;

/// <summary>A module's database migration step. Applied by <see cref="ModuleMigrationRunner"/> in ascending <see cref="Order"/>.</summary>
public interface IModuleMigration
{
    string ModuleName { get; }

    /// <summary>Dependency order: audit (10), identity (20), organisation (30).</summary>
    int Order { get; }

    Task MigrateAsync(CancellationToken cancellationToken);
}

public sealed class DbContextModuleMigration<TContext>(IServiceScopeFactory scopes, string module, int order) : IModuleMigration
    where TContext : DbContext
{
    public string ModuleName { get; } = module;

    public int Order { get; } = order;

    public async Task MigrateAsync(CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        await db.Database.MigrateAsync(cancellationToken);
    }
}

public sealed partial class ModuleMigrationRunner(IEnumerable<IModuleMigration> migrations, ILogger<ModuleMigrationRunner> logger)
{
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        foreach (var migration in migrations.OrderBy(m => m.Order))
        {
            LogApplying(logger, migration.ModuleName);
            await migration.MigrateAsync(cancellationToken);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Applying migrations for module {Module}")]
    private static partial void LogApplying(ILogger logger, string module);
}
