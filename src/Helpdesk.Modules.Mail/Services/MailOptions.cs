using System.ComponentModel.DataAnnotations;

namespace Helpdesk.Modules.Mail.Services;

/// <summary>Bound from the <c>Mail</c> configuration section. Secrets (client secret, certificate password) come from user-secrets or the environment.</summary>
internal sealed class MailOptions
{
    public const string Section = "Mail";

    /// <summary><c>Graph</c> (production) or <c>Fake</c> (Development/Testing only: an in-process mailbox simulator).</summary>
    public string Provider { get; set; } = "Graph";

    /// <summary>
    /// How inbound mail is noticed. <c>Webhook</c> (default): Graph change-notification subscriptions nudge the 5-minute delta sync, which stays the source of truth.
    /// <c>Polling</c>: an intentional operating mode with NO subscriptions (none are created, renewed or reported as failed); the 5-minute delta sync alone finds new mail.
    /// Use it where Graph cannot reach the host (a private pilot); switch to <c>Webhook</c> when the host has an always-on public HTTPS endpoint.
    /// </summary>
    public string InboundMode { get; set; } = InboundModes.Webhook;

    /// <summary>True when subscriptions are deliberately not used (see <see cref="InboundMode"/>).</summary>
    public bool PollingOnly => string.Equals(InboundMode, InboundModes.Polling, StringComparison.OrdinalIgnoreCase);

    /// <summary>Public HTTPS base URL Microsoft Graph calls back (webhook + lifecycle). Required for Graph subscriptions (not in polling mode).</summary>
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

internal static class InboundModes
{
    public const string Webhook = "Webhook";
    public const string Polling = "Polling";

    public static bool IsValid(string? mode) =>
        string.Equals(mode, Webhook, StringComparison.OrdinalIgnoreCase) || string.Equals(mode, Polling, StringComparison.OrdinalIgnoreCase);
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
