using System.ComponentModel.DataAnnotations;

namespace Helpdesk.Modules.Mail.Services;

/// <summary>Bound from the <c>Mail</c> configuration section. Secrets (client secret, certificate password) come from user-secrets or the environment.</summary>
internal sealed class MailOptions
{
    public const string Section = "Mail";

    /// <summary><c>Graph</c> (production) or <c>Fake</c> (Development/Testing only: an in-process mailbox simulator).</summary>
    public string Provider { get; set; } = "Graph";

    /// <summary>Public HTTPS base URL Microsoft Graph calls back (webhook + lifecycle). Required for Graph subscriptions.</summary>
    public string? NotificationBaseUrl { get; set; }

    /// <summary>Target subscription lifetime. Documented maximum for Outlook messages without resource data is just under 7 days.</summary>
    [Range(1, 6)]
    public int SubscriptionLifetimeDays { get; set; } = 3;

    /// <summary>Subscriptions expiring within this many hours are renewed by the maintenance job.</summary>
    [Range(1, 72)]
    public int RenewWithinHours { get; set; } = 36;

    /// <summary>Inbound messages from one sender to one mailbox accepted per window before further mail is ignored (rate limiting).</summary>
    [Range(1, 10000)]
    public int SenderLimitPerWindow { get; set; } = 30;

    [Range(1, 1440)]
    public int SenderWindowMinutes { get; set; } = 10;

    public GraphOptions Graph { get; set; } = new();
}

internal sealed class GraphOptions
{
    public string? TenantId { get; set; }

    public string? ClientId { get; set; }

    /// <summary>Client secret (dev/test tenants). Prefer a certificate in production.</summary>
    public string? ClientSecret { get; set; }

    /// <summary>Path to a PFX/PEM certificate file used instead of a secret.</summary>
    public string? CertificatePath { get; set; }

    public string? CertificatePassword { get; set; }

    public string BaseUrl { get; set; } = "https://graph.microsoft.com/v1.0";
}
