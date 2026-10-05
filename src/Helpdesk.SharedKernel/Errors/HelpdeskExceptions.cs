namespace Helpdesk.SharedKernel.Errors;

/// <summary>Base for errors that map to a specific HTTP problem response. The Host maps these in one place.</summary>
public abstract class HelpdeskException(int statusCode, string title, string? detail = null) : Exception(title)
{
    public int StatusCode { get; } = statusCode;

    public string Title { get; } = title;

    public string? Detail { get; } = detail;
}

/// <summary>Object missing, or the caller has no access to its department (deliberately indistinguishable).</summary>
public sealed class NotFoundException() : HelpdeskException(404, "Not found");

public sealed class ForbiddenException() : HelpdeskException(403, "Forbidden");

public sealed class ConflictException(string detail) : HelpdeskException(409, "Conflict", detail);

public sealed class RequestValidationException(IReadOnlyDictionary<string, string[]> errors) : HelpdeskException(400, "Validation failed")
{
    public IReadOnlyDictionary<string, string[]> Errors { get; } = errors;

    public RequestValidationException(string field, string message)
        : this(new Dictionary<string, string[]> { [field] = [message] })
    {
    }
}
