namespace Helpdesk.Modules.Ticketing.Services;

/// <summary>
/// Optional client-generated message ids (BE-003): a GUID the browser creates once per composed message and re-sends on every retry. It becomes the message's unique
/// <c>source_ref</c> (the mechanism portal creation and email already use), so a repeated post is recognised and answered with the first result.
/// </summary>
internal static class ClientMessageIds
{
    /// <summary>The stored source reference for a client message id, or null when none was sent. A malformed id is a validation error.</summary>
    public static string? SourceRef(Dictionary<string, string[]> errors, string? clientMessageId)
    {
        if (string.IsNullOrWhiteSpace(clientMessageId))
        {
            return null;
        }

        if (!Guid.TryParse(clientMessageId, out var id) || id == Guid.Empty)
        {
            errors["clientMessageId"] = ["clientMessageId must be a GUID."];
            return null;
        }

        return "client:" + id.ToString("N");
    }
}
