using System.Text.Json.Nodes;

namespace Helpdesk.Modules.Ticketing.Services;

// Response shapes of docs/api/PHASE2_API.md. Enumerations are PascalCase strings.

internal sealed record RefDto(Guid Id, string DisplayName);

internal sealed record TeamRefDto(Guid Id, string Name);

internal sealed record RequesterRefDto(Guid Id, string DisplayName, string Email);

internal sealed record RequesterDetailDto(Guid Id, string DisplayName, string Email, int OpenTicketCount, int TotalTicketCount);

internal sealed record MatchDto(string Field, string? Snippet);

internal sealed record TicketListItemDto(
    Guid Id,
    string Reference,
    Guid DepartmentId,
    string DepartmentKey,
    string Subject,
    string Status,
    string Priority,
    RequesterRefDto Requester,
    TeamRefDto? Team,
    RefDto? Assignee,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    MatchDto? Match = null,
    Helpdesk.Modules.Ticketing.Sla.SlaSummaryDto? Sla = null,
    string? MergedInto = null);

internal sealed record MailboxRefDto(Guid Id, string Address, string Name);

internal sealed record ParticipantDto(string Email, string? DisplayName);

internal sealed record TicketDetailDto(
    Guid Id,
    string Reference,
    Guid DepartmentId,
    string DepartmentKey,
    string DepartmentName,
    string Subject,
    string Status,
    string Priority,
    RequesterDetailDto Requester,
    TeamRefDto? Team,
    RefDto? Assignee,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ResolvedAt,
    DateTimeOffset? ClosedAt,
    string Version,
    MailboxRefDto? Mailbox,
    IReadOnlyList<ParticipantDto> Participants,
    Helpdesk.Modules.Ticketing.Sla.SlaSummaryDto? Sla = null,
    string? Source = null,
    string? RequestForm = null,
    IReadOnlyList<AgentFieldDto>? Fields = null,
    RefDto? Type = null,
    MergedIntoDto? MergedInto = null,
    IReadOnlyList<MergedTicketDto>? MergedTickets = null);

/// <summary>Shown on a merged ticket: where it went, when and by whom.</summary>
internal sealed record MergedIntoDto(Guid Id, string Reference, string Subject, DateTimeOffset MergedAt, ActorDto? MergedBy);

/// <summary>Shown on a canonical ticket: a ticket that was merged into it.</summary>
internal sealed record MergedTicketDto(Guid Id, string Reference, string Subject, DateTimeOffset MergedAt, ActorDto? MergedBy);

/// <summary>A submitted custom-field value as agents see it (label and text as rendered at submission).</summary>
internal sealed record AgentFieldDto(string Label, string Value);

internal sealed record MessageAuthorDto(Guid? Id, string DisplayName);

internal sealed record MessageDto(
    Guid Id,
    Guid TicketId,
    string Kind,
    string AuthorType,
    MessageAuthorDto Author,
    string Body,
    string? BodyHtml,
    string Source,
    string DeliveryState,
    string? DeliveryError,
    IReadOnlyList<Helpdesk.Modules.Attachments.Contracts.AttachmentSummary> Attachments,
    DateTimeOffset CreatedAt,
    string? ImportedFrom = null);

internal sealed record PostedMessageDto(MessageDto Message, TicketDetailDto Ticket);

internal sealed record ActorDto(Guid Id, string? DisplayName);

internal sealed record ActivityDto(Guid Id, DateTimeOffset OccurredAt, ActorDto? Actor, string Action, JsonNode? Previous, JsonNode? Next, string? Reason);

internal sealed record PageDto<T>(IReadOnlyList<T> Items, string? NextCursor);

internal sealed record QueueCountsDto(int My, int Team, int Unassigned, int All, int SlaBreached = 0, int SlaApproaching = 0);

internal sealed record RequesterLookupDto(Guid Id, string DisplayName, string Email);

// Request shapes.

internal sealed record RequesterInput(string? Email, string? DisplayName);

internal sealed record CreateTicketRequest(
    string? Subject,
    string? Description,
    RequesterInput? Requester,
    Guid? RequesterId,
    string? Priority,
    Guid? TeamId,
    Guid? AssigneeUserId,
    Guid? TicketTypeId = null,
    Dictionary<string, System.Text.Json.JsonElement>? Fields = null);

internal sealed record UpdateTicketRequest(string? Version, string? Subject, string? Status, string? Priority);

internal sealed record AssignTicketRequest(string? Version, Guid? TeamId, Guid? AssigneeUserId);

internal sealed record PostMessageRequest(string? Kind, string? Body, string? Status);

internal sealed record IdChange(Guid? Id);

/// <summary>Bulk change: only the fields present are applied (Team/Assignee present with a null id means "clear").</summary>
internal sealed record BulkRequest(Guid[]? TicketIds, string? Status, string? Priority, IdChange? Team, IdChange? Assignee);

internal sealed record BulkItemDto(Guid TicketId, string? Reference, string Outcome, string? Code);

internal sealed record BulkResultDto(IReadOnlyList<BulkItemDto> Results, int Updated, int Unchanged, int Failed);

/// <summary>Query string of <c>GET /api/tickets</c> (bound by the endpoint).</summary>
internal sealed record TicketListQuery(
    string? Queue,
    Guid? DepartmentId,
    string[]? Status,
    string[]? Priority,
    Guid? TeamId,
    Guid? AssigneeUserId,
    bool? Unassigned,
    string? Q,
    string? Sort,
    string? Cursor,
    int? Limit,
    Guid? View = null,
    string? Sla = null,
    Guid? TypeId = null);
