using System.Runtime.InteropServices;

namespace iPodCommander;

/// <summary>
/// Mixtape's main window, styled after Apple Music: a left <see cref="Sidebar"/> (device +
/// playlists), a <see cref="HeaderPanel"/> (artwork + title + actions), and a refined track
/// table. Writes are gated on <see cref="DeviceProfile.CanWrite"/> and go through
/// <see cref="SafeDbWriter"/> (backup + verify + rollback).
/// </summary>
internal sealed class MainForm : Form
{
    private readonly Sidebar _sidebar = new() { Dock = DockStyle.Fill };
    private readonly HeaderPanel _header = new() { Dock = DockStyle.Fill };
    private readonly DataGridView _tracks = new() { Dock = DockStyle.Fill };
    private readonly ThinScrollBar _scrollbar = new() { Dock = DockStyle.Right };
    private readonly Label _status = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(22, 0, 0, 0) };
    private int _hotRow = -1;
    private int _dragRow = -1, _dropRow = -1, _dragStartY; // drag-to-reorder a playlist's tracks
    private bool _rowDragging;

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
    private int _sortCol = -1;    // -1 = playlist order; 1=Song 2=Artist 3=Album 4=Rating 5=Plays 6=Time
    private bool _sortAsc = true;
    private static readonly string[] ColBase = { "", "SONG", "ARTIST", "ALBUM", "RATING", "PLAYS", "TIME" };
    private SidebarRowKind _viewKind = SidebarRowKind.AllSongs; // which top-level view is active
    private PhotoLibrary? _photos;
    private readonly PhotoGridView _photoView = new() { Dock = DockStyle.Fill, Visible = false };
    private readonly Panel _deviceView = new() { Dock = DockStyle.Fill, Visible = false, AutoScroll = true, BackColor = Color.FromArgb(29, 30, 34) };
    private WallpaperPanel? _root;             // gradient shell + caption strip (custom title bar)
    private TableLayoutPanel? _content;        // kept so a theme-variant change can recolour it
    private Panel? _center;                     // the swappable centre region (cross-dissolved on view switches)
    private bool _viewTransitionBusy;          // guards against overlapping centre cross-dissolves
    private readonly SearchBox _search = new() { Dock = DockStyle.Fill };
    private string _searchQuery = "";
    private bool _navigating; // suppresses the search box's redundant ShowCurrent while we clear it on navigation
    private bool _userSorted; // true once the user clicks a column header (don't snap back to the default sort)
    private bool _currentHasCustomCover; // the active view has a chosen cover art → don't override it with a track thumbnail
    private readonly NowPlayingBar _nowPlaying = new() { Dock = DockStyle.Fill };
    private Track? _playingTrack; // the track in the now-playing bar (for prev/next within the visible list)
    private readonly BrowseGridView _browseView = new() { Dock = DockStyle.Fill, Visible = false };
    private Func<Track, bool>? _browseFilter; // when set, the song grid is drilled into one album/artist
    private string _browseTitle = "", _browseKicker = "ALBUM";
    private int _browseArtGen; // cancels stale background cover loads for the album/artist grid
    private const int SidebarIconPx = 36; // render playlist icons at 2× then downscale crisply into the 18px tile

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
        _sidebar.RowRightClicked += OnSidebarRightClick;
        _sidebar.PlaylistAreaRightClicked += OnPlaylistAreaRightClick;
        _header.AddButton.Click += (_, _) => OnAddClicked();
        _header.DeleteButton.Click += (_, _) => OnDeleteClicked();
        _header.AddButton.BlockedClicked += _ => ShowActionBlockedHelp();
        _header.DeleteButton.BlockedClicked += _ => ShowActionBlockedHelp();
        _header.ArtClicked += OnHeaderArtClicked;

        AllowDrop = true; // drop audio/video/photo files anywhere on the window to add them
        DragEnter += (_, e) => e.Effect = (_device is not null && e.Data?.GetDataPresent(DataFormats.FileDrop) == true) ? DragDropEffects.Copy : DragDropEffects.None;
        DragDrop += OnDragDrop;

        if (_autoDetect) Shown += (_, _) => RefreshDevices();
    }

    private const int Gap = 14;        // wallpaper gap around + between the floating cards
    private const int CardRadius = 16; // rounded card corners
    private const int CaptionH = 36;   // the custom title-bar strip height
    private const int ResizeBorder = 6;
    private readonly WindowButton _btnMin = new() { Which = WindowButton.Kind.Minimize };
    private readonly WindowButton _btnMax = new() { Which = WindowButton.Kind.Maximize };
    private readonly WindowButton _btnClose = new() { Which = WindowButton.Kind.Close };

    private void BuildLayout()
    {
        // The shell floats two rounded cards (sidebar + content) over a themed gradient wallpaper, with
        // our own caption strip on top (the native title bar is removed in WndProc). Manual layout in
        // LayoutShell() positions the cards + window buttons so the wallpaper shows through the gaps.
        var root = _root = new WallpaperPanel { Dock = DockStyle.Fill, CaptionHeight = CaptionH, ResizeBorder = ResizeBorder };

        var content = _content = new TableLayoutPanel { Dock = DockStyle.None, ColumnCount = 1, RowCount = 4, BackColor = Theme.Bg, Margin = new Padding(0) };
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 178));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));   // now-playing bar — collapsed until a song plays
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));

        SetupTrackGrid();
        ApplyColumns();
        var gridHost = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(22, 4, 8, 8) };
        var searchHost = new Panel { Dock = DockStyle.Top, Height = 46, BackColor = Theme.Bg, Padding = new Padding(0, 8, 6, 8) };
        searchHost.Controls.Add(_search);   // Fill within the search strip
        _search.Changed += q => { _searchQuery = q.Trim(); if (!_navigating && _viewKind != SidebarRowKind.Photos && _viewKind != SidebarRowKind.Device) ShowCurrent(); };
        gridHost.Controls.Add(_tracks);     // Fill (added first → keeps remaining space)
        gridHost.Controls.Add(_scrollbar);  // Right
        gridHost.Controls.Add(searchHost);  // Top (added last → docks the top strip first)

        // The track grid, photo grid and device page share the centre cell; only one shows at a time.
        var center = _center = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
        center.Controls.Add(_deviceView);   // Fill, hidden until the Device view is active
        center.Controls.Add(_photoView);    // Fill, hidden until the Photos view is active
        center.Controls.Add(_browseView);   // Fill, hidden until the Albums/Artists view is active
        center.Controls.Add(gridHost);      // Fill
        _photoView.SelectionChanged += () => UpdatePhotoStatus();
        _photoView.ItemRightClicked += ShowPhotoMenu;
        _photoView.ItemActivated += OpenPhotoViewer;
        _browseView.ItemActivated += OnBrowseActivated;

        var statusPanel = new Panel { Dock = DockStyle.Fill, BackColor = Theme.SidebarBg };
        _status.ForeColor = Theme.Subtle;
        _status.BackColor = Theme.SidebarBg;
        statusPanel.Controls.Add(_status);

        _nowPlaying.PrevRequested += () => PlayRelative(-1);
        _nowPlaying.NextRequested += () => PlayRelative(+1);
        _nowPlaying.CloseRequested += () => { _playingTrack = null; SetNowPlayingVisible(false); };

        content.Controls.Add(_header, 0, 0);
        content.Controls.Add(center, 0, 1);
        content.Controls.Add(_nowPlaying, 0, 2);
        content.Controls.Add(statusPanel, 0, 3);

        _sidebar.Dock = DockStyle.None;
        root.Controls.Add(_sidebar);
        root.Controls.Add(content);
        root.Controls.Add(_btnMin);
        root.Controls.Add(_btnMax);
        root.Controls.Add(_btnClose);

        _btnMin.Click += (_, _) => WindowState = FormWindowState.Minimized;
        _btnMax.Click += (_, _) => WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;
        _btnClose.Click += (_, _) => Close();

        root.Resize += (_, _) => LayoutShell();
        Controls.Add(root);
        LayoutShell();
    }

    /// <summary>Positions the sidebar + content cards (with wallpaper gaps + rounded corners) and the
    /// window control buttons in the caption strip. Runs on every shell resize / maximize.</summary>
    private void LayoutShell()
    {
        if (_root is null || _content is null) return;
        int w = _root.ClientSize.Width, h = _root.ClientSize.Height;
        if (w <= 0 || h <= 0) return;

        const int sideW = 236;
        int top = CaptionH, bottom = h - Gap;
        _sidebar.Bounds = new Rectangle(Gap, top, sideW, Math.Max(1, bottom - top));
        int cx = Gap + sideW + Gap;
        _content.Bounds = new Rectangle(cx, top, Math.Max(1, w - cx - Gap), Math.Max(1, bottom - top));
        Theme.RoundRegion(_sidebar, CardRadius);
        Theme.RoundRegion(_content, CardRadius);

        const int bw = 46, bh = CaptionH;
        _btnClose.Bounds = new Rectangle(w - bw, 0, bw, bh);
        _btnMax.Bounds = new Rectangle(w - bw * 2, 0, bw, bh);
        _btnMin.Bounds = new Rectangle(w - bw * 3, 0, bw, bh);
        _btnMin.BringToFront(); _btnMax.BringToFront(); _btnClose.BringToFront();
        _btnMax.Maximized = WindowState == FormWindowState.Maximized;
    }

    private void SetupTrackGrid()
    {
        _tracks.AutoGenerateColumns = false;
        _tracks.ReadOnly = true;
        _tracks.AllowUserToAddRows = false;
        _tracks.AllowUserToDeleteRows = false;
        _tracks.AllowUserToResizeRows = false;
        _tracks.AllowUserToResizeColumns = false;
        _tracks.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _tracks.MultiSelect = true;
        _tracks.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        Theme.StyleGrid(_tracks);
        _tracks.RowTemplate.Height = _settings.RowHeight;

        var art = new DataGridViewImageColumn { HeaderText = "", Width = 52, AutoSizeMode = DataGridViewAutoSizeColumnMode.None, ImageLayout = DataGridViewImageCellLayout.Zoom, SortMode = DataGridViewColumnSortMode.NotSortable, Visible = _settings.ShowArtwork };
        _tracks.Columns.Add(art);

        var dimSel = Theme.Blend(Theme.Subtle, Color.White, 0.35); // secondary columns stay dimmer even when the row is selected

        var song = new DataGridViewTextBoxColumn { HeaderText = "SONG", FillWeight = 32, SortMode = DataGridViewColumnSortMode.NotSortable };
        song.DefaultCellStyle.Padding = new Padding(8, 0, 4, 0);          // match the 8px header inset
        song.DefaultCellStyle.Font = Theme.UiFont(10f, FontStyle.Bold);  // song name leads
        _tracks.Columns.Add(song);

        var artist = new DataGridViewTextBoxColumn { HeaderText = "ARTIST", FillWeight = 24, SortMode = DataGridViewColumnSortMode.NotSortable };
        artist.DefaultCellStyle.ForeColor = Theme.Subtle; artist.DefaultCellStyle.SelectionForeColor = dimSel;
        _tracks.Columns.Add(artist);

        var album = new DataGridViewTextBoxColumn { HeaderText = "ALBUM", FillWeight = 30, SortMode = DataGridViewColumnSortMode.NotSortable };
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

        var time = new DataGridViewTextBoxColumn { HeaderText = "TIME", Width = 72, AutoSizeMode = DataGridViewAutoSizeColumnMode.None, SortMode = DataGridViewColumnSortMode.NotSortable };
        time.DefaultCellStyle.ForeColor = Theme.Subtle;
        time.DefaultCellStyle.SelectionForeColor = dimSel;
        time.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
        time.DefaultCellStyle.Padding = new Padding(4, 0, 14, 0);
        time.HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleRight;
        time.HeaderCell.Style.Padding = new Padding(4, 0, 14, 0);          // header right-edge matches the values
        _tracks.Columns.Add(time);

        _scrollbar.Attach(_tracks);
        _tracks.RowPostPaint += OnRowPostPaint;
        _tracks.CellMouseEnter += (_, e) => SetHotRow(e.RowIndex);
        _tracks.MouseLeave += (_, _) => SetHotRow(-1);
        _tracks.MouseDown += OnTrackMouseDown;
        _tracks.MouseDown += OnReorderMouseDown;
        _tracks.MouseMove += OnReorderMouseMove;
        _tracks.MouseUp += OnReorderMouseUp;
        _tracks.CellMouseDoubleClick += (_, e) => { if (e.RowIndex >= 0 && e.RowIndex < _tracks.Rows.Count) ActivateTrackRow(e.RowIndex); };
        _tracks.ColumnHeaderMouseClick += (_, e) =>
        {
            if (e.ColumnIndex < 1 || e.ColumnIndex >= ColBase.Length) return; // artwork column isn't sortable
            if (_sortCol == e.ColumnIndex) { if (_sortAsc) _sortAsc = false; else _sortCol = -1; } // asc → desc → off
            else { _sortCol = e.ColumnIndex; _sortAsc = true; }
            _userSorted = true; // an explicit sort choice — don't let a settings change snap it back
            ShowCurrent();
        };
        _tracks.Paint += (_, e) =>
        {
            // hairline under the fixed column-header row
            using var pen = new Pen(Theme.Border);
            e.Graphics.DrawLine(pen, 0, _tracks.ColumnHeadersHeight, _tracks.Width, _tracks.ColumnHeadersHeight);

            // drag-to-reorder: a teal insertion line at the drop position
            if (_rowDragging && _dropRow >= 0)
            {
                int y;
                if (_dropRow < _tracks.Rows.Count) y = _tracks.GetRowDisplayRectangle(_dropRow, false).Top;
                else { var last = _tracks.GetRowDisplayRectangle(_tracks.Rows.Count - 1, false); y = last.Bottom; }
                using var ip = new Pen(Theme.Accent, 2.5f);
                e.Graphics.DrawLine(ip, 2, y, _tracks.Width - 4, y);
                e.Graphics.FillEllipse(new SolidBrush(Theme.Accent), 0, y - 3, 6, 6);
            }
        };
    }

    private void OnRowPostPaint(object? sender, DataGridViewRowPostPaintEventArgs e)
    {
        var b = e.RowBounds;
        // Start the divider at the Song column, leaving the artwork cell clean — but reach the row's
        // left edge when the artwork column is hidden, so dividers don't start indented in a ragged gap.
        var artCol = _tracks.Columns[0];
        int x0 = b.X + (artCol.Visible ? artCol.Width : 0);
        using (var pen = new Pen(Theme.HairLine)) e.Graphics.DrawLine(pen, x0, b.Bottom - 1, b.Right, b.Bottom - 1);
        if (e.RowIndex >= 0 && e.RowIndex < _tracks.Rows.Count && _tracks.Rows[e.RowIndex].Selected)
            using (var bar = new SolidBrush(Theme.Accent)) e.Graphics.FillRectangle(bar, b.X, b.Y + 1, 3, b.Height - 2);
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
        if (e.Y < _tracks.ColumnHeadersHeight + 18) ScrollGrid(-1);       // auto-scroll near the edges
        else if (e.Y > _tracks.Height - 18) ScrollGrid(+1);
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

    private void ScrollGrid(int dir)
    {
        try
        {
            int first = _tracks.FirstDisplayedScrollingRowIndex;
            int next = Math.Clamp(first + dir, 0, Math.Max(0, _tracks.Rows.Count - 1));
            if (next != first) _tracks.FirstDisplayedScrollingRowIndex = next;
        }
        catch { /* row not scrollable */ }
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
        try { Cursor = Cursors.WaitCursor; _lib.ReorderPlaylist(_current, order); _lib.Save(); }
        catch (Exception ex) { MessageBox.Show(this, "Write failed (a backup was kept as iTunesDB.bak):\n\n" + ex.Message, "Mixtape", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
        finally { Cursor = Cursors.Default; }
        ReloadAfterEdit();
        SetStatus("Playlist order updated.");
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
        if (_tracks.Rows[rowIndex].Tag is not Track t || _device is null) return;
        string? path = t.ResolveFilePath(_device.MountRoot);
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            MessageBox.Show(this, "The file for this item isn't on the iPod, so it can't be previewed.", "Preview", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (MediaType.IsVideo(t.MediaType)) OpenVideoPreview(t, path);
        else PlayAudio(t, path, rowIndex);
    }

    private void PlayAudio(Track t, string path, int rowIndex)
    {
        Bitmap? cover = null;
        if (rowIndex >= 0 && rowIndex < _tracks.Rows.Count && _tracks.Rows[rowIndex].Cells[0].Value is Image img)
            try { cover = new Bitmap(img); } catch { cover = null; }
        _playingTrack = t;
        SetNowPlayingVisible(true); // expand the row first so the hosted media engine is realized before playing
        _nowPlaying.Play(t, path, cover);
        cover?.Dispose(); // the bar took its own copy
    }

    private void OpenVideoPreview(Track t, string path)
    {
        bool wasPlaying = _nowPlaying.Pause(); // don't play audio and video at once
        using (var dlg = new VideoPreviewDialog(path, t.DisplayTitle)) dlg.ShowDialog(this);
        if (wasPlaying) _nowPlaying.Resume(); // pick the song back up where it left off
    }

    /// <summary>Play the audio row N steps from the current one in the visible list, skipping videos.</summary>
    private void PlayRelative(int dir)
    {
        if (_playingTrack is null || _tracks.Rows.Count == 0) { _nowPlaying.StopAndHide(); _playingTrack = null; return; }
        int cur = -1;
        for (int i = 0; i < _tracks.Rows.Count; i++)
            if (ReferenceEquals(_tracks.Rows[i].Tag, _playingTrack)) { cur = i; break; }
        if (cur < 0) cur = dir > 0 ? -1 : _tracks.Rows.Count; // not in the list → start from an edge
        for (int i = cur + dir; i >= 0 && i < _tracks.Rows.Count; i += dir)
        {
            if (_tracks.Rows[i].Tag is Track t && !MediaType.IsVideo(t.MediaType))
            {
                string? p = t.ResolveFilePath(_device!.MountRoot);
                if (!string.IsNullOrEmpty(p) && File.Exists(p)) { PlayAudio(t, p, i); return; }
            }
        }
        // No neighbour to play — leave the bar showing the last track, paused (NextRequested at end-of-list).
    }

    private void SetNowPlayingVisible(bool on)
    {
        if (_content is null) return;
        _content.RowStyles[2].Height = on ? NowPlayingBar.H : 0;
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
        p.Date is DateTime d && d.Year > 1970 ? d.ToString("yyyy. MM. dd. HH:mm") : $"Photo {index + 1}";

    /// <summary>The selected track rows as Track objects, in visible (top-to-bottom) order.</summary>
    private List<Track> SelectedTracks()
    {
        var rows = new List<(int Index, Track T)>();
        foreach (DataGridViewRow r in _tracks.SelectedRows) if (r.Tag is Track t) rows.Add((r.Index, t));
        rows.Sort((a, b) => a.Index.CompareTo(b.Index));
        return rows.Select(x => x.T).ToList();
    }

    /// <summary>Edit the first selected track's tags + star rating, write it back, and refresh the list.</summary>
    private void OnEditTrackInfo()
    {
        if (_lib is null || _device is null || !_device.Profile.CanWrite) return;
        var sel = SelectedTracks();
        if (sel.Count == 0) { SetStatus("Select a song to edit."); return; }
        var t = sel[0];

        using var dlg = new TrackInfoDialog(t);
        if (dlg.ShowDialog(this) != DialogResult.OK || !dlg.HasChanges) return;
        if (!ConfirmWriteOnce()) return;

        Exception? error = null;
        using (var prog = new CopyProgressDialog("Saving song info", 1, (report, cancelled) =>
        {
            report(0, "Updating “" + t.DisplayTitle + "”");
            _lib!.EditTrack(t.UniqueId, dlg.Edit);
            _lib!.Save();
            report(1, "Done");
        }))
        {
            prog.ShowDialog(this);
            error = prog.Error;
        }
        ReloadAfterEdit();
        if (error is not null)
            MessageBox.Show(this, "Saving failed (a backup was kept as iTunesDB.bak):\n\n" + error.Message, "Mixtape", MessageBoxButtons.OK, MessageBoxIcon.Error);
        else
            SetStatus($"Updated “{dlg.Edit.Title ?? t.DisplayTitle}”.");
    }

    /// <summary>Copy the selected songs off the iPod to a folder on the PC (Artist/Album/NN Title), retagged.</summary>
    private void OnExportSelected()
    {
        if (_device is null) return;
        var tracks = SelectedTracks();
        if (tracks.Count == 0) { SetStatus("Select one or more songs to copy to the PC."); return; }
        using var dlg = new FolderBrowserDialog { Description = $"Copy {tracks.Count} song(s) from the iPod into this folder", ShowNewFolderButton = true };
        if (dlg.ShowDialog(this) != DialogResult.OK || string.IsNullOrEmpty(dlg.SelectedPath)) return;
        string dest = dlg.SelectedPath, mount = _device.MountRoot;

        int ok = 0, missing = 0;
        var errors = new List<string>();
        using var prog = new CopyProgressDialog("Copying songs to your PC", tracks.Count, (report, cancelled) =>
        {
            for (int i = 0; i < tracks.Count; i++)
            {
                if (cancelled()) break;
                var t = tracks[i];
                report(i, $"Copying {i + 1} of {tracks.Count}   ·   {t.DisplayTitle}");
                try { if (MusicExporter.ExportOne(t, mount, dest, organize: true, applyTags: true) is null) missing++; else ok++; }
                catch (Exception ex) { errors.Add($"{t.DisplayTitle}: {ex.Message}"); }
            }
        });
        prog.ShowDialog(this);

        string msg = prog.WasCancelled ? $"Stopped — copied {ok} song(s)." : $"Copied {ok} song(s) to:\n{dest}";
        if (missing > 0) msg += $"\n\n{missing} had no file on the iPod (skipped).";
        if (errors.Count > 0) msg += $"\n\n{errors.Count} failed:\n• " + string.Join("\n• ", errors.Take(8));
        MessageBox.Show(this, msg, "Copy to PC", MessageBoxButtons.OK, errors.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
    }

    // ---- detection / device load ----

    private void RefreshDevices()
    {
        _devices.Clear();
        try { _devices.AddRange(DeviceDetector.DetectAll()); }
        catch (Exception ex) { SetStatus("Detection error: " + ex.Message); }

        if (_devices.Count > 0) LoadDevice(_devices[0]);
        else
        {
            _device = null; _lib = null; _db = null; _current = null; _photos = null;
            _viewKind = SidebarRowKind.AllSongs;
            SetCenter();
            _tracks.Rows.Clear();
            _header.SetInfo("", "No iPod connected", "Plug in your iPod and press Refresh — or use Open folder. A Mac-formatted (HFS+) iPod isn't readable on Windows.", 0);
            SetActionButtons(); // recompute visibility + the "No iPod is connected" blocked reason (keeps the buttons shown + clickable)
            BuildSidebar();
            SetStatus("No device.");
        }
    }

    private void OpenFolder()
    {
        using var dlg = new FolderBrowserDialog { Description = "Select the iPod's drive root (the folder that contains iPod_Control)" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        var device = DeviceDetector.Build(dlg.SelectedPath);
        if (device is null)
        {
            MessageBox.Show(this, "That folder has no iPod_Control directory, so it isn't an iPod root.", "Mixtape", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
            _db = _lib.View;
        }
        catch (Exception ex)
        {
            SetStatus("Failed to read iTunesDB: " + ex.Message);
            MessageBox.Show(this, "Could not read this iPod's database:\n\n" + ex.Message, "Mixtape", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _photos = device.Profile.SupportsPhotos ? PhotoLibrary.Load(device) : null;

        RebuildPlaylists();
        SeedDefaultSort();
        _viewKind = SidebarRowKind.AllSongs;
        _current = null;
        _browseFilter = null;
        BuildSidebar();
        ShowCurrent();
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
        _sidebar.AddSection("DEVICE");
        foreach (var d in _devices)
            // Clicking the device opens its info page; the row is highlighted when that page is shown.
            _sidebar.AddItem(SidebarRowKind.Device, d.Profile.ModelName ?? d.Profile.ModelNumber ?? "iPod", d,
                _viewKind == SidebarRowKind.Device && ReferenceEquals(d, _device));

        var others = new List<Playlist>();
        if (_db is not null)
        {
            var master = _db.Master;
            _sidebar.AddSection("LIBRARY");
            _sidebar.AddItem(SidebarRowKind.AllSongs, "All songs", "all", _viewKind == SidebarRowKind.AllSongs);
            _sidebar.AddItem(SidebarRowKind.Albums, "Albums", "albums", _viewKind == SidebarRowKind.Albums);
            _sidebar.AddItem(SidebarRowKind.Artists, "Artists", "artists", _viewKind == SidebarRowKind.Artists);
            if (_device?.Profile.SupportsVideo == true && _settings.ShowVideos)
                _sidebar.AddItem(SidebarRowKind.Videos, "Videos", "videos", _viewKind == SidebarRowKind.Videos);
            if (_device?.Profile.SupportsPhotos == true && _settings.ShowPhotos)
                _sidebar.AddItem(SidebarRowKind.Photos, "Photos", "photos", _viewKind == SidebarRowKind.Photos);

            others = _shownPlaylists.Where(p => !ReferenceEquals(p, master)).ToList();
            // Always show the PLAYLISTS section so the area is discoverable; when empty, a faint hint
            // tells the user they can right-click to make one.
            _sidebar.AddSection("PLAYLISTS");
            if (others.Count > 0)
                foreach (var pl in others)
                    _sidebar.AddItem(SidebarRowKind.Playlist, pl.Name, pl, _viewKind == SidebarRowKind.Playlist && ReferenceEquals(pl, _current));
            else if (_device?.Profile.CanWrite == true)
                _sidebar.AddHint("Right-click here to add one");
        }
        _sidebar.End();
        // Playlists with a chosen cover get it as their sidebar icon instantly; the rest fall back to
        // the background first-track cover.
        var custom = new List<Playlist>();
        foreach (var pl in others)
        {
            string? k = CoverKeyFor(SidebarRowKind.Playlist, pl);
            if (k is null) continue;
            int cid = _settings.GetCover(k);
            if (cid >= 0) { _sidebar.SetIcon(pl, CoverArt.Generate(cid, SidebarIconPx)); custom.Add(pl); }
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
                    _viewKind = kind; _current = null; _browseFilter = null; // clicking the section resets any drill-in
                    BuildSidebar(); ShowCurrent();
                    break;
                case SidebarRowKind.Playlist when tag is Playlist pl:
                    _viewKind = SidebarRowKind.Playlist; _current = pl;
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

        var overlay = new CrossfadePanel(oldBmp, newBmp) { Bounds = new Rectangle(0, 0, center.Width, center.Height) };
        _viewTransitionBusy = true;
        center.Controls.Add(overlay);
        overlay.BringToFront();
        overlay.Start(() => { if (!center.IsDisposed) center.Controls.Remove(overlay); overlay.Dispose(); _viewTransitionBusy = false; });
    }

    // ---- track view ----

    /// <summary>Show exactly one of the centre panels (track grid / photo grid / album-artist grid / device page).</summary>
    private void SetCenter()
    {
        bool photos = _viewKind == SidebarRowKind.Photos;
        bool device = _viewKind == SidebarRowKind.Device;
        bool browse = _viewKind is SidebarRowKind.Albums or SidebarRowKind.Artists && _browseFilter is null; // the grid, not a drill-in
        if (_tracks.Parent is Control gh) gh.Visible = !photos && !device && !browse;
        _photoView.Visible = photos;
        _deviceView.Visible = device;
        _browseView.Visible = browse;
    }

    private void ShowCurrent()
    {
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
            kicker = _browseKicker; title = _browseTitle;
            list = _db.Tracks.Where(t => MediaType.IsAudio(t.MediaType)).Where(_browseFilter).ToList();
        }
        else if (isPlaylist)
        {
            kicker = "PLAYLIST";
            title = _current!.Name.Length == 0 ? "Untitled playlist" : _current.Name;
            list = new List<Track>();
            foreach (uint id in _current.TrackIds) { var t = _db.FindByUniqueId(id); if (t is not null) list.Add(t); }
        }
        else if (isVideos)
        {
            kicker = "LIBRARY"; title = "Videos";
            list = _db.Tracks.Where(t => MediaType.IsVideo(t.MediaType)).ToList();
        }
        else
        {
            kicker = "LIBRARY"; title = "All songs";
            list = _db.Tracks.Where(t => MediaType.IsAudio(t.MediaType)).ToList();
        }

        if (_searchQuery.Length > 0)
            list = list.Where(t => Match(t, _searchQuery)).ToList();

        long totalMs = 0;
        foreach (var t in list) totalMs += t.LengthMs;
        SortTracks(list);
        UpdateSortIndicators();
        int artSize = _settings.Compact ? 30 : 36;

        _tracks.SuspendLayout();
        foreach (var t in list)
        {
            var thumb = Theme.MakeArt(artSize, Theme.StableHash(t.Album ?? t.DisplayTitle)); // placeholder until real art loads
            int r = _tracks.Rows.Add(thumb, t.DisplayTitle, t.Artist ?? "", t.Album ?? "",
                RatingStars(t.Rating), t.PlayCount > 0 ? t.PlayCount.ToString() : "", t.Duration.ToString(@"m\:ss"));
            _tracks.Rows[r].Tag = t;
        }
        _tracks.ResumeLayout();

        string noun = isVideos ? "video" : "song";
        _header.SetInfo(kicker, title, Summary(list.Count, totalMs, noun), Theme.StableHash(title));
        int coverId = CurrentViewCover();
        _currentHasCustomCover = coverId >= 0;
        if (_currentHasCustomCover) _header.SetArt(CoverArt.Generate(coverId, 150)); // chosen art wins over the song thumbnail
        _header.ArtClickable = _viewKind is SidebarRowKind.AllSongs or SidebarRowKind.Playlist; // click the cover to choose art
        string st = $"{list.Count} {noun}{(list.Count == 1 ? "" : "s")}";
        if (_db.Warnings.Count > 0) st += $"   ·   ⚠ {_db.Warnings.Count} warning(s)";
        if (_device is not null && !_device.Profile.CanWrite) st += "   ·   Read-only — " + _device.Profile.WriteBlockReason;
        SetStatus(st);
        SetActionButtons();
        LoadArtworkAsync(list, artSize);
    }

    // ---- album / artist browse ----

    private const int BrowseCover = 150;
    private static string AlbumKey(Track t) => (t.Album ?? "").Trim().ToLowerInvariant() + "" + DisplayAlbumArtist(t).ToLowerInvariant();
    private static string ArtistKey(Track t) => (t.AlbumArtist ?? t.Artist ?? "").Trim();
    private static string DisplayAlbum(Track t) => string.IsNullOrWhiteSpace(t.Album) ? "Unknown Album" : t.Album!;
    private static string DisplayAlbumArtist(Track t) =>
        !string.IsNullOrWhiteSpace(t.AlbumArtist) ? t.AlbumArtist! : !string.IsNullOrWhiteSpace(t.Artist) ? t.Artist! : "Unknown Artist";

    /// <summary>Build the Albums or Artists cover grid from the library; covers load in the background.</summary>
    private void ShowBrowse()
    {
        SetCenter();
        _tracks.Rows.Clear();
        _hotRow = -1;
        _header.ArtClickable = false;

        bool albums = _viewKind == SidebarRowKind.Albums;
        string title = albums ? "Albums" : "Artists";
        if (_db is null) { _browseView.SetItems(Array.Empty<(string, string, string)>(), "—"); _header.SetInfo("LIBRARY", title, "", 0); SetActionButtons(); return; }

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
                cards.Add((grp.Key, grp.Key.Length > 0 ? grp.Key : "Unknown Artist", $"{albumCount} album{(albumCount == 1 ? "" : "s")}  ·  {songs} song{(songs == 1 ? "" : "s")}"));
                reps[grp.Key] = grp.First();
            }
        }

        _browseView.SetItems(cards, albums ? "No albums on this iPod." : "No artists on this iPod.");
        string sub = $"{cards.Count} {(albums ? "album" : "artist")}{(cards.Count == 1 ? "" : "s")}";
        _header.SetInfo("LIBRARY", title, sub, Theme.StableHash(title));
        _header.SetArt(null); // the header uses its generated gradient for these overview pages
        SetStatus(sub);
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
                _browseTitle = first is not null ? DisplayAlbum(first) : "Album";
                _browseKicker = "ALBUM";
            }
            else
            {
                _browseFilter = t => ArtistKey(t) == key;
                _browseTitle = key.Length > 0 ? key : "Unknown Artist";
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
        Task.Run(() =>
        {
            foreach (var j in jobs)
            {
                if (_browseArtGen != gen) return;
                var art = ArtworkService.Load("br:" + j.ArtKey, j.Path, BrowseCover);
                if (art != null) { string key = j.Key; TryBeginInvoke(() => { if (_browseArtGen == gen) _browseView.SetCover(key, art); }); }
            }
        });
    }

    private void ShowPhotos()
    {
        SetCenter();
        _tracks.Rows.Clear();
        _hotRow = -1;

        _header.ArtClickable = false; // covers are for music lists, not the photo library
        if (_photos is null)
        {
            _photoView.SetPhotos(Array.Empty<(uint, Bitmap?)>(), "This iPod can't display photos.");
            _header.SetInfo("LIBRARY", "Photos", "", Theme.StableHash("Photos"));
            SetActionButtons();
            return;
        }

        var photos = _photos.Photos.ToList();
        string empty = _photos.SafeToWrite ? "No photos yet — click “Add photos”." : (_photos.BlockReason ?? "Photos are read-only.");
        // Show the tiles immediately (placeholders); decode the thumbnails in the background so a
        // large library (the user's has 1500+) doesn't freeze the UI on open.
        _photoView.SetPhotos(photos.Select(p => (p.ImageId, (Bitmap?)null)), empty);
        long pb = PhotoBytes();
        string sub = PhotoSummary(photos.Count) + (pb > 0 ? "  ·  " + CapacityBar.Human(pb) : "");
        _header.SetInfo("LIBRARY", "Photos", sub, Theme.StableHash("Photos"));
        using (var hb = photos.Count > 0 ? _photos.RenderThumb(photos[0]) : null)
            _header.SetArt(hb); // header clones it; dispose our fresh copy

        SetActionButtons();
        UpdatePhotoStatus();

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
        bool canAudio = _device?.Profile.CanWrite == true;
        bool canPhotos = _photos?.SafeToWrite == true && _device?.Profile.SupportsPhotos == true;
        bool deviceView = _viewKind == SidebarRowKind.Device;
        _header.AddButton.Visible = !deviceView;   // the device page has its own buttons
        _header.DeleteButton.Visible = !deviceView;
        if (deviceView) return;

        bool photos = _viewKind == SidebarRowKind.Photos;
        bool allowed = photos ? canPhotos : canAudio;
        string reason = photos ? PhotoBlockReason() : AudioBlockReason();
        _header.AddButton.Text = _viewKind switch
        {
            SidebarRowKind.Videos => "Add video",
            SidebarRowKind.Photos => "Add photos",
            _ => "Add music",
        };
        _header.AddButton.BlockedReason = allowed ? null : reason;
        _header.DeleteButton.BlockedReason = allowed ? null : reason;
    }

    private string AudioBlockReason()
    {
        if (_device is null) return "No iPod is connected.";
        var r = _device.Profile.WriteBlockReason;
        return r.Length > 0 ? r : "This iPod is read-only.";
    }

    private string PhotoBlockReason()
    {
        if (_device is null) return "No iPod is connected.";
        if (_device.Profile.SupportsPhotos != true) return "This iPod doesn't have a colour screen, so it can't store photos.";
        return _photos?.BlockReason ?? "Photos can't be written to this iPod.";
    }

    /// <summary>Explain why the greyed Add/Delete button is unavailable, and how to fix it — offering to
    /// jump to the device page (where Read device ID / Restore live) when that's the fix.</summary>
    private void ShowActionBlockedHelp()
    {
        const string title = "Why is this greyed out?";
        string reason, fix;
        bool offerDevicePage = false;

        if (_device is null)
        {
            reason = "No iPod is connected.";
            fix = "Plug in your iPod (in disk mode) and press Refresh, or use Open folder to point at its drive. A Mac-formatted (HFS+) iPod can't be read on Windows.";
        }
        else if (_viewKind == SidebarRowKind.Photos)
        {
            var p = _device.Profile;
            if (p.SupportsPhotos != true)
            {
                reason = "This iPod doesn't have a colour screen, so it can't store photos.";
                fix = "Photos work on the iPod photo, 5G (video), Classic, and nano 3G and later.";
            }
            else
            {
                reason = _photos?.BlockReason ?? "Photos can't be written to this iPod right now.";
                fix = "Check the iPod isn't read-only on its device page, then try again.";
                offerDevicePage = !p.CanWrite;
            }
        }
        else // music / video
        {
            var p = _device.Profile;
            reason = p.WriteBlockReason.Length > 0 ? p.WriteBlockReason : "This iPod is read-only.";
            switch (p.Scheme)
            {
                case ChecksumScheme.Hash58 when string.IsNullOrEmpty(p.FirewireGuid):
                    fix = "Open this iPod's device page and click “Read device ID” — a safe, read-only query that reads the iPod's hardware ID (the same thing iTunes does) so music can be written.";
                    offerDevicePage = true;
                    break;
                case ChecksumScheme.Hash58: // GUID is known, but Mixtape's signature didn't match this iPod's
                    fix = "Mixtape's signature for this iPod didn't match the one already on it, so writing stays disabled to avoid corrupting its library. Open the device page and use “Save report…” so this can be looked into.";
                    offerDevicePage = true;
                    break;
                case ChecksumScheme.Hash72:
                    fix = "This iPod's signature (hash72, used by the nano 5G / Touch) can't be reproduced yet, so writing isn't possible. You can still browse, play, and copy music off the iPod.";
                    break;
                case ChecksumScheme.HashAB:
                    fix = "This iPod uses the experimental hashAB signature (nano 6G/7G), which isn't enabled yet. Browsing and copying off still work.";
                    break;
                default:
                    fix = "See the device page for details on this iPod's signature.";
                    offerDevicePage = true;
                    break;
            }
        }

        string body = reason + "\n\n" + fix;
        if (offerDevicePage && _device is not null
            && MessageBox.Show(this, body + "\n\nOpen the device page now?", title, MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
            GoToDevicePage();
        else if (!offerDevicePage || _device is null)
            MessageBox.Show(this, body, title, MessageBoxButtons.OK, MessageBoxIcon.Information);
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
        SetStatus(sel > 0 ? $"{sel} selected   ·   {total} photo{(total == 1 ? "" : "s")}" : $"{total} photo{(total == 1 ? "" : "s")}");
    }

    private static string PhotoSummary(int count) => $"{count} photo{(count == 1 ? "" : "s")}";

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
        string? key = _viewKind switch
        {
            SidebarRowKind.AllSongs => CoverKeyFor(SidebarRowKind.AllSongs, null),
            SidebarRowKind.Playlist => CoverKeyFor(SidebarRowKind.Playlist, _current),
            _ => null,
        };
        if (key is null) return;
        string title = _viewKind == SidebarRowKind.AllSongs ? "Cover for All songs" : $"Cover for “{_current?.Name}”";
        ChooseCover(key, title);
    }

    private void ChooseCover(string key, string title)
    {
        using var dlg = new CoverPickerDialog(title, _settings.GetCover(key));
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        _settings.SetCover(key, dlg.SelectedCoverId); // -1 reverts to automatic
        BuildSidebar();
        ShowCurrent();
    }

    private void AddCoverItem(ContextMenuStrip m, string key, string title)
    {
        var cover = new ToolStripMenuItem("Choose cover…");
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
            string why = _device.Profile.WriteBlockReason is { Length: > 0 } w ? w : "This iPod is read-only.";
            m.Items.Add(new ToolStripMenuItem("Read-only — " + why) { Enabled = false });
            m.Show(screen);
            return;
        }
        var nu = new ToolStripMenuItem("New playlist…");
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

        string cap = total > 0 ? $"{CapacityBar.Human(total - free)} of {CapacityBar.Human(total)} used" : "Connected";
        _header.SetInfo("DEVICE", p.ModelName ?? p.ModelNumber ?? "iPod", cap, Theme.StableHash(p.ModelName ?? "iPod"));
        using (var art = IpodArt.Render(p.Generation, 150, p.ModelNumber)) _header.SetArt(art); // a picture of THIS iPod, in its real colour
        _header.ArtClickable = false;

        BuildDeviceView(p, total, free, music, video, photoBytes, other, songCount, videoCount, photoCount);
        SetActionButtons();
        SetStatus(p.ModelName ?? "iPod");
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
        _deviceView.SuspendLayout();
        _deviceView.BackColor = Theme.Bg; // field init baked the default Graphite; honor the saved theme variant
        // Dispose the previous page's controls (Region/GDI handles) before detaching them.
        var oldControls = _deviceView.Controls.Cast<Control>().ToArray();
        _deviceView.Controls.Clear();
        foreach (var c in oldControls) c.Dispose();
        const int cardW = 540, x = 24;
        int y = 18;
        void SectionLabel(string t)
        {
            _deviceView.Controls.Add(new Label { Text = t, Font = Theme.UiFont(8f, FontStyle.Bold), ForeColor = Theme.Faint, AutoSize = false, Left = x + 4, Top = y, Width = cardW, Height = 20, TextAlign = ContentAlignment.BottomLeft });
            y += 24;
        }
        void Add(Control c) { c.Left = x; c.Top = y; _deviceView.Controls.Add(c); y += c.Height + 18; }

        if (total > 0)
        {
            SectionLabel("STORAGE");
            var bar = new CapacityBar { Width = cardW };
            bar.Set(total,
                new CapacityBar.Seg("Audio", music, Theme.Accent),
                new CapacityBar.Seg("Video", video, Color.FromArgb(255, 149, 56)),
                new CapacityBar.Seg("Photos", photo, Color.FromArgb(54, 200, 110)),
                new CapacityBar.Seg("Other", other, Theme.Faint),
                new CapacityBar.Seg("Free", free, Theme.Blend(Theme.Bg, Color.White, 0.07))); // matches the recessed track
            Add(bar);
        }

        SectionLabel("ABOUT");
        var about = new CardPanel(cardW);
        about.AddInfoRow("Model", p.ModelName ?? p.ModelNumber ?? "iPod");
        about.AddInfoRow("Generation", p.GenerationDisplay);
        if (total > 0) about.AddInfoRow("Capacity", $"{CapacityBar.Human(total)}  ·  {CapacityBar.Human(free)} free");
        about.AddInfoRow("Songs", songCount.ToString());
        if (p.SupportsVideo) about.AddInfoRow("Videos", videoCount.ToString());
        if (p.SupportsPhotos) about.AddInfoRow("Photos", photoCount.ToString());
        about.AddInfoRow("Signature", p.SchemeLabel);
        about.AddInfoRow("Writable", p.CanWrite ? "Yes" : "No");
        if (!string.IsNullOrEmpty(p.SerialNumber)) about.AddInfoRow("Serial", p.SerialNumber!);
        if (!string.IsNullOrEmpty(p.FirewireGuid)) about.AddInfoRow("FireWire GUID", p.FirewireGuid!);
        about.Finish(); Add(about);

        if (!p.CanWrite && p.WriteBlockReason.Length > 0)
        {
            var ro = new CardPanel(cardW);
            ro.AddRow("Why read-only", p.WriteBlockReason, null, 72);
            ro.Finish(); Add(ro);
        }

        // hash58 device whose GUID isn't on disk → offer to read it straight from the firmware (no iTunes).
        if (!p.CanWrite && p.Scheme == ChecksumScheme.Hash58 && string.IsNullOrEmpty(p.FirewireGuid)
            && dev.MountRoot.Length > 1 && dev.MountRoot[1] == ':')
        {
            var fix = new CardPanel(cardW);
            var fixBtn = new ThemedButton { Text = "Read device ID", Pill = true, Primary = true, Width = 150, Height = 32 };
            fixBtn.Click += (_, _) => EnableWritingByReadingDeviceId(dev);
            fix.AddRow("Enable writing", "Read this iPod's hardware ID directly from the device (a safe, read-only query — the same thing iTunes does) so music can be written. No iTunes needed.", fixBtn, 76);
            fix.Finish(); Add(fix);
        }

        SectionLabel("BACKUPS");
        var backups = new CardPanel(cardW);
        string db = dev.ITunesDbPath, bak = db + ".bak", orig = db + ".original";
        string status = File.Exists(bak) ? $"Last automatic backup: {File.GetLastWriteTime(bak):g}." : File.Exists(orig) ? "Original database kept." : "No backup yet.";
        // No manual "back up now": Mixtape already snapshots iTunesDB.original once and rolls iTunesDB.bak
        // before every write, so a manual copy here would only risk overwriting that good rollback point.
        var restoreBtn = new ThemedButton { Text = "Restore…", Pill = true, Width = 110, Height = 30, Enabled = File.Exists(bak) || File.Exists(orig) };
        restoreBtn.Click += (_, _) => RestoreDatabaseBackup();
        backups.AddRow("Automatic backup", status + " Mixtape backs up before every change and verifies the result.", restoreBtn, 64);
        backups.Finish(); Add(backups);

        SectionLabel("OPTIONS");
        var options = new CardPanel(cardW);
        var settingsBtn = new ThemedButton { Text = "Open Settings", Pill = true, Width = 130, Height = 30 };
        settingsBtn.Click += (_, _) => OpenSettings();
        options.AddRow("All settings", "Appearance, library, video, photos and safety.", settingsBtn, 56);
        var explorerBtn = new ThemedButton { Text = "Show in Explorer", Pill = true, Width = 150, Height = 30 };
        explorerBtn.Click += (_, _) =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{dev.MountRoot}\"") { UseShellExecute = true }); } catch { }
        };
        options.AddRow("Files", "Open the iPod's drive in File Explorer.", explorerBtn, 56);
        var reportBtn = new ThemedButton { Text = "Save report…", Pill = true, Width = 130, Height = 30 };
        reportBtn.Click += (_, _) => SaveDeviceReport(dev);
        options.AddRow("Device report", "Save a diagnostic file (model, signature, why it's read-only) to send for support.", reportBtn, 56);
        options.Finish(); Add(options);

        _deviceView.ResumeLayout();
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
        if (MessageBox.Show(this,
            "This saves a diagnostic text file containing your iPod's identifiers (serial number and FireWire GUID).\n\nOnly share it with someone helping you fix the app. Continue?",
            "Save device report", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK) return;

        // BuildDeviceReportText does a blocking SCSI INQUIRY — run it off the UI thread so the window
        // never freezes, and only after the user confirmed (so a stalled device can't hang on cancel).
        string report;
        var prev = Cursor; Cursor = Cursors.WaitCursor;
        try { report = await System.Threading.Tasks.Task.Run(() => BuildDeviceReportText(dev)); }
        catch (Exception ex) { report = "Report generation failed:\n" + ex; }
        finally { Cursor = prev; }

        using var dlg = new SaveFileDialog
        {
            Title = "Save device report",
            FileName = "Mixtape device report.txt",
            Filter = "Text file|*.txt",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try { File.WriteAllText(dlg.FileName, report); SetStatus("Device report saved."); }
        catch (Exception ex) { MessageBox.Show(this, "Couldn't save the report:\n\n" + ex.Message, "Save report", MessageBoxButtons.OK, MessageBoxIcon.Error); }
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
        if (root.Length < 2 || root[1] != ':') { MessageBox.Show(this, "This only works for an iPod mounted on a drive letter.", "Enable writing", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        char drive = root[0];
        if (MessageBox.Show(this,
            "Mixtape will read this iPod's hardware ID directly from the device — a safe, read-only query (the same one iTunes uses) — and save it to the iPod so music can be written. Nothing on the iPod is changed except adding the standard device-info file.\n\nContinue?",
            "Enable writing", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK) return;

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
            MessageBox.Show(this,
                "Couldn't read a hardware ID from this iPod.\n\n" + (err ?? "The device didn't return a FireWire GUID.") +
                "\n\nYou can still use the iPod read-only (browse, play, copy music off). Use \"Save report…\" to capture details.",
                "Enable writing", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        // Validate with the SAME parser re-detection uses, so we never persist a doc the firmware-read
        // happened to regex-match but DeviceDetector can't actually re-read into a GUID.
        if (SysInfoExtended.TryParse(doc!)?.FirewireGuid is null)
        {
            MessageBox.Show(this,
                $"Read the device ID ({guid}), but the device-info document the iPod returned isn't in a form Mixtape can reliably re-read, so it was NOT saved to the iPod.\n\nPlease use \"Save report…\" and send it.",
                "Enable writing", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
            MessageBox.Show(this, $"Read the device ID ({guid}) but couldn't save it to the iPod:\n\n{ex.Message}", "Enable writing", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        // Re-detect from scratch so the new SysInfoExtended is parsed and the hash58 check re-runs.
        var rebuilt = DeviceDetector.Build(root);
        if (rebuilt is not null) LoadDevice(rebuilt);

        var p = _device?.Profile;
        if (p?.CanWrite == true && p.Hash58Verified == true)
            MessageBox.Show(this, $"Success — read device ID {guid} and verified the signature against this iPod's database. Writing is now enabled.", "Enable writing", MessageBoxButtons.OK, MessageBoxIcon.Information);
        else if (p?.CanWrite == true)
            MessageBox.Show(this, $"Read device ID {guid}. Writing is now enabled.", "Enable writing", MessageBoxButtons.OK, MessageBoxIcon.Information);
        else if (p?.Scheme == ChecksumScheme.Hash58 && p.Hash58Verified == false)
            MessageBox.Show(this, $"Read the device ID ({guid}), but Mixtape's hash58 signature didn't match this iPod's existing one, so writing stays disabled to avoid corrupting its database.\n\nThis iPod is the first real-world test of hash58 signing — please use \"Save report…\" and send it so the signing can be fixed.", "Enable writing", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        else
            MessageBox.Show(this, $"Read the device ID ({guid}). Writing is still disabled — see the reason on this page.", "Enable writing", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private void RestoreDatabaseBackup()
    {
        if (_device is null) return;
        string db = _device.ITunesDbPath, bak = db + ".bak", orig = db + ".original";
        string? source = File.Exists(bak) ? bak : File.Exists(orig) ? orig : null;
        if (source is null) { MessageBox.Show(this, "No database backup was found on this iPod yet.", "Restore", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        string which = source == bak ? "the state before the last change (iTunesDB.bak)" : "the original database from before Mixtape first wrote to it";
        if (MessageBox.Show(this, $"Restore {which}?\n\nThe current database will be replaced.", "Restore database", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        try { File.Copy(source, db, overwrite: true); }
        catch (Exception ex) { MessageBox.Show(this, "Restore failed:\n\n" + ex.Message, "Restore", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
        ReloadCurrentDevice();
        SetStatus("Database restored.");
    }

    /// <summary>Star rating as glyphs (rating is stars×20). Unrated → blank, like iTunes.</summary>
    private static string RatingStars(byte rating)
    {
        int stars = Math.Min(5, rating / 20);
        return stars > 0 ? new string('★', stars) + new string('☆', 5 - stars) : "";
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
            6 => (a, b) => a.LengthMs.CompareTo(b.LengthMs),
            _ => (_, _) => 0,
        };
        list.Sort(cmp);
        if (!_sortAsc) list.Reverse();
    }

    private void UpdateSortIndicators()
    {
        for (int i = 1; i < _tracks.Columns.Count && i < ColBase.Length; i++)
            _tracks.Columns[i].HeaderText = i == _sortCol ? ColBase[i] + (_sortAsc ? "  ↑" : "  ↓") : ColBase[i];
    }

    /// <summary>Background-loads real embedded cover art into the header + each row (album-cached, gen-guarded).</summary>
    private void LoadArtworkAsync(List<Track> tags, int size)
    {
        if (!_settings.ShowArtwork || _device is null || tags.Count == 0) return;
        int gen = ++_artGen;
        string mount = _device.MountRoot;
        var jobs = tags.Select((t, i) => (Index: i, Key: ArtworkService.KeyFor(t), Path: t.ResolveFilePath(mount))).ToList();
        var first = tags[0];

        Task.Run(() =>
        {
            var hdr = ArtworkService.Load(ArtworkService.KeyFor(first), first.ResolveFilePath(mount), 150);
            if (hdr != null) TryBeginInvoke(() => { if (_artGen == gen && !_currentHasCustomCover) _header.SetArt(hdr); });
            foreach (var j in jobs)
            {
                if (_artGen != gen) return;
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
            ? (span.Minutes == 0 ? $"{(int)span.TotalHours} hr" : $"{(int)span.TotalHours} hr {span.Minutes} min")
            : $"{span.Minutes} min";
        return $"{count} {noun}{(count == 1 ? "" : "s")}  ·  {dur}";
    }

    // ---- mutations ----

    private void OnAddMusic()
    {
        if (_lib is null || _device is null || !_device.Profile.CanWrite) return;
        using var dlg = new OpenFileDialog
        {
            Multiselect = true,
            Title = "Select music to copy onto the iPod",
            Filter = "Audio files|*.mp3;*.m4a;*.aac;*.wav;*.aif;*.aiff;*.m4b;*.flac;*.ogg;*.oga;*.opus;*.wma;*.ape;*.wv;*.mpc|All files|*.*",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK || dlg.FileNames.Length == 0) return;
        AddMusicFiles(dlg.FileNames);
    }

    /// <summary>Core music-add (shared by the button and drag-and-drop): confirm, copy/transcode on a background thread, save.</summary>
    private void AddMusicFiles(string[] files)
    {
        if (_lib is null || _device is null || !_device.Profile.CanWrite || files.Length == 0) return;

        // FLAC/OGG/Opus/WMA (or "always re-encode") need ffmpeg → AAC; mp3/m4a/wav/aiff copy as-is.
        var ffmpeg = FfmpegService.Detect(_settings.FfmpegPath);
        bool anyNeedsTranscode = files.Any(f => !IsNativeAudio(f)); // only non-native files truly require ffmpeg
        if (ffmpeg is null && anyNeedsTranscode)
        {
            var r = MessageBox.Show(this,
                "Some of these need converting to an iPod format (e.g. FLAC / OGG / Opus / WMA), but ffmpeg wasn't found.\n\n" +
                "• Install ffmpeg (`winget install Gyan.FFmpeg`) and it's picked up automatically, or set its path in Settings ▸ Video.\n\n" +
                "Continue and copy just the already-compatible files (mp3, m4a, wav, aiff)?",
                "ffmpeg not found", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
            if (r != DialogResult.OK) return;
        }
        if (!ConfirmWriteOnce()) return;

        int ok = 0;
        var errors = new List<string>();
        string tempDir = Path.Combine(Path.GetTempPath(), "mixtape-audio");

        // *100 scale so transcoding shows a percentage; copy-only files just jump to the next step.
        using (var prog = new CopyProgressDialog("Adding music to your iPod", files.Length * 100, (report, cancelled) =>
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
                        throw new InvalidOperationException("needs ffmpeg to convert — skipped.");
                    if (transcode)
                    {
                        double dur = ffmpeg!.Probe(src)?.DurationSec ?? 0;
                        temp = Path.Combine(tempDir, Guid.NewGuid().ToString("N") + ".m4a");
                        report(baseP, $"Converting {i + 1} of {files.Length}   ·   {name}"); // advance even when duration is unknown
                        ffmpeg.TranscodeAudio(src, temp, 256, dur,
                            frac => report(baseP + (int)(frac * 98), $"Converting {i + 1} of {files.Length}   ·   {(int)(frac * 100)}%   ·   {name}"),
                            cancelled);
                        report(baseP + 99, $"Copying {i + 1} of {files.Length}   ·   {name}");
                        // ffmpeg preserved the tags into the .m4a; keep the source's title as a safety net.
                        _lib!.AddMediaFile(temp, MediaType.Audio, MetadataExtractor.Read(src).Title, dur);
                    }
                    else
                    {
                        report(baseP, $"Copying {i + 1} of {files.Length}   ·   {name}");
                        _lib!.AddFile(src);
                    }
                    ok++;
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { errors.Add($"{name}: {ex.Message}"); }
                finally { if (temp is not null) { try { File.Delete(temp); } catch { } } }
            }
            if (ok > 0) { report(files.Length * 100, "Saving the iPod database…"); _lib!.Save(); }
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
        }))
        {
            prog.ShowDialog(this);
            ReloadAfterEdit();

            if (prog.Error is not null)
            {
                MessageBox.Show(this, "Writing the database failed (a backup was kept as iTunesDB.bak):\n\n" + prog.Error.Message, "Mixtape", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            string msg = prog.WasCancelled ? $"Stopped — added {ok} song(s)." : $"Added {ok} song(s).";
            if (errors.Count > 0) msg += $"\n\n{errors.Count} could not be added:\n• " + string.Join("\n• ", errors);
            MessageBox.Show(this, msg, "Mixtape", MessageBoxButtons.OK, errors.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
        }
    }

    private void OnDelete()
    {
        if (_lib is null || _device is null || !_device.Profile.CanWrite) return;
        var ids = new List<uint>();
        foreach (DataGridViewRow row in _tracks.SelectedRows)
            if (row.Tag is Track t) ids.Add(t.UniqueId);
        if (ids.Count == 0) { SetStatus("Select one or more songs to delete."); return; }

        var confirm = MessageBox.Show(this,
            $"Delete {ids.Count} song(s) from the iPod, including the audio file(s)?\n\nThis can't be undone, but a backup of the database is kept (iTunesDB.bak).",
            "Delete songs", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
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
            MessageBox.Show(this, "Writing the database failed (a backup was kept as iTunesDB.bak):\n\n" + ex.Message, "Mixtape", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        finally { Cursor = Cursors.Default; }

        ReloadAfterEdit();
        SetStatus($"Deleted {ids.Count} song(s).");
    }

    // ---- action dispatch (the header buttons adapt to the active view) ----

    private void OnAddClicked()
    {
        switch (_viewKind)
        {
            case SidebarRowKind.Videos: OnAddVideo(); break;
            case SidebarRowKind.Photos: ShowPhotoAddMenu(); break;
            default: OnAddMusic(); break;
        }
    }

    /// <summary>The Photos "Add" button offers individual files or a whole folder (incl. subfolders).</summary>
    private void ShowPhotoAddMenu()
    {
        if (_photos is null || _device is null) return;
        if (!_photos.SafeToWrite) { MessageBox.Show(this, _photos.BlockReason ?? "Photos are read-only on this iPod.", "Mixtape", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        var m = ThemedMenu.New();
        var files = new ToolStripMenuItem("Add photos…"); files.Click += (_, _) => OnAddPhotos();
        var folder = new ToolStripMenuItem("Add folder…   (includes subfolders)"); folder.Click += (_, _) => OnAddPhotoFolder();
        m.Items.Add(files);
        m.Items.Add(folder);
        var b = _header.AddButton;
        m.Show(b.PointToScreen(new Point(0, b.Height + 2)));
    }

    /// <summary>Pick a folder; add every image inside it and all its subfolders.</summary>
    private void OnAddPhotoFolder()
    {
        if (_photos is null || _device is null || !_photos.SafeToWrite) return;
        using var dlg = new FolderBrowserDialog { Description = "Select a folder — every photo inside it and its subfolders will be added", ShowNewFolderButton = false };
        if (dlg.ShowDialog(this) != DialogResult.OK || string.IsNullOrEmpty(dlg.SelectedPath)) return;

        string[] images;
        try { UseWaitCursor = true; images = GatherFiles(dlg.SelectedPath, ImageExt); }
        finally { UseWaitCursor = false; }

        if (images.Length == 0)
        {
            MessageBox.Show(this, "No photos (jpg, png, …) were found in that folder or its subfolders.", "Add folder", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (MessageBox.Show(this, $"Found {images.Length} photo(s) in “{Path.GetFileName(dlg.SelectedPath.TrimEnd(Path.DirectorySeparatorChar))}” (including subfolders).\n\nAdd them all to the iPod?",
                "Add folder", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        AddPhotoFiles(images);
    }

    private void OnDeleteClicked()
    {
        if (_viewKind == SidebarRowKind.Photos) OnDeletePhotos();
        else OnDelete();
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

    private void OnDragDrop(object? sender, DragEventArgs e)
    {
        if (_device is null || e.Data?.GetData(DataFormats.FileDrop) is not string[] paths) return;
        // Expand any dropped FOLDERS into their media files recursively (incl. subfolders), then route by type.
        var media = AudioExt.Concat(VideoExt).Concat(ImageExt).ToArray();
        var files = paths.Where(File.Exists).ToList();
        foreach (var dir in paths.Where(Directory.Exists)) files.AddRange(GatherFiles(dir, media));
        var audio = files.Where(f => IsExt(f, AudioExt)).ToArray();
        var video = files.Where(f => IsExt(f, VideoExt)).ToArray();
        var images = files.Where(f => IsExt(f, ImageExt)).ToArray();
        if (audio.Length == 0 && video.Length == 0 && images.Length == 0)
        {
            SetStatus("Drop music, video, or photo files — or a folder of them — to add them.");
            return;
        }
        // Each runs its own progress dialog; a mixed drop processes them in turn.
        if (audio.Length > 0) AddMusicFiles(audio);
        if (video.Length > 0) AddVideoFiles(video);
        if (images.Length > 0) AddPhotoFiles(images);
    }

    // ---- video ----

    private void OnAddVideo()
    {
        if (_lib is null || _device is null || !_device.Profile.CanWrite) return;
        using var dlg = new OpenFileDialog
        {
            Multiselect = true,
            Title = "Select video to copy onto the iPod",
            Filter = "Video files|*.mp4;*.m4v;*.mov;*.avi;*.mkv;*.wmv;*.flv;*.webm;*.mpg;*.mpeg|All files|*.*",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK || dlg.FileNames.Length == 0) return;
        AddVideoFiles(dlg.FileNames);
    }

    /// <summary>Core video-add (shared by the button and drag-and-drop): detect ffmpeg, transcode if needed, save.</summary>
    private void AddVideoFiles(string[] files)
    {
        if (_lib is null || _device is null || !_device.Profile.CanWrite || files.Length == 0) return;

        var ffmpeg = FfmpegService.Detect(_settings.FfmpegPath);
        if (ffmpeg is null)
        {
            var r = MessageBox.Show(this,
                "ffmpeg was not found, so videos can't be converted to an iPod-compatible format.\n\n" +
                "• Install ffmpeg (e.g. `winget install Gyan.FFmpeg`) and it'll be picked up automatically, or set its path in Settings ▸ Video.\n\n" +
                "Copy the selected file(s) as-is for now? They will only play if they are already iPod-compatible H.264/MPEG-4.",
                "ffmpeg not found", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
            if (r != DialogResult.OK) return;
        }
        if (!ConfirmWriteOnce()) return;

        var target = VideoTarget.ForQuality(_settings.VideoQuality);
        int ok = 0;
        var errors = new List<string>();
        string tempDir = Path.Combine(Path.GetTempPath(), "mixtape-video");

        using var prog = new CopyProgressDialog("Adding video to your iPod", files.Length * 100, (report, cancelled) =>
        {
            Directory.CreateDirectory(tempDir);
            for (int i = 0; i < files.Length; i++)
            {
                if (cancelled()) break;
                string src = files[i], name = Path.GetFileName(src);
                int baseP = i * 100;
                report(baseP, $"Processing {i + 1} of {files.Length}   ·   {name}");
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
                                frac => report(baseP + (int)(frac * 98), $"Converting {i + 1} of {files.Length}   ·   {(int)(frac * 100)}%   ·   {name}"),
                                cancelled);
                            toCopy = temp;
                        }
                    }
                    else
                    {
                        // No ffmpeg: only an already-packaged MP4-family container has any chance of playing.
                        string ext = Path.GetExtension(src).ToLowerInvariant();
                        if (ext is not (".m4v" or ".mp4" or ".mov"))
                            throw new InvalidOperationException("not an iPod-compatible container — install ffmpeg to convert it.");
                    }
                    report(baseP + 99, $"Copying {i + 1} of {files.Length}   ·   {name}");
                    _lib!.AddMediaFile(toCopy, MediaType.Movie, Path.GetFileNameWithoutExtension(src), durSec);
                    ok++;
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { errors.Add($"{name}: {ex.Message}"); }
                finally { if (temp is not null) { try { File.Delete(temp); } catch { } } }
            }
            if (ok > 0) { report(files.Length * 100, "Saving the iPod database…"); _lib!.Save(); }
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
        });
        prog.ShowDialog(this);
        _viewKind = SidebarRowKind.Videos;
        ReloadAfterEdit();

        if (prog.Error is not null)
        {
            MessageBox.Show(this, "Writing the database failed (a backup was kept as iTunesDB.bak):\n\n" + prog.Error.Message, "Mixtape", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        string msg = prog.WasCancelled ? $"Stopped — added {ok} video(s)." : $"Added {ok} video(s).";
        if (errors.Count > 0) msg += $"\n\n{errors.Count} could not be added:\n• " + string.Join("\n• ", errors);
        MessageBox.Show(this, msg, "Mixtape", MessageBoxButtons.OK, errors.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
    }

    // ---- photos ----

    private void OnAddPhotos()
    {
        if (_photos is null || _device is null) return;
        if (!_photos.SafeToWrite)
        {
            MessageBox.Show(this, _photos.BlockReason ?? "Photos are read-only on this iPod.", "Mixtape", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        using var dlg = new OpenFileDialog
        {
            Multiselect = true,
            Title = "Select photos to copy onto the iPod",
            Filter = "Image files|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.tif;*.tiff;*.webp|All files|*.*",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK || dlg.FileNames.Length == 0) return;
        AddPhotoFiles(dlg.FileNames);
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
        using var prog = new CopyProgressDialog("Adding photos to your iPod", files.Length, (report, cancelled) =>
        {
            int staged = 0;
            for (int i = 0; i < files.Length; i++)
            {
                if (cancelled()) break;
                report(i, $"Rendering {i + 1} of {files.Length}   ·   {Path.GetFileName(files[i])}");
                try { _photos!.AddPhoto(files[i]); ok++; staged++; }
                catch (Exception ex) { errors.Add($"{Path.GetFileName(files[i])}: {ex.Message}"); }
                if (staged >= BatchSize) { report(i + 1, $"Saving… ({ok} added so far)"); _photos!.Save(); staged = 0; }
            }
            if (staged > 0) { report(files.Length, "Writing the photo library… (this can take a moment)"); _photos!.Save(); }
        });
        prog.ShowDialog(this);
        ShowPhotos();

        if (prog.Error is not null)
        {
            MessageBox.Show(this, "Writing the photo library failed (a backup was kept as Photo Database.bak):\n\n" + prog.Error.Message, "Mixtape", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        string msg = prog.WasCancelled ? $"Stopped — added {ok} photo(s)." : $"Added {ok} photo(s).";
        if (errors.Count > 0) msg += $"\n\n{errors.Count} could not be added:\n• " + string.Join("\n• ", errors);
        MessageBox.Show(this, msg, "Mixtape", MessageBoxButtons.OK, errors.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
    }

    private void OnDeletePhotos()
    {
        if (_photos is null || !_photos.SafeToWrite) return;
        var ids = _photoView.SelectedIds;
        if (ids.Count == 0) { SetStatus("Select one or more photos to delete."); return; }
        if (MessageBox.Show(this, $"Delete {ids.Count} photo(s) from the iPod?\n\nA backup of the photo database is kept (Photo Database.bak).",
                "Delete photos", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        if (!ConfirmWriteOnce()) return;

        Exception? error = null;
        using (var prog = new CopyProgressDialog("Updating the photo library", 1, (report, cancelled) =>
        {
            report(0, "Removing photos and repacking…");
            _photos!.DeletePhotos(ids);
            _photos!.Save();
            report(1, "Done");
        }))
        {
            prog.ShowDialog(this);
            error = prog.Error;
        }
        ShowPhotos();
        if (error is not null)
            MessageBox.Show(this, "Writing the photo library failed (a backup was kept as Photo Database.bak):\n\n" + error.Message, "Mixtape", MessageBoxButtons.OK, MessageBoxIcon.Error);
        else
            SetStatus($"Deleted {ids.Count} photo(s).");
    }

    private void ShowPhotoMenu(Point screen)
    {
        if (_photos is null) return;
        var m = ThemedMenu.New();
        var sel = _photoView.SelectedIds;
        if (sel.Count > 0) // viewing isn't a write — offered even on read-only iPods
        {
            uint vid = sel[0];
            var view = new ToolStripMenuItem("View");
            view.Click += (_, _) => OpenPhotoViewer(vid);
            m.Items.Add(view);
            m.Items.Add(new ToolStripSeparator());
        }
        if (!_photos.SafeToWrite)
        {
            m.Items.Add(new ToolStripMenuItem("Read-only — " + (_photos.BlockReason ?? "")) { Enabled = false });
            m.Show(screen);
            return;
        }
        var ids = _photoView.SelectedIds;
        if (ids.Count > 0)
        {
            var del = new ToolStripMenuItem($"Delete {ids.Count} photo(s) from iPod");
            del.Click += (_, _) => OnDeletePhotos();
            m.Items.Add(del);
            m.Items.Add(new ToolStripSeparator());
        }
        var add = new ToolStripMenuItem("Add photos…");
        add.Click += (_, _) => OnAddPhotos();
        m.Items.Add(add);
        var addFolder = new ToolStripMenuItem("Add folder…   (includes subfolders)");
        addFolder.Click += (_, _) => OnAddPhotoFolder();
        m.Items.Add(addFolder);
        m.Show(screen);
    }

    // ---- context menus (right-click) ----

    private void OnSidebarRightClick(SidebarRowKind kind, object? tag, Point screen)
    {
        if (_db is null) return;

        // The library row offers just the cover chooser (a local preference, always available).
        if (kind == SidebarRowKind.AllSongs)
        {
            string? lk = CoverKeyFor(SidebarRowKind.AllSongs, null);
            if (lk is null) return;
            var lm = ThemedMenu.New();
            AddCoverItem(lm, lk, "Cover for All songs");
            lm.Show(screen);
            return;
        }

        if (kind != SidebarRowKind.Playlist || tag is not Playlist pl) return;
        var m = ThemedMenu.New();

        // Choosing a cover is a local preference, so offer it even on read-only iPods.
        string? ck = CoverKeyFor(SidebarRowKind.Playlist, pl);
        if (ck is not null) { AddCoverItem(m, ck, $"Cover for “{pl.Name}”"); m.Items.Add(new ToolStripSeparator()); }

        if (_lib is null || _device is null || !_device.Profile.CanWrite)
        {
            string why = _device?.Profile.WriteBlockReason is { Length: > 0 } w ? w : "This iPod is read-only.";
            m.Items.Add(new ToolStripMenuItem("Read-only — " + why) { Enabled = false });
            m.Show(screen);
            return;
        }
        var rename = new ToolStripMenuItem("Rename…");
        rename.Click += (_, _) => RenamePlaylistInteractive(pl);
        m.Items.Add(rename);
        m.Items.Add(new ToolStripSeparator());
        var del = new ToolStripMenuItem("Delete playlist (keep songs)");
        del.Click += (_, _) => DeletePlaylist(pl);
        m.Items.Add(del);
        m.Items.Add(new ToolStripSeparator());
        var nu = new ToolStripMenuItem("New playlist…");
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
        ShowTrackMenu(_tracks.PointToScreen(e.Location));
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
            var play = new ToolStripMenuItem(MediaType.IsVideo(ft.MediaType) ? "Play video" : "Play");
            play.Click += (_, _) => ActivateTrackRow(fr);
            m.Items.Add(play);
        }

        // Copy off the iPod to the PC — a read, so offered on every device (incl. read-only).
        var copyOut = new ToolStripMenuItem($"Copy to PC…   ({ids.Count})");
        copyOut.Click += (_, _) => OnExportSelected();
        m.Items.Add(copyOut);
        m.Items.Add(new ToolStripSeparator());

        if (!_device.Profile.CanWrite)
        {
            string why = _device.Profile.WriteBlockReason.Length > 0 ? _device.Profile.WriteBlockReason : "This iPod is read-only.";
            m.Items.Add(new ToolStripMenuItem("Read-only — " + why) { Enabled = false });
            m.Show(screen);
            return;
        }

        // Edit tags + star rating.
        var edit = new ToolStripMenuItem("Edit info…");
        edit.Click += (_, _) => OnEditTrackInfo();
        m.Items.Add(edit);
        m.Items.Add(new ToolStripSeparator());

        // Add to playlist ▸ (existing playlists + New playlist…)
        var addTo = new ToolStripMenuItem("Add to playlist");
        foreach (var pl in _shownPlaylists.Where(p => _db is not null && !ReferenceEquals(p, _db.Master) && !p.IsPodcast))
        {
            var plRef = pl;
            var it = new ToolStripMenuItem(pl.Name.Length == 0 ? "Untitled" : pl.Name);
            it.Click += (_, _) => AddSelectedToPlaylist(plRef, ids);
            addTo.DropDownItems.Add(it);
        }
        if (addTo.DropDownItems.Count > 0) addTo.DropDownItems.Add(new ToolStripSeparator());
        var newWith = new ToolStripMenuItem("New playlist…");
        newWith.Click += (_, _) => CreatePlaylistWithTracks(ids);
        addTo.DropDownItems.Add(newWith);
        addTo.DropDown.BackColor = Theme.PanelBg;
        addTo.DropDown.Renderer = new DarkMenuRenderer();
        m.Items.Add(addTo);
        m.Items.Add(new ToolStripSeparator());

        bool inUserPlaylist = _db is not null && _current is not null && !ReferenceEquals(_current, _db.Master) && !_current.IsPodcast;
        if (inUserPlaylist)
        {
            var rem = new ToolStripMenuItem($"Remove from “{_current!.Name}”   ({ids.Count})");
            rem.Click += (_, _) => RemoveFromCurrentPlaylist(ids);
            m.Items.Add(rem);
            m.Items.Add(new ToolStripSeparator());
        }
        var del = new ToolStripMenuItem($"Delete from iPod   ({ids.Count})");
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
        string label = pl.Name.Length == 0 ? "Untitled" : pl.Name;
        if (MessageBox.Show(this, $"Delete the playlist “{label}”?\n\nThe songs stay on the iPod — only the playlist itself is removed.",
                "Delete playlist", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        if (!ConfirmWriteOnce()) return;
        try { Cursor = Cursors.WaitCursor; _lib.RemovePlaylist(pl); _lib.Save(); }
        catch (Exception ex) { MessageBox.Show(this, "Write failed (backup kept as iTunesDB.bak):\n\n" + ex.Message, "Mixtape", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
        finally { Cursor = Cursors.Default; }
        ReloadAfterEdit();
        SetStatus($"Deleted playlist “{label}” — songs kept.");
    }

    private void RenamePlaylistInteractive(Playlist pl)
    {
        if (_lib is null) return;
        string? name = PromptDialog.Show(this, "Rename playlist", "New name:", pl.Name);
        if (string.IsNullOrWhiteSpace(name) || name.Trim() == pl.Name) return;
        if (!ConfirmWriteOnce()) return;
        try { Cursor = Cursors.WaitCursor; _lib.RenamePlaylist(pl, name.Trim()); _lib.Save(); }
        catch (Exception ex) { MessageBox.Show(this, "Write failed (backup kept as iTunesDB.bak):\n\n" + ex.Message, "Mixtape", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
        finally { Cursor = Cursors.Default; }
        ReloadAfterEdit();
        SetStatus($"Renamed playlist to “{name.Trim()}”.");
    }

    private void AddSelectedToPlaylist(Playlist pl, List<uint> ids)
    {
        if (_lib is null) return;
        if (!ConfirmWriteOnce()) return;
        try { Cursor = Cursors.WaitCursor; _lib.AddToPlaylist(pl.PersistentId, ids); _lib.Save(); }
        catch (Exception ex) { MessageBox.Show(this, "Write failed (backup kept as iTunesDB.bak):\n\n" + ex.Message, "Mixtape", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
        finally { Cursor = Cursors.Default; }
        ReloadAfterEdit();
        SetStatus($"Added {ids.Count} song(s) to “{pl.Name}”.");
    }

    private void CreatePlaylistWithTracks(List<uint> ids)
    {
        if (_lib is null) return;
        string? name = PromptDialog.Show(this, "New playlist", "Playlist name:", "New Playlist");
        if (string.IsNullOrWhiteSpace(name)) return;
        if (!ConfirmWriteOnce()) return;
        try
        {
            Cursor = Cursors.WaitCursor;
            ulong pid = _lib.CreatePlaylist(name.Trim());
            if (ids.Count > 0) _lib.AddToPlaylist(pid, ids);
            _lib.Save();
        }
        catch (Exception ex) { MessageBox.Show(this, "Write failed (backup kept as iTunesDB.bak):\n\n" + ex.Message, "Mixtape", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
        finally { Cursor = Cursors.Default; }
        ReloadAfterEdit();
        SetStatus($"Created playlist “{name.Trim()}”" + (ids.Count > 0 ? $" with {ids.Count} song(s)." : "."));
    }

    private void RemoveFromCurrentPlaylist(List<uint> ids)
    {
        if (_lib is null || _current is null) return;
        if (!ConfirmWriteOnce()) return;
        try { Cursor = Cursors.WaitCursor; _lib.RemoveFromPlaylist(_current, ids); _lib.Save(); }
        catch (Exception ex) { MessageBox.Show(this, "Write failed (backup kept as iTunesDB.bak):\n\n" + ex.Message, "Mixtape", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
        finally { Cursor = Cursors.Default; }
        ReloadAfterEdit();
        SetStatus($"Removed {ids.Count} song(s) from the playlist.");
    }

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

    /// <summary>Re-apply every visual setting live (called from the Settings window as things change).</summary>
    private void ApplyAllSettings()
    {
        Theme.SetThemeVariant(_settings.ThemeVariant);
        Theme.SetAccent(_settings.Accent);
        RestyleEverything();
        if (_tracks.Columns.Count > 0) _tracks.Columns[0].Visible = _settings.ShowArtwork;
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
    }

    /// <summary>Show/hide the Artist/Album/Time columns per settings.</summary>
    private void ApplyColumns()
    {
        if (_tracks.Columns.Count < 7) return;
        _tracks.Columns[2].Visible = _settings.ShowArtist;
        _tracks.Columns[3].Visible = _settings.ShowAlbum;
        _tracks.Columns[4].Visible = _settings.ShowRating;
        _tracks.Columns[5].Visible = _settings.ShowPlays;
        _tracks.Columns[6].Visible = _settings.ShowTime;
    }

    /// <summary>Re-colour the BackColor-baked panels + grid after a background-theme change (owner-painted controls repaint via Invalidate).</summary>
    private void RestyleEverything()
    {
        BackColor = Theme.Bg;
        ForeColor = Theme.TextCol;
        ApplyWindowChrome();                          // re-melt the title bar into the new variant's wallpaper
        if (_root is not null) _root.Invalidate();   // repaint the gradient wallpaper for the new variant
        if (_content is not null) _content.BackColor = Theme.Bg;
        if (_photoView.Parent is Control center) center.BackColor = Theme.Bg;
        if (_tracks.Parent is Control gh) gh.BackColor = Theme.Bg;
        if (_status.Parent is Control sp) sp.BackColor = Theme.SidebarBg;
        _status.BackColor = Theme.SidebarBg;
        _status.ForeColor = Theme.Subtle;
        _deviceView.BackColor = Theme.Bg;
        Theme.StyleGrid(_tracks);
        _tracks.RowTemplate.Height = _settings.RowHeight;
        var sel = Theme.Blend(Theme.Bg, Theme.Accent, 0.22);
        _tracks.DefaultCellStyle.SelectionBackColor = sel;
        _tracks.AlternatingRowsDefaultCellStyle.SelectionBackColor = sel;
    }

    private void SeedDefaultSort()
    {
        _sortCol = _settings.DefaultSort switch { "Song" => 1, "Artist" => 2, "Album" => 3, "Rating" => 4, "Plays" => 5, "Time" => 6, _ => -1 };
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
        string drive = _device?.MountRoot.TrimEnd('\\') ?? "the iPod";
        var r = MessageBox.Show(this,
            "Mixtape backs up the database (iTunesDB.bak) before every write and verifies the result afterwards.\n\n" +
            $"Tip: if Windows has flagged this iPod's drive for repair, run  chkdsk {drive} /f  first.\n\nContinue?",
            "Writing to the iPod", MessageBoxButtons.OKCancel, MessageBoxIcon.Information);
        if (r == DialogResult.OK) { _writeConfirmed = true; return true; }
        return false;
    }

    private void SetStatus(string text) => _status.Text = text;

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
    }
}
