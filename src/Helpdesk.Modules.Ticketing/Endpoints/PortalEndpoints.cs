using Helpdesk.Modules.Ticketing.Portal;
using Helpdesk.SharedKernel.Authorization;
using Helpdesk.SharedKernel.Errors;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace Helpdesk.Modules.Ticketing.Endpoints;

/// <summary>
/// Requester portal routes (docs/api/PHASE6_API.md). Any signed-in employee may call them, so they declare "authenticated only" and the services do
/// the scoping: every ticket query is constrained to requesters linked to the caller, and a reference that is not theirs is a plain 404. Agent
/// permissions are neither required nor consulted.
/// </summary>
internal static class PortalEndpoints
{
    private sealed record ReplyJson(string? Body);

    public static void Map(IEndpointRouteBuilder app)
    {
        var portal = app.MapGroup("/api/portal").WithTags("Portal");

        portal.MapGet("forms", async (PortalFormsService forms, CancellationToken ct) => TypedResults.Ok(await forms.ListAsync(ct))).RequireAuthenticatedOnly();
        portal.MapGet("forms/{formId:guid}", async (Guid formId, PortalFormsService forms, CancellationToken ct) => TypedResults.Ok(await forms.GetAsync(formId, ct))).RequireAuthenticatedOnly();

        portal.MapGet("requests", async (string? filter, string? cursor, int? limit, PortalRequestService requests, CancellationToken ct) =>
                TypedResults.Ok(await requests.ListAsync(filter, cursor, limit, ct)))
            .RequireAuthenticatedOnly();
        portal.MapPost("requests", async (PortalCreateRequest body, PortalRequestService requests, CancellationToken ct) =>
            {
                var created = await requests.CreateAsync(body, ct);
                return TypedResults.Created($"/api/portal/requests/{created.Reference}", created);
            })
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
        g.MapPost("{formId:guid}/fields", async (Guid departmentId, Guid formId, FieldRequest body, FormsAdminService service, CancellationToken ct) =>
                TypedResults.Ok(await service.CreateFieldAsync(departmentId, formId, body, ct)))
            .RequirePermission(PermissionCodes.DepartmentFormsManage);
        g.MapPatch("{formId:guid}/fields/{fieldId:guid}", async (Guid departmentId, Guid formId, Guid fieldId, FieldRequest body, FormsAdminService service, CancellationToken ct) =>
                TypedResults.Ok(await service.UpdateFieldAsync(departmentId, formId, fieldId, body, ct)))
            .RequirePermission(PermissionCodes.DepartmentFormsManage);
        g.MapPut("{formId:guid}/fields/order", async (Guid departmentId, Guid formId, Sla.ReorderRequest body, FormsAdminService service, CancellationToken ct) =>
                TypedResults.Ok(await service.ReorderFieldsAsync(departmentId, formId, body, ct)))
            .RequirePermission(PermissionCodes.DepartmentFormsManage);
    }
}
