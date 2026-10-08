using System.Text.Json;
using Helpdesk.Modules.Ticketing.Fields;
using Helpdesk.Modules.Ticketing.Portal;
using Helpdesk.SharedKernel.Authorization;
using Helpdesk.SharedKernel.Errors;
using Helpdesk.SharedKernel.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;

namespace Helpdesk.Modules.Ticketing.Endpoints;

/// <summary>
/// Requester portal routes (docs/api/PHASE6_API.md). Any signed-in employee may call them, so they declare "authenticated only" and the services do
/// the scoping: every ticket query is constrained to requesters linked to the caller, and a reference that is not theirs is a plain 404. Agent
/// permissions are neither required nor consulted.
/// </summary>
internal static class PortalEndpoints
{
    /// <summary>Largest portal reply request: 25 MiB attachment (the attachment limit) plus 4 MiB for the other parts.</summary>
    private const long MaxReplyRequestBytes = (25L + 4) * 1024 * 1024;

    private sealed record ReplyJson(string? Body);

    private sealed record FieldsJson(Dictionary<string, JsonElement>? Fields);

    public static void Map(IEndpointRouteBuilder app)
    {
        var portal = app.MapGroup("/api/portal").WithTags("Portal");

        portal.MapGet("forms", async (PortalFormsService forms, CancellationToken ct) => TypedResults.Ok(await forms.ListAsync(ct))).RequireAuthenticatedOnly();
        portal.MapGet("forms/{formId:guid}", async (Guid formId, PortalFormsService forms, CancellationToken ct) => TypedResults.Ok(await forms.GetAsync(formId, ct))).RequireAuthenticatedOnly();

        // Help topics: navigation to a request form. Present only while the form itself is usable by the caller; otherwise exactly like a missing object.
        portal.MapGet("help-topics", async (PortalHelpTopicsService topics, CancellationToken ct) => TypedResults.Ok(await topics.ListAsync(ct))).RequireAuthenticatedOnly();
        portal.MapGet("help-topics/{topicId:guid}", async (Guid topicId, PortalHelpTopicsService topics, CancellationToken ct) => TypedResults.Ok(await topics.GetAsync(topicId, ct))).RequireAuthenticatedOnly();

        portal.MapGet("requests", async (string? filter, string? cursor, int? limit, PortalRequestService requests, CancellationToken ct) =>
                TypedResults.Ok(await requests.ListAsync(filter, cursor, limit, ct)))
            .RequireAuthenticatedOnly();
        portal.MapPost("requests", async (PortalCreateRequest body, PortalRequestService requests, CancellationToken ct) =>
            {
                var created = await requests.CreateAsync(body, ct);
                return TypedResults.Created($"/api/portal/requests/{created.Reference}", created);
            })
            .RequireRateLimiting(RateLimitPolicies.PortalCreate)
            .RequireAuthenticatedOnly();
        portal.MapGet("requests/{reference}", async (string reference, PortalRequestService requests, CancellationToken ct) => TypedResults.Ok(await requests.GetAsync(reference, ct)))
            .RequireAuthenticatedOnly();

        // JSON ({ body }) or multipart (body + files). Files go through the one attachment pipeline: validation, object store, scan state.
        portal.MapPost("requests/{reference}/replies", async (string reference, HttpRequest http, PortalRequestService requests, CancellationToken ct) =>
            {
                string? body;
                IReadOnlyList<IFormFile> files = [];
                if (http.HasFormContentType)
                {
                    // Explicit and bounded (SEC-003): one maximal attachment plus the text, roughly what the server default allowed implicitly.
                    http.LimitRequestBody(MaxReplyRequestBytes);
                    var form = await http.ReadFormAsync(ct);
                    body = form["body"].ToString();
                    files = [.. form.Files];
                }
                else
                {
                    body = (await http.ReadFromJsonAsync<ReplyJson>(ct))?.Body;
                }

                return TypedResults.Created($"/api/portal/requests/{reference}", await requests.ReplyAsync(reference, body, files, ct));
            })
            .RequireRateLimiting(RateLimitPolicies.PortalReply)
            .RequireAuthenticatedOnly();

        portal.MapPut("requests/{reference}/fields", async (string reference, FieldsJson body, PortalRequestService requests, CancellationToken ct) =>
            {
                await requests.UpdateFieldsAsync(reference, body.Fields, ct);
                return TypedResults.Ok(await requests.GetAsync(reference, ct));
            })
            .RequireAuthenticatedOnly();

        portal.MapGet("requests/{reference}/attachments/{attachmentId:guid}", async (string reference, Guid attachmentId, PortalRequestService requests, CancellationToken ct) =>
            {
                var result = await requests.OpenAttachmentAsync(reference, attachmentId, ct);
                return result.Download is { } file
                    ? Results.File(file.Content, file.ContentType, file.FileName, enableRangeProcessing: false)
                    : throw new ConflictException($"The attachment cannot be downloaded ({result.BlockedReason}).", ProblemCodes.InvalidState);
            })
            .RequireAuthenticatedOnly();
    }

    public static void MapAdministration(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/departments/{departmentId:guid}/request-forms").WithTags("Request forms");

        g.MapGet("", async (Guid departmentId, FormsAdminService service, CancellationToken ct) => TypedResults.Ok(await service.ListAsync(departmentId, ct)))
            .RequirePermission(PermissionCodes.DepartmentFormsManage);
        g.MapPost("", async (Guid departmentId, FormRequest body, FormsAdminService service, CancellationToken ct) =>
            {
                var created = await service.CreateFormAsync(departmentId, body, ct);
                return TypedResults.Created($"/api/departments/{departmentId}/request-forms/{created.Id}", created);
            })
            .RequirePermission(PermissionCodes.DepartmentFormsManage);
        g.MapPatch("{formId:guid}", async (Guid departmentId, Guid formId, FormRequest body, FormsAdminService service, CancellationToken ct) =>
                TypedResults.Ok(await service.UpdateFormAsync(departmentId, formId, body, ct)))
            .RequirePermission(PermissionCodes.DepartmentFormsManage);
        g.MapPut("order", async (Guid departmentId, Sla.ReorderRequest body, FormsAdminService service, CancellationToken ct) =>
                TypedResults.Ok(await service.ReorderFormsAsync(departmentId, body, ct)))
            .RequirePermission(PermissionCodes.DepartmentFormsManage);
        g.MapPut("{formId:guid}/fields", async (Guid departmentId, Guid formId, FieldLinksRequest body, FormsAdminService service, CancellationToken ct) =>
                TypedResults.Ok(await service.SetFieldsAsync(departmentId, formId, body, ct)))
            .RequirePermission(PermissionCodes.DepartmentFormsManage);

        // Requester identity resolution: department-scoped, same authority as managing members (people). Platform administrators gain nothing extra:
        // every query is department-scope filtered, so without a membership the requesters and tickets simply do not exist.
        var identities = app.MapGroup("/api/departments/{departmentId:guid}/requester-identities").WithTags("Requester identities");
        identities.MapGet("", async (Guid departmentId, RequesterIdentityAdminService service, CancellationToken ct) => TypedResults.Ok(await service.ListAsync(departmentId, ct)))
            .RequirePermission(PermissionCodes.DepartmentMembersManage);
        identities.MapGet("{requesterId:guid}", async (Guid departmentId, Guid requesterId, RequesterIdentityAdminService service, CancellationToken ct) =>
                TypedResults.Ok(await service.GetAsync(departmentId, requesterId, ct)))
            .RequirePermission(PermissionCodes.DepartmentMembersManage);
        identities.MapPost("{requesterId:guid}/link", async (Guid departmentId, Guid requesterId, LinkRequesterRequest body, RequesterIdentityAdminService service, CancellationToken ct) =>
                TypedResults.Ok(await service.LinkAsync(departmentId, requesterId, body, ct)))
            .RequirePermission(PermissionCodes.DepartmentMembersManage);

        var topics = app.MapGroup("/api/departments/{departmentId:guid}/help-topics").WithTags("Help topics");
        topics.MapGet("", async (Guid departmentId, HelpTopicsAdminService service, CancellationToken ct) => TypedResults.Ok(await service.ListAsync(departmentId, ct)))
            .RequirePermission(PermissionCodes.DepartmentFormsManage);
        topics.MapPost("", async (Guid departmentId, HelpTopicRequest body, HelpTopicsAdminService service, CancellationToken ct) =>
            {
                var created = await service.CreateAsync(departmentId, body, ct);
                return TypedResults.Created($"/api/departments/{departmentId}/help-topics/{created.Id}", created);
            })
            .RequirePermission(PermissionCodes.DepartmentFormsManage);
        topics.MapPatch("{topicId:guid}", async (Guid departmentId, Guid topicId, HelpTopicRequest body, HelpTopicsAdminService service, CancellationToken ct) =>
                TypedResults.Ok(await service.UpdateAsync(departmentId, topicId, body, ct)))
            .RequirePermission(PermissionCodes.DepartmentFormsManage);
        topics.MapPut("order", async (Guid departmentId, Sla.ReorderRequest body, HelpTopicsAdminService service, CancellationToken ct) =>
                TypedResults.Ok(await service.ReorderAsync(departmentId, body, ct)))
            .RequirePermission(PermissionCodes.DepartmentFormsManage);

        // Ticket types and ticket fields share the forms permission: they are the same configuration area (Ticketing).
        var types = app.MapGroup("/api/departments/{departmentId:guid}/ticket-types").WithTags("Ticket types");
        types.MapGet("", async (Guid departmentId, TicketTypesService service, CancellationToken ct) => TypedResults.Ok(await service.ListAsync(departmentId, ct)))
            .RequirePermission(PermissionCodes.DepartmentFormsManage);
        types.MapPost("", async (Guid departmentId, TicketTypeRequest body, TicketTypesService service, CancellationToken ct) =>
            {
                var created = await service.CreateAsync(departmentId, body, ct);
                return TypedResults.Created($"/api/departments/{departmentId}/ticket-types/{created.Id}", created);
            })
            .RequirePermission(PermissionCodes.DepartmentFormsManage);
        types.MapPatch("{typeId:guid}", async (Guid departmentId, Guid typeId, TicketTypeRequest body, TicketTypesService service, CancellationToken ct) =>
                TypedResults.Ok(await service.UpdateAsync(departmentId, typeId, body, ct)))
            .RequirePermission(PermissionCodes.DepartmentFormsManage);
        types.MapPut("order", async (Guid departmentId, Sla.ReorderRequest body, TicketTypesService service, CancellationToken ct) =>
                TypedResults.Ok(await service.ReorderAsync(departmentId, body, ct)))
            .RequirePermission(PermissionCodes.DepartmentFormsManage);
        types.MapPut("{typeId:guid}/fields", async (Guid departmentId, Guid typeId, FieldLinksRequest body, TicketTypesService service, CancellationToken ct) =>
                TypedResults.Ok(await service.SetFieldsAsync(departmentId, typeId, body, ct)))
            .RequirePermission(PermissionCodes.DepartmentFormsManage);

        var fields = app.MapGroup("/api/departments/{departmentId:guid}/ticket-fields").WithTags("Ticket fields");
        fields.MapGet("", async (Guid departmentId, TicketFieldsService service, CancellationToken ct) => TypedResults.Ok(await service.ListAsync(departmentId, ct)))
            .RequirePermission(PermissionCodes.DepartmentFormsManage);
        fields.MapPost("", async (Guid departmentId, TicketFieldRequest body, TicketFieldsService service, CancellationToken ct) =>
            {
                var created = await service.CreateAsync(departmentId, body, ct);
                return TypedResults.Created($"/api/departments/{departmentId}/ticket-fields/{created.Id}", created);
            })
            .RequirePermission(PermissionCodes.DepartmentFormsManage);
        fields.MapPatch("{fieldId:guid}", async (Guid departmentId, Guid fieldId, TicketFieldRequest body, TicketFieldsService service, CancellationToken ct) =>
                TypedResults.Ok(await service.UpdateAsync(departmentId, fieldId, body, ct)))
            .RequirePermission(PermissionCodes.DepartmentFormsManage);
        fields.MapPut("order", async (Guid departmentId, Sla.ReorderRequest body, TicketFieldsService service, CancellationToken ct) =>
                TypedResults.Ok(await service.ReorderAsync(departmentId, body, ct)))
            .RequirePermission(PermissionCodes.DepartmentFormsManage);
    }
}
