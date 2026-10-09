using System.Text.RegularExpressions;
using Helpdesk.Modules.Mail.Domain;
using Helpdesk.Modules.Mail.Persistence;
using Helpdesk.Modules.Mail.Providers;
using Helpdesk.Modules.Organisation.Contracts;
using Helpdesk.SharedKernel.Errors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Helpdesk.Modules.Mail.Services;

internal sealed record GraphConfigDto(bool Configured, string? TenantId, string? ClientId, string Credential, string BaseUrl);

/// <summary>Staff sign-in (Microsoft Entra ID / OIDC). Identifiers only: the client secret is reported as present or absent and never returned.</summary>
internal sealed record SignInConfigDto(bool EntraConfigured, string? TenantId, string? ClientId, bool SecretPresent, bool DevSignInEnabled, string RedirectPath);

/// <summary><see cref="Mode"/>: <c>webhook</c> (Graph change notifications to a public https URL, plus a reconcile every few minutes), <c>simulated</c> (built-in simulator).</summary>
internal sealed record NotificationsDto(string Mode, string? BaseUrl, bool PublicHttps, int ReconcileMinutes, int SubscriptionLifetimeDays);

internal sealed record RequiredPermissionDto(string Name, string Purpose, string Scope, bool Required);

internal sealed record Microsoft365MailboxDto(
    Guid Id,
    Guid DepartmentId,
    string DepartmentName,
    string Address,
    string DisplayName,
    bool IsActive,
    bool IsDefault,
    string Health,
    IReadOnlyList<string> Reasons,
    string SubscriptionState,
    DateTimeOffset? SubscriptionExpiresAt,
    DateTimeOffset? LastSyncAt,
    bool LastSyncFailed,
    int OutboundFailed,
    int OutboundUncertain);

/// <summary><see cref="Available"/> is false until a separate Exchange Online management identity is approved and configured; the guided manual path is used instead.</summary>
internal sealed record ProvisioningDto(bool Available, string Reason);

internal sealed record Microsoft365StatusDto(
    string Provider,
    bool Simulated,
    GraphConfigDto Graph,
    SignInConfigDto SignIn,
    NotificationsDto Notifications,
    IReadOnlyList<RequiredPermissionDto> RequiredPermissions,
    IReadOnlyList<Microsoft365MailboxDto> Mailboxes,
    IReadOnlyList<string> Issues,
    ProvisioningDto Provisioning,
    IReadOnlyList<string> ApprovedDomains);

internal sealed record ConnectionCheckDto(bool Ok, string Code, DateTimeOffset CheckedAt);

internal sealed record CheckMailboxRequest(string? Address);

/// <summary><see cref="Access"/>: <c>ok</c>, <c>denied</c> (does not exist or the application may not reach it), <c>unavailable</c> (could not be established), <c>skipped</c> (not checked because the address is invalid or already registered).</summary>
internal sealed record MailboxCheckDto(string Address, bool Valid, string? Problem, Guid? RegisteredDepartmentId, string? RegisteredDepartmentName, string Access);

internal sealed record NewMailboxPlanRequest(string? DisplayName, string? Alias, string? Domain, Guid? DepartmentId);

internal sealed record NewMailboxPlanDto(
    bool Valid,
    IReadOnlyDictionary<string, string> Problems,
    string? Address,
    string? DepartmentName,
    IReadOnlyList<string> Script,
    ProvisioningDto Provisioning);

/// <summary>
/// The platform-level Microsoft 365 page: what is configured (identifiers only, never secrets), what the application is allowed to do, how mail is received, and
/// the mailboxes across departments. Everything here is read-only against the tenant. The two checks send one read request each, on an administrator's click,
/// and change nothing; nothing in this service creates, changes or deletes anything in Microsoft 365. Permission (platform administrator) is enforced by the endpoints.
/// </summary>
internal sealed partial class Microsoft365Service(
    MailDbContext db,
    MailboxService mailboxes,
    IOrganisationDirectory organisation,
    IOptions<MailOptions> options,
    IConfiguration configuration,
    IMailProvider provider,
    MailAudit audit,
    TimeProvider clock)
{
    private const int ReconcileMinutes = 5; // the sync-all job, traffic or not (MailSync)

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex EmailPattern();

    [GeneratedRegex(@"^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?(\.[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)+$")]
    private static partial Regex DomainPattern();

    // Letters, digits and a few separators only: the value is shown inside a script an administrator may paste into PowerShell, so nothing that PowerShell treats as syntax is allowed.
    [GeneratedRegex(@"^[\p{L}\p{N}][\p{L}\p{N} .,&'()_-]{0,63}$")]
    private static partial Regex DisplayNamePattern();

    [GeneratedRegex(@"^[A-Za-z0-9]+([._-][A-Za-z0-9]+)*$")]
    private static partial Regex AliasPattern();

    private static readonly RequiredPermissionDto[] Permissions =
    [
        new("Application Mail.ReadWrite (Exchange RBAC for Applications)", "Read incoming mail, create reply drafts, change mail subscriptions and read changes.", "Helpdesk shared mailboxes only (management scope)", true),
        new("Application Mail.Send (Exchange RBAC for Applications)", "Send the reply drafts from the ticket's mailbox.", "Helpdesk shared mailboxes only (management scope)", true),
        new("Microsoft Entra: openid, profile, email", "Staff sign-in. Identifies the person; reads no mail and no directory data.", "Sign-in application", true),
        new("Tenant-wide Microsoft Graph Mail.* consent in Entra", "Not required. Granting it would remove the mailbox scoping above.", "Do not grant", false),
        new("Mail.ReadBasic.All, *.Shared, MailboxSettings.*, calendar and contact roles", "Not required.", "Do not grant", false),
    ];

    public async Task<Microsoft365StatusDto> GetStatusAsync(CancellationToken ct)
    {
        var settings = options.Value;
        var simulated = !string.Equals(provider.Kind, Contracts.MailProviderKinds.Graph, StringComparison.OrdinalIgnoreCase);
        var graph = settings.Graph;
        var credential = !string.IsNullOrWhiteSpace(graph.CertificatePath) ? "certificate" : !string.IsNullOrWhiteSpace(graph.ClientSecret) ? "secret" : "none";
        var graphDto = new GraphConfigDto(!simulated && credential != "none", simulated ? null : graph.TenantId, simulated ? null : graph.ClientId, credential, graph.BaseUrl);

        var entra = configuration.GetSection("Authentication:Entra");
        var entraConfigured = !string.IsNullOrWhiteSpace(entra["TenantId"]) || !string.IsNullOrWhiteSpace(entra["ClientId"]) || !string.IsNullOrWhiteSpace(entra["ClientSecret"]);
        var signIn = new SignInConfigDto(
            entraConfigured, entra["TenantId"], entra["ClientId"], !string.IsNullOrWhiteSpace(entra["ClientSecret"]),
            configuration.GetValue<bool>("Authentication:DevSignIn:Enabled"), "/api/auth/signin-oidc");

        var publicHttps = Uri.TryCreate(settings.NotificationBaseUrl, UriKind.Absolute, out var url) && url.Scheme == Uri.UriSchemeHttps;
        var notifications = new NotificationsDto(simulated ? "simulated" : "webhook", settings.NotificationBaseUrl, publicHttps, ReconcileMinutes, settings.SubscriptionLifetimeDays);

        var all = await mailboxes.ListAllAsync(ct);
        var departments = await organisation.GetDepartmentsAsync([.. all.Select(m => m.DepartmentId).Distinct()], ct);
        var list = all.Select(m => new Microsoft365MailboxDto(
            m.Id, m.DepartmentId, departments.TryGetValue(m.DepartmentId, out var d) ? d.Name : "Department", m.Address, m.DisplayName, m.IsActive, m.IsDefault,
            m.Health.Status, m.Health.Reasons, m.Subscription.State, m.Subscription.ExpiresAt, m.LastSyncAt, m.LastSyncError is not null, m.OutboundFailedCount, m.OutboundUncertainCount)).ToList();

        var issues = new List<string>();
        if (simulated)
        {
            issues.Add("simulated_provider");
        }

        if (!entraConfigured)
        {
            issues.Add("sign_in_not_configured");
        }

        if (signIn.DevSignInEnabled)
        {
            issues.Add("dev_sign_in_enabled");
        }

        if (!simulated && entraConfigured && !string.Equals(entra["TenantId"], graph.TenantId, StringComparison.OrdinalIgnoreCase))
        {
            issues.Add("tenant_differs");
        }

        if (list.Count == 0)
        {
            issues.Add("no_mailboxes");
        }

        if (list.Any(m => m.Health == "Failed"))
        {
            issues.Add("mailbox_failed");
        }
        else if (list.Any(m => m.Health is "Attention" or "NotConfigured"))
        {
            issues.Add("mailbox_attention");
        }

        return new Microsoft365StatusDto(
            settings.Provider, simulated, graphDto, signIn, notifications, Permissions, list, issues,
            new ProvisioningDto(false, "not_configured"), [.. settings.ApprovedDomains]);
    }

    public async Task<ConnectionCheckDto> CheckConnectionAsync(CancellationToken ct)
    {
        var result = await provider.CheckConnectionAsync(ct);
        await WriteAuditAsync(MailAuditActions.ConnectionChecked, new { code = result.Code }, ct);
        return new ConnectionCheckDto(result.Ok, result.Code, clock.GetUtcNow());
    }

    public async Task<MailboxCheckDto> CheckMailboxAsync(CheckMailboxRequest request, CancellationToken ct)
    {
        var address = request.Address?.Trim() ?? "";
        if (address.Length is 0 or > 320 || !EmailPattern().IsMatch(address) || address.Contains('\0', StringComparison.Ordinal))
        {
            return new MailboxCheckDto(address, false, "invalid_address", null, null, "skipped");
        }

        var normalised = Mailbox.Normalise(address);
        var existing = await db.Mailboxes.AsNoTracking().Where(m => m.AddressNormalised == normalised).Select(m => new { m.DepartmentId }).FirstOrDefaultAsync(ct);
        if (existing is not null)
        {
            var name = (await organisation.FindDepartmentAsync(existing.DepartmentId, ct))?.Name;
            await WriteAuditAsync(MailAuditActions.MailboxChecked, new { address, outcome = "already_registered" }, ct);
            return new MailboxCheckDto(address, true, "already_registered", existing.DepartmentId, name, "skipped");
        }

        var access = "ok";
        try
        {
            await provider.ProbeMailboxAsync(new MailboxRef(Guid.Empty, address, address), ct);
        }
        catch (PermanentProviderException)
        {
            access = "denied";
        }
        catch (TransientProviderException)
        {
            access = "unavailable";
        }

        await WriteAuditAsync(MailAuditActions.MailboxChecked, new { address, outcome = access }, ct);
        return new MailboxCheckDto(address, true, null, null, null, access);
    }

    /// <summary>
    /// Validates a new shared mailbox request and returns the Exchange Online PowerShell for a tenant administrator to run. Helpdesk never runs it: automatic creation needs a separate
    /// Exchange management identity that is not configured (<see cref="ProvisioningDto"/>). Nothing is stored or changed here.
    /// </summary>
    public async Task<NewMailboxPlanDto> PlanNewMailboxAsync(NewMailboxPlanRequest request, CancellationToken ct)
    {
        var problems = new Dictionary<string, string>();
        var displayName = request.DisplayName?.Trim() ?? "";
        var alias = request.Alias?.Trim() ?? "";
        var domain = request.Domain?.Trim().ToLowerInvariant() ?? "";

        if (!DisplayNamePattern().IsMatch(displayName))
        {
            problems["displayName"] = "Use 1 to 64 letters, digits, spaces and . , & ' ( ) _ - only.";
        }

        if (alias.Length is 0 or > 64 || !AliasPattern().IsMatch(alias))
        {
            problems["alias"] = "Use letters and digits, with single . _ or - between them (up to 64 characters).";
        }

        var approved = options.Value.ApprovedDomains;
        if (!DomainPattern().IsMatch(domain))
        {
            problems["domain"] = "Enter a domain such as example.org.";
        }
        else if (approved.Length > 0 && !approved.Contains(domain, StringComparer.OrdinalIgnoreCase))
        {
            problems["domain"] = "This domain is not on the approved list.";
        }

        string? departmentName = null;
        if (request.DepartmentId is not { } departmentId)
        {
            problems["departmentId"] = "Choose the destination department.";
        }
        else
        {
            var department = await organisation.FindDepartmentAsync(departmentId, ct);
            if (department is null || !department.IsActive)
            {
                problems["departmentId"] = "Choose an active department.";
            }
            else
            {
                departmentName = department.Name;
            }
        }

        string? address = null;
        if (!problems.ContainsKey("alias") && !problems.ContainsKey("domain"))
        {
            address = $"{alias.ToLowerInvariant()}@{domain}";
            if (await db.Mailboxes.AsNoTracking().AnyAsync(m => m.AddressNormalised == Mailbox.Normalise(address), ct))
            {
                problems["alias"] = "A mailbox with this address is already registered in Helpdesk.";
            }
        }

        var provisioning = new ProvisioningDto(false, "not_configured");
        if (problems.Count > 0 || address is null)
        {
            return new NewMailboxPlanDto(false, problems, address, departmentName, [], provisioning);
        }

        await WriteAuditAsync(MailAuditActions.NewMailboxPlanned, new { address }, ct);
        string[] script =
        [
            "# Run by a Microsoft 365 administrator in Exchange Online PowerShell. Helpdesk does not run this and has no access to do so.",
            "Connect-ExchangeOnline",
            $"New-Mailbox -Shared -Name \"{displayName}\" -DisplayName \"{displayName}\" -Alias \"{alias}\" -PrimarySmtpAddress \"{address}\"",
            "# Put the mailbox inside the Helpdesk application's Exchange scope. This line applies only if you scope by custom attribute, as in docs/guides/GRAPH_TENANT_VALIDATION.md.",
            $"Set-Mailbox -Identity \"{address}\" -CustomAttribute1 HelpdeskApp",
            "# Check access (replace the placeholder with the Helpdesk application's service principal object id). InScope must be True.",
            $"Test-ServicePrincipalAuthorization -Identity <ServicePrincipalObjectId> -Resource \"{address}\"",
        ];
        return new NewMailboxPlanDto(true, problems, address, departmentName, script, provisioning);
    }

    private async Task WriteAuditAsync(string action, object next, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await audit.WriteAsync(action, MailAudit.IntegrationObject, null, null, null, next, ct);
        await tx.CommitAsync(ct);
    }
}
