namespace iPodCommander;

/// <summary>
/// Infers an iPod's generation when the plain-text SysInfo has no ModelNumStr (common on
/// Windows-managed iPods). Two independent signals, both from authoritative libgpod data:
///   • <see cref="FromFamilyId"/> — the SysInfoExtended FamilyID integer (a VERIFIED table;
///     UpdaterFamilyID splits the three known collisions).
///   • <see cref="FromArtworkFingerprint"/> — the set of image-format correlation ids the device
///     uses, read cheaply from the <c>F&lt;id&gt;_&lt;n&gt;.ithmb</c> filenames in Photos/Thumbs and
///     iPod_Control/Artwork. A few ids are unique to one family (e.g. 1036 ⇒ iPod 5G "Video").
///
/// Both return a FAMILY-level result and never claim more than the data supports — where a signal
/// can't separate sub-generations (the {nano3G, classic1/2/3} photo collision, mini 1G vs 2G, etc.)
/// it returns the family with a generic label. These feed the device LABEL and capabilities only;
/// the write-safety <see cref="ChecksumScheme"/> is resolved separately from the device's own DB.
/// </summary>
internal static class IpodIdentify
{
    /// <summary>Map a SysInfoExtended FamilyID (+ optional UpdaterFamilyID) to a generation. Null when unknown/unsafe.</summary>
    public static (IPodGeneration Gen, string Name)? FromFamilyId(int familyId, int? updater) => familyId switch
    {
        3 => updater == 6 ? (IPodGeneration.Mini1, "iPod mini (1st gen)")
            : updater == 7 ? (IPodGeneration.Mini2, "iPod mini (2nd gen)")
            : (IPodGeneration.Mini2, "iPod mini"),
        4 => (IPodGeneration.Fourth, "iPod (4th gen)"),
        5 => (IPodGeneration.Photo, "iPod photo"),
        6 => (IPodGeneration.Video, "iPod (5th gen, video)"),
        7 => (IPodGeneration.Nano1, "iPod nano (1st gen)"),
        9 => (IPodGeneration.Nano2, "iPod nano (2nd gen)"),
        11 => updater == 38 ? (IPodGeneration.Classic3, "iPod classic (Late 2009)")
            : updater == 24 ? (IPodGeneration.Classic1, "iPod classic")
            : (IPodGeneration.Classic1, "iPod classic"),
        12 => (IPodGeneration.Nano3, "iPod nano (3rd gen)"),
        15 => (IPodGeneration.Nano4, "iPod nano (4th gen)"),
        16 => (IPodGeneration.Nano5, "iPod nano (5th gen)"),
        17 => (IPodGeneration.Nano6, "iPod nano (6th gen)"),
        18 => (IPodGeneration.Nano7, "iPod nano (7th gen)"),
        128 or 130 or 132 or 133 => (IPodGeneration.Shuffle, "iPod shuffle"),
        // 1,2 (presumed 1G/2G, unconfirmed), 8,10,13,14 (unaccounted gaps), >=10000 (Touch/iPhone — won't mount): don't claim.
        _ => null,
    };

    /// <summary>
    /// Fingerprint the generation from the set of image-format correlation ids present on the device.
    /// Tested most-distinctive id first (each chosen id appears on only one family). Returns a family
    /// label; the 1067 case is the {nano3G, classic1/2/3} collision that artwork ids cannot split.
    /// </summary>
    public static (IPodGeneration Gen, string Name)? FromArtworkFingerprint(IReadOnlySet<int> ids, long driveBytes = 0)
    {
        if (ids.Contains(1087) || ids.Contains(1056) || ids.Contains(1073)) return (IPodGeneration.Nano5, "iPod nano (5th gen)");
        if (ids.Contains(1083) || ids.Contains(1084)) return (IPodGeneration.Nano4, "iPod nano (4th gen)");
        if (ids.Contains(1036) || ids.Contains(1028)) return (IPodGeneration.Video, "iPod (5th gen, video)");
        // {nano3G, classic1/2/3} share these ids; the only thing that splits them is capacity —
        // nano 3G was 4/8 GB, every classic is ≥80 GB. A small drive ⇒ nano 3G.
        if (ids.Contains(1067) || ids.Contains(1060) || ids.Contains(1061))
            return driveBytes is > 0 and <= 40_000_000_000L
                ? (IPodGeneration.Nano3, "iPod nano (3rd gen)")
                : (IPodGeneration.Classic1, "iPod classic");
        if (ids.Contains(1009) || ids.Contains(1013) || ids.Contains(1016) || ids.Contains(1017)) return (IPodGeneration.Photo, "iPod photo");
        if (ids.Contains(1032) || ids.Contains(1023) || ids.Contains(1031) || ids.Contains(1027)) return (IPodGeneration.Nano1, "iPod nano (1st/2nd gen)");
        return null;
    }

    /// <summary>
    /// The set of image-format correlation ids on the device, read from the <c>F&lt;id&gt;_&lt;n&gt;.ithmb</c>
    /// filenames in <c>Photos/Thumbs</c> (photos) and <c>iPod_Control/Artwork</c> (cover art). No file
    /// contents are read — just the directory listing — so this is cheap even on a huge library.
    /// </summary>
    public static HashSet<int> ScanFormatIds(string mountRoot)
    {
        var ids = new HashSet<int>();
        Collect(Path.Combine(mountRoot, "Photos", "Thumbs"), ids);
        Collect(Path.Combine(mountRoot, "iPod_Control", "Artwork"), ids);
        return ids;
    }

    private static void Collect(string dir, HashSet<int> ids)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            foreach (string path in Directory.EnumerateFiles(dir, "F*.ithmb"))
            {
                string name = Path.GetFileNameWithoutExtension(path); // "F1036_1"
                int us = name.IndexOf('_');
                string digits = us > 1 ? name[1..us] : name[1..];
                if (int.TryParse(digits, out int id)) ids.Add(id);
            }
        }
        catch { /* listing failed — just contributes no ids */ }
    }
}
