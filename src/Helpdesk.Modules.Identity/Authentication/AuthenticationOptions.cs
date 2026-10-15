using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Helpdesk.Modules.Identity.Authentication;

internal sealed class HelpdeskAuthenticationOptions
{
    public const string Section = "Authentication";

    public EntraOptions Entra { get; init; } = new();

    public DevSignInOptions DevSignIn { get; init; } = new();

    public SessionOptions Session { get; init; } = new();

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

/// <summary>
/// Session policy. A session ends after <see cref="IdleTimeoutMinutes"/> without use (sliding) and, whatever the activity, <see cref="AbsoluteLifetimeHours"/> after
/// sign-in: renewal can never extend the absolute limit. Both are checked on every request against the sign-in time stamped in the cookie.
/// </summary>
internal sealed class SessionOptions
{
    public const int MinAbsoluteHours = 1;
    public const int MaxAbsoluteHours = 168;

    /// <summary>Hard limit from sign-in, in hours (default 12).</summary>
    public int AbsoluteLifetimeHours { get; init; } = 12;

    /// <summary>Sliding idle timeout, in minutes (default 480 = 8 hours).</summary>
    public int IdleTimeoutMinutes { get; init; } = 480;
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
        // Nothing configured is allowed: Microsoft sign-in can be connected from Administration > Microsoft 365 (stored in the database), and until then the sign-in page says
        // setup is needed. Partial server settings above are still refused.

        if (options.Session.AbsoluteLifetimeHours is < SessionOptions.MinAbsoluteHours or > SessionOptions.MaxAbsoluteHours)
        {
            failures.Add($"Authentication:Session:AbsoluteLifetimeHours must be between {SessionOptions.MinAbsoluteHours} and {SessionOptions.MaxAbsoluteHours}.");
        }

        if (options.Session.IdleTimeoutMinutes is < 5 or > 1440)
        {
            failures.Add("Authentication:Session:IdleTimeoutMinutes must be between 5 and 1440.");
        }

        if (options.Session.IdleTimeoutMinutes > options.Session.AbsoluteLifetimeHours * 60)
        {
            failures.Add("Authentication:Session:IdleTimeoutMinutes must not exceed the absolute lifetime.");
        }

        if (options.BootstrapPlatformAdminSubjects.Any(string.IsNullOrWhiteSpace))
        {
            failures.Add("Authentication:BootstrapPlatformAdminSubjects must not contain empty values.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
