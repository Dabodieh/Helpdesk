namespace Helpdesk.SharedKernel.Errors;

/// <summary>Stable error codes clients may branch on instead of matching message text.</summary>
public static class ProblemCodes
{
    /// <summary>Optimistic-concurrency mismatch: the resource changed since the client read it.</summary>
    public const string VersionConflict = "version_conflict";

    public const string Conflict = "conflict";
    public const string Duplicate = "duplicate";
    public const string LastAdmin = "last_admin";
    public const string SelfDeactivation = "self_deactivation";
    public const string Inactive = "inactive";
    public const string InvalidState = "invalid_state";
    public const string CsrfInvalid = "csrf_invalid";
    public const string RateLimited = "rate_limited";
}

/// <summary>Base for errors that map to a specific HTTP problem response. The Host maps these in one place.</summary>
public abstract class HelpdeskException(int statusCode, string title, string? detail = null, string? code = null) : Exception(title)
{
    /// <summary>Stable machine-readable error code (<see cref="ProblemCodes"/>), sent as the `code` problem-details extension.</summary>
    public string? Code { get; } = code;

    public int StatusCode { get; } = statusCode;

    public string Title { get; } = title;

    public string? Detail { get; } = detail;
}

/// <summary>Object missing, or the caller has no access to its department (deliberately indistinguishable).</summary>
public sealed class NotFoundException() : HelpdeskException(404, "Not found");

public sealed class ForbiddenException() : HelpdeskException(403, "Forbidden");

public sealed class ConflictException(string detail, string code = ProblemCodes.Conflict) : HelpdeskException(409, "Conflict", detail, code);

public sealed class RequestValidationException(IReadOnlyDictionary<string, string[]> errors) : HelpdeskException(400, "Validation failed")
{
    public IReadOnlyDictionary<string, string[]> Errors { get; } = errors;

    public RequestValidationException(string field, string message)
        : this(new Dictionary<string, string[]> { [field] = [message] })
    {
    }
}
