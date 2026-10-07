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

internal static class TicketSources
{
    public const string Agent = "agent";
    public const string Email = "email";
    public const string Portal = "portal";
}

internal static class MessageSources
{
    public const string Portal = "portal";
    public const string Web = "web";
    public const string Manual = "manual";
    public const string Email = "email";
    public const string Automation = "automation";

    /// <summary>Sources a caller of <c>ITicketIngress</c> may name (email enters through the mail sink only).</summary>
    public static bool IsValid(string? source) => source is Web or Manual or Portal;
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

    /// <summary>
    /// The last activity a REQUESTER can see: the first message, public messages (either side) and status changes. Assignment, priority,
    /// team, internal notes and automation never move it, so portal ordering, timestamps and realtime reveal nothing private.
    /// </summary>
    public DateTimeOffset LastPublicActivityAt { get; set; }

    /// <summary>Where the ticket came from (<see cref="TicketSources"/>); the conversation is one and the same whatever the channel.</summary>
    public string Source { get; set; } = TicketSources.Agent;

    /// <summary>The request form the requester used (portal tickets only).</summary>
    public Guid? RequestFormId { get; set; }

    /// <summary>The ticket type (same department). Historical tickets keep a type that was later deactivated.</summary>
    public Guid? TicketTypeId { get; set; }

    public DateTimeOffset? ResolvedAt { get; set; }

    public DateTimeOffset? ClosedAt { get; set; }

    /// <summary>
    /// Set when this ticket was merged into a canonical ticket of the SAME department (composite foreign key). A merged ticket keeps its
    /// reference, subject, requester, status and conversation but is read-only and leaves queues; never chained (merging a canonical
    /// ticket repoints its sources). Not a status: Resolved/Closed semantics stay untouched.
    /// </summary>
    public Guid? MergedIntoTicketId { get; set; }

    public DateTimeOffset? MergedAt { get; set; }

    public Guid? MergedByUserId { get; set; }

    public bool IsMerged => MergedIntoTicketId is not null;

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
        LastPublicActivityAt = now; // a status change is visible to the requester
        return true;
    }

    /// <summary>A public message or other requester-visible event happened now.</summary>
    public void RecordPublicActivity(DateTimeOffset now)
    {
        UpdatedAt = now;
        LastPublicActivityAt = now;
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

/// <summary>
/// A personal saved ticket view: a validated, structured filter definition (never a query language). Executing it goes through the
/// normal list endpoint, so it can only ever return what the caller may see.
/// </summary>
internal sealed class SavedView
{
    public Guid Id { get; set; }

    public Guid OwnerUserId { get; set; }

    public string Name { get; set; } = "";

    /// <summary>Normalised JSON of <see cref="Services.ViewFilter"/>.</summary>
    public string Filter { get; set; } = "{}";

    public bool IsPinned { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public uint Version { get; set; }
}

internal static class CannedScopes
{
    public const string Personal = "Personal";
    public const string Department = "Department";
}

/// <summary>A reusable reply text. Personal: owned by one user (any department); Department: shared by the department's agents.</summary>
internal sealed class CannedResponse
{
    public Guid Id { get; set; }

    public string Scope { get; set; } = CannedScopes.Personal;

    public Guid? DepartmentId { get; set; }

    public Guid? OwnerUserId { get; set; }

    public string Title { get; set; } = "";

    /// <summary>Plain text, inserted into the composer (never sent automatically).</summary>
    public string Body { get; set; } = "";

    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public uint Version { get; set; }
}
/// <summary>
/// Explicit association of an authenticated application user with a department Requester. Created from the VERIFIED email of the signed-in
/// principal (never from client input) and then used by id, so a later change of display name or address does not change who owns a request.
/// A requester belongs to at most one user.
/// </summary>
internal sealed class RequesterLink
{
    public Guid Id { get; set; }

    public Guid RequesterId { get; set; }

    public Guid DepartmentId { get; set; }

    public Guid UserId { get; set; }

    /// <summary>The verified address that justified the link (audit trail; not used for access).</summary>
    public string LinkedEmailNormalised { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }
}


/// <summary>
/// Internal interest of an agent in a ticket (NOT an assignee, NOT a requester CC participant). Grants nothing: access always comes from the
/// user's own department membership. The row is tied to that membership by a foreign key that cascades, so it disappears with the membership.
/// </summary>
internal sealed class TicketFollower
{
    public Guid TicketId { get; set; }

    public Guid DepartmentId { get; set; }

    public Guid UserId { get; set; }

    public Guid? AddedByUserId { get; set; }

    public DateTimeOffset AddedAt { get; set; }
}

/// <summary>
/// A manually run bundle of structured ticket actions of one department (not an automation rule: it never reacts to events). Deactivate rather than delete.
/// </summary>
internal sealed class Macro
{
    public Guid Id { get; set; }

    public Guid DepartmentId { get; set; }

    public string Name { get; set; } = "";

    public string? Description { get; set; }

    public bool IsActive { get; set; } = true;

    public int SortOrder { get; set; }

    /// <summary>JSON array of <see cref="Services.MacroAction"/> (data only, no code).</summary>
    public string Actions { get; set; } = "[]";

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public uint Version { get; set; }
}

internal static class TicketLinkTypes
{
    /// <summary>Symmetric; stored once with the smaller id first.</summary>
    public const string Related = "Related";

    /// <summary>Source is a duplicate of target.</summary>
    public const string DuplicateOf = "DuplicateOf";

    /// <summary>Source is the parent of target.</summary>
    public const string ParentOf = "ParentOf";

    public static readonly string[] All = [Related, DuplicateOf, ParentOf];
}

/// <summary>
/// Informational relationship between two tickets of the same department (composite foreign keys). Both tickets stay independently active;
/// unlike a merge nothing about either ticket changes. At most one link exists per pair of tickets.
/// </summary>
internal sealed class TicketLink
{
    public Guid Id { get; set; }

    public Guid DepartmentId { get; set; }

    public Guid SourceTicketId { get; set; }

    public Guid TargetTicketId { get; set; }

    public string Type { get; set; } = TicketLinkTypes.Related;

    public Guid? CreatedByUserId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
