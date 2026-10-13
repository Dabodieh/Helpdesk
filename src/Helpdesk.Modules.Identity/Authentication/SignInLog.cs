using Microsoft.Extensions.Logging;

namespace Helpdesk.Modules.Identity.Authentication;

/// <summary>Diagnostic lines for sign-in failures. The reference is the request's trace identifier, also shown to the user, so a report can be matched to its log entry.</summary>
internal static partial class SignInLog
{
    public const string Category = "Helpdesk.Identity.SignIn";

    [LoggerMessage(EventId = 1, Level = LogLevel.Error, Message = "Microsoft sign-in could not be started (reference {Reference})")]
    public static partial void StartFailed(ILogger logger, Exception exception, string reference);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Microsoft sign-in did not complete ({Reason}, reference {Reference})")]
    public static partial void Incomplete(ILogger logger, Exception? exception, string reason, string reference);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "{Message}")]
    public static partial void KeyRingUnreadable(ILogger logger, string message);
}
