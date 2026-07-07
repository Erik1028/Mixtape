using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using iPodCommander;   // the cross-platform engine in Mixtape.Core

namespace Mixtape.App.ViewModels;

/// <summary>An ObservableCollection with a bulk <see cref="ResetTo"/>: replace all items and raise ONE
/// Reset notification instead of a Clear + N Adds. Rebuilding the song list on every search keystroke /
/// nav used to fire N CollectionChanged events, which the DataGrid processed one at a time.</summary>
public sealed class RangeObservableCollection<T> : ObservableCollection<T>
{
    public void ResetTo(IEnumerable<T> items)
    {
        Items.Clear();
        foreach (var it in items) Items.Add(it);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}

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
    public string Plays { get; init; } = "";
    public string Added { get; init; } = "";
    public bool AddedRecent { get; init; }   // added Today/Yesterday → tinted accent in the ADDED column
    public string Time { get; init; } = "";
    internal Track? Source { get; init; }

    // Rating is live (click-to-rate updates it in place without a full grid rebuild). 0–5 stars.
    // (The RATING column binds RatingValue via the per-star buttons; the old `Stars` string is gone — nothing bound it.)
    private int _rating;
    public int RatingValue { get => _rating; set { if (_rating != value) { _rating = value; OnChanged(nameof(RatingValue)); OnChanged(nameof(HasRating)); } } }
    public bool HasRating => _rating > 0;   // unrated rows hide their stars until hovered (matches Windows)

    // Per-album seeded placeholder tile (the Windows Theme.MakeArt colours) shown until the cover decodes.
    public Avalonia.Media.IBrush TileBrush { get; init; } = Avalonia.Media.Brushes.Transparent;

    private Bitmap? _art;
    public Bitmap? Art { get => _art; set { _art = value; OnChanged(nameof(Art)); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

    internal static TrackRow From(Track t)
    {
        var (added, recent) = FmtAdded(t.DateAdded);
        return new()
        {
            Title = t.DisplayTitle,
            Artist = t.Artist ?? "",
            Album = t.Album ?? "",
            _rating = Math.Clamp(t.Rating / 20, 0, 5),
            Plays = t.PlayCount > 0 ? t.PlayCount.ToString() : "",
            Added = added,
            AddedRecent = recent,
            Time = t.DurationStr,
            Source = t,
            TileBrush = AppTheme.ArtTileBrush(string.IsNullOrEmpty(t.Album) ? t.DisplayTitle : t.Album!),
        };
    }

    /// <summary>Conversational date for the ADDED column (matches the Windows list): Today / Yesterday / "Mar 27" /
    /// "Mar 27, 2025". Returns whether it's Today/Yesterday so the cell can tint those accent ("what's new" pops).</summary>
    private static (string, bool) FmtAdded(DateTime? d)
    {
        if (d is not { } dt || dt.Year <= 1970) return ("", false);
        var local = dt.ToLocalTime();   // the reader yields UTC; compare in LOCAL time so Today/Yesterday isn't off by a day near midnight
        var today = DateTime.Today; var day = local.Date;
        if (day == today) return ("Today", true);
        if (day == today.AddDays(-1)) return ("Yesterday", true);
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        return (local.Year == today.Year ? local.ToString("MMM d", ci) : local.ToString("MMM d, yyyy", ci), false);
    }
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
    public RangeObservableCollection<TrackRow> Tracks { get; } = new();

    public MainViewModel()
    {
        var (sh, rep) = AppConfig.LoadModes();
        RestoreModes(sh, rep);
        BuildEqBands();
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
    public string HeaderTitle { get => _headerTitle; set { if (Set(ref _headerTitle, value)) OnPropertyChanged(nameof(HeaderTileBrush)); } }
    /// <summary>Generated header artwork tile: per-title seeded gradient (the Windows Theme.MakeArt).</summary>
    public Avalonia.Media.IBrush HeaderTileBrush => AppTheme.ArtTileBrush(_headerTitle);
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

    /// <summary>Folder the sidebar "Open folder" button reveals: the iPod mount root, else the first Local Music folder.</summary>
    public string? OpenableFolder => IsLocalView ? _localFolders.FirstOrDefault() : _device?.MountRoot;

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
        _searchTimer?.Stop();   // a pending debounce must not re-render the search over the view we're navigating to
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

    /// <summary>Click-to-rate: set (or clear) a track's star rating on the iPod through the SAME verified write path
    /// as the Windows app (SafeDbWriter backup + verify + rollback). Clicking the current level — or left of ★1 —
    /// clears it. Updates just this row in place (no grid rebuild), so scroll + selection are kept. Writable iPod only.</summary>
    public void RateTrack(TrackRow? row, int star)
    {
        if (row?.Source is not { } t) return;
        // Local Music tracks aren't on the iPod (UniqueId==0) — EditTrack would no-op, so guard the VIEW, not just
        // CanWrite: otherwise a Local Music row would report "Rated" + run a needless DB rewrite that wrote nothing.
        if (!IsIpodView) { Status = "You can only rate songs that are on the iPod."; return; }
        if (_lib is null || _device is null || !_device.Profile.CanWrite)
        {
            Status = _device is null ? "Connect a writable iPod to rate songs." : "This iPod is read-only.";
            return;
        }
        int cur = Math.Clamp(t.Rating / 20, 0, 5);
        int stars = (star == 0 || star == cur) ? 0 : Math.Clamp(star, 0, 5);   // toggle-to-clear like the Windows list
        byte rating = (byte)(stars * 20);
        try
        {
            if (!_lib.EditTrack(t.UniqueId, new TrackEdit { Rating = rating }))   // honour the write: a no-op must not "succeed"
            {
                Status = "That song isn't on the iPod.";
                return;
            }
            _lib.Save();
        }
        catch (Exception ex) { Status = "Saving the rating failed (a backup was kept): " + ex.Message; return; }
        t.Rating = rating;              // keep the engine's track in sync for display + the toggle-clear compare
        row.RatingValue = stars;        // live-update just this row
        Status = stars > 0 ? $"Rated “{t.DisplayTitle}” {stars}★." : $"Cleared the rating for “{t.DisplayTitle}”.";
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
    private DispatcherTimer? _searchTimer;
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!Set(ref _searchText, value)) return;
            // Debounce: a fast typist would otherwise rebuild the whole list + restart the artwork sweep on EVERY
            // keystroke. Coalesce to one render ~150ms after typing pauses. (Nav-driven clears call RenderCurrent
            // directly, so results still appear instantly when switching views.)
            _searchTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            _searchTimer.Stop();
            _searchTimer.Tick -= OnSearchTick;
            _searchTimer.Tick += OnSearchTick;
            _searchTimer.Start();
        }
    }
    private void OnSearchTick(object? sender, EventArgs e) { _searchTimer?.Stop(); RenderCurrent(); }

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
        Tracks.ResetTo(shown.Select(TrackRow.From));   // one Reset, not Clear + N Adds — the DataGrid rebuilds once
        long ms = shown.Sum(t => (long)t.LengthMs);
        HeaderKicker = _curKicker;
        HeaderTitle = _curTitle;
        HeaderSubtitle = $"{shown.Count} {_curNoun}{(shown.Count == 1 ? "" : "s")} · {FormatTotal(ms)}";
        Status = _localView
            ? (_localFolders.Count == 0 ? "Click “Add folder” to add music from your PC."
               : $"{shown.Count} songs · {_localFolders.Count} folder{(_localFolders.Count == 1 ? "" : "s")}")
            : HeaderSubtitle;

        UpdateEmptyState(shown.Count, q);
        LoadArtwork(Tracks.ToList());
    }

    // ---- in-content empty state (overlaid on the song grid when the current view has no rows) ----
    private bool _showEmpty;         public bool ShowEmptyState    { get => _showEmpty;       set => Set(ref _showEmpty, value); }
    private string _emptyGlyph = ""; public string EmptyGlyph      { get => _emptyGlyph;      set => Set(ref _emptyGlyph, value); }
    private string _emptyHead = "";  public string EmptyHeadline   { get => _emptyHead;       set => Set(ref _emptyHead, value); }
    private string _emptyHint = "";  public string EmptyHint       { get => _emptyHint;       set => Set(ref _emptyHint, value); }
    private bool _ctaFolder;         public bool EmptyAddFolderCta { get => _ctaFolder;       set => Set(ref _ctaFolder, value); }
    private bool _ctaMusic;          public bool EmptyAddMusicCta  { get => _ctaMusic;        set => Set(ref _ctaMusic, value); }
    private bool _ctaClear;          public bool EmptyClearSearchCta { get => _ctaClear;      set => Set(ref _ctaClear, value); }

    private void UpdateEmptyState(int count, string query)
    {
        EmptyAddFolderCta = EmptyAddMusicCta = EmptyClearSearchCta = false;
        if (count > 0) { ShowEmptyState = false; return; }
        if (query.Length > 0)
        {
            EmptyGlyph = "⌕"; EmptyHeadline = "No matches"; EmptyHint = $"No songs match “{query}”."; EmptyClearSearchCta = true;
        }
        else if (_localView)
        {
            EmptyGlyph = "♪";
            if (_localFolders.Count == 0) { EmptyHeadline = "No music yet"; EmptyHint = "Add a folder of music from your PC to browse it here."; }
            else { EmptyHeadline = "No playable audio"; EmptyHint = "None of the files in your folders are a supported audio format."; }
            EmptyAddFolderCta = true;
        }
        else if (_lib is null || _device is null)
        {
            EmptyGlyph = "▣"; EmptyHeadline = "No iPod connected";
            EmptyHint = "Plug in an iPod to browse its songs — or open a PC folder under “On this PC”.";
        }
        else
        {
            EmptyGlyph = "♪"; EmptyHeadline = "This iPod has no songs yet";
            EmptyHint = "Copy music from your PC to fill it up."; EmptyAddMusicCta = CanWrite;
        }
        ShowEmptyState = true;
    }

    private Bitmap? _headerArt;
    public Bitmap? HeaderArt { get => _headerArt; set => Set(ref _headerArt, value); }

    private int _artGen;
    private async void LoadArtwork(IReadOnlyList<TrackRow> rows)
    {
        // async void: a post-await throw would reach the dispatcher and crash the process — so a failed decode of
        // one cover must never take the app down. Swallow per-row art errors (the row just keeps its placeholder).
        int gen = ++_artGen;
        HeaderArt = null;
        bool headerSet = false;
        foreach (var r in rows)
        {
            if (gen != _artGen) return;                  // a newer view replaced this one
            try
            {
                var t = r.Source;
                if (t is null) continue;
                string? path = t.LocalPath ?? (_device is not null ? t.ResolveFilePath(_device.MountRoot) : null);
                if (string.IsNullOrEmpty(path)) continue;
                string key = string.IsNullOrEmpty(t.Album) ? path : (Norm(t.Artist) + "|" + Norm(t.Album));
                // Probe the cache BEFORE touching the filesystem: an all-cached re-render (the common search-refine
                // case) then costs zero File.Exists syscalls. Only a genuine miss pays the stat + decode.
                if (ArtLoader.TryGet(key, out var cached))
                {
                    if (cached is not null) { r.Art = cached; if (!headerSet) { HeaderArt = cached; headerSet = true; } }
                    continue;
                }
                if (!File.Exists(path)) continue;
                var bmp = await ArtLoader.LoadAsync(path, key);
                if (gen != _artGen) return;
                if (bmp is not null)
                {
                    r.Art = bmp;
                    if (!headerSet) { HeaderArt = bmp; headerSet = true; }   // header tile shows the first cover found
                }
            }
            catch { /* skip this row's art */ }
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
        // async void: guard the whole body so a post-await throw can't reach the dispatcher and crash the app.
        try
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
        catch (Exception ex) { Status = "Couldn't load your music: " + ex.Message; }
    }

    private static string FormatTotal(long ms)
    {
        var t = TimeSpan.FromMilliseconds(ms);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours} hr {t.Minutes} min"
            : t.TotalMinutes >= 1 ? $"{t.Minutes} min"
            : $"{t.Seconds} s";
    }

    // ============================ playback engine (LibVLC via AudioService) ============================
    // Mirrors the Windows player: a play CONTEXT (the view we started from) drives sequential/shuffle
    // advance; an explicit Up-Next QUEUE overrides it forward (FIFO); nav HISTORY drives Prev; Repeat
    // cycles Off→All→One (One restarts only at track-end, manual Next still advances). Pure-random
    // shuffle each step (no bag), only guaranteeing i!=current. No play-count writes on advance.
    public enum RepeatMode { Off, All, One }

    private AudioService? _audio;
    private DispatcherTimer? _tick;
    private bool _updatingFromTimer;
    private readonly float[] _eqGains = { 5, 4, 2, 0, -1, -1, 0, 2, 4, 5 }; // gentle "smile" preset
    private static readonly Random _rng = new();

    private readonly List<TrackRow> _ctx = new();       // play context snapshot (the view playback started from)
    private int _ctxIndex = -1;                          // index of the now-playing row within _ctx
    private readonly List<TrackRow> _queue = new();      // explicit "Up Next" override (takes priority forward)
    private readonly List<TrackRow> _history = new();    // departed rows, newest last → Prev retraces the real path
    private TrackRow? _nowRow;

    private bool _hasNow;   public bool HasNowPlaying { get => _hasNow; set => Set(ref _hasNow, value); }
    private bool _isPlaying; public bool IsPlaying { get => _isPlaying; set => Set(ref _isPlaying, value); }   // drives play↔pause morph
    private Bitmap? _nowArt; public Bitmap? NowArt { get => _nowArt; set => Set(ref _nowArt, value); }
    private string _nowTitle = ""; public string NowTitle { get => _nowTitle; set => Set(ref _nowTitle, value); }
    private string _nowSub = "";   public string NowSub { get => _nowSub; set => Set(ref _nowSub, value); }
    private string _posText = "0:00"; public string PosText { get => _posText; set => Set(ref _posText, value); }
    private string _durText = "0:00"; public string DurText { get => _durText; set => Set(ref _durText, value); }

    private double _posFrac;
    public double PositionFraction
    {
        get => _posFrac;
        set { if (Set(ref _posFrac, value) && !_updatingFromTimer) _audio?.SeekFraction(value); }
    }

    private int _volume = 90, _preMuteVolume = 90;
    public int Volume
    {
        get => _volume;
        set { if (Set(ref _volume, value)) { if (_audio is not null) _audio.Volume = value; if (value > 0 && _muted) { _muted = false; OnPropertyChanged(nameof(Muted)); } } }
    }
    private bool _muted;
    public bool Muted { get => _muted; set => Set(ref _muted, value); }
    public void ToggleMute()
    {
        if (_muted) { Muted = false; Volume = _preMuteVolume > 0 ? _preMuteVolume : 60; }
        else { _preMuteVolume = _volume; Muted = true; Volume = 0; }
    }

    private bool _eqOn;
    public bool EqOn { get => _eqOn; set { if (Set(ref _eqOn, value)) _audio?.SetEq(value, EqGains); } }
    public float[] EqGains => _eqGains;

    /// <summary>One EQ band, bound to a vertical slider in the equalizer flyout; moving it writes into the
    /// shared gain array and re-applies the EQ live (auto-enabling it, like the Windows EqualizerDialog).</summary>
    public sealed class EqBand : INotifyPropertyChanged
    {
        private readonly int _i; private readonly MainViewModel _vm; private double _gain;
        public EqBand(MainViewModel vm, int i, string label, double gain) { _vm = vm; _i = i; Label = label; _gain = gain; }
        public string Label { get; }
        public double Gain
        {
            get => _gain;
            set { if (Math.Abs(_gain - value) > 0.001) { _gain = value; _vm._eqGains[_i] = (float)value; if (!_vm.EqOn) _vm.EqOn = true; else _vm.ApplyEq(); PropertyChanged?.Invoke(this, new(nameof(Gain))); } }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
    public ObservableCollection<EqBand> EqBands { get; } = new();
    private void BuildEqBands()
    {
        EqBands.Clear();
        for (int i = 0; i < AudioService.BandCount; i++)
        {
            int hz = AudioService.BandFrequencies[i];
            EqBands.Add(new EqBand(this, i, hz >= 1000 ? $"{hz / 1000}k" : hz.ToString(), _eqGains[i]));
        }
    }
    /// <summary>Re-apply the EQ after a band slider moved (the EQ flyout mutates <see cref="EqGains"/> in place).</summary>
    public void ApplyEq() { if (_eqOn) _audio?.SetEq(true, _eqGains); }
    /// <summary>Flat preset: zero every band (and refresh the sliders).</summary>
    public void EqFlat() { for (int i = 0; i < _eqGains.Length; i++) _eqGains[i] = 0; BuildEqBands(); ApplyEq(); }

    // ---- shuffle / repeat (persisted to the shared settings.json, like the Windows app) ----
    private bool _shuffle;
    public bool Shuffle
    {
        get => _shuffle;
        set { if (Set(ref _shuffle, value)) { AppConfig.SaveModes(_shuffle, _repeat.ToString()); RebuildUpNext(); } }
    }
    private RepeatMode _repeat = RepeatMode.Off;
    public RepeatMode Repeat
    {
        get => _repeat;
        set { if (Set(ref _repeat, value)) { OnPropertyChanged(nameof(RepeatActive)); OnPropertyChanged(nameof(RepeatOne)); AppConfig.SaveModes(_shuffle, _repeat.ToString()); } }
    }
    public bool RepeatActive => _repeat != RepeatMode.Off;   // tints the repeat glyph accent
    public bool RepeatOne => _repeat == RepeatMode.One;      // shows the "1" overlay
    public void CycleRepeat() => Repeat = (RepeatMode)(((int)_repeat + 1) % 3);
    public void ToggleShuffle() => Shuffle = !_shuffle;

    /// <summary>Restore the saved shuffle/repeat once, at construction (without re-saving).</summary>
    private void RestoreModes(bool shuffle, string repeat)
    {
        _shuffle = shuffle;
        _repeat = repeat == "All" ? RepeatMode.All : repeat == "One" ? RepeatMode.One : RepeatMode.Off;
        OnPropertyChanged(nameof(Shuffle)); OnPropertyChanged(nameof(Repeat));
        OnPropertyChanged(nameof(RepeatActive)); OnPropertyChanged(nameof(RepeatOne));
    }

    /// <summary>Start playback of <paramref name="row"/>, snapshotting the CURRENT view as the play context.</summary>
    public void PlayRow(TrackRow? row)
    {
        if (row is null) return;
        _ctx.Clear();
        _ctx.AddRange(Tracks);
        _ctxIndex = _ctx.IndexOf(row);
        _history.Clear();
        PlayInternal(row);
    }

    private string? ResolvePath(TrackRow row)
    {
        var t = row.Source;
        if (t is null) return null;
        string? path = t.LocalPath;
        if (string.IsNullOrEmpty(path) && _device is not null) path = t.ResolveFilePath(_device.MountRoot);
        return path;
    }

    private void PlayInternal(TrackRow row)
    {
        var t = row.Source;
        if (t is null) return;
        string? path = ResolvePath(row);
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
            _nowRow = row;
            NowTitle = t.DisplayTitle;
            NowSub = string.Join("  —  ", new[] { t.Artist, t.Album }.Where(s => !string.IsNullOrEmpty(s)));
            NowArt = row.Art;
            HasNowPlaying = true;
            IsPlaying = true;
            _tick!.Start();
            Status = "Playing: " + t.DisplayTitle;
            RebuildUpNext();
        }
        catch (Exception ex) { Status = "Playback error: " + ex.Message; }
    }

    public void PlayPause()
    {
        if (_audio is null) return;
        _audio.TogglePause();
        IsPlaying = _audio.IsPlaying;
    }

    // ---- next / previous / auto-advance ----
    private bool Playable(TrackRow row)
    {
        var p = ResolvePath(row);
        return !string.IsNullOrEmpty(p) && File.Exists(p);
    }

    public void Next() => Advance(+1, auto: false);
    public void Previous() => Advance(-1, auto: false);

    private void OnTrackEnded()
    {
        // Repeat One restarts the SAME track; otherwise auto-advance forward (Off/All handled by Advance's wrap).
        if (_repeat == RepeatMode.One && _nowRow is not null) { PlayInternal(_nowRow); return; }
        Advance(+1, auto: true);
    }

    private void Advance(int dir, bool auto)
    {
        if (_nowRow is null || _ctx.Count == 0) return;

        if (dir > 0)
        {
            // (1) explicit queue wins — pop the head; drop unplayable queued rows.
            while (_queue.Count > 0)
            {
                var q = _queue[0]; _queue.RemoveAt(0);
                if (Playable(q)) { Depart(); SyncCtxIndex(q); PlayInternal(q); RebuildUpNext(); return; }
            }
            // (2) shuffle — a pure random playable pick that isn't the current row.
            if (_shuffle)
            {
                var pool = new List<int>();
                for (int i = 0; i < _ctx.Count; i++) if (i != _ctxIndex && Playable(_ctx[i])) pool.Add(i);
                if (pool.Count > 0) { int pick = pool[_rng.Next(pool.Count)]; Depart(); _ctxIndex = pick; PlayInternal(_ctx[pick]); RebuildUpNext(); return; }
                if (_repeat == RepeatMode.One) { PlayInternal(_nowRow); return; }
            }
        }
        else
        {
            // Prev retraces the real path via history first (correct under shuffle).
            while (_history.Count > 0)
            {
                var prev = _history[^1]; _history.RemoveAt(_history.Count - 1);
                int hi = _ctx.IndexOf(prev);
                if (hi >= 0 && Playable(prev)) { _ctxIndex = hi; PlayInternal(prev); RebuildUpNext(); return; }
            }
        }

        // (3) sequential scan in the given direction.
        for (int i = _ctxIndex + dir; i >= 0 && i < _ctx.Count; i += dir)
            if (Playable(_ctx[i])) { if (dir > 0) Depart(); _ctxIndex = i; PlayInternal(_ctx[i]); RebuildUpNext(); return; }

        // (4) edge: Repeat All wraps; otherwise rest paused on the current track.
        if (_repeat == RepeatMode.All)
        {
            int start = dir > 0 ? 0 : _ctx.Count - 1;
            for (int i = start; i >= 0 && i < _ctx.Count; i += dir)
                if (Playable(_ctx[i])) { if (dir > 0) Depart(); _ctxIndex = i; PlayInternal(_ctx[i]); RebuildUpNext(); return; }
        }
        if (auto) IsPlaying = false;   // reached the end with Repeat Off → stop
    }

    private void Depart() { if (_nowRow is not null) { _history.Add(_nowRow); if (_history.Count > 200) _history.RemoveAt(0); } }
    private void SyncCtxIndex(TrackRow row) { int i = _ctx.IndexOf(row); if (i >= 0) _ctxIndex = i; }

    private void EnsureAudio()
    {
        if (_audio is not null) return;
        _audio = new AudioService();
        _audio.Ended += () => Dispatcher.UIThread.Post(OnTrackEnded);   // marshal off the VLC thread (no sync player calls there)
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
        IsPlaying = _audio.IsPlaying;
    }

    // ---- Up Next queue (explicit "Play next" / "Add to queue") ----
    public sealed class QueueRow
    {
        public string Title { get; init; } = "";
        public string Sub { get; init; } = "";
        public Bitmap? Art { get; init; }
        public Avalonia.Media.IBrush Tile { get; init; } = Avalonia.Media.Brushes.Transparent;
        public bool IsNowPlaying { get; init; }
        internal TrackRow? Row { get; init; }
    }
    public ObservableCollection<QueueRow> UpNext { get; } = new();
    private int _queueCount; public int QueueCount { get => _queueCount; set { if (Set(ref _queueCount, value)) OnPropertyChanged(nameof(HasQueue)); } }
    public bool HasQueue => _queueCount > 0;
    private string _upNextHint = ""; public string UpNextHint { get => _upNextHint; set => Set(ref _upNextHint, value); }

    private static QueueRow ToQueueRow(TrackRow r, bool now) => new()
    {
        Title = r.Title,
        Sub = string.Join("  ·  ", new[] { r.Artist, r.Album }.Where(s => !string.IsNullOrEmpty(s))),
        Art = r.Art,
        Tile = AppTheme.ArtTileBrush(string.IsNullOrEmpty(r.Album) ? r.Title : r.Album),
        IsNowPlaying = now,
        Row = r,
    };

    private void RebuildUpNext()
    {
        UpNext.Clear();
        if (_nowRow is not null) UpNext.Add(ToQueueRow(_nowRow, now: true));
        foreach (var q in _queue) UpNext.Add(ToQueueRow(q, now: false));
        // then the upcoming context rows (skip ones already in the explicit queue) — a preview of what plays next
        if (!_shuffle)
            for (int i = _ctxIndex + 1; i < _ctx.Count; i++)
                if (!_queue.Contains(_ctx[i])) UpNext.Add(ToQueueRow(_ctx[i], now: false));
        QueueCount = _queue.Count;
        UpNextHint = _queue.Count > 0 && _repeat == RepeatMode.One ? "Repeat One is on — Next still steps the queue." : "";
    }

    public void QueuePlayNext(IEnumerable<TrackRow> rows)
    {
        int i = 0; bool any = false;
        foreach (var r in rows) if (!_queue.Contains(r)) { _queue.Insert(i++, r); any = true; }
        if (any) RebuildUpNext();
    }
    public void QueueAdd(IEnumerable<TrackRow> rows)
    {
        bool any = false;
        foreach (var r in rows) if (!_queue.Contains(r)) { _queue.Add(r); any = true; }
        if (any) RebuildUpNext();
    }
    // ---- Cover Flow ---------------------------------------------------------------------------------
    public bool CoverFlowAvailable => Tracks.Count > 0;

    /// <summary>Build the deck for a browse mode from the CURRENT view's rows. Songs = one card per track;
    /// Albums / Artists = one card per group (sorted, seeded tile + the group's first decoded cover). Returns
    /// the cards, the index to centre on (the now-playing item, else 0), and the Tag to mark "Now Playing".</summary>
    public (List<Controls.CoverFlowView.CoverItem> items, int start, object? playing) BuildCoverFlow(string mode)
    {
        var rows = Tracks.ToList();
        var items = new List<Controls.CoverFlowView.CoverItem>();
        object? playing = null;
        int start = 0;

        static string AlbumKey(TrackRow r) => Norm(r.Album);
        static string ArtistKey(TrackRow r) => Norm(r.Artist);

        if (mode == "Songs")
        {
            foreach (var r in rows.OrderBy(r => r.Title, StringComparer.OrdinalIgnoreCase))
            {
                items.Add(new(r.Art, AppTheme.ArtTileBrush(string.IsNullOrEmpty(r.Album) ? r.Title : r.Album),
                    r.Title, string.Join("   ·   ", new[] { r.Artist, r.Album }.Where(s => !string.IsNullOrEmpty(s))), r));
                if (ReferenceEquals(r, _nowRow)) { playing = r; start = items.Count - 1; }
            }
        }
        else if (mode == "Artists")
        {
            foreach (var g in rows.GroupBy(ArtistKey).OrderBy(g => g.Key == "" ? "￿" : g.Key, StringComparer.OrdinalIgnoreCase))
            {
                var rep = g.First();
                string name = string.IsNullOrEmpty(rep.Artist) ? "Unknown Artist" : rep.Artist;
                int albums = g.Select(AlbumKey).Distinct().Count();
                items.Add(new(rep.Art, AppTheme.ArtTileBrush(name), name,
                    $"{albums} album{(albums == 1 ? "" : "s")}   ·   {g.Count()} song{(g.Count() == 1 ? "" : "s")}", rep));
                if (_nowRow is not null && ArtistKey(_nowRow) == g.Key) { playing = rep; start = items.Count - 1; }
            }
        }
        else // Albums
        {
            foreach (var g in rows.GroupBy(AlbumKey).OrderBy(g => g.First().Album ?? "", StringComparer.OrdinalIgnoreCase))
            {
                var rep = g.First();
                string name = string.IsNullOrEmpty(rep.Album) ? "Unknown Album" : rep.Album;
                items.Add(new(rep.Art, AppTheme.ArtTileBrush(name), name,
                    $"{(string.IsNullOrEmpty(rep.Artist) ? "Unknown Artist" : rep.Artist)}   ·   {g.Count()} song{(g.Count() == 1 ? "" : "s")}", rep));
                if (_nowRow is not null && AlbumKey(_nowRow) == g.Key) { playing = rep; start = items.Count - 1; }
            }
        }
        return (items, start, playing);
    }

    /// <summary>Activate a cover: play its representative row (context = the current view, so Next/Prev walk it).</summary>
    public void CoverFlowActivate(object? tag) { if (tag is TrackRow r) PlayRow(r); }

    public void QueueClear() { if (_queue.Count > 0) { _queue.Clear(); RebuildUpNext(); } }
    public void QueueRemove(TrackRow? r) { if (r is not null && _queue.Remove(r)) RebuildUpNext(); }
    public void JumpTo(TrackRow? r)
    {
        if (r is null) return;
        if (_queue.Remove(r)) { Depart(); SyncCtxIndex(r); PlayInternal(r); RebuildUpNext(); return; }
        int i = _ctx.IndexOf(r);
        if (i >= 0 && Playable(r)) { Depart(); _ctxIndex = i; PlayInternal(r); RebuildUpNext(); }
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
