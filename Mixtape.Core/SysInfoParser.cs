namespace iPodCommander;

/// <summary>
/// Parses iPod_Control/Device/SysInfo — a plain-text file of "Key: value" lines, e.g.
///   ModelNumStr: xM9802
///   FirewireGuid: 0x000A270012345678
///   pszSerialNumber: ...
/// Split on the FIRST ':' only (values can contain colons). Note: this file is written
/// by libgpod tooling, not by iTunes-on-Windows, so it may be absent on a Windows-only iPod.
/// </summary>
internal static class SysInfoParser
{
    public static Dictionary<string, string> Parse(string path)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path)) return result;

        foreach (string raw in File.ReadAllLines(path))
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;
            int colon = line.IndexOf(':');
            if (colon <= 0) continue;
            string key = line[..colon].Trim();
            string value = line[(colon + 1)..].Trim();
            if (key.Length > 0) result[key] = value;
        }
        return result;
    }

    /// <summary>Normalise a FireWire GUID to 16 uppercase hex chars (strip "0x"), or null.</summary>
    public static string? NormalizeGuid(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        string s = raw.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        s = s.Trim();
        if (s.Length == 0) return null;
        foreach (char c in s)
            if (!Uri.IsHexDigit(c)) return null;
        if (s.Length != 16) return null; // a FireWire GUID is exactly 8 bytes / 16 hex — reject anything else
        return s.ToUpperInvariant();
    }
}
