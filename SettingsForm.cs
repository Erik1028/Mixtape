namespace iPodCommander;

/// <summary>
/// The Settings window, styled after Windows 11 Settings: a left category rail (Appearance, Library,
/// Video, Photos, Safety, This iPod, About) and a right page of individually-rounded rows
/// (title + subtitle + control). Each control writes straight to <see cref="AppSettings"/>, saves,
/// and calls back so the main window re-applies the change immediately.
/// </summary>
internal sealed class SettingsForm : Form, IMessageFilter
{
    private readonly AppSettings _s;
    private readonly IPodDevice? _device;
    private readonly Action _applyChanged;
    private readonly Action _reloadDevice;

    private readonly SettingsNav _nav;
    private readonly Panel _pane;        // clipping viewport
    private readonly Panel _paneBody;    // the (taller) content panel, scrolled by its Top
    private readonly ThinScrollBar _paneScroll;

    private int _homeTop;       // resting Top, captured on first show (anchor for the open/close slide)
    private bool _closingAnim;  // true once the dismiss animation has begun

    private const int NavW = 212;                       // left category rail
    private const int SideMargin = 24;                  // symmetric gutter for the card column + page title
    private const int CardW = 608;                      // card width (fills the pane to a matching right gutter)
    private const int PaneW = CardW + SideMargin * 2;   // content pane width
    private const int ContentLeft = SideMargin;         // card column left edge
    private const int PageHeight = 380; // every category uses this one compact height; dense pages (Library) scroll via the themed ThinScrollBar instead of growing the window

    private static readonly string[] Categories = { "Appearance", "Library", "Video", "Photos", "Safety", "This iPod", "About" };

    public SettingsForm(AppSettings settings, IPodDevice? device, Action applyChanged, Action reloadDevice)
    {
        _s = settings; _device = device; _applyChanged = applyChanged; _reloadDevice = reloadDevice;

        Text = Loc.T("Settings");
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
        ClientSize = new Size(NavW + PaneW, 600);
        BackColor = Theme.Bg;
        ForeColor = Theme.TextCol;
        Font = Theme.UiFont(9.5f);

        _pane = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };   // clipping viewport
        _paneBody = new Panel { BackColor = Theme.Bg, Location = new Point(0, 0), Width = PaneW };
        _paneScroll = new ThinScrollBar();
        _pane.Controls.Add(_paneBody);
        _pane.Controls.Add(_paneScroll);
        _paneScroll.AttachScrollPanel(_pane, _paneBody);
        _pane.Resize += (_, _) => LayoutPane();
        _nav = new SettingsNav(Array.ConvertAll(Categories, Loc.T)) { Dock = DockStyle.Left, Width = NavW };
        _nav.Selected += ShowCategory;
        Controls.Add(_pane);
        Controls.Add(_nav);
        Application.AddMessageFilter(this);   // route the mouse wheel over the pane to the themed scrollbar
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
        var old = _paneBody.Controls.Cast<Control>().ToArray();
        _paneBody.Controls.Clear();
        // The page-title Label owns a DisplayFont; CardPanels dispose their own fonts. Free the title's
        // font here so switching categories doesn't leak a GDI font each time.
        foreach (var c in old) { if (c is Label lbl) lbl.Font.Dispose(); c.Dispose(); }

        _y = 6;
        PageTitle(Loc.T(Categories[index]));
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

        // Size the scrollable body to its content and reset it to the top; the themed scrollbar appears
        // only when this exceeds the (screen-clamped) window height.
        _paneBody.Top = 0;
        _paneBody.Height = _y + 18;

        // Fixed, compact window height for EVERY category (no jiggle when switching, no ballooning toward
        // full-screen on the dense Library page) — content taller than the viewport scrolls via the themed
        // ThinScrollBar. Capped to the screen so it never opens taller than the desktop.
        int desired = Math.Min(PageHeight, Screen.FromControl(this).WorkingArea.Height - 72);
        if (ClientSize.Height != desired) ClientSize = new Size(ClientSize.Width, desired);

        // Re-lay-out the themed scrollbar for the (rare) case content still exceeds the screen-capped window.
        LayoutPane();
        if (categoryChanged) AnimatePaneIn();
    }

    /// <summary>Settle the freshly-built page in with a small upward slide.</summary>
    private void AnimatePaneIn()
    {
        if (!Anim.MotionEnabled) return;
        var kids = _paneBody.Controls.Cast<Control>().ToArray();
        var baseTops = Array.ConvertAll(kids, c => c.Top);
        Anim.Run(180, v =>
        {
            if (IsDisposed) return;
            int dy = (int)Math.Round(12 * (1 - v));
            _paneBody.SuspendLayout();
            for (int i = 0; i < kids.Length; i++) if (!kids[i].IsDisposed) kids[i].Top = baseTops[i] + dy;
            _paneBody.ResumeLayout();
        }, null, Easings.OutCubic);
    }

    /// <summary>Keep the themed scrollbar pinned to the pane's right edge; the body fills the rest of the
    /// width so it never sits on top of (and hides) the scrollbar — the same contract the song list uses.</summary>
    private void LayoutPane()
    {
        int bar = _paneScroll.Width;
        _paneBody.Width = Math.Max(0, _pane.ClientSize.Width - bar);
        _paneScroll.Bounds = new Rectangle(_pane.ClientSize.Width - bar, 0, bar, _pane.ClientSize.Height);
        _paneScroll.BringToFront();
    }

    /// <summary>Route the mouse wheel to the themed scrollbar when the pointer is over the content pane.</summary>
    bool IMessageFilter.PreFilterMessage(ref Message m)
    {
        const int WM_MOUSEWHEEL = 0x020A;
        if (m.Msg != WM_MOUSEWHEEL || IsDisposed || !_pane.IsHandleCreated) return false;
        var p = _pane.PointToClient(Cursor.Position);
        if (p.X < 0 || p.Y < 0 || p.X >= _pane.ClientSize.Width || p.Y >= _pane.ClientSize.Height) return false;
        _paneScroll.ScrollByWheel((short)((long)m.WParam >> 16));
        return true;
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        Application.RemoveMessageFilter(this);
        base.OnFormClosed(e);
    }

    // ---- category pages ----

    private void BuildAppearance()
    {
        var langNames = Array.ConvertAll(Loc.Languages, l => l.Native);
        var lang = new SegmentedControl { Options = langNames, SelectedIndex = Math.Max(0, Array.FindIndex(Loc.Languages, l => l.Code == Loc.Lang)), Width = 220 };
        lang.SelectedChanged += () =>
        {
            string code = Loc.Languages[lang.SelectedIndex].Code;
            if (code == Loc.Lang) return;
            _s.Language = code; _s.Save();
            PromptLanguageRestart();
        };
        Row(Loc.T("Language"), Loc.T("Choose the app's language. Mixtape restarts to apply."), lang);

        var accent = new AccentPicker(_s.Accent);
        accent.AccentChosen += name => { _s.Accent = name; _s.Save(); _applyChanged(); Rebuild(); };
        Row(Loc.T("Accent colour"), Loc.T("Used for highlights, buttons and selection."), accent);

        var theme = new SegmentedControl { Options = Theme.ThemeVariants, SelectedIndex = Math.Max(0, Array.IndexOf(Theme.ThemeVariants, _s.ThemeVariant)), Width = 432 };
        theme.SelectedChanged += () => { _s.ThemeVariant = Theme.ThemeVariants[theme.SelectedIndex]; _s.Save(); _applyChanged(); BackColor = Theme.Bg; _pane.BackColor = Theme.Bg; _paneBody.BackColor = Theme.Bg; _nav.Invalidate(); Rebuild(); };
        Row(Loc.T("Background"), Loc.T("The window's colour palette."), theme);

        var density = new SegmentedControl { Options = new[] { Loc.T("Comfortable"), Loc.T("Compact") }, SelectedIndex = _s.Compact ? 1 : 0, Width = 220 };
        density.SelectedChanged += () => { _s.Compact = density.SelectedIndex == 1; _s.Save(); _applyChanged(); };
        Row(Loc.T("Row density"), Loc.T("How tall the song rows are."), density);

        Row(Loc.T("Show artwork"), Loc.T("Show album/photo covers in lists."), Toggle(_s.ShowArtwork, v => { _s.ShowArtwork = v; _s.Save(); _applyChanged(); }));
    }

    /// <summary>Offer to relaunch so the new language takes effect. "Restart now" starts a fresh instance with
    /// <c>--relaunch</c> (which waits for this one's single-instance lock to release) and exits this one.</summary>
    private void PromptLanguageRestart()
    {
        if (MessageDialog.Show(this, Loc.T("The language changes after a restart. Restart Mixtape now?"),
                Loc.T("Restart Mixtape?"), MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        try
        {
            if (Environment.ProcessPath is string exe)
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, "--relaunch") { UseShellExecute = true });
                Application.Exit();
            }
        }
        catch { }
    }

    private void BuildLibrary()
    {
        string[] sorts = { "Playlist", "Song", "Artist", "Album", "Added", "Time" };   // stored values (English) — translate only the display
        var sort = new SegmentedControl { Options = Array.ConvertAll(sorts, Loc.T), SelectedIndex = Math.Max(0, Array.IndexOf(sorts, _s.DefaultSort)), Width = 396 };
        sort.SelectedChanged += () => { _s.DefaultSort = sorts[sort.SelectedIndex]; _s.Save(); _applyChanged(); };
        Row(Loc.T("Default sort"), Loc.T("Column a list is sorted by when it opens."), sort);
        Row(Loc.T("Sort descending"), Loc.T("Reverse the default sort order."), Toggle(_s.DefaultSortDescending, v => { _s.DefaultSortDescending = v; _s.Save(); _applyChanged(); }));
        Row(Loc.T("Show Videos"), Loc.T("List the Videos library (video-capable iPods)."), Toggle(_s.ShowVideos, v => { _s.ShowVideos = v; _s.Save(); _applyChanged(); }));
        Row(Loc.T("Show Photos"), Loc.T("List the Photos library (colour-screen iPods)."), Toggle(_s.ShowPhotos, v => { _s.ShowPhotos = v; _s.Save(); _applyChanged(); }));
        Row(Loc.T("Artist column"), Loc.T("Show the Artist column in the song list."), Toggle(_s.ShowArtist, v => { _s.ShowArtist = v; _s.Save(); _applyChanged(); }));
        Row(Loc.T("Album column"), Loc.T("Show the Album column in the song list."), Toggle(_s.ShowAlbum, v => { _s.ShowAlbum = v; _s.Save(); _applyChanged(); }));
        Row(Loc.T("Star rating column"), Loc.T("Show your star ratings in the song list."), Toggle(_s.ShowRating, v => { _s.ShowRating = v; _s.Save(); _applyChanged(); }));
        Row(Loc.T("Play count column"), Loc.T("Show how many times each song has been played."), Toggle(_s.ShowPlays, v => { _s.ShowPlays = v; _s.Save(); _applyChanged(); }));
        Row(Loc.T("Date added column"), Loc.T("Show when each song was added to the iPod."), Toggle(_s.ShowDateAdded, v => { _s.ShowDateAdded = v; _s.Save(); _applyChanged(); }));
        Row(Loc.T("Time column"), Loc.T("Show the Time column in the song list."), Toggle(_s.ShowTime, v => { _s.ShowTime = v; _s.Save(); _applyChanged(); }));
    }

    private void BuildVideo()
    {
        var quality = new SegmentedControl { Options = new[] { Loc.T("iPod-safe"), Loc.T("High (Classic)") }, SelectedIndex = string.Equals(_s.VideoQuality, "High", StringComparison.OrdinalIgnoreCase) ? 1 : 0, Width = 230 };
        quality.SelectedChanged += () => { _s.VideoQuality = quality.SelectedIndex == 1 ? "High" : "Safe"; _s.Save(); _applyChanged(); };
        Row(Loc.T("Quality"), Loc.T("iPod-safe (320×240) plays on every model; High (640×480) is Classic/5.5G only."), quality);
        Row(Loc.T("Always re-encode"), Loc.T("Convert even files that already look compatible."), Toggle(_s.AlwaysTranscode, v => { _s.AlwaysTranscode = v; _s.Save(); }));

        var ff = FfmpegService.Detect(_s.FfmpegPath);
        var browse = new ThemedButton { Text = Loc.T("Browse…"), Pill = true, Width = 96, Height = 30 };
        browse.Click += (_, _) =>
        {
            using var d = new OpenFileDialog { Title = Loc.T("Locate ffmpeg.exe"), Filter = "ffmpeg|ffmpeg.exe|All files|*.*" };
            if (d.ShowDialog(this) == DialogResult.OK) { _s.FfmpegPath = d.FileName; _s.Save(); Rebuild(); }
        };
        Row("ffmpeg", ff is null ? Loc.T("Not found — install ffmpeg or browse to ffmpeg.exe to enable video conversion.") : Loc.T("Found: {0}", ff.FfmpegPath), browse);
    }

    private void BuildPhotos()
    {
        Row(Loc.T("Store full-screen image"), Loc.T("Also write the 320×240 image so photos look sharp on the iPod (uses more space)."),
            Toggle(_s.PhotoStoreFullResolution, v => { _s.PhotoStoreFullResolution = v; _s.Save(); }));
    }

    private void BuildSafety()
    {
        Row(Loc.T("Confirm before writing"), Loc.T("Show a reminder before the first change each session."), Toggle(_s.ConfirmWrites, v => { _s.ConfirmWrites = v; _s.Save(); }));
        Row(Loc.T("Auto device-ID recovery"), Loc.T("When a hash58 iPod with no stored ID is plugged in, offer to read its hardware ID automatically (a safe, read-only query) so music can be written — no hunting for the “Read device ID” button."), Toggle(_s.AutoGuidRecovery, v => { _s.AutoGuidRecovery = v; _s.Save(); }));
        if (_device is not null)
        {
            var restore = new ThemedButton { Text = Loc.T("Restore…"), Pill = true, Width = 110, Height = 30 };
            restore.Click += (_, _) => RestoreBackup();
            Row(Loc.T("Database backup"), Loc.T("Mixtape backs up before every change and verifies the result. Restore rolls back to the previous state."), restore);
        }
    }

    private void BuildDevice()
    {
        if (_device is null) { Row(Loc.T("No iPod"), Loc.T("Connect an iPod to see its details."), null); return; }
        var p = _device.Profile;
        var rows = new List<(string, string)>
        {
            (Loc.T("Model"), p.ModelName ?? p.ModelNumber ?? "iPod"),
            (Loc.T("Generation"), p.GenerationDisplay),
            (Loc.T("Capacity"), DriveSummary(_device.MountRoot)),
            (Loc.T("Signature"), p.SchemeLabel),
            (Loc.T("Writable"), p.CanWrite ? Loc.T("Yes") : Loc.T("No")),
            (Loc.T("Plays video"), p.SupportsVideo ? Loc.T("Yes") : Loc.T("No")),
            (Loc.T("Shows photos"), p.SupportsPhotos ? Loc.T("Yes") : Loc.T("No")),
        };
        if (!string.IsNullOrEmpty(p.SerialNumber)) rows.Add((Loc.T("Serial"), p.SerialNumber!));
        if (!string.IsNullOrEmpty(p.FirewireGuid)) rows.Add((Loc.T("FireWire GUID"), p.FirewireGuid!));
        InfoGroup(rows);
        if (!p.CanWrite && p.WriteBlockReason.Length > 0)
            Row(Loc.T("Why read-only"), p.WriteBlockReason, null);
    }

    private void BuildAbout()
    {
        Row("Mixtape", Loc.T("Version {0}", "0.11.0"), null);
        Row(Loc.T("A friendly manager for classic iPods"), Loc.T("Copy music, videos and photos; make playlists and mixtapes; choose covers — all written natively, no iTunes."), null);
    }

    // ---- row builders ----

    private void PageTitle(string text)
    {
        // Align the heading text with the card label column (card.Left + the card's internal labelLeft)
        // so the page title and every row title share one left edge.
        _paneBody.Controls.Add(new Label { Text = text, Font = Theme.DisplayFont(17f, FontStyle.Bold), ForeColor = Theme.TextCol, AutoSize = false, Left = ContentLeft + 18, Top = _y, Width = CardW, Height = 34, TextAlign = ContentAlignment.MiddleLeft });
        _y += 44;
    }

    private void Row(string title, string? subtitle, Control? control, int height = 0)
    {
        int rowH = height > 0 ? height : (subtitle is null ? 52 : MeasureRowHeight(subtitle, control));
        var card = new CardPanel(CardW) { Left = ContentLeft, Top = _y };
        card.AddRow(title, subtitle, control, rowH);
        card.Finish();
        _paneBody.Controls.Add(card);
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
        _paneBody.Controls.Add(card);
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
        if (source is null) { MessageDialog.Show(this, Loc.T("No database backup was found on this iPod yet."), Loc.T("Restore"), MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        string which = source == bak ? Loc.T("the state before the last change (iTunesDB.bak)") : Loc.T("the original database from before Mixtape first wrote to it");
        if (MessageDialog.Show(this, Loc.T("Restore {0}?\n\nThe current database will be replaced.", which), Loc.T("Restore database"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        try { File.Copy(source, db, overwrite: true); }
        catch (Exception ex) { MessageDialog.Show(this, Loc.T("Restore failed:\n\n{0}", ex.Message), Loc.T("Restore"), MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
        _reloadDevice();
        MessageDialog.Show(this, Loc.T("Database restored."), Loc.T("Restore"), MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    [System.Runtime.InteropServices.DllImport("uxtheme.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hWnd, string? subAppName, string? subIdList);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try { int on = 1; DwmSetWindowAttribute(Handle, 20, ref on, sizeof(int)); } catch { }
        try { int caption = 0x001A1716; DwmSetWindowAttribute(Handle, 35, ref caption, sizeof(int)); } catch { }
        // Dark scrollbar instead of the bright native one, for the rare case the pane still scrolls
        // (a screen too short to fit a dense page).
        try { SetWindowTheme(_pane.Handle, "DarkMode_Explorer", null); } catch { }
    }
}
