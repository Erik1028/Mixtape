using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
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
    internal Playlist? Playlist { get; init; }
}

/// <summary>A display row in the song table (public, so reflection bindings see the columns).</summary>
public sealed class TrackRow
{
    public string Title { get; init; } = "";
    public string Artist { get; init; } = "";
    public string Album { get; init; } = "";
    public string Stars { get; init; } = "";
    public string Plays { get; init; } = "";
    public string Added { get; init; } = "";
    public string Time { get; init; } = "";
    internal Track? Source { get; init; }

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

    private void BuildSidebar()
    {
        SidebarItems.Clear();
        if (_device is not null)
            SidebarItems.Add(new SidebarItem { Kind = SidebarKind.Device, Glyph = "◉", Title = _device.Profile.ModelName ?? _device.Profile.ModelNumber ?? "iPod" });
        if (_lib is not null)
        {
            SidebarItems.Add(new SidebarItem { Kind = SidebarKind.AllSongs, Glyph = "♪", Title = "All songs" });
            if (_lib.View.Tracks.Any(t => MediaType.IsVideo(t.MediaType)))
                SidebarItems.Add(new SidebarItem { Kind = SidebarKind.Videos, Glyph = "▶", Title = "Videos" });

            var seen = new HashSet<ulong>();
            foreach (var pl in _lib.View.Playlists)
            {
                if (pl.IsMaster || pl.IsPodcast) continue;
                if (pl.PersistentId != 0 && !seen.Add(pl.PersistentId)) continue;
                SidebarItems.Add(new SidebarItem { Kind = SidebarKind.Playlist, Glyph = "☰", Title = string.IsNullOrEmpty(pl.Name) ? "Untitled playlist" : pl.Name, Playlist = pl });
            }
        }
        SidebarItems.Add(new SidebarItem { Kind = SidebarKind.LocalMusic, Glyph = "▣", Title = "Local Music" });
    }

    // ---- view switching (mirrors ShowCurrent) ----
    private void OnSelect(SidebarItem? s)
    {
        if (s is null) return;
        if (_searchText.Length > 0) { _searchText = ""; OnPropertyChanged(nameof(SearchText)); } // clear search on navigation
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
            _audio!.Play(path);
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
