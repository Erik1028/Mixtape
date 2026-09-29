namespace iPodCommander;

/// <summary>
/// The Settings window, styled after Windows 11 Settings: a left category rail (Appearance, Library,
/// Video, Photos, Safety, This iPod, About) and a right page of individually-rounded rows
/// (title + subtitle + control). Each control writes straight to <see cref="AppSettings"/>, saves,
/// and calls back so the main window re-applies the change immediately.
/// </summary>
internal sealed class SettingsForm : GlassDialog, IMessageFilter
{
    private readonly AppSettings _s;
    private readonly IPodDevice? _device;
    private readonly Action _applyChanged;
    private readonly Action _reloadDevice;

    private readonly SettingsNav _nav;
    private readonly Panel _pane;        // clipping viewport
    private readonly Panel _paneBody;    // the (taller) content panel, scrolled by its Top
    private readonly ThinScrollBar _paneScroll;

    // ---- the Classic (Windows 95) layout: a property sheet ----
    private ClassicPropertySheet? _sheet;
    private ClassicGroupBox? _group;       // the group box rows go into while one is open
    private int _gy;                       // the layout cursor inside it
    private int _col;                      // check boxes run two to a line; 1 = the next one takes the right column
    private GlassLabel? _descLabel;        // the Description box's text
    private bool _hasDesc;                 // this page has something to describe
    private const int ClassicW = 540, ClassicH = 500;        // the sheet's client area (inside the window frame)
    private const int ClassicBodyW = ClassicW - 32, ClassicBodyH = ClassicH - 84;

    private int _homeTop;       // resting Top, captured on first show (anchor for the open/close slide)
    private bool _closingAnim;  // true once the dismiss animation has begun

    private const int NavW = 212;                       // left category rail
    private const int SideMargin = 24;                  // symmetric gutter for the card column + page title
    private const int CardW = 608;                      // card width (fills the pane to a matching right gutter)
    private const int PaneW = CardW + SideMargin * 2;   // content pane width
    private const int ContentLeft = SideMargin;         // card column left edge
    private const int PageHeight = 540; // every category uses this one height; dense pages (Library) scroll via the themed ThinScrollBar instead of growing the window (taller so it's not "flat")

    private static readonly string[] Categories = { "Appearance", "Library", "Video", "Photos", "Safety", "Discord", "This iPod", "About" };

    public SettingsForm(AppSettings settings, IPodDevice? device, Action applyChanged, Action reloadDevice, int startCategory = 0)
    {
        _s = settings; _device = device; _applyChanged = applyChanged; _reloadDevice = reloadDevice;

        Text = Loc.T("Settings");
        FormBorderStyle = FormBorderStyle.None;   // borderless: our own caption strip (no system title-bar slab), like the main window
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
        ClientSize = new Size(NavW + PaneW, PageHeight + DialogTitleBar.H);   // + the custom caption strip; matches the per-page height so there's no open-then-shrink flash
        BackColor = Theme.Bg;
        if (Theme.Classic)
        {
            // A 95 window's raised frame: the docked caption, rail and pane sit two pixels in, and the form paints
            // the frame in the margin that leaves.
            Padding = new Padding(2);
            ClientSize = new Size(ClientSize.Width + 4, ClientSize.Height + 4);
            Paint += (_, pe) => Theme.Bevel(pe.Graphics, ClientRectangle, raised: true);
        }
        ForeColor = Theme.TextCol;
        Font = Theme.UiFont(9.5f);

        _pane = new GlassPanel { Dock = DockStyle.Fill, BackColor = Theme.Bg };   // clipping viewport (frosted glass in this dialog)
        _paneBody = new GlassPanel { BackColor = Theme.Bg, Location = new Point(0, 0), Width = PaneW };
        _paneScroll = new ThinScrollBar();
        _pane.Controls.Add(_paneBody);
        _pane.Controls.Add(_paneScroll);
        _paneScroll.AttachScrollPanel(_pane, _paneBody);
        _pane.Resize += (_, _) => LayoutPane();
        _nav = new SettingsNav(Array.ConvertAll(Categories, Loc.T)) { Dock = DockStyle.Left, Width = NavW };
        _nav.Selected += ShowCategory;
        if (Theme.Classic)
        {
            // A 1995 property sheet: the tabs across the top, one raised page under them, OK at the bottom right.
            ClientSize = new Size(ClassicW + 4, ClassicH + DialogTitleBar.H + 4);
            var sheetHost = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Face, Padding = new Padding(6, 6, 6, 0) };
            _sheet = new ClassicPropertySheet(Array.ConvertAll(Categories, Loc.T)) { Dock = DockStyle.Fill };
            _sheet.Selected += ShowCategory;
            _pane.Dock = DockStyle.None;
            _pane.Bounds = new Rectangle(10, ClassicPropertySheet.TabH + 10, ClassicBodyW, ClassicBodyH);
            _sheet.Resize += (_, _) => { var pr = _sheet.PageRect; _pane.Bounds = new Rectangle(pr.X + 10, pr.Y + 10, Math.Max(1, pr.Width - 20), Math.Max(1, pr.Height - 18)); };
            _sheet.Controls.Add(_pane);
            sheetHost.Controls.Add(_sheet);
            var buttons = new Panel { Dock = DockStyle.Bottom, Height = 40, BackColor = Theme.Face };
            var ok = new ThemedButton { Text = Loc.T("OK"), Primary = true, Width = 75, Height = 23 };
            ok.Click += (_, _) => Close();
            buttons.Controls.Add(ok);
            buttons.Resize += (_, _) => ok.Location = new Point(buttons.Width - 6 - ok.Width, 9);
            AcceptButton = ok;
            Controls.Add(sheetHost);
            Controls.Add(buttons);
            Controls.Add(new DialogTitleBar(Loc.T("Settings"), 0));
        }
        else
        {
        Controls.Add(_pane);
        Controls.Add(_nav);
        Controls.Add(new DialogTitleBar(Loc.T("Settings"), NavW));   // added LAST so it docks to the top first; nav+pane fill below it
        }
        Application.AddMessageFilter(this);   // route the mouse wheel over the pane to the themed scrollbar
        startCategory = Math.Clamp(startCategory, 0, Categories.Length - 1);
        if (startCategory > 0) { if (_sheet is not null) _sheet.SelectedIndex = startCategory; else _nav.SelectedIndex = startCategory; }   // raises Selected → ShowCategory
        else ShowCategory(0);

        if (Anim.MotionEnabled) Opacity = 0; // fade up from invisible in OnShown
    }

    /// <summary>Fade + rise into place when the window opens (Apple-modal entrance).</summary>
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _homeTop = Top;
        // MIX_TEST_RESTART (test harness, see MainForm): "Restart now" exactly as a page takes it - this window open,
        // modal, once its entrance has played - minus only the Yes/No prompt. Cleared first, so the relaunched copy
        // doesn't repeat it.
        if (Environment.GetEnvironmentVariable("MIX_TEST_RESTART") is "1" or "play" or "busy")
        {
            Environment.SetEnvironmentVariable("MIX_TEST_RESTART", null);
            var t = new System.Windows.Forms.Timer { Interval = 700 };
            t.Tick += (_, _) => { t.Dispose(); RestartSoon(); };
            t.Start();
        }
        if (!Anim.MotionEnabled) { Opacity = 1; return; }
        Top = _homeTop + 16;
        Anim.Run(190, v =>
        {
            if (IsDisposed) return;
            Opacity = v;
            Top = _homeTop + (int)Math.Round(16 * (1 - v));
        }, () => { if (!IsDisposed) { Opacity = 1; Top = _homeTop; } }, Easings.OutCubic);
    }

    /// <summary>Fade + settle down on dismiss before the window actually closes. Only a close the user asked for
    /// fades: the fade works by refusing the first close, and refusing an APP exit - "Restart now", Windows shutting
    /// down - refuses the whole exit. That is how "Restart now" used to leave the app open with nothing restarted.</summary>
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_closingAnim || !Anim.MotionEnabled || e.CloseReason is not (CloseReason.UserClosing or CloseReason.None)) { base.OnFormClosing(e); return; }
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
    public void RenderCategory(int index) { int i = Math.Clamp(index, 0, Categories.Length - 1); if (_sheet is not null) _sheet.SelectedIndex = i; else _nav.SelectedIndex = i; }

    private void Rebuild() => ShowCategory(_sheet?.SelectedIndex ?? _nav.SelectedIndex);

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
        _group = null; _hasDesc = false; _descLabel = null; _col = 0;
        if (!Theme.Classic) PageTitle(Loc.T(Categories[index]));   // Classic: the tab already names the page
        switch (index)
        {
            case 0: BuildAppearance(); break;
            case 1: BuildLibrary(); break;
            case 2: BuildVideo(); break;
            case 3: BuildPhotos(); break;
            case 4: BuildSafety(); break;
            case 5: BuildDiscord(); break;
            case 6: BuildDevice(); break;
            default: BuildAbout(); break;
        }
        if (Theme.Classic) FinishClassicPage();

        // Size the scrollable body to its content and reset it to the top; the themed scrollbar appears
        // only when this exceeds the (screen-clamped) window height.
        _paneBody.Top = 0;
        _paneBody.Height = Theme.Classic ? _y + 2 : _y + 18;   // Classic: a page that fits must not grow a scrollbar

        // Fixed, compact window height for EVERY category (no jiggle when switching, no ballooning toward
        // full-screen on the dense Library page) — content taller than the viewport scrolls via the themed
        // ThinScrollBar. Capped to the screen so it never opens taller than the desktop.
        int desired = Math.Min(PageHeight, Screen.FromControl(this).WorkingArea.Height - 72);
        if (!Theme.Classic && ClientSize.Height != desired) ClientSize = new Size(ClientSize.Width, desired);   // Classic: a property sheet keeps one size

        // Re-lay-out the themed scrollbar for the (rare) case content still exceeds the screen-capped window.
        LayoutPane();
        if (categoryChanged && !Theme.Classic) AnimatePaneIn();   // 1995 switched pages at once
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
        Group(Loc.T("Language"));
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

        Group(Loc.T("Look"));
        var look = new SegmentedControl { Options = new[] { Loc.T("Modern"), Loc.T("Windows 95") }, SelectedIndex = _s.ClassicSkin ? 1 : 0, Width = 260 };
        look.SelectedChanged += () =>
        {
            bool classic = look.SelectedIndex == 1;
            if (classic == _s.ClassicSkin) return;
            _s.ClassicSkin = classic; _s.Save();
            PromptRestart(Loc.T("The look changes after a restart. Restart Mixtape now?"));
        };
        Row(Loc.T("Look"), Loc.T("Modern is this app's own look. Windows 95 skins the whole window the way a program looked in 1995: grey panels with 3D edges, square corners, navy selection and the era's typeface. Takes effect after a restart."), look);

        if (_s.ClassicSkin)
        {
            // The accent and the background belong to the modern look; the 95 skin has exactly one palette.
            Row(Loc.T("Colours"), Loc.T("The Windows 95 look has one palette of its own \u2014 the accent colour and the background are used by the Modern look."), null);
            Row(Loc.T("256-colour pictures"), Loc.T("Show covers the way a 256-colour display of 1995 did: on the Windows halftone palette, dithered."),
                Toggle(_s.DitherCovers, v => { _s.DitherCovers = v; _s.Save(); Theme.DitherCovers = v; _applyChanged(); }));
        }
        else
        {
        var accent = new AccentPicker(_s.Accent);
        accent.AccentChosen += name => { _s.Accent = name; _s.Save(); _applyChanged(); Rebuild(); };
        Row(Loc.T("Accent colour"), Loc.T("Used for highlights, buttons and selection."), accent);

        var theme = new SegmentedControl { Options = Theme.ThemeVariants, SelectedIndex = Math.Max(0, Array.IndexOf(Theme.ThemeVariants, _s.ThemeVariant)), Width = 432 };
        theme.SelectedChanged += () => { _s.ThemeVariant = Theme.ThemeVariants[theme.SelectedIndex]; _s.Save(); _applyChanged(); BackColor = Theme.Bg; _pane.BackColor = Theme.Bg; _paneBody.BackColor = Theme.Bg; _nav.Invalidate(); Rebuild(); };
        Row(Loc.T("Background"), Loc.T("The window's colour palette."), theme);
        }

        Group(Loc.T("Song list"));
        var density = new SegmentedControl { Options = new[] { Loc.T("Comfortable"), Loc.T("Compact") }, SelectedIndex = _s.Compact ? 1 : 0, Width = 220 };
        density.SelectedChanged += () => { _s.Compact = density.SelectedIndex == 1; _s.Save(); _applyChanged(); };
        Row(Loc.T("Row density"), Loc.T("How tall the song rows are."), density);

        Row(Loc.T("Show artwork"), Loc.T("Show album/photo covers in lists."), Toggle(_s.ShowArtwork, v => { _s.ShowArtwork = v; _s.Save(); _applyChanged(); }));
        Group(Loc.T("Player"));
        Row(Loc.T("Player at the top"), Loc.T("The transport and the playing song live in the window's top strip instead of under the list. Takes effect after a restart."),
            Toggle(_s.BarOnTop, v => { _s.BarOnTop = v; _s.Save(); PromptRestart(Loc.T("The player moves after a restart. Restart Mixtape now?")); }));
    }

    /// <summary>Offer to relaunch so a change that needs one (language, look, where the player sits) takes effect.
    /// "Restart now" closes this copy and starts a fresh one - see <see cref="Program.Restart"/>.</summary>
    private void PromptLanguageRestart() => PromptRestart(Loc.T("The language changes after a restart. Restart Mixtape now?"));

    private void PromptRestart(string question)
    {
        if (MessageDialog.Show(this, question,
                Loc.T("Restart Mixtape?"), MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        RestartSoon();
    }

    /// <summary>Posted rather than called: the switch or radio button that asked is still inside its own click, and
    /// the restart closes every window - so it runs once that click is over, from the dialog's message loop.</summary>
    private void RestartSoon() => BeginInvoke(new Action(Program.Restart));

    private void BuildLibrary()
    {
        Group(Loc.T("Sorting"));
        string[] sorts = { "Playlist", "Song", "Artist", "Album", "Added", "Time" };   // stored values (English) — translate only the display
        var sort = new SegmentedControl { Options = Array.ConvertAll(sorts, Loc.T), SelectedIndex = Math.Max(0, Array.IndexOf(sorts, _s.DefaultSort)), Width = 396 };
        sort.SelectedChanged += () => { _s.DefaultSort = sorts[sort.SelectedIndex]; _s.Save(); _applyChanged(); };
        Row(Loc.T("Default sort"), Loc.T("Column a list is sorted by when it opens."), sort);
        Row(Loc.T("Sort descending"), Loc.T("Reverse the default sort order."), Toggle(_s.DefaultSortDescending, v => { _s.DefaultSortDescending = v; _s.Save(); _applyChanged(); }));
        Group(Loc.T("Libraries"));
        Row(Loc.T("Show Videos"), Loc.T("List the Videos library (video-capable iPods)."), Toggle(_s.ShowVideos, v => { _s.ShowVideos = v; _s.Save(); _applyChanged(); }));
        Row(Loc.T("Show Photos"), Loc.T("List the Photos library (colour-screen iPods)."), Toggle(_s.ShowPhotos, v => { _s.ShowPhotos = v; _s.Save(); _applyChanged(); }));
        Group(Loc.T("Song list columns"));
        Row(Loc.T("Artist column"), Loc.T("Show the Artist column in the song list."), Toggle(_s.ShowArtist, v => { _s.ShowArtist = v; _s.Save(); _applyChanged(); }));
        Row(Loc.T("Album column"), Loc.T("Show the Album column in the song list."), Toggle(_s.ShowAlbum, v => { _s.ShowAlbum = v; _s.Save(); _applyChanged(); }));
        Row(Loc.T("Star rating column"), Loc.T("Show your star ratings in the song list."), Toggle(_s.ShowRating, v => { _s.ShowRating = v; _s.Save(); _applyChanged(); }));
        Row(Loc.T("Play count column"), Loc.T("Show how many times each song has been played."), Toggle(_s.ShowPlays, v => { _s.ShowPlays = v; _s.Save(); _applyChanged(); }));
        Row(Loc.T("Date added column"), Loc.T("Show when each song was added to the iPod."), Toggle(_s.ShowDateAdded, v => { _s.ShowDateAdded = v; _s.Save(); _applyChanged(); }));
        Row(Loc.T("Time column"), Loc.T("Show the Time column in the song list."), Toggle(_s.ShowTime, v => { _s.ShowTime = v; _s.Save(); _applyChanged(); }));
        Group(Loc.T("Internet"));
        Row(Loc.T("Online lyrics"), Loc.T("When a song has no lyrics of its own, look up time-synced lyrics from the public LRCLIB database. Only the artist, title and length are sent, and only while the lyrics panel is open."),
            Toggle(_s.OnlineLyrics, v => { _s.OnlineLyrics = v; _s.Save(); _applyChanged(); }));
        Row(Loc.T("Missing covers from the internet"), Loc.T("When a file has no cover of its own, look one up by artist and album in Apple's public music search and the MusicBrainz Cover Art Archive. Only the artist and the album are sent, and a cover is used only when it clearly matches."),
            Toggle(_s.OnlineCovers, v => { _s.OnlineCovers = v; _s.Save(); _applyChanged(); }));
        Row(Loc.T("Write covers into local files"), Loc.T("Save a downloaded cover into the music file itself on this PC, so other players see it too. Files on the iPod are never changed."),
            Toggle(_s.EmbedDownloadedCovers, v => { _s.EmbedDownloadedCovers = v; _s.Save(); _applyChanged(); }));
    }

    private void BuildVideo()
    {
        Group(Loc.T("Conversion"));
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
        Group(Loc.T("Photos"));
        Row(Loc.T("Store full-screen image"), Loc.T("Also write the 320×240 image so photos look sharp on the iPod (uses more space)."),
            Toggle(_s.PhotoStoreFullResolution, v => { _s.PhotoStoreFullResolution = v; _s.Save(); }));
    }

    /// <summary>Discord Rich Presence: show what Mixtape is playing on the user's Discord profile. Off by
    /// default and inert without an Application ID — nothing is published until BOTH are set.</summary>
    private void BuildDiscord()
    {
        Group(Loc.T("Rich Presence"));
        Row(Loc.T("Show on Discord"), Loc.T("Put the song you're playing on your Discord profile, with a live progress bar. Talks only to the Discord app on this PC."),
            Toggle(_s.DiscordRichPresence, v => { _s.DiscordRichPresence = v; _s.Save(); _applyChanged(); }));

        // Freeform string: Settings has no text-entry row type, so mirror the ffmpeg row (a button that
        // opens a prompt, then Rebuild() so the subtitle shows the new value).
        var idBtn = new ThemedButton { Text = Loc.T(_s.DiscordAppId.Length > 0 ? "Change…" : "Set…"), Pill = true, Width = 96, Height = 30 };
        idBtn.Click += (_, _) =>
        {
            string? v = PromptDialog.Show(this, Loc.T("Discord Application ID"), Loc.T("Paste the Application ID from the Discord Developer Portal:"), _s.DiscordAppId);
            if (v is null) return;
            _s.DiscordAppId = new string(v.Where(char.IsDigit).ToArray());   // the portal shows a numeric id
            _s.Save(); _applyChanged(); Rebuild();
        };
        Row(Loc.T("Application ID"),
            _s.DiscordAppId.Length > 0 ? _s.DiscordAppId
                                       : Loc.T("Create a free app at discord.com/developers, then paste its Application ID here. It isn't a secret — it only names the app Discord shows."),
            idBtn);

        Row(Loc.T("Album cover"), Loc.T("Show the real cover instead of the Mixtape logo. Looks it up by artist and album on Apple's public music search — the only part of this feature that uses the internet. A cover is only used when it clearly matches."),
            Toggle(_s.DiscordCoverArt, v => { _s.DiscordCoverArt = v; _s.Save(); _applyChanged(); }));

        Row(Loc.T("What it shows"), Loc.T("The song, the artist and how far along it is — only while Mixtape itself is playing, never what the iPod plays on its own."), null);
    }

    private void BuildSafety()
    {
        Group(Loc.T("Writing to the iPod"));
        Row(Loc.T("Confirm before writing"), Loc.T("Show a reminder before the first change each session."), Toggle(_s.ConfirmWrites, v => { _s.ConfirmWrites = v; _s.Save(); }));
        Row(Loc.T("Auto device-ID recovery"), Loc.T("When a hash58 iPod with no stored ID is plugged in, offer to read its hardware ID automatically (a safe, read-only query) so music can be written — no hunting for the “Read device ID” button."), Toggle(_s.AutoGuidRecovery, v => { _s.AutoGuidRecovery = v; _s.Save(); }));
        if (_device is not null)
        {
            Group(Loc.T("Backup"));
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

    /// <summary>The build's own version, read from the assembly — a literal here went stale for a whole
    /// release once, and About is the one place a wrong number is visible.</summary>
    private static string AppVersion =>
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "";

    private void BuildAbout()
    {
        if (Theme.Classic) { BuildClassicAbout(); return; }
        Row("Mixtape", Loc.T("Version {0}", AppVersion), null);
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
        if (Theme.Classic) { ClassicRow(title, subtitle, control); return; }
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
        int inset = control is IEdgeInset ei ? ei.RightInset : 0;   // mirrors CardPanel.AddRow
        int ctrlLeft = control is not null ? CardW - rightPad - control.Width + inset : CardW - rightPad;
        int labelW = Math.Max(80, ctrlLeft - gap - labelLeft);
        using var f = Theme.UiFont(9f);
        int descH = TextRenderer.MeasureText(subtitle, f, new Size(labelW, 0), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height;
        return Math.Max(62, 28 + descH + 16); // title band + wrapped subtitle + bottom padding
    }

    /// <summary>A single grouped card of read-only "label … value" rows with internal hairline dividers.</summary>
    private void InfoGroup(List<(string Label, string Value)> rows)
    {
        if (Theme.Classic)
        {
            Group(Loc.T("Details"));
            foreach (var (l, v) in rows)
            {
                Host.Controls.Add(ClassicText(l + ":", 12, Y, 150, 16));
                Host.Controls.Add(ClassicText(v, 166, Y, HostW - 178, 16));
                Y += 18;
            }
            EndGroup();
            return;
        }
        var card = new CardPanel(CardW) { Left = ContentLeft, Top = _y };
        foreach (var (l, v) in rows) card.AddInfoRow(l, v);
        card.Finish();
        _paneBody.Controls.Add(card);
        _y += card.Height + 10;
    }

    // ---- the Classic property sheet's layout ----

    private Control Host => (Control?)_group ?? _paneBody;
    private int HostW => _group?.Width ?? ClassicBodyW;
    private int Y { get => _group is null ? _y : _gy; set { if (_group is null) _y = value; else _gy = value; } }

    /// <summary>Open a captioned group box (Classic only; the modern page has no groups). Rows go into it until the
    /// next group or the end of the page.</summary>
    private void Group(string caption)
    {
        if (!Theme.Classic) return;
        EndGroup();
        _group = new ClassicGroupBox { Text = caption, Left = 0, Top = _y, Width = ClassicBodyW };
        _paneBody.Controls.Add(_group);
        _gy = 20; _col = 0;
    }

    private void EndGroup()
    {
        if (_group is null) return;
        if (_col == 1) { _gy += 20; _col = 0; }
        _group.Height = _gy + 6;
        _y = _group.Bottom + 8;
        _group = null;
    }

    private static GlassLabel ClassicText(string text, int x, int y, int w, int h, bool wrap = false) => new()
    {
        Text = text, Left = x, Top = y, Width = Math.Max(10, w), Height = h, AutoSize = false,
        Font = Theme.UiFont(Theme.SzBody), ForeColor = Theme.TextCol, BackColor = Theme.Face,
        TextAlign = wrap ? ContentAlignment.TopLeft : ContentAlignment.MiddleLeft, AutoEllipsis = !wrap,
    };

    /// <summary>Show <paramref name="text"/> in the page's Description box while the pointer is on the control.</summary>
    private void Describe(Control c, string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        _hasDesc = true;
        c.MouseEnter += (_, _) => { if (_descLabel is not null) _descLabel.Text = text; };
    }

    private static int TextHeight(string text, int width)
    {
        using var f = Theme.UiFont(Theme.SzBody);
        return TextRenderer.MeasureText(text, f, new Size(Math.Max(10, width), 0), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height;
    }

    /// <summary>
    /// One setting on a 1995 property page. A switch is a check box with its label on the right (two to a line
    /// while they run together); a choice is its caption over a row of radio buttons; a button stands at the right
    /// of its label with any status text under the label; a plain note is a wrapped paragraph. What a setting DOES
    /// goes into the Description box, not onto the page - the way the era's dialogs kept a page readable.
    /// </summary>
    private void ClassicRow(string title, string? subtitle, Control? control)
    {
        int x = 12, w = HostW - 24;
        switch (control)
        {
            case ToggleSwitch t:
            {
                int colW = w / 2;
                t.ClassicLabel = title;
                var sz = t.ClassicLabelSize();
                bool right = _col == 1 && sz.Width <= colW;
                if (_col == 1 && !right) { Y += 20; _col = 0; }
                t.Anchor = AnchorStyles.Top | AnchorStyles.Left;
                t.Location = new Point(right ? x + colW : x, Y);
                t.Size = new Size(Math.Min(sz.Width, right ? colW : w), 18);
                Host.Controls.Add(t);
                Describe(t, subtitle);
                if (right || sz.Width > colW) { Y += 20; _col = 0; } else _col = 1;
                return;
            }
            case SegmentedControl s:
            {
                if (_col == 1) { Y += 20; _col = 0; }
                bool captioned = _group is not null && string.Equals(_group.Text, title, StringComparison.CurrentCulture);
                if (!captioned) { var cap = ClassicText(title + ":", x, Y, w, 16); Host.Controls.Add(cap); Describe(cap, subtitle); Y += 18; }
                int avail = w - 8;
                s.ClassicWrap = true;
                var (_, rows) = s.ClassicGrid(avail);
                s.Anchor = AnchorStyles.Top | AnchorStyles.Left;
                s.Location = new Point(x + 4, Y);
                s.Size = new Size(Math.Min(avail, s.ClassicRadioWidth()), rows * 18);
                if (rows > 1) s.Width = avail;
                Host.Controls.Add(s);
                Describe(s, subtitle);
                Y += rows * 18 + 6;
                return;
            }
            case ThemedButton b:
            {
                if (_col == 1) { Y += 20; _col = 0; }
                b.Anchor = AnchorStyles.Top | AnchorStyles.Left;
                b.Size = new Size(Math.Max(75, b.NeededWidth), 23);
                b.Location = new Point(HostW - 12 - b.Width, Y);
                int tw = w - b.Width - 12;
                Host.Controls.Add(ClassicText(title, x, Y, tw, 18));
                int th = 0;
                if (!string.IsNullOrWhiteSpace(subtitle))
                {
                    th = TextHeight(subtitle, tw);
                    var st = ClassicText(subtitle, x, Y + 18, tw, th, wrap: true);
                    st.ForeColor = Theme.Subtle;
                    Host.Controls.Add(st);
                }
                Host.Controls.Add(b);
                Y += Math.Max(28, 18 + th + 6);
                return;
            }
            case null:
            {
                if (_col == 1) { Y += 20; _col = 0; }
                bool captioned = _group is not null && string.Equals(_group.Text, title, StringComparison.CurrentCulture);
                if (!captioned) { Host.Controls.Add(ClassicText(title, x, Y, w, 16)); Y += 18; }
                if (!string.IsNullOrWhiteSpace(subtitle))
                {
                    int th = TextHeight(subtitle, w);
                    var st = ClassicText(subtitle, x, Y, w, th, wrap: true);
                    st.ForeColor = captioned ? Theme.TextCol : Theme.Subtle;
                    Host.Controls.Add(st);
                    Y += th + 6;
                }
                return;
            }
            default:
            {
                if (_col == 1) { Y += 20; _col = 0; }
                Host.Controls.Add(ClassicText(title, x, Y, w - control.Width - 12, 18));
                control.Location = new Point(HostW - 12 - control.Width, Y);
                Host.Controls.Add(control);
                Describe(control, subtitle);
                Y += Math.Max(24, control.Height + 6);
                return;
            }
        }
    }

    /// <summary>Close the page: the last group, then the Description box, pinned to the bottom of the page.</summary>
    private void FinishClassicPage()
    {
        EndGroup();
        if (!_hasDesc) return;
        const int h = 66;
        int top = Math.Max(_y, ClassicBodyH - h - 2);
        var box = new ClassicGroupBox { Text = Loc.T("Description"), Left = 0, Top = top, Width = ClassicBodyW, Height = h };
        _descLabel = ClassicText(Loc.T("Point at a setting to see what it does."), 12, 20, ClassicBodyW - 24, h - 26, wrap: true);
        box.Controls.Add(_descLabel);
        _paneBody.Controls.Add(box);
        _y = top + h;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX { public uint dwLength, dwMemoryLoad; public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual; }
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);

    /// <summary>
    /// The About page as a 1995 About box: the program's icon, name and version, then who the copy is licensed
    /// to and what the machine has free - the two lines every Windows 95 About box ended with.
    /// </summary>
    private void BuildClassicAbout()
    {
        Group(Loc.T("About Mixtape"));
        var icon = new PictureBox { Left = 14, Top = Y + 2, Size = new Size(32, 32), SizeMode = PictureBoxSizeMode.StretchImage, BackColor = Theme.Face };
        try { if (Environment.ProcessPath is string pth) icon.Image = System.Drawing.Icon.ExtractAssociatedIcon(pth)?.ToBitmap(); } catch { }
        Host.Controls.Add(icon);
        int tx = 58, tw = HostW - tx - 12;
        var name = ClassicText("Mixtape", tx, Y, tw, 16);
        name.Font = Theme.UiFont(Theme.SzBody, FontStyle.Bold);
        Host.Controls.Add(name);
        Host.Controls.Add(ClassicText(Loc.T("Version {0}", AppVersion), tx, Y + 16, tw, 16));
        Y += 40;
        string blurb = Loc.T("A friendly manager for classic iPods") + ". " + Loc.T("Copy music, videos and photos; make playlists and mixtapes; choose covers — all written natively, no iTunes.");
        int bh = TextHeight(blurb, tw);
        Host.Controls.Add(ClassicText(blurb, tx, Y, tw, bh, wrap: true));
        Y += bh + 8;
        EndGroup();

        Group(Loc.T("This copy"));
        Host.Controls.Add(ClassicText(Loc.T("This product is licensed to:"), 12, Y, HostW - 24, 16)); Y += 16;
        Host.Controls.Add(ClassicText(Environment.UserName, 28, Y, HostW - 40, 16)); Y += 22;
        var m = new MEMORYSTATUSEX { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (GlobalMemoryStatusEx(ref m))
        {
            Host.Controls.Add(ClassicText(Loc.T("Physical memory available to Windows:"), 12, Y, 250, 16));
            Host.Controls.Add(ClassicText(Loc.T("{0} KB", (m.ullAvailPhys / 1024).ToString("N0")), 266, Y, HostW - 278, 16)); Y += 18;
            Host.Controls.Add(ClassicText(Loc.T("System resources:"), 12, Y, 250, 16));
            Host.Controls.Add(ClassicText(Loc.T("{0}% free", 100 - m.dwMemoryLoad), 266, Y, HostW - 278, 16)); Y += 18;
        }
        EndGroup();
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
        try { var bg = Theme.Bg; int caption = (bg.B << 16) | (bg.G << 8) | bg.R;   /* the caption in the theme's own surface colour (was a baked Graphite grey) */ DwmSetWindowAttribute(Handle, 35, ref caption, sizeof(int)); } catch { }
        // Dark scrollbar instead of the bright native one, for the rare case the pane still scrolls
        // (a screen too short to fit a dense page).
        try { SetWindowTheme(_pane.Handle, "DarkMode_Explorer", null); } catch { }
    }
}
