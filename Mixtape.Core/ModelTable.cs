namespace iPodCommander;

/// <summary>
/// Maps a SysInfo ModelNumStr to a generation/name. The table is a best-effort subset
/// of libgpod's model list — enough to identify the common click-wheel devices and,
/// crucially, to pick the right <see cref="ChecksumScheme"/>.
///
/// ModelNumStr carries one leading marketing letter (M/P/x) we strip before lookup,
/// e.g. "MA350" → "A350", "M9802" → "9802".
/// </summary>
internal static class ModelTable
{
    private readonly record struct Entry(IPodGeneration Gen, string Name);

    // Keyed by the 4-char tail of ModelNumStr (after stripping one leading letter).
    private static readonly Dictionary<string, Entry> Models = new(StringComparer.OrdinalIgnoreCase)
    {
        // iPod mini
        ["9160"] = new(IPodGeneration.Mini1, "iPod mini (1st gen)"),
        ["9436"] = new(IPodGeneration.Mini1, "iPod mini (1st gen)"),
        ["9435"] = new(IPodGeneration.Mini1, "iPod mini (1st gen)"),
        ["9434"] = new(IPodGeneration.Mini1, "iPod mini (1st gen)"),
        ["9437"] = new(IPodGeneration.Mini1, "iPod mini (1st gen)"),
        ["9800"] = new(IPodGeneration.Mini2, "iPod mini (2nd gen)"),   // 4 GB
        ["9801"] = new(IPodGeneration.Mini2, "iPod mini (2nd gen)"),
        ["9802"] = new(IPodGeneration.Mini2, "iPod mini (2nd gen)"),   // 6 GB
        ["9803"] = new(IPodGeneration.Mini2, "iPod mini (2nd gen)"),
        ["9804"] = new(IPodGeneration.Mini2, "iPod mini (2nd gen)"),
        ["9806"] = new(IPodGeneration.Mini2, "iPod mini (2nd gen)"),
        ["9807"] = new(IPodGeneration.Mini2, "iPod mini (2nd gen)"),

        // iPod (classic monochrome/color, photo)
        ["8541"] = new(IPodGeneration.Fourth, "iPod (4th gen)"),
        ["9282"] = new(IPodGeneration.Photo, "iPod photo"),
        ["9585"] = new(IPodGeneration.Photo, "iPod photo"),
        ["9829"] = new(IPodGeneration.Photo, "iPod photo"),
        ["9830"] = new(IPodGeneration.Photo, "iPod photo"),

        // iPod video (5G / 5.5G)
        ["A002"] = new(IPodGeneration.Video, "iPod (5th gen, video)"),
        ["A003"] = new(IPodGeneration.Video, "iPod (5th gen, video)"),
        ["A146"] = new(IPodGeneration.Video, "iPod (5th gen, video)"),
        ["A147"] = new(IPodGeneration.Video, "iPod (5th gen, video)"),
        ["A448"] = new(IPodGeneration.Video, "iPod (5.5 gen, video)"),
        ["A450"] = new(IPodGeneration.Video, "iPod (5.5 gen, video)"),

        // iPod nano
        ["A350"] = new(IPodGeneration.Nano1, "iPod nano (1st gen)"),
        ["A352"] = new(IPodGeneration.Nano1, "iPod nano (1st gen)"),
        ["A004"] = new(IPodGeneration.Nano1, "iPod nano (1st gen)"),
        ["A005"] = new(IPodGeneration.Nano1, "iPod nano (1st gen)"),
        ["A477"] = new(IPodGeneration.Nano2, "iPod nano (2nd gen)"),
        ["A487"] = new(IPodGeneration.Nano2, "iPod nano (2nd gen)"),
        ["A489"] = new(IPodGeneration.Nano2, "iPod nano (2nd gen)"),
        ["A980"] = new(IPodGeneration.Nano3, "iPod nano (3rd gen)"),   // hash58
        ["B261"] = new(IPodGeneration.Nano4, "iPod nano (4th gen)"),   // hash58
        ["B598"] = new(IPodGeneration.Nano5, "iPod nano (5th gen)"),   // hash72
        ["B654"] = new(IPodGeneration.Nano6, "iPod nano (6th gen)"),   // hashAB

        // iPod classic
        ["B029"] = new(IPodGeneration.Classic1, "iPod classic (80 GB)"),   // hash58
        ["B147"] = new(IPodGeneration.Classic1, "iPod classic (160 GB)"),
        ["B145"] = new(IPodGeneration.Classic1, "iPod classic (160 GB)"),
        ["B562"] = new(IPodGeneration.Classic2, "iPod classic (120 GB)"),
        ["C293"] = new(IPodGeneration.Classic3, "iPod classic (160 GB, late)"),
        ["C297"] = new(IPodGeneration.Classic3, "iPod classic (160 GB, late)"),
    };

    public static (IPodGeneration Gen, string? Name) Lookup(string? modelNumStr)
    {
        if (string.IsNullOrWhiteSpace(modelNumStr)) return (IPodGeneration.Unknown, null);
        string s = modelNumStr.Trim();
        // Strip a single leading marketing letter if the remainder looks like a 4-char code.
        if (s.Length == 5 && char.IsLetter(s[0])) s = s[1..];
        if (Models.TryGetValue(s, out var e)) return (e.Gen, e.Name);
        return (IPodGeneration.Unknown, null);
    }

    /// <summary>
    /// Pick the signature scheme from generation. This is the fallback when SysInfoExtended
    /// has no DBVersion (the more authoritative source). Pre-2007 devices need no hash.
    /// </summary>
    public static ChecksumScheme SchemeFor(IPodGeneration gen) => gen switch
    {
        IPodGeneration.Classic1 or IPodGeneration.Classic2 or IPodGeneration.Classic3
            or IPodGeneration.Nano3 or IPodGeneration.Nano4 => ChecksumScheme.Hash58,
        IPodGeneration.Nano5 or IPodGeneration.Touch => ChecksumScheme.Hash72,
        IPodGeneration.Nano6 or IPodGeneration.Nano7 => ChecksumScheme.HashAB,
        IPodGeneration.Unknown => ChecksumScheme.Unknown,
        // Everything older (1G–5G, Photo, Mini 1/2, Nano 1/2, Video) → no signature.
        _ => ChecksumScheme.None,
    };

    /// <summary>A friendly, human label for a generation (used on the device page instead of the raw enum name).</summary>
    public static string GenerationLabel(IPodGeneration gen) => gen switch
    {
        IPodGeneration.First => "iPod (1st gen)",
        IPodGeneration.Second => "iPod (2nd gen)",
        IPodGeneration.Third => "iPod (3rd gen)",
        IPodGeneration.Fourth => "iPod (4th gen)",
        IPodGeneration.Photo => "iPod photo",
        IPodGeneration.Mini1 => "iPod mini (1st gen)",
        IPodGeneration.Mini2 => "iPod mini (2nd gen)",
        IPodGeneration.Video => "iPod (5th gen, video)",
        IPodGeneration.Nano1 => "iPod nano (1st gen)",
        IPodGeneration.Nano2 => "iPod nano (2nd gen)",
        IPodGeneration.Nano3 => "iPod nano (3rd gen)",
        IPodGeneration.Nano4 => "iPod nano (4th gen)",
        IPodGeneration.Nano5 => "iPod nano (5th gen)",
        IPodGeneration.Nano6 => "iPod nano (6th gen)",
        IPodGeneration.Nano7 => "iPod nano (7th gen)",
        IPodGeneration.Classic1 => "iPod classic",
        IPodGeneration.Classic2 => "iPod classic (2008)",
        IPodGeneration.Classic3 => "iPod classic (Late 2009)",
        IPodGeneration.Shuffle => "iPod shuffle",
        IPodGeneration.Touch => "iPod touch",
        _ => "Unknown",
    };

    /// <summary>Map a SysInfoExtended DBVersion integer to a scheme (the authoritative path).</summary>
    public static ChecksumScheme SchemeForDbVersion(int dbVersion) => dbVersion switch
    {
        0 or 1 or 2 => ChecksumScheme.None,
        3 => ChecksumScheme.Hash58,
        4 => ChecksumScheme.Hash72,
        5 => ChecksumScheme.HashAB,
        _ => ChecksumScheme.Unknown,
    };
}
