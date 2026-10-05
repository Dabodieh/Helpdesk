using System.Security.Claims;
using Helpdesk.Modules.Identity.Contracts;

namespace Helpdesk.Modules.Identity.Authentication;

internal static class AuthSchemes
{
    public const string Cookie = "helpdesk";
    public const string Entra = "Entra";
}

/// <summary>The one cookie principal shape, issued identically for Entra and development sign-in.</summary>
internal static class HelpdeskPrincipal
{
    public const string UserIdClaim = "helpdesk:uid";
    public const string ProviderClaim = "helpdesk:idp";

    public static ClaimsPrincipal Create(ProvisionedUser user, string provider)
    {
        var claims = new List<Claim>
        {
            new(UserIdClaim, user.UserId.ToString()),
            new(ClaimTypes.NameIdentifier, user.UserId.ToString()),
            new(ClaimTypes.Name, user.DisplayName),
            new(ProviderClaim, provider),
        };
        if (!string.IsNullOrEmpty(user.Email))
        {
            claims.Add(new Claim(ClaimTypes.Email, user.Email));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, AuthSchemes.Cookie, ClaimTypes.Name, ClaimTypes.Role));
    }

    public static bool TryGetUserId(ClaimsPrincipal? principal, out Guid userId)
    {
        userId = default;
        return principal?.FindFirst(UserIdClaim)?.Value is { } raw && Guid.TryParse(raw, out userId);
    }
}
