namespace iPodCommander;

/// <summary>
/// Identity + capabilities of a detected iPod, derived from SysInfo/SysInfoExtended and
/// the model table. <see cref="CanWrite"/> is the hard safety gate the UI uses to enable
/// or disable every mutating action.
/// </summary>
internal sealed class DeviceProfile
{
    public IPodGeneration Generation = IPodGeneration.Unknown;
    public ChecksumScheme Scheme = ChecksumScheme.Unknown;

    public string? ModelNumber;     // ModelNumStr from SysInfo, e.g. "M9802"
    public string? ModelName;       // friendly, e.g. "iPod mini (2nd gen)"
    public string? IdentifiedBy;    // how the generation was determined: "model number" / "FamilyID" / "photo formats" / null
    public string? SerialNumber;
    public string? FirewireGuid;    // 16 hex chars, no 0x — null if not found
    public int MusicDirCount = 50;  // F00..F(n-1); used when copying

    /// <summary>Colour-screen models that can display photos (set in <see cref="DeviceDetector"/>).</summary>
    public bool SupportsPhotos;
    /// <summary>Colour-screen models that show album art on screen (set in <see cref="DeviceDetector"/>).</summary>
    public bool SupportsArtwork;
    /// <summary>Models with a video decoder (5G/Classic/Nano 3G+); set in <see cref="DeviceDetector"/>.</summary>
    public bool SupportsVideo;
    /// <summary>
    /// For hash58 devices: result of the signature known-answer test against the device's existing DB.
    /// null = couldn't check (no stored signature/GUID); true = our signing matches the firmware;
    /// false = our signing differs → writing would corrupt the DB, so it's blocked.
    /// </summary>
    public bool? Hash58Verified;

    /// <summary>
    /// Whether we will let the app write this device's database.
    ///   None   → yes.
    ///   Hash58 → yes only if a FireWire GUID was found (needed to sign).
    ///   Hash72 → never (cannot generate the signature).
    ///   HashAB → only behind an explicit experimental opt-in (not yet implemented).
    ///   Unknown→ no.
    /// Milestone 1 is read-only regardless of this flag; it becomes meaningful in M2.
    /// </summary>
    public bool CanWrite => Scheme switch
    {
        ChecksumScheme.None => true,
        ChecksumScheme.Hash58 => !string.IsNullOrEmpty(FirewireGuid) && Hash58Verified != false,
        _ => false,
    };

    public string WriteBlockReason => Scheme switch
    {
        ChecksumScheme.None => "",
        ChecksumScheme.Hash58 when string.IsNullOrEmpty(FirewireGuid)
            => "This iPod uses the hash58 signature but no FireWire GUID was found, so writes are disabled.",
        ChecksumScheme.Hash58 when Hash58Verified == false
            => "Mixtape's hash58 signature doesn't match this iPod's existing one, so writing is disabled to avoid corrupting its database.",
        ChecksumScheme.Hash72
            => "This iPod (Nano 5G / Touch / iPhone) uses a signature this app cannot generate. Listing is safe; copying/deleting is disabled.",
        ChecksumScheme.HashAB
            => "This iPod (Nano 6G/7G) uses the experimental hashAB signature, not yet enabled. Read-only for now.",
        ChecksumScheme.Unknown => "Could not identify this iPod's signature scheme; read-only for safety.",
        _ => "",
    };

    /// <summary>A friendly generation string for the device page, e.g. "iPod (5th gen, video)" — never the raw enum.</summary>
    public string GenerationLabel => ModelTable.GenerationLabel(Generation);

    /// <summary>The generation label plus how it was determined when it had to be inferred (e.g. "… · via photo formats").</summary>
    public string GenerationDisplay =>
        Generation != IPodGeneration.Unknown && IdentifiedBy is not null && IdentifiedBy != "model number"
            ? $"{GenerationLabel}  ·  via {IdentifiedBy}"
            : GenerationLabel;

    public string SchemeLabel => Scheme switch
    {
        ChecksumScheme.None => "none (no signature needed)",
        ChecksumScheme.Hash58 => "hash58",
        ChecksumScheme.Hash72 => "hash72 (unsupported)",
        ChecksumScheme.HashAB => "hashAB (experimental)",
        _ => "unknown",
    };
}
