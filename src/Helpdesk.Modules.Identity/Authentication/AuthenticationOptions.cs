using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Helpdesk.Modules.Identity.Authentication;

internal sealed class HelpdeskAuthenticationOptions
{
    public const string Section = "Authentication";

    public EntraOptions Entra { get; init; } = new();

    public DevSignInOptions DevSignIn { get; init; } = new();

    /// <summary>Entra object ids (oid) that become platform admins when first provisioned (or when no active platform admin exists). Audited.</summary>
    public string[] BootstrapPlatformAdminSubjects { get; init; } = [];
}

internal sealed class EntraOptions
{
    public string? TenantId { get; init; }

    public string? ClientId { get; init; }

    /// <summary>Client secret. Supply through user-secrets or environment variables only; never commit it.</summary>
    public string? ClientSecret { get; init; }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(TenantId) || !string.IsNullOrWhiteSpace(ClientId) || !string.IsNullOrWhiteSpace(ClientSecret);
}

internal sealed class DevSignInOptions
{
    public bool Enabled { get; init; }
}

internal sealed class AuthenticationOptionsValidator(IHostEnvironment environment) : IValidateOptions<HelpdeskAuthenticationOptions>
{
    public ValidateOptionsResult Validate(string? name, HelpdeskAuthenticationOptions options)
    {
        var failures = new List<string>();

        if (options.DevSignIn.Enabled && !(environment.IsDevelopment() || environment.IsEnvironment("Testing")))
        {
            failures.Add("Authentication:DevSignIn:Enabled is only allowed in the Development or Testing environment.");
        }

        if (options.Entra.IsConfigured)
        {
            if (!Guid.TryParse(options.Entra.TenantId, out _))
            {
                failures.Add("Authentication:Entra:TenantId must be the tenant GUID.");
            }

            if (!Guid.TryParse(options.Entra.ClientId, out _))
            {
                failures.Add("Authentication:Entra:ClientId must be the application (client) id GUID.");
            }

            if (string.IsNullOrWhiteSpace(options.Entra.ClientSecret))
            {
                failures.Add("Authentication:Entra:ClientSecret is required (use user-secrets or environment variables).");
            }
        }
        else if (!options.DevSignIn.Enabled)
        {
            failures.Add("Authentication:Entra must be configured unless Authentication:DevSignIn:Enabled is true (Development/Testing only).");
        }

        if (options.BootstrapPlatformAdminSubjects.Any(string.IsNullOrWhiteSpace))
        {
            failures.Add("Authentication:BootstrapPlatformAdminSubjects must not contain empty values.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
