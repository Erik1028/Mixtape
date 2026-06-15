namespace iPodCommander;

/// <summary>
/// iPod timestamps are HFS+/Mac time: seconds since 1904-01-01 00:00 UTC, stored as a
/// little-endian u32. A value of 0 means "unset". This converts to/from .NET DateTime.
/// </summary>
internal static class MacTime
{
    // Seconds between the Mac epoch (1904-01-01) and the Unix epoch (1970-01-01).
    private const long MacEpochDelta = 2082844800L;

    /// <summary>Mac seconds → UTC DateTime, or null when the field is unset (0).</summary>
    public static DateTime? ToDateTime(uint macSeconds)
    {
        if (macSeconds == 0) return null;
        long unix = (long)macSeconds - MacEpochDelta;
        return DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime;
    }

    /// <summary>UTC DateTime → Mac seconds; null/default maps to 0 (unset).</summary>
    public static uint FromDateTime(DateTime? value)
    {
        if (value is null) return 0;
        DateTime dt = value.Value;
        // A Local time is converted to UTC; an Unspecified one is assumed already UTC (our callers pass UtcNow).
        DateTime utc = dt.Kind == DateTimeKind.Local ? dt.ToUniversalTime() : DateTime.SpecifyKind(dt, DateTimeKind.Utc);
        long mac = ((DateTimeOffset)utc).ToUnixTimeSeconds() + MacEpochDelta;
        if (mac < 0 || mac > uint.MaxValue) return 0; // out of an iPod's representable range → unset
        return (uint)mac;
    }
}
