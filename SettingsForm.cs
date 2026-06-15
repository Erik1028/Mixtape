namespace iPodCommander;

/// <summary>
/// The Settings window, styled after Windows 11 Settings: a left category rail (Appearance, Library,
/// Video, Photos, Safety, This iPod, About) and a right page of individually-rounded rows
/// (title + subtitle + control). Each control writes straight to <see cref="AppSettings"/>, saves,
/// and calls back so the main window re-applies the change immediately.
/// </summary>
internal sealed class SettingsForm : Form
{
    private readonly AppSettings _s;
    private readonly IPodDevice? _device;
    private readonly Action _applyChanged;
    private readonly Action _reloadDevice;

    private readonly SettingsNav _nav;
    private readonly Panel _pane;

    private int _homeTop;       // resting Top, captured on first show (anchor for the open/close slide)
    private bool _closingAnim;  // true once the dismiss animation has begun

    private const int NavW = 212;                       // left category rail
    private const int SideMargin = 24;                  // symmetric gutter for the card column + page title
    private const int CardW = 608;                      // card width (fills the pane to a matching right gutter)
    private const int PaneW = CardW + SideMargin * 2;   // content pane width
    private const int ContentLeft = SideMargin;         // card column left edge
    private const int MinHeight = 380, MaxHeight = 620; // size-to-content clamp

    private static readonly string[] Categories = { "Appearance", "Library", "Video", "Photos", "Safety", "This iPod", "About" };

    public SettingsForm(AppSettings settings, IPodDevice? device, Action applyChanged, Action reloadDevice)
    {
        _s = settings; _device = device; _applyChanged = applyChanged; _reloadDevice = reloadDevice;

        Text = "Settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
        ClientSize = new Size(NavW + PaneW, 600);
        BackColor = Theme.Bg;
        ForeColor = Theme.TextCol;
        Font = Theme.UiFont(9.5f);

        _pane = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Theme.Bg, Padding = new Padding(0, 0, 0, 18) };
        _nav = new SettingsNav(Categories) { Dock = DockStyle.Left, Width = NavW };
        _nav.Selected += ShowCategory;
        Controls.Add(_pane);
        Controls.Add(_nav);
        ShowCategory(0);

        if (Anim.MotionEnabled) Opacity = 0; // fade up from invisible in OnShown
    }

    /// <summary>Fade + rise into place when the window opens (Apple-modal entrance).</summary>
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _homeTop = Top;
        if (!Anim.MotionEnabled) { Opacity = 1; return; }
        Top = _homeTop + 16;
        Anim.Run(190, v =>
        {
            if (IsDisposed) return;
            Opacity = v;
            Top = _homeTop + (int)Math.Round(16 * (1 - v));
        }, () => { if (!IsDisposed) { Opacity = 1; Top = _homeTop; } }, Easings.OutCubic);
    }

    /// <summary>Fade + settle down on dismiss before the window actually closes.</summary>
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_closingAnim || !Anim.MotionEnabled) { base.OnFormClosing(e); return; }
        e.Cancel = true;
        _closingAnim = true;
        Anim.Run(130, v =>
        {
            if (IsDisposed) return;
            Opacity = 1 - v;
            Top = _homeTop + (int)Math.Round(10 * v);
        }, () => { if (!IsDisposed) Close(); }, Easings.OutCubic);
    }

    private int _y;

    /// <summary>Render-harness hook: switch to a category by index (used by <c>--render settingsN</c>).</summary>
    public void RenderCategory(int index) { _nav.SelectedIndex = Math.Clamp(index, 0, Categories.Length - 1); }

    private void Rebuild() => ShowCategory(_nav.SelectedIndex);

    private int _lastCat;

    private void ShowCategory(int index)
    {
        bool categoryChanged = index != _lastCat;
        _lastCat = index;
        var old = _pane.Controls.Cast<Control>().ToArray();
        _pane.Controls.Clear();
        // The page-title Label owns a DisplayFont; CardPanels dispose their own fonts. Free the title's
        // font here so switching categories doesn't leak a GDI font each time.
        foreach (var c in old) { if (c is Label lbl) lbl.Font.Dispose(); c.Dispose(); }

        _y = 6;
        PageTitle(Categories[index]);
        switch (index)
        {
            case 0: BuildAppearance(); break;
            case 1: BuildLibrary(); break;
            case 2: BuildVideo(); break;
            case 3: BuildPhotos(); break;
            case 4: BuildSafety(); break;
            case 5: BuildDevice(); break;
            default: BuildAbout(); break;
        }

        // Size the dialog to its content (clamped) so sparse pages (Photos/Safety/About) aren't a
        // mostly-empty 600px window, while dense ones (Library) still fit without scrolling. _y already
        // holds the exact content bottom (+10 trailing gap) since the layout is absolute.
        int desired = Math.Clamp(_y + 12, MinHeight, MaxHeight);
        if (ClientSize.Height != desired) ClientSize = new Size(ClientSize.Width, desired);

        if (categoryChanged) AnimatePaneIn();
    }

    /// <summary>Settle the freshly-built page in with a small upward slide.</summary>
    private void AnimatePaneIn()
    {
        if (!Anim.MotionEnabled) return;
        var kids = _pane.Controls.Cast<Control>().ToArray();
        var baseTops = Array.ConvertAll(kids, c => c.Top);
        Anim.Run(180, v =>
        {
            if (IsDisposed) return;
            int dy = (int)Math.Round(12 * (1 - v));
            _pane.SuspendLayout();
            for (int i = 0; i < kids.Length; i++) if (!kids[i].IsDisposed) kids[i].Top = baseTops[i] + dy;
            _pane.ResumeLayout();
        }, null, Easings.OutCubic);
    }

    // ---- category pages ----

    private void BuildAppearance()
    {
        var accent = new AccentPicker(_s.Accent);
        accent.AccentChosen += name => { _s.Accent = name; _s.Save(); _applyChanged(); Rebuild(); };
        Row("Accent colour", "Used for highlights, buttons and selection.", accent);

        var theme = new SegmentedControl { Options = Theme.ThemeVariants, SelectedIndex = Math.Max(0, Array.IndexOf(Theme.ThemeVariants, _s.ThemeVariant)), Width = 320 };
        theme.SelectedChanged += () => { _s.ThemeVariant = Theme.ThemeVariants[theme.SelectedIndex]; _s.Save(); _applyChanged(); BackColor = Theme.Bg; _pane.BackColor = Theme.Bg; _nav.Invalidate(); Rebuild(); };
        Row("Background", "The window's colour palette.", theme);

        var density = new SegmentedControl { Options = new[] { "Comfortable", "Compact" }, SelectedIndex = _s.Compact ? 1 : 0, Width = 220 };
        density.SelectedChanged += () => { _s.Compact = density.SelectedIndex == 1; _s.Save(); _applyChanged(); };
        Row("Row density", "How tall the song rows are.", density);

        Row("Show artwork", "Show album/photo covers in lists.", Toggle(_s.ShowArtwork, v => { _s.ShowArtwork = v; _s.Save(); _applyChanged(); }));
    }

    private void BuildLibrary()
    {
        string[] sorts = { "Playlist", "Song", "Artist", "Album", "Added", "Time" };
        var sort = new SegmentedControl { Options = sorts, SelectedIndex = Math.Max(0, Array.IndexOf(sorts, _s.DefaultSort)), Width = 396 };
        sort.SelectedChanged += () => { _s.DefaultSort = sorts[sort.SelectedIndex]; _s.Save(); _applyChanged(); };
        Row("Default sort", "Column a list is sorted by when it opens.", sort);
        Row("Sort descending", "Reverse the default sort order.", Toggle(_s.DefaultSortDescending, v => { _s.DefaultSortDescending = v; _s.Save(); _applyChanged(); }));
        Row("Show Videos", "List the Videos library (video-capable iPods).", Toggle(_s.ShowVideos, v => { _s.ShowVideos = v; _s.Save(); _applyChanged(); }));
        Row("Show Photos", "List the Photos library (colour-screen iPods).", Toggle(_s.ShowPhotos, v => { _s.ShowPhotos = v; _s.Save(); _applyChanged(); }));
        Row("Artist column", "Show the Artist column in the song list.", Toggle(_s.ShowArtist, v => { _s.ShowArtist = v; _s.Save(); _applyChanged(); }));
        Row("Album column", "Show the Album column in the song list.", Toggle(_s.ShowAlbum, v => { _s.ShowAlbum = v; _s.Save(); _applyChanged(); }));
        Row("Star rating column", "Show your star ratings in the song list.", Toggle(_s.ShowRating, v => { _s.ShowRating = v; _s.Save(); _applyChanged(); }));
        Row("Play count column", "Show how many times each song has been played.", Toggle(_s.ShowPlays, v => { _s.ShowPlays = v; _s.Save(); _applyChanged(); }));
        Row("Date added column", "Show when each song was added to the iPod.", Toggle(_s.ShowDateAdded, v => { _s.ShowDateAdded = v; _s.Save(); _applyChanged(); }));
        Row("Time column", "Show the Time column in the song list.", Toggle(_s.ShowTime, v => { _s.ShowTime = v; _s.Save(); _applyChanged(); }));
    }

    private void BuildVideo()
    {
        var quality = new SegmentedControl { Options = new[] { "iPod-safe", "High (Classic)" }, SelectedIndex = string.Equals(_s.VideoQuality, "High", StringComparison.OrdinalIgnoreCase) ? 1 : 0, Width = 230 };
        quality.SelectedChanged += () => { _s.VideoQuality = quality.SelectedIndex == 1 ? "High" : "Safe"; _s.Save(); _applyChanged(); };
        Row("Quality", "iPod-safe (320×240) plays on every model; High (640×480) is Classic/5.5G only.", quality);
        Row("Always re-encode", "Convert even files that already look compatible.", Toggle(_s.AlwaysTranscode, v => { _s.AlwaysTranscode = v; _s.Save(); }));

        var ff = FfmpegService.Detect(_s.FfmpegPath);
        var browse = new ThemedButton { Text = "Browse…", Pill = true, Width = 96, Height = 30 };
        browse.Click += (_, _) =>
        {
            using var d = new OpenFileDialog { Title = "Locate ffmpeg.exe", Filter = "ffmpeg|ffmpeg.exe|All files|*.*" };
            if (d.ShowDialog(this) == DialogResult.OK) { _s.FfmpegPath = d.FileName; _s.Save(); Rebuild(); }
        };
        Row("ffmpeg", ff is null ? "Not found — install ffmpeg or browse to ffmpeg.exe to enable video conversion." : "Found: " + ff.FfmpegPath, browse);
    }

    private void BuildPhotos()
    {
        Row("Store full-screen image", "Also write the 320×240 image so photos look sharp on the iPod (uses more space).",
            Toggle(_s.PhotoStoreFullResolution, v => { _s.PhotoStoreFullResolution = v; _s.Save(); }));
    }

    private void BuildSafety()
    {
        Row("Confirm before writing", "Show a reminder before the first change each session.", Toggle(_s.ConfirmWrites, v => { _s.ConfirmWrites = v; _s.Save(); }));
        if (_device is not null)
        {
            var restore = new ThemedButton { Text = "Restore…", Pill = true, Width = 110, Height = 30 };
            restore.Click += (_, _) => RestoreBackup();
            Row("Database backup", "Mixtape backs up before every change and verifies the result. Restore rolls back to the previous state.", restore);
        }
    }

    private void BuildDevice()
    {
        if (_device is null) { Row("No iPod", "Connect an iPod to see its details.", null); return; }
        var p = _device.Profile;
        var rows = new List<(string, string)>
        {
            ("Model", p.ModelName ?? p.ModelNumber ?? "iPod"),
            ("Generation", p.GenerationDisplay),
            ("Capacity", DriveSummary(_device.MountRoot)),
            ("Signature", p.SchemeLabel),
            ("Writable", p.CanWrite ? "Yes" : "No"),
            ("Plays video", p.SupportsVideo ? "Yes" : "No"),
            ("Shows photos", p.SupportsPhotos ? "Yes" : "No"),
        };
        if (!string.IsNullOrEmpty(p.SerialNumber)) rows.Add(("Serial", p.SerialNumber!));
        if (!string.IsNullOrEmpty(p.FirewireGuid)) rows.Add(("FireWire GUID", p.FirewireGuid!));
        InfoGroup(rows);
        if (!p.CanWrite && p.WriteBlockReason.Length > 0)
            Row("Why read-only", p.WriteBlockReason, null);
    }

    private void BuildAbout()
    {
        Row("Mixtape", "Version 0.6.1", null);
        Row("A friendly manager for classic iPods", "Copy music, videos and photos; make playlists and mixtapes; choose covers — all written natively, no iTunes.", null);
    }

    // ---- row builders ----

    private void PageTitle(string text)
    {
        // Align the heading text with the card label column (card.Left + the card's internal labelLeft)
        // so the page title and every row title share one left edge.
        _pane.Controls.Add(new Label { Text = text, Font = Theme.DisplayFont(17f, FontStyle.Bold), ForeColor = Theme.TextCol, AutoSize = false, Left = ContentLeft + 18, Top = _y, Width = CardW, Height = 34, TextAlign = ContentAlignment.MiddleLeft });
        _y += 44;
    }

    private void Row(string title, string? subtitle, Control? control, int height = 0)
    {
        int rowH = height > 0 ? height : (subtitle is null ? 52 : MeasureRowHeight(subtitle, control));
        var card = new CardPanel(CardW) { Left = ContentLeft, Top = _y };
        card.AddRow(title, subtitle, control, rowH);
        card.Finish();
        _pane.Controls.Add(card);
        _y += card.Height + 10;
    }

    /// <summary>Row height for a titled row whose subtitle may wrap to two+ lines (mirrors CardPanel.AddRow geometry).</summary>
    private static int MeasureRowHeight(string subtitle, Control? control)
    {
        const int labelLeft = 18, gap = 16, rightPad = 18;
        int ctrlLeft = control is not null ? CardW - rightPad - control.Width : CardW - rightPad;
        int labelW = Math.Max(80, ctrlLeft - gap - labelLeft);
        using var f = Theme.UiFont(9f);
        int descH = TextRenderer.MeasureText(subtitle, f, new Size(labelW, 0), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height;
        return Math.Max(62, 28 + descH + 16); // title band + wrapped subtitle + bottom padding
    }

    /// <summary>A single grouped card of read-only "label … value" rows with internal hairline dividers.</summary>
    private void InfoGroup(List<(string Label, string Value)> rows)
    {
        var card = new CardPanel(CardW) { Left = ContentLeft, Top = _y };
        foreach (var (l, v) in rows) card.AddInfoRow(l, v);
        card.Finish();
        _pane.Controls.Add(card);
        _y += card.Height + 10;
    }

    private static ToggleSwitch Toggle(bool initial, Action<bool> onChange)
    {
        var t = new ToggleSwitch { Checked = initial };
        t.CheckedChanged += () => onChange(t.Checked);
        return t;
    }

    private static string DriveSummary(string root)
    {
        try
        {
            var di = new DriveInfo(root);
            return $"{di.AvailableFreeSpace / 1e9:0.0} GB free of {di.TotalSize / 1e9:0.0} GB";
        }
        catch { return "—"; }
    }

    private void RestoreBackup()
    {
        if (_device is null) return;
        string db = _device.ITunesDbPath, bak = db + ".bak", orig = db + ".original";
        string? source = File.Exists(bak) ? bak : File.Exists(orig) ? orig : null;
        if (source is null) { MessageBox.Show(this, "No database backup was found on this iPod yet.", "Restore", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        string which = source == bak ? "the state before the last change (iTunesDB.bak)" : "the original database from before Mixtape first wrote to it";
        if (MessageBox.Show(this, $"Restore {which}?\n\nThe current database will be replaced.", "Restore database", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        try { File.Copy(source, db, overwrite: true); }
        catch (Exception ex) { MessageBox.Show(this, "Restore failed:\n\n" + ex.Message, "Restore", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
        _reloadDevice();
        MessageBox.Show(this, "Database restored.", "Restore", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try { int on = 1; DwmSetWindowAttribute(Handle, 20, ref on, sizeof(int)); } catch { }
        try { int caption = 0x001A1716; DwmSetWindowAttribute(Handle, 35, ref caption, sizeof(int)); } catch { }
    }
}
