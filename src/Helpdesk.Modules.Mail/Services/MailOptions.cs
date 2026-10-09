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

    /// <summary>
    /// Automatic acknowledgements one mailbox sends per window (MSG-005, D11). A forged or rotating From address cannot make the mailbox send more than this; beyond it the
    /// ticket is still created and the missing acknowledgement is audited. Provisional starting values: tune to the real inbound volume.
    /// </summary>
    [Range(1, 100000)]
    public int AcknowledgementLimitPerWindow { get; set; } = 60;

    [Range(1, 1440)]
    public int AcknowledgementWindowMinutes { get; set; } = 10;

    /// <summary>
    /// Email domains a NEW shared mailbox may be planned under (guided creation only). Empty means the domain is not restricted by Helpdesk; the tenant's own accepted domains still apply.
    /// Registering an existing mailbox is never restricted by this list: the application's Exchange scope decides what it can reach.
    /// </summary>
    public string[] ApprovedDomains { get; set; } = [];

    /// <summary>Address of the dedicated mailbox that sends mail belonging to no ticket (account invitations). Empty: such mail is not sent. Never a department mailbox.</summary>
    public string? SystemMailbox { get; set; }

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
