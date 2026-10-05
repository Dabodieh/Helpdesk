using System.Diagnostics;
using System.Text.Json;
using Helpdesk.Modules.Audit.Contracts;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace Helpdesk.Modules.Audit.Services;

internal sealed class AuditWriter(TimeProvider clock) : IAuditWriter
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private const string InsertSql = """
        INSERT INTO audit.audit_events
            (id, occurred_at, category, action, actor_user_id, actor_display_name, object_type, object_id,
             department_id, previous, next, source, correlation_id)
        VALUES
            (@id, @occurred_at, @category, @action, @actor_user_id, @actor_display_name, @object_type, @object_id,
             @department_id, @previous, @next, @source, @correlation_id)
        """;

    public async Task WriteAsync(AuditEntry entry, DbContext caller, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(caller);

        if (caller.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("IAuditWriter requires the caller to run inside an explicit transaction.");
        }

        var parameters = new object[]
        {
            new NpgsqlParameter("id", Guid.CreateVersion7()),
            new NpgsqlParameter("occurred_at", clock.GetUtcNow()),
            new NpgsqlParameter("category", entry.Category),
            new NpgsqlParameter("action", entry.Action),
            new NpgsqlParameter("actor_user_id", NpgsqlDbType.Uuid) { Value = (object?)entry.ActorUserId ?? DBNull.Value },
            new NpgsqlParameter("actor_display_name", NpgsqlDbType.Text) { Value = (object?)entry.ActorDisplayName ?? DBNull.Value },
            new NpgsqlParameter("object_type", entry.ObjectType),
            new NpgsqlParameter("object_id", NpgsqlDbType.Uuid) { Value = (object?)entry.ObjectId ?? DBNull.Value },
            new NpgsqlParameter("department_id", NpgsqlDbType.Uuid) { Value = (object?)entry.DepartmentId ?? DBNull.Value },
            JsonParameter("previous", entry.Previous),
            JsonParameter("next", entry.Next),
            new NpgsqlParameter("source", entry.Source),
            new NpgsqlParameter("correlation_id", NpgsqlDbType.Text) { Value = (object?)Activity.Current?.TraceId.ToString() ?? DBNull.Value },
        };

        await caller.Database.ExecuteSqlRawAsync(InsertSql, parameters, cancellationToken);
    }

    private static NpgsqlParameter JsonParameter(string name, object? value) =>
        new(name, NpgsqlDbType.Jsonb) { Value = value is null ? DBNull.Value : JsonSerializer.Serialize(value, Json) };
}
