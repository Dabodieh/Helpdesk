using System.Globalization;

namespace Helpdesk.SharedKernel.Database;

/// <summary>Opaque optimistic-concurrency token exposed to API clients; wraps PostgreSQL <c>xmin</c>.</summary>
public static class ConcurrencyVersion
{
    public static string ToToken(uint xmin) => xmin.ToString(CultureInfo.InvariantCulture);

    public static bool TryParse(string? token, out uint xmin) =>
        uint.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out xmin);
}
