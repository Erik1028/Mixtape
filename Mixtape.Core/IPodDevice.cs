namespace iPodCommander;

/// <summary>
/// A detected iPod mounted on this PC: where it lives on disk and what it is.
/// </summary>
internal sealed class IPodDevice
{
    public required string MountRoot;        // e.g. "E:\"
    public required DeviceProfile Profile;

    public string ControlDir => Path.Combine(MountRoot, "iPod_Control");
    public string ITunesDir => Path.Combine(ControlDir, "iTunes");
    public string MusicDir => Path.Combine(ControlDir, "Music");
    public string DeviceDir => Path.Combine(ControlDir, "Device");
    public string ITunesDbPath => Path.Combine(ITunesDir, "iTunesDB");

    public bool HasDatabase => File.Exists(ITunesDbPath);

    public string DisplayName
    {
        get
        {
            string model = Profile.ModelName ?? Profile.ModelNumber ?? "iPod";
            return $"{model}  ({MountRoot.TrimEnd('\\')})";
        }
    }
}
