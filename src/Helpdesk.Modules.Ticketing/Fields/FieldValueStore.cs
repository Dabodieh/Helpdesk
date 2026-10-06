using Helpdesk.Modules.Ticketing.Domain;
using Helpdesk.Modules.Ticketing.Persistence;
using Helpdesk.Modules.Ticketing.Services;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Modules.Ticketing.Fields;

internal sealed record FieldChange(TicketField Field, string? PreviousDisplay, string? NewDisplay);

/// <summary>Writes validated values onto a ticket (one row per ticket and key) and records what changed. Callers own the transaction and the ticket's updated-at.</summary>
internal static class FieldValueStore
{
    /// <summary>Inserts new values, rewrites changed ones (refreshing the snapshot of label, type, visibility and display text) and deletes cleared ones. Returns only real changes.</summary>
    public static async Task<List<FieldChange>> ApplyAsync(TicketingDbContext db, Ticket ticket, ValidationResult result, Guid? formId, DateTimeOffset now, CancellationToken ct)
    {
        var existing = await db.TicketFieldValues.IgnoreDepartmentScope().Where(v => v.TicketId == ticket.Id && v.DepartmentId == ticket.DepartmentId).ToListAsync(ct);
        var changes = new List<FieldChange>();
        foreach (var value in result.Values)
        {
            var row = existing.SingleOrDefault(v => v.Key == value.Field.Key);
            if (row is null)
            {
                db.TicketFieldValues.Add(new TicketFieldValue
                {
                    Id = Guid.CreateVersion7(),
                    TicketId = ticket.Id,
                    DepartmentId = ticket.DepartmentId,
                    FormId = formId,
                    FieldId = value.Field.Id,
                    Key = value.Field.Key,
                    Label = value.Field.Label,
                    FieldType = value.Field.FieldType,
                    ValueJson = value.ValueJson,
                    Display = value.Display,
                    RequesterVisible = value.Field.RequesterVisible,
                    AgentVisible = value.Field.AgentVisible,
                    SortOrder = value.Order,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
                changes.Add(new FieldChange(value.Field, null, value.Display));
                continue;
            }

            if (row.ValueJson == value.ValueJson)
            {
                continue; // same value: nothing to rewrite, nothing to audit
            }

            changes.Add(new FieldChange(value.Field, row.Display, value.Display));
            row.FieldId = value.Field.Id;
            row.Label = value.Field.Label;
            row.FieldType = value.Field.FieldType;
            row.ValueJson = value.ValueJson;
            row.Display = value.Display;
            row.RequesterVisible = value.Field.RequesterVisible;
            row.AgentVisible = value.Field.AgentVisible;
            row.SortOrder = value.Order;
            row.UpdatedAt = now;
        }

        foreach (var field in result.Cleared)
        {
            if (existing.SingleOrDefault(v => v.Key == field.Key) is { } row)
            {
                changes.Add(new FieldChange(field, row.Display, null));
                db.TicketFieldValues.Remove(row);
            }
        }

        return changes;
    }

    /// <summary>One audit entry per changed field. The values of a field agents cannot see are never written to the audit trail agents read.</summary>
    public static async Task AuditAsync(TicketingAudit audit, Ticket ticket, IEnumerable<FieldChange> changes, CancellationToken ct)
    {
        foreach (var c in changes)
        {
            var show = c.Field.AgentVisible;
            await audit.WriteAsync(TicketAuditActions.FieldChanged, TicketingAudit.TicketObject, ticket.Id, ticket.DepartmentId,
                new { key = c.Field.Key, label = c.Field.Label, display = show ? c.PreviousDisplay : null },
                new { key = c.Field.Key, label = c.Field.Label, display = show ? c.NewDisplay : null }, ct);
        }
    }
}
