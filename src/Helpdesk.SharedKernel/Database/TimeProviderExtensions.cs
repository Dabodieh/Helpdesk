namespace Helpdesk.SharedKernel.Database;

public static class TimeProviderExtensions
{
    /// <summary>UTC now truncated to microseconds (PostgreSQL precision), so values written equal values read back.</summary>
    public static DateTimeOffset GetUtcNowMicroseconds(this TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        var now = clock.GetUtcNow();
        return new DateTimeOffset(now.UtcTicks - (now.UtcTicks % 10), TimeSpan.Zero);
    }
}
