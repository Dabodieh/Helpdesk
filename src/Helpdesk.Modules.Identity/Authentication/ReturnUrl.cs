namespace Helpdesk.Modules.Identity.Authentication;

internal static class ReturnUrl
{
    /// <summary>Only local absolute paths are accepted (open-redirect protection); anything else becomes "/".</summary>
    public static string Sanitize(string? returnUrl)
    {
        if (string.IsNullOrEmpty(returnUrl) || returnUrl.Length > 2048 || returnUrl[0] != '/')
        {
            return "/";
        }

        if (returnUrl.Length > 1 && (returnUrl[1] == '/' || returnUrl[1] == '\\'))
        {
            return "/";
        }

        return returnUrl.Any(char.IsControl) || returnUrl.Contains('\\', StringComparison.Ordinal) ? "/" : returnUrl;
    }
}
