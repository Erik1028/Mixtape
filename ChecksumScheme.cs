namespace iPodCommander;

/// <summary>
/// The iTunesDB signing scheme a device demands. Mirrors libgpod's ItdbChecksumType.
/// This is THE gating fact for whether we can safely write a device's database:
/// writing an unsigned/wrongly-signed DB to a hash-requiring device makes it show
/// "0 songs" or demand a Restore.
///
/// Decided from SysInfoExtended DBVersion when available (0/1/2→None, 3→Hash58,
/// 4→Hash72, 5→HashAB), else from the device generation.
/// </summary>
internal enum ChecksumScheme
{
    Unknown = -1,
    /// <summary>Pre-2007 iPods (1G–5G, Photo, Mini, Nano 1G/2G). No hash — plain iTunesDB. Fully writable.</summary>
    None = 0,
    /// <summary>Classic 1/2/3, Nano 3G/4G. Reverse-engineered HMAC-SHA1; writable if a FireWire GUID is available.</summary>
    Hash58 = 1,
    /// <summary>Nano 5G, Touch/iPhone 1–3. Not reproducible clean-room — must be refused for writing.</summary>
    Hash72 = 2,
    /// <summary>Nano 6G/7G. Reproducible (dstaley/hashab) but experimental — opt-in only.</summary>
    HashAB = 3,
}
