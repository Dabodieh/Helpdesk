using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Helpdesk.Modules.Audit.Contracts;
using Helpdesk.Modules.Identity.Authentication;
using Helpdesk.Modules.Identity.Domain;
using Helpdesk.Modules.Identity.Persistence;
using Helpdesk.SharedKernel.Errors;
using Helpdesk.SharedKernel.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Helpdesk.Modules.Identity.Services;

/// <summary>
/// Bound from <c>EntraSetup</c>. The defaults are Microsoft's public endpoints and Microsoft's own public sign-in application "Microsoft Graph Command Line Tools"
/// (the one Graph PowerShell uses), which an administrator signs in to once with a device code. Tests point the base addresses at a stub.
/// </summary>
internal sealed class EntraSetupOptions
{
    public const string Section = "EntraSetup";

    public string LoginBaseUrl { get; set; } = "https://login.microsoftonline.com";

    public string GraphBaseUrl { get; set; } = "https://graph.microsoft.com/v1.0";

    public string BootstrapClientId { get; set; } = "14d82eec-204b-4c2f-b7e8-296a70dab67e";

    public int SecretMonths { get; set; } = 12;

    /// <summary>Wait before the first follow-up call after creating an application: Entra needs a moment before a new application can be used.</summary>
    public int PropagationDelayMs { get; set; } = 2000;
}

internal sealed record EntraStatusDto(
    string Source,
    bool Configured,
    string? TenantId,
    string? ClientId,
    DateTimeOffset? SecretExpiresAt,
    bool SecretExpiresSoon,
    bool SecretUnreadable,
    string? ApplicationObjectId,
    IReadOnlyList<string> RedirectOrigins,
    bool DevSignInEnabled);

internal sealed record EntraSetupStartDto(string SessionId, string UserCode, string VerificationUri, int ExpiresInSeconds, int IntervalSeconds);

internal sealed record EntraSetupSessionRequest(string? SessionId);

/// <summary><see cref="State"/>: <c>pending</c>, <c>signed_in</c>, <c>expired</c>, <c>declined</c>, <c>failed</c>.</summary>
internal sealed record EntraSetupPollDto(string State, int IntervalSeconds, string? AdminName, string? AdminAccount, string? TenantId);

internal sealed record EntraSetupCreateRequest(string? SessionId, IReadOnlyList<string>? RedirectOrigins);

internal sealed record EntraSetupCreatedDto(string TenantId, string ClientId, DateTimeOffset SecretExpiresAt, IReadOnlyList<string> RedirectUris, string SignedInAs, string? PreviousApplicationObjectId);

/// <summary>
/// One administrator's in-progress setup. The Microsoft access token lives only here, in memory, for the life of the session: it is never stored, logged or returned.
/// </summary>
internal sealed class EntraSetupSession
{
    public string Id { get; init; } = "";

    public Guid UserId { get; init; }

    public string DeviceCode { get; init; } = "";

    public string UserCode { get; init; } = "";

    public string VerificationUri { get; init; } = "";

    public DateTimeOffset ExpiresAt { get; init; }

    public int IntervalSeconds { get; set; }

    public string State { get; set; } = "pending";

    public string? AccessToken { get; set; }

    public string? TenantId { get; set; }

    public string? AdminOid { get; set; }

    public string? AdminName { get; set; }

    public string? AdminAccount { get; set; }
}

internal sealed class EntraSetupSessions
{
    private readonly ConcurrentDictionary<string, EntraSetupSession> _sessions = new(StringComparer.Ordinal);

    public void Add(EntraSetupSession session)
    {
        Purge();
        foreach (var old in _sessions.Values.Where(s => s.UserId == session.UserId))
        {
            _sessions.TryRemove(old.Id, out _);
        }

        _sessions[session.Id] = session;
    }

    public EntraSetupSession? Find(string? id, Guid userId, TimeProvider clock)
    {
        Purge();
        return id is not null && _sessions.TryGetValue(id, out var s) && s.UserId == userId && s.ExpiresAt > clock.GetUtcNow() - TimeSpan.FromMinutes(30) ? s : null;
    }

    public void Remove(string id) => _sessions.TryRemove(id, out _);

    private void Purge()
    {
        var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromHours(2);
        foreach (var stale in _sessions.Values.Where(s => s.ExpiresAt < cutoff))
        {
            _sessions.TryRemove(stale.Id, out _);
        }
    }
}

/// <summary>
/// Connects Microsoft Entra sign-in without hand-editing server settings. A platform administrator signs in to Microsoft once with a device code (their own account, their own
/// consent: nothing is granted tenant-wide); Helpdesk then creates ONE single-tenant application registration for staff sign-in (openid, profile, email; no API permissions),
/// its service principal and a client secret, and stores the secret protected in the database. Nothing else in the tenant is read or changed, and no mail permission is touched.
/// </summary>
internal sealed class EntraSetupService(
    IdentityDbContext db,
    IAuditWriter audit,
    ICurrentUser current,
    EntraSettingsStore store,
    EntraSetupSessions sessions,
    IHttpClientFactory httpClients,
    IOptions<EntraSetupOptions> options,
    IOptions<HelpdeskAuthenticationOptions> authOptions,
    TimeProvider clock)
{
    public const string HttpClientName = "entra-setup";
    private const string GraphScope = "https://graph.microsoft.com/Application.ReadWrite.All";
    private const int MaxOrigins = 5;
    private static readonly TimeSpan SoonWindow = TimeSpan.FromDays(30);

    public EntraStatusDto GetStatus()
    {
        var c = store.Current;
        return new EntraStatusDto(
            c.Source, c.IsConfigured, c.IsConfigured ? c.TenantId : null, c.IsConfigured ? c.ClientId : null, c.SecretExpiresAt,
            c.SecretExpiresAt is { } e && e - clock.GetUtcNow() < SoonWindow, c.SecretUnreadable, c.ApplicationObjectId, c.RedirectOrigins, authOptions.Value.DevSignIn.Enabled);
    }

    public async Task<EntraSetupStartDto> StartAsync(CancellationToken ct)
    {
        EnsureNotServerConfigured();
        var o = options.Value;
        JsonNode json;
        try
        {
            using var response = await Http().PostAsync(
                $"{o.LoginBaseUrl.TrimEnd('/')}/organizations/oauth2/v2.0/devicecode",
                new FormUrlEncodedContent(new Dictionary<string, string> { ["client_id"] = o.BootstrapClientId, ["scope"] = $"openid profile {GraphScope}" }), ct);
            if (!response.IsSuccessStatusCode)
            {
                throw new ConflictException("Microsoft did not start the sign-in. Try again shortly.", ProblemCodes.InvalidState);
            }

            json = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct)) ?? throw new ConflictException("Microsoft sent an unexpected reply.", ProblemCodes.InvalidState);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            throw new ConflictException("Microsoft could not be reached. Try again shortly.", ProblemCodes.InvalidState);
        }

        var expiresIn = json["expires_in"]?.GetValue<int>() ?? 900;
        var session = new EntraSetupSession
        {
            Id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(),
            UserId = current.UserId,
            DeviceCode = json["device_code"]?.GetValue<string>() ?? throw new ConflictException("Microsoft sent an unexpected reply.", ProblemCodes.InvalidState),
            UserCode = json["user_code"]?.GetValue<string>() ?? "",
            VerificationUri = json["verification_uri"]?.GetValue<string>() ?? "https://microsoft.com/devicelogin",
            ExpiresAt = clock.GetUtcNow().AddSeconds(expiresIn),
            IntervalSeconds = Math.Max(1, json["interval"]?.GetValue<int>() ?? 5),
        };
        sessions.Add(session);
        return new EntraSetupStartDto(session.Id, session.UserCode, session.VerificationUri, expiresIn, session.IntervalSeconds);
    }

    public async Task<EntraSetupPollDto> PollAsync(string? sessionId, CancellationToken ct)
    {
        var session = sessions.Find(sessionId, current.UserId, clock) ?? throw new NotFoundException();
        if (session.State == "signed_in" || session.State == "creating")
        {
            return Poll(session);
        }

        if (clock.GetUtcNow() >= session.ExpiresAt)
        {
            session.State = "expired";
            return Poll(session);
        }

        var o = options.Value;
        JsonNode json;
        try
        {
            using var response = await Http().PostAsync(
                $"{o.LoginBaseUrl.TrimEnd('/')}/organizations/oauth2/v2.0/token",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                    ["client_id"] = o.BootstrapClientId,
                    ["device_code"] = session.DeviceCode,
                }), ct);
            json = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct)) ?? new JsonObject();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return Poll(session); // a missed poll is just retried by the page
        }

        if (json["access_token"]?.GetValue<string>() is { Length: > 0 } token)
        {
            ReadAdmin(session, token, json["id_token"]?.GetValue<string>());
            session.State = session.TenantId is null || session.AdminOid is null ? "failed" : "signed_in";
            session.AccessToken = session.State == "signed_in" ? token : null;
            return Poll(session);
        }

        switch (json["error"]?.GetValue<string>())
        {
            case "authorization_pending":
                break;
            case "slow_down":
                session.IntervalSeconds += 5;
                break;
            case "expired_token" or "code_expired":
                session.State = "expired";
                break;
            case "authorization_declined" or "access_denied":
                session.State = "declined";
                break;
            default:
                session.State = "failed";
                break;
        }

        return Poll(session);
    }

    public async Task<EntraSetupCreatedDto> CreateAsync(EntraSetupCreateRequest request, CancellationToken ct)
    {
        EnsureNotServerConfigured();
        var origins = ValidateOrigins(request.RedirectOrigins);
        var session = sessions.Find(request.SessionId, current.UserId, clock) ?? throw new NotFoundException();
        lock (session)
        {
            // One creation per sign-in: a double click or a second tab cannot create a second application.
            if (session.State != "signed_in" || session.AccessToken is null)
            {
                throw new ConflictException("Sign in to Microsoft first.", ProblemCodes.InvalidState);
            }

            session.State = "creating";
        }

        var token = session.AccessToken!;
        string? applicationObjectId = null;
        try
        {
            var redirectUris = origins.Select(origin => origin + EntraOpenIdConnectConfigurer.CallbackPath).ToList();
            var app = await GraphAsync(HttpMethod.Post, "applications", token, new JsonObject
            {
                ["displayName"] = "Helpdesk staff sign-in",
                ["signInAudience"] = "AzureADMyOrg",
                ["web"] = new JsonObject
                {
                    ["redirectUris"] = new JsonArray([.. redirectUris.Select(u => (JsonNode?)u)]),
                    ["implicitGrantSettings"] = new JsonObject { ["enableIdTokenIssuance"] = false, ["enableAccessTokenIssuance"] = false },
                },
            }, ct);
            applicationObjectId = app["id"]?.GetValue<string>() ?? throw new InvalidOperationException("no application id");
            var clientId = app["appId"]?.GetValue<string>() ?? throw new InvalidOperationException("no client id");

            // A newly created application is not usable for a moment (replication); a short wait and a few tries cover it.
            await Task.Delay(options.Value.PropagationDelayMs, ct);
            await WithRetriesAsync(() => GraphAsync(HttpMethod.Post, "servicePrincipals", token, new JsonObject { ["appId"] = clientId }, ct), ct);
            var expires = clock.GetUtcNow().AddMonths(options.Value.SecretMonths);
            var secretResponse = await WithRetriesAsync(() => GraphAsync(
                HttpMethod.Post, $"applications/{applicationObjectId}/addPassword", token,
                new JsonObject { ["passwordCredential"] = new JsonObject { ["displayName"] = "Helpdesk staff sign-in", ["endDateTime"] = expires.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) } }, ct), ct);
            var secret = secretResponse["secretText"]?.GetValue<string>() ?? throw new InvalidOperationException("no secret");

            var previous = await SaveAsync(session, clientId, secret, expires, applicationObjectId, origins, ct);
            sessions.Remove(session.Id);
            return new EntraSetupCreatedDto(session.TenantId!, clientId, expires, redirectUris, session.AdminAccount ?? session.AdminName ?? "", previous);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Remove only the application this attempt just made, so a retry starts clean. The sign-in is still valid, so the administrator can try again.
            if (applicationObjectId is not null)
            {
                try
                {
                    await GraphAsync(HttpMethod.Delete, $"applications/{applicationObjectId}", token, null, CancellationToken.None);
                }
                catch (Exception cleanup) when (cleanup is not OperationCanceledException)
                {
                    // Left in place; the administrator can remove "Helpdesk staff sign-in" in Entra.
                }
            }

            session.State = "signed_in";
            throw ex is EntraSetupGraphException g && g.Status is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized
                ? new ConflictException("The Microsoft account you signed in with is not allowed to create applications. It needs the Application Administrator or Global Administrator role.", ProblemCodes.InvalidState)
                : new ConflictException("Microsoft refused or could not complete the setup. Nothing was saved; you can try again.", ProblemCodes.InvalidState);
        }
    }

    /// <summary>Removes the stored sign-in settings (the application in the tenant is left alone). Sign-in then needs setup again, or server settings.</summary>
    public async Task DisconnectAsync(CancellationToken ct)
    {
        if (store.Current.Source == "server")
        {
            throw new ConflictException("Sign-in is configured in the server settings; remove it there.", ProblemCodes.InvalidState);
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var row = await db.EntraSettings.SingleOrDefaultAsync(ct) ?? throw new NotFoundException();
        db.EntraSettings.Remove(row);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(new AuditEntry(AuditCategories.Identity, "entra.disconnected", "entra_settings", null, null, current.UserId, current.DisplayName), db, ct);
        await tx.CommitAsync(ct);
        store.Reload();
    }

    private async Task<string?> SaveAsync(EntraSetupSession session, string clientId, string secret, DateTimeOffset expires, string applicationObjectId, IReadOnlyList<string> origins, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var row = await db.EntraSettings.SingleOrDefaultAsync(ct);
        var previous = row?.ApplicationObjectId;
        var isNew = row is null;
        row ??= new EntraSettings();
        row.TenantId = session.TenantId!;
        row.ClientId = clientId;
        row.ClientSecretProtected = store.Protect(secret);
        row.SecretExpiresAt = expires;
        row.ApplicationObjectId = applicationObjectId;
        row.RedirectOrigins = string.Join(' ', origins);
        row.BootstrapAdminSubject = session.AdminOid!;
        row.ConfiguredBy = current.UserId;
        row.ConfiguredAt = clock.GetUtcNow();
        if (isNew)
        {
            db.EntraSettings.Add(row);
        }

        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(
            new AuditEntry(AuditCategories.Identity, isNew ? "entra.connected" : "entra.reconnected", "entra_settings", null, null, current.UserId, current.DisplayName,
                Next: new { tenantId = row.TenantId, clientId, secretExpiresAt = expires, redirectOrigins = origins }), db, ct);
        await tx.CommitAsync(ct);
        store.Reload();
        return previous;
    }

    private static EntraSetupPollDto Poll(EntraSetupSession s) =>
        new(s.State == "creating" ? "signed_in" : s.State, s.IntervalSeconds, s.AdminName, s.AdminAccount, s.TenantId);

    private void EnsureNotServerConfigured()
    {
        if (store.Current.Source == "server")
        {
            throw new ConflictException("Sign-in is configured in the server settings, which take priority. Remove them there first.", ProblemCodes.InvalidState);
        }
    }

    /// <summary>Only the origin (scheme, host, port) of an address people really use: https, or http for localhost during development. No path, query or credentials.</summary>
    private static List<string> ValidateOrigins(IReadOnlyList<string>? origins)
    {
        if (origins is null || origins.Count == 0 || origins.Count > MaxOrigins)
        {
            throw new RequestValidationException("redirectOrigins", $"Give between 1 and {MaxOrigins} addresses.");
        }

        var result = new List<string>();
        foreach (var raw in origins)
        {
            if (!Uri.TryCreate(raw?.Trim(), UriKind.Absolute, out var uri)
                || uri.UserInfo.Length > 0 || uri.AbsolutePath != "/" || uri.Query.Length > 0 || uri.Fragment.Length > 0
                || !(uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && (uri.Host is "localhost" or "127.0.0.1"))))
            {
                throw new RequestValidationException("redirectOrigins", "Use the address people open (https://host, or http://localhost:port while developing), with no path.");
            }

            var origin = uri.GetLeftPart(UriPartial.Authority);
            if (!result.Contains(origin, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(origin);
            }
        }

        return result;
    }

    private HttpClient Http() => httpClients.CreateClient(HttpClientName);

    private async Task<JsonNode> GraphAsync(HttpMethod method, string path, string token, JsonNode? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, $"{options.Value.GraphBaseUrl.TrimEnd('/')}/{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
        {
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        }

        using var response = await Http().SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new EntraSetupGraphException(response.StatusCode);
        }

        var text = await response.Content.ReadAsStringAsync(ct);
        return string.IsNullOrWhiteSpace(text) ? new JsonObject() : JsonNode.Parse(text) ?? new JsonObject();
    }

    private async Task<JsonNode> WithRetriesAsync(Func<Task<JsonNode>> action, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await action();
            }
            catch (EntraSetupGraphException ex) when (attempt < 4 && ex.Status is HttpStatusCode.NotFound or HttpStatusCode.BadRequest or HttpStatusCode.TooManyRequests)
            {
                await Task.Delay(options.Value.PropagationDelayMs, ct);
            }
        }
    }

    /// <summary>Reads who signed in from the id token Microsoft returned over TLS (not trusted for anything else: it is only shown to the administrator and used as the bootstrap identity).</summary>
    private static void ReadAdmin(EntraSetupSession session, string accessToken, string? idToken)
    {
        var claims = Payload(idToken) ?? Payload(accessToken);
        if (claims is null)
        {
            return;
        }

        session.TenantId = claims["tid"]?.GetValue<string>()?.ToLowerInvariant();
        session.AdminOid = claims["oid"]?.GetValue<string>();
        session.AdminName = claims["name"]?.GetValue<string>();
        session.AdminAccount = claims["preferred_username"]?.GetValue<string>() ?? claims["upn"]?.GetValue<string>();
    }

    private static JsonNode? Payload(string? jwt)
    {
        var parts = jwt?.Split('.');
        if (parts is not { Length: 3 })
        {
            return null;
        }

        try
        {
            var padded = parts[1].Replace('-', '+').Replace('_', '/');
            padded = padded.PadRight(padded.Length + ((4 - padded.Length % 4) % 4), '=');
            return JsonNode.Parse(Convert.FromBase64String(padded));
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return null;
        }
    }
}

internal sealed class EntraSetupGraphException(HttpStatusCode status) : Exception("Graph request failed")
{
    public HttpStatusCode Status { get; } = status;
}
