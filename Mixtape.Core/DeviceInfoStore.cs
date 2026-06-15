namespace iPodCommander;

/// <summary>
/// Persists recovered device identity back to <c>iPod_Control/Device/SysInfo</c> so that the next
/// <see cref="DeviceDetector.Build"/> picks it up. Used when a FireWire GUID is recovered straight
/// from the device (e.g. the USB storage serial) but we have no full SysInfoExtended document to
/// write — SysInfo is the plain-text file DeviceDetector reads first (see DeviceDetector step 1).
/// </summary>
internal static class DeviceInfoStore
{
    /// <summary>
    /// Create or update <c>iPod_Control/Device/SysInfo</c> so it contains
    /// <c>FirewireGuid: 0x&lt;GUID&gt;</c>, preserving every other existing key (e.g. ModelNumStr,
    /// pszSerialNumber). The GUID must be exactly 16 hex chars (no "0x"); throws otherwise.
    /// </summary>
    public static void WriteFirewireGuid(string mountRoot, string guid16hex)
    {
        if (SysInfoParser.NormalizeGuid(guid16hex) is not { } guid)
            throw new ArgumentException("Not a valid 16-hex-char FireWire GUID.", nameof(guid16hex));

        string deviceDir = Path.Combine(mountRoot, "iPod_Control", "Device");
        Directory.CreateDirectory(deviceDir);
        string path = Path.Combine(deviceDir, "SysInfo");

        // Preserve existing lines verbatim; replace an existing FirewireGuid line in place, or append.
        var lines = File.Exists(path)
            ? new List<string>(File.ReadAllLines(path))
            : new List<string>();

        string newLine = $"FirewireGuid: 0x{guid}";
        bool replaced = false;
        for (int i = 0; i < lines.Count; i++)
        {
            int colon = lines[i].IndexOf(':');
            if (colon <= 0) continue;
            if (lines[i][..colon].Trim().Equals("FirewireGuid", StringComparison.OrdinalIgnoreCase))
            {
                lines[i] = newLine;
                replaced = true;
                break;
            }
        }
        if (!replaced) lines.Add(newLine);

        File.WriteAllText(path, string.Join(Environment.NewLine, lines) + Environment.NewLine);
    }
}
