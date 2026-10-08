using Helpdesk.SharedKernel.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Helpdesk.ArchitectureTests;

/// <summary>
/// Every routed endpoint must declare its authorization explicitly: either an explicit AllowAnonymous (from a short reviewed
/// allow-list) or authorization metadata PLUS a permission declaration or an explicit authenticated-only marker.
/// </summary>
public sealed class EndpointAuthorizationTests(AppFixture app) : IClassFixture<AppFixture>
{
    private static readonly string[] ReviewedAuthenticatedOnly =
    [
        // Realtime hub (server decides the groups; no client-callable methods) and personal/aggregate endpoints whose authorization is
        // decided inside the service (owner-only views, per-row canned responses, per-ticket bulk checks).
        "* /hubs/tickets",
        "* /hubs/tickets/negotiate",
        "DELETE /api/views/{viewId:guid}",
        "GET /api/canned-responses",
        "GET /api/canned-responses/placeholders", // a constant list, no data
        "GET /api/departments",
        "GET /api/requesters/{requesterId:guid}",
        "GET /api/requesters/{requesterId:guid}/tickets",
        "GET /api/me",
        // Personal, owner-only endpoints (each row is the caller's own: notifications, queue preferences) and the portal help-topic catalogue, which applies the same
        // requester-audience rule as the forms it leads to (ADR-029).
        "GET /api/me/queue-preferences",
        "PUT /api/me/queue-preferences",
        "GET /api/notifications",
        "POST /api/notifications/{id:guid}/read",
        "POST /api/notifications/read-all",
        "GET /api/portal/help-topics",
        "GET /api/portal/help-topics/{topicId:guid}",
        "GET /api/tickets",
        "GET /api/tickets/queue-counts",
        "GET /api/users/lookup",
        "GET /api/views",
        "PATCH /api/canned-responses/{id:guid}",
        "PATCH /api/views/{viewId:guid}",
        "POST /api/auth/logout",
        "POST /api/canned-responses",
        "POST /api/tickets/bulk",
        "POST /api/views",

        // Phase 6 requester portal: any signed-in employee; the services constrain every query to the caller's own requesters and to published
        // knowledge in the caller's audience (docs/security/PHASE6_THREAT_MODEL.md).
        "GET /api/portal/forms",
        "GET /api/portal/forms/{formId:guid}",
        "GET /api/portal/knowledge/articles",
        "GET /api/portal/knowledge/articles/{slug}",
        "GET /api/portal/knowledge/categories",
        "GET /api/portal/requests",
        "GET /api/portal/requests/{reference}",
        "GET /api/portal/requests/{reference}/attachments/{attachmentId:guid}",
        "POST /api/portal/requests",
        "POST /api/portal/requests/{reference}/replies",
        "PUT /api/portal/knowledge/articles/{slug}/feedback",
        "PUT /api/portal/requests/{reference}/fields",
    ];

    private static readonly string[] AllowedAnonymous =
    [
        "/health/live",
        "/health/ready",
        "/api/csrf",
        "/api/auth/login",
        "/api/auth/dev-login",
        "/api/mail/graph/notifications",
        "/api/mail/graph/lifecycle",
    ];

    /// <summary>Webhooks called by Microsoft Graph: anonymous and CSRF-exempt, authenticated by the per-subscription client state (reviewed).</summary>
    private static readonly string[] CsrfExemptWebhooks = ["/api/mail/graph/notifications", "/api/mail/graph/lifecycle"];

    private List<RouteEndpoint> Endpoints() =>
        app.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToList();

    private static string Describe(RouteEndpoint e) =>
        $"{string.Join(",", e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? ["*"])} {e.RoutePattern.RawText}";

    [Fact]
    public void There_are_endpoints_to_check()
    {
        var all = Endpoints();
        Assert.True(all.Count >= 36, $"Only {all.Count} endpoints found; discovery is broken.");
    }

    [Fact]
    public void Every_endpoint_has_authorization_metadata_or_explicit_allow_anonymous()
    {
        var offenders = Endpoints()
            .Where(e => e.Metadata.GetMetadata<IAllowAnonymous>() is null && !e.Metadata.GetOrderedMetadata<IAuthorizeData>().Any())
            .Select(Describe).ToList();
        Assert.True(offenders.Count == 0, "Endpoints without authorization metadata: " + string.Join("; ", offenders));
    }

    [Fact]
    public void Anonymous_endpoints_are_exactly_the_reviewed_allow_list()
    {
        var anonymous = Endpoints().Where(e => e.Metadata.GetMetadata<IAllowAnonymous>() is not null)
            .Select(e => e.RoutePattern.RawText!).Distinct().Order().ToList();
        Assert.Equal(AllowedAnonymous.Order(), anonymous);
    }

    [Fact]
    public void Authenticated_endpoints_declare_a_permission_or_are_explicitly_authenticated_only()
    {
        var offenders = Endpoints()
            .Where(e => e.Metadata.GetMetadata<IAllowAnonymous>() is null)
            .Where(e => e.Metadata.GetMetadata<PermissionMetadata>() is null && e.Metadata.GetMetadata<ResourcePermissionMetadata>() is null
                && e.Metadata.GetMetadata<AuthenticatedOnlyMetadata>() is null)
            .Select(Describe).ToList();
        Assert.True(offenders.Count == 0, "Endpoints with neither a permission, a resource permission nor an authenticated-only marker: " + string.Join("; ", offenders));
    }

    [Fact]
    public void Authenticated_only_endpoints_are_the_reviewed_short_list()
    {
        var authenticatedOnly = Endpoints().Where(e => e.Metadata.GetMetadata<AuthenticatedOnlyMetadata>() is not null)
            .Select(Describe).Order().ToList();
        // The two ticket lists are scoped by the query itself (accessible departments), see TICKETING.md "Department scope".
        // Strict equality, reported as the two differences so a failure names the endpoint.
        var unreviewed = authenticatedOnly.Except(ReviewedAuthenticatedOnly).ToList();
        var vanished = ReviewedAuthenticatedOnly.Except(authenticatedOnly).ToList();
        Assert.True(unreviewed.Count == 0 && vanished.Count == 0, $"Not in the reviewed list: [{string.Join("; ", unreviewed)}]. In the list but no longer authenticated-only: [{string.Join("; ", vanished)}].");
    }

    [Fact]
    public void Resource_permissions_are_content_permissions_with_an_existing_route_parameter_and_a_resolver()
    {
        var resourceEndpoints = Endpoints().Where(e => e.Metadata.GetMetadata<ResourcePermissionMetadata>() is not null).ToList();
        Assert.NotEmpty(resourceEndpoints);
        foreach (var endpoint in resourceEndpoints)
        {
            var meta = endpoint.Metadata.GetMetadata<ResourcePermissionMetadata>()!;
            var definition = Helpdesk.Modules.Organisation.Authorization.PermissionCatalogue.Get(meta.Permission);
            Assert.True(
                definition.Kind == Helpdesk.Modules.Organisation.Authorization.PermissionKind.Content,
                $"{Describe(endpoint)}: resource permissions must be content permissions (never satisfied by the platform-admin flag)");
            Assert.Contains(endpoint.RoutePattern.Parameters, p => p.Name == meta.ResourceRouteParameter);
            Assert.True(typeof(IResourceDepartmentResolver).IsAssignableFrom(meta.ResolverType), $"{Describe(endpoint)}: resolver type");
            Assert.Null(endpoint.Metadata.GetMetadata<PermissionMetadata>()); // exactly one declaration style per endpoint
        }
    }

    [Fact]
    public void Ticket_id_routes_always_use_the_resource_permission_declaration()
    {
        var offenders = Endpoints()
            .Where(e => e.RoutePattern.Parameters.Any(p => p.Name == "ticket"))
            .Where(e => e.Metadata.GetMetadata<ResourcePermissionMetadata>() is null)
            .Select(Describe).ToList();
        Assert.Empty(offenders);
    }

    [Fact]
    public void Department_scoped_permissions_name_a_route_parameter_that_exists()
    {
        foreach (var endpoint in Endpoints())
        {
            var meta = endpoint.Metadata.GetMetadata<PermissionMetadata>();
            if (meta?.DepartmentRouteParameter is { } parameter)
            {
                Assert.Contains(endpoint.RoutePattern.Parameters, p => p.Name == parameter);
            }
        }
    }

    [Fact]
    public void Declared_permissions_exist_in_the_catalogue_and_platform_permissions_are_not_department_scoped()
    {
        foreach (var endpoint in Endpoints())
        {
            if (endpoint.Metadata.GetMetadata<PermissionMetadata>() is not { } meta)
            {
                continue;
            }

            var definition = Helpdesk.Modules.Organisation.Authorization.PermissionCatalogue.Get(meta.Permission);
            var isPlatform = definition.Kind == Helpdesk.Modules.Organisation.Authorization.PermissionKind.Platform;
            Assert.True(isPlatform == (meta.DepartmentRouteParameter is null), $"{Describe(endpoint)}: platform permissions take no department; department permissions require one");
        }
    }

    [Fact]
    public void Unsafe_methods_are_never_anonymous()
    {
        var offenders = Endpoints()
            .Where(e => e.Metadata.GetMetadata<IAllowAnonymous>() is not null)
            .Where(e => !CsrfExemptWebhooks.Contains(e.RoutePattern.RawText))
            .Where(e => (e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? []).Any(m => m is not ("GET" or "HEAD" or "OPTIONS")))
            .Select(Describe).ToList();
        Assert.Empty(offenders);
    }

    [Fact]
    public void Only_the_reviewed_webhooks_are_exempt_from_csrf()
    {
        var exempt = Endpoints().Where(e => e.Metadata.GetMetadata<IgnoreCsrfMetadata>() is not null).Select(e => e.RoutePattern.RawText!).Distinct().Order().ToList();
        Assert.Equal(CsrfExemptWebhooks.Order(), exempt);
        Assert.All(Endpoints().Where(e => e.Metadata.GetMetadata<IgnoreCsrfMetadata>() is not null), e => Assert.NotNull(e.Metadata.GetMetadata<IAllowAnonymous>()));
    }

    [Fact]
    public async Task Fallback_policy_requires_an_authenticated_user()
    {
        var provider = app.Services.GetRequiredService<IAuthorizationPolicyProvider>();
        var fallback = await provider.GetFallbackPolicyAsync();
        Assert.NotNull(fallback);
        Assert.Contains(fallback!.Requirements, r => r is Microsoft.AspNetCore.Authorization.Infrastructure.DenyAnonymousAuthorizationRequirement);
    }
}
