using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Modules.Audit.Contracts;

public static class AuditCategories
{
    public const string Organisation = "organisation";
    public const string Identity = "identity";
    public const string Ticketing = "ticketing";
}

public static class AuditSources
{
    public const string Web = "web";
    public const string Bootstrap = "bootstrap";

    /// <summary>Changes made by the system itself (ingestion, automation) rather than a signed-in user; the actor is null.</summary>
    public const string System = "system";
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

/// <summary>One stored audit event as read back (previous/next exactly as written: already redacted by the writer's caller).</summary>
public sealed record AuditRecord(
    Guid Id,
    DateTimeOffset OccurredAt,
    Guid? ActorUserId,
    string? ActorDisplayName,
    string Action,
    JsonElement? Previous,
    JsonElement? Next,
    string Source);

public sealed record AuditRecordPage(IReadOnlyList<AuditRecord> Items, string? NextCursor);

/// <summary>Read access to the audit stream of one object for other modules (e.g. the ticket activity feed).</summary>
public interface IAuditReader
{
    /// <summary>
    /// Events of <paramref name="objectType"/>/<paramref name="objectId"/> in <paramref name="category"/> within the given department,
    /// newest first. <paramref name="limit"/> must be 1..200; an invalid <paramref name="cursor"/> throws a validation error.
    /// The caller is responsible for having authorised access to the object.
    /// </summary>
    Task<AuditRecordPage> ReadObjectEventsAsync(
        string category, string objectType, Guid objectId, Guid departmentId, string? cursor, int limit, CancellationToken cancellationToken = default);
}
