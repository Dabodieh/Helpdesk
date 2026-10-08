namespace Helpdesk.SharedKernel.Security;

/// <summary>Names of the per-user rate-limit policies (SEC-003). The limits themselves are configured by the host (<c>RateLimits</c> section); endpoints only name the policy.</summary>
public static class RateLimitPolicies
{
    /// <summary>Creating requests in the requester portal (each one can queue an acknowledgement email).</summary>
    public const string PortalCreate = "portal-create";

    /// <summary>Replying to a request in the portal (may carry attachments).</summary>
    public const string PortalReply = "portal-reply";

    /// <summary>Free-text search over tickets (counted only when a search term is present).</summary>
    public const string Search = "search";
}
