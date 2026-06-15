using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using iPodCommander;   // the cross-platform engine in Mixtape.Core

namespace Mixtape.App.ViewModels;

public enum SidebarKind { Device, AllSongs, Videos, Playlist, LocalMusic }

/// <summary>One row in the left rail.</summary>
public sealed class SidebarItem
{
    public string Title { get; init; } = "";
    public string Glyph { get; init; } = "";
    public SidebarKind Kind { get; init; }
    public bool IsHeader { get; init; }   // section label (DEVICE / LIBRARY / …) — not selectable
    internal Playlist? Playlist { get; init; }
}

/// <summary>A display row in the song table (public, so reflection bindings see the columns).</summary>
public sealed class TrackRow : INotifyPropertyChanged
{
    public string Title { get; init; } = "";
    public string Artist { get; init; } = "";
    public string Album { get; init; } = "";
    public string Stars { get; init; } = "";
    public string Plays { get; init; } = "";
    public string Added { get; init; } = "";
    public string Time { get; init; } = "";
    internal Track? Source { get; init; }

    private Bitmap? _art;
    public Bitmap? Art { get => _art; set { _art = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Art))); } }
    public event PropertyChangedEventHandler? PropertyChanged;

    internal static TrackRow From(Track t) => new()
    {
        Title = t.DisplayTitle,
        Artist = t.Artist ?? "",
        Album = t.Album ?? "",
        Stars = t.Rating >= 20 ? new string('★', Math.Clamp(t.Rating / 20, 0, 5)) : "",
        Plays = t.PlayCount > 0 ? t.PlayCount.ToString() : "",
        Added = t.DateAdded is { } d && d.Year > 1970 ? d.ToString("yyyy-MM-dd") : "",
        Time = t.DurationStr,
        Source = t,
    };
}

public sealed class MainViewModel : INotifyPropertyChanged
{
    private static readonly string[] AudioExt =
    {
        ".mp3", ".m4a", ".aac", ".wav", ".aif", ".aiff", ".m4b",
        ".flac", ".ogg", ".oga", ".opus", ".wma", ".ape", ".wv", ".mpc", ".mka",
    };

    private readonly List<IPodDevice> _devices = new();
    private IPodDevice? _device;
    private IpodLibrary? _lib;
    private readonly List<Track> _localTracks = new();
    private readonly List<string> _localFolders = new();
    private int _localGen;

    public ObservableCollection<SidebarItem> SidebarItems { get; } = new();
    public ObservableCollection<TrackRow> Tracks { get; } = new();

    public MainViewModel()
    {
        Refresh();
        // Optional: `Mixtape.App <folder>` opens that PC folder as Local Music on launch.
        var folderArg = Environment.GetCommandLineArgs().Skip(1).FirstOrDefault(Directory.Exists);
        if (folderArg is not null) AddLocalFolders(new[] { folderArg });
        var q = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--q=")); // test aid
        if (q is not null) SearchText = q.Substring(4);
    }

    // ---- bound header / status ----
    private string _headerKicker = "LIBRARY", _headerTitle = "Mixtape", _headerSubtitle = "", _status = "";
    public string HeaderKicker { get => _headerKicker; set => Set(ref _headerKicker, value); }
    public string HeaderTitle { get => _headerTitle; set => Set(ref _headerTitle, value); }
    public string HeaderSubtitle { get => _headerSubtitle; set => Set(ref _headerSubtitle, value); }
    public string Status { get => _status; set => Set(ref _status, value); }

    private SidebarItem? _selectedSidebar;
    public SidebarItem? SelectedSidebar
    {
        get => _selectedSidebar;
        set { if (!ReferenceEquals(_selectedSidebar, value)) { _selectedSidebar = value; OnPropertyChanged(); OnSelect(value); } }
    }

    // Header action buttons adapt to the view: iPod views show Add music / Delete; Local Music shows Add folder.
    private bool _isIpodView;
    public bool IsIpodView { get => _isIpodView; set => Set(ref _isIpodView, value); }
    private bool _isLocalView = true;
    public bool IsLocalView { get => _isLocalView; set => Set(ref _isLocalView, value); }
    public bool CanWrite => _device?.Profile.CanWrite ?? false;

    // ---- device detection (mirrors the WinForms RefreshDevices/LoadDevice flow) ----
    public void Refresh()
    {
        _devices.Clear();
        try { _devices.AddRange(DeviceDetector.DetectAll()); }
        catch (Exception ex) { Status = "Couldn't scan for iPods: " + ex.Message; }

        _device = _devices.FirstOrDefault();
        _lib = null;
        if (_device is not null)
        {
            try { _lib = IpodLibrary.Load(_device); }
            catch (Exception ex) { Status = "Couldn't read the iPod: " + ex.Message; }
        }

        BuildSidebar();
        SelectedSidebar = SidebarItems.FirstOrDefault(s => s.Kind == SidebarKind.AllSongs)
                          ?? SidebarItems.FirstOrDefault(s => s.Kind == SidebarKind.LocalMusic);
    }

    private static SidebarItem Header(string t) => new() { IsHeader = true, Title = t };

    private void BuildSidebar()
    {
        SidebarItems.Clear();
        if (_device is not null)
        {
            SidebarItems.Add(Header("DEVICE"));
            SidebarItems.Add(new SidebarItem { Kind = SidebarKind.Device, Glyph = "◉", Title = _device.Profile.ModelName ?? _device.Profile.ModelNumber ?? "iPod" });
        }
        if (_lib is not null)
        {
            SidebarItems.Add(Header("LIBRARY"));
            SidebarItems.Add(new SidebarItem { Kind = SidebarKind.AllSongs, Glyph = "♪", Title = "All songs" });
            if (_lib.View.Tracks.Any(t => MediaType.IsVideo(t.MediaType)))
                SidebarItems.Add(new SidebarItem { Kind = SidebarKind.Videos, Glyph = "▶", Title = "Videos" });

            var seen = new HashSet<ulong>();
            var playlists = new List<SidebarItem>();
            foreach (var pl in _lib.View.Playlists)
            {
                if (pl.IsMaster || pl.IsPodcast) continue;
                if (pl.PersistentId != 0 && !seen.Add(pl.PersistentId)) continue;
                playlists.Add(new SidebarItem { Kind = SidebarKind.Playlist, Glyph = "☰", Title = string.IsNullOrEmpty(pl.Name) ? "Untitled playlist" : pl.Name, Playlist = pl });
            }
            if (playlists.Count > 0)
            {
                SidebarItems.Add(Header("PLAYLISTS"));
                foreach (var p in playlists) SidebarItems.Add(p);
            }
        }
        SidebarItems.Add(Header("ON THIS PC"));
        SidebarItems.Add(new SidebarItem { Kind = SidebarKind.LocalMusic, Glyph = "▣", Title = "Local Music" });
    }

    // ---- view switching (mirrors ShowCurrent) ----
    private void OnSelect(SidebarItem? s)
    {
        if (s is null) return;
        if (_searchText.Length > 0) { _searchText = ""; OnPropertyChanged(nameof(SearchText)); } // clear search on navigation
        IsLocalView = s.Kind == SidebarKind.LocalMusic;
        IsIpodView = _lib is not null && s.Kind is SidebarKind.AllSongs or SidebarKind.Videos or SidebarKind.Playlist or SidebarKind.Device;
        OnPropertyChanged(nameof(CanWrite));
        switch (s.Kind)
        {
            case SidebarKind.LocalMusic: ShowLocalMusic(); break;
            case SidebarKind.Videos when _lib is not null:
                ShowTracks(_lib.View.Tracks.Where(t => MediaType.IsVideo(t.MediaType)), "LIBRARY", "Videos", "video"); break;
            case SidebarKind.Playlist when s.Playlist is not null && _lib is not null:
                ShowPlaylist(s.Playlist); break;
            case SidebarKind.Device when _lib is not null:
                ShowTracks(_lib.View.Tracks.Where(t => MediaType.IsAudio(t.MediaType)), "DEVICE", _device?.Profile.ModelName ?? "iPod", "song"); break;
            case SidebarKind.AllSongs when _lib is not null:
                ShowTracks(_lib.View.Tracks.Where(t => MediaType.IsAudio(t.MediaType)), "LIBRARY", "All songs", "song"); break;
        }
    }

    // ---- copy to / delete from the iPod (writes go through Mixtape.Core's SafeDbWriter: backup + verify + rollback) ----
    private static readonly string[] NativeAudioExt = { ".mp3", ".m4a", ".aac", ".wav", ".aif", ".aiff", ".m4b" };

    public void AddMusicToIpod(string[] files)
    {
        if (_lib is null || _device is null || !_device.Profile.CanWrite || files.Length == 0) return;
        try
        {
            var existing = new HashSet<string>(StringComparer.Ordinal);
            foreach (var t in _lib.View.Tracks)
                if (MediaType.IsAudio(t.MediaType)) existing.Add(AudioKey(t.Title, t.Artist, t.Album));

            int added = 0, dup = 0, needConv = 0, failed = 0;
            foreach (var f in files)
            {
                try
                {
                    if (!NativeAudioExt.Contains(Path.GetExtension(f).ToLowerInvariant())) { needConv++; continue; }
                    var nt = MetadataExtractor.Read(f);
                    string key = AudioKey(nt.Title, nt.Artist, nt.Album);
                    if (existing.Contains(key)) { dup++; continue; }   // skip duplicates already on the iPod
                    _lib.AddFile(f);
                    existing.Add(key);
                    added++;
                }
                catch { failed++; }
            }
            if (added > 0) _lib.Save();
            ReloadLibrary();
            var msg = $"Added {added} song{(added == 1 ? "" : "s")}";
            if (dup > 0) msg += $", skipped {dup} duplicate{(dup == 1 ? "" : "s")}";
            if (needConv > 0) msg += $", {needConv} need converting (not supported here yet)";
            if (failed > 0) msg += $", {failed} failed";
            Status = msg + ".";
        }
        catch (Exception ex) { Status = "Add failed: " + ex.Message; }
    }

    public void DeleteSelected(IReadOnlyList<TrackRow> rows)
    {
        if (_lib is null || _device is null || !_device.Profile.CanWrite || rows.Count == 0) return;
        try
        {
            int n = 0;
            foreach (var r in rows) if (r.Source is { } t) { _lib.DeleteTrack(t.UniqueId, deleteFile: true); n++; }
            if (n > 0) _lib.Save();
            ReloadLibrary();
            Status = $"Deleted {n} song{(n == 1 ? "" : "s")}.";
        }
        catch (Exception ex) { Status = "Delete failed: " + ex.Message; }
    }

    private void ReloadLibrary()
    {
        if (_device is null) return;
        try { _lib = IpodLibrary.Load(_device); } catch { }
        BuildSidebar();
        SelectedSidebar = SidebarItems.FirstOrDefault(s => s.Kind == SidebarKind.AllSongs) ?? SidebarItems.FirstOrDefault();
    }

    private static string AudioKey(string? title, string? artist, string? album)
        => Norm(title) + "" + Norm(artist) + "" + Norm(album);
    private static string Norm(string? s)
    {
        s = (s ?? "").Trim().ToLowerInvariant();
        return string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private void ShowPlaylist(Playlist pl)
    {
        if (_lib is null) return;
        var list = new List<Track>();
        foreach (var id in pl.TrackIds)
            if (_lib.View.FindByUniqueId(id) is { } t) list.Add(t);
        ShowTracks(list, "PLAYLIST", string.IsNullOrEmpty(pl.Name) ? "Untitled playlist" : pl.Name, "song", preserveOrder: true);
    }

    private List<Track> _currentFull = new();
    private string _curKicker = "LIBRARY", _curTitle = "Mixtape", _curNoun = "song";
    private bool _localView;

    private string _searchText = "";
    public string SearchText { get => _searchText; set { if (Set(ref _searchText, value)) RenderCurrent(); } }

    private void ShowTracks(IEnumerable<Track> tracks, string kicker, string title, string noun, bool preserveOrder = false)
    {
        _currentFull = tracks.ToList();
        _curKicker = kicker; _curTitle = title; _curNoun = noun; _localView = false;
        RenderCurrent();
    }

    private void RenderCurrent()
    {
        string q = _searchText.Trim();
        IEnumerable<Track> src = _currentFull;
        if (q.Length > 0) src = src.Where(t => Match(t, q));
        var shown = src.ToList();
        Tracks.Clear();
        foreach (var t in shown) Tracks.Add(TrackRow.From(t));
        long ms = shown.Sum(t => (long)t.LengthMs);
        HeaderKicker = _curKicker;
        HeaderTitle = _curTitle;
        HeaderSubtitle = $"{shown.Count} {_curNoun}{(shown.Count == 1 ? "" : "s")} · {FormatTotal(ms)}";
        Status = _localView
            ? (_localFolders.Count == 0 ? "Click “Add folder” to add music from your PC."
               : $"{shown.Count} songs · {_localFolders.Count} folder{(_localFolders.Count == 1 ? "" : "s")}")
            : HeaderSubtitle;

        LoadArtwork(Tracks.ToList());
    }

    private Bitmap? _headerArt;
    public Bitmap? HeaderArt { get => _headerArt; set => Set(ref _headerArt, value); }

    private int _artGen;
    private async void LoadArtwork(IReadOnlyList<TrackRow> rows)
    {
        int gen = ++_artGen;
        HeaderArt = null;
        bool headerSet = false;
        foreach (var r in rows)
        {
            if (gen != _artGen) return;                  // a newer view replaced this one
            var t = r.Source;
            if (t is null) continue;
            string? path = t.LocalPath ?? (_device is not null ? t.ResolveFilePath(_device.MountRoot) : null);
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) continue;
            string key = string.IsNullOrEmpty(t.Album) ? path : (Norm(t.Artist) + "|" + Norm(t.Album));
            var bmp = await ArtLoader.LoadAsync(path, key);
            if (gen != _artGen) return;
            if (bmp is not null)
            {
                r.Art = bmp;
                if (!headerSet) { HeaderArt = bmp; headerSet = true; }   // header tile shows the first cover found
            }
        }
    }

    private static bool Match(Track t, string q)
        => t.DisplayTitle.Contains(q, StringComparison.OrdinalIgnoreCase)
        || (t.Artist?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
        || (t.Album?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false);

    // ---- Local Music (no iPod required) ----
    private void ShowLocalMusic()
    {
        _currentFull = _localTracks.ToList();
        _curKicker = "ON THIS PC"; _curTitle = "Local Music"; _curNoun = "song"; _localView = true;
        RenderCurrent();
    }

    /// <summary>Files/folders dropped on the window → add their folders to Local Music.</summary>
    public void AddDropped(IEnumerable<string> paths)
    {
        var folders = new List<string>();
        foreach (var p in paths)
        {
            if (Directory.Exists(p)) folders.Add(p);
            else if (File.Exists(p) && AudioExt.Contains(Path.GetExtension(p).ToLowerInvariant()))
            {
                var d = Path.GetDirectoryName(p);
                if (!string.IsNullOrEmpty(d)) folders.Add(d);
            }
        }
        if (folders.Count > 0) AddLocalFolders(folders);
        else Status = "Drop a music folder — or songs — to add them to Local Music.";
    }

    public void AddLocalFolders(IEnumerable<string> folders)
    {
        foreach (var f in folders)
            if (!_localFolders.Any(p => string.Equals(p, f, StringComparison.OrdinalIgnoreCase)))
                _localFolders.Add(f);
        // jump to the Local Music view, then scan
        var local = SidebarItems.FirstOrDefault(s => s.Kind == SidebarKind.LocalMusic);
        if (local is not null) _selectedSidebar = local; // set without re-triggering scan loop
        OnPropertyChanged(nameof(SelectedSidebar));
        ScanLocal();
    }

    private async void ScanLocal()
    {
        int gen = ++_localGen;
        var folders = _localFolders.ToList();
        if (folders.Count == 0) { _localTracks.Clear(); ShowLocalMusic(); return; }
        Status = "Scanning your music…";

        var found = await Task.Run(() =>
        {
            var list = new List<Track>();
            var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
            foreach (var dir in folders)
            {
                try
                {
                    foreach (var f in Directory.EnumerateFiles(dir, "*", opts))
                    {
                        if (!AudioExt.Contains(Path.GetExtension(f).ToLowerInvariant())) continue;
                        Track t;
                        try
                        {
                            var nt = MetadataExtractor.Read(f);
                            t = new Track
                            {
                                Title = string.IsNullOrWhiteSpace(nt.Title) ? Path.GetFileNameWithoutExtension(f) : nt.Title,
                                Artist = nt.Artist,
                                Album = nt.Album,
                                LengthMs = nt.LengthMs,
                            };
                        }
                        catch { t = new Track { Title = Path.GetFileNameWithoutExtension(f) }; }
                        t.MediaType = MediaType.Audio;
                        t.LocalPath = f;
                        try { t.DateAdded = File.GetLastWriteTime(f); } catch { }
                        list.Add(t);
                    }
                }
                catch { /* skip unreadable folder */ }
            }
            return list;
        });

        if (gen != _localGen) return; // superseded by a newer scan
        _localTracks.Clear();
        _localTracks.AddRange(found);
        ShowLocalMusic();
    }

    private static string FormatTotal(long ms)
    {
        var t = TimeSpan.FromMilliseconds(ms);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours} hr {t.Minutes} min"
            : t.TotalMinutes >= 1 ? $"{t.Minutes} min"
            : $"{t.Seconds} s";
    }

    // ---- playback (LibVLC via AudioService) ----
    private AudioService? _audio;
    private DispatcherTimer? _tick;
    private bool _updatingFromTimer;
    private readonly float[] _eqGains = { 5, 4, 2, 0, -1, -1, 0, 2, 4, 5 }; // gentle "smile" preset

    private bool _hasNow;   public bool HasNowPlaying { get => _hasNow; set => Set(ref _hasNow, value); }
    private string _nowTitle = ""; public string NowTitle { get => _nowTitle; set => Set(ref _nowTitle, value); }
    private string _nowSub = "";   public string NowSub { get => _nowSub; set => Set(ref _nowSub, value); }
    private string _playGlyph = "▶"; public string PlayPauseGlyph { get => _playGlyph; set => Set(ref _playGlyph, value); }
    private string _posText = "0:00"; public string PosText { get => _posText; set => Set(ref _posText, value); }
    private string _durText = "0:00"; public string DurText { get => _durText; set => Set(ref _durText, value); }

    private double _posFrac;
    public double PositionFraction
    {
        get => _posFrac;
        set { if (Set(ref _posFrac, value) && !_updatingFromTimer) _audio?.SeekFraction(value); }
    }

    private int _volume = 90;
    public int Volume { get => _volume; set { if (Set(ref _volume, value) && _audio is not null) _audio.Volume = value; } }

    private bool _eqOn;
    public bool EqOn { get => _eqOn; set { if (Set(ref _eqOn, value)) _audio?.SetEq(value, _eqGains); } }

    public void PlayRow(TrackRow? row)
    {
        var t = row?.Source;
        if (t is null) return;
        string? path = t.LocalPath;
        if (string.IsNullOrEmpty(path) && _device is not null) path = t.ResolveFilePath(_device.MountRoot);
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) { Status = "Can't find the audio file for this track."; return; }

        try
        {
            EnsureAudio();
            if (!_audio!.Available)
            {
                Status = "Audio playback needs VLC. On Linux install it (e.g. “sudo pacman -S vlc” / “sudo apt install vlc”); the AppImage bundles it. Browsing and copying still work.";
                return;
            }
            _audio.Play(path);
            _audio.Volume = _volume;
            _audio.SetEq(_eqOn, _eqGains);
            NowTitle = t.DisplayTitle;
            NowSub = string.Join("  —  ", new[] { t.Artist, t.Album }.Where(s => !string.IsNullOrEmpty(s)));
            HasNowPlaying = true;
            PlayPauseGlyph = "⏸";
            _tick!.Start();
            Status = "Playing: " + t.DisplayTitle;
        }
        catch (Exception ex) { Status = "Playback error: " + ex.Message; }
    }

    public void PlayPause()
    {
        if (_audio is null) return;
        _audio.TogglePause();
        PlayPauseGlyph = _audio.IsPlaying ? "⏸" : "▶";
    }

    private void EnsureAudio()
    {
        if (_audio is not null) return;
        _audio = new AudioService();
        _tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _tick.Tick += (_, _) => UpdateTransport();
    }

    private void UpdateTransport()
    {
        if (_audio is null) return;
        long pos = _audio.PositionMs, dur = _audio.DurationMs;
        _updatingFromTimer = true;
        PositionFraction = dur > 0 ? Math.Clamp((double)pos / dur, 0, 1) : 0;
        _updatingFromTimer = false;
        PosText = FmtClock(pos);
        DurText = FmtClock(dur);
        PlayPauseGlyph = _audio.IsPlaying ? "⏸" : "▶";
    }

    private static string FmtClock(long ms)
    {
        var t = TimeSpan.FromMilliseconds(ms < 0 ? 0 : ms);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }

    // ---- INotifyPropertyChanged ----
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value; OnPropertyChanged(name); return true;
    }
}
