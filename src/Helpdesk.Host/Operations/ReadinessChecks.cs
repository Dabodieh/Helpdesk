using Helpdesk.Modules.Attachments.Contracts;
using Helpdesk.SharedKernel.Database;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Helpdesk.Host.Operations;

/// <summary>
/// Readiness (decision D4): not ready when the object store is unreachable or a module's schema is not fully migrated. A job backlog never fails readiness:
/// it is a metric (<see cref="OperationalMetrics"/>), because restarting or removing the web instance would not clear it.
/// </summary>
internal sealed class ObjectStoreHealthCheck(IObjectStore store) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));

            // A read of a key that never exists answers "not found" from a reachable store and throws from an unreachable one; nothing is written.
            using var stream = await store.OpenReadAsync("_health/ready-probe", timeout.Token);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return HealthCheckResult.Unhealthy("object store unreachable", data: new Dictionary<string, object> { ["error"] = ex.GetType().Name });
        }
    }
}

internal sealed class MigrationsHealthCheck(IEnumerable<IModuleMigration> migrations) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var behind = new List<string>();
        foreach (var migration in migrations.OrderBy(m => m.Order))
        {
            if (!await migration.IsUpToDateAsync(cancellationToken))
            {
                behind.Add(migration.ModuleName);
            }
        }

        return behind.Count == 0 ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("pending migrations: " + string.Join(", ", behind));
    }
}
