using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Modules.Audit.Contracts;

public static class AuditCategories
{
    public const string Organisation = "organisation";
    public const string Identity = "identity";
}

public static class AuditSources
{
    public const string Web = "web";
    public const string Bootstrap = "bootstrap";
}

/// <summary>
/// One audit event. <see cref="Previous"/>/<see cref="Next"/> are serialised to jsonb by the writer: callers pass
/// minimal, already-redacted objects (never tokens, claims dumps or message bodies).
/// </summary>
public sealed record AuditEntry(
    string Category,
    string Action,
    string ObjectType,
    Guid? ObjectId,
    Guid? DepartmentId,
    Guid? ActorUserId,
    string? ActorDisplayName,
    object? Previous = null,
    object? Next = null,
    string Source = AuditSources.Web);

public interface IAuditWriter
{
    /// <summary>
    /// Inserts the event on the <paramref name="caller"/>'s connection and current transaction so that the state change and
    /// its audit record commit or roll back together. Throws <see cref="InvalidOperationException"/> when the caller has no
    /// explicit transaction.
    /// </summary>
    Task WriteAsync(AuditEntry entry, DbContext caller, CancellationToken cancellationToken = default);
}
