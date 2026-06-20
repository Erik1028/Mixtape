using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace iPodCommander;

/// <summary>
/// Mixtape's main window, styled after Apple Music: a left <see cref="Sidebar"/> (device +
/// playlists), a <see cref="HeaderPanel"/> (artwork + title + actions), and a refined track
/// table. Writes are gated on <see cref="DeviceProfile.CanWrite"/> and go through
/// <see cref="SafeDbWriter"/> (backup + verify + rollback).
/// </summary>
internal sealed class MainForm : Form, IMessageFilter
{
    private readonly Sidebar _sidebar = new() { Dock = DockStyle.Fill };
    private readonly HeaderPanel _header = new() { Dock = DockStyle.Fill, Margin = Padding.Empty };   // no cell margin → fills to the card's corners (so the AA corner-carve rounds the real corner)
    private readonly SmoothGrid _tracks = new() { Dock = DockStyle.None };   // full-height; scrolled by Top inside _trackViewport
    private readonly ThinScrollBar _scrollbar = new() { Dock = DockStyle.Right };
    private readonly Panel _trackViewport = new() { Dock = DockStyle.Fill };  // clips the (taller) grid
    private TrackHeader? _trackHeader;                                        // fixed column header (the grid's own is hidden)
    private int _hotRow = -1;
    private int _dragRow = -1, _dropRow = -1, _dragStartY; // drag-to-reorder a playlist's tracks
    private bool _rowDragging;
    private readonly List<Track> _navHistory = new(); // tracks navigated away from, so Prev (esp. in shuffle) can step back
    private readonly PlayQueue _queue = new();        // "Up Next" — takes priority over shuffle/sequential selection

    private readonly List<IPodDevice> _devices = new();
    private readonly List<Playlist> _shownPlaylists = new();
    private IPodDevice? _device;
    private IpodLibrary? _lib;
    private ITunesDb? _db;
    private Playlist? _current;
    private bool _writeConfirmed;
    private readonly bool _autoDetect;
    private readonly AppSettings _settings = AppSettings.Load();
    private int _artGen;          // bumped each ShowCurrent; cancels stale background art loads
    private int _sidebarArtGen;   // same, for sidebar playlist covers
    private int _photoArtGen;     // same, for the photo grid's background thumbnail decode
    private PhotoLibrary? _photoShownLib;   // which library the photo grid currently holds decoded thumbs for…
    private int _photoShownGen = -1;        // …at which Generation, so a revisit can reuse it instead of re-decoding
    private int _sortCol = -1;    // -1 = playlist order; 1=Song 2=Artist 3=Album 4=Rating 5=Plays 6=Added 7=Time
    private bool _sortAsc = true;
    private static readonly string[] ColBase = { "", "SONG", "ARTIST", "ALBUM", "RATING", "PLAYS", "ADDED", "TIME" };
    private string _emptyMsg = ""; // shown centred when the song list has no rows
    private string _baseStatus = ""; // the view's normal status line (restored when a multi-selection clears)
    private bool _baseStatusClickable; // true when _baseStatus is the clickable warning line
    private bool _populatingGrid;    // suppress selection-status churn while rows are being added
    private SidebarRowKind _viewKind = SidebarRowKind.AllSongs; // which top-level view is active
    private PhotoLibrary? _photos;
    private readonly PhotoGridView _photoView = new() { Dock = DockStyle.Fill, Visible = false };
    private MiniSlider _photoSize = null!;   // photo-grid size slider in the header (Photos view only)
    private readonly Panel _deviceView = new() { Dock = DockStyle.Fill, Visible = false, BackColor = Color.FromArgb(29, 30, 34) };           // clipping viewport
    private readonly Panel _deviceScrollPanel = new() { BackColor = Color.FromArgb(29, 30, 34), Location = new Point(0, 0) }; // scrolled content (moved by its Top; no native bar)
    private readonly ThinScrollBar _deviceScroll = new();                                                                                       // the app's slim dark scrollbar
    private WallpaperPanel? _root;             // gradient shell + caption strip (custom title bar)
    private TableLayoutPanel? _content;        // kept so a theme-variant change can recolour it
    private Panel? _center;                     // the swappable centre region (animated on view switches)
    private bool _viewTransitionBusy;          // guards against overlapping centre transitions
    private const ViewTransition ViewStyle = ViewTransition.Slide;   // the motion for view switches — flip to Fade / Scale to restyle
    // Auto-detect on plug/unplug: WM_DEVICECHANGE kicks this; it fires once after the burst settles + the
    // volume has finished mounting, then re-scans for iPods.
    private readonly System.Windows.Forms.Timer _deviceChangeTimer = new() { Interval = 900 };
    private string? _ejectedRoot; // after a manual eject, ignore this drive in auto-detect until a fresh plug-in
    private readonly SearchBox _search = new() { Dock = DockStyle.Fill };
    private string _searchQuery = "";
    private bool _navigating; // suppresses the search box's redundant ShowCurrent while we clear it on navigation
    private bool _userSorted; // true once the user clicks a column header (don't snap back to the default sort)
    private bool _currentHasCustomCover; // the active view has a chosen cover art → don't override it with a track thumbnail
    private readonly NowPlayingBar _nowPlaying = new() { Dock = DockStyle.Fill, Margin = Padding.Empty };   // no cell margin → reaches the card's bottom corners (and gets its full H=88, not H-6)
    private Track? _playingTrack; // the track in the now-playing bar (for prev/next within the visible list)
    private readonly BrowseGridView _browseView = new() { Dock = DockStyle.Fill, Visible = false };
    private Panel _gridHost = null!;          // the song-list host (a Dock=Fill centre sibling); set in BuildLayout
    private Func<Track, bool>? _browseFilter; // when set, the song grid is drilled into one album/artist
    private CoverFlowView? _coverFlow;        // immersive album browser overlay (lazily created)
    private UpNextFlyout? _upNext;            // "Up Next" queue popover (floating, rounded; open while non-null)
    private int _upNextClosedTick;            // when it last closed — so clicking the queue button to dismiss doesn't instantly reopen
    private readonly System.Windows.Forms.Timer _backdropTimer = new() { Interval = 90 };  // debounce the frosted now-playing-bar recapture
    private CoverFlowView.BrowseMode _cfMode = CoverFlowView.BrowseMode.Albums; // Cover Flow: songs / albums / artists
    private bool _cfLocal;                    // Cover Flow is browsing the PC library (Local Music), not the iPod
    private Bitmap? _cfPlaceholder;           // shared "loading" cover shown until real art streams in
    private int _cfGen;                       // bumps to cancel a previous Cover-Flow art-load pass
    private string _browseTitle = "", _browseKicker = "ALBUM";
    private int _browseArtGen; // cancels stale background cover loads for the album/artist grid
    private const int SidebarIconPx = 36; // render playlist icons at 2× then downscale crisply into the 18px tile
    private DropOverlay? _dropOverlay; // "drop to add" card shown over the content while files are dragged in
    private readonly System.Windows.Forms.Timer _dropHideTimer = new() { Interval = 130 }; // debounce hiding it between controls
    private readonly System.Windows.Forms.Timer _searchDebounce = new() { Interval = 140 }; // collapse a burst of keystrokes into one grid rebuild
    // A whisper-faint row divider, reused across every row + paint (was allocated per row, per repaint, during scroll).
    private readonly Pen _rowDividerPen = new(Theme.Blend(Theme.Bg, Color.White, 0.03));
    // Inline click-to-rate in the RATING column (col 4): owner-drawn stars with a ghost hover preview.
    private const int RatingCol = 4, RatingPadX = 8, RatingStarW = 16;
    private int _ratingHotRow = -1, _ratingHotStar = -1;   // row + star slot (0=clear, 1-5) under the cursor
    private readonly Font _ratingFont = Theme.UiFont(11.5f);
    // Drag selected songs from the grid onto a sidebar playlist (reuses AddSelectedToPlaylist).
    private const string SongDragFormat = "MixtapeSongIds";
    private bool _songDragArmed;
    private Point _songDragStart;
    private int _songDragRow = -1;

    public MainForm(bool autoDetect = true)
    {
        _autoDetect = autoDetect;
        Text = "Mixtape";
        Font = Theme.UiFont(9f);
        BackColor = Theme.Bg;
        ForeColor = Theme.TextCol;
        ClientSize = new Size(1040, 660);
        MinimumSize = new Size(820, 520);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.None; // custom title bar — chrome handled in WndProc/CreateParams
        try { if (Environment.ProcessPath is string p) Icon = System.Drawing.Icon.ExtractAssociatedIcon(p); } catch { }
        Theme.SetAccent(_settings.Accent); // apply saved accent before any control styles bake it in
        Theme.SetThemeVariant(_settings.ThemeVariant); // and the background palette, before controls bake their BackColors
        SeedDefaultSort();

        BuildLayout();

        _sidebar.RowActivated += OnSidebarActivated;
        _sidebar.RefreshClicked += RefreshDevices;
        _sidebar.OpenFolderClicked += OpenFolder;
        _sidebar.SettingsClicked += OpenSettings;
        _sidebar.EjectClicked += _ => EjectDevice();
        _sidebar.PlayFileClicked += OnPlayLocalFile;
        _sidebar.RowRightClicked += OnSidebarRightClick;
        _sidebar.PlaylistAreaRightClicked += OnPlaylistAreaRightClick;
        _sidebar.AllowDrop = true;                            // accept songs dragged from the grid
        _sidebar.DragEnter += OnSidebarDragOver;
        _sidebar.DragOver += OnSidebarDragOver;
        _sidebar.DragLeave += (_, _) => _sidebar.SetDropHighlight(null);
        _sidebar.DragDrop += OnSidebarDragDrop;
        _header.AddButton.Click += (_, _) => OnAddClicked();
        _header.DeleteButton.Click += (_, _) => OnDeleteClicked();
        _header.CoverButton.Click += (_, _) => OpenCoverFlow();
        _header.AddButton.BlockedClicked += _ => ShowActionBlockedHelp();
        _header.DeleteButton.BlockedClicked += _ => ShowActionBlockedHelp();
        _header.ArtClicked += OnHeaderArtClicked;

        WireDragDrop(); // drop music/video/photo files (or folders) anywhere on the window to add them

        if (_autoDetect) Shown += (_, _) => RefreshDevices();

        Application.AddMessageFilter(this); // route the mouse wheel to the device page's custom scroll
        _deviceChangeTimer.Tick += (_, _) => { _deviceChangeTimer.Stop(); AutoDetectDevices(); };
        _dropHideTimer.Tick += (_, _) => { _dropHideTimer.Stop(); SetDropActive(false); };

        // Field-initialized controls (header, search box, sidebar) baked the DEFAULT theme's colours before the
        // saved variant was applied in this ctor — push the current colours in so a saved non-default theme is
        // correct from the first frame, not just after a Settings change.
        RecolorBakedControls();
    }

    /// <summary>Route the mouse wheel to the device page's custom scroll when the pointer is over it (its
    /// cards would otherwise swallow the wheel, since the page isn't a native scroll container).</summary>
    bool IMessageFilter.PreFilterMessage(ref Message m)
    {
        const int WM_MOUSEWHEEL = 0x020A;
        if (m.Msg != WM_MOUSEWHEEL) return false;
        int delta = (short)((long)m.WParam >> 16);

        // Sidebar list: a non-focusable Panel never receives WM_MOUSEWHEEL on its own, so route it here when
        // the pointer is over the rail (otherwise overflowing playlists are unreachable by wheel).
        if (_sidebar.IsHandleCreated)
        {
            var sp = _sidebar.PointToClient(Cursor.Position);
            if (sp.X >= 0 && sp.Y >= 0 && sp.X < _sidebar.ClientSize.Width && sp.Y < _sidebar.ClientSize.Height)
            {
                _sidebar.ScrollByWheel(delta);
                return true;
            }
        }

        if (_viewKind != SidebarRowKind.Device || !_deviceView.Visible || !_deviceView.IsHandleCreated) return false;
        var p = _deviceView.PointToClient(Cursor.Position);
        if (p.X < 0 || p.Y < 0 || p.X >= _deviceView.ClientSize.Width || p.Y >= _deviceView.ClientSize.Height) return false;
        ScrollDeviceBy(-Math.Sign(delta) * 80);
        return true; // handled
    }

    private void ScrollDeviceBy(int px)
    {
        int max = Math.Max(0, _deviceScrollPanel.Height - _deviceView.ClientSize.Height);
        int next = Math.Min(max, Math.Max(0, -_deviceScrollPanel.Top + px));
        _deviceScrollPanel.Top = -next;
        _deviceScroll.Invalidate();
    }

    private const int Gap = 14;        // wallpaper gap around + between the floating cards
    private const int CardRadius = Theme.RadShell; // rounded card corners
    private const int CaptionH = 36;   // the custom title-bar strip height
    private const int ResizeBorder = 6;
    private readonly WindowButton _btnMini = new() { Which = WindowButton.Kind.MiniPlayer };
    private readonly WindowButton _btnMin = new() { Which = WindowButton.Kind.Minimize };
    private readonly WindowButton _btnMax = new() { Which = WindowButton.Kind.Maximize };
    private readonly WindowButton _btnClose = new() { Which = WindowButton.Kind.Close };
    private MiniPlayerForm? _mini;            // iTunes-style detached mini player (lazily created)
    private Track? _miniTrack;                // last track pushed to the mini (avoids re-cloning cover each tick)

    private void BuildLayout()
    {
        // The shell floats two rounded cards (sidebar + content) over a themed gradient wallpaper, with
        // our own caption strip on top (the native title bar is removed in WndProc). Manual layout in
        // LayoutShell() positions the cards + window buttons so the wallpaper shows through the gaps.
        // No empty title strip any more — the cards float right up under a thin draggable wallpaper gap, and the
        // window buttons live in the header's top-right. CaptionHeight = that top gap (still native-draggable).
        var root = _root = new WallpaperPanel { Dock = DockStyle.Fill, CaptionHeight = Gap, ResizeBorder = ResizeBorder };

        var content = _content = new TableLayoutPanel { Dock = DockStyle.None, ColumnCount = 1, RowCount = 3, BackColor = Theme.Bg, Margin = new Padding(0) };
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 142));   // content header — now also hosts the window buttons (top-right), so it's a touch taller
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, NowPlayingBar.H));   // now-playing bar — always the bottom row (idle state when nothing plays)

        SetupTrackGrid();
        ApplyColumns();
        var gridHost = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(22, 10, 8, 8) };
        _gridHost = gridHost;
        // The search box now lives in the header (top-right, left of the action-button stack).
        _search.Dock = DockStyle.None;
        _header.Search = _search;
        _header.Controls.Add(_search);

        // Photo-grid size slider (shown only on the Photos view, above the Add/Delete buttons).
        _photoView.TileSize = Math.Clamp(_settings.PhotoTileSize, 96, 240);
        _photoSize = new MiniSlider { Minimum = 96, Maximum = 240, Value = _photoView.TileSize, Width = 150, Height = 28, Visible = false };
        _photoSize.Changed += v => { _photoView.TileSize = v; _settings.PhotoTileSize = v; };   // live resize
        _photoSize.Committed += _ => _settings.Save();                                            // persist on release
        _header.SizeSlider = _photoSize;
        _header.Controls.Add(_photoSize);
        _search.Changed += q =>
        {
            _searchQuery = q.Trim();
            if (_navigating) return;
            // Debounce: each rebuild re-filters the whole library + tears down and repopulates the grid, so a
            // burst of keystrokes should collapse into ONE rebuild (on a big library this is the search lag).
            _searchDebounce.Stop();
            _searchDebounce.Start();
        };
        _searchDebounce.Tick += (_, _) =>
        {
            _searchDebounce.Stop();
            if (_navigating) return;
            if (_viewKind == SidebarRowKind.LocalMusic) FillLocalGrid();   // filter the cached list — don't re-scan disk
            else if (_viewKind != SidebarRowKind.Photos && _viewKind != SidebarRowKind.Device) ShowCurrent();
        };
        // Song list: a fixed custom header on top + a clipping viewport holding the full-height grid,
        // which pixel-scrolls by moving its Top. The themed scrollbar drives it in panel mode.
        _trackViewport.BackColor = Theme.Bg;
        _trackHeader = new TrackHeader(_tracks) { Dock = DockStyle.Top };
        _trackHeader.SortRequested += DoSort;
        _trackViewport.Controls.Add(_tracks);       // manual bounds (full height), scrolled by Top
        _trackViewport.Controls.Add(_scrollbar);    // Right
        _scrollbar.AttachScrollPanel(_trackViewport, _tracks);
        _trackViewport.Resize += (_, _) => LayoutTrackViewport();
        // The header docks Top. The viewport is positioned MANUALLY below it — Dock=Fill here wrongly fills the
        // whole host and lets the header overlap the viewport's top ~34px (a real dock z-order quirk: DrawToBitmap
        // hid it by painting the Fill over the header, but on screen the header clips the first row, so you could
        // never scroll the top row fully into view). Manual placement keeps the list strictly below the header.
        _trackViewport.Dock = DockStyle.None;
        gridHost.Controls.Add(_trackHeader);        // Top (full width)
        gridHost.Controls.Add(_trackViewport);      // manual, below the header
        void LayoutGridHost()
        {
            var disp = gridHost.DisplayRectangle;   // padded content area
            int hh = _trackHeader.Height;
            _trackViewport.Bounds = new Rectangle(disp.X, disp.Y + hh, disp.Width, Math.Max(0, disp.Height - hh));
        }
        gridHost.SizeChanged += (_, _) => LayoutGridHost();
        _trackHeader.SizeChanged += (_, _) => LayoutGridHost();
        LayoutGridHost();

        // The track grid, photo grid and device page share the centre cell; only one shows at a time.
        var center = _center = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Margin = Padding.Empty };   // fill the middle cell with no gap (matches the zero-margin header/bar rows)
        center.Controls.Add(_deviceView);   // Fill, hidden until the Device view is active
        // Device page scrolls via a custom thin scrollbar: the content panel is moved by its Top inside the
        // clipping viewport (no AutoScroll → no native bar), and our slim bar is overlaid on the right.
        _deviceView.Controls.Add(_deviceScrollPanel);
        _deviceView.Controls.Add(_deviceScroll);
        _deviceScroll.AttachScrollPanel(_deviceView, _deviceScrollPanel);
        _deviceView.Resize += (_, _) => LayoutDeviceView();
        center.Controls.Add(_photoView);    // Fill, hidden until the Photos view is active
        center.Controls.Add(_browseView);   // Fill, hidden until the Albums/Artists view is active
        center.Controls.Add(gridHost);      // Fill
        _photoView.SelectionChanged += () => UpdatePhotoStatus();
        _photoView.ItemRightClicked += ShowPhotoMenu;
        _photoView.ItemActivated += OpenPhotoViewer;
        _browseView.ItemActivated += OnBrowseActivated;

        _header.StatusClicked += () => ShowDbWarnings();   // the status line (warnings) moved into the header

        _nowPlaying.PrevRequested += () => PlayRelative(-1);
        _nowPlaying.NextRequested += () => PlayRelative(+1);
        _nowPlaying.EqualizerRequested += OpenEqualizer;
        _nowPlaying.ProRequested += OpenProFeatures;
        _nowPlaying.ApplyEq(_settings.EqEnabled, _settings.EqGains ?? EqualizerSampleProvider.FlatGains()); // restore saved EQ
        _nowPlaying.ApplyPro(_settings.GaplessEnabled, _settings.CrossfadeSeconds, _settings.CrossfadeEnabled, _settings.NormalizeVolume, _settings.MonoOutput); // restore Pro features
        _nowPlaying.NextTrackProvider = PeekNextForGapless;   // supply the next track for gapless/crossfade prefetch
        _nowPlaying.AdvancedToNext += OnGaplessAdvancedToNext;
        _nowPlaying.QueueRequested += OpenUpNext;
        _queue.Changed += RefreshUpNext;
        _queue.JumpedToFront += () => _nowPlaying.InvalidatePrefetch();   // a late "Play next" must replace a committed prefetch
        _backdropTimer.Tick += (_, _) => CaptureBarBackdrop();           // frosted now-playing bar
        _tracks.LocationChanged += (_, _) => ScheduleBarBackdrop();      // recapture when the list scrolls (settles)
        _nowPlaying.SetModes(_settings.Shuffle, ParseRepeat(_settings.RepeatMode));                          // restore shuffle/repeat
        _nowPlaying.ModesChanged += () =>
        {
            _settings.Shuffle = _nowPlaying.Shuffle;
            _settings.RepeatMode = _nowPlaying.Repeat.ToString();
            _settings.Save();
        };

        content.Controls.Add(_header, 0, 0);
        content.Controls.Add(center, 0, 1);
        content.Controls.Add(_nowPlaying, 0, 2);   // now-playing bar is the bottom row

        _nowPlaying.Changed += PushMiniState;   // keep the detached mini player in sync (metadata / play-state / volume)
        _nowPlaying.Tick += PushMiniProgress;   // …and its seek bar (engine position ticks)

        _sidebar.Dock = DockStyle.None;
        root.Controls.Add(_sidebar);
        root.Controls.Add(content);
        _header.SetWindowButtons(_btnMini, _btnMin, _btnMax, _btnClose);   // hosted in the header's top-right now

        _btnMini.Click += (_, _) => OpenMiniPlayer();
        _btnMin.Click += (_, _) => WindowState = FormWindowState.Minimized;
        _btnMax.Click += (_, _) => WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;
        _btnClose.Click += (_, _) => Close();

        root.Resize += (_, _) => LayoutShell();
        Controls.Add(root);
        LayoutShell();

        // Drag-and-drop overlay floats above everything; it's its own drop target so it can cover the
        // song grid without blocking the drop. Force its handle so AllowDrop registers before any drag.
        _dropOverlay = new DropOverlay { Visible = false, AllowDrop = true };
        _dropOverlay.DragOver += OnDragOverAny;
        _dropOverlay.DragDrop += OnDragDrop;
        _dropOverlay.DragLeave += (_, _) => ScheduleHideDrop();
        Controls.Add(_dropOverlay);
        _ = _dropOverlay.Handle;
        _dropOverlay.BringToFront();
    }

    /// <summary>Positions the sidebar + content cards (with wallpaper gaps + rounded corners) and the
    /// window control buttons in the caption strip. Runs on every shell resize / maximize.</summary>
    private void LayoutShell()
    {
        if (_root is null || _content is null) return;
        int w = _root.ClientSize.Width, h = _root.ClientSize.Height;
        if (w <= 0 || h <= 0) return;

        const int sideW = 236;
        int top = Gap, bottom = h - Gap;
        _sidebar.Bounds = new Rectangle(Gap, top, sideW, Math.Max(1, bottom - top));
        int cx = Gap + sideW + Gap;
        _content.Bounds = new Rectangle(cx, top, Math.Max(1, w - cx - Gap), Math.Max(1, bottom - top));
        // Corners are rounded by ANTI-ALIASED carving in each card's own paint (Sidebar/HeaderPanel/NowPlayingBar
        // → Theme.CarveCardCorners), which samples the wallpaper-with-shadow bitmap — smooth, unlike a Region clip.
        _root.InvalidateWallpaper();   // card bounds moved → re-bake the wallpaper + shadow the carving samples
        if (_coverFlow is { Visible: true }) { LayoutCoverFlow(); _coverFlow.BringToFront(); }

        _btnMax.Maximized = WindowState == FormWindowState.Maximized;   // window buttons are positioned by the header now
        ScheduleBarBackdrop();   // size changed → refresh the frosted bar
    }

    private void SetupTrackGrid()
    {
        _tracks.AutoGenerateColumns = false;
        _tracks.ReadOnly = true;
        _tracks.AllowUserToAddRows = false;
        _tracks.AllowUserToDeleteRows = false;
        _tracks.AllowUserToResizeRows = false;
        _tracks.AllowUserToResizeColumns = true;   // drag column borders to resize (Fill columns adjust their weights)
        _tracks.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _tracks.MultiSelect = true;
        _tracks.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        Theme.StyleGrid(_tracks);
        _tracks.RowTemplate.Height = _settings.RowHeight;

        var art = new DataGridViewImageColumn { HeaderText = "", Width = 52, AutoSizeMode = DataGridViewAutoSizeColumnMode.None, ImageLayout = DataGridViewImageCellLayout.Zoom, SortMode = DataGridViewColumnSortMode.NotSortable, Visible = _settings.ListArtwork };
        _tracks.Columns.Add(art);

        var dimSel = Theme.Blend(Theme.Subtle, Color.White, 0.35); // secondary columns stay dimmer even when the row is selected

        var song = new DataGridViewTextBoxColumn { HeaderText = "SONG", FillWeight = 32, MinimumWidth = 70, SortMode = DataGridViewColumnSortMode.NotSortable };
        song.DefaultCellStyle.Padding = new Padding(8, 0, 4, 0);          // match the 8px header inset
        song.DefaultCellStyle.Font = Theme.UiFont(10f, FontStyle.Bold);  // song name leads
        _tracks.Columns.Add(song);

        var artist = new DataGridViewTextBoxColumn { HeaderText = "ARTIST", FillWeight = 24, MinimumWidth = 60, SortMode = DataGridViewColumnSortMode.NotSortable };
        artist.DefaultCellStyle.ForeColor = Theme.Subtle; artist.DefaultCellStyle.SelectionForeColor = dimSel;
        _tracks.Columns.Add(artist);

        var album = new DataGridViewTextBoxColumn { HeaderText = "ALBUM", FillWeight = 30, MinimumWidth = 60, SortMode = DataGridViewColumnSortMode.NotSortable };
        album.DefaultCellStyle.ForeColor = Theme.Subtle; album.DefaultCellStyle.SelectionForeColor = dimSel;
        _tracks.Columns.Add(album);

        var rating = new DataGridViewTextBoxColumn { HeaderText = "RATING", Width = 92, AutoSizeMode = DataGridViewAutoSizeColumnMode.None, SortMode = DataGridViewColumnSortMode.NotSortable, Visible = _settings.ShowRating };
        rating.DefaultCellStyle.ForeColor = Theme.Accent;                 // stars pop in the accent colour
        rating.DefaultCellStyle.SelectionForeColor = Color.White;
        rating.DefaultCellStyle.Padding = new Padding(8, 0, 4, 0);
        _tracks.Columns.Add(rating);

        var plays = new DataGridViewTextBoxColumn { HeaderText = "PLAYS", Width = 76, AutoSizeMode = DataGridViewAutoSizeColumnMode.None, SortMode = DataGridViewColumnSortMode.NotSortable, Visible = _settings.ShowPlays };
        plays.DefaultCellStyle.ForeColor = Theme.Subtle; plays.DefaultCellStyle.SelectionForeColor = dimSel;
        plays.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
        plays.DefaultCellStyle.Padding = new Padding(4, 0, 10, 0);
        plays.HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleRight;
        plays.HeaderCell.Style.Padding = new Padding(4, 0, 10, 0);
        _tracks.Columns.Add(plays);

        var added = new DataGridViewTextBoxColumn { HeaderText = "ADDED", Width = 112, AutoSizeMode = DataGridViewAutoSizeColumnMode.None, SortMode = DataGridViewColumnSortMode.NotSortable, Visible = _settings.ShowDateAdded };
        added.DefaultCellStyle.ForeColor = Theme.Subtle; added.DefaultCellStyle.SelectionForeColor = dimSel;
        added.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
        added.DefaultCellStyle.Padding = new Padding(4, 0, 10, 0);
        added.HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleRight;
        added.HeaderCell.Style.Padding = new Padding(4, 0, 10, 0);
        _tracks.Columns.Add(added);

        var time = new DataGridViewTextBoxColumn { HeaderText = "TIME", Width = 72, AutoSizeMode = DataGridViewAutoSizeColumnMode.None, SortMode = DataGridViewColumnSortMode.NotSortable };
        time.DefaultCellStyle.ForeColor = Theme.Subtle;
        time.DefaultCellStyle.SelectionForeColor = dimSel;
        time.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
        time.DefaultCellStyle.Padding = new Padding(4, 0, 14, 0);
        time.HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleRight;
        time.HeaderCell.Style.Padding = new Padding(4, 0, 14, 0);          // header right-edge matches the values
        _tracks.Columns.Add(time);

        _tracks.ColumnHeadersVisible = false;              // a fixed TrackHeader strip replaces it (so the grid can scroll whole)
        // Keep the full-content height current as rows change — but NOT once per row during a bulk populate
        // (that fired SizeTracks N times per view switch, each re-setting Height + re-clamping scroll). The
        // populate paths call SizeTracks() once after the loop instead.
        _tracks.RowsAdded += (_, _) => { if (!_populatingGrid) SizeTracks(); };
        _tracks.RowsRemoved += (_, _) => { if (!_populatingGrid) SizeTracks(); };
        _tracks.RowPrePaint += OnRowPrePaint;
        _tracks.RowPostPaint += OnRowPostPaint;
        _tracks.SelectionChanged += (_, _) => OnTrackSelectionChanged();
        _tracks.CellMouseEnter += (_, e) => SetHotRow(e.RowIndex);
        _tracks.MouseLeave += (_, _) => { SetHotRow(-1); ClearRatingHover(); };
        _tracks.MouseDown += OnTrackMouseDown;
        _tracks.MouseDown += OnReorderMouseDown;
        _tracks.MouseDown += OnSongDragMouseDown;              // arm a drag of the selection onto a playlist
        _tracks.MouseMove += OnReorderMouseMove;
        _tracks.MouseMove += OnSongDragMouseMove;
        _tracks.MouseUp += OnReorderMouseUp;
        _tracks.MouseUp += (_, _) => _songDragArmed = false;
        _tracks.CellPainting += OnRatingCellPainting;          // owner-draw the RATING column (stars + hover preview)
        _tracks.CellMouseMove += OnRatingCellMouseMove;        // ghost-star hover preview
        _tracks.CellMouseClick += OnRatingCellClick;           // click a star to set the rating inline
        _tracks.CellMouseLeave += (_, e) => { if (e.ColumnIndex == RatingCol) ClearRatingHover(); };
        _tracks.CellMouseDoubleClick += (_, e) => { if (e.ColumnIndex != RatingCol && e.RowIndex >= 0 && e.RowIndex < _tracks.Rows.Count) ActivateTrackRow(e.RowIndex); };
        _tracks.Paint += (_, e) =>
        {
            // friendly empty-state when the list has no rows (the grid fills the viewport when empty)
            if (_tracks.RowCount == 0 && _emptyMsg.Length > 0)
            {
                var area = new Rectangle(0, 0, _tracks.Width, _tracks.Height);
                TextRenderer.DrawText(e.Graphics, _emptyMsg, _tracks.Font, area, Theme.Faint,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
            }

            // drag-to-reorder: a teal insertion line at the drop position
            if (_rowDragging && _dropRow >= 0)
            {
                int y;
                if (_dropRow < _tracks.Rows.Count) y = _tracks.GetRowDisplayRectangle(_dropRow, false).Top;
                else { var last = _tracks.GetRowDisplayRectangle(_tracks.Rows.Count - 1, false); y = last.Bottom; }
                // AA on only now (the column-header hairline above stayed crisp 1px); the round bullet needs it.
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using var ip = new Pen(Theme.Accent, 2.5f);
                e.Graphics.DrawLine(ip, 2, y, _tracks.Width - 4, y);
                using var db = new SolidBrush(Theme.Accent);
                e.Graphics.FillEllipse(db, 0, y - 3, 6, 6);
            }
        };
    }

    private void OnRowPrePaint(object? sender, DataGridViewRowPrePaintEventArgs e)
    {
        // Fill the FULL row width with its background BEFORE the cells paint. In the live (double-buffered)
        // paint, DataGridView's per-cell fill leaves a 1px sliver of the un-cleared (black) buffer at the
        // fractional Fill-column boundaries — that sliver reads as a faint vertical "grid" seam (Bg darkened
        // ~26%). DrawToBitmap tiles columns exactly so it never shows there. An opaque base fill turns those
        // slivers into Bg instead of black, killing the seam without touching the (intentional) row dividers.
        if (e.RowIndex < 0 || e.RowIndex >= _tracks.Rows.Count) return;
        Color bg = _tracks.Rows[e.RowIndex].Selected ? Theme.Blend(Theme.Bg, Theme.Accent, 0.12)
            : e.RowIndex == _hotRow ? Theme.RowHover : Theme.Bg;
        using var b = new SolidBrush(bg);
        e.Graphics.FillRectangle(b, e.RowBounds);
    }

    private void OnRowPostPaint(object? sender, DataGridViewRowPostPaintEventArgs e)
    {
        var b = e.RowBounds;
        // Start the divider at the Song column, leaving the artwork cell clean — but reach the row's
        // left edge when the artwork column is hidden, so dividers don't start indented in a ragged gap.
        var artCol = _tracks.Columns[0];
        int x0 = b.X + (artCol.Visible ? artCol.Width : 0);
        // A whisper-faint row divider — just enough to separate tracks without reading as a grid.
        // Draw it FIRST on integer bounds with AA off so it stays a true crisp 1px.
        e.Graphics.DrawLine(_rowDividerPen, x0, b.Bottom - 1, b.Right, b.Bottom - 1);
        // Selection is carried by one crisp, bright accent bar (the row fill itself only whispers a tint).
        if (e.RowIndex >= 0 && e.RowIndex < _tracks.Rows.Count && _tracks.Rows[e.RowIndex].Selected)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var bar = new SolidBrush(Theme.AccentBright);
            using var bp = Theme.RoundedRect(new RectangleF(b.X, b.Y + 1, 4, b.Height - 2), 2);
            e.Graphics.FillPath(bar, bp);
        }
    }

    // ---- inline click-to-rate (RATING column) ----

    private bool CanEditRatings => _lib is not null && _device is not null && _device.Profile.CanWrite;

    private void ClearRatingHover()
    {
        if (_ratingHotRow < 0) return;
        int row = _ratingHotRow; _ratingHotRow = -1; _ratingHotStar = -1;
        if (row < _tracks.Rows.Count) _tracks.InvalidateCell(RatingCol, row);
    }

    /// <summary>Which star (1-5) a cell-relative X falls on; 0 = the clear zone left of the first star.</summary>
    private static int RatingStarFromX(int xInCell)
    {
        int rel = xInCell - RatingPadX;
        return rel < 0 ? 0 : Math.Clamp(rel / RatingStarW + 1, 0, 5);
    }

    private void OnRatingCellMouseMove(object? sender, DataGridViewCellMouseEventArgs e)
    {
        if (e.ColumnIndex != RatingCol || e.RowIndex < 0 || !CanEditRatings) { ClearRatingHover(); return; }
        int star = RatingStarFromX(e.X);
        if (_ratingHotRow == e.RowIndex && _ratingHotStar == star) return;
        int prev = _ratingHotRow;
        _ratingHotRow = e.RowIndex; _ratingHotStar = star;
        if (prev >= 0 && prev != e.RowIndex && prev < _tracks.Rows.Count) _tracks.InvalidateCell(RatingCol, prev);
        _tracks.InvalidateCell(RatingCol, e.RowIndex);
    }

    private void OnRatingCellClick(object? sender, DataGridViewCellMouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || e.ColumnIndex != RatingCol || e.RowIndex < 0 || e.RowIndex >= _tracks.Rows.Count) return;
        if (_tracks.Rows[e.RowIndex].Tag is not Track t) return;
        if (!CanEditRatings) { if (_device is not null) SetStatus(Loc.T("This iPod is read-only — {0}", _device.Profile.WriteBlockReason)); return; }
        int star = RatingStarFromX(e.X);
        int curStars = Math.Min(5, t.Rating / 20);
        byte newRating = star == 0 || star == curStars ? (byte)0 : (byte)(star * 20);  // click the current level (or left of ★1) to clear
        if (newRating == t.Rating) return;
        SetRatingInline(t, e.RowIndex, newRating);
    }

    /// <summary>Owner-draw the RATING cell: filled stars for the saved rating, a ghost-star preview on hover.
    /// Fixed star geometry (RatingPadX + i·RatingStarW) so the hit-test in <see cref="RatingStarFromX"/> lines up.</summary>
    private void OnRatingCellPainting(object? sender, DataGridViewCellPaintingEventArgs e)
    {
        if (e.ColumnIndex != RatingCol || e.RowIndex < 0 || e.RowIndex >= _tracks.Rows.Count) return;
        if (_tracks.Rows[e.RowIndex].Tag is not Track t) return;
        var g = e.Graphics!;
        // Background already painted by OnRowPrePaint (full-row fill) — just draw stars over it.
        int curStars = Math.Min(5, t.Rating / 20);
        bool hovering = CanEditRatings && e.RowIndex == _ratingHotRow;
        int shown = hovering ? _ratingHotStar : curStars;     // how many filled stars
        bool ghosts = hovering || curStars > 0;               // empty slots only when rated or hovering
        bool selected = _tracks.Rows[e.RowIndex].Selected;
        Color fill = hovering ? Theme.AccentBright : selected ? Color.White : Theme.Accent;
        var cb = e.CellBounds;
        for (int i = 0; i < 5; i++)
        {
            if (i >= shown && !ghosts) break;
            var slot = new Rectangle(cb.X + RatingPadX + i * RatingStarW, cb.Y, RatingStarW, cb.Height);
            TextRenderer.DrawText(g, i < shown ? "★" : "☆", _ratingFont, slot, i < shown ? fill : Theme.Faint,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }
        e.Handled = true;
    }

    /// <summary>Apply a rating to one track through the verified write path, then refresh just its cell so the
    /// list keeps its scroll + selection (no full rebuild). UniqueId is stable across a simple rewrite.</summary>
    private void SetRatingInline(Track t, int rowIndex, byte rating)
    {
        if (!CanEditRatings || !ConfirmWriteOnce()) return;
        try
        {
            Cursor = Cursors.WaitCursor;
            _lib!.EditTrack(t.UniqueId, new TrackEdit { Rating = rating });
            _lib.Save();
        }
        catch (Exception ex)
        {
            MessageDialog.Show(this, Loc.T("Saving failed (a backup was kept as iTunesDB.bak):\n\n{0}", ex.Message), "Mixtape", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        finally { Cursor = Cursors.Default; }
        t.Rating = rating;   // keep the grid's track object in sync for display
        if (rowIndex >= 0 && rowIndex < _tracks.Rows.Count) { _tracks.Rows[rowIndex].Cells[RatingCol].Value = RatingStars(rating); _tracks.InvalidateRow(rowIndex); }
        SetStatus(rating > 0 ? Loc.T("Rated “{0}” {1}★.", t.DisplayTitle, rating / 20) : Loc.T("Cleared the rating for “{0}”.", t.DisplayTitle));
    }

    /// <summary>Sort by a clicked header column (asc → desc → off), like the old grid header click.</summary>
    private void DoSort(int colIndex)
    {
        if (colIndex < 1 || colIndex >= ColBase.Length) return; // artwork column isn't sortable
        if (_sortCol == colIndex) { if (_sortAsc) _sortAsc = false; else _sortCol = -1; } // asc → desc → off
        else { _sortCol = colIndex; _sortAsc = true; }
        _userSorted = true; // an explicit sort choice — don't let a settings change snap it back
        ShowCurrent();
    }

    /// <summary>Keep the full-height grid sized to the viewport (width) and its content (height).</summary>
    private void LayoutTrackViewport()
    {
        int w = Math.Max(0, _trackViewport.ClientSize.Width - _scrollbar.Width);
        _tracks.Left = 0;
        if (_tracks.Width != w) _tracks.Width = w;
        SizeTracks();
        _trackHeader?.Invalidate();
        ApplyColumns();
    }

    /// <summary>Set the grid's height to its full content (so it scrolls as one block), re-clamping the scroll.</summary>
    private void SizeTracks()
    {
        int rowH = Math.Max(1, _tracks.RowTemplate.Height);
        int h = Math.Max(_trackViewport.ClientSize.Height, _tracks.RowCount * rowH);
        if (_tracks.Height != h) _tracks.Height = h;
        _tracks.SetScrollTop(_tracks.Top);
        ScheduleBarBackdrop();   // list content changed → refresh the frosted bar
    }

    // ---- drag-to-reorder a playlist's tracks ----

    /// <summary>Reordering is allowed only inside a writable user playlist shown in its natural (unsorted, unfiltered) order.</summary>
    private bool CanReorderPlaylist =>
        _viewKind == SidebarRowKind.Playlist && _current is not null && !_current.IsPodcast
        && _device?.Profile.CanWrite == true && _sortCol < 1 && _searchQuery.Length == 0;

    private void OnReorderMouseDown(object? sender, MouseEventArgs e)
    {
        _rowDragging = false;
        if (e.Button != MouseButtons.Left || !CanReorderPlaylist) { _dragRow = -1; return; }
        _dragRow = _tracks.HitTest(e.X, e.Y).RowIndex; // -1 if not on a row
        _dragStartY = e.Y;
    }

    private void OnReorderMouseMove(object? sender, MouseEventArgs e)
    {
        if (_dragRow < 0 || (e.Button & MouseButtons.Left) == 0) return;
        if (!_rowDragging)
        {
            if (Math.Abs(e.Y - _dragStartY) < 5) return; // small move = a click, not a drag
            _rowDragging = true;
            _tracks.Cursor = Cursors.SizeNS;
        }
        int viewTop = -_tracks.Top, viewBot = viewTop + _trackViewport.ClientSize.Height; // e.Y is grid-relative
        if (e.Y < viewTop + 18) ScrollGrid(-1);                          // auto-scroll near the viewport edges
        else if (e.Y > viewBot - 18) ScrollGrid(+1);
        _dropRow = DropIndexAt(e.Y);
        _tracks.Invalidate();
    }

    private void OnReorderMouseUp(object? sender, MouseEventArgs e)
    {
        if (_rowDragging && _dragRow >= 0 && _dropRow >= 0) CommitReorder(_dragRow, _dropRow);
        _rowDragging = false; _dragRow = -1; _dropRow = -1;
        _tracks.Cursor = Cursors.Default;
        _tracks.Invalidate();
    }

    /// <summary>The insert position (0..count) for a drop at vertical position <paramref name="y"/>.</summary>
    private int DropIndexAt(int y)
    {
        for (int i = 0; i < _tracks.Rows.Count; i++)
        {
            var r = _tracks.GetRowDisplayRectangle(i, false);
            if (r.Height == 0) continue; // off-screen
            if (y < r.Top + r.Height / 2) return i;
        }
        return _tracks.Rows.Count; // past the last visible row → end
    }

    private void ScrollGrid(int dir) => _tracks.ScrollByPixels(dir * Math.Max(1, _tracks.RowTemplate.Height));

    // ---- drag selected songs from the grid onto a sidebar playlist ----

    private void OnSongDragMouseDown(object? sender, MouseEventArgs e)
    {
        _songDragArmed = false;
        // Reorder owns the left-drag inside a reorderable playlist; elsewhere a left-drag means "add to a playlist".
        if (e.Button != MouseButtons.Left || CanReorderPlaylist) return;
        if (_lib is null || _device is null || !_device.Profile.CanWrite) return;   // nothing to add to without a writable iPod
        int row = _tracks.HitTest(e.X, e.Y).RowIndex;
        if (row < 0) return;
        _songDragRow = row; _songDragStart = e.Location; _songDragArmed = true;
    }

    private void OnSongDragMouseMove(object? sender, MouseEventArgs e)
    {
        if (!_songDragArmed || (e.Button & MouseButtons.Left) == 0) return;
        if (Math.Abs(e.X - _songDragStart.X) < 5 && Math.Abs(e.Y - _songDragStart.Y) < 5) return; // a click, not a drag
        _songDragArmed = false;
        var ids = SongDragIds(_songDragRow);
        if (ids.Count == 0) return;
        try { _tracks.DoDragDrop(new DataObject(SongDragFormat, ids), DragDropEffects.Copy); } catch { }
    }

    /// <summary>Track ids for a song-drag: the whole pre-click selection if the grabbed row was part of it, else just it.</summary>
    private List<uint> SongDragIds(int grabbedRow)
    {
        var snap = _tracks.PreClickSelection;
        var rows = snap.Count > 1 && snap.Contains(grabbedRow) ? snap : new List<int> { grabbedRow };
        var ids = new List<uint>();
        foreach (int i in rows) if (i >= 0 && i < _tracks.Rows.Count && _tracks.Rows[i].Tag is Track t) ids.Add(t.UniqueId);
        return ids;
    }

    /// <summary>The valid playlist drop target for the current sidebar drag (writable user playlist), or null.</summary>
    private Playlist? SidebarDropTarget(DragEventArgs e)
    {
        if (e.Data?.GetDataPresent(SongDragFormat) != true || _db is null || _device is null || !_device.Profile.CanWrite) return null;
        var pl = _sidebar.PlaylistAtPoint(_sidebar.PointToClient(new Point(e.X, e.Y)));
        if (pl is null || ReferenceEquals(pl, _db.Master) || pl.IsPodcast || pl.PersistentId == 0) return null;
        return pl;
    }

    private void OnSidebarDragOver(object? sender, DragEventArgs e)
    {
        var pl = SidebarDropTarget(e);
        e.Effect = pl is not null ? DragDropEffects.Copy : DragDropEffects.None;
        _sidebar.SetDropHighlight(pl);
    }

    private void OnSidebarDragDrop(object? sender, DragEventArgs e)
    {
        var pl = SidebarDropTarget(e);
        _sidebar.SetDropHighlight(null);
        if (pl is null || e.Data?.GetData(SongDragFormat) is not List<uint> ids || ids.Count == 0) return;
        AddSelectedToPlaylist(pl, ids);
    }

    /// <summary>Queue a (debounced) recapture of the frosted now-playing-bar backdrop — coalesces bursts of
    /// scroll/layout events into one capture once things settle.</summary>
    private void ScheduleBarBackdrop() { _backdropTimer.Stop(); _backdropTimer.Start(); }

    /// <summary>Snapshot the bottom strip of the active center view (the list above the bar), shrink it hard, and
    /// hand it to the bar as a frosted-glass blur. Debounced via <see cref="_backdropTimer"/> so it never runs in
    /// the smooth-scroll hot path.</summary>
    private void CaptureBarBackdrop()
    {
        _backdropTimer.Stop();
        if (_center is null || !_center.Visible || _center.Width <= 4 || _center.Height <= 4) { _nowPlaying.SetBackdrop(null); return; }
        int h = NowPlayingBar.H;
        try
        {
            using var strip = new Bitmap(_center.Width, h);
            _center.DrawToBitmap(strip, new Rectangle(0, h - _center.Height, _center.Width, _center.Height)); // negative offset → keep only the bottom strip
            var tiny = new Bitmap(72, 9);
            using (var g = Graphics.FromImage(tiny))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                g.DrawImage(strip, new Rectangle(0, 0, 72, 9));
            }
            _nowPlaying.SetBackdrop(tiny);   // bar owns + disposes it
        }
        catch { /* capture is best-effort; the bar falls back to its gradient */ }
    }

    private void CommitReorder(int from, int to)
    {
        if (_lib is null || _current is null) return;
        var order = new List<uint>(_tracks.Rows.Count);
        foreach (DataGridViewRow row in _tracks.Rows) if (row.Tag is Track t) order.Add(t.UniqueId);
        if (from < 0 || from >= order.Count) return;
        if (to > from) to--;                       // removing the source shifts the rest up
        to = Math.Clamp(to, 0, order.Count - 1);
        if (to == from) return;                    // dropped back where it started
        uint moved = order[from];
        order.RemoveAt(from);
        order.Insert(to, moved);

        if (!ConfirmWriteOnce()) return;
        bool ok;
        try { Cursor = Cursors.WaitCursor; ok = _lib.ReorderPlaylist(_current, order); if (ok) _lib.Save(); }
        catch (Exception ex) { MessageDialog.Show(this, Loc.T("Write failed (a backup was kept as iTunesDB.bak):\n\n{0}", ex.Message), "Mixtape", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
        finally { Cursor = Cursors.Default; }
        if (!ok) { ShowPlaylistNotEditable(); return; }
        ReloadAfterEdit();
        SetStatus(Loc.T("Playlist order updated."));
    }

    private void SetHotRow(int row)
    {
        if (row == _hotRow) return;
        if (_hotRow >= 0 && _hotRow < _tracks.Rows.Count) _tracks.Rows[_hotRow].DefaultCellStyle.BackColor = Theme.Bg;
        _hotRow = row;
        if (_hotRow >= 0 && _hotRow < _tracks.Rows.Count) _tracks.Rows[_hotRow].DefaultCellStyle.BackColor = Theme.RowHover;
    }

    // ---- in-app media preview (play songs / watch videos & photos straight off the iPod) ----

    /// <summary>Double-click / "Play" on a track row: audio → now-playing bar; video → preview window.</summary>
    private void ActivateTrackRow(int rowIndex)
    {
        if (_tracks.Rows[rowIndex].Tag is not Track t) return;
        string? path = ResolvePlayPath(t);
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            MessageDialog.Show(this, Loc.T("The file for this item can't be found, so it can't be played."), Loc.T("Play"), MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (MediaType.IsVideo(t.MediaType)) OpenVideoPreview(t, path);
        else PlayAudio(t, path, rowIndex);
    }

    /// <summary>The on-disk path to play: a Local-Music track carries its own absolute path; iPod tracks resolve via the mount.</summary>
    private string? ResolvePlayPath(Track t) => t.LocalPath ?? (_device is null ? null : t.ResolveFilePath(_device.MountRoot));

    private void PlayAudio(Track t, string path, int rowIndex)
    {
        Bitmap? cover = null;
        if (rowIndex >= 0 && rowIndex < _tracks.Rows.Count && _tracks.Rows[rowIndex].Cells[0].Value is Image img)
            try { cover = new Bitmap(img); } catch { cover = null; }
        if (rowIndex >= 0) _tracks.EnsureRowVisible(rowIndex);   // scroll the now-playing row into view
        _playingTrack = t;
        SetNowPlayingVisible(true); // expand the row first so the hosted media engine is realized before playing
        _nowPlaying.Play(t, path, cover);
        cover?.Dispose(); // the bar took its own copy
        if (_coverFlow is not null && MediaType.IsAudio(t.MediaType)) _coverFlow.PlayingTag = CoverTag(t, _cfMode); // mark it in Cover Flow (per current mode)
        RefreshUpNext();   // update the Up Next panel's Now-Playing row
    }

    private void OpenVideoPreview(Track t, string path)
    {
        bool wasPlaying = _nowPlaying.Pause(); // don't play audio and video at once
        using (var dlg = new VideoPreviewDialog(path, t.DisplayTitle)) dlg.ShowDialog(this);
        if (wasPlaying) _nowPlaying.Resume(); // pick the song back up where it left off
    }

    private int RowIndexOf(Track t)
    {
        for (int i = 0; i < _tracks.Rows.Count; i++) if (ReferenceEquals(_tracks.Rows[i].Tag, t)) return i;
        return -1;
    }

    /// <summary>Can this row be played now (audio that resolves to an existing file)?</summary>
    private bool IsPlayableRow(int i, out Track track, out string path)
    {
        track = null!; path = "";
        if (i < 0 || i >= _tracks.Rows.Count || _tracks.Rows[i].Tag is not Track t || MediaType.IsVideo(t.MediaType)) return false;
        string? p = ResolvePlayPath(t);
        if (string.IsNullOrEmpty(p) || !File.Exists(p)) return false;
        track = t; path = p; return true;
    }

    /// <summary>Advance/retreat in the visible list, honoring shuffle + repeat. Prev steps back through
    /// navigation history (important in shuffle); videos and missing files are skipped.</summary>
    private void PlayRelative(int dir)
    {
        if (_playingTrack is null || _tracks.Rows.Count == 0) { _nowPlaying.StopAndHide(); _playingTrack = null; return; }

        // Prev: replay the most recent still-present track from history before falling back to list order.
        if (dir < 0)
        {
            while (_navHistory.Count > 0)
            {
                var prev = _navHistory[^1]; _navHistory.RemoveAt(_navHistory.Count - 1);
                int ri = RowIndexOf(prev);
                if (ri >= 0 && IsPlayableRow(ri, out var pt, out var pp)) { PlayAudio(pt, pp, ri); return; }
            }
        }

        int cur = RowIndexOf(_playingTrack);

        // Up Next queue takes priority (forward only; Prev never consumes the queue).
        if (dir > 0)
        {
            while (_queue.Peek() is Track q)
            {
                int qi = RowIndexOf(q);
                if (qi >= 0 && IsPlayableRow(qi, out _, out _)) { _queue.Dequeue(); PlayFromNav(cur, qi); return; }
                _queue.Dequeue();   // no longer in the current view / unplayable → drop and try the next
            }
        }

        // Shuffle (forward only): jump to a random other playable row.
        if (dir > 0 && _nowPlaying.Shuffle)
        {
            var pool = new List<int>();
            for (int i = 0; i < _tracks.Rows.Count; i++) if (i != cur && IsPlayableRow(i, out _, out _)) pool.Add(i);
            if (pool.Count > 0) { PlayFromNav(cur, pool[Random.Shared.Next(pool.Count)]); return; }
            // Current track is the only playable one: under Repeat One, keep it going rather than dead-stopping.
            if (_nowPlaying.Repeat == NowPlayingBar.RepeatMode.One && IsPlayableRow(cur, out var st, out var sp)) { PlayAudio(st, sp, cur); return; }
        }

        // Sequential scan in the requested direction.
        if (cur < 0) cur = dir > 0 ? -1 : _tracks.Rows.Count;
        for (int i = cur + dir; i >= 0 && i < _tracks.Rows.Count; i += dir)
            if (IsPlayableRow(i, out _, out _)) { PlayFromNav(cur, i); return; }

        // Hit an edge: wrap around when Repeat All is on, otherwise rest (paused) on the current track.
        if (_nowPlaying.Repeat == NowPlayingBar.RepeatMode.All)
        {
            int start = dir > 0 ? 0 : _tracks.Rows.Count - 1;
            for (int i = start; i >= 0 && i < _tracks.Rows.Count; i += dir)
                if (IsPlayableRow(i, out _, out _)) { PlayFromNav(cur, i); return; }
        }
    }

    /// <summary>Play row <paramref name="to"/>, pushing the row we left (<paramref name="from"/>) onto nav history.</summary>
    private void PlayFromNav(int from, int to)
    {
        if (from >= 0 && from < _tracks.Rows.Count && _tracks.Rows[from].Tag is Track ft)
        {
            _navHistory.Add(ft);
            if (_navHistory.Count > 200) _navHistory.RemoveAt(0);
        }
        if (IsPlayableRow(to, out var t, out var p)) PlayAudio(t, p, to);
    }

    private static NowPlayingBar.RepeatMode ParseRepeat(string? s) => s switch
    {
        "All" => NowPlayingBar.RepeatMode.All,
        "One" => NowPlayingBar.RepeatMode.One,
        _ => NowPlayingBar.RepeatMode.Off,
    };

    private void SetNowPlayingVisible(bool on)
    {
        if (_content is null) return;
        _content.RowStyles[2].Height = NowPlayingBar.H;   // always visible; the bar shows an idle state when nothing plays
    }

    private void OpenPhotoViewer(uint startId)
    {
        if (_photos is null) return;
        var photos = _photos.Photos.ToList();
        var byId = photos.ToDictionary(p => p.ImageId);
        var ids = photos.Select(p => p.ImageId).ToList();
        int start = ids.IndexOf(startId);
        if (start < 0) return;
        var lib = _photos;
        using var dlg = new PhotoViewerDialog(ids, start,
            id => byId.TryGetValue(id, out var p) ? lib.RenderFull(p) : null,
            id => byId.TryGetValue(id, out var p) ? PhotoCaption(p, photos.IndexOf(p)) : null);
        dlg.ShowDialog(this);
    }

    private static string PhotoCaption(Photo p, int index) =>
        p.Date is DateTime d && d.Year > 1970 ? d.ToString("yyyy. MM. dd. HH:mm") : Loc.T("Photo {0}", index + 1);

    /// <summary>The selected track rows as Track objects, in visible (top-to-bottom) order.</summary>
    private List<Track> SelectedTracks()
    {
        var rows = new List<(int Index, Track T)>();
        foreach (DataGridViewRow r in _tracks.SelectedRows) if (r.Tag is Track t) rows.Add((r.Index, t));
        rows.Sort((a, b) => a.Index.CompareTo(b.Index));
        return rows.Select(x => x.T).ToList();
    }

    /// <summary>Add "Play next" / "Add to queue" for the selected rows (not iPod-DB writes — offered everywhere).</summary>
    private void AddQueueMenuItems(ContextMenuStrip m)
    {
        var sel = SelectedTracks();
        if (sel.Count == 0) return;
        var pn = new ToolStripMenuItem(sel.Count > 1 ? Loc.T("Play next   ({0})", sel.Count) : Loc.T("Play next"));
        pn.Click += (_, _) => _queue.PlayNext(sel);
        m.Items.Add(pn);
        var aq = new ToolStripMenuItem(sel.Count > 1 ? Loc.T("Add to queue   ({0})", sel.Count) : Loc.T("Add to queue"));
        aq.Click += (_, _) => _queue.Add(sel);
        m.Items.Add(aq);
    }

    /// <summary>Edit the first selected track's tags + star rating, write it back, and refresh the list.</summary>
    private void OnEditTrackInfo()
    {
        if (_lib is null || _device is null || !_device.Profile.CanWrite) return;
        var sel = SelectedTracks();
        if (sel.Count == 0) { SetStatus(Loc.T("Select a song to edit.")); return; }

        using var dlg = new TrackInfoDialog(sel);   // one song, or the whole selection (shared fields applied to all)
        if (dlg.ShowDialog(this) != DialogResult.OK || !dlg.HasChanges) return;
        if (!ConfirmWriteOnce()) return;

        var edit = dlg.Edit;
        Exception? error = null;
        using (var prog = new CopyProgressDialog(Loc.T("Saving song info"), sel.Count, (report, cancelled) =>
        {
            for (int i = 0; i < sel.Count; i++)
            {
                report(i, sel.Count == 1 ? Loc.T("Updating “{0}”", sel[i].DisplayTitle) : Loc.T("Updating {0} of {1} songs…", i + 1, sel.Count));
                _lib!.EditTrack(sel[i].UniqueId, edit);
            }
            _lib!.Save();
            report(sel.Count, Loc.T("Done"));
        }))
        {
            prog.ShowDialog(this);
            error = prog.Error;
        }
        ReloadAfterEdit();
        if (error is not null)
            MessageDialog.Show(this, Loc.T("Saving failed (a backup was kept as iTunesDB.bak):\n\n{0}", error.Message), "Mixtape", MessageBoxButtons.OK, MessageBoxIcon.Error);
        else
            SetStatus(sel.Count == 1 ? Loc.T("Updated “{0}”.", edit.Title ?? sel[0].DisplayTitle) : Loc.T("Updated {0} songs.", sel.Count));
    }

    /// <summary>Copy the selected songs off the iPod to a folder on the PC (Artist/Album/NN Title), retagged.</summary>
    private void OnExportSelected()
    {
        if (_device is null) return;
        var tracks = SelectedTracks();
        if (tracks.Count == 0) { SetStatus(Loc.T("Select one or more songs to copy to the PC.")); return; }
        using var dlg = new FolderBrowserDialog { Description = Loc.T("Copy {0} song(s) from the iPod into this folder", tracks.Count), ShowNewFolderButton = true };
        if (dlg.ShowDialog(this) != DialogResult.OK || string.IsNullOrEmpty(dlg.SelectedPath)) return;
        string dest = dlg.SelectedPath, mount = _device.MountRoot;

        int ok = 0, missing = 0;
        var errors = new List<string>();
        using var prog = new CopyProgressDialog(Loc.T("Copying songs to your PC"), tracks.Count, (report, cancelled) =>
        {
            for (int i = 0; i < tracks.Count; i++)
            {
                if (cancelled()) break;
                var t = tracks[i];
                report(i, Loc.T("Copying {0} of {1}   ·   {2}", i + 1, tracks.Count, t.DisplayTitle));
                try { if (MusicExporter.ExportOne(t, mount, dest, organize: true, applyTags: true) is null) missing++; else ok++; }
                catch (Exception ex) { errors.Add($"{t.DisplayTitle}: {ex.Message}"); }
            }
        });
        prog.ShowDialog(this);

        string msg = prog.WasCancelled ? Loc.T("Stopped — copied {0} song(s).", ok) : Loc.T("Copied {0} song(s) to:\n{1}", ok, dest);
        if (missing > 0) msg += Loc.T("\n\n{0} had no file on the iPod (skipped).", missing);
        if (errors.Count > 0) msg += Loc.T("\n\n{0} failed:\n• ", errors.Count) + string.Join("\n• ", errors.Take(8));
        MessageDialog.Show(this, msg, Loc.T("Copy to PC"), MessageBoxButtons.OK, errors.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
    }

    // ---- detection / device load ----

    private bool _scanning; // a drive scan (DetectAll) is in flight on a background thread

    private async void RefreshDevices()
    {
        if (_scanning) return;
        _scanning = true;
        _ejectedRoot = null; // an explicit Refresh means the user wants to re-detect everything
        if (_device is null) ShowScanning(); // instant feedback on first open — never a blank, frozen window
        try
        {
            // Scan drives off the UI thread: enumerating drives can stall for seconds on an empty card
            // reader or optical drive, which would otherwise freeze the just-opened window.
            var found = await Task.Run(() => DeviceDetector.DetectAll().ToList());
            if (IsDisposed) return;
            _devices.Clear();
            _devices.AddRange(found);
            if (_devices.Count > 0) LoadDevice(_devices[0]);
            else ShowNoDevice();
        }
        catch (Exception ex) { if (!IsDisposed) { SetStatus(Loc.T("Detection error: {0}", ex.Message)); ShowNoDevice(); } }
        finally { _scanning = false; }
    }

    /// <summary>Transient "scanning for an iPod" state shown while the background drive scan runs.</summary>
    private void ShowScanning()
    {
        _device = null; _lib = null; _db = null; _current = null; _photos = null;
        _viewKind = SidebarRowKind.AllSongs;
        SetCenter();
        _tracks.Rows.Clear();
        _emptyMsg = "";
        _header.SetInfo("", Loc.T("Looking for your iPod…"), Loc.T("Scanning for a connected iPod."), 0);
        SetActionButtons();
        BuildSidebar();
    }

    /// <summary>Reset to the "no iPod connected" state (also stops playback off a now-removed device).</summary>
    private void ShowNoDevice()
    {
        _nowPlaying.StopAndHide(); _playingTrack = null; _queue.Clear();
        _device = null; _lib = null; _db = null; _current = null; _photos = null;
        _viewKind = SidebarRowKind.AllSongs;
        SetCenter();
        _tracks.Rows.Clear();
        _emptyMsg = ""; // the header already says "No iPod connected" — don't also draw a stale grid message
        _header.SetInfo("", Loc.T("No iPod connected"), Loc.T("Plug in your iPod — Mixtape detects it automatically. Or use Open folder. A Mac-formatted (HFS+) iPod isn't readable on Windows."), 0);
        SetActionButtons(); // recompute visibility + the "No iPod is connected" blocked reason
        BuildSidebar();
        SetStatus("");   // the header already says "No iPod connected" — don't echo it at the bottom too
    }

    /// <summary>Safely eject the connected iPod (flush + dismount), then return to the "no iPod" screen.</summary>
    private async void EjectDevice()
    {
        if (_device is null) return;
        string root = _device.MountRoot;
        if (root.Length < 2 || root[1] != ':')
        {
            MessageDialog.Show(this, Loc.T("This iPod isn't on a drive letter, so it can't be ejected from here. Use Windows' “Safely Remove Hardware”."), Loc.T("Eject"), MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        char drive = root[0];
        _nowPlaying.StopAndHide(); _playingTrack = null; // release any open audio file first
        UseWaitCursor = true;
        (bool ok, string msg) result;
        try { result = await Task.Run(() => { bool s = DeviceEjector.TryEject(drive, out var m); return (s, m); }); }
        finally { UseWaitCursor = false; }

        if (result.ok)
        {
            _ejectedRoot = root;        // don't let auto-detect re-adopt it until it's physically replugged
            _deviceChangeTimer.Stop();
            ShowNoDevice();
            SetStatus(Loc.T("Ejected — safe to unplug your iPod."));
        }
        else
        {
            MessageDialog.Show(this, Loc.T("Couldn't eject the iPod:\n\n{0}", result.msg), Loc.T("Eject"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>Auto-detect after a plug/unplug. Leaves the current view alone when the active iPod is still
    /// connected; only re-loads when it was removed (or one appears while none is loaded).</summary>
    private async void AutoDetectDevices()
    {
        if (IsDisposed || _scanning) return;
        _scanning = true;
        List<IPodDevice> found;
        try { found = await Task.Run(() => DeviceDetector.DetectAll().ToList()); }
        catch { _scanning = false; return; }
        finally { _scanning = false; }
        if (IsDisposed) return;

        // Don't re-adopt an iPod the user just ejected (a drive scan can re-mount it); wait for a real replug.
        if (_ejectedRoot is not null)
            found = found.Where(d => !string.Equals(d.MountRoot, _ejectedRoot, StringComparison.OrdinalIgnoreCase)).ToList();

        bool currentPresent = _device is not null
            && found.Any(d => string.Equals(d.MountRoot, _device!.MountRoot, StringComparison.OrdinalIgnoreCase));

        if (currentPresent)
        {
            // The active iPod is still connected — don't disturb the user's view. Only refresh the device
            // list in the rail if the set of connected iPods actually changed.
            var before = string.Join("|", _devices.Select(d => d.MountRoot).OrderBy(s => s, StringComparer.OrdinalIgnoreCase));
            var after = string.Join("|", found.Select(d => d.MountRoot).OrderBy(s => s, StringComparer.OrdinalIgnoreCase));
            if (!string.Equals(before, after, StringComparison.OrdinalIgnoreCase))
            {
                for (int i = 0; i < found.Count; i++) // keep the active device's object so its row stays highlighted
                    if (string.Equals(found[i].MountRoot, _device!.MountRoot, StringComparison.OrdinalIgnoreCase)) found[i] = _device!;
                _devices.Clear(); _devices.AddRange(found);
                BuildSidebar();
                SetStatus(found.Count == 1 ? Loc.T("{0} iPod connected.", found.Count) : Loc.T("{0} iPods connected.", found.Count));
            }
            return;
        }

        // The active iPod was unplugged, or one appeared while none was loaded → adopt the new set.
        _devices.Clear(); _devices.AddRange(found);
        if (found.Count > 0) LoadDevice(found[0]);
        else ShowNoDevice();
    }

    private void OpenFolder()
    {
        using var dlg = new FolderBrowserDialog { Description = Loc.T("Select the iPod's drive root (the folder that contains iPod_Control)") };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        var device = DeviceDetector.Build(dlg.SelectedPath);
        if (device is null)
        {
            MessageDialog.Show(this, Loc.T("That folder has no iPod_Control directory, so it isn't an iPod root."), "Mixtape", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        LoadDevice(device);
    }

    /// <summary>Used by the headless --render preview to populate the window without scanning drives.</summary>
    public void PreviewDevice(IPodDevice device) => LoadDevice(device);

    /// <summary>Headless preview: switch to a named view (songs/videos/photos) before capture.</summary>
    public void PreviewSelectView(string view)
    {
        _viewKind = view.ToLowerInvariant() switch
        {
            "videos" => SidebarRowKind.Videos,
            "photos" => SidebarRowKind.Photos,
            "device" => SidebarRowKind.Device,
            "albums" => SidebarRowKind.Albums,
            "artists" => SidebarRowKind.Artists,
            "local" => SidebarRowKind.LocalMusic,
            _ => SidebarRowKind.AllSongs,
        };
        _current = null;
        _browseFilter = null;
        BuildSidebar();
        ShowCurrent();
    }

    private void LoadDevice(IPodDevice device)
    {
        _nowPlaying.StopAndHide(); // release any open audio file so the previous iPod can be ejected
        _playingTrack = null;
        _device = device;
        _writeConfirmed = false; // consent is per-device — re-confirm after switching iPods
        if (!_devices.Contains(device)) _devices.Add(device);
        try
        {
            _lib = IpodLibrary.Load(device);
            _lib.Artwork = ArtworkLibrary.Load(device);   // cover-art sync on colour-screen models (no-op otherwise)
            _db = _lib.View;
        }
        catch (Exception ex)
        {
            SetStatus(Loc.T("Failed to read iTunesDB: {0}", ex.Message));
            MessageDialog.Show(this, Loc.T("Could not read this iPod's database:\n\n{0}", ex.Message), "Mixtape", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _photos = device.Profile.SupportsPhotos ? PhotoLibrary.Load(device) : null;

        RebuildPlaylists();
        SeedDefaultSort();
        _queue.Clear();   // a freshly-loaded library invalidates any queued track instances
        _viewKind = SidebarRowKind.AllSongs;
        _current = null;
        _browseFilter = null;
        BuildSidebar();
        ShowCurrent();
        MaybeAutoRecoverGuid();
    }

    private readonly HashSet<string> _autoGuidOffered = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>When "Auto device-ID recovery" is on, the moment a hash58 iPod with no stored GUID is
    /// detected we offer to read its hardware ID — so the user never has to hunt for the device-page
    /// "Read device ID" button. Offered once per device per session; the recovery flow confirms + reports.</summary>
    private void MaybeAutoRecoverGuid()
    {
        if (!_settings.AutoGuidRecovery) return;
        var dev = _device; var p = dev?.Profile;
        if (dev is null || p is null) return;
        if (p.CanWrite || p.Scheme != ChecksumScheme.Hash58 || !string.IsNullOrEmpty(p.FirewireGuid)) return;
        if (dev.MountRoot.Length < 2 || dev.MountRoot[1] != ':') return;   // only drive-letter mounts can be read
        if (!_autoGuidOffered.Add(dev.MountRoot)) return;                  // don't re-offer the same iPod this session
        // Let the device page finish painting first; the recovery flow has its own confirm + result dialogs.
        BeginInvoke(() => { if (ReferenceEquals(_device, dev)) EnableWritingByReadingDeviceId(dev); });
    }

    private void RebuildPlaylists()
    {
        _shownPlaylists.Clear();
        if (_db is null) return;
        var seen = new HashSet<ulong>();
        foreach (var pl in _db.Playlists)
        {
            if (pl.PersistentId != 0 && !seen.Add(pl.PersistentId)) continue;
            _shownPlaylists.Add(pl);
        }
    }

    private void BuildSidebar()
    {
        _sidebar.Begin();
        _sidebar.AddSection(Loc.T("DEVICE"));
        foreach (var d in _devices)
            // Clicking the device opens its info page; the row is highlighted when that page is shown.
            _sidebar.AddItem(SidebarRowKind.Device, d.Profile.ModelName ?? d.Profile.ModelNumber ?? "iPod", d,
                _viewKind == SidebarRowKind.Device && ReferenceEquals(d, _device));

        var others = new List<Playlist>();
        if (_db is not null)
        {
            var master = _db.Master;
            _sidebar.AddSection(Loc.T("LIBRARY"));
            _sidebar.AddItem(SidebarRowKind.AllSongs, Loc.T("All songs"), "all", _viewKind == SidebarRowKind.AllSongs);
            _sidebar.AddItem(SidebarRowKind.Albums, Loc.T("Albums"), "albums", _viewKind == SidebarRowKind.Albums);
            _sidebar.AddItem(SidebarRowKind.Artists, Loc.T("Artists"), "artists", _viewKind == SidebarRowKind.Artists);
            if (_device?.Profile.SupportsVideo == true && _settings.ShowVideos)
                _sidebar.AddItem(SidebarRowKind.Videos, Loc.T("Videos"), "videos", _viewKind == SidebarRowKind.Videos);
            if (_device?.Profile.SupportsPhotos == true && _settings.ShowPhotos)
                _sidebar.AddItem(SidebarRowKind.Photos, Loc.T("Photos"), "photos", _viewKind == SidebarRowKind.Photos);

            others = _shownPlaylists.Where(p => !ReferenceEquals(p, master)).ToList();
            // Always show the PLAYLISTS section so the area is discoverable; when empty, a faint hint
            // tells the user they can right-click to make one.
            _sidebar.AddSection(Loc.T("PLAYLISTS"));
            if (others.Count > 0)
                foreach (var pl in others)
                    _sidebar.AddItem(SidebarRowKind.Playlist, pl.Name, pl, _viewKind == SidebarRowKind.Playlist && ReferenceEquals(pl, _current));
            else if (_device?.Profile.CanWrite == true)
                _sidebar.AddHint(Loc.T("Right-click here to add one"));
        }

        // Always available, with or without an iPod: music that lives on this PC.
        _sidebar.AddSection(Loc.T("ON THIS PC"));
        _sidebar.AddItem(SidebarRowKind.LocalMusic, Loc.T("Local Music"), "local", _viewKind == SidebarRowKind.LocalMusic);
        foreach (var lp in _settings.LocalPlaylists)
            _sidebar.AddItem(SidebarRowKind.LocalPlaylist, lp.Name.Length == 0 ? Loc.T("Untitled") : lp.Name, lp,
                _viewKind == SidebarRowKind.LocalPlaylist && ReferenceEquals(lp, _currentLocalPlaylist));
        if (_settings.LocalPlaylists.Count == 0) _sidebar.AddHint(Loc.T("Right-click to add a playlist"));

        _sidebar.End();
        // Playlists with a chosen cover get it as their sidebar icon instantly; the rest fall back to
        // the background first-track cover.
        var custom = new List<Playlist>();
        foreach (var pl in others)
        {
            string? k = CoverKeyFor(SidebarRowKind.Playlist, pl);
            if (k is null) continue;
            int cid = _settings.GetCover(k);
            if (cid >= 0) { _sidebar.SetIcon(pl, cid == CoverArt.CassetteId ? CoverArt.GenerateTitled(cid, SidebarIconPx, pl.Name) : CoverArt.Generate(cid, SidebarIconPx)); custom.Add(pl); }
        }
        // Local (PC-side) playlists with a chosen cover get it as their sidebar icon too (keyed by their stable id).
        foreach (var lp in _settings.LocalPlaylists)
        {
            if (LocalCoverKey(lp) is not { } lk) continue;
            int cid = _settings.GetCover(lk);
            // Normalise the empty name to "Untitled" — the SAME label the header + picker use — so a cassette cover's
            // title-derived hue matches across the sidebar, header and the picker the user just chose it in.
            string lpName = lp.Name.Length == 0 ? Loc.T("Untitled") : lp.Name;
            if (cid >= 0) _sidebar.SetIcon(lp, cid == CoverArt.CassetteId ? CoverArt.GenerateTitled(cid, SidebarIconPx, lpName) : CoverArt.Generate(cid, SidebarIconPx));
        }
        LoadSidebarIconsAsync(others.Where(p => !custom.Contains(p)).ToList());
    }

    /// <summary>Background-loads each playlist's first-track cover as its sidebar icon (gen-guarded).</summary>
    private void LoadSidebarIconsAsync(List<Playlist> playlists)
    {
        if (!_settings.ShowArtwork || _device is null || _db is null || playlists.Count == 0) return;
        int gen = ++_sidebarArtGen;
        string mount = _device.MountRoot;
        var jobs = playlists
            .Select(pl => (Pl: pl, First: pl.TrackIds.Select(id => _db!.FindByUniqueId(id)).FirstOrDefault(t => t is not null)))
            .Where(j => j.First is not null)
            .ToList();
        Task.Run(() =>
        {
            foreach (var j in jobs)
            {
                if (_sidebarArtGen != gen) return;
                var icon = ArtworkService.Load("sb:" + ArtworkService.KeyFor(j.First!), j.First!.ResolveFilePath(mount), SidebarIconPx);
                if (icon != null) { var pl = j.Pl; TryBeginInvoke(() => { if (_sidebarArtGen == gen) _sidebar.SetIcon(pl, icon); }); }
            }
        });
    }

    /// <summary>Reset the search box so a query never silently carries over to another view.</summary>
    private void ClearSearch()
    {
        _searchDebounce.Stop();   // a view switch supersedes any pending search rebuild
        if (_searchQuery.Length == 0) return;
        _navigating = true;
        try { _search.ClearQuery(); _searchQuery = ""; } finally { _navigating = false; }
    }

    private void OnSidebarActivated(SidebarRowKind kind, object? tag)
    {
        ClearSearch(); // a query from a previous view must not leak into the one we're switching to
        TransitionCenter(() =>
        {
            switch (kind)
            {
                case SidebarRowKind.Device when tag is IPodDevice d:
                    if (!ReferenceEquals(d, _device)) LoadDevice(d); // switch first (resets to All songs)
                    _viewKind = SidebarRowKind.Device; _current = null;
                    BuildSidebar(); ShowCurrent();
                    break;
                case SidebarRowKind.AllSongs:
                case SidebarRowKind.Albums:
                case SidebarRowKind.Artists:
                case SidebarRowKind.Videos:
                case SidebarRowKind.Photos:
                case SidebarRowKind.LocalMusic:
                    if (kind == SidebarRowKind.LocalMusic) _localStale = true; // re-clicking the row rescans for new files
                    _viewKind = kind; _current = null; _browseFilter = null; // clicking the section resets any drill-in
                    BuildSidebar(); ShowCurrent();
                    break;
                case SidebarRowKind.Playlist when tag is Playlist pl:
                    _viewKind = SidebarRowKind.Playlist; _current = pl;
                    BuildSidebar(); ShowCurrent();
                    break;
                case SidebarRowKind.LocalPlaylist when tag is LocalPlaylistData lp:
                    _viewKind = SidebarRowKind.LocalPlaylist; _current = null; _currentLocalPlaylist = lp; _browseFilter = null;
                    BuildSidebar(); ShowCurrent();
                    break;
            }
        });
    }

    /// <summary>
    /// Run a view switch with an Apple-style cross-dissolve over the centre region: snapshot the current
    /// content, apply the switch (which repopulates the centre), snapshot the result, then dissolve from
    /// one to the other. Falls back to an instant switch when motion is off or a snapshot isn't possible.
    /// </summary>
    private void TransitionCenter(Action switchViews)
    {
        var center = _center;
        if (center is null || !Anim.MotionEnabled || _viewTransitionBusy || !center.IsHandleCreated || center.Width < 8 || center.Height < 8)
        {
            switchViews();
            return;
        }

        Bitmap? oldBmp = null, newBmp = null;
        try
        {
            oldBmp = new Bitmap(center.Width, center.Height);
            center.DrawToBitmap(oldBmp, new Rectangle(0, 0, center.Width, center.Height));
        }
        catch { oldBmp?.Dispose(); switchViews(); return; }

        switchViews();

        try
        {
            newBmp = new Bitmap(center.Width, center.Height);
            center.DrawToBitmap(newBmp, new Rectangle(0, 0, center.Width, center.Height));
        }
        catch { oldBmp.Dispose(); newBmp?.Dispose(); return; }

        var overlay = new TransitionPanel(oldBmp, newBmp, ViewStyle) { Bounds = new Rectangle(0, 0, center.Width, center.Height) };
        _viewTransitionBusy = true;
        center.Controls.Add(overlay);
        overlay.BringToFront();
        overlay.Start(() => { if (!center.IsDisposed) center.Controls.Remove(overlay); overlay.Dispose(); _viewTransitionBusy = false; });
    }

    // ---- track view ----

    /// <summary>Show exactly one of the centre panels (track grid / photo grid / album-artist grid / device page).</summary>
    private void SetCenter()
    {
        // Switching views cancels any in-flight song-list art decode: its header callback checks `_artGen == gen`,
        // so bumping here stops a stale first-track cover from slamming over the Device/Albums/Artists/Photos header.
        _artGen++;
        bool photos = _viewKind == SidebarRowKind.Photos;
        bool device = _viewKind == SidebarRowKind.Device;
        bool browse = _viewKind is SidebarRowKind.Albums or SidebarRowKind.Artists && _browseFilter is null; // the grid, not a drill-in
        bool songs = !photos && !device && !browse;
        // The four centre panels are all Dock=Fill siblings. Only ONE must be Visible at a time — otherwise two
        // visible Fill siblings compete and only the back-most gets real size, so the front one collapses to zero.
        // (Previously this toggled _tracks.Parent — the INNER viewport — and left the gridHost itself always
        // visible, so Albums/Artists/Photos rendered for a frame then "went away" when a later layout pass flipped
        // which Fill won.) Hide the whole song-list host instead, and keep only the active panel visible + at back.
        _gridHost.Visible = songs;
        _photoView.Visible = photos;
        _deviceView.Visible = device;
        _browseView.Visible = browse;
        Control active = photos ? _photoView : device ? _deviceView : browse ? _browseView : _gridHost;
        active.SendToBack();
        ScheduleBarBackdrop();   // the view behind the bar changed → refresh the frosted backdrop
    }

    private void ShowCurrent()
    {
        _header.SetBadge("", false);   // cleared for every view; the song list re-sets it below when the DB has warnings
        if (_viewKind == SidebarRowKind.LocalPlaylist) { ShowLocalPlaylist(); return; }
        if (_viewKind == SidebarRowKind.LocalMusic) { ShowLocalMusic(); return; }
        if (_viewKind == SidebarRowKind.Photos) { ShowPhotos(); return; }
        if (_viewKind == SidebarRowKind.Device) { ShowDevice(); return; }
        if (_viewKind is SidebarRowKind.Albums or SidebarRowKind.Artists && _browseFilter is null) { ShowBrowse(); return; }

        SetCenter();
        _tracks.Rows.Clear();
        _hotRow = -1;
        if (_db is null) { _header.SetInfo("", "—", "", 0); SetActionButtons(); return; }

        bool isVideos = _viewKind == SidebarRowKind.Videos;
        bool isPlaylist = _viewKind == SidebarRowKind.Playlist && _current is not null;

        List<Track> list;
        string kicker, title;
        if (_browseFilter is not null && _viewKind is SidebarRowKind.Albums or SidebarRowKind.Artists)
        {
            kicker = Loc.T(_browseKicker); title = _browseTitle;
            list = _db.Tracks.Where(t => MediaType.IsAudio(t.MediaType)).Where(_browseFilter).ToList();
        }
        else if (isPlaylist)
        {
            kicker = Loc.T("PLAYLIST");
            title = _current!.Name.Length == 0 ? Loc.T("Untitled playlist") : _current.Name;
            list = new List<Track>();
            foreach (uint id in _current.TrackIds) { var t = _db.FindByUniqueId(id); if (t is not null) list.Add(t); }
        }
        else if (isVideos)
        {
            kicker = Loc.T("LIBRARY"); title = Loc.T("Videos");
            list = _db.Tracks.Where(t => MediaType.IsVideo(t.MediaType)).ToList();
        }
        else
        {
            kicker = Loc.T("LIBRARY"); title = Loc.T("All songs");
            list = _db.Tracks.Where(t => MediaType.IsAudio(t.MediaType)).ToList();
        }

        if (_searchQuery.Length > 0)
            list = list.Where(t => Match(t, _searchQuery)).ToList();

        _emptyMsg = list.Count > 0 ? ""
            : _searchQuery.Length > 0 ? Loc.T("No results for “{0}”", _searchQuery)
            : isPlaylist ? Loc.T("This playlist is empty.")
            : isVideos ? Loc.T("No videos on this iPod.")
            : Loc.T("No songs on this iPod.");

        long totalMs = 0;
        foreach (var t in list) totalMs += t.LengthMs;
        SortTracks(list);
        UpdateSortIndicators();
        int artSize = _settings.Compact ? 22 : 36;

        _tracks.SuspendLayout();
        _populatingGrid = true; // ignore the selection churn while we add rows
        foreach (var t in list)
        {
            object? thumb = _settings.ListArtwork ? Theme.MakeArt(artSize, Theme.StableHash(t.Album ?? t.DisplayTitle)) : null; // placeholder until real art loads (none in compact / text-only)
            int r = _tracks.Rows.Add(thumb, t.DisplayTitle, t.Artist ?? "", t.Album ?? "",
                RatingStars(t.Rating), t.PlayCount > 0 ? t.PlayCount.ToString() : "", DateAddedStr(t.DateAdded), t.DurationStr);
            _tracks.Rows[r].Tag = t;
        }
        _populatingGrid = false;
        _tracks.ResumeLayout();
        SizeTracks();   // once, after the bulk populate (the per-row handler is suppressed during it)

        string noun = isVideos ? "video" : "song";
        _header.SetInfo(kicker, title, Summary(list.Count, totalMs, noun), Theme.StableHash(title));
        int coverId = CurrentViewCover();
        _currentHasCustomCover = coverId >= 0;
        if (_currentHasCustomCover) _header.SetArt(coverId == CoverArt.CassetteId ? CoverArt.GenerateTitled(coverId, 150, title) : CoverArt.Generate(coverId, 150)); // chosen art wins over the song thumbnail
        _header.ArtClickable = _viewKind is SidebarRowKind.AllSongs or SidebarRowKind.Playlist; // click the cover to choose art
        // The count lives in the header subtitle. The DB warning gets its own amber badge under the subtitle;
        // the under-button line carries only the read-only note (and transient feedback).
        bool warn = _db.Warnings.Count > 0;
        _header.SetBadge(warn ? Loc.T("⚠ {0} warning(s)", _db.Warnings.Count) : "", warn);
        string st = "";
        if (_device is not null && !_device.Profile.CanWrite)
            st = Loc.T("Read-only — {0}", _device.Profile.WriteBlockReason);
        _baseStatus = st; _baseStatusClickable = false;
        SetStatus(st);
        SetActionButtons();
        if (_settings.ListArtwork) LoadArtworkAsync(list, artSize);   // skip cover loading when text-only
    }

    // ---- album / artist browse ----

    private const int BrowseCover = 150;
    private static string AlbumKey(Track t) => (t.Album ?? "").Trim().ToLowerInvariant() + "" + DisplayAlbumArtist(t).ToLowerInvariant();
    private static string ArtistKey(Track t) => (t.AlbumArtist ?? t.Artist ?? "").Trim();
    private static string DisplayAlbum(Track t) => string.IsNullOrWhiteSpace(t.Album) ? Loc.T("Unknown Album") : t.Album!;
    private static string DisplayAlbumArtist(Track t) =>
        !string.IsNullOrWhiteSpace(t.AlbumArtist) ? t.AlbumArtist! : !string.IsNullOrWhiteSpace(t.Artist) ? t.Artist! : Loc.T("Unknown Artist");

    /// <summary>Build the Albums or Artists cover grid from the library; covers load in the background.</summary>
    private void ShowBrowse()
    {
        SetCenter();
        _tracks.Rows.Clear();
        _hotRow = -1;
        _header.ArtClickable = false;

        bool albums = _viewKind == SidebarRowKind.Albums;
        string title = albums ? Loc.T("Albums") : Loc.T("Artists");
        if (_db is null) { _browseView.SetItems(Array.Empty<(string, string, string)>(), "—"); _header.SetInfo(Loc.T("LIBRARY"), title, "", 0); SetActionButtons(); return; }

        var audio = _db.Tracks.Where(t => MediaType.IsAudio(t.MediaType)).ToList();
        var cards = new List<(string Key, string Title, string Subtitle)>();
        var reps = new Dictionary<string, Track>(); // representative track per card (for the cover)

        if (albums)
        {
            foreach (var grp in audio.GroupBy(AlbumKey).OrderBy(g => DisplayAlbum(g.First()), StringComparer.OrdinalIgnoreCase))
            {
                cards.Add((grp.Key, DisplayAlbum(grp.First()), DisplayAlbumArtist(grp.First())));
                reps[grp.Key] = grp.First();
            }
        }
        else
        {
            foreach (var grp in audio.GroupBy(ArtistKey).OrderBy(g => g.Key.Length == 0 ? "￿" : g.Key, StringComparer.OrdinalIgnoreCase))
            {
                int albumCount = grp.Select(AlbumKey).Distinct().Count();
                int songs = grp.Count();
                cards.Add((grp.Key, grp.Key.Length > 0 ? grp.Key : Loc.T("Unknown Artist"), $"{CountNoun(albumCount, "album")}  ·  {CountNoun(songs, "song")}"));
                reps[grp.Key] = grp.First();
            }
        }

        // Filter by the header search query (matches the card title or its subtitle).
        if (!string.IsNullOrEmpty(_searchQuery))
        {
            string q = _searchQuery;
            cards = cards.Where(c => c.Title.Contains(q, StringComparison.OrdinalIgnoreCase) || c.Subtitle.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
            var keep = new HashSet<string>(cards.Select(c => c.Key));
            foreach (var k in reps.Keys.Where(k => !keep.Contains(k)).ToList()) reps.Remove(k);
        }

        _browseView.SetItems(cards, albums ? Loc.T("No albums on this iPod.") : Loc.T("No artists on this iPod."));
        string sub = CountNoun(cards.Count, albums ? "album" : "artist");
        _header.SetInfo(Loc.T("LIBRARY"), title, sub, Theme.StableHash(albums ? "Albums" : "Artists"));
        _header.SetArt(null); // the header uses its generated gradient for these overview pages
        _baseStatus = ""; _baseStatusClickable = false; SetStatus("");  // count shows in the subtitle
        SetActionButtons();
        LoadBrowseCoversAsync(reps);
    }

    /// <summary>Drill into the clicked album/artist card → show its songs in the track grid.</summary>
    private void OnBrowseActivated(string key)
    {
        if (_db is null) return;
        ClearSearch(); // drilling into an album/artist starts unfiltered (no leftover query)
        TransitionCenter(() =>
        {
            if (_viewKind == SidebarRowKind.Albums)
            {
                _browseFilter = t => AlbumKey(t) == key;
                var first = _db.Tracks.FirstOrDefault(t => MediaType.IsAudio(t.MediaType) && AlbumKey(t) == key);
                _browseTitle = first is not null ? DisplayAlbum(first) : Loc.T("Album");
                _browseKicker = "ALBUM";
            }
            else
            {
                _browseFilter = t => ArtistKey(t) == key;
                _browseTitle = key.Length > 0 ? key : Loc.T("Unknown Artist");
                _browseKicker = "ARTIST";
            }
            ShowCurrent();
        });
    }

    private void LoadBrowseCoversAsync(Dictionary<string, Track> reps)
    {
        if (!_settings.ShowArtwork || _device is null || reps.Count == 0) return;
        int gen = ++_browseArtGen;
        string mount = _device.MountRoot;
        var jobs = reps.Select(kv => (kv.Key, Path: kv.Value.ResolveFilePath(mount), ArtKey: ArtworkService.KeyFor(kv.Value))).ToList();
        // Apply ALREADY-CACHED covers synchronously + instantly first, so they're present when the view-switch
        // snapshot is taken (revisits show real covers sliding in, instead of placeholders that pop after the
        // transition). Only genuinely-uncached covers go to the background decode + cross-dissolve in.
        var pending = new List<(string Key, string? Path, string ArtKey)>();
        foreach (var j in jobs)
        {
            var hit = ArtworkService.TryGet("br:" + j.ArtKey, BrowseCover);
            if (hit != null) _browseView.SetCover(j.Key, hit, animate: false);
            else pending.Add(j);
        }
        if (pending.Count == 0) return;
        Task.Run(() =>
        {
            foreach (var j in pending)
            {
                if (_browseArtGen != gen) return;
                var art = ArtworkService.Load("br:" + j.ArtKey, j.Path, BrowseCover);
                if (art != null) { string key = j.Key; TryBeginInvoke(() => { if (_browseArtGen == gen) _browseView.SetCover(key, art); }); }
            }
        });
    }

    // ---- Cover Flow ----

    /// <summary>Open the immersive Cover-Flow album browser over the content area. Covers stream in over a
    /// placeholder so it opens instantly even on a big library.</summary>
    // ---- mini player (iTunes-style detached transport) ----

    /// <summary>Open the mini player and hide the full window — the single audio engine keeps running, the
    /// mini just mirrors and drives it. Reopening surfaces the existing window in its last position.</summary>
    private void OpenMiniPlayer()
    {
        if (_mini is null)
        {
            _mini = new MiniPlayerForm(); // independent (no Owner) so it keeps its own taskbar button while the big window is hidden
            _mini.PrevRequested += () => PlayRelative(-1);
            _mini.NextRequested += () => PlayRelative(+1);
            _mini.PlayPauseRequested += () => _nowPlaying.TogglePlayback();
            _mini.SeekRequested += f => _nowPlaying.SeekFraction(f);
            _mini.VolumeRequested += v => _nowPlaying.SetVolumeLevel(v);
            _mini.MuteRequested += () => _nowPlaying.ToggleMute();
            _mini.ShuffleRequested += () => _nowPlaying.ToggleShuffle();
            _mini.RepeatRequested += () => _nowPlaying.CycleRepeat();
            _mini.EqualizerRequested += OpenEqualizer;
            _mini.ProFeaturesRequested += OpenProFeatures;
            _mini.SpectrumProvider = _nowPlaying.ReadSpectrum;   // live spectrum for the mini's visualizer
            _mini.ExpandRequested += RestoreFromMini;
        }

        // First open: float it near the full window's top-left (it's draggable + remembers its spot after).
        if (_mini.Location == Point.Empty)
        {
            var wa = Screen.FromControl(this).WorkingArea;
            int x = Math.Clamp(Location.X + 40, wa.Left + 8, wa.Right - _mini.Width - 8);
            int y = Math.Clamp(Location.Y + 80, wa.Top + 8, wa.Bottom - _mini.Height - 8);
            _mini.Location = new Point(x, y);
        }

        // Seed it with the current state before showing.
        _miniTrack = _nowPlaying.NowTrack;
        _mini.SetTrack(_miniTrack, _nowPlaying.LoadHeroCover(460));   // hi-res so the big cover hero stays sharp
        _mini.SetProgress(_nowPlaying.Playing, _nowPlaying.PositionSeconds, _nowPlaying.DurationSeconds, _nowPlaying.VolumeLevel, _nowPlaying.Muted, _nowPlaying.Shuffle, _nowPlaying.Repeat);

        _mini.Show();
        _mini.Activate();
        Hide(); // turn off the big window
    }

    /// <summary>Return from the mini player to the full window.</summary>
    private void RestoreFromMini()
    {
        Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
        _mini?.Hide();
    }

    // Keep the mini player in lock-step with the bar. State pushes carry the cover only when the track
    // changes (PushMiniState); progress ticks are cheap (PushMiniProgress).
    private void PushMiniState()
    {
        if (_mini is not { Visible: true }) return;
        if (!ReferenceEquals(_nowPlaying.NowTrack, _miniTrack))
        {
            _miniTrack = _nowPlaying.NowTrack;
            _mini.SetTrack(_miniTrack, _nowPlaying.LoadHeroCover(460));   // hi-res so the big cover hero stays sharp
        }
        _mini.SetProgress(_nowPlaying.Playing, _nowPlaying.PositionSeconds, _nowPlaying.DurationSeconds, _nowPlaying.VolumeLevel, _nowPlaying.Muted, _nowPlaying.Shuffle, _nowPlaying.Repeat);
    }

    private void PushMiniProgress()
    {
        if (_mini is { Visible: true })
            _mini.SetProgress(_nowPlaying.Playing, _nowPlaying.PositionSeconds, _nowPlaying.DurationSeconds, _nowPlaying.VolumeLevel, _nowPlaying.Muted, _nowPlaying.Shuffle, _nowPlaying.Repeat);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        // Real app close: force the mini shut (its UserClosing guard only blocks user-initiated closes).
        if (_mini is not null) { _mini.Dispose(); _mini = null; }
        // App-lifetime timers aren't in a components container — dispose them so repeated show/close
        // (test harness / --render reuse) doesn't leak Win32 timer registrations.
        _deviceChangeTimer.Dispose(); _backdropTimer.Dispose(); _dropHideTimer.Dispose(); _searchDebounce.Dispose(); _rowDividerPen.Dispose(); _ratingFont.Dispose();
        base.OnFormClosed(e);
    }

    /// <summary>The audio tracks Cover Flow browses for the current view: the PC library on the Local-Music
    /// views, otherwise the whole iPod library.</summary>
    private List<Track> CoverFlowSource(out bool local)
    {
        local = _viewKind is SidebarRowKind.LocalMusic or SidebarRowKind.LocalPlaylist;
        IEnumerable<Track> src = local ? _localTracks : (_db?.Tracks ?? Enumerable.Empty<Track>());
        return src.Where(t => MediaType.IsAudio(t.MediaType)).ToList();
    }

    private void OpenCoverFlow()
    {
        if (_root is null || _content is null) return;
        if (CoverFlowSource(out _).Count == 0) return;

        if (_coverFlow is null)
        {
            _coverFlow = new CoverFlowView { Visible = false };
            _coverFlow.CloseRequested += CloseCoverFlow;
            _coverFlow.Activated += OnCoverFlowActivated;
            _coverFlow.ModeChanged += OnCoverFlowModeChanged;
            _root.Controls.Add(_coverFlow);
        }
        _cfPlaceholder ??= MakeCoverPlaceholder();

        _coverFlow.Visible = true;
        PopulateCoverFlow(_cfMode, centerOnCurrent: true);
        LayoutCoverFlow();
        _coverFlow.BringToFront();
        _coverFlow.Focus();
        _coverFlow.AnimateIn();   // zoom + fade-in
    }

    private void OnCoverFlowModeChanged(CoverFlowView.BrowseMode mode)
    {
        _cfMode = mode;
        PopulateCoverFlow(mode, centerOnCurrent: true);
        _coverFlow?.AnimateIn();   // a subtle pop on switch
    }

    /// <summary>The cover tag for a track in a given mode — the key the deck matches against (and that
    /// <see cref="PlayingTag"/> uses so the "Now Playing" chip tracks the right item).</summary>
    private static object CoverTag(Track t, CoverFlowView.BrowseMode mode) => mode switch
    {
        CoverFlowView.BrowseMode.Songs => t,            // the track itself (reference identity)
        CoverFlowView.BrowseMode.Artists => ArtistKey(t),
        _ => AlbumKey(t),
    };

    /// <summary>(Re)build the Cover-Flow deck for the chosen mode: one cover per song / album / artist,
    /// covers streaming in over the placeholder. Centres on the playing item (or drilled album) when asked.</summary>
    private void PopulateCoverFlow(CoverFlowView.BrowseMode mode, bool centerOnCurrent)
    {
        if (_coverFlow is null) return;
        var audio = CoverFlowSource(out bool local);
        _cfLocal = local;
        if (audio.Count == 0) return;
        _cfMode = mode;
        _coverFlow.Mode = mode;
        _cfPlaceholder ??= MakeCoverPlaceholder();

        var items = new List<CoverFlowView.Item>();
        var reps = new List<(int idx, Track rep)>();
        int idx = 0;

        if (mode == CoverFlowView.BrowseMode.Songs)
        {
            foreach (var t in audio.OrderBy(t => t.DisplayTitle, StringComparer.OrdinalIgnoreCase))
            {
                string sub = string.Join("   ·   ", new[] { t.Artist, t.Album }.Where(x => !string.IsNullOrWhiteSpace(x)));
                items.Add(new CoverFlowView.Item(_cfPlaceholder, t.DisplayTitle, sub, t));
                reps.Add((idx++, t));
            }
        }
        else if (mode == CoverFlowView.BrowseMode.Artists)
        {
            foreach (var grp in audio.GroupBy(ArtistKey).OrderBy(g => g.Key.Length == 0 ? "￿" : g.Key, StringComparer.OrdinalIgnoreCase))
            {
                var rep = grp.First();
                int albums = grp.Select(AlbumKey).Distinct().Count(), songs = grp.Count();
                string title = grp.Key.Length > 0 ? grp.Key : Loc.T("Unknown Artist");
                items.Add(new CoverFlowView.Item(_cfPlaceholder, title, $"{CountNoun(albums, "album")}   ·   {CountNoun(songs, "song")}", grp.Key));
                reps.Add((idx++, rep));
            }
        }
        else // Albums
        {
            foreach (var grp in audio.GroupBy(AlbumKey).OrderBy(g => DisplayAlbum(g.First()), StringComparer.OrdinalIgnoreCase))
            {
                var rep = grp.First();
                int n = grp.Count();
                items.Add(new CoverFlowView.Item(_cfPlaceholder, DisplayAlbum(rep), $"{DisplayAlbumArtist(rep)}   ·   {CountNoun(n, "song")}", grp.Key));
                reps.Add((idx++, rep));
            }
        }

        int start = 0;
        if (centerOnCurrent && _playingTrack is not null)
        {
            object want = CoverTag(_playingTrack, mode);
            int f = items.FindIndex(it => Equals(it.Tag, want));
            if (f >= 0) start = f;
        }
        else if (centerOnCurrent && mode == CoverFlowView.BrowseMode.Albums && _browseFilter is not null && audio.FirstOrDefault(_browseFilter) is { } cur)
        {
            string ck = AlbumKey(cur);
            int f = items.FindIndex(it => (string?)it.Tag == ck);
            if (f >= 0) start = f;
        }

        _coverFlow.SetItems(items, start);
        _coverFlow.PlayingTag = _playingTrack is not null ? CoverTag(_playingTrack, mode) : null;

        int gen = ++_cfGen;
        string? mount = _device?.MountRoot;
        var jobs = reps;
        Task.Run(() =>
        {
            foreach (var (i, rep) in jobs)
            {
                if (_cfGen != gen) return;
                string? path = rep.LocalPath ?? (mount is not null ? rep.ResolveFilePath(mount) : null);
                Bitmap? art = null;
                try { art = ArtworkService.Load("cf:" + ArtworkService.KeyFor(rep), path, 300); } catch { }
                if (art is not null) { int ii = i; TryBeginInvoke(() => { if (_cfGen == gen && _coverFlow is { Visible: true }) _coverFlow.SetCover(ii, art); }); }
            }
        });
    }

    /// <summary>Size Cover Flow over the content card but leave the Now-Playing bar strip visible at the
    /// bottom; round only the top corners so it meets the bar flush (within the card's rounded frame).</summary>
    private void LayoutCoverFlow()
    {
        if (_coverFlow is null || _content is null) return;
        var a = _content.Bounds;
        a.Height = Math.Max(1, a.Height - NowPlayingBar.H);
        _coverFlow.Bounds = a;

        int w = a.Width, h = a.Height, r = CardRadius;
        if (w <= r * 2 || h <= r) { _coverFlow.Region = null; return; }
        using var p = new System.Drawing.Drawing2D.GraphicsPath();
        p.AddArc(0, 0, r * 2, r * 2, 180, 90);
        p.AddArc(w - r * 2, 0, r * 2, r * 2, 270, 90);
        p.AddLine(w, h, 0, h);     // square bottom edge — sits against the Now-Playing bar
        p.CloseFigure();
        _coverFlow.Region = new Region(p);
    }

    private void CloseCoverFlow()
    {
        _cfGen++; // cancel any in-flight cover load
        if (_coverFlow is null) return;
        var cf = _coverFlow;
        cf.AnimateOut(() => { cf.Visible = false; try { _tracks.Focus(); } catch { } }); // fade/zoom out, then hide
    }

    // ---- Up Next queue panel (right-side overlay, mirrors Cover Flow's hosting) ----

    /// <summary>Open the Up Next queue as a floating, rounded popover anchored to the queue button (like the
    /// Equalizer / Pro-features flyouts: click-away / Esc / × dismiss). Re-clicking the button reopens it.</summary>
    private void OpenUpNext(Rectangle anchor)
    {
        // Clicking the queue button while it's open closes it (the button's mouse-down already dismissed the
        // popover via click-away); the just-closed timestamp tells us not to reopen on the same click.
        if (Environment.TickCount - _upNextClosedTick < 250) return;

        var flyout = new UpNextFlyout();
        flyout.ClearRequested += () => _queue.Clear();
        flyout.RemoveRequested += t => _queue.Remove(t);
        flyout.MoveRequested += (from, to) => _queue.Move(from, to);
        flyout.ActivateRequested += JumpToQueued;
        flyout.FormClosed += (_, _) => { if (ReferenceEquals(_upNext, flyout)) _upNext = null; _upNextClosedTick = Environment.TickCount; };
        _upNext = flyout;
        RefreshUpNext();   // populate + size it to the queue before it's anchored
        // Defer the show so it isn't dismissed by the click that opened it (button mouse-down → reactivation race).
        BeginInvoke(() => { if (ReferenceEquals(_upNext, flyout) && !flyout.IsDisposed) flyout.ShowAnchored(anchor); });
    }

    /// <summary>Push the queue into the open popover (and reflect the count on the bar icon). Cheap: covers are
    /// album-cached. Safe to call when it's closed (then it only updates the bar count).</summary>
    private void RefreshUpNext()
    {
        _nowPlaying.SetQueueCount(_queue.Count);
        if (_upNext is null || _upNext.IsDisposed) return;
        var now = _nowPlaying.NowTrack;
        var nowArt = now is not null ? QueueCover(now) : null;
        var items = _queue.Items.Select(t => (t, QueueCover(t))).ToList();
        string? hint = _queue.Count > 0 && _nowPlaying.Repeat == NowPlayingBar.RepeatMode.One ? Loc.T("Repeat One is on — queue advances on Next") : null;
        _upNext.SetData(now, nowArt, items, hint);
    }

    /// <summary>A small rounded cover for a queued track (album-cached via ArtworkService), or null.</summary>
    private Bitmap? QueueCover(Track t)
    {
        try { return ArtworkService.Load("q:" + ArtworkService.KeyFor(t), ResolvePlayPath(t), 48); }
        catch { return null; }
    }

    /// <summary>Double-click a queued track → jump straight to it (drop it + anything ahead of it).</summary>
    private void JumpToQueued(Track t)
    {
        int ri = RowIndexOf(t);
        if (ri < 0 || !IsPlayableRow(ri, out var track, out var path)) { _queue.Remove(t); return; }
        while (_queue.Peek() is Track q) { bool hit = ReferenceEquals(q, t); _queue.Dequeue(); if (hit) break; }
        PlayFromNav(RowIndexOf(_playingTrack), ri);
    }

    /// <summary>Activate a cover (centre click / Enter) WITHOUT closing Cover Flow — it stays open as a
    /// visual player with the real Now-Playing bar below. The grid underneath is pointed at the chosen
    /// album/artist/song first so the bar's next/prev step through it. Behaviour depends on the mode:
    /// album/artist → play its first song; song → play that exact song (in the full-library context).</summary>
    private void OnCoverFlowActivated(CoverFlowView.Item it)
    {
        if (_cfLocal) { ActivateLocalCover(it); return; }
        if (_db is null) return;
        switch (_cfMode)
        {
            case CoverFlowView.BrowseMode.Songs:
                if (it.Tag is Track t) PlaySongFromCoverFlow(t);
                break;
            case CoverFlowView.BrowseMode.Artists:
                if (it.Tag is string ak) { NavigateToArtist(ak); if (_tracks.Rows.Count > 0) ActivateTrackRow(0); }
                break;
            default:
                if (it.Tag is string albk) { NavigateToAlbum(albk); if (_tracks.Rows.Count > 0) ActivateTrackRow(0); }
                break;
        }
    }

    /// <summary>Activate a Cover-Flow cover when browsing the PC library: resolve the target local track
    /// (the song, or the first track of the album/artist) and play it — preferring its row in the local
    /// grid (so prev/next walk it), else playing the file directly.</summary>
    private void ActivateLocalCover(CoverFlowView.Item it)
    {
        Track? target = _cfMode switch
        {
            CoverFlowView.BrowseMode.Songs => it.Tag as Track,
            CoverFlowView.BrowseMode.Artists => _localTracks.FirstOrDefault(t => MediaType.IsAudio(t.MediaType) && (string?)it.Tag == ArtistKey(t)),
            _ => _localTracks.FirstOrDefault(t => MediaType.IsAudio(t.MediaType) && (string?)it.Tag == AlbumKey(t)),
        };
        if (target is null) return;
        int row = RowIndexOf(target);
        if (row >= 0) { ActivateTrackRow(row); return; }
        if (target.LocalPath is { } p && File.Exists(p)) PlayAudio(target, p, -1);
    }

    /// <summary>Point the song grid at one album (no view transition — used while Cover Flow is on top).</summary>
    private void NavigateToAlbum(string key)
    {
        if (_db is null) return;
        ClearSearch();
        _viewKind = SidebarRowKind.Albums;
        _browseFilter = t => AlbumKey(t) == key;
        var first = _db.Tracks.FirstOrDefault(t => MediaType.IsAudio(t.MediaType) && AlbumKey(t) == key);
        _browseTitle = first is not null ? DisplayAlbum(first) : Loc.T("Album");
        _browseKicker = "ALBUM";
        ShowCurrent();
    }

    /// <summary>Point the song grid at one artist (no view transition — used while Cover Flow is on top).</summary>
    private void NavigateToArtist(string key)
    {
        if (_db is null) return;
        ClearSearch();
        _viewKind = SidebarRowKind.Artists;
        _browseFilter = t => ArtistKey(t) == key;
        _browseTitle = key.Length > 0 ? key : Loc.T("Unknown Artist");
        _browseKicker = "ARTIST";
        ShowCurrent();
    }

    /// <summary>Play one song picked from Cover Flow's Songs mode, with the full library as the playback
    /// context (so the bar's next/prev walk all songs).</summary>
    private void PlaySongFromCoverFlow(Track t)
    {
        if (_db is null) return;
        ClearSearch();
        _viewKind = SidebarRowKind.AllSongs;
        _current = null;
        _browseFilter = null;
        ShowCurrent();
        int row = RowIndexOf(t);
        if (row >= 0) ActivateTrackRow(row);
    }

    /// <summary>A neutral "loading" cover (dark panel + faint note) shown until real art streams in.</summary>
    private static Bitmap MakeCoverPlaceholder()
    {
        const int s = 256;
        var bmp = new Bitmap(s, s);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using (var br = new System.Drawing.Drawing2D.LinearGradientBrush(new Rectangle(0, 0, s, s),
            Theme.Blend(Theme.PanelBg, Color.White, 0.07), Theme.Blend(Theme.PanelBg, Color.Black, 0.18), 60f))
            g.FillRectangle(br, 0, 0, s, s);
        Theme.DrawNote(g, new RectangleF(s * 0.32f, s * 0.32f, s * 0.36f, s * 0.36f), Color.FromArgb(55, 255, 255, 255));
        return bmp;
    }

    private void ShowPhotos()
    {
        SetCenter();
        _tracks.Rows.Clear();
        _hotRow = -1;

        _header.ArtClickable = false; // covers are for music lists, not the photo library
        if (_photos is null)
        {
            _photoView.SetPhotos(Array.Empty<(uint, Bitmap?)>(), Loc.T("This iPod can't display photos."));
            _header.SetInfo(Loc.T("LIBRARY"), Loc.T("Photos"), "", Theme.StableHash("Photos"));
            SetActionButtons();
            return;
        }

        var photos = _photos.Photos.ToList();
        long pb = PhotoBytes();
        string sub = PhotoSummary(photos.Count) + (pb > 0 ? "  ·  " + CapacityBar.Human(pb) : "");
        _header.SetInfo(Loc.T("LIBRARY"), Loc.T("Photos"), sub, Theme.StableHash("Photos"));
        using (var hb = photos.Count > 0 ? _photos.RenderThumb(photos[0]) : null)
            _header.SetArt(hb); // header clones it; dispose our fresh copy
        SetActionButtons();
        UpdatePhotoStatus();

        // Revisiting Photos with the same, unchanged library AND a fully-decoded grid? Keep the existing tiles:
        // re-decoding the whole library (1500+ Ithmb decodes) on every open is the heaviest avoidable cost here.
        if (ReferenceEquals(_photos, _photoShownLib) && _photos.Generation == _photoShownGen
            && _photoView.Count == photos.Count && _photoView.AllThumbsLoaded)
            return;
        _photoShownLib = _photos; _photoShownGen = _photos.Generation;

        string empty = _photos.SafeToWrite ? Loc.T("No photos yet — click “Add photos”.") : (_photos.BlockReason ?? Loc.T("Photos are read-only."));
        // Show the tiles immediately (placeholders); decode the thumbnails in the background so a
        // large library (the user's has 1500+) doesn't freeze the UI on open.
        _photoView.SetPhotos(photos.Select(p => (p.ImageId, (Bitmap?)null)), empty);

        int gen = ++_photoArtGen;
        var lib = _photos;
        Task.Run(() =>
        {
            foreach (var p in photos)
            {
                if (_photoArtGen != gen) return;
                var bmp = lib.RenderThumb(p);
                if (bmp is null) continue;
                uint id = p.ImageId;
                TryBeginInvoke(() => { if (_photoArtGen == gen) _photoView.SetThumb(id, bmp); else bmp.Dispose(); });
            }
        });
    }

    /// <summary>Set the header action buttons' labels + availability for the active view. When an action
    /// isn't allowed the button stays clickable-but-greyed (BlockedReason), so clicking it explains why.</summary>
    private void SetActionButtons()
    {
        // Search lives in the header; show it for the library/list views, not the device or photo pages.
        _search.Visible = _viewKind is not (SidebarRowKind.Device or SidebarRowKind.Photos);
        if (_photoSize is not null) _photoSize.Visible = _viewKind == SidebarRowKind.Photos;   // the size slider takes the search row on Photos
        _header.Invalidate();   // re-layout the header cluster for the changed search/slider visibility

        // Cover Flow is available wherever an audio library is on screen (iPod views use the iPod DB; the
        // Local-Music views use the PC library). Not on the device/photos/videos pages.
        bool localView = _viewKind is SidebarRowKind.LocalMusic or SidebarRowKind.LocalPlaylist;
        bool hasAudio = localView
            ? _localTracks.Any(t => MediaType.IsAudio(t.MediaType))
            : _db is not null && _db.Tracks.Any(t => MediaType.IsAudio(t.MediaType));
        _header.CoverButton.Visible = hasAudio
            && _viewKind is SidebarRowKind.AllSongs or SidebarRowKind.Albums or SidebarRowKind.Artists
                or SidebarRowKind.Playlist or SidebarRowKind.LocalMusic or SidebarRowKind.LocalPlaylist;

        if (_viewKind == SidebarRowKind.LocalPlaylist)   // a local playlist: add/remove songs via right-click, no header buttons
        {
            _header.AddButton.Visible = false; _header.DeleteButton.Visible = false;
            return;
        }
        if (_viewKind == SidebarRowKind.LocalMusic)
        {
            _header.AddButton.Visible = _header.DeleteButton.Visible = true;
            _header.AddButton.Text = Loc.T("Add folder"); _header.AddButton.BlockedReason = null;
            _header.DeleteButton.Text = Loc.T("Manage…"); _header.DeleteButton.Danger = false; // Manage isn't destructive
            _header.DeleteButton.BlockedReason = _settings.LocalMusicFolders.Count == 0 ? Loc.T("Add a folder first.") : null;
            return;
        }
        bool canAudio = _device?.Profile.CanWrite == true;
        bool canPhotos = _photos?.SafeToWrite == true && _device?.Profile.SupportsPhotos == true;
        bool deviceView = _viewKind == SidebarRowKind.Device;
        _header.AddButton.Visible = !deviceView;   // the device page has its own buttons
        _header.DeleteButton.Visible = !deviceView;
        if (deviceView) return;

        bool photos = _viewKind == SidebarRowKind.Photos;
        bool allowed = photos ? canPhotos : canAudio;
        string reason = photos ? PhotoBlockReason() : AudioBlockReason();
        _header.DeleteButton.Text = Loc.T("Delete"); _header.DeleteButton.Danger = true; // restore destructive identity after Local Music
        _header.AddButton.Text = _viewKind switch
        {
            SidebarRowKind.Videos => Loc.T("Add video"),
            SidebarRowKind.Photos => Loc.T("Add photos"),
            _ => Loc.T("Add music"),
        };
        _header.AddButton.BlockedReason = allowed ? null : reason;
        _header.DeleteButton.BlockedReason = allowed ? null : reason;
    }

    private string AudioBlockReason()
    {
        if (_device is null) return Loc.T("No iPod is connected.");
        var r = _device.Profile.WriteBlockReason;
        return r.Length > 0 ? r : Loc.T("This iPod is read-only.");
    }

    private string PhotoBlockReason()
    {
        if (_device is null) return Loc.T("No iPod is connected.");
        if (_device.Profile.SupportsPhotos != true) return Loc.T("This iPod doesn't have a colour screen, so it can't store photos.");
        return _photos?.BlockReason ?? Loc.T("Photos can't be written to this iPod.");
    }

    /// <summary>Explain why the greyed Add/Delete button is unavailable, and how to fix it — offering to
    /// jump to the device page (where Read device ID / Restore live) when that's the fix.</summary>
    private void ShowActionBlockedHelp()
    {
        string title = Loc.T("Why is this greyed out?");
        string reason, fix;
        bool offerDevicePage = false;

        if (_device is null)
        {
            reason = Loc.T("No iPod is connected.");
            fix = Loc.T("Plug in your iPod (in disk mode) and press Refresh, or use Open folder to point at its drive. A Mac-formatted (HFS+) iPod can't be read on Windows.");
        }
        else if (_viewKind == SidebarRowKind.Photos)
        {
            var p = _device.Profile;
            if (p.SupportsPhotos != true)
            {
                reason = Loc.T("This iPod doesn't have a colour screen, so it can't store photos.");
                fix = Loc.T("Photos work on the iPod photo, 5G (video), Classic, and nano 3G and later.");
            }
            else
            {
                reason = _photos?.BlockReason ?? Loc.T("Photos can't be written to this iPod right now.");
                fix = Loc.T("Check the iPod isn't read-only on its device page, then try again.");
                offerDevicePage = !p.CanWrite;
            }
        }
        else // music / video
        {
            var p = _device.Profile;
            reason = p.WriteBlockReason.Length > 0 ? p.WriteBlockReason : Loc.T("This iPod is read-only.");
            switch (p.Scheme)
            {
                case ChecksumScheme.Hash58 when string.IsNullOrEmpty(p.FirewireGuid):
                    fix = Loc.T("Open this iPod's device page and click “Read device ID” — a safe, read-only query that reads the iPod's hardware ID (the same thing iTunes does) so music can be written.");
                    offerDevicePage = true;
                    break;
                case ChecksumScheme.Hash58: // GUID is known, but Mixtape's signature didn't match this iPod's
                    fix = Loc.T("Mixtape's signature for this iPod didn't match the one already on it, so writing stays disabled to avoid corrupting its library. Open the device page and use “Save report…” so this can be looked into.");
                    offerDevicePage = true;
                    break;
                case ChecksumScheme.Hash72:
                    fix = Loc.T("This iPod's signature (hash72, used by the nano 5G / Touch) can't be reproduced yet, so writing isn't possible. You can still browse, play, and copy music off the iPod.");
                    break;
                case ChecksumScheme.HashAB:
                    fix = Loc.T("This iPod uses the experimental hashAB signature (nano 6G/7G), which isn't enabled yet. Browsing and copying off still work.");
                    break;
                default:
                    fix = Loc.T("See the device page for details on this iPod's signature.");
                    offerDevicePage = true;
                    break;
            }
        }

        string body = reason + "\n\n" + fix;
        if (offerDevicePage && _device is not null
            && MessageDialog.Show(this, body + Loc.T("\n\nOpen the device page now?"), title, MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
            GoToDevicePage();
        else if (!offerDevicePage || _device is null)
            MessageDialog.Show(this, body, title, MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    /// <summary>Navigate to the connected iPod's device page (Read device ID / Restore / Save report live there).</summary>
    private void GoToDevicePage()
    {
        if (_device is null) return;
        TransitionCenter(() => { _viewKind = SidebarRowKind.Device; _current = null; BuildSidebar(); ShowCurrent(); });
    }

    private void UpdatePhotoStatus()
    {
        int sel = _photoView.SelectedIds.Count;
        int total = _photos?.Photos.Count ?? 0;
        SetStatus(sel > 0 ? Loc.T("{0} selected   ·   {1}", sel, PhotoSummary(total)) : PhotoSummary(total));
    }

    private static string PhotoSummary(int count) => CountNoun(count, "photo");

    /// <summary>Case-insensitive match of a track against the search query (title / artist / album).</summary>
    private static bool Match(Track t, string q) =>
        t.DisplayTitle.Contains(q, StringComparison.OrdinalIgnoreCase)
        || (t.Artist?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
        || (t.Album?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false);

    // ---- selectable cover art (playlists + the library) ----

    /// <summary>The settings key a chosen cover is stored under (per playlist, or per library/device).</summary>
    private string? CoverKeyFor(SidebarRowKind kind, Playlist? pl)
    {
        if (kind == SidebarRowKind.AllSongs)
        {
            if (_db is null) return null;
            if (_db.PersistentId != 0) return "lib:" + _db.PersistentId.ToString("X");
            // Some iPods report a 0 library id; key by a stable device identity so the cover doesn't bleed across devices.
            string? id = _device?.Profile.FirewireGuid ?? _device?.Profile.SerialNumber ?? _device?.MountRoot;
            return id is null ? null : "libdev:" + id;
        }
        if (kind == SidebarRowKind.Playlist && pl is not null && pl.PersistentId != 0) return "pl:" + pl.PersistentId.ToString("X");
        return null;
    }

    /// <summary>The CoverArt id chosen for the active view, or -1 for the automatic (song-derived) cover.</summary>
    private int CurrentViewCover()
    {
        string? k = _viewKind switch
        {
            SidebarRowKind.AllSongs => CoverKeyFor(SidebarRowKind.AllSongs, null),
            SidebarRowKind.Playlist => CoverKeyFor(SidebarRowKind.Playlist, _current),
            _ => null,
        };
        return k is null ? -1 : _settings.GetCover(k);
    }

    /// <summary>Clicking the big header cover opens the picker for the current music view.</summary>
    private void OnHeaderArtClicked()
    {
        if (_viewKind == SidebarRowKind.LocalPlaylist)
        {
            if (_currentLocalPlaylist is { } lp) ChooseLocalCover(lp);
            return;
        }
        string? key = _viewKind switch
        {
            SidebarRowKind.AllSongs => CoverKeyFor(SidebarRowKind.AllSongs, null),
            SidebarRowKind.Playlist => CoverKeyFor(SidebarRowKind.Playlist, _current),
            _ => null,
        };
        if (key is null) return;
        string title = _viewKind == SidebarRowKind.AllSongs ? Loc.T("Cover for All songs") : Loc.T("Cover for “{0}”", _current?.Name);
        ChooseCover(key, title);
    }

    /// <summary>The settings key a local (PC-side) playlist's chosen cover is stored under, or null if it never got
    /// one (no id assigned yet). Read-only — never mints an id (so just listing the sidebar doesn't churn settings).</summary>
    private static string? LocalCoverKey(LocalPlaylistData lp) => string.IsNullOrEmpty(lp.Id) ? null : "lpl:" + lp.Id;

    /// <summary>Open the cover picker for a local playlist, minting its stable id on first use so the choice persists
    /// across renames. Mirrors the iPod-playlist cover flow (<see cref="OnHeaderArtClicked"/> / the sidebar menu).</summary>
    private void ChooseLocalCover(LocalPlaylistData lp)
    {
        if (string.IsNullOrEmpty(lp.Id)) { lp.Id = Guid.NewGuid().ToString("N"); _settings.Save(); }
        ChooseCover("lpl:" + lp.Id, Loc.T("Cover for “{0}”", lp.Name.Length == 0 ? Loc.T("Untitled") : lp.Name));
    }

    private void ChooseCover(string key, string title)
    {
        // Derive the bare name from the "Cover for X" caption so the cassette tile shows the real label.
        string sample = title.StartsWith("Cover for ", StringComparison.Ordinal) ? title["Cover for ".Length..].Trim('“', '”', '"', ' ') : title;
        using var dlg = new CoverPickerDialog(title, _settings.GetCover(key), sample);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        _settings.SetCover(key, dlg.SelectedCoverId); // -1 reverts to automatic
        BuildSidebar();
        ShowCurrent();
    }

    private void AddCoverItem(ContextMenuStrip m, string key, string title)
    {
        var cover = new ToolStripMenuItem(Loc.T("Choose cover…"));
        cover.Click += (_, _) => ChooseCover(key, title);
        m.Items.Add(cover);
    }

    /// <summary>Right-click on the empty PLAYLISTS area → create a new playlist.</summary>
    private void OnPlaylistAreaRightClick(Point screen)
    {
        if (_lib is null || _device is null) return;
        var m = ThemedMenu.New();
        if (!_device.Profile.CanWrite)
        {
            string why = _device.Profile.WriteBlockReason is { Length: > 0 } w ? w : Loc.T("This iPod is read-only.");
            m.Items.Add(new ToolStripMenuItem(Loc.T("Read-only — {0}", why)) { Enabled = false });
            m.Show(screen);
            return;
        }
        var nu = new ToolStripMenuItem(Loc.T("New playlist…"));
        nu.Click += (_, _) => CreatePlaylistWithTracks(new List<uint>());
        m.Items.Add(nu);
        m.Show(screen);
    }

    // ---- device info page (the "General"-style view shown when you click the iPod) ----

    private void ShowDevice()
    {
        SetCenter();
        _tracks.Rows.Clear();
        _hotRow = -1;
        if (_device is null) { _header.SetInfo("", "—", "", 0); SetActionButtons(); return; }
        var p = _device.Profile;

        long total = 0, free = 0;
        try { var di = new DriveInfo(_device.MountRoot); total = di.TotalSize; free = di.AvailableFreeSpace; } catch { }
        long music = 0, video = 0; int songCount = 0, videoCount = 0;
        if (_db is not null)
            foreach (var t in _db.Tracks)
                if (MediaType.IsVideo(t.MediaType)) { video += t.FileSize; videoCount++; }
                else { music += t.FileSize; songCount++; }
        long photoBytes = PhotoBytes();
        int photoCount = _photos?.Photos.Count ?? 0;
        long other = Math.Max(0, total - free - music - video - photoBytes);

        string cap = total > 0 ? Loc.T("{0} of {1} used", CapacityBar.Human(total - free), CapacityBar.Human(total)) : Loc.T("Connected");
        _header.SetInfo(Loc.T("DEVICE"), p.ModelName ?? p.ModelNumber ?? "iPod", cap, Theme.StableHash(p.ModelName ?? "iPod"));
        using (var art = IpodArt.Render(p.Generation, 150, p.ModelNumber)) _header.SetArt(art); // a picture of THIS iPod, in its real colour
        _header.ArtClickable = false;

        BuildDeviceView(p, total, free, music, video, photoBytes, other, songCount, videoCount, photoCount);
        SetActionButtons();
        // The device name is already the header title + the sidebar row — don't echo it at the bottom.
        // Show a useful at-a-glance summary instead (consistent with the song views' status line).
        var bits = new List<string> { CountNoun(songCount, "song") };
        if (videoCount > 0) bits.Add(CountNoun(videoCount, "video"));
        if (photoCount > 0) bits.Add(CountNoun(photoCount, "photo"));
        if (total > 0) bits.Add(Loc.T("{0} free", CapacityBar.Human(free)));
        SetStatus(string.Join("   ·   ", bits));
    }

    private long PhotoBytes()
    {
        if (_device is null) return 0;
        try { string td = Path.Combine(_device.MountRoot, "Photos", "Thumbs"); if (Directory.Exists(td)) return Directory.GetFiles(td, "F*.ithmb").Sum(f => new FileInfo(f).Length); }
        catch { }
        return 0;
    }

    private void BuildDeviceView(DeviceProfile p, long total, long free, long music, long video, long photo, long other, int songCount, int videoCount, int photoCount)
    {
        var dev = _device!; // captured so button handlers don't deref a field that may be nulled later
        _deviceScrollPanel.SuspendLayout();
        _deviceScrollPanel.BackColor = Theme.Bg; // field init baked the default Graphite; honor the saved theme variant
        // Dispose the previous page's controls (Region/GDI handles) before detaching them.
        var oldControls = _deviceScrollPanel.Controls.Cast<Control>().ToArray();
        _deviceScrollPanel.Controls.Clear();
        foreach (var c in oldControls) c.Dispose();
        const int cardW = 540, x = 24;
        int y = 18;
        void SectionLabel(string t)
        {
            _deviceScrollPanel.Controls.Add(new Label { Text = t, Font = Theme.UiFont(8f, FontStyle.Bold), ForeColor = Theme.Faint, AutoSize = false, Left = x + 4, Top = y, Width = cardW, Height = 20, TextAlign = ContentAlignment.BottomLeft });
            y += 24;
        }
        void Add(Control c) { c.Left = x; c.Top = y; _deviceScrollPanel.Controls.Add(c); y += c.Height + 18; }

        if (total > 0)
        {
            SectionLabel(Loc.T("STORAGE"));
            var hero = new DeviceHero { Width = cardW };
            // No iPod picture here — the page header already shows one; the hero centres its capacity donut instead.
            hero.Set(null, total, free,
                new DeviceHero.Seg(Loc.T("Music"), music, Theme.Accent),
                new DeviceHero.Seg(Loc.T("Video"), video, Color.FromArgb(255, 149, 56)),
                new DeviceHero.Seg(Loc.T("Photos"), photo, Color.FromArgb(54, 200, 110)),
                new DeviceHero.Seg(Loc.T("Free"), free + other, Theme.Blend(Theme.Bg, Color.White, 0.07))); // "other" folded into free
            Add(hero);
        }

        SectionLabel(Loc.T("ABOUT"));
        var about = new CardPanel(cardW);
        about.AddInfoRow(Loc.T("Model"), p.ModelName ?? p.ModelNumber ?? "iPod");
        about.AddInfoRow(Loc.T("Generation"), p.GenerationDisplay);
        if (total > 0) about.AddInfoRow(Loc.T("Capacity"), Loc.T("{0}  ·  {1} free", CapacityBar.Human(total), CapacityBar.Human(free)));
        about.AddInfoRow(Loc.T("Songs"), songCount.ToString());
        if (p.SupportsVideo) about.AddInfoRow(Loc.T("Videos"), videoCount.ToString());
        if (p.SupportsPhotos) about.AddInfoRow(Loc.T("Photos"), photoCount.ToString());
        about.AddInfoRow(Loc.T("Signature"), p.SchemeLabel);
        about.AddInfoRow(Loc.T("Writable"), p.CanWrite ? Loc.T("Yes") : Loc.T("No"));
        if (!string.IsNullOrEmpty(p.SerialNumber)) about.AddInfoRow(Loc.T("Serial"), p.SerialNumber!);
        if (!string.IsNullOrEmpty(p.FirewireGuid)) about.AddInfoRow(Loc.T("FireWire GUID"), p.FirewireGuid!);
        about.Finish(); Add(about);

        if (!p.CanWrite && p.WriteBlockReason.Length > 0)
        {
            var ro = new CardPanel(cardW);
            ro.AddRow(Loc.T("Why read-only"), p.WriteBlockReason, null, 72);
            ro.Finish(); Add(ro);
        }

        // hash58 device whose GUID isn't on disk → offer to read it straight from the firmware (no iTunes).
        if (!p.CanWrite && p.Scheme == ChecksumScheme.Hash58 && string.IsNullOrEmpty(p.FirewireGuid)
            && dev.MountRoot.Length > 1 && dev.MountRoot[1] == ':')
        {
            var fix = new CardPanel(cardW);
            var fixBtn = new ThemedButton { Text = Loc.T("Read device ID"), Pill = true, Primary = true, Width = 150, Height = 32 };
            fixBtn.Click += (_, _) => EnableWritingByReadingDeviceId(dev);
            fix.AddRow(Loc.T("Enable writing"), Loc.T("Read this iPod's hardware ID directly from the device (a safe, read-only query — the same thing iTunes does) so music can be written. No iTunes needed."), fixBtn, 76);
            fix.Finish(); Add(fix);
        }

        SectionLabel(Loc.T("BACKUPS"));
        var backups = new CardPanel(cardW);
        string db = dev.ITunesDbPath, bak = db + ".bak", orig = db + ".original";
        string status = File.Exists(bak) ? Loc.T("Last automatic backup: {0}.", File.GetLastWriteTime(bak).ToString("g")) : File.Exists(orig) ? Loc.T("Original database kept.") : Loc.T("No backup yet.");
        // No manual "back up now": Mixtape already snapshots iTunesDB.original once and rolls iTunesDB.bak
        // before every write, so a manual copy here would only risk overwriting that good rollback point.
        var restoreBtn = new ThemedButton { Text = Loc.T("Restore…"), Pill = true, Width = 110, Height = 30, Enabled = File.Exists(bak) || File.Exists(orig) };
        restoreBtn.Click += (_, _) => RestoreDatabaseBackup();
        backups.AddRow(Loc.T("Automatic backup"), status + Loc.T(" Mixtape backs up before every change and verifies the result."), restoreBtn, 64);
        backups.Finish(); Add(backups);

        SectionLabel(Loc.T("OPTIONS"));
        var options = new CardPanel(cardW);
        var ejectBtn = new ThemedButton { Text = Loc.T("⏏  Eject"), Pill = true, Primary = true, Width = 120, Height = 30 };
        ejectBtn.Click += (_, _) => EjectDevice();
        options.AddRow(Loc.T("Safely remove"), Loc.T("Flush changes and eject so you can unplug the iPod safely."), ejectBtn, 56);
        var settingsBtn = new ThemedButton { Text = Loc.T("Open Settings"), Pill = true, Width = 130, Height = 30 };
        settingsBtn.Click += (_, _) => OpenSettings();
        options.AddRow(Loc.T("All settings"), Loc.T("Appearance, library, video, photos and safety."), settingsBtn, 56);
        var explorerBtn = new ThemedButton { Text = Loc.T("Show in Explorer"), Pill = true, Width = 150, Height = 30 };
        explorerBtn.Click += (_, _) =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{dev.MountRoot}\"") { UseShellExecute = true }); } catch { }
        };
        options.AddRow(Loc.T("Files"), Loc.T("Open the iPod's drive in File Explorer."), explorerBtn, 56);
        var reportBtn = new ThemedButton { Text = Loc.T("Save report…"), Pill = true, Width = 130, Height = 30 };
        reportBtn.Click += (_, _) => SaveDeviceReport(dev);
        options.AddRow(Loc.T("Device report"), Loc.T("Save a diagnostic file (model, signature, why it's read-only) to send for support."), reportBtn, 56);
        if (dev.Profile.SupportsArtwork)
        {
            var artBtn = new ThemedButton { Text = Loc.T("Rebuild artwork"), Pill = true, Width = 150, Height = 30, Enabled = dev.Profile.CanWrite };
            artBtn.Click += (_, _) => RebuildArtwork(dev);
            options.AddRow(Loc.T("Album artwork"), Loc.T("Write cover art onto every song already on the iPod (from each file's embedded cover). Handy after copying songs from an older version."), artBtn, 64);
        }
        var docBtn = new ThemedButton { Text = Loc.T("Check library"), Pill = true, Width = 150, Height = 30, Enabled = dev.Profile.CanWrite };
        docBtn.Click += (_, _) => OpenLibraryDoctor();
        options.AddRow(Loc.T("Library Doctor"), Loc.T("Scan for missing files, duplicate songs and stray files left on the iPod — then fix them safely in one click."), docBtn, 64);
        options.Finish(); Add(options);

        _deviceScrollPanel.Top = 0;        // start at the top
        _deviceScrollPanel.Height = y + 8; // total content height (+ a little bottom breathing room)
        _deviceScrollPanel.ResumeLayout();
        LayoutDeviceView();
    }

    /// <summary>Lay out the device viewport: content panel fills the width; the slim custom scrollbar sits on
    /// the right; the scroll offset is clamped if the content now fits.</summary>
    private void LayoutDeviceView()
    {
        int vw = _deviceView.ClientSize.Width, vh = _deviceView.ClientSize.Height;
        if (vw <= 0 || vh <= 0) return;
        const int sb = 12;
        int max = Math.Max(0, _deviceScrollPanel.Height - vh);
        bool needBar = max > 0;
        _deviceScrollPanel.Left = 0;
        _deviceScrollPanel.Width = vw - (needBar ? sb : 0); // leave the bar's strip uncovered so it isn't hidden
        if (-_deviceScrollPanel.Top > max) _deviceScrollPanel.Top = -max; // don't strand the content past its end
        _deviceScroll.Visible = needBar;
        _deviceScroll.Bounds = new Rectangle(vw - sb, 0, sb, vh);
        if (needBar) _deviceScroll.BringToFront();
        _deviceScroll.Invalidate();
    }

    /// <summary>Write cover art onto every track already on the iPod (clean rebuild of the ArtworkDB from
    /// each file's embedded cover). Runs off the UI thread; reloads the device afterwards.</summary>
    private async void RebuildArtwork(IPodDevice dev)
    {
        if (!dev.Profile.CanWrite) return;
        if (MessageDialog.Show(this,
            Loc.T("This writes album art onto every song already on the iPod, reading the cover embedded in each music file.\n\nSongs whose files have no embedded cover are left without art. Mixtape backs up the database first. Continue?"),
            Loc.T("Rebuild artwork"), MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK) return;

        SetStatus(Loc.T("Rebuilding artwork…"));
        UseWaitCursor = true;
        (int added, int noFile, int noArt, string? error) result;
        try
        {
            result = await System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    var lib = IpodLibrary.Load(dev);
                    var art = ArtworkLibrary.Load(dev);
                    art.Force = true;
                    lib.Artwork = art;
                    if (!art.SupportsArtwork) return (0, 0, 0, Loc.T("This iPod's artwork database can't be written safely."));
                    art.StartCleanRebuild();
                    lib.Raw.ClearAllTrackArtwork();
                    int added = 0, noFile = 0, noArt = 0;
                    foreach (var t in lib.View.Tracks)
                    {
                        string? path = t.ResolveFilePath(dev.MountRoot);
                        if (path is null || !File.Exists(path)) { noFile++; continue; }
                        var staged = art.Stage(t.Dbid, path);
                        if (staged is null) { noArt++; continue; }
                        if (lib.Raw.SetTrackArtwork(t.UniqueId, staged.Value.MhiiId, staged.Value.Size)) added++;
                    }
                    if (added > 0) lib.Save();
                    return (added, noFile, noArt, (string?)null);
                }
                catch (Exception ex) { return (0, 0, 0, (string?)ex.Message); }
            });
        }
        finally { UseWaitCursor = false; }

        if (result.error is { } err)
        {
            SetStatus(Loc.T("Rebuild artwork failed."));
            MessageDialog.Show(this, Loc.T("Couldn't rebuild artwork:\n\n{0}", err), Loc.T("Rebuild artwork"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        SetStatus(Loc.T("Artwork rebuilt for {0} song(s).", result.added));
        if (ReferenceEquals(_device, dev)) LoadDevice(dev); // refresh from the rewritten database
        MessageDialog.Show(this,
            Loc.T("Wrote cover art for {0} song(s).", result.added) +
            (result.noArt > 0 ? Loc.T("\n{0} song(s) had no embedded cover in the file.", result.noArt) : "") +
            Loc.T("\n\nEject the iPod, then check the Now Playing screen."),
            Loc.T("Rebuild artwork"), MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    /// <summary>Library Doctor: scan the library for problems (off the UI thread), let the user pick safe
    /// fixes, then apply them through the normal delete+save path and reload the device.</summary>
    private async void OpenLibraryDoctor()
    {
        var lib = _lib; var dev = _device; var photoLib = _photos;
        if (lib is null || dev is null) return;

        SetStatus(Loc.T("Checking library…"));
        UseWaitCursor = true;
        DoctorReport report;
        try { report = await System.Threading.Tasks.Task.Run(() => LibraryDoctor.Scan(lib, photoLib)); }
        catch (Exception ex)
        {
            UseWaitCursor = false; SetStatus("");
            MessageDialog.Show(this, Loc.T("Couldn't scan the library:\n\n{0}", ex.Message), Loc.T("Library Doctor"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        UseWaitCursor = false; SetStatus("");

        DoctorPlan? plan;
        using (var dlg = new LibraryDoctorDialog(report))
        {
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            plan = dlg.Plan;
        }
        if (plan is not { HasActions: true }) return;
        if (!ConfirmWriteOnce()) return;

        // Resolve the files we're allowed to physically delete: a duplicate's file may, in odd libraries,
        // be shared by a track we're KEEPING — never delete a file a survivor still references.
        var deleteIds = new HashSet<uint>(plan.DeleteDupTracks);
        var survivors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in lib.View.Tracks)
            if (!deleteIds.Contains(t.UniqueId))
            {
                string? p = t.ResolveFilePath(dev.MountRoot);
                if (!string.IsNullOrEmpty(p)) { try { survivors.Add(Path.GetFullPath(p)); } catch { } }
            }

        SetStatus(Loc.T("Fixing library…"));
        UseWaitCursor = true;
        int rows = 0, dupes = 0, files = 0, photoDupes = 0; string? error = null;
        try
        {
            await System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    // Resolve duplicate files BEFORE removing their rows (resolution needs the live View).
                    var dupFiles = new List<string>();
                    foreach (var id in plan.DeleteDupTracks)
                    {
                        string? p = lib.View.FindByUniqueId(id)?.ResolveFilePath(dev.MountRoot);
                        if (!string.IsNullOrEmpty(p)) dupFiles.Add(p);
                    }
                    foreach (var id in plan.RemoveRows) { lib.DeleteTrack(id, deleteFile: false); rows++; }
                    foreach (var id in plan.DeleteDupTracks) { lib.DeleteTrack(id, deleteFile: false); dupes++; }
                    lib.Save();   // DB no longer references the dup/orphan files → now safe to delete them
                    foreach (var p in dupFiles.Concat(plan.DeleteFiles))
                    {
                        try { string fp = Path.GetFullPath(p); if (!survivors.Contains(fp) && File.Exists(fp)) { File.Delete(fp); files++; } }
                        catch { /* a leftover file is harmless; the DB no longer points at it */ }
                    }
                    // Duplicate photos: separate DB (no iTunesDB link), so removed via the photo library's own save.
                    if (plan.DeletePhotoIds.Count > 0 && photoLib is { SafeToWrite: true })
                    {
                        photoLib.DeletePhotos(plan.DeletePhotoIds);
                        photoLib.Save();
                        photoDupes = plan.DeletePhotoIds.Count;
                    }
                }
                catch (Exception ex) { error = ex.Message; }
            });
        }
        finally { UseWaitCursor = false; }

        if (error is not null)
        {
            SetStatus(Loc.T("Library fix failed."));
            MessageDialog.Show(this, Loc.T("Couldn't apply the fixes:\n\n{0}", error), Loc.T("Library Doctor"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        SetStatus(Loc.T("Library cleaned up."));
        if (ReferenceEquals(_device, dev)) LoadDevice(dev);   // refresh from the rewritten database
        var lines = new List<string>();
        if (rows > 0) lines.Add(Loc.T("• Removed {0} dead entries", rows));
        if (dupes > 0) lines.Add(Loc.T("• Removed {0} duplicates", dupes));
        if (photoDupes > 0) lines.Add(Loc.T("• Removed {0} duplicate photos", photoDupes));
        if (files > 0) lines.Add(Loc.T("• Cleared {0} stray files", files));
        MessageDialog.Show(this, Loc.T("Library cleaned up.") + "\n\n" + string.Join("\n", lines), Loc.T("Library Doctor"), MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    /// <summary>
    /// Writes a self-contained diagnostic the user can send for support: detection results plus the
    /// raw Device identity files (SysInfo / SysInfoExtended) and the iTunesDB header — enough to tell
    /// why a hash58 device opened read-only (no FireWire GUID found vs. a signature mismatch) without
    /// the user having to dig into the hidden iPod_Control folder. Contains device identifiers, so the
    /// dialog warns before saving.
    /// </summary>
    private async void SaveDeviceReport(IPodDevice dev)
    {
        if (MessageDialog.Show(this,
            Loc.T("This saves a diagnostic text file containing your iPod's identifiers (serial number and FireWire GUID).\n\nOnly share it with someone helping you fix the app. Continue?"),
            Loc.T("Save device report"), MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK) return;

        // BuildDeviceReportText does a blocking SCSI INQUIRY — run it off the UI thread so the window
        // never freezes, and only after the user confirmed (so a stalled device can't hang on cancel).
        string report;
        var prev = Cursor; Cursor = Cursors.WaitCursor;
        try { report = await System.Threading.Tasks.Task.Run(() => BuildDeviceReportText(dev)); }
        catch (Exception ex) { report = "Report generation failed:\n" + ex; }
        finally { Cursor = prev; }

        using var dlg = new SaveFileDialog
        {
            Title = Loc.T("Save device report"),
            FileName = "Mixtape device report.txt",
            Filter = Loc.T("Text file") + "|*.txt",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try { File.WriteAllText(dlg.FileName, report); SetStatus(Loc.T("Device report saved.")); }
        catch (Exception ex) { MessageDialog.Show(this, Loc.T("Couldn't save the report:\n\n{0}", ex.Message), Loc.T("Save report"), MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    /// <summary>Builds the diagnostic report text (pure; reused by the device-page button and --devreport).</summary>
    public static string BuildDeviceReportText(IPodDevice dev)
    {
        var p = dev.Profile;
        var sb = new System.Text.StringBuilder();
        void L(string s = "") => sb.AppendLine(s);

        L("=== Mixtape device report ===");
        L("This file contains your iPod's identifiers (serial, FireWire GUID). Share it only with");
        L("someone helping you fix the app.");
        L();
        L("Mount: " + dev.MountRoot);
        try { var di = new DriveInfo(dev.MountRoot); L($"Drive: {di.DriveFormat}, {di.TotalSize / 1e9:0.0} GB, {di.AvailableFreeSpace / 1e9:0.0} GB free"); } catch { }
        L();
        L("Model:        " + (p.ModelName ?? "(unknown)"));
        L("ModelNumber:  " + (p.ModelNumber ?? "(none)"));
        L("Generation:   " + p.GenerationDisplay + (p.IdentifiedBy is { } by ? $"  [{by}]" : ""));
        L("Signature:    " + p.SchemeLabel);
        L("CanWrite:     " + p.CanWrite);
        L("WriteBlocked: " + (p.WriteBlockReason.Length > 0 ? p.WriteBlockReason : "(none)"));
        L("Hash58Verified: " + (p.Hash58Verified?.ToString() ?? "(not checked)"));
        L("FireWireGUID: " + (string.IsNullOrEmpty(p.FirewireGuid) ? "(NOT FOUND)" : p.FirewireGuid));
        L("Serial:       " + (p.SerialNumber ?? "(none)"));
        L("MusicDirs:    " + p.MusicDirCount);
        L($"Supports:     video={p.SupportsVideo}  photos={p.SupportsPhotos}");
        L();

        string devDir = Path.Combine(dev.MountRoot, "iPod_Control", "Device");
        string siPath = Path.Combine(devDir, "SysInfo"), sxPath = Path.Combine(devDir, "SysInfoExtended");

        L("--- SysInfo (plain text) ---");
        try { L(File.Exists(siPath) ? File.ReadAllText(siPath).Trim() : "(missing)"); } catch (Exception ex) { L("(read error: " + ex.Message + ")"); }
        L();

        L("--- SysInfoExtended ---");
        try
        {
            if (!File.Exists(sxPath)) L("(missing)");
            else
            {
                byte[] raw = File.ReadAllBytes(sxPath);
                bool bin = raw.Length >= 6 && System.Text.Encoding.ASCII.GetString(raw, 0, 6) == "bplist";
                L($"exists: yes   size: {raw.Length} bytes   format: {(bin ? "binary plist" : "xml/text")}");
                if (bin)
                {
                    var map = BinaryPlist.TryFlattenTopDict(sxPath);
                    L("parsed top-level keys: " + (map is null ? "(parse failed)" : map.Count == 0 ? "(none)" : string.Join(", ", map.Keys)));
                    L("base64 (so support can re-parse it exactly):");
                    L(Convert.ToBase64String(raw));
                }
                else
                {
                    L("content:");
                    L(System.Text.Encoding.UTF8.GetString(raw).Trim());
                }
            }
        }
        catch (Exception ex) { L("(read error: " + ex.Message + ")"); }
        L();

        L("--- iTunesDB header ---");
        try
        {
            if (File.Exists(dev.ITunesDbPath))
            {
                byte[] head = new byte[0x34];
                using (var fs = File.OpenRead(dev.ITunesDbPath)) { int n = fs.Read(head, 0, head.Length); Array.Resize(ref head, n); }
                L("first 0x34 bytes: " + Convert.ToHexString(head));
                if (head.Length > 0x30) L("hashing_scheme @0x30: " + head[0x30]);
            }
            else L("(missing)");
        }
        catch (Exception ex) { L("(read error: " + ex.Message + ")"); }
        L();

        // Live, read-only SCSI INQUIRY for the FireWire GUID — so one report says whether the device
        // hands over its ID (and what it is) without writing anything yet.
        L("--- Live device-ID read (SCSI INQUIRY 0xC0, read-only) ---");
        if (dev.MountRoot.Length > 1 && dev.MountRoot[1] == ':')
        {
            char drv = dev.MountRoot[0];
            byte[]? doc = null;
            foreach (byte tid in new byte[] { 0, 1 })
            {
                try { doc = IpodGuidReader.ReadSysInfoExtendedScsi(drv, tid); L($"targetId={tid}: OK — {doc.Length} bytes"); break; }
                catch (Exception ex) { L($"targetId={tid}: FAILED — {ex.Message}"); }
            }
            if (doc is { Length: > 0 })
            {
                L("first bytes: " + Convert.ToHexString(doc, 0, Math.Min(16, doc.Length)));
                L("FireWireGUID (parsed): " + (IpodGuidReader.ExtractFireWireGuid(doc) ?? "(NOT found)"));
                L("base64 of read document:");
                L(Convert.ToBase64String(doc));
            }
        }
        else L("(skipped — not a drive-letter mount)");

        return sb.ToString();
    }

    /// <summary>
    /// For a hash58 iPod with no GUID on disk: read the device's SysInfoExtended (incl. FireWireGUID)
    /// straight from the firmware over USB (a safe, read-only SCSI query — the libgpod approach), write
    /// it to iPod_Control/Device/SysInfoExtended so the device looks normal, then re-detect so the
    /// signature check runs and writing can enable. No iTunes required.
    /// </summary>
    private void EnableWritingByReadingDeviceId(IPodDevice dev)
    {
        string root = dev.MountRoot;
        if (root.Length < 2 || root[1] != ':') { MessageDialog.Show(this, Loc.T("This only works for an iPod mounted on a drive letter."), Loc.T("Enable writing"), MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        char drive = root[0];
        if (MessageDialog.Show(this,
            Loc.T("Mixtape will read this iPod's hardware ID directly from the device — a safe, read-only query (the same one iTunes uses) — and save it to the iPod so music can be written. Nothing on the iPod is changed except adding the standard device-info file.\n\nContinue?"),
            Loc.T("Enable writing"), MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK) return;

        byte[]? doc = null; string? err = null;
        var prev = Cursor; Cursor = Cursors.WaitCursor;
        try
        {
            foreach (byte tid in new byte[] { 0, 1 })
            {
                try { doc = IpodGuidReader.ReadSysInfoExtendedScsi(drive, tid); break; }
                catch (Exception ex) { err = ex.Message; }
            }
        }
        finally { Cursor = prev; }

        string? guid = doc is { Length: > 0 } ? IpodGuidReader.ExtractFireWireGuid(doc) : null;
        if (guid is null)
        {
            MessageDialog.Show(this,
                Loc.T("Couldn't read a hardware ID from this iPod.\n\n") + (err ?? Loc.T("The device didn't return a FireWire GUID.")) +
                Loc.T("\n\nYou can still use the iPod read-only (browse, play, copy music off). Use \"Save report…\" to capture details."),
                Loc.T("Enable writing"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        // Validate with the SAME parser re-detection uses, so we never persist a doc the firmware-read
        // happened to regex-match but DeviceDetector can't actually re-read into a GUID.
        if (SysInfoExtended.TryParse(doc!)?.FirewireGuid is null)
        {
            MessageDialog.Show(this,
                Loc.T("Read the device ID ({0}), but the device-info document the iPod returned isn't in a form Mixtape can reliably re-read, so it was NOT saved to the iPod.\n\nPlease use \"Save report…\" and send it.", guid),
                Loc.T("Enable writing"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        try
        {
            string devDir = Path.Combine(root, "iPod_Control", "Device");
            Directory.CreateDirectory(devDir);
            File.WriteAllBytes(Path.Combine(devDir, "SysInfoExtended"), doc!);
        }
        catch (Exception ex)
        {
            MessageDialog.Show(this, Loc.T("Read the device ID ({0}) but couldn't save it to the iPod:\n\n{1}", guid, ex.Message), Loc.T("Enable writing"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        // Re-detect from scratch so the new SysInfoExtended is parsed and the hash58 check re-runs.
        var rebuilt = DeviceDetector.Build(root);
        if (rebuilt is not null) LoadDevice(rebuilt);

        var p = _device?.Profile;
        if (p?.CanWrite == true && p.Hash58Verified == true)
            MessageDialog.Show(this, Loc.T("Success — read device ID {0} and verified the signature against this iPod's database. Writing is now enabled.", guid), Loc.T("Enable writing"), MessageBoxButtons.OK, MessageBoxIcon.Information);
        else if (p?.CanWrite == true)
            MessageDialog.Show(this, Loc.T("Read device ID {0}. Writing is now enabled.", guid), Loc.T("Enable writing"), MessageBoxButtons.OK, MessageBoxIcon.Information);
        else if (p?.Scheme == ChecksumScheme.Hash58 && p.Hash58Verified == false)
            MessageDialog.Show(this, Loc.T("Read the device ID ({0}), but Mixtape's hash58 signature didn't match this iPod's existing one, so writing stays disabled to avoid corrupting its database.\n\nThis iPod is the first real-world test of hash58 signing — please use \"Save report…\" and send it so the signing can be fixed.", guid), Loc.T("Enable writing"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
        else
            MessageDialog.Show(this, Loc.T("Read the device ID ({0}). Writing is still disabled — see the reason on this page.", guid), Loc.T("Enable writing"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private void RestoreDatabaseBackup()
    {
        if (_device is null) return;
        string db = _device.ITunesDbPath, bak = db + ".bak", orig = db + ".original";
        string? source = File.Exists(bak) ? bak : File.Exists(orig) ? orig : null;
        if (source is null) { MessageDialog.Show(this, Loc.T("No database backup was found on this iPod yet."), Loc.T("Restore"), MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        string which = source == bak ? Loc.T("the state before the last change (iTunesDB.bak)") : Loc.T("the original database from before Mixtape first wrote to it");
        if (MessageDialog.Show(this, Loc.T("Restore {0}?\n\nThe current database will be replaced.", which), Loc.T("Restore database"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        try { File.Copy(source, db, overwrite: true); }
        catch (Exception ex) { MessageDialog.Show(this, Loc.T("Restore failed:\n\n{0}", ex.Message), Loc.T("Restore"), MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
        ReloadCurrentDevice();
        SetStatus(Loc.T("Database restored."));
    }

    /// <summary>Star rating as glyphs (rating is stars×20). Unrated → blank, like iTunes.</summary>
    private static string RatingStars(byte rating)
    {
        int stars = Math.Min(5, rating / 20);
        return stars > 0 ? new string('★', stars) + new string('☆', 5 - stars) : "";
    }

    /// <summary>Compact ISO date for the Added column; blank when there's no (valid) date.</summary>
    // Conversational dates (matches the app's "185 songs · 11 hr" voice) instead of raw yyyy-MM-dd.
    // English month names via InvariantCulture so it stays consistent with the English UI (a localised
    // "ddd" weekday collapses to a cryptic single letter in some cultures, e.g. Hungarian "P").
    private static string DateAddedStr(DateTime? d)
    {
        if (d is not { } dt || dt.Year <= 1970) return "";
        var today = DateTime.Today;
        var day = dt.Date;
        if (day == today) return Loc.T("Today");
        if (day == today.AddDays(-1)) return Loc.T("Yesterday");
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        return dt.Year == today.Year ? dt.ToString("MMM d", ci) : dt.ToString("MMM d, yyyy", ci);  // Mar 27 / Mar 27, 2025
    }

    /// <summary>Show count + total time + size in the status bar while several songs are selected.</summary>
    private void OnTrackSelectionChanged()
    {
        if (_populatingGrid) return;
        if (_tracks.Parent is Control gh && !gh.Visible) return; // only when the track grid is the visible centre
        int n = _tracks.SelectedRows.Count;
        if (n <= 1) { SetStatus(_baseStatus, _baseStatusClickable); return; }
        long ms = 0, bytes = 0;
        foreach (DataGridViewRow row in _tracks.SelectedRows)
            if (row.Tag is Track t) { ms += t.LengthMs; bytes += t.FileSize; }
        SetStatus(Loc.T("{0} selected   ·   {1}   ·   {2}", n, FormatDur(ms), CapacityBar.Human(bytes)));
    }

    private static string FormatDur(long ms)
    {
        var t = TimeSpan.FromMilliseconds(ms);
        return t.TotalHours >= 1 ? Loc.T("{0} hr {1} min", (int)t.TotalHours, t.Minutes)
            : t.TotalMinutes >= 1 ? Loc.T("{0} min {1} s", t.Minutes, t.Seconds)
            : Loc.T("{0} s", t.Seconds);
    }

    /// <summary>Soft pre-flight space check before copying files on. Over-estimates for files that will be
    /// transcoded smaller, so it only asks (OK/Cancel) rather than blocking.</summary>
    private bool SpaceOkToAdd(string[] files)
    {
        if (_device is null) return true;
        long need, free;
        try
        {
            need = files.Where(File.Exists).Sum(f => { try { return new FileInfo(f).Length; } catch { return 0L; } });
            free = new DriveInfo(_device.MountRoot).AvailableFreeSpace;
        }
        catch { return true; } // can't tell → don't get in the way
        if (need <= free) return true;
        return MessageDialog.Show(this,
            Loc.T("These files are about {0}, but the iPod has only {1} free.\n\nThey might not all fit. (Converted media usually ends up smaller, so it may still work.)\n\nTry anyway?", CapacityBar.Human(need), CapacityBar.Human(free)),
            Loc.T("Not enough space?"), MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) == DialogResult.OK;
    }

    private void SortTracks(List<Track> list)
    {
        if (_sortCol < 1) return; // playlist order
        Comparison<Track> cmp = _sortCol switch
        {
            1 => (a, b) => string.Compare(a.DisplayTitle, b.DisplayTitle, StringComparison.OrdinalIgnoreCase),
            2 => (a, b) => string.Compare(a.Artist ?? "", b.Artist ?? "", StringComparison.OrdinalIgnoreCase),
            3 => (a, b) => string.Compare(a.Album ?? "", b.Album ?? "", StringComparison.OrdinalIgnoreCase),
            4 => (a, b) => a.Rating.CompareTo(b.Rating),
            5 => (a, b) => a.PlayCount.CompareTo(b.PlayCount),
            6 => (a, b) => Nullable.Compare(a.DateAdded, b.DateAdded),
            7 => (a, b) => a.LengthMs.CompareTo(b.LengthMs),
            _ => (_, _) => 0,
        };
        list.Sort(cmp);
        if (!_sortAsc) list.Reverse();
    }

    private void UpdateSortIndicators()
    {
        for (int i = 1; i < _tracks.Columns.Count && i < ColBase.Length; i++)
            _tracks.Columns[i].HeaderText = i == _sortCol ? ColBase[i] + (_sortAsc ? "  ↑" : "  ↓") : ColBase[i];
        _trackHeader?.Invalidate();   // the fixed header draws these labels + arrows
    }

    /// <summary>Background-loads real embedded cover art into the header + each row (album-cached, gen-guarded).</summary>
    private void LoadArtworkAsync(List<Track> tags, int size)
    {
        if (!_settings.ShowArtwork || tags.Count == 0) return;   // works for Local Music too — no iPod required
        int gen = ++_artGen;
        string? mount = _device?.MountRoot;
        // Local Music tracks live on the PC (LocalPath); iPod tracks resolve against the mount.
        string? PathOf(Track t) => !string.IsNullOrEmpty(t.LocalPath) ? t.LocalPath
                                   : mount is not null ? t.ResolveFilePath(mount) : null;
        var jobs = tags.Select((t, i) => (Index: i, Key: ArtworkService.KeyFor(t), Path: PathOf(t))).ToList();
        var first = tags[0];

        Task.Run(() =>
        {
            var hdr = ArtworkService.Load(ArtworkService.KeyFor(first), PathOf(first), 150);
            if (hdr != null) TryBeginInvoke(() => { if (_artGen == gen && !_currentHasCustomCover) _header.SetArt(hdr); });
            foreach (var j in jobs)
            {
                if (_artGen != gen) return;
                if (string.IsNullOrEmpty(j.Path)) continue;
                var art = ArtworkService.Load(j.Key, j.Path, size);
                if (art != null)
                {
                    int idx = j.Index;
                    TryBeginInvoke(() => { if (_artGen == gen && idx < _tracks.Rows.Count) _tracks.Rows[idx].Cells[0].Value = art; });
                }
            }
        });
    }

    private void TryBeginInvoke(Action a)
    {
        try { if (IsHandleCreated) BeginInvoke(a); } catch { /* form closing */ }
    }

    private static string Summary(int count, long totalMs, string noun = "song")
    {
        var span = TimeSpan.FromMilliseconds(totalMs);
        string dur = span.TotalHours >= 1
            ? (span.Minutes == 0 ? Loc.T("{0} hr", (int)span.TotalHours) : Loc.T("{0} hr {1} min", (int)span.TotalHours, span.Minutes))
            : Loc.T("{0} min", span.Minutes);
        return $"{CountNoun(count, noun)}  ·  {dur}";
    }

    /// <summary>"185 songs" / "1 video" — English pluralizes with "s"; Hungarian (and other no-count-plural
    /// languages) use a single translated "{0} songs"/"{0} videos" template that reads right for any count.</summary>
    private static string CountNoun(int count, string noun)
    {
        if (Loc.Lang != "en")
            return Loc.T(noun switch { "video" => "{0} videos", "album" => "{0} albums", "artist" => "{0} artists", "photo" => "{0} photos", _ => "{0} songs" }, count);
        return $"{count} {noun}{(count == 1 ? "" : "s")}";
    }

    // ---- mutations ----

    private void OnAddMusic()
    {
        if (_lib is null || _device is null || !_device.Profile.CanWrite) return;
        using var dlg = new OpenFileDialog
        {
            Multiselect = true,
            Title = Loc.T("Select music to copy onto the iPod"),
            Filter = Loc.T("Audio files") + "|*.mp3;*.m4a;*.aac;*.wav;*.aif;*.aiff;*.m4b;*.flac;*.ogg;*.oga;*.opus;*.wma;*.ape;*.wv;*.mpc|" + Loc.T("All files") + "|*.*",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK || dlg.FileNames.Length == 0) return;
        AddMusicFiles(dlg.FileNames);
    }

    /// <summary>Core music-add (shared by the button and drag-and-drop): confirm, copy/transcode on a background thread, save.</summary>
    private void AddMusicFiles(string[] files)
    {
        if (_lib is null || _device is null || !_device.Profile.CanWrite || files.Length == 0) return;

        files = FilterAlreadyOnIpod(files, Loc.T("songs"), isVideo: false); // skip/keep duplicates already on the iPod
        if (files.Length == 0) return;

        // FLAC/OGG/Opus/WMA (or "always re-encode") need ffmpeg → AAC; mp3/m4a/wav/aiff copy as-is.
        var ffmpeg = FfmpegService.Detect(_settings.FfmpegPath);
        bool anyNeedsTranscode = files.Any(f => !IsNativeAudio(f)); // only non-native files truly require ffmpeg
        if (ffmpeg is null && anyNeedsTranscode)
        {
            var r = MessageDialog.Show(this,
                Loc.T("Some of these need converting to an iPod format (e.g. FLAC / OGG / Opus / WMA), but ffmpeg wasn't found.\n\n• Install ffmpeg (`winget install Gyan.FFmpeg`) and it's picked up automatically, or set its path in Settings ▸ Video.\n\nContinue and copy just the already-compatible files (mp3, m4a, wav, aiff)?"),
                Loc.T("ffmpeg not found"), MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
            if (r != DialogResult.OK) return;
        }
        if (!SpaceOkToAdd(files)) return;
        if (!ConfirmWriteOnce()) return;

        int ok = 0;
        var errors = new List<string>();
        string tempDir = Path.Combine(Path.GetTempPath(), "mixtape-audio");

        // *100 scale so transcoding shows a percentage; copy-only files just jump to the next step.
        using (var prog = new CopyProgressDialog(Loc.T("Adding music to your iPod"), files.Length * 100, (report, cancelled) =>
        {
            Directory.CreateDirectory(tempDir);
            for (int i = 0; i < files.Length; i++)
            {
                if (cancelled()) break;
                string src = files[i], name = Path.GetFileName(src);
                int baseP = i * 100;
                string? temp = null;
                try
                {
                    // Native files copy as-is; non-native (or "always re-encode") transcode — but only if ffmpeg
                    // exists. Without ffmpeg, native files still copy (honoring the warning dialog's promise);
                    // only genuinely non-native files are skipped.
                    bool transcode = (!IsNativeAudio(src) || _settings.AlwaysTranscode) && ffmpeg is not null;
                    if (!transcode && !IsNativeAudio(src))
                        throw new InvalidOperationException(Loc.T("needs ffmpeg to convert — skipped."));
                    if (transcode)
                    {
                        double dur = ffmpeg!.Probe(src)?.DurationSec ?? 0;
                        temp = Path.Combine(tempDir, Guid.NewGuid().ToString("N") + ".m4a");
                        report(baseP, Loc.T("Converting {0} of {1}   ·   {2}", i + 1, files.Length, name)); // advance even when duration is unknown
                        ffmpeg.TranscodeAudio(src, temp, 256, dur,
                            frac => report(baseP + (int)(frac * 98), Loc.T("Converting {0} of {1}   ·   {2}%   ·   {3}", i + 1, files.Length, (int)(frac * 100), name)),
                            cancelled);
                        report(baseP + 99, Loc.T("Copying {0} of {1}   ·   {2}", i + 1, files.Length, name));
                        // ffmpeg preserved the tags into the .m4a; keep the source's title as a safety net.
                        _lib!.AddMediaFile(temp, MediaType.Audio, MetadataExtractor.Read(src).Title, dur);
                    }
                    else
                    {
                        report(baseP, Loc.T("Copying {0} of {1}   ·   {2}", i + 1, files.Length, name));
                        _lib!.AddFile(src);
                    }
                    ok++;
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { errors.Add($"{name}: {ex.Message}"); }
                finally { if (temp is not null) { try { File.Delete(temp); } catch { } } }
            }
            if (ok > 0) { report(files.Length * 100, Loc.T("Saving the iPod database…")); _lib!.Save(); }
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
        }))
        {
            prog.ShowDialog(this);
            ReloadAfterEdit();

            if (prog.Error is not null)
            {
                MessageDialog.Show(this, Loc.T("Writing the database failed (a backup was kept as iTunesDB.bak):\n\n{0}", prog.Error.Message), "Mixtape", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            string msg = prog.WasCancelled ? Loc.T("Stopped — added {0} song(s).", ok) : Loc.T("Added {0} song(s).", ok);
            if (errors.Count > 0) msg += Loc.T("\n\n{0} could not be added:\n• ", errors.Count) + string.Join("\n• ", errors);
            MessageDialog.Show(this, msg, "Mixtape", MessageBoxButtons.OK, errors.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
        }
    }

    // ---- duplicate guard (shared by music + video add, for both the button and drag-and-drop) ----

    private static string NormKey(string? s)
    {
        s = (s ?? "").Trim().ToLowerInvariant();
        return string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)); // collapse whitespace
    }

    /// <summary>Identity for "the same item already on the iPod": music keys on title+artist+album; a video on its title (file name).</summary>
    private static string ItemKey(string? title, string? artist, string? album, bool isVideo)
        => isVideo ? "V" + NormKey(title)
                   : "A" + NormKey(title) + "" + NormKey(artist) + "" + NormKey(album);

    private static string ItemKeyFromAudioFile(string path)
    {
        var nt = MetadataExtractor.Read(path);
        return ItemKey(nt.Title, nt.Artist, nt.Album, isVideo: false);
    }

    /// <summary>If some incoming files are already on the iPod, ask whether to skip them or add anyway.
    /// Returns the files to actually add (possibly fewer); an empty array means "add nothing".</summary>
    private string[] FilterAlreadyOnIpod(string[] files, string mediaWord, bool isVideo)
    {
        if (_lib is null || files.Length == 0) return files;

        var existing = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in _lib.View.Tracks)
            if (MediaType.IsVideo(t.MediaType) == isVideo)
                existing.Add(ItemKey(t.Title, t.Artist, t.Album, isVideo));
        if (existing.Count == 0) return files; // nothing of this kind on the iPod yet → no dupes possible

        var fresh = new List<string>();
        var dups = new List<string>();
        bool prevCursor = UseWaitCursor; UseWaitCursor = true;
        try
        {
            foreach (var f in files)
            {
                string key;
                try { key = isVideo ? ItemKey(Path.GetFileNameWithoutExtension(f), null, null, true) : ItemKeyFromAudioFile(f); }
                catch { key = ""; } // unreadable tags → treat as new rather than wrongly skipping
                if (key.Length > 0 && existing.Contains(key)) dups.Add(f); else fresh.Add(f);
            }
        }
        finally { UseWaitCursor = prevCursor; }

        if (dups.Count == 0) return files; // nothing already present → add them all, no prompt

        string preview = string.Join("\n• ", dups.Take(8).Select(Path.GetFileName));
        if (dups.Count > 8) preview += Loc.T("\n• …and {0} more", dups.Count - 8);
        var r = MessageDialog.Show(this,
            Loc.T("{0} of these {1} {2} look like they're already on your iPod:\n\n• {3}\n\nYes   —   Skip the duplicates, add only what's new   (recommended)\nNo    —   Add everything anyway (you'll get duplicates)\nCancel —  Don't add anything", dups.Count, files.Length, mediaWord, preview),
            Loc.T("Already on your iPod"), MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);

        if (r == DialogResult.No) return files;                 // add anyway
        if (r == DialogResult.Cancel) return Array.Empty<string>();
        if (fresh.Count == 0) SetStatus(Loc.T("All {0} {1} are already on your iPod — nothing to add.", dups.Count, mediaWord));
        return fresh.ToArray();                                 // skip the duplicates
    }

    private void OnDelete()
    {
        if (_lib is null || _device is null || !_device.Profile.CanWrite) return;
        var ids = new List<uint>();
        foreach (DataGridViewRow row in _tracks.SelectedRows)
            if (row.Tag is Track t) ids.Add(t.UniqueId);
        if (ids.Count == 0) { SetStatus(Loc.T("Select one or more songs to delete.")); return; }

        var confirm = MessageDialog.Show(this,
            Loc.T("Delete {0} song(s) from the iPod, including the audio file(s)?\n\nThis can't be undone, but a backup of the database is kept (iTunesDB.bak).", ids.Count),
            Loc.T("Delete songs"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (confirm != DialogResult.Yes) return;
        if (!ConfirmWriteOnce()) return;

        try
        {
            Cursor = Cursors.WaitCursor;
            foreach (uint id in ids) _lib.DeleteTrack(id, deleteFile: true);
            _lib.Save();
        }
        catch (Exception ex)
        {
            MessageDialog.Show(this, Loc.T("Writing the database failed (a backup was kept as iTunesDB.bak):\n\n{0}", ex.Message), "Mixtape", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        finally { Cursor = Cursors.Default; }

        ReloadAfterEdit();
        SetStatus(Loc.T("Deleted {0} song(s).", ids.Count));
    }

    // ---- action dispatch (the header buttons adapt to the active view) ----

    private void OnAddClicked()
    {
        switch (_viewKind)
        {
            case SidebarRowKind.Videos: OnAddVideo(); break;
            case SidebarRowKind.Photos: ShowPhotoAddMenu(); break;
            case SidebarRowKind.LocalMusic: AddLocalFolder(); break;
            default: OnAddMusic(); break;
        }
    }

    /// <summary>The Photos "Add" button offers individual files or a whole folder (incl. subfolders).</summary>
    private void ShowPhotoAddMenu()
    {
        if (_photos is null || _device is null) return;
        if (!_photos.SafeToWrite) { MessageDialog.Show(this, _photos.BlockReason ?? Loc.T("Photos are read-only on this iPod."), "Mixtape", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        var m = ThemedMenu.New();
        var files = new ToolStripMenuItem(Loc.T("Add photos…")); files.Click += (_, _) => OnAddPhotos();
        var folder = new ToolStripMenuItem(Loc.T("Add folder…   (includes subfolders)")); folder.Click += (_, _) => OnAddPhotoFolder();
        var wall = new ToolStripMenuItem(Loc.T("Add wallpapers…")); wall.Click += (_, _) => OnAddWallpapers();
        m.Items.Add(files);
        m.Items.Add(folder);
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add(wall);
        var b = _header.AddButton;
        m.Show(b.PointToScreen(new Point(0, b.Height + 2)));
    }

    /// <summary>Pick a folder; add every image inside it and all its subfolders.</summary>
    private void OnAddPhotoFolder()
    {
        if (_photos is null || _device is null || !_photos.SafeToWrite) return;
        using var dlg = new FolderBrowserDialog { Description = Loc.T("Select a folder — every photo inside it and its subfolders will be added"), ShowNewFolderButton = false };
        if (dlg.ShowDialog(this) != DialogResult.OK || string.IsNullOrEmpty(dlg.SelectedPath)) return;

        string[] images;
        try { UseWaitCursor = true; images = GatherFiles(dlg.SelectedPath, ImageExt); }
        finally { UseWaitCursor = false; }

        if (images.Length == 0)
        {
            MessageDialog.Show(this, Loc.T("No photos (jpg, png, …) were found in that folder or its subfolders."), Loc.T("Add folder"), MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (MessageDialog.Show(this, Loc.T("Found {0} photo(s) in “{1}” (including subfolders).\n\nAdd them all to the iPod?", images.Length, Path.GetFileName(dlg.SelectedPath.TrimEnd(Path.DirectorySeparatorChar))),
                Loc.T("Add folder"), MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        AddPhotoFiles(images);
    }

    private void OnDeleteClicked()
    {
        if (_viewKind == SidebarRowKind.LocalMusic) ManageLocalFolders();
        else if (_viewKind == SidebarRowKind.Photos) OnDeletePhotos();
        else OnDelete();
    }

    // ---- Local Music (PC files browsed inside Mixtape) ----

    private readonly List<Track> _localTracks = new();
    private LocalPlaylistData? _currentLocalPlaylist;   // the local playlist being viewed (ON THIS PC)
    private int _localGen;
    private bool _localStale = true; // true → next ShowLocalMusic re-scans the folders (set on entry / folder change)

    private void ShowLocalMusic()
    {
        SetCenter();
        _hotRow = -1;
        _header.ArtClickable = false;
        _header.SetArt(null);
        _currentHasCustomCover = false;
        FillLocalGrid();                                          // show cached results instantly (sorted/filtered)
        if (_localStale) { _localStale = false; ScanLocalMusicAsync(); } // re-scan only when something may have changed
    }

    private void FillLocalGrid()
    {
        int artSize = _settings.Compact ? 22 : 36;
        var list = _localTracks.AsEnumerable();
        if (_searchQuery.Length > 0) list = list.Where(t => Match(t, _searchQuery));
        var shown = list.ToList();
        SortTracks(shown);
        UpdateSortIndicators();

        _tracks.SuspendLayout();
        _populatingGrid = true;
        _tracks.Rows.Clear();
        long totalMs = 0;
        foreach (var t in shown)
        {
            totalMs += t.LengthMs;
            object? thumb = _settings.ListArtwork ? Theme.MakeArt(artSize, Theme.StableHash(t.Album ?? t.DisplayTitle)) : null;   // no cover in compact (text-only)
            int r = _tracks.Rows.Add(thumb, t.DisplayTitle, t.Artist ?? "", t.Album ?? "",
                RatingStars(t.Rating), t.PlayCount > 0 ? t.PlayCount.ToString() : "", DateAddedStr(t.DateAdded), t.DurationStr);
            _tracks.Rows[r].Tag = t;
        }
        _populatingGrid = false;
        _tracks.ResumeLayout();
        SizeTracks();   // once, after the bulk populate
        if (_settings.ListArtwork) LoadArtworkAsync(shown, artSize);   // replace placeholder thumbs with real covers (skipped when text-only)

        int folders = _settings.LocalMusicFolders.Count;
        _emptyMsg = folders == 0 ? Loc.T("Click “Add folder” to add music from your PC.")
            : _searchQuery.Length > 0 ? Loc.T("No results for “{0}”", _searchQuery)
            : shown.Count == 0 ? Loc.T("No playable audio found in your folders.")
            : "";
        _header.SetInfo(Loc.T("ON THIS PC"), Loc.T("Local Music"),
            folders == 0 ? Loc.T("Music from folders on your PC") : Summary(shown.Count, totalMs, "song"), Theme.StableHash("Local Music"));
        // Song count is in the subtitle; the under-button line carries only the folder count (or the empty hint).
        string st = folders == 0 ? Loc.T("No folders added yet — click “Add folder”.")
            : folders == 1 ? Loc.T("{0} folder", folders) : Loc.T("{0} folders", folders);
        _baseStatus = st; _baseStatusClickable = false; SetStatus(st);
        SetActionButtons();
    }

    private void ScanLocalMusicAsync()
    {
        int gen = ++_localGen;
        var folders = _settings.LocalMusicFolders.ToList();
        if (folders.Count == 0) { _localTracks.Clear(); return; }
        if (_localTracks.Count == 0) SetStatus(Loc.T("Scanning your music…"));
        Task.Run(() =>
        {
            var found = new List<Track>();
            foreach (var dir in folders)
            {
                if (_localGen != gen) return;
                string[] files;
                try
                {
                    files = Directory.EnumerateFiles(dir, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
                        .Where(f => AudioExt.Contains(Path.GetExtension(f).ToLowerInvariant())).ToArray();
                }
                catch { continue; }
                foreach (var f in files)
                {
                    if (_localGen != gen) return;
                    Track t;
                    try
                    {
                        var nt = MetadataExtractor.Read(f, isVideo: false);
                        t = new Track { Title = !string.IsNullOrWhiteSpace(nt.Title) ? nt.Title : Path.GetFileNameWithoutExtension(f), Artist = nt.Artist, Album = nt.Album, LengthMs = nt.LengthMs };
                    }
                    catch { t = new Track { Title = Path.GetFileNameWithoutExtension(f) }; }
                    t.MediaType = MediaType.Audio;
                    t.LocalPath = f;
                    try { t.DateAdded = File.GetLastWriteTime(f); } catch { }
                    found.Add(t);
                }
            }
            if (_localGen != gen) return;
            TryBeginInvoke(() =>
            {
                if (_localGen != gen) return;
                _localTracks.Clear(); _localTracks.AddRange(found);
                if (_viewKind == SidebarRowKind.LocalMusic) FillLocalGrid();
            });
        });
    }

    // ---- local playlists (PC-side playlists shown under ON THIS PC) ----

    private void ShowLocalPlaylist()
    {
        SetCenter();
        _hotRow = -1;   // the header art (cover + clickability) is set by FillLocalPlaylistGrid / ShowLocalMusic below
        var lp = _currentLocalPlaylist;
        if (lp is null) { _viewKind = SidebarRowKind.LocalMusic; ShowLocalMusic(); return; }

        FillLocalPlaylistGrid(lp, ResolveLocalTracks(lp.Paths, cacheOnly: true)); // instant from cache
        int gen = ++_localGen;
        var paths = lp.Paths.ToList();
        Task.Run(() =>
        {
            var built = ResolveLocalTracks(paths, cacheOnly: false);
            if (_localGen != gen) return;
            TryBeginInvoke(() => { if (_localGen == gen && _viewKind == SidebarRowKind.LocalPlaylist && ReferenceEquals(_currentLocalPlaylist, lp)) FillLocalPlaylistGrid(lp, built); });
        });
    }

    /// <summary>Resolve file paths to tracks, reusing the Local Music scan cache; reads uncached files unless cacheOnly.</summary>
    private List<Track> ResolveLocalTracks(IEnumerable<string> paths, bool cacheOnly)
    {
        var cache = new Dictionary<string, Track>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in _localTracks) if (t.LocalPath is { } p) cache[p] = t;
        var result = new List<Track>();
        foreach (var p in paths)
        {
            if (cache.TryGetValue(p, out var c)) { result.Add(c); continue; }
            if (cacheOnly || !File.Exists(p)) continue;
            Track t;
            try { var nt = MetadataExtractor.Read(p, isVideo: false); t = new Track { Title = !string.IsNullOrWhiteSpace(nt.Title) ? nt.Title : Path.GetFileNameWithoutExtension(p), Artist = nt.Artist, Album = nt.Album, LengthMs = nt.LengthMs }; }
            catch { t = new Track { Title = Path.GetFileNameWithoutExtension(p) }; }
            t.MediaType = MediaType.Audio; t.LocalPath = p;
            try { t.DateAdded = File.GetLastWriteTime(p); } catch { }
            result.Add(t);
        }
        return result;
    }

    private void FillLocalPlaylistGrid(LocalPlaylistData lp, List<Track> tracks)
    {
        int artSize = _settings.Compact ? 22 : 36;
        var list = (_searchQuery.Length > 0 ? tracks.Where(t => Match(t, _searchQuery)) : tracks).ToList();
        SortTracks(list);          // honor a clicked column header; SortTracks no-ops (keeps playlist order) when none is chosen
        UpdateSortIndicators();
        _tracks.SuspendLayout(); _populatingGrid = true; _tracks.Rows.Clear();
        long totalMs = 0;
        foreach (var t in list)
        {
            totalMs += t.LengthMs;
            object? thumb = _settings.ListArtwork ? Theme.MakeArt(artSize, Theme.StableHash(t.Album ?? t.DisplayTitle)) : null;   // no cover in compact (text-only)
            int r = _tracks.Rows.Add(thumb, t.DisplayTitle, t.Artist ?? "", t.Album ?? "",
                RatingStars(t.Rating), t.PlayCount > 0 ? t.PlayCount.ToString() : "", DateAddedStr(t.DateAdded), t.DurationStr);
            _tracks.Rows[r].Tag = t;
        }
        _populatingGrid = false; _tracks.ResumeLayout(); SizeTracks();
        if (_settings.ListArtwork) LoadArtworkAsync(list, artSize);   // skip cover loading when text-only
        string name = lp.Name.Length == 0 ? Loc.T("Untitled") : lp.Name;
        _emptyMsg = list.Count == 0 ? Loc.T("This playlist is empty — right-click songs in Local Music to add them.") : "";
        _header.SetInfo(Loc.T("PLAYLIST · ON THIS PC"), name, Summary(list.Count, totalMs, "song"), Theme.StableHash(name));
        // A chosen cover wins over the auto (name-derived) header tile; either way the tile is clickable to pick one.
        int coverId = LocalCoverKey(lp) is { } lk ? _settings.GetCover(lk) : -1;
        _currentHasCustomCover = coverId >= 0;
        _header.SetArt(_currentHasCustomCover
            ? (coverId == CoverArt.CassetteId ? CoverArt.GenerateTitled(coverId, 150, name) : CoverArt.Generate(coverId, 150))
            : null);
        _header.ArtClickable = true;
        _baseStatus = ""; _baseStatusClickable = false; SetStatus("");   // count shows in the subtitle
        SetActionButtons();
    }

    private void CreateLocalPlaylist(List<string>? initialPaths)
    {
        string? name = PromptDialog.Show(this, Loc.T("New playlist"), Loc.T("Playlist name:"), Loc.T("New Playlist"));
        if (string.IsNullOrWhiteSpace(name)) return;
        var lp = new LocalPlaylistData { Name = name.Trim() };
        if (initialPaths is not null) lp.Paths.AddRange(initialPaths.Where(p => !string.IsNullOrEmpty(p)));
        _settings.LocalPlaylists.Add(lp); _settings.Save();
        _viewKind = SidebarRowKind.LocalPlaylist; _currentLocalPlaylist = lp;
        BuildSidebar(); ShowCurrent();
        SetStatus(lp.Paths.Count > 0 ? Loc.T("Created playlist “{0}” with {1} song(s).", lp.Name, lp.Paths.Count) : Loc.T("Created playlist “{0}”.", lp.Name));
    }

    private void AddPathsToLocalPlaylist(LocalPlaylistData lp, List<string> paths)
    {
        int added = 0;
        foreach (var p in paths) if (!lp.Paths.Any(x => string.Equals(x, p, StringComparison.OrdinalIgnoreCase))) { lp.Paths.Add(p); added++; }
        _settings.Save(); BuildSidebar();
        if (_viewKind == SidebarRowKind.LocalPlaylist && ReferenceEquals(_currentLocalPlaylist, lp)) ShowCurrent();
        SetStatus(added == 0 ? Loc.T("Already in “{0}”.", lp.Name) : Loc.T("Added {0} song(s) to “{1}”.", added, lp.Name));
    }

    private void RenameLocalPlaylist(LocalPlaylistData lp)
    {
        string? name = PromptDialog.Show(this, Loc.T("Rename playlist"), Loc.T("New name:"), lp.Name);
        if (string.IsNullOrWhiteSpace(name) || name.Trim() == lp.Name) return;
        lp.Name = name.Trim(); _settings.Save(); BuildSidebar();
        if (_viewKind == SidebarRowKind.LocalPlaylist && ReferenceEquals(_currentLocalPlaylist, lp)) ShowCurrent();
        SetStatus(Loc.T("Renamed playlist to “{0}”.", lp.Name));
    }

    private void DeleteLocalPlaylist(LocalPlaylistData lp)
    {
        string label = lp.Name.Length == 0 ? Loc.T("Untitled") : lp.Name;
        if (MessageDialog.Show(this, Loc.T("Delete the playlist “{0}”?\n\nThis only removes the playlist — your music files on the PC are untouched.", label),
                Loc.T("Delete playlist"), MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        if (LocalCoverKey(lp) is { } ck) _settings.SetCover(ck, -1);   // drop the chosen-cover entry (its random id is never reused)
        _settings.LocalPlaylists.Remove(lp); _settings.Save();
        if (ReferenceEquals(_currentLocalPlaylist, lp)) { _currentLocalPlaylist = null; _viewKind = SidebarRowKind.LocalMusic; }
        BuildSidebar(); ShowCurrent();
        SetStatus(Loc.T("Deleted playlist “{0}”.", label));
    }

    private void RemoveFromLocalPlaylist(LocalPlaylistData lp, List<string> paths)
    {
        int n = lp.Paths.RemoveAll(p => paths.Any(x => string.Equals(x, p, StringComparison.OrdinalIgnoreCase)));
        _settings.Save(); BuildSidebar();
        if (_viewKind == SidebarRowKind.LocalPlaylist && ReferenceEquals(_currentLocalPlaylist, lp)) ShowCurrent();
        SetStatus(Loc.T("Removed {0} song(s) from “{1}”.", n, lp.Name));
    }

    private List<string> SelectedLocalPaths()
    {
        var paths = new List<string>();
        foreach (DataGridViewRow row in _tracks.SelectedRows) if (row.Tag is Track t && t.LocalPath is { } p) paths.Add(p);
        return paths;
    }

    /// <summary>Right-click menu for tracks in Local Music / a local playlist (no iPod write involved).</summary>
    private void ShowLocalTrackMenu(Point screen)
    {
        var paths = SelectedLocalPaths();
        if (paths.Count == 0) return;
        var m = ThemedMenu.New();

        int firstRow = -1;
        foreach (DataGridViewRow r in _tracks.SelectedRows) if (firstRow < 0 || r.Index < firstRow) firstRow = r.Index;
        if (firstRow >= 0) { int fr = firstRow; var play = new ToolStripMenuItem(Loc.T("Play")); play.Click += (_, _) => ActivateTrackRow(fr); m.Items.Add(play); }
        AddQueueMenuItems(m);
        m.Items.Add(new ToolStripSeparator());

        var addTo = new ToolStripMenuItem(Loc.T("Add to playlist"));
        foreach (var lp in _settings.LocalPlaylists)
        {
            if (_viewKind == SidebarRowKind.LocalPlaylist && ReferenceEquals(lp, _currentLocalPlaylist)) continue; // already here
            var r = lp; var it = new ToolStripMenuItem(lp.Name.Length == 0 ? Loc.T("Untitled") : lp.Name);
            it.Click += (_, _) => AddPathsToLocalPlaylist(r, paths);
            addTo.DropDownItems.Add(it);
        }
        if (addTo.DropDownItems.Count > 0) addTo.DropDownItems.Add(new ToolStripSeparator());
        var nu = new ToolStripMenuItem(Loc.T("New playlist…")); nu.Click += (_, _) => CreateLocalPlaylist(paths);
        addTo.DropDownItems.Add(nu);
        m.Items.Add(addTo); // the submenu is themed automatically by RoundContextMenu

        if (_viewKind == SidebarRowKind.LocalPlaylist && _currentLocalPlaylist is { } cur)
        {
            m.Items.Add(new ToolStripSeparator());
            var rem = new ToolStripMenuItem(Loc.T("Remove from “{0}”   ({1})", cur.Name.Length == 0 ? Loc.T("Untitled") : cur.Name, paths.Count));
            rem.Click += (_, _) => RemoveFromLocalPlaylist(cur, paths);
            m.Items.Add(rem);
        }
        m.Show(screen);
    }

    private void AddLocalFolder()
    {
        using var dlg = new FolderBrowserDialog { Description = Loc.T("Choose a folder of music on your PC") };
        if (dlg.ShowDialog(this) != DialogResult.OK || string.IsNullOrEmpty(dlg.SelectedPath)) return;
        if (!_settings.LocalMusicFolders.Any(p => string.Equals(p, dlg.SelectedPath, StringComparison.OrdinalIgnoreCase)))
        {
            _settings.LocalMusicFolders.Add(dlg.SelectedPath);
            _settings.Save();
        }
        _viewKind = SidebarRowKind.LocalMusic;
        _localStale = true;
        BuildSidebar();
        ShowLocalMusic();
    }

    private void ManageLocalFolders()
    {
        if (_settings.LocalMusicFolders.Count == 0) { AddLocalFolder(); return; }
        var m = ThemedMenu.New();
        var add = new ToolStripMenuItem(Loc.T("Add folder…")); add.Click += (_, _) => AddLocalFolder();
        m.Items.Add(add);
        m.Items.Add(new ToolStripSeparator());
        foreach (var folder in _settings.LocalMusicFolders.ToList())
        {
            var item = new ToolStripMenuItem(Loc.T("Remove:  {0}", folder.Length <= 48 ? folder : "…" + folder[^47..]));
            item.Click += (_, _) => { _settings.LocalMusicFolders.RemoveAll(p => p == folder); _settings.Save(); _localStale = true; ShowLocalMusic(); };
            m.Items.Add(item);
        }
        m.Items.Add(new ToolStripSeparator());
        var clear = new ToolStripMenuItem(Loc.T("Clear all folders")); clear.Click += (_, _) => { _settings.LocalMusicFolders.Clear(); _settings.Save(); _localStale = true; ShowLocalMusic(); };
        m.Items.Add(clear);
        var b = _header.DeleteButton;
        m.Show(b.PointToScreen(new Point(0, b.Height + 2)));
    }

    // ---- drag & drop (drop files onto the window → route by type) ----

    // Formats the click-wheel iPod plays as-is (copy), vs ones we transcode to AAC first via ffmpeg.
    private static readonly string[] NativeAudioExt = { ".mp3", ".m4a", ".aac", ".wav", ".aif", ".aiff", ".m4b" };
    private static readonly string[] TranscodeAudioExt = { ".flac", ".ogg", ".oga", ".opus", ".wma", ".ape", ".wv", ".mpc", ".mka" };
    private static readonly string[] AudioExt = NativeAudioExt.Concat(TranscodeAudioExt).ToArray();
    private static readonly string[] VideoExt = { ".mp4", ".m4v", ".mov", ".avi", ".mkv", ".wmv", ".flv", ".webm", ".mpg", ".mpeg" };
    private static readonly string[] ImageExt = { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff", ".webp" };
    private static bool IsExt(string path, string[] exts) => exts.Contains(Path.GetExtension(path).ToLowerInvariant());
    private static bool IsNativeAudio(string path) => IsExt(path, NativeAudioExt);

    /// <summary>Recursively collect files under <paramref name="folder"/> whose extension matches, skipping
    /// unreadable/system entries, sorted by path so subfolders group together. Safe on huge trees.</summary>
    private static string[] GatherFiles(string folder, string[] exts)
    {
        try
        {
            var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System | FileAttributes.Hidden };
            return Directory.EnumerateFiles(folder, "*", opts)
                .Where(f => IsExt(f, exts))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch { return Array.Empty<string>(); }
    }

    // Enable file-drop on every surface the user might aim at, so dropping works over the song list and
    // panels — not just the window's bare edges.
    private void WireDragDrop()
    {
        foreach (var c in new Control?[] { this, _sidebar, _header, _center, _tracks, _browseView, _photoView, _deviceView, _deviceScrollPanel, _search, _nowPlaying })
            EnableFileDrop(c);
    }

    private void EnableFileDrop(Control? c)
    {
        if (c is null) return;
        c.AllowDrop = true;
        c.DragEnter += OnDragOverAny;
        c.DragOver += OnDragOverAny;
        c.DragDrop += OnDragDrop;
        c.DragLeave += (_, _) => ScheduleHideDrop();
    }

    /// <summary>Can the current view receive a file drop? Local Music always; an iPod view only when writable.</summary>
    private bool CanAcceptDrop() => _viewKind == SidebarRowKind.LocalMusic || (_device is not null && _device.Profile.CanWrite);

    private void OnDragOverAny(object? sender, DragEventArgs e)
    {
        bool ok = e.Data?.GetDataPresent(DataFormats.FileDrop) == true && CanAcceptDrop();
        e.Effect = ok ? DragDropEffects.Copy : DragDropEffects.None;
        if (ok) SetDropActive(true, _viewKind == SidebarRowKind.LocalMusic ? Loc.T("Drop to add to Local Music") : Loc.T("Drop to add to your iPod"));
        else SetDropActive(false);
    }

    private void SetDropActive(bool on, string? caption = null)
    {
        _dropHideTimer.Stop();
        if (_dropOverlay is null) return;
        if (!on) { if (_dropOverlay.Visible) _dropOverlay.Visible = false; return; }
        if (caption is not null) _dropOverlay.Caption = caption;
        if (!_dropOverlay.Visible)
        {
            _dropOverlay.Bounds = RectangleToClient(_center!.RectangleToScreen(_center.ClientRectangle));
            _dropOverlay.Visible = true;
            _dropOverlay.BringToFront();
        }
    }

    // Moving the cursor between controls fires DragLeave then DragEnter; debounce so the overlay doesn't flicker.
    private void ScheduleHideDrop() { _dropHideTimer.Stop(); _dropHideTimer.Start(); }

    private void OnDragDrop(object? sender, DragEventArgs e)
    {
        SetDropActive(false);
        if (e.Data?.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;

        // On the Local Music view, a drop adds PC folders to the library (no iPod needed).
        if (_viewKind == SidebarRowKind.LocalMusic) { AddDroppedToLocal(paths); return; }

        if (_device is null || !_device.Profile.CanWrite)
        {
            SetStatus(Loc.T("Connect a writable iPod, or open Local Music, to add files by dropping them."));
            return;
        }

        // Expand any dropped FOLDERS into their media files recursively (incl. subfolders), then route by type.
        var media = AudioExt.Concat(VideoExt).Concat(ImageExt).ToArray();
        var files = paths.Where(File.Exists).ToList();
        foreach (var dir in paths.Where(Directory.Exists)) files.AddRange(GatherFiles(dir, media));
        var audio = files.Where(f => IsExt(f, AudioExt)).ToArray();
        var video = files.Where(f => IsExt(f, VideoExt)).ToArray();
        var images = files.Where(f => IsExt(f, ImageExt)).ToArray();
        if (audio.Length == 0 && video.Length == 0 && images.Length == 0)
        {
            SetStatus(Loc.T("Drop music, video, or photo files — or a folder of them — to add them."));
            return;
        }
        // Each runs its own progress dialog; a mixed drop processes them in turn.
        if (audio.Length > 0) AddMusicFiles(audio);
        if (video.Length > 0) AddVideoFiles(video);
        if (images.Length > 0) AddPhotoFiles(images);
    }

    /// <summary>Local Music view: dropped folders (and the folders containing dropped songs) become library folders.</summary>
    private void AddDroppedToLocal(string[] paths)
    {
        var toAdd = new List<string>();
        bool sawMusic = false;
        void Consider(string? folder)
        {
            if (string.IsNullOrEmpty(folder)) return;
            if (_settings.LocalMusicFolders.Any(p => string.Equals(p, folder, StringComparison.OrdinalIgnoreCase))) return;
            if (toAdd.Any(p => string.Equals(p, folder, StringComparison.OrdinalIgnoreCase))) return;
            toAdd.Add(folder);
        }
        foreach (var p in paths)
        {
            if (Directory.Exists(p)) { sawMusic = true; Consider(p); }
            else if (File.Exists(p) && IsExt(p, AudioExt)) { sawMusic = true; Consider(Path.GetDirectoryName(p)); }
        }
        if (toAdd.Count == 0)
        {
            SetStatus(sawMusic ? Loc.T("That folder is already in your Local Music.") : Loc.T("Drop a music folder — or songs — to add them to Local Music."));
            return;
        }
        _settings.LocalMusicFolders.AddRange(toAdd);
        _settings.Save();
        _viewKind = SidebarRowKind.LocalMusic;
        _localStale = true;
        BuildSidebar();
        ShowLocalMusic();
        SetStatus(toAdd.Count == 1 ? Loc.T("Added a folder to Local Music.") : Loc.T("Added {0} folders to Local Music.", toAdd.Count));
    }

    // ---- video ----

    private void OnAddVideo()
    {
        if (_lib is null || _device is null || !_device.Profile.CanWrite) return;
        using var dlg = new OpenFileDialog
        {
            Multiselect = true,
            Title = Loc.T("Select video to copy onto the iPod"),
            Filter = Loc.T("Video files") + "|*.mp4;*.m4v;*.mov;*.avi;*.mkv;*.wmv;*.flv;*.webm;*.mpg;*.mpeg|" + Loc.T("All files") + "|*.*",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK || dlg.FileNames.Length == 0) return;
        AddVideoFiles(dlg.FileNames);
    }

    /// <summary>Core video-add (shared by the button and drag-and-drop): detect ffmpeg, transcode if needed, save.</summary>
    private void AddVideoFiles(string[] files)
    {
        if (_lib is null || _device is null || !_device.Profile.CanWrite || files.Length == 0) return;

        files = FilterAlreadyOnIpod(files, Loc.T("videos"), isVideo: true); // skip/keep duplicates already on the iPod
        if (files.Length == 0) return;
        if (!SpaceOkToAdd(files)) return;   // free-space pre-flight (source size over-estimates the transcode — safe)

        var ffmpeg = FfmpegService.Detect(_settings.FfmpegPath);
        if (ffmpeg is null)
        {
            var r = MessageDialog.Show(this,
                Loc.T("ffmpeg was not found, so videos can't be converted to an iPod-compatible format.\n\n• Install ffmpeg (e.g. `winget install Gyan.FFmpeg`) and it'll be picked up automatically, or set its path in Settings ▸ Video.\n\nCopy the selected file(s) as-is for now? They will only play if they are already iPod-compatible H.264/MPEG-4."),
                Loc.T("ffmpeg not found"), MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
            if (r != DialogResult.OK) return;
        }
        if (!ConfirmWriteOnce()) return;

        var target = VideoTarget.ForQuality(_settings.VideoQuality);
        int ok = 0;
        var errors = new List<string>();
        string tempDir = Path.Combine(Path.GetTempPath(), "mixtape-video");

        using var prog = new CopyProgressDialog(Loc.T("Adding video to your iPod"), files.Length * 100, (report, cancelled) =>
        {
            Directory.CreateDirectory(tempDir);
            for (int i = 0; i < files.Length; i++)
            {
                if (cancelled()) break;
                string src = files[i], name = Path.GetFileName(src);
                int baseP = i * 100;
                report(baseP, Loc.T("Processing {0} of {1}   ·   {2}", i + 1, files.Length, name));
                string toCopy = src;
                string? temp = null;
                double durSec = 0;
                try
                {
                    if (ffmpeg is not null)
                    {
                        var probe = ffmpeg.Probe(src);
                        durSec = probe?.DurationSec ?? 0;
                        bool ready = !_settings.AlwaysTranscode && probe is not null && FfmpegService.IsIpodReady(probe, target);
                        if (!ready)
                        {
                            temp = Path.Combine(tempDir, Guid.NewGuid().ToString("N") + ".m4v");
                            ffmpeg.Transcode(src, temp, target, durSec,
                                frac => report(baseP + (int)(frac * 98), Loc.T("Converting {0} of {1}   ·   {2}%   ·   {3}", i + 1, files.Length, (int)(frac * 100), name)),
                                cancelled);
                            toCopy = temp;
                        }
                    }
                    else
                    {
                        // No ffmpeg: only an already-packaged MP4-family container has any chance of playing.
                        string ext = Path.GetExtension(src).ToLowerInvariant();
                        if (ext is not (".m4v" or ".mp4" or ".mov"))
                            throw new InvalidOperationException(Loc.T("not an iPod-compatible container — install ffmpeg to convert it."));
                    }
                    report(baseP + 99, Loc.T("Copying {0} of {1}   ·   {2}", i + 1, files.Length, name));
                    _lib!.AddMediaFile(toCopy, MediaType.Movie, Path.GetFileNameWithoutExtension(src), durSec);
                    ok++;
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { errors.Add($"{name}: {ex.Message}"); }
                finally { if (temp is not null) { try { File.Delete(temp); } catch { } } }
            }
            if (ok > 0) { report(files.Length * 100, Loc.T("Saving the iPod database…")); _lib!.Save(); }
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
        });
        prog.ShowDialog(this);
        _viewKind = SidebarRowKind.Videos;
        ReloadAfterEdit();

        if (prog.Error is not null)
        {
            MessageDialog.Show(this, Loc.T("Writing the database failed (a backup was kept as iTunesDB.bak):\n\n{0}", prog.Error.Message), "Mixtape", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        string msg = prog.WasCancelled ? Loc.T("Stopped — added {0} video(s).", ok) : Loc.T("Added {0} video(s).", ok);
        if (errors.Count > 0) msg += Loc.T("\n\n{0} could not be added:\n• ", errors.Count) + string.Join("\n• ", errors);
        MessageDialog.Show(this, msg, "Mixtape", MessageBoxButtons.OK, errors.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
    }

    // ---- photos ----

    private void OnAddPhotos()
    {
        if (_photos is null || _device is null) return;
        if (!_photos.SafeToWrite)
        {
            MessageDialog.Show(this, _photos.BlockReason ?? Loc.T("Photos are read-only on this iPod."), "Mixtape", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        using var dlg = new OpenFileDialog
        {
            Multiselect = true,
            Title = Loc.T("Select photos to copy onto the iPod"),
            Filter = Loc.T("Image files") + "|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.tif;*.tiff;*.webp|" + Loc.T("All files") + "|*.*",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK || dlg.FileNames.Length == 0) return;
        AddPhotoFiles(dlg.FileNames);
    }

    /// <summary>Pick from the generated wallpaper pack and add the chosen designs to the iPod's Photos library
    /// (rendered full-size in memory — no temp files). They show full-screen / as a slideshow on color iPods.</summary>
    private void OnAddWallpapers()
    {
        if (_photos is null || _device is null) return;
        if (!_photos.SafeToWrite)
        {
            MessageDialog.Show(this, _photos.BlockReason ?? Loc.T("Photos are read-only on this iPod."), "Mixtape", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        using var dlg = new WallpaperPickerDialog();
        if (dlg.ShowDialog(this) != DialogResult.OK || dlg.SelectedIndices.Count == 0) return;
        if (!ConfirmWriteOnce()) return;

        var picks = dlg.SelectedIndices.ToArray();
        int ok = 0;
        using var prog = new CopyProgressDialog(Loc.T("Adding wallpapers to your iPod"), picks.Length, (report, cancelled) =>
        {
            for (int i = 0; i < picks.Length; i++)
            {
                if (cancelled()) break;
                report(i, Loc.T("Rendering {0} of {1}   ·   {2}", i + 1, picks.Length, Loc.T(Wallpaper.Names[picks[i]])));
                using var bmp = Wallpaper.Render(picks[i], 640, 480);
                _photos!.AddPhoto(bmp, bmp.Width * (long)bmp.Height * 2);
                ok++;
            }
            report(picks.Length, Loc.T("Writing the photo library… (this can take a moment)"));
            _photos!.Save();
        });
        prog.ShowDialog(this);
        ShowPhotos();

        if (prog.Error is not null)
        {
            MessageDialog.Show(this, Loc.T("Writing the photo library failed (a backup was kept as Photo Database.bak):\n\n{0}", prog.Error.Message), "Mixtape", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        MessageDialog.Show(this, prog.WasCancelled ? Loc.T("Stopped — added {0} wallpaper(s).", ok) : Loc.T("Added {0} wallpaper(s).", ok), "Mixtape", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    /// <summary>Core photo-add (shared by the button and drag-and-drop): render thumbnails + write the photo DB.</summary>
    private void AddPhotoFiles(string[] files)
    {
        if (_photos is null || _device is null || !_photos.SafeToWrite || files.Length == 0 || !ConfirmWriteOnce()) return;
        int ok = 0;
        var errors = new List<string>();

        // Save in batches so a large folder import keeps memory bounded (rendered thumbnails are flushed
        // and freed each Save) and so already-added photos persist even if a later one fails or is cancelled.
        const int BatchSize = 150;
        using var prog = new CopyProgressDialog(Loc.T("Adding photos to your iPod"), files.Length, (report, cancelled) =>
        {
            int staged = 0;
            for (int i = 0; i < files.Length; i++)
            {
                if (cancelled()) break;
                report(i, Loc.T("Rendering {0} of {1}   ·   {2}", i + 1, files.Length, Path.GetFileName(files[i])));
                try { _photos!.AddPhoto(files[i]); ok++; staged++; }
                catch (Exception ex) { errors.Add($"{Path.GetFileName(files[i])}: {ex.Message}"); }
                if (staged >= BatchSize) { report(i + 1, Loc.T("Saving… ({0} added so far)", ok)); _photos!.Save(); staged = 0; }
            }
            if (staged > 0) { report(files.Length, Loc.T("Writing the photo library… (this can take a moment)")); _photos!.Save(); }
        });
        prog.ShowDialog(this);
        ShowPhotos();

        if (prog.Error is not null)
        {
            MessageDialog.Show(this, Loc.T("Writing the photo library failed (a backup was kept as Photo Database.bak):\n\n{0}", prog.Error.Message), "Mixtape", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        string msg = prog.WasCancelled ? Loc.T("Stopped — added {0} photo(s).", ok) : Loc.T("Added {0} photo(s).", ok);
        if (errors.Count > 0) msg += Loc.T("\n\n{0} could not be added:\n• ", errors.Count) + string.Join("\n• ", errors);
        MessageDialog.Show(this, msg, "Mixtape", MessageBoxButtons.OK, errors.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
    }

    private void OnDeletePhotos()
    {
        if (_photos is null || !_photos.SafeToWrite) return;
        var ids = _photoView.SelectedIds;
        if (ids.Count == 0) { SetStatus(Loc.T("Select one or more photos to delete.")); return; }
        if (MessageDialog.Show(this, Loc.T("Delete {0} photo(s) from the iPod?\n\nA backup of the photo database is kept (Photo Database.bak).", ids.Count),
                Loc.T("Delete photos"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        if (!ConfirmWriteOnce()) return;

        Exception? error = null;
        using (var prog = new CopyProgressDialog(Loc.T("Updating the photo library"), 1, (report, cancelled) =>
        {
            report(0, Loc.T("Removing photos and repacking…"));
            _photos!.DeletePhotos(ids);
            _photos!.Save();
            report(1, Loc.T("Done"));
        }))
        {
            prog.ShowDialog(this);
            error = prog.Error;
        }
        if (error is not null)
        {
            ShowPhotos();   // the model was resynced from disk on failure — rebuild the grid to match
            MessageDialog.Show(this, Loc.T("Writing the photo library failed (a backup was kept as Photo Database.bak):\n\n{0}", error.Message), "Mixtape", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        else
        {
            // Drop just the deleted tiles in place — the surviving tiles keep their decoded thumbnails and the
            // grid keeps its scroll position, so deleting one photo no longer snaps the view back to the top
            // (and we skip re-decoding the whole library). Remaining offsets are unchanged by a delete.
            RefreshPhotosAfterDelete(ids);
            SetStatus(Loc.T("Deleted {0} photo(s).", ids.Count));
        }
    }

    /// <summary>Refresh the Photos view after an in-place delete without rebuilding the grid: drop the removed
    /// tiles (keeping scroll + the other thumbnails) and re-sync the header count, art and status.</summary>
    private void RefreshPhotosAfterDelete(IReadOnlyCollection<uint> deletedIds)
    {
        if (_photos is null) { ShowPhotos(); return; }
        _photoView.RemovePhotos(deletedIds);

        // Keep the revisit-skip cache valid so re-opening Photos doesn't needlessly rebuild.
        _photoShownLib = _photos; _photoShownGen = _photos.Generation;

        var photos = _photos.Photos.ToList();
        long pb = PhotoBytes();
        string sub = PhotoSummary(photos.Count) + (pb > 0 ? "  ·  " + CapacityBar.Human(pb) : "");
        _header.SetInfo(Loc.T("LIBRARY"), Loc.T("Photos"), sub, Theme.StableHash("Photos"));
        using (var hb = photos.Count > 0 ? _photos.RenderThumb(photos[0]) : null)
            _header.SetArt(hb);
        SetActionButtons();
        UpdatePhotoStatus();
    }

    private void ShowPhotoMenu(Point screen)
    {
        if (_photos is null) return;
        var m = ThemedMenu.New();
        var sel = _photoView.SelectedIds;
        if (sel.Count > 0) // viewing isn't a write — offered even on read-only iPods
        {
            uint vid = sel[0];
            var view = new ToolStripMenuItem(Loc.T("View"));
            view.Click += (_, _) => OpenPhotoViewer(vid);
            m.Items.Add(view);
            m.Items.Add(new ToolStripSeparator());
        }
        if (!_photos.SafeToWrite)
        {
            m.Items.Add(new ToolStripMenuItem(Loc.T("Read-only — {0}", _photos.BlockReason ?? "")) { Enabled = false });
            m.Show(screen);
            return;
        }
        var ids = _photoView.SelectedIds;
        if (ids.Count > 0)
        {
            var del = new ToolStripMenuItem(Loc.T("Delete {0} photo(s) from iPod", ids.Count));
            del.Click += (_, _) => OnDeletePhotos();
            m.Items.Add(del);
            m.Items.Add(new ToolStripSeparator());
        }
        var add = new ToolStripMenuItem(Loc.T("Add photos…"));
        add.Click += (_, _) => OnAddPhotos();
        m.Items.Add(add);
        var addFolder = new ToolStripMenuItem(Loc.T("Add folder…   (includes subfolders)"));
        addFolder.Click += (_, _) => OnAddPhotoFolder();
        m.Items.Add(addFolder);
        var addWall = new ToolStripMenuItem(Loc.T("Add wallpapers…"));
        addWall.Click += (_, _) => OnAddWallpapers();
        m.Items.Add(addWall);
        m.Show(screen);
    }

    // ---- context menus (right-click) ----

    private void OnSidebarRightClick(SidebarRowKind kind, object? tag, Point screen)
    {
        // Local-music rows work with or without an iPod, so handle them before the device check.
        if (kind == SidebarRowKind.LocalMusic)
        {
            var lm = ThemedMenu.New();
            var n = new ToolStripMenuItem(Loc.T("New playlist…")); n.Click += (_, _) => CreateLocalPlaylist(null);
            lm.Items.Add(n);
            lm.Show(screen);
            return;
        }
        if (kind == SidebarRowKind.LocalPlaylist && tag is LocalPlaylistData lp)
        {
            var lm = ThemedMenu.New();
            var cov = new ToolStripMenuItem(Loc.T("Choose cover…")); cov.Click += (_, _) => ChooseLocalCover(lp); lm.Items.Add(cov);
            lm.Items.Add(new ToolStripSeparator());
            var ren = new ToolStripMenuItem(Loc.T("Rename…")); ren.Click += (_, _) => RenameLocalPlaylist(lp); lm.Items.Add(ren);
            lm.Items.Add(new ToolStripSeparator());
            var ldel = new ToolStripMenuItem(Loc.T("Delete playlist")); ldel.Click += (_, _) => DeleteLocalPlaylist(lp); lm.Items.Add(ldel);
            lm.Items.Add(new ToolStripSeparator());
            var lnu = new ToolStripMenuItem(Loc.T("New playlist…")); lnu.Click += (_, _) => CreateLocalPlaylist(null); lm.Items.Add(lnu);
            lm.Show(screen);
            return;
        }

        if (_db is null) return;

        // The library row offers just the cover chooser (a local preference, always available).
        if (kind == SidebarRowKind.AllSongs)
        {
            string? lk = CoverKeyFor(SidebarRowKind.AllSongs, null);
            if (lk is null) return;
            var lm = ThemedMenu.New();
            AddCoverItem(lm, lk, Loc.T("Cover for All songs"));
            lm.Show(screen);
            return;
        }

        if (kind != SidebarRowKind.Playlist || tag is not Playlist pl) return;
        var m = ThemedMenu.New();

        // Choosing a cover is a local preference, so offer it even on read-only iPods.
        string? ck = CoverKeyFor(SidebarRowKind.Playlist, pl);
        if (ck is not null) { AddCoverItem(m, ck, Loc.T("Cover for “{0}”", pl.Name)); m.Items.Add(new ToolStripSeparator()); }

        if (_lib is null || _device is null || !_device.Profile.CanWrite)
        {
            string why = _device?.Profile.WriteBlockReason is { Length: > 0 } w ? w : Loc.T("This iPod is read-only.");
            m.Items.Add(new ToolStripMenuItem(Loc.T("Read-only — {0}", why)) { Enabled = false });
            m.Show(screen);
            return;
        }
        var rename = new ToolStripMenuItem(Loc.T("Rename…"));
        rename.Click += (_, _) => RenamePlaylistInteractive(pl);
        m.Items.Add(rename);
        m.Items.Add(new ToolStripSeparator());
        var del = new ToolStripMenuItem(Loc.T("Delete playlist (keep songs)"));
        del.Click += (_, _) => DeletePlaylist(pl);
        m.Items.Add(del);
        m.Items.Add(new ToolStripSeparator());
        var nu = new ToolStripMenuItem(Loc.T("New playlist…"));
        nu.Click += (_, _) => CreatePlaylistWithTracks(new List<uint>());
        m.Items.Add(nu);
        m.Show(screen);
    }

    private void OnTrackMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right) return;
        var hit = _tracks.HitTest(e.X, e.Y);
        if (hit.RowIndex < 0) return;
        if (!_tracks.Rows[hit.RowIndex].Selected)
        {
            _tracks.ClearSelection();
            _tracks.Rows[hit.RowIndex].Selected = true;
        }
        var pt = _tracks.PointToScreen(e.Location);
        if (_viewKind is SidebarRowKind.LocalMusic or SidebarRowKind.LocalPlaylist) ShowLocalTrackMenu(pt);
        else ShowTrackMenu(pt);
    }

    private void ShowTrackMenu(Point screen)
    {
        if (_lib is null || _device is null) return;
        var ids = SelectedTrackIds();
        if (ids.Count == 0) return;

        var m = ThemedMenu.New();

        // Play / Preview — not a write, so offered on every device (incl. read-only).
        int firstRow = -1;
        foreach (DataGridViewRow r in _tracks.SelectedRows) if (firstRow < 0 || r.Index < firstRow) firstRow = r.Index;
        if (firstRow >= 0 && _tracks.Rows[firstRow].Tag is Track ft)
        {
            int fr = firstRow;
            var play = new ToolStripMenuItem(MediaType.IsVideo(ft.MediaType) ? Loc.T("Play video") : Loc.T("Play"));
            play.Click += (_, _) => ActivateTrackRow(fr);
            m.Items.Add(play);
        }

        AddQueueMenuItems(m);
        m.Items.Add(new ToolStripSeparator());

        // Copy off the iPod to the PC — a read, so offered on every device (incl. read-only).
        var copyOut = new ToolStripMenuItem(Loc.T("Copy to PC…   ({0})", ids.Count));
        copyOut.Click += (_, _) => OnExportSelected();
        m.Items.Add(copyOut);
        m.Items.Add(new ToolStripSeparator());

        if (!_device.Profile.CanWrite)
        {
            string why = _device.Profile.WriteBlockReason.Length > 0 ? _device.Profile.WriteBlockReason : Loc.T("This iPod is read-only.");
            m.Items.Add(new ToolStripMenuItem(Loc.T("Read-only — {0}", why)) { Enabled = false });
            m.Show(screen);
            return;
        }

        // Edit tags + star rating (one song, or shared fields across the whole selection).
        var edit = new ToolStripMenuItem(ids.Count > 1 ? Loc.T("Edit {0} songs…", ids.Count) : Loc.T("Edit info…"));
        edit.Click += (_, _) => OnEditTrackInfo();
        m.Items.Add(edit);
        m.Items.Add(new ToolStripSeparator());

        // Add to playlist ▸ (existing playlists + New playlist…)
        var addTo = new ToolStripMenuItem(Loc.T("Add to playlist"));
        foreach (var pl in _shownPlaylists.Where(p => _db is not null && !ReferenceEquals(p, _db.Master) && !p.IsPodcast))
        {
            var plRef = pl;
            var it = new ToolStripMenuItem(pl.Name.Length == 0 ? Loc.T("Untitled") : pl.Name);
            it.Click += (_, _) => AddSelectedToPlaylist(plRef, ids);
            addTo.DropDownItems.Add(it);
        }
        if (addTo.DropDownItems.Count > 0) addTo.DropDownItems.Add(new ToolStripSeparator());
        var newWith = new ToolStripMenuItem(Loc.T("New playlist…"));
        newWith.Click += (_, _) => CreatePlaylistWithTracks(ids);
        addTo.DropDownItems.Add(newWith);
        m.Items.Add(addTo); // the submenu is themed automatically by RoundContextMenu
        m.Items.Add(new ToolStripSeparator());

        bool inUserPlaylist = _db is not null && _current is not null && !ReferenceEquals(_current, _db.Master) && !_current.IsPodcast;
        if (inUserPlaylist)
        {
            var rem = new ToolStripMenuItem(Loc.T("Remove from “{0}”   ({1})", _current!.Name, ids.Count));
            rem.Click += (_, _) => RemoveFromCurrentPlaylist(ids);
            m.Items.Add(rem);
            m.Items.Add(new ToolStripSeparator());
        }
        var del = new ToolStripMenuItem(Loc.T("Delete from iPod   ({0})", ids.Count));
        del.Click += (_, _) => OnDelete();
        m.Items.Add(del);
        m.Show(screen);
    }

    private List<uint> SelectedTrackIds()
    {
        var ids = new List<uint>();
        foreach (DataGridViewRow row in _tracks.SelectedRows) if (row.Tag is Track t) ids.Add(t.UniqueId);
        return ids;
    }

    private void DeletePlaylist(Playlist pl)
    {
        if (_lib is null) return;
        string label = pl.Name.Length == 0 ? Loc.T("Untitled") : pl.Name;
        if (MessageDialog.Show(this, Loc.T("Delete the playlist “{0}”?\n\nThe songs stay on the iPod — only the playlist itself is removed.", label),
                Loc.T("Delete playlist"), MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        if (!ConfirmWriteOnce()) return;
        bool ok;
        try { Cursor = Cursors.WaitCursor; ok = _lib.RemovePlaylist(pl); if (ok) _lib.Save(); }
        catch (Exception ex) { MessageDialog.Show(this, Loc.T("Write failed (backup kept as iTunesDB.bak):\n\n{0}", ex.Message), "Mixtape", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
        finally { Cursor = Cursors.Default; }
        if (!ok) { ShowPlaylistNotEditable(); return; }
        ReloadAfterEdit();
        SetStatus(Loc.T("Deleted playlist “{0}” — songs kept.", label));
    }

    private void RenamePlaylistInteractive(Playlist pl)
    {
        if (_lib is null) return;
        string? name = PromptDialog.Show(this, Loc.T("Rename playlist"), Loc.T("New name:"), pl.Name);
        if (string.IsNullOrWhiteSpace(name) || name.Trim() == pl.Name) return;
        if (!ConfirmWriteOnce()) return;
        bool ok;
        try { Cursor = Cursors.WaitCursor; ok = _lib.RenamePlaylist(pl, name.Trim()); if (ok) _lib.Save(); }
        catch (Exception ex) { MessageDialog.Show(this, Loc.T("Write failed (backup kept as iTunesDB.bak):\n\n{0}", ex.Message), "Mixtape", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
        finally { Cursor = Cursors.Default; }
        if (!ok) { ShowPlaylistNotEditable(); return; }
        ReloadAfterEdit();
        SetStatus(Loc.T("Renamed playlist to “{0}”.", name.Trim()));
    }

    private void AddSelectedToPlaylist(Playlist pl, List<uint> ids)
    {
        if (_lib is null) return;
        if (!ConfirmWriteOnce()) return;
        bool ok;
        try { Cursor = Cursors.WaitCursor; ok = _lib.AddToPlaylist(pl.PersistentId, ids); if (ok) _lib.Save(); }
        catch (Exception ex) { MessageDialog.Show(this, Loc.T("Write failed (backup kept as iTunesDB.bak):\n\n{0}", ex.Message), "Mixtape", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
        finally { Cursor = Cursors.Default; }
        // AddToPlaylist returns false for BOTH "no stable id" and "every song was already in the list" — only the
        // former is the not-editable case; the latter is a benign no-op, not an error.
        if (!ok) { if (pl.PersistentId == 0) ShowPlaylistNotEditable(); else SetStatus(Loc.T("All selected songs are already in “{0}”.", pl.Name)); return; }
        ReloadAfterEdit();
        SetStatus(Loc.T("Added {0} song(s) to “{1}”.", ids.Count, pl.Name));
    }

    private void CreatePlaylistWithTracks(List<uint> ids)
    {
        if (_lib is null) return;
        string? name = PromptDialog.Show(this, Loc.T("New playlist"), Loc.T("Playlist name:"), Loc.T("New Playlist"));
        if (string.IsNullOrWhiteSpace(name)) return;
        if (!ConfirmWriteOnce()) return;
        try
        {
            Cursor = Cursors.WaitCursor;
            ulong pid = _lib.CreatePlaylist(name.Trim());
            if (ids.Count > 0) _lib.AddToPlaylist(pid, ids);
            _lib.Save();
        }
        catch (Exception ex) { MessageDialog.Show(this, Loc.T("Write failed (backup kept as iTunesDB.bak):\n\n{0}", ex.Message), "Mixtape", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
        finally { Cursor = Cursors.Default; }
        ReloadAfterEdit();
        SetStatus(ids.Count > 0 ? Loc.T("Created playlist “{0}” with {1} song(s).", name.Trim(), ids.Count) : Loc.T("Created playlist “{0}”.", name.Trim()));
    }

    private void RemoveFromCurrentPlaylist(List<uint> ids)
    {
        if (_lib is null || _current is null) return;
        if (!ConfirmWriteOnce()) return;
        bool ok;
        try { Cursor = Cursors.WaitCursor; ok = _lib.RemoveFromPlaylist(_current, ids); if (ok) _lib.Save(); }
        catch (Exception ex) { MessageDialog.Show(this, Loc.T("Write failed (backup kept as iTunesDB.bak):\n\n{0}", ex.Message), "Mixtape", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
        finally { Cursor = Cursors.Default; }
        if (!ok) { ShowPlaylistNotEditable(); return; }
        ReloadAfterEdit();
        SetStatus(Loc.T("Removed {0} song(s) from the playlist.", ids.Count));
    }

    /// <summary>Shown when a playlist edit no-ops because the list has no stable persistent id (an externally-authored
    /// list, e.g. an On-The-Go playlist) — so the user is never told an edit succeeded when the DB was left unchanged.</summary>
    private void ShowPlaylistNotEditable() => MessageDialog.Show(this,
        Loc.T("This playlist can't be edited — it has no stable identifier in the iPod's database, so nothing was changed.\n\n(Playlists you create in Mixtape don't have this limitation.)"),
        "Mixtape", MessageBoxButtons.OK, MessageBoxIcon.Information);

    private void ReloadAfterEdit()
    {
        _db = _lib?.View;
        RebuildPlaylists();
        if (_viewKind == SidebarRowKind.Playlist)
        {
            ulong keep = _current?.PersistentId ?? 0;
            _current = keep != 0 ? _shownPlaylists.FirstOrDefault(p => p.PersistentId == keep) : null;
            if (_current is null) _viewKind = SidebarRowKind.AllSongs; // the playlist was deleted — fall back to the library
        }
        // If a drilled album/artist is now empty (all its tracks deleted, or edited so they no longer match),
        // drop back to the overview grid instead of stranding the user on an empty filtered page.
        if (_browseFilter is not null && _viewKind is SidebarRowKind.Albums or SidebarRowKind.Artists
            && !(_db?.Tracks.Any(t => MediaType.IsAudio(t.MediaType) && _browseFilter(t)) ?? false))
            _browseFilter = null;
        BuildSidebar();
        ShowCurrent();
    }

    // ---- customization ----

    private void OpenSettings()
    {
        using var f = new SettingsForm(_settings, _device, ApplyAllSettings, ReloadCurrentDevice);
        f.ShowDialog(this);
    }

    private void OpenEqualizer(Rectangle anchor)
    {
        var gains = _settings.EqGains is { Length: > 0 } g ? g : EqualizerSampleProvider.FlatGains();
        var dlg = new EqualizerDialog(_settings.EqEnabled, gains, (enabled, newGains) =>
        {
            _settings.EqEnabled = enabled;
            _settings.EqGains = newGains;
            _settings.Save();
            _nowPlaying.ApplyEq(enabled, newGains);
        });
        // Defer the show so it isn't dismissed by the click that opened it (button mouse-down → reactivation race).
        BeginInvoke(() => dlg.ShowAnchored(anchor));
    }

    private void OpenProFeatures(Rectangle anchor)
    {
        var dlg = new ProFeaturesDialog(_settings.GaplessEnabled, _settings.CrossfadeEnabled, _settings.CrossfadeSeconds, _settings.NormalizeVolume, _settings.MonoOutput, _nowPlaying.SleepMinutes,
            (gapless, secs, crossOn, normalize, mono) =>
            {
                _settings.GaplessEnabled = gapless;
                _settings.CrossfadeEnabled = crossOn;
                _settings.CrossfadeSeconds = secs;
                _settings.NormalizeVolume = normalize;
                _settings.MonoOutput = mono;
                _settings.Save();
                _nowPlaying.ApplyPro(gapless, secs, crossOn, normalize, mono);
            },
            minutes => _nowPlaying.SetSleepMinutes(minutes));
        BeginInvoke(() => dlg.ShowAnchored(anchor));
    }

    /// <summary>Supply the next track (path + small cover) for gapless/crossfade prefetch — mirrors the forward
    /// selection of <see cref="PlayRelative"/> (shuffle / sequential / repeat-all) but does NOT play it.</summary>
    private (Track track, string path, Bitmap? cover)? PeekNextForGapless()
    {
        if (_playingTrack is null || _tracks.Rows.Count == 0) return null;
        int cur = RowIndexOf(_playingTrack);
        int pick = -1;
        // Up Next queue wins (PEEK only — gapless re-asks every tick; the dequeue happens in OnGaplessAdvancedToNext).
        foreach (var q in _queue.Items)
        {
            int qi = RowIndexOf(q);
            if (qi >= 0 && IsPlayableRow(qi, out _, out _)) { pick = qi; break; }
        }
        if (pick < 0 && _nowPlaying.Shuffle)
        {
            var pool = new List<int>();
            for (int i = 0; i < _tracks.Rows.Count; i++) if (i != cur && IsPlayableRow(i, out _, out _)) pool.Add(i);
            if (pool.Count > 0) pick = pool[Random.Shared.Next(pool.Count)];
        }
        if (pick < 0)   // sequential (or shuffle with an empty pool) → next playable, then Repeat-All wrap (matches PlayRelative)
        {
            for (int i = (cur < 0 ? -1 : cur) + 1; i < _tracks.Rows.Count; i++) if (IsPlayableRow(i, out _, out _)) { pick = i; break; }
            if (pick < 0 && _nowPlaying.Repeat == NowPlayingBar.RepeatMode.All)
                for (int i = 0; i < _tracks.Rows.Count; i++) if (IsPlayableRow(i, out _, out _)) { pick = i; break; }
        }
        if (pick < 0 || !IsPlayableRow(pick, out var t, out var p)) return null;
        Bitmap? cover = null;
        if (_tracks.Rows[pick].Cells[0].Value is Image img) try { cover = new Bitmap(img); } catch { cover = null; }
        return (t, p, cover);
    }

    /// <summary>Gapless/crossfade advanced internally to the queued track: update the current-track pointer,
    /// nav history and Cover-Flow highlight (the bar already flipped its own metadata/cover).</summary>
    private void OnGaplessAdvancedToNext(Track t)
    {
        if (_queue.Peek() is Track head && ReferenceEquals(head, t)) _queue.Dequeue();   // gapless consumed this queued track
        if (_playingTrack is Track from)
        {
            _navHistory.Add(from);
            if (_navHistory.Count > 200) _navHistory.RemoveAt(0);
        }
        _playingTrack = t;
        int ri = RowIndexOf(t);
        if (ri >= 0) _tracks.EnsureRowVisible(ri);
        if (_coverFlow is not null && MediaType.IsAudio(t.MediaType)) _coverFlow.PlayingTag = CoverTag(t, _cfMode);
        RefreshUpNext();   // update the Up Next panel (Now-Playing row + dequeued item)
    }

    /// <summary>Pick a local audio file on the PC and play it in the now-playing bar (independent of the iPod).</summary>
    private void OnPlayLocalFile()
    {
        using var dlg = new OpenFileDialog
        {
            Title = Loc.T("Play an audio file from your PC"),
            Filter = Loc.T("Audio files") + "|*.mp3;*.m4a;*.aac;*.wav;*.aif;*.aiff;*.m4b;*.flac;*.wma|" + Loc.T("All files") + "|*.*",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK || string.IsNullOrEmpty(dlg.FileName)) return;
        try
        {
            var nt = MetadataExtractor.Read(dlg.FileName, isVideo: false);
            var t = new Track
            {
                Title = !string.IsNullOrWhiteSpace(nt.Title) ? nt.Title : Path.GetFileNameWithoutExtension(dlg.FileName),
                Artist = nt.Artist,
                Album = nt.Album,
                LengthMs = nt.LengthMs,
                MediaType = MediaType.Audio,
            };
            _playingTrack = null;            // a PC file isn't in the iPod list, so prev/next have nothing to step through
            SetNowPlayingVisible(true);
            _nowPlaying.Play(t, dlg.FileName, null);
        }
        catch (Exception ex)
        {
            MessageDialog.Show(this, Loc.T("Couldn't play that file:\n\n{0}", ex.Message), Loc.T("Play file"), MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    /// <summary>Re-apply every visual setting live (called from the Settings window as things change).</summary>
    private void ApplyAllSettings()
    {
        Theme.SetThemeVariant(_settings.ThemeVariant);
        Theme.SetAccent(_settings.Accent);
        RestyleEverything();
        if (_tracks.Columns.Count > 0) _tracks.Columns[0].Visible = _settings.ListArtwork;
        ApplyColumns();
        if (!_userSorted) SeedDefaultSort(); // don't snap an interactive sort back to the default
        // If the active view is no longer offered (capability off, or hidden in settings), fall back.
        if (_viewKind == SidebarRowKind.Videos && !(_device?.Profile.SupportsVideo == true && _settings.ShowVideos)) _viewKind = SidebarRowKind.AllSongs;
        if (_viewKind == SidebarRowKind.Photos && !(_device?.Profile.SupportsPhotos == true && _settings.ShowPhotos)) _viewKind = SidebarRowKind.AllSongs;
        BuildSidebar();
        ShowCurrent();
        Invalidate(true);
        _sidebar.Invalidate();
        _header.Invalidate();
        _header.AddButton.Invalidate();
        ScheduleBarBackdrop();   // re-snapshot the frosted bar so it doesn't keep the old colours until the next scroll
    }

    /// <summary>Show/hide columns per the user's settings AND the available width — as the grid narrows the
    /// low-priority columns drop (Rating → Plays → Added → Album → Artist) so Song/Artist/Album keep readable
    /// width instead of collapsing to "S… / A / A.". Song + Time always stay; art per the artwork setting.</summary>
    private void ApplyColumns()
    {
        if (_tracks.Columns.Count < 8) return;
        int w = _tracks.ClientSize.Width;   // default window → ~720; minimum window → ~500
        _tracks.Columns[0].Visible = _settings.ListArtwork;   // compact = text-only (no cover), like iTunes
        _tracks.Columns[2].Visible = _settings.ShowArtist    && w >= 380;
        _tracks.Columns[3].Visible = _settings.ShowAlbum     && w >= 460;
        _tracks.Columns[6].Visible = _settings.ShowDateAdded && w >= 550;
        _tracks.Columns[5].Visible = _settings.ShowPlays     && w >= 610;
        _tracks.Columns[4].Visible = _settings.ShowRating    && w >= 680;
        _tracks.Columns[7].Visible = _settings.ShowTime;
        _trackHeader?.Invalidate();   // header mirrors the grid's columns → repaint when visibility changes
    }

    /// <summary>Push current theme colours into controls whose BackColor (or inner controls) were baked at
    /// field-initialization, which runs before the saved variant is applied. Their child pill-buttons / search
    /// field backfill rounded corners with the parent BackColor, so a stale value shows as gray corners.</summary>
    private void RecolorBakedControls()
    {
        _header.BackColor = Theme.Bg;
        _sidebar.BackColor = Theme.SidebarBg;
        _search.Restyle();
        if (_search.Parent is Control searchHost) searchHost.BackColor = Theme.Bg;
        _scrollbar.BackColor = Theme.Bg;       // music-list scrollbar track (baked at field-init)
        _deviceScroll.BackColor = Theme.Bg;
    }

    /// <summary>Re-colour the BackColor-baked panels + grid after a background-theme change (owner-painted controls repaint via Invalidate).</summary>
    private void RestyleEverything()
    {
        BackColor = Theme.Bg;
        ForeColor = Theme.TextCol;
        ApplyWindowChrome();                          // re-melt the title bar into the new variant's wallpaper
        _root?.InvalidateWallpaper();                 // re-bake the wallpaper + shadow (drives the corner-carving) in the new colours
        if (_root is not null) _root.Invalidate();   // repaint the gradient wallpaper for the new variant
        if (_content is not null) _content.BackColor = Theme.Bg;
        RecolorBakedControls();
        if (_photoView.Parent is Control center) center.BackColor = Theme.Bg;
        if (_tracks.Parent is Control gh) gh.BackColor = Theme.Bg;
        _deviceView.BackColor = Theme.Bg;
        _deviceScrollPanel.BackColor = Theme.Bg;
        _deviceScroll.BackColor = Theme.Bg;
        Theme.StyleGrid(_tracks);
        _tracks.RowTemplate.Height = _settings.RowHeight;
        var sel = Theme.Blend(Theme.Bg, Theme.Accent, 0.12);
        _tracks.DefaultCellStyle.SelectionBackColor = sel;
        _tracks.AlternatingRowsDefaultCellStyle.SelectionBackColor = sel;
    }

    /// <summary>Click the "⚠ N warning(s)" status to read the actual reader warnings.</summary>
    private void ShowDbWarnings()
    {
        var w = _db?.Warnings;
        if (w is not { Count: > 0 }) return;
        MessageDialog.Show(this,
            Loc.T("While reading your iPod's database, Mixtape noticed the following. These are non-fatal — your music still reads and plays normally:\n\n• ") +
            string.Join("\n\n• ", w) +
            Loc.T("\n\n(These usually come from how a previous app or sync wrote the database, and are safe to ignore.)"),
            Loc.T("Database notes"), MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void SeedDefaultSort()
    {
        _sortCol = _settings.DefaultSort switch { "Song" => 1, "Artist" => 2, "Album" => 3, "Rating" => 4, "Plays" => 5, "Added" => 6, "Time" => 7, _ => -1 };
        _sortAsc = !_settings.DefaultSortDescending;
        _userSorted = false;
    }

    private void ReloadCurrentDevice()
    {
        if (_device is not null) LoadDevice(_device);
    }

    private bool ConfirmWriteOnce()
    {
        if (!_settings.ConfirmWrites || _writeConfirmed) return true;
        string drive = _device?.MountRoot.TrimEnd('\\') ?? Loc.T("the iPod");
        var r = MessageDialog.Show(this,
            Loc.T("Mixtape backs up the database (iTunesDB.bak) before every write and verifies the result afterwards.\n\nTip: if Windows has flagged this iPod's drive for repair, run  chkdsk {0} /f  first.\n\nContinue?", drive),
            Loc.T("Writing to the iPod"), MessageBoxButtons.OKCancel, MessageBoxIcon.Information);
        if (r == DialogResult.OK) { _writeConfirmed = true; return true; }
        return false;
    }

    // The status line now lives in the header (under the action buttons). Transient messages aren't
    // clickable; the persistent warning line is (clickable = true → opens the warnings list).
    private void SetStatus(string text, bool clickable = false) => _header.SetStatus(text, clickable);

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO mi);

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int left, top, right, bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MINMAXINFO { public POINT ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize; }
    [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public int dwFlags; }

    // Window styles kept so the borderless window still gets Aero Snap, min/max animations and a shadow.
    private const int WS_MINIMIZEBOX = 0x20000, WS_MAXIMIZEBOX = 0x10000, WS_THICKFRAME = 0x40000, WS_SYSMENU = 0x80000, WS_CAPTION = 0x00C00000;
    private const int WM_NCCALCSIZE = 0x0083, WM_NCHITTEST = 0x0084, WM_GETMINMAXINFO = 0x0024;
    private const int WM_APPCOMMAND = 0x0319; // keyboard/headset media buttons (delivered to the focused window)
    private const int APPCOMMAND_MEDIA_NEXTTRACK = 11, APPCOMMAND_MEDIA_PREVIOUSTRACK = 12, APPCOMMAND_MEDIA_STOP = 13,
        APPCOMMAND_MEDIA_PLAY_PAUSE = 14, APPCOMMAND_MEDIA_PLAY = 46, APPCOMMAND_MEDIA_PAUSE = 47;
    private const int WM_DEVICECHANGE = 0x0219, DBT_DEVICEARRIVAL = 0x8000, DBT_DEVICEREMOVECOMPLETE = 0x8004, DBT_DEVNODES_CHANGED = 0x0007;
    private const int HTCLIENT = 1, HTCAPTION = 2, HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13, HTTOPRIGHT = 14, HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;
    private const int MONITOR_DEFAULTTONEAREST = 2;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.Style |= WS_MINIMIZEBOX | WS_MAXIMIZEBOX | WS_THICKFRAME | WS_SYSMENU | WS_CAPTION;
            return cp;
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == (int)Program.ShowInstanceMessage)   // a second launch asked us to surface
        {
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Show();
            Activate();
            BringToFront();
            return;
        }
        if (m.Msg == WM_APPCOMMAND)
        {
            int cmd = ((int)((long)m.LParam >> 16)) & 0x0FFF; // GET_APPCOMMAND_LPARAM
            switch (cmd)
            {
                case APPCOMMAND_MEDIA_PLAY_PAUSE:
                case APPCOMMAND_MEDIA_PLAY:
                case APPCOMMAND_MEDIA_PAUSE: _nowPlaying.MediaPlayPause(); m.Result = (IntPtr)1; return;
                case APPCOMMAND_MEDIA_NEXTTRACK: PlayRelative(+1); m.Result = (IntPtr)1; return;
                case APPCOMMAND_MEDIA_PREVIOUSTRACK: PlayRelative(-1); m.Result = (IntPtr)1; return;
                case APPCOMMAND_MEDIA_STOP: _nowPlaying.Pause(); m.Result = (IntPtr)1; return;
            }
        }
        if (m.Msg == WM_DEVICECHANGE)
        {
            int ev = (int)m.WParam;
            if (ev == DBT_DEVICEARRIVAL) _ejectedRoot = null; // a fresh plug-in → resume detecting that drive
            if (ev == DBT_DEVICEARRIVAL || ev == DBT_DEVICEREMOVECOMPLETE || ev == DBT_DEVNODES_CHANGED)
            {
                _deviceChangeTimer.Stop();  // debounce the burst of messages a single plug/unplug fires
                _deviceChangeTimer.Start();
            }
        }
        switch (m.Msg)
        {
            case WM_NCCALCSIZE when m.WParam != IntPtr.Zero:
                m.Result = IntPtr.Zero;     // reclaim the whole window as client (no OS title bar/frame)
                return;
            case WM_GETMINMAXINFO:
                AdjustMaximize(m.LParam);   // keep maximize within the monitor work area (taskbar visible)
                return;
            case WM_NCHITTEST:
                m.Result = (IntPtr)NcHitTest();
                return;
        }
        base.WndProc(ref m);
    }

    /// <summary>Hit-test for our custom frame: resize edges, the caption drag strip, else client.</summary>
    private int NcHitTest()
    {
        var p = PointToClient(Cursor.Position);
        int w = ClientSize.Width, h = ClientSize.Height, b = ResizeBorder;
        if (WindowState == FormWindowState.Maximized)
            return p.Y < CaptionH ? HTCAPTION : HTCLIENT; // a maximized window can't edge-resize
        bool l = p.X < b, r = p.X >= w - b, t = p.Y < b, bot = p.Y >= h - b;
        if (t && l) return HTTOPLEFT;
        if (t && r) return HTTOPRIGHT;
        if (bot && l) return HTBOTTOMLEFT;
        if (bot && r) return HTBOTTOMRIGHT;
        if (l) return HTLEFT;
        if (r) return HTRIGHT;
        if (t) return HTTOP;
        if (bot) return HTBOTTOM;
        return p.Y < CaptionH ? HTCAPTION : HTCLIENT;
    }

    private void AdjustMaximize(IntPtr lParam)
    {
        var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
        var mon = MonitorFromWindow(Handle, MONITOR_DEFAULTTONEAREST);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (GetMonitorInfo(mon, ref mi))
        {
            mmi.ptMaxPosition.x = mi.rcWork.left - mi.rcMonitor.left;
            mmi.ptMaxPosition.y = mi.rcWork.top - mi.rcMonitor.top;
            mmi.ptMaxSize.x = mi.rcWork.right - mi.rcWork.left;
            mmi.ptMaxSize.y = mi.rcWork.bottom - mi.rcWork.top;
            mmi.ptMinTrackSize.x = MinimumSize.Width;
            mmi.ptMinTrackSize.y = MinimumSize.Height;
            Marshal.StructureToPtr(mmi, lParam, false);
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyWindowChrome();
    }

    /// <summary>Dark mode + rounded (Win11) corners for the borderless window. Re-applied on theme change.</summary>
    private void ApplyWindowChrome()
    {
        if (!IsHandleCreated) return;
        try { int on = 1; DwmSetWindowAttribute(Handle, 20, ref on, sizeof(int)); } catch { }     // DWMWA_USE_IMMERSIVE_DARK_MODE
        try { int round = 2; DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int)); } catch { } // DWMWA_WINDOW_CORNER_PREFERENCE = ROUND
        // Remove the 1px DWM window border entirely (DWMWA_COLOR_NONE). Tinting it to the wallpaper top
        // wasn't enough — that colour is lighter than the darker lower gradient, so a faint line remained.
        try { int none = unchecked((int)0xFFFFFFFE); DwmSetWindowAttribute(Handle, 34, ref none, sizeof(int)); } catch { } // DWMWA_BORDER_COLOR = DWMWA_COLOR_NONE
    }
}
