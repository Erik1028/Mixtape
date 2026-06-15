namespace iPodCommander;

/// <summary>
/// Finds iPods mounted on this PC and builds a <see cref="DeviceProfile"/> for each.
///
/// Identity rule (verified against libgpod behaviour): do NOT filter by drive type.
/// Click-wheel iPods may enumerate as Removable OR Fixed depending on the SCSI removable-
/// media bit, independent of HDD-vs-flash storage. We accept both and identify a device
/// purely by the presence of a hidden "iPod_Control" folder at the drive root.
/// </summary>
internal static class DeviceDetector
{
    public static List<IPodDevice> DetectAll()
    {
        var found = new List<IPodDevice>();
        foreach (DriveInfo drive in SafeGetDrives())
        {
            string root;
            try
            {
                if (!drive.IsReady) continue;
                if (drive.DriveType != DriveType.Fixed && drive.DriveType != DriveType.Removable) continue;
                root = drive.RootDirectory.FullName;
                if (!Directory.Exists(Path.Combine(root, "iPod_Control"))) continue;
            }
            catch
            {
                continue; // a drive that throws on probe isn't our iPod
            }

            var device = Build(root);
            if (device is not null) found.Add(device);
        }
        return found;
    }

    /// <summary>Build a device for an explicit mount root (used by tests / manual override).</summary>
    public static IPodDevice? Build(string mountRoot)
    {
        string controlDir = Path.Combine(mountRoot, "iPod_Control");
        if (!Directory.Exists(controlDir)) return null;

        var profile = new DeviceProfile();
        string deviceDir = Path.Combine(controlDir, "Device");

        // 1) SysInfo (plain text) — model number + maybe FireWire GUID.
        var sysInfo = SysInfoParser.Parse(Path.Combine(deviceDir, "SysInfo"));
        sysInfo.TryGetValue("ModelNumStr", out string? modelNum);
        profile.ModelNumber = modelNum;
        if (sysInfo.TryGetValue("pszSerialNumber", out string? serial)) profile.SerialNumber = serial;
        if (sysInfo.TryGetValue("FirewireGuid", out string? fwSys))
            profile.FirewireGuid = SysInfoParser.NormalizeGuid(fwSys);

        // 2) Model table → generation + friendly name (Layer 0 — the authoritative ModelNumStr lookup).
        var (gen, name) = ModelTable.Lookup(modelNum);
        profile.Generation = gen;
        profile.ModelName = name;
        if (name is not null) profile.IdentifiedBy = "model number";

        // 3) SysInfoExtended (plist) — better GUID/serial, FamilyID, and DBVersion (authoritative scheme).
        var ext = SysInfoExtended.TryParse(Path.Combine(deviceDir, "SysInfoExtended"));
        if (ext is not null)
        {
            if (!string.IsNullOrEmpty(ext.FirewireGuid)) profile.FirewireGuid ??= ext.FirewireGuid;
            if (!string.IsNullOrEmpty(ext.SerialNumber)) profile.SerialNumber ??= ext.SerialNumber;
        }

        // 3b) If SysInfo had no usable model string (common on Windows-managed iPods → "Unknown"),
        //     infer the generation from other signals so the device page shows something useful.
        //     These set the LABEL + capabilities only; they never relax the write-safety scheme below.
        if (gen == IPodGeneration.Unknown)
        {
            // Layer 3 — SysInfoExtended FamilyID (verified table; the most precise of the fallbacks).
            if (ext?.FamilyId is int fam && IpodIdentify.FromFamilyId(fam, ext.UpdaterFamilyId) is { } fr)
            {
                profile.Generation = fr.Gen; profile.ModelName ??= fr.Name; profile.IdentifiedBy = "FamilyID";
            }
            // Layer 2 — photo/artwork .ithmb format-id fingerprint (works even with no SysInfo at all).
            // Pass the drive capacity so the {nano3G, classic} id collision resolves by size.
            else if (IpodIdentify.FromArtworkFingerprint(IpodIdentify.ScanFormatIds(mountRoot), TryDriveBytes(mountRoot)) is { } pf)
            {
                profile.Generation = pf.Gen; profile.ModelName ??= pf.Name; profile.IdentifiedBy = "photo formats";
            }
            gen = profile.Generation; // capability checks below use the inferred generation
        }

        // 4) Signature scheme (write-safety). Resolve from the most authoritative source first and never
        //    let an inferred generation override the device's own evidence:
        //      a) SysInfoExtended DBVersion (the device's explicit declaration), then
        //      b) the existing iTunesDB's own hashing_scheme @0x30 (how the working DB is already signed), then
        //      c) only as a last resort, infer from the generation.
        if (ext?.DbVersion is int dbv)
            profile.Scheme = ModelTable.SchemeForDbVersion(dbv);
        if (profile.Scheme == ChecksumScheme.Unknown)
            profile.Scheme = ReadDbHashingScheme(Path.Combine(controlDir, "iTunes", "iTunesDB"));
        if (profile.Scheme == ChecksumScheme.Unknown)
            profile.Scheme = ModelTable.SchemeFor(gen);

        // For hash58 devices, verify our signing reproduces the device's EXISTING signature before
        // ever enabling a write — if it doesn't match, writing is blocked rather than corrupting the DB.
        if (profile.Scheme == ChecksumScheme.Hash58 && !string.IsNullOrEmpty(profile.FirewireGuid))
        {
            try
            {
                string dbPath = Path.Combine(controlDir, "iTunes", "iTunesDB");
                if (File.Exists(dbPath))
                    profile.Hash58Verified = ChecksumWriter.VerifyHash58(File.ReadAllBytes(dbPath), profile.FirewireGuid);
            }
            catch { /* leave null — couldn't check */ }
        }

        // 5) Music bucket count — count existing F-folders, default 50.
        try
        {
            string musicDir = Path.Combine(controlDir, "Music");
            if (Directory.Exists(musicDir))
            {
                int buckets = Directory.GetDirectories(musicDir, "F*").Length;
                if (buckets > 0) profile.MusicDirCount = buckets;
            }
        }
        catch { /* cosmetic only */ }

        // 6) Media capabilities. Drive off the generation, but also trust the device itself.
        //    The Photos folder lives at the DRIVE ROOT (not under iPod_Control); if it exists the
        //    device is a colour-screen model that handles photos. Windows-managed iPods often have
        //    no model string at all (Generation == Unknown), so be permissive there rather than
        //    hiding the Videos/Photos rows on a perfectly capable device.
        bool unknown = gen == IPodGeneration.Unknown;
        profile.SupportsPhotos = GenSupportsPhotos(gen) || unknown || Directory.Exists(Path.Combine(mountRoot, "Photos"));
        profile.SupportsVideo = GenSupportsVideo(gen) || unknown;

        return new IPodDevice { MountRoot = mountRoot, Profile = profile };
    }

    /// <summary>Colour-screen models that render photos: iPod photo, 5G video, every Classic, every Nano.</summary>
    private static bool GenSupportsPhotos(IPodGeneration gen) => gen switch
    {
        IPodGeneration.Photo or IPodGeneration.Video
            or IPodGeneration.Classic1 or IPodGeneration.Classic2 or IPodGeneration.Classic3
            or IPodGeneration.Nano1 or IPodGeneration.Nano2 or IPodGeneration.Nano3
            or IPodGeneration.Nano4 or IPodGeneration.Nano5 or IPodGeneration.Nano6 or IPodGeneration.Nano7 => true,
        _ => false,
    };

    /// <summary>Models with a hardware video decoder: 5G video, every Classic, Nano 3G and later.</summary>
    private static bool GenSupportsVideo(IPodGeneration gen) => gen switch
    {
        IPodGeneration.Video
            or IPodGeneration.Classic1 or IPodGeneration.Classic2 or IPodGeneration.Classic3
            or IPodGeneration.Nano3 or IPodGeneration.Nano4 or IPodGeneration.Nano5
            or IPodGeneration.Nano6 or IPodGeneration.Nano7 => true,
        _ => false,
    };

    /// <summary>Total capacity of the mounted volume in bytes, or 0 if it can't be read.</summary>
    private static long TryDriveBytes(string mountRoot)
    {
        try { return new DriveInfo(mountRoot).TotalSize; } catch { return 0; }
    }

    /// <summary>Reads the mhbd hashing_scheme field (@0x30) from an existing iTunesDB → the signature it already uses.</summary>
    private static ChecksumScheme ReadDbHashingScheme(string dbPath)
    {
        try
        {
            if (!File.Exists(dbPath)) return ChecksumScheme.Unknown;
            using var fs = new FileStream(dbPath, FileMode.Open, FileAccess.Read);
            Span<byte> head = stackalloc byte[0x32];
            if (fs.Read(head) < 0x32) return ChecksumScheme.Unknown;
            if (head[0] != (byte)'m' || head[1] != (byte)'h' || head[2] != (byte)'b' || head[3] != (byte)'d') return ChecksumScheme.Unknown;
            int scheme = head[0x30] | (head[0x31] << 8);
            return scheme switch
            {
                0 => ChecksumScheme.None,
                1 => ChecksumScheme.Hash58,
                2 => ChecksumScheme.Hash72,
                3 => ChecksumScheme.HashAB,
                _ => ChecksumScheme.Unknown,
            };
        }
        catch { return ChecksumScheme.Unknown; }
    }

    private static DriveInfo[] SafeGetDrives()
    {
        try { return DriveInfo.GetDrives(); }
        catch { return Array.Empty<DriveInfo>(); }
    }
}
