using System.Data.Common;
using System.Text.RegularExpressions;
using Helpdesk.Modules.Audit.Contracts;
using Helpdesk.Modules.Ticketing.Domain;
using Helpdesk.Modules.Ticketing.Persistence;
using Helpdesk.SharedKernel.Database;
using Helpdesk.SharedKernel.Errors;
using Helpdesk.SharedKernel.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Helpdesk.Modules.Ticketing.Services;

/// <summary>Writes ticketing-category audit events on the Ticketing DbContext's current transaction. Never receives message bodies.</summary>
internal sealed class TicketingAudit(IAuditWriter writer, TicketingDbContext db, ICurrentUser current)
{
    public const string TicketObject = "ticket";
    public const string RequesterObject = "requester";

    public Task WriteAsync(string action, string objectType, Guid objectId, Guid departmentId, object? previous, object? next, CancellationToken ct) =>
        writer.WriteAsync(new AuditEntry(
            AuditCategories.Ticketing, action, objectType, objectId, departmentId, current.UserId, current.DisplayName, previous, next), db, ct);

    /// <summary>Change made by the system (ingestion): no actor, source <c>system</c>.</summary>
    public Task WriteSystemAsync(string action, string objectType, Guid objectId, Guid departmentId, object? previous, object? next, CancellationToken ct) =>
        writer.WriteAsync(new AuditEntry(
            AuditCategories.Ticketing, action, objectType, objectId, departmentId, null, null, previous, next, AuditSources.System), db, ct);
}

internal static class TicketAuditActions
{
    public const string Created = "ticket.created";
    public const string SubjectChanged = "ticket.subject_changed";
    public const string StatusChanged = "ticket.status_changed";
    public const string PriorityChanged = "ticket.priority_changed";
    public const string TeamChanged = "ticket.team_changed";
    public const string AssigneeChanged = "ticket.assignee_changed";
    public const string MessageAdded = "ticket.message_added";
    public const string MessageDeliveryChanged = "ticket.message_delivery_changed";
    public const string RequesterCreated = "requester.created";

    public const string ReasonRequesterReply = "requester_reply";
}

/// <summary>Per-department ticket numbers (TICKETING.md invariant 2).</summary>
internal static class TicketNumbering
{
    /// <summary>
    /// Allocates the next number inside the caller's open transaction. The upsert row-locks the department's counter until the
    /// creation transaction ends, serialising concurrent creators; a rollback rolls the number back (gap-free).
    /// </summary>
    public static async Task<int> AllocateAsync(TicketingDbContext db, Guid departmentId, CancellationToken ct)
    {
        var transaction = db.Database.CurrentTransaction ?? throw new InvalidOperationException("Number allocation requires the creation transaction.");
        var connection = db.Database.GetDbConnection();
        await using DbCommand command = connection.CreateCommand();
        command.Transaction = transaction.GetDbTransaction();
        command.CommandText = """
            INSERT INTO ticketing.department_counters (department_id, next_number) VALUES (@department_id, 2)
            ON CONFLICT (department_id) DO UPDATE SET next_number = ticketing.department_counters.next_number + 1
            RETURNING next_number - 1
            """;
        command.Parameters.Add(new NpgsqlParameter("department_id", departmentId));
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
    }
}

internal static partial class TicketValidation
{
    public const int MaxSubject = 200;
    public const int MaxBody = 20000;
    public const int MaxDisplayName = 200;
    public const int MaxEmail = 320;

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex EmailPattern();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9]{1,9}-[0-9]{1,9}$")]
    private static partial Regex ReferencePattern();

    /// <summary>PostgreSQL text cannot hold U+0000; reject it as a validation error instead of a server error.</summary>
    public static bool HasNul(string? value) => value is not null && value.Contains('\u0000', StringComparison.Ordinal);

    private static bool RejectNul(IDictionary<string, string[]> errors, string field, string? value)
    {
        if (!HasNul(value))
        {
            return false;
        }

        errors[field] = [$"{field} contains invalid characters."];
        return true;
    }

    public static bool IsReference(string value) => ReferencePattern().IsMatch(value);

    public static void Subject(IDictionary<string, string[]> errors, string field, string? value)
    {
        if (RejectNul(errors, field, value))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            errors[field] = [$"{field} is required."];
        }
        else if (value.Trim().Length > MaxSubject)
        {
            errors[field] = [$"{field} must be at most {MaxSubject} characters."];
        }
    }

    public static void Body(IDictionary<string, string[]> errors, string field, string? value)
    {
        if (RejectNul(errors, field, value))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            errors[field] = [$"{field} is required."];
        }
        else if (value.Length > MaxBody)
        {
            errors[field] = [$"{field} must be at most {MaxBody} characters."];
        }
    }

    public static void Email(IDictionary<string, string[]> errors, string field, string? value)
    {
        if (RejectNul(errors, field, value))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            errors[field] = [$"{field} is required."];
        }
        else if (value.Trim().Length > MaxEmail || !EmailPattern().IsMatch(value.Trim()))
        {
            errors[field] = [$"{field} must be a valid email address of at most {MaxEmail} characters."];
        }
    }

    public static void DisplayName(IDictionary<string, string[]> errors, string field, string? value)
    {
        if (RejectNul(errors, field, value))
        {
            return;
        }

        if (value is not null && value.Trim().Length > MaxDisplayName)
        {
            errors[field] = [$"{field} must be at most {MaxDisplayName} characters."];
        }
    }

    public static uint Version(IDictionary<string, string[]> errors, string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            errors["version"] = ["version is required."];
            return 0;
        }

        if (!ConcurrencyVersion.TryParse(version, out var parsed))
        {
            errors["version"] = ["version is not valid."];
        }

        return parsed;
    }

    /// <summary>Case-insensitive enum parse of a name (numbers and unknown names are rejected).</summary>
    public static TEnum? ParseEnum<TEnum>(IDictionary<string, string[]> errors, string field, string? value)
        where TEnum : struct, Enum
    {
        if (value is null)
        {
            return null;
        }

        if (long.TryParse(value, out _) || !Enum.TryParse<TEnum>(value.Trim(), ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
        {
            errors[field] = [$"{field} must be one of {string.Join(", ", Enum.GetNames<TEnum>())}."];
            return null;
        }

        return parsed;
    }

    public static void ThrowIfAny(Dictionary<string, string[]> errors)
    {
        if (errors.Count > 0)
        {
            throw new RequestValidationException(errors);
        }
    }

    /// <summary>Escapes LIKE wildcards (escape character is backslash).</summary>
    public static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);
}

internal static class ClockExtensions
{
    /// <summary>
    /// UTC now truncated to microseconds, the precision PostgreSQL stores, so a mutation response equals what a later read returns
    /// (and keyset cursors built from stored values compare equal).
    /// </summary>
    public static DateTimeOffset GetUtcNowMicroseconds(this TimeProvider clock)
    {
        var now = clock.GetUtcNow();
        return new DateTimeOffset(now.UtcTicks - (now.UtcTicks % 10), TimeSpan.Zero);
    }
}
