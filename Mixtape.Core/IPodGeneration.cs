namespace iPodCommander;

/// <summary>
/// Coarse iPod generation/family, enough to pick a <see cref="ChecksumScheme"/> and to
/// label the device for the user. Loosely mirrors libgpod's Itdb_IpodGeneration but we
/// only enumerate what we need to reason about.
/// </summary>
internal enum IPodGeneration
{
    Unknown = 0,
    First,
    Second,
    Third,
    Fourth,
    Photo,
    Mini1,
    Mini2,        // the user's device
    Video,        // 5G / 5.5G
    Nano1,
    Nano2,
    Nano3,
    Nano4,
    Nano5,
    Nano6,
    Nano7,
    Classic1,
    Classic2,
    Classic3,
    Shuffle,      // out of scope for v1 (iTunesSD, different layout)
    Touch,        // iOS device — out of scope
}
