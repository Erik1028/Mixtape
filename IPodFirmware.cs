using System.Text;

namespace iPodCommander;

/// <summary>
/// What the iPod boots, read off its disk: Apple's own software, Rockbox, or Encore OS (Erik's own OS for the iPod
/// Video). Encore is started by the Rockbox bootloader from the same /.rockbox/rockbox.ipod, often with a full Rockbox
/// install still around it - so the folder says nothing; the binary's signature does: the 8 bytes after the ARM
/// vectors are "Rockbox\1" in a Rockbox build and "EncoreOS" in an Encore build (Encore's firmware/ipod5g/crt0.S).
/// </summary>
internal sealed record IPodFirmware(IPodFirmware.Kinds Kind, string? Version)
{
    public enum Kinds { Apple, Rockbox, Encore }

    public static readonly IPodFirmware Apple = new(Kinds.Apple, null);

    public bool IsEncore => Kind == Kinds.Encore;

    /// <summary>"Encore OS 0.10.12", "Rockbox 4.0", "Apple".</summary>
    public string Display => Kind switch
    {
        Kinds.Encore => Version is null ? "Encore OS" : "Encore OS " + Version,
        Kinds.Rockbox => Version is null ? "Rockbox" : "Rockbox " + Version,
        _ => "Apple",
    };

    /// <summary>The codecs Encore plays on the iPod (Encore's library_codec_of + hal_can_decode_mp3, 0.10.13): FLAC and
    /// Apple Lossless. MP3 is too slow for its decoder on the iPod and is skipped; AAC is not supported yet.</summary>
    public static bool EncorePlays(string codec) => codec is "FLAC" or "ALAC";

    private static readonly Dictionary<string, (DateTime Stamp, long Size, IPodFirmware Fw)> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The firmware on the iPod mounted at <paramref name="mountRoot"/>; Apple when there is no Rockbox
    /// bootloader payload, or when the disk cannot be read (say nothing rather than guess). Cached per binary.</summary>
    public static IPodFirmware Detect(string mountRoot)
    {
        try
        {
            string dir = Path.Combine(mountRoot, ".rockbox"), bin = Path.Combine(dir, "rockbox.ipod");
            var fi = new FileInfo(bin);
            if (!fi.Exists) return Apple;
            lock (Cache)
                if (Cache.TryGetValue(mountRoot, out var hit) && hit.Stamp == fi.LastWriteTimeUtc && hit.Size == fi.Length) return hit.Fw;
            byte[] data = File.ReadAllBytes(bin);   // ~1 MB, once per binary
            int head = Math.Min(data.Length, 256);
            IPodFirmware fw = IndexOf(data, 0, head, "EncoreOS"u8) >= 0
                ? new(Kinds.Encore, EncoreVersion(data))
                : new(Kinds.Rockbox, RockboxVersion(dir));
            lock (Cache) Cache[mountRoot] = (fi.LastWriteTimeUtc, fi.Length, fw);
            return fw;
        }
        catch { return Apple; }
    }

    /// <summary>Encore's About screen pairs "Encore OS" with its version literal ("0.10.12"), and the compiler lays the
    /// version out just before the name - the string right before "\0Encore OS\0", when it reads like a version. No
    /// version (rather than a wrong one) if a later build lays its strings out differently.</summary>
    private static string? EncoreVersion(byte[] data)
    {
        ReadOnlySpan<byte> name = "\0Encore OS\0"u8;
        for (int at = IndexOf(data, 0, data.Length, name); at > 0; at = IndexOf(data, at + 1, data.Length, name))
        {
            int start = at;
            while (start > 0 && data[start - 1] != 0 && at - start < 16) start--;
            string s = Encoding.ASCII.GetString(data, start, at - start);
            if (System.Text.RegularExpressions.Regex.IsMatch(s, @"^\d+\.\d+(\.\d+)?$")) return s;
        }
        return null;
    }

    /// <summary>"Version: 4.0" from the install's rockbox-info.txt.</summary>
    private static string? RockboxVersion(string dir)
    {
        try
        {
            foreach (var line in File.ReadLines(Path.Combine(dir, "rockbox-info.txt")))
                if (line.StartsWith("Version:", StringComparison.OrdinalIgnoreCase)) return line[8..].Trim() is { Length: > 0 } v ? v : null;
        }
        catch { }
        return null;
    }

    private static int IndexOf(byte[] data, int from, int to, ReadOnlySpan<byte> pat)
    {
        if (from >= to) return -1;
        int i = data.AsSpan(from, to - from).IndexOf(pat);
        return i < 0 ? -1 : from + i;
    }
}
