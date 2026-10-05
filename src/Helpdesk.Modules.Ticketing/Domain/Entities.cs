namespace Helpdesk.Modules.Ticketing.Domain;

internal enum TicketStatus
{
    Open,
    Pending,
    Resolved,
    Closed,
}

/// <summary>Stored as smallint (value = sort rank) so "Urgent first" is a plain index-friendly ordering.</summary>
internal enum TicketPriority : short
{
    Low = 1,
    Normal = 2,
    High = 3,
    Urgent = 4,
}

internal enum MessageKind
{
    Public,
    Internal,
}

internal enum AuthorType
{
    Agent,
    Requester,
    System,
}

/// <summary>
/// Delivery of an agent's PUBLIC reply. None: not applicable (notes, requester mail). NotDelivered: recorded but no mailbox could send it.
/// Pending/Sent/Failed: tracked through the mail outbox. Never "Sent" unless the provider accepted the message.
/// </summary>
internal enum DeliveryState
{
    None,
    NotDelivered,
    Pending,
    Sent,
    Failed,
}

internal static class MessageSources
{
    public const string Web = "web";
    public const string Manual = "manual";
    public const string Email = "email";

    /// <summary>Sources a caller of <c>ITicketIngress</c> may name (email enters through the mail sink only).</summary>
    public static bool IsValid(string? source) => source is Web or Manual;
}

/// <summary>A person who raises tickets within one department. Not a user; the same address in two departments is two requesters.</summary>
internal sealed class Requester
{
    public Guid Id { get; set; }

    public Guid DepartmentId { get; set; }

    /// <summary>Address as first entered (display only).</summary>
    public string Email { get; set; } = "";

    /// <summary>Trimmed, lower-cased address; the identity within the department.</summary>
    public string EmailNormalised { get; set; } = "";

    public string DisplayName { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }

    public static string Normalise(string email) => email.Trim().ToLowerInvariant();
}

/// <summary>Per-department number allocator row. Only touched by <see cref="Services.TicketNumbering"/> (raw SQL).</summary>
internal sealed class DepartmentCounter
{
    public Guid DepartmentId { get; set; }

    /// <summary>The next number to hand out.</summary>
    public int NextNumber { get; set; }
}

internal sealed class Ticket
{
    public Guid Id { get; set; }

    public Guid DepartmentId { get; set; }

    public int Number { get; set; }

    /// <summary>Department key + "-" + number. Immutable.</summary>
    public string Reference { get; set; } = "";

    public Guid RequesterId { get; set; }

    public Requester Requester { get; set; } = null!;

    public string Subject { get; set; } = "";

    public TicketStatus Status { get; set; }

    public TicketPriority Priority { get; set; }

    public Guid? TeamId { get; set; }

    public Guid? AssigneeUserId { get; set; }

    /// <summary>Null = created by the system (ingestion).</summary>
    public Guid? CreatedByUserId { get; set; }

    /// <summary>The mailbox the ticket arrived through (replies leave from it while it is active); null for tickets created in the web UI.</summary>
    public Guid? MailboxId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public DateTimeOffset? ResolvedAt { get; set; }

    public DateTimeOffset? ClosedAt { get; set; }

    /// <summary>PostgreSQL xmin; exposed to clients as the opaque <c>version</c> string.</summary>
    public uint Version { get; set; }

    /// <summary>
    /// Applies a status and its timestamps (TICKETING.md invariant 6): Resolved sets resolved_at; Closed sets closed_at (and
    /// resolved_at when empty); Open/Pending clear both. Returns false when the status is unchanged.
    /// </summary>
    public bool ApplyStatus(TicketStatus status, DateTimeOffset now)
    {
        if (status == Status)
        {
            return false;
        }

        switch (status)
        {
            case TicketStatus.Resolved:
                ResolvedAt = now;
                ClosedAt = null;
                break;
            case TicketStatus.Closed:
                ResolvedAt ??= now;
                ClosedAt = now;
                break;
            default:
                ResolvedAt = null;
                ClosedAt = null;
                break;
        }

        Status = status;
        return true;
    }
}

/// <summary>Append-only conversation entry. Never updated or deleted.</summary>
internal sealed class Message
{
    public Guid Id { get; set; }

    public Guid TicketId { get; set; }

    /// <summary>Denormalised from the ticket; a composite foreign key keeps it equal.</summary>
    public Guid DepartmentId { get; set; }

    public MessageKind Kind { get; set; }

    public AuthorType AuthorType { get; set; }

    public Guid? AuthorUserId { get; set; }

    public Guid? AuthorRequesterId { get; set; }

    /// <summary>Plain text. Never logged and never written to the audit stream.</summary>
    public string Body { get; set; } = "";

    public string Source { get; set; } = MessageSources.Web;

    public DeliveryState DeliveryState { get; set; }

    /// <summary>Short machine code of a failed delivery (never provider text or content).</summary>
    public string? DeliveryError { get; set; }

    /// <summary>Idempotency key of the producer (the mail message id for email); unique per department and source.</summary>
    public string? SourceRef { get; set; }

    /// <summary>Sanitised HTML of an email body (allow-list, no scripts or remote content); null for plain-text messages. The original stays in Mail.</summary>
    public string? BodyHtml { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>A person copied on the ticket (CC from email). Replies are copied to them. Department-scoped like the ticket.</summary>
internal sealed class TicketParticipant
{
    public Guid TicketId { get; set; }

    public Guid DepartmentId { get; set; }

    public string EmailNormalised { get; set; } = "";

    public string Email { get; set; } = "";

    public string? DisplayName { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
