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
    private static readonly string[] AllowedAnonymous =
    [
        "/health/live",
        "/health/ready",
        "/api/csrf",
        "/api/auth/login",
        "/api/auth/dev-login",
    ];

    private List<RouteEndpoint> Endpoints() =>
        app.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToList();

    private static string Describe(RouteEndpoint e) =>
        $"{string.Join(",", e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? ["*"])} {e.RoutePattern.RawText}";

    [Fact]
    public void There_are_endpoints_to_check()
    {
        var all = Endpoints();
        Assert.True(all.Count >= 27, $"Only {all.Count} endpoints found; discovery is broken.");
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
            .Where(e => e.Metadata.GetMetadata<PermissionMetadata>() is null && e.Metadata.GetMetadata<AuthenticatedOnlyMetadata>() is null)
            .Select(Describe).ToList();
        Assert.True(offenders.Count == 0, "Endpoints with neither a permission nor an authenticated-only marker: " + string.Join("; ", offenders));
    }

    [Fact]
    public void Authenticated_only_endpoints_are_the_reviewed_short_list()
    {
        var authenticatedOnly = Endpoints().Where(e => e.Metadata.GetMetadata<AuthenticatedOnlyMetadata>() is not null)
            .Select(Describe).Order().ToList();
        Assert.Equal(
            ["GET /api/departments", "GET /api/me", "GET /api/users/lookup", "POST /api/auth/logout"],
            authenticatedOnly);
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
            .Where(e => (e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? []).Any(m => m is not ("GET" or "HEAD" or "OPTIONS")))
            .Select(Describe).ToList();
        Assert.Empty(offenders);
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
