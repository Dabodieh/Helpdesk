using System.Text.RegularExpressions;
using Helpdesk.SharedKernel.Database;
using Helpdesk.SharedKernel.Errors;

namespace Helpdesk.Modules.Organisation.Services;

internal static partial class Validation
{
    public const int MaxName = 100;
    public const int MaxDescription = 500;

    [GeneratedRegex("^[A-Z][A-Z0-9]{1,9}$")]
    private static partial Regex KeyPattern();

    public static bool IsValidKey(string? key) => key is not null && KeyPattern().IsMatch(key);

    public static void Name(IDictionary<string, string[]> errors, string field, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors[field] = [$"{field} is required."];
        }
        else if (value.Trim().Length > MaxName)
        {
            errors[field] = [$"{field} must be at most {MaxName} characters."];
        }
    }

    public static void Description(IDictionary<string, string[]> errors, string? value)
    {
        if (value is not null && value.Trim().Length > MaxDescription)
        {
            errors["description"] = [$"description must be at most {MaxDescription} characters."];
        }
    }

    public static uint Version(IDictionary<string, string[]> errors, string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            errors["version"] = ["version is required."];
            return 0;
        }

        if (!ConcurrencyVersion.TryParse(version, out var parsed))
        {
            errors["version"] = ["version is not valid."];
        }

        return parsed;
    }

    public static void ThrowIfAny(Dictionary<string, string[]> errors)
    {
        if (errors.Count > 0)
        {
            throw new RequestValidationException(errors);
        }
    }

    /// <summary>Description semantics: null = leave unchanged; empty/whitespace = clear.</summary>
    public static string? NormaliseDescription(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
