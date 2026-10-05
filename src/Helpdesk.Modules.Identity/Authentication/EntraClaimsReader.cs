using System.Security.Claims;
using Helpdesk.Modules.Identity.Contracts;

namespace Helpdesk.Modules.Identity.Authentication;

internal static class EntraClaimsReader
{
    private const string LongOid = "http://schemas.microsoft.com/identity/claims/objectidentifier";
    private const string LongTid = "http://schemas.microsoft.com/identity/claims/tenantid";

    /// <summary>
    /// Extracts (tenant, oid) from a validated ID token principal. Rejects tokens whose <c>tid</c> is not the single configured
    /// tenant or whose <c>oid</c> is missing/not a GUID. Display name and email are attributes only.
    /// </summary>
    public static bool TryRead(ClaimsPrincipal principal, string expectedTenantId, out ExternalIdentityClaims? identity, out string? error)
    {
        identity = null;
        var tid = Find(principal, "tid", LongTid);
        if (!string.Equals(tid, expectedTenantId, StringComparison.OrdinalIgnoreCase))
        {
            error = "tenant_mismatch";
            return false;
        }

        var oid = Find(principal, "oid", LongOid);
        if (!Guid.TryParse(oid, out var oidGuid))
        {
            error = "missing_subject";
            return false;
        }

        var email = Find(principal, "email");
        var preferred = Find(principal, "preferred_username");
        email ??= preferred is not null && preferred.Contains('@', StringComparison.Ordinal) ? preferred : null;
        var name = Find(principal, "name") ?? preferred ?? email ?? oidGuid.ToString();

        identity = new ExternalIdentityClaims(ExternalIdentityProviders.Entra, tid!.ToLowerInvariant(), oidGuid.ToString(), name, email);
        error = null;
        return true;
    }

    private static string? Find(ClaimsPrincipal principal, params string[] types)
    {
        foreach (var type in types)
        {
            var value = principal.FindFirst(type)?.Value;
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }
}
