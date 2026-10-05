using Helpdesk.SharedKernel.Authorization;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Modules.Ticketing.Persistence;

/// <summary>
/// The caller's accessible departments for <c>tickets.read</c>, loaded from <see cref="IAuthorizer"/> once per request. It feeds
/// the global query filter of <see cref="TicketingDbContext"/>. <b>Fail closed:</b> until <see cref="EnsureLoadedAsync"/> ran,
/// <see cref="DepartmentIds"/> is empty and every filtered query matches nothing.
/// </summary>
internal interface IDepartmentScope
{
    bool IsLoaded { get; }

    Guid[] DepartmentIds { get; }

    Task EnsureLoadedAsync(CancellationToken cancellationToken);
}

internal sealed class DepartmentScope(IAuthorizer authorizer) : IDepartmentScope
{
    private Guid[]? _ids;

    public bool IsLoaded => _ids is not null;

    public Guid[] DepartmentIds => _ids ?? [];

    public async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        _ids ??= [.. await authorizer.AccessibleDepartmentsAsync(PermissionCodes.TicketsRead, cancellationToken)];
    }
}

/// <summary>A scope that never grants anything (design-time tooling, tests of the fail-closed behaviour).</summary>
internal sealed class EmptyDepartmentScope : IDepartmentScope
{
    public bool IsLoaded => false;

    public Guid[] DepartmentIds => [];

    public Task EnsureLoadedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// The one named, reviewed bypass of the department scope filter. Allowed only for system work that carries an explicit
/// department (ingestion) and tests; an architecture test pins the set of callers. Every query using it must also filter by
/// department id itself.
/// </summary>
internal static class DepartmentScopeBypass
{
    public static IQueryable<T> IgnoreDepartmentScope<T>(this IQueryable<T> query)
        where T : class => query.IgnoreQueryFilters();
}
