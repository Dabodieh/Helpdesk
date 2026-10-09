using System.Security.Cryptography;
using Helpdesk.Modules.Identity.Contracts;
using Helpdesk.Modules.Identity.Persistence;
using Helpdesk.Modules.Identity.Services;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Helpdesk.Modules.Identity.Authentication;

/// <summary>
/// The Microsoft Entra sign-in settings in force. <see cref="Source"/> is <c>server</c> (server settings / environment: these always win) or <c>database</c> (written by the setup
/// wizard) or <c>none</c>. <see cref="SecretUnreadable"/> means a stored secret exists but cannot be decrypted (the Data Protection keys were lost): sign-in stays off until reconnected.
/// </summary>
internal sealed record EntraResolved(
    string Source,
    string TenantId,
    string ClientId,
    string ClientSecret,
    IReadOnlyList<string> RedirectOrigins,
    string BootstrapAdminSubject,
    DateTimeOffset? SecretExpiresAt,
    string? ApplicationObjectId,
    bool SecretUnreadable)
{
    public static readonly EntraResolved None = new("none", "", "", "", [], "", null, null, false);

    public bool IsConfigured => Source != "none" && TenantId.Length > 0 && ClientId.Length > 0 && ClientSecret.Length > 0;
}

/// <summary>
/// Holds the sign-in settings in memory (server settings first, otherwise the database row) so the OpenID Connect handler can use them without a restart. Saving or removing the
/// database row calls <see cref="Reload"/>, which also drops the handler's cached options. Single instance: another running copy would pick the change up on restart.
/// </summary>
internal sealed class EntraSettingsStore(
    IOptions<HelpdeskAuthenticationOptions> options,
    IServiceScopeFactory scopes,
    IDataProtectionProvider protection,
    IOptionsMonitorCache<OpenIdConnectOptions> handlerOptions)
{
    private const string Purpose = "Helpdesk.Identity.EntraClientSecret";
    private readonly object _gate = new();
    private EntraResolved? _current;

    public EntraResolved Current
    {
        get
        {
            var current = _current;
            if (current is not null)
            {
                return current;
            }

            lock (_gate)
            {
                return _current ??= Load();
            }
        }
    }

    public string Protect(string secret) => protection.CreateProtector(Purpose).Protect(secret);

    public void Reload()
    {
        lock (_gate)
        {
            _current = Load();
            handlerOptions.Clear();
        }
    }

    private EntraResolved Load()
    {
        var server = options.Value.Entra;
        if (server.IsConfigured)
        {
            return new EntraResolved("server", server.TenantId ?? "", server.ClientId ?? "", server.ClientSecret ?? "", [], "", null, null, false);
        }

        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
            var row = db.EntraSettings.AsNoTracking().SingleOrDefault();
            if (row is null)
            {
                return EntraResolved.None;
            }

            string secret;
            try
            {
                secret = protection.CreateProtector(Purpose).Unprotect(row.ClientSecretProtected);
            }
            catch (CryptographicException)
            {
                return EntraResolved.None with { SecretUnreadable = true };
            }

            return new EntraResolved(
                "database", row.TenantId, row.ClientId, secret, [.. row.RedirectOrigins.Split(' ', StringSplitOptions.RemoveEmptyEntries)],
                row.BootstrapAdminSubject, row.SecretExpiresAt, row.ApplicationObjectId, false);
        }
        catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException or Npgsql.NpgsqlException)
        {
            // The table is not there yet (first start before migration): behave as "not configured"; the next change reloads.
            return EntraResolved.None;
        }
    }
}

/// <summary>Configures the Entra OpenID Connect scheme from <see cref="EntraSettingsStore"/> each time its options are built (so a saved change takes effect without a restart).</summary>
internal sealed class EntraOpenIdConnectConfigurer(EntraSettingsStore store) : IConfigureNamedOptions<OpenIdConnectOptions>
{
    public const string CallbackPath = "/api/auth/signin-oidc";

    public void Configure(OpenIdConnectOptions options) => Configure(Options.DefaultName, options);

    public void Configure(string? name, OpenIdConnectOptions o)
    {
        if (name != AuthSchemes.Entra)
        {
            return;
        }

        var entra = store.Current;
        o.SignInScheme = AuthSchemes.Cookie;
        o.ResponseType = "code";
        o.ResponseMode = "query";
        o.UsePkce = true;
        o.SaveTokens = false;
        o.GetClaimsFromUserInfoEndpoint = false;
        o.MapInboundClaims = false;
        o.Scope.Clear();
        o.Scope.Add("openid");
        o.Scope.Add("profile");
        o.Scope.Add("email");

        if (!entra.IsConfigured)
        {
            // The handler is always registered (it inspects every request for its callback path), so it needs valid-looking options. These never reach Microsoft:
            // /api/auth/login refuses to challenge, and the callback paths below are not the ones Microsoft is ever given.
            o.Authority = "https://login.microsoftonline.com/common/v2.0";
            o.ClientId = "entra-not-configured";
            o.ClientSecret = "entra-not-configured";
            o.CallbackPath = CallbackPath + "-unconfigured";
            o.SignedOutCallbackPath = "/api/auth/signout-callback-oidc-unconfigured";
            return;
        }

        var authority = $"https://login.microsoftonline.com/{entra.TenantId}/v2.0";
        o.Authority = authority;
        o.ClientId = entra.ClientId;
        o.ClientSecret = entra.ClientSecret;
        o.CallbackPath = CallbackPath;
        o.SignedOutCallbackPath = "/api/auth/signout-callback-oidc";
        // State and nonce protection stay at their (enabled) defaults. Single tenant: issuer is pinned, tid is checked below.
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = authority,
            ValidateAudience = true,
            NameClaimType = "name",
        };
        o.Events = new OpenIdConnectEvents
        {
            // Behind a proxy or tunnel the request may arrive as plain http. The redirect address is taken from the origins registered with Entra (never from a header):
            // the one whose host is the host this request was made to.
            OnRedirectToIdentityProvider = ctx =>
            {
                var host = ctx.Request.Host.Value;
                var origin = store.Current.RedirectOrigins.FirstOrDefault(r => Uri.TryCreate(r, UriKind.Absolute, out var u) && string.Equals(HostWithPort(u), host, StringComparison.OrdinalIgnoreCase));
                if (origin is not null)
                {
                    ctx.ProtocolMessage.RedirectUri = origin.TrimEnd('/') + CallbackPath;
                }

                return Task.CompletedTask;
            },
            OnTokenValidated = async ctx =>
            {
                var tenant = store.Current.TenantId;
                if (ctx.Principal is null
                    || !EntraClaimsReader.TryRead(ctx.Principal, tenant, out var identity, out _))
                {
                    ctx.Fail("denied");
                    return;
                }

                var provisioner = ctx.HttpContext.RequestServices.GetRequiredService<IUserProvisioner>();
                var user = await provisioner.ProvisionAsync(identity!, ctx.HttpContext.RequestAborted);
                if (!user.IsActive)
                {
                    ctx.Fail("inactive");
                    return;
                }

                ctx.Principal = HelpdeskPrincipal.Create(user, identity!.Provider);
            },
            OnRemoteFailure = ctx =>
            {
                // Never surface exception details to the browser.
                var reason = ctx.Failure?.Message == "inactive" ? "inactive" : "failed";
                ctx.Response.Redirect($"/?authError={reason}");
                ctx.HandleResponse();
                return Task.CompletedTask;
            },
        };
    }

    private static string HostWithPort(Uri u) => u.IsDefaultPort ? u.Host : $"{u.Host}:{u.Port}";
}
