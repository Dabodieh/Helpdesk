using System.Text;

namespace Helpdesk.SharedKernel.Paging;

/// <summary>Opaque cursor helpers (url-safe base64 of a small payload string).</summary>
public static class Cursor
{
    public static string Encode(string payload) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(payload)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static bool TryDecode(string? cursor, out string payload)
    {
        payload = "";
        if (string.IsNullOrEmpty(cursor) || cursor.Length > 512)
        {
            return false;
        }

        var s = cursor.Replace('-', '+').Replace('_', '/');
        s = s.PadRight(s.Length + ((4 - (s.Length % 4)) % 4), '=');
        try
        {
            payload = Encoding.UTF8.GetString(Convert.FromBase64String(s));
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
