using Helpdesk.Modules.Ticketing.Services;
using Helpdesk.SharedKernel.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Helpdesk.Modules.Ticketing.Endpoints;

/// <summary>Routes of docs/api/PHASE2_API.md. Authorization declarations are explained in <see cref="TicketService"/>.</summary>
internal static class TicketEndpoints
{
    private const string Route = "ticket";

    public static void Map(IEndpointRouteBuilder app)
    {
        MapDepartmentScoped(app);
        MapQueues(app);
        MapViewsAndCanned(app);
        MapTicket(app);
        ServiceManagementEndpoints.Map(app);
        PortalEndpoints.Map(app);
        PortalEndpoints.MapAdministration(app);
    }

    private static void MapDepartmentScoped(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/departments/{departmentId:guid}/tickets",
                async (Guid departmentId, CreateTicketRequest body, TicketService tickets, CancellationToken ct) =>
                {
                    var created = await tickets.CreateAsync(departmentId, body, ct);
                    return TypedResults.Created($"/api/tickets/{created.Id}", created);
                })
            .RequirePermission(PermissionCodes.TicketsCreate)
            .WithTags("Tickets");

        app.MapGet("/api/departments/{departmentId:guid}/requesters/lookup",
                async (Guid departmentId, string? q, RequesterService requesters, CancellationToken ct) =>
                    TypedResults.Ok(await requesters.LookupAsync(departmentId, q, ct)))
            .RequirePermission(PermissionCodes.TicketsCreate)
            .WithTags("Tickets");
    }

    private static void MapQueues(IEndpointRouteBuilder app)
    {
        // Authorization is the department scope of the query itself (accessible departments for tickets.read).
        app.MapGet("/api/tickets", async ([AsParameters] TicketListQuery query, TicketQueryService queries, CancellationToken ct) =>
                TypedResults.Ok(await queries.ListAsync(query, ct)))
            .RequireAuthenticatedOnly()
            .WithTags("Tickets");

        app.MapGet("/api/tickets/queue-counts", async (TicketQueryService queries, CancellationToken ct) =>
                TypedResults.Ok(await queries.QueueCountsAsync(ct)))
            .RequireAuthenticatedOnly()
            .WithTags("Tickets");
    }

    private static void MapViewsAndCanned(IEndpointRouteBuilder app)
    {
        // Personal saved views: owner-only inside the service (404 for anybody else's id). Running one is the normal list with its filter.
        app.MapGet("/api/views", async (SavedViewService views, CancellationToken ct) => TypedResults.Ok(await views.ListAsync(ct)))
            .RequireAuthenticatedOnly().WithTags("Views");
        app.MapPost("/api/views", async (CreateViewRequest body, SavedViewService views, CancellationToken ct) =>
            {
                var created = await views.CreateAsync(body, ct);
                return TypedResults.Created($"/api/views/{created.Id}", created);
            })
            .RequireAuthenticatedOnly().WithTags("Views");
        app.MapPatch("/api/views/{viewId:guid}", async (Guid viewId, UpdateViewRequest body, SavedViewService views, CancellationToken ct) =>
                TypedResults.Ok(await views.UpdateAsync(viewId, body, ct)))
            .RequireAuthenticatedOnly().WithTags("Views");
        app.MapDelete("/api/views/{viewId:guid}", async (Guid viewId, SavedViewService views, CancellationToken ct) =>
            {
                await views.DeleteAsync(viewId, ct);
                return TypedResults.NoContent();
            })
            .RequireAuthenticatedOnly().WithTags("Views");

        // Canned responses: authorization (personal owner / department permission) is decided in the service per row.
        app.MapGet("/api/canned-responses", async (Guid? departmentId, bool? includeInactive, CannedResponseService canned, CancellationToken ct) =>
                TypedResults.Ok(await canned.ListAsync(departmentId, includeInactive ?? false, ct)))
            .RequireAuthenticatedOnly().WithTags("Canned responses");
        app.MapPost("/api/canned-responses", async (CreateCannedRequest body, CannedResponseService canned, CancellationToken ct) =>
            {
                var created = await canned.CreateAsync(body, ct);
                return TypedResults.Created($"/api/canned-responses/{created.Id}", created);
            })
            .RequireAuthenticatedOnly().WithTags("Canned responses");
        app.MapPatch("/api/canned-responses/{id:guid}", async (Guid id, UpdateCannedRequest body, CannedResponseService canned, CancellationToken ct) =>
                TypedResults.Ok(await canned.UpdateAsync(id, body, ct)))
            .RequireAuthenticatedOnly().WithTags("Canned responses");

        // Bulk change: every id is authorised and validated individually (see TicketService.BulkAsync).
        app.MapPost("/api/tickets/bulk", async (BulkRequest body, TicketService tickets, CancellationToken ct) => TypedResults.Ok(await tickets.BulkAsync(body, ct)))
            .RequireAuthenticatedOnly().WithTags("Tickets");
    }

    private static void MapTicket(IEndpointRouteBuilder app)
    {
        // Ticket-id routes: the filter resolves the ticket through the caller's department scope (404) and authorises the
        // permission below against the ticket's persisted department (403). The department is never read from the request.
        app.MapGet("/api/tickets/{ticket}", async (string ticket, TicketService tickets, CancellationToken ct) =>
                TypedResults.Ok(await tickets.GetAsync(ticket, ct)))
            .RequireResourcePermission<TicketResolver>(PermissionCodes.TicketsRead, Route)
            .WithTags("Tickets");

        app.MapPatch("/api/tickets/{ticket}", async (string ticket, UpdateTicketRequest body, TicketService tickets, CancellationToken ct) =>
                TypedResults.Ok(await tickets.UpdateAsync(ticket, body, ct)))
            .RequireResourcePermission<TicketResolver>(PermissionCodes.TicketsUpdate, Route)
            .WithTags("Tickets");

        // Baseline tickets.assign.self; the service requires tickets.assign.any unless the caller only takes or releases the ticket for themself.
        app.MapPut("/api/tickets/{ticket}/assignment", async (string ticket, AssignTicketRequest body, TicketService tickets, CancellationToken ct) =>
                TypedResults.Ok(await tickets.AssignAsync(ticket, body, ct)))
            .RequireResourcePermission<TicketResolver>(PermissionCodes.TicketsAssignSelf, Route)
            .WithTags("Tickets");

        app.MapGet("/api/tickets/{ticket}/messages",
                async (string ticket, string? cursor, int? limit, TicketService tickets, CancellationToken ct) =>
                    TypedResults.Ok(await tickets.ListMessagesAsync(ticket, cursor, limit, ct)))
            .RequireResourcePermission<TicketResolver>(PermissionCodes.TicketsRead, Route)
            .WithTags("Tickets");

        // Baseline tickets.read; the service requires tickets.reply (Public) or tickets.note (Internal), plus tickets.update for `status`.
        app.MapPost("/api/tickets/{ticket}/messages", async (string ticket, PostMessageRequest body, TicketService tickets, CancellationToken ct) =>
            {
                var posted = await tickets.PostMessageAsync(ticket, body, ct);
                return TypedResults.Created($"/api/tickets/{posted.Ticket.Id}/messages", posted);
            })
            .RequireResourcePermission<TicketResolver>(PermissionCodes.TicketsRead, Route)
            .WithTags("Tickets");

        // Baseline tickets.read; the service requires tickets.reply.
        app.MapPost("/api/tickets/{ticket}/messages/{messageId:guid}/retry-delivery",
                async (string ticket, Guid messageId, TicketService tickets, CancellationToken ct) =>
                    TypedResults.Ok(await tickets.RetryDeliveryAsync(ticket, messageId, ct)))
            .RequireResourcePermission<TicketResolver>(PermissionCodes.TicketsRead, Route)
            .WithTags("Tickets");

        // Always an attachment download (never rendered inline); blocked or not-yet-scanned items are refused.
        app.MapGet("/api/tickets/{ticket}/attachments/{attachmentId:guid}",
                async (string ticket, Guid attachmentId, TicketService tickets, CancellationToken ct) =>
                {
                    var result = await tickets.OpenAttachmentAsync(ticket, attachmentId, ct);
                    return result.Download is { } file
                        ? Results.File(file.Content, file.ContentType, file.FileName, enableRangeProcessing: false)
                        : throw new Helpdesk.SharedKernel.Errors.ConflictException(
                            $"The attachment cannot be downloaded ({result.BlockedReason}).", Helpdesk.SharedKernel.Errors.ProblemCodes.InvalidState);
                })
            .RequireResourcePermission<TicketResolver>(PermissionCodes.TicketsRead, Route)
            .WithTags("Tickets");

        app.MapGet("/api/tickets/{ticket}/activity",
                async (string ticket, string? cursor, int? limit, TicketService tickets, CancellationToken ct) =>
                    TypedResults.Ok(await tickets.ActivityAsync(ticket, cursor, limit, ct)))
            .RequireResourcePermission<TicketResolver>(PermissionCodes.TicketsRead, Route)
            .WithTags("Tickets");
    }
}
