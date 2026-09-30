using System.Drawing.Drawing2D;

namespace iPodCommander;

/// <summary>
/// "Get Info"-style editor for one track's tags + star rating. Returns a <see cref="TrackEdit"/>
/// containing ONLY the fields the user actually changed (so untouched mhods are preserved verbatim
/// when written back). Themed dark dialog.
/// </summary>
internal sealed class TrackInfoDialog : CardDialog
{
    private readonly Track _t;
    private readonly IReadOnlyList<Track> _tracks;
    private readonly bool _multi;
    private readonly HashSet<Control> _touched = new();   // fields the user actually edited (multi mode applies only these)
    private bool _ratingTouched;
    private readonly TextBox _title, _artist, _album, _albumArtist, _genre, _composer, _comment, _year, _track, _trackTotal, _disc, _discTotal;
    private readonly StarRating _rating;

    /// <summary>The edit to apply (only changed/edited fields), valid after the dialog returns OK.</summary>
    public TrackEdit Edit { get; private set; } = new();

    public TrackInfoDialog(Track t) : this(new[] { t }) { }

    /// <summary>Edit one OR several songs. With several, Title and Track # (per-song) are disabled, the shared
    /// fields pre-fill when every song agrees (else blank), and only the fields the user edits are applied to ALL.</summary>
    public TrackInfoDialog(IReadOnlyList<Track> tracks)
    {
        _tracks = tracks;
        _t = tracks[0];
        _multi = tracks.Count > 1;
        Text = Theme.Classic   // Classic: a 95 Properties sheet, named the way 95 named one
            ? (_multi ? Loc.T("{0} songs Properties", tracks.Count) : Loc.T("{0} Properties", _t.DisplayTitle))
            : _multi ? Loc.T("Edit {0} songs", tracks.Count) : Loc.T("Song info");
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
        ClientSize = new Size(Theme.Classic ? 460 : 440, _multi ? 524 : 624);
        BackColor = Theme.Bg;
        ForeColor = Theme.TextCol;
        Font = Theme.UiFont(9.5f);

        // The shared value across the selection, or "" when the songs disagree (so a blank field = "leave each as-is").
        string Common(Func<Track, string> sel)
        {
            string first = sel(_t);
            return _tracks.All(x => sel(x) == first) ? first : "";
        }

        // Classic: Windows 95's Properties - a tabbed sheet with General (what the song is, read-only) in front and
        // Details (the tags you can change: the same fields as the modern card) behind it.
        Control.ControlCollection target = Controls;
        ClassicPropertySheet? sheet = null;
        Panel? general = null, details = null;
        if (Theme.Classic)
        {
            sheet = new ClassicPropertySheet(new[] { Loc.T("General"), Loc.T("Details") });
            general = new Panel { BackColor = Theme.Face };
            details = new Panel { BackColor = Theme.Face, Visible = false };
            sheet.Controls.Add(general);
            sheet.Controls.Add(details);
            sheet.Selected += i => { general.Visible = i == 0; details.Visible = i == 1; };
            target = details.Controls;
        }
        int y = Theme.Classic ? 12 : 18;
        if (_multi)
        {
            target.Add(new Label { Text = Loc.T("Editing {0} songs. Type in a field to set it on all of them; leave a field blank to keep each song's own value.", tracks.Count),
                ForeColor = Theme.Subtle, AutoSize = false, Location = new Point(16, y), Size = new Size(410, 36) });
            y += 42;
        }

        TextBox Row(string label, string value, bool editable = true, int width = 300)
        {
            target.Add(new Label { Text = label, ForeColor = Theme.Subtle, AutoSize = false, TextAlign = ContentAlignment.MiddleRight, Location = new Point(16, y), Size = new Size(96, 26) });
            var tb = new TextBox { Text = value, Location = new Point(122, y), Width = width, BackColor = editable ? Theme.RowBg : Theme.PanelBg, ForeColor = editable ? Theme.TextCol : Theme.Faint, BorderStyle = BorderStyle.FixedSingle, ReadOnly = !editable, TabStop = editable };
            if (editable) tb.TextChanged += (_, _) => _touched.Add(tb);
            target.Add(ThemedField.Wrap(tb));
            y += 36;
            return tb;
        }

        _title = Row(Loc.T("Title"), _multi ? Loc.T("(varies — not changed)") : (_t.Title ?? ""), !_multi);
        _artist = Row(Loc.T("Artist"), Common(x => x.Artist ?? ""));
        _album = Row(Loc.T("Album"), Common(x => x.Album ?? ""));
        _albumArtist = Row(Loc.T("Album artist"), Common(x => x.AlbumArtist ?? ""));
        _genre = Row(Loc.T("Genre"), Common(x => x.Genre ?? ""));
        _composer = Row(Loc.T("Composer"), Common(x => x.Composer ?? ""));
        _comment = Row(Loc.T("Comment"), Common(x => x.Comment ?? ""));

        // A small numeric box + an optional "/ total" companion, both styled like the main rows.
        TextBox Num(int x, int width, string value, bool editable)
        {
            var tb = new TextBox { Text = value, Location = new Point(x, y), Width = width, BackColor = editable ? Theme.RowBg : Theme.PanelBg, ForeColor = editable ? Theme.TextCol : Theme.Faint, BorderStyle = BorderStyle.FixedSingle, ReadOnly = !editable, TabStop = editable };
            if (editable) tb.TextChanged += (_, _) => _touched.Add(tb);
            target.Add(ThemedField.Wrap(tb));
            return tb;
        }
        void Slash(int x) => target.Add(new Label { Text = "/", ForeColor = Theme.Faint, AutoSize = false, TextAlign = ContentAlignment.MiddleCenter, Location = new Point(x, y), Size = new Size(14, 26) });

        // Year + "Track # / total" share a row. Track # is per-song, so it's disabled when editing
        // several; the album total is shared, so it stays editable either way.
        target.Add(new Label { Text = Loc.T("Year"), ForeColor = Theme.Subtle, AutoSize = false, TextAlign = ContentAlignment.MiddleRight, Location = new Point(16, y), Size = new Size(96, 26) });
        _year = Num(122, 70, Common(x => x.Year > 0 ? x.Year.ToString() : ""), true);
        target.Add(new Label { Text = Loc.T("Track #"), ForeColor = Theme.Subtle, AutoSize = false, TextAlign = ContentAlignment.MiddleRight, Location = new Point(206, y), Size = new Size(66, 26) });
        _track = Num(282, 70, _multi ? "" : (_t.TrackNumber > 0 ? _t.TrackNumber.ToString() : ""), !_multi);
        Slash(352);
        _trackTotal = Num(368, 56, Common(x => x.TotalTracks > 0 ? x.TotalTracks.ToString() : ""), true);
        y += 38;

        // "Disc # / total" — disc # is per-song (disabled in multi), the disc count is album-shared.
        target.Add(new Label { Text = Loc.T("Disc #"), ForeColor = Theme.Subtle, AutoSize = false, TextAlign = ContentAlignment.MiddleRight, Location = new Point(16, y), Size = new Size(96, 26) });
        _disc = Num(122, 70, _multi ? "" : (_t.DiscNumber > 0 ? _t.DiscNumber.ToString() : ""), !_multi);
        Slash(194);
        _discTotal = Num(210, 56, Common(x => x.TotalDiscs > 0 ? x.TotalDiscs.ToString() : ""), true);
        y += 38;

        // Rating stars.
        target.Add(new Label { Text = Loc.T("Rating"), ForeColor = Theme.Subtle, AutoSize = false, TextAlign = ContentAlignment.MiddleRight, Location = new Point(16, y), Size = new Size(96, 26) });
        int commonStars = _multi && !_tracks.All(x => Math.Min(5, x.Rating / 20) == Math.Min(5, _t.Rating / 20)) ? 0 : Math.Min(5, _t.Rating / 20);
        _rating = new StarRating { Location = new Point(120, y - 2), Size = new Size(160, 30), Value = commonStars };
        _rating.UserChanged += () => _ratingTouched = true;
        target.Add(_rating);
        y += 44;

        // Read-only stats (single song only) — the info the grid can't show all at once. (Classic: the General page.)
        if (!_multi && !Theme.Classic)
        {
            Controls.Add(new Panel { BackColor = Theme.PanelBg, Location = new Point(16, y), Size = new Size(408, 1) });
            y += 10;
            void Stat(string label, string value)
            {
                Controls.Add(new Label { Text = label, ForeColor = Theme.Subtle, AutoSize = false, TextAlign = ContentAlignment.MiddleRight, Location = new Point(16, y), Size = new Size(96, 22) });
                Controls.Add(new Label { Text = value, ForeColor = Theme.TextCol, AutoSize = false, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, Location = new Point(122, y), Size = new Size(302, 22) });
                y += 26;
            }
            Stat(Loc.T("Plays"), _t.PlayCount.ToString());
            Stat(Loc.T("Last played"), DateStr(_t.LastPlayed));
            Stat(Loc.T("Added"), DateStr(_t.DateAdded));
            Stat(Loc.T("Format"), FormatStr(_t));
            Stat(Loc.T("Path"), _t.LocalPath ?? (_t.Location is { Length: > 0 } loc ? loc.TrimStart(':').Replace(':', '\\') : "—"));
            y += 8;
        }

        if (sheet is not null)
        {
            // the sheet: its tabs, then a page as tall as the Details fields need; OK and Cancel under it
            sheet.SetBounds(8, 10, ClientSize.Width - 16, ClassicPropertySheet.TabH + y + 16);
            var pr = sheet.PageRect;
            general!.Bounds = details!.Bounds = new Rectangle(pr.X + 3, pr.Y + 3, pr.Width - 6, pr.Height - 6);
            BuildGeneral(general);
            Controls.Add(sheet);
            _sheet = sheet;
            y = sheet.Bottom + 10;
        }
        var save = new ThemedButton { Text = Theme.Classic ? Loc.T("OK") : Loc.T("Save"), Primary = true, Pill = true, Width = Theme.Classic ? 75 : 100, Height = Theme.Classic ? 23 : 32, DialogResult = DialogResult.OK };
        var cancel = new ThemedButton { Text = Loc.T("Cancel"), Pill = true, Width = Theme.Classic ? 75 : 96, Height = Theme.Classic ? 23 : 32, DialogResult = DialogResult.Cancel };
        if (Theme.Classic) { save.Location = new Point(ClientSize.Width - 8 - 75 - 6 - 75, y); cancel.Location = new Point(ClientSize.Width - 8 - 75, y); }   // 95: OK, then Cancel
        else { save.Location = new Point(ClientSize.Width - 116, y); cancel.Location = new Point(ClientSize.Width - 116 - 106, y); }
        save.Click += (_, _) => BuildEdit();
        Controls.Add(save);
        Controls.Add(cancel);
        AcceptButton = save;
        CancelButton = cancel;
        // Height from the content, not a guess: the fixed 524/624 left a 60 px hole under the buttons
        // while the top margin was 18.
        ClientSize = new Size(ClientSize.Width, y + save.Height + (Theme.Classic ? 10 : 18));
        AdoptCard();
        if (Theme.Classic) ActiveControl = save;   // the General page is in front; its fields are read-only
        else if (_multi) ActiveControl = _artist;   // start on the first editable field, not the disabled Title
    }

    private Bitmap? _classicIcon;   // the General page's 32 px icon (the dialog owns it)
    private ClassicPropertySheet? _sheet;

    /// <summary>Harness only (MIX_PROP_TAB): open the Classic sheet on another tab.</summary>
    internal void PreviewTab(int i) { if (_sheet is not null) _sheet.SelectedIndex = i; }

    /// <summary>
    /// Classic: the General page of a 95 file's Properties - what the song is (its type, where it sits, how big and how
    /// long, its format), its history on the iPod, and for a file on this PC the attributes the file system gives it,
    /// as greyed check boxes. Read-only; the tags you can change are on Details.
    /// </summary>
    private void BuildGeneral(Panel page)
    {
        int w = page.Width, y = 12;
        var ci = System.Globalization.CultureInfo.CurrentCulture;
        _classicIcon = Theme.ClassicHeaderIcon(_multi ? ClassicIcons.Id.Album : ClassicIcons.Id.Songs);
        page.Controls.Add(new PictureBox { Image = _classicIcon, Location = new Point(14, y), Size = new Size(32, 32) });
        page.Controls.Add(new Label { Text = _multi ? Loc.T("{0} songs", _tracks.Count) : _t.DisplayTitle, ForeColor = Theme.TextCol, AutoSize = false, AutoEllipsis = true,
            UseMnemonic = false, Location = new Point(76, y + 9), Size = new Size(w - 88, 18) });
        y += 46;
        void Rule()   // the etched line between a 95 page's groups
        {
            page.Controls.Add(new Panel { BackColor = Theme.FaceShadow, Location = new Point(10, y), Size = new Size(w - 20, 1) });
            page.Controls.Add(new Panel { BackColor = Theme.FaceHi, Location = new Point(10, y + 1), Size = new Size(w - 20, 1) });
            y += 12;
        }
        void Info(string label, string value)
        {
            page.Controls.Add(new Label { Text = label, ForeColor = Theme.TextCol, AutoSize = false, UseMnemonic = false, Location = new Point(14, y), Size = new Size(100, 18) });
            page.Controls.Add(new Label { Text = value, ForeColor = Theme.TextCol, AutoSize = false, AutoEllipsis = true, UseMnemonic = false, Location = new Point(118, y), Size = new Size(w - 130, 18) });
            y += 22;
        }
        Rule();
        if (_multi)
        {
            Info(Loc.T("Songs:"), _tracks.Count.ToString("N0", ci));
            Info(Loc.T("Size:"), SizeStr(_tracks.Sum(x => (long)x.FileSize)));
            Info(Loc.T("Length:"), LengthStr(TimeSpan.FromMilliseconds(_tracks.Sum(x => (double)x.LengthMs))));
            return;
        }
        string? file = _t.LocalPath is { Length: > 0 } lp ? lp : _t.Location is { Length: > 0 } loc ? loc.TrimStart(':').Replace(':', '\\') : null;
        Info(Loc.T("Type:"), _t.FileTypeDescription is { Length: > 0 } d ? d
            : Path.GetExtension(file ?? "") is { Length: > 1 } ext ? Loc.T("{0} file", ext.TrimStart('.').ToUpperInvariant()) : "—");
        Info(Loc.T("Location:"), file is null ? "—" : Path.GetDirectoryName(file) ?? "—");
        Info(Loc.T("Size:"), _t.FileSize > 0 ? SizeStr(_t.FileSize) : "—");
        Info(Loc.T("Length:"), _t.LengthMs > 0 ? LengthStr(_t.Duration) : "—");
        Info(Loc.T("Format:"), FormatStr(_t));
        Rule();
        Info(Loc.T("Added:"), DateStr(_t.DateAdded));
        Info(Loc.T("Last played:"), DateStr(_t.LastPlayed));
        Info(Loc.T("Plays:"), _t.PlayCount.ToString("N0", ci));
        if (_t.LocalPath is not { Length: > 0 } path || !File.Exists(path)) return;
        FileAttributes attrs;
        try { attrs = File.GetAttributes(path); } catch { return; }
        Rule();
        page.Controls.Add(new Label { Text = Loc.T("Attributes:"), ForeColor = Theme.TextCol, AutoSize = false, UseMnemonic = false, Location = new Point(14, y), Size = new Size(100, 18) });
        var boxes = new Panel { Location = new Point(118, y), Size = new Size(w - 130, 18), BackColor = Theme.Face };
        boxes.Paint += (_, e) =>
        {
            int x = 0;
            foreach (var (text, on) in new[] { (Loc.T("Read-only"), attrs.HasFlag(FileAttributes.ReadOnly)), (Loc.T("Hidden"), attrs.HasFlag(FileAttributes.Hidden)), (Loc.T("Archive"), attrs.HasFlag(FileAttributes.Archive)) })
            {
                Theme.ClassicCheckBox(e.Graphics, new Rectangle(x, 2, 13, 13), on, enabled: false);
                int tw = TextRenderer.MeasureText(e.Graphics, text, Font).Width;
                var tr = new Rectangle(x + 17, 0, tw + 4, 18);
                const TextFormatFlags tf = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix;
                TextRenderer.DrawText(e.Graphics, text, Font, new Rectangle(tr.X + 1, tr.Y + 1, tr.Width, tr.Height), Theme.FaceHi, tf);   // a greyed 95 label: embossed
                TextRenderer.DrawText(e.Graphics, text, Font, tr, Theme.FaceShadow, tf);
                x += 17 + tw + 14;
            }
        };
        page.Controls.Add(boxes);
    }

    /// <summary>"4,1 MB (4 302 341 bytes)" - the short size and the exact one, as 95's General page gave both.</summary>
    private static string SizeStr(long bytes) =>
        CapacityBar.Human(bytes) + " (" + Loc.T("{0} bytes", bytes.ToString("N0", System.Globalization.CultureInfo.CurrentCulture)) + ")";

    private static string LengthStr(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";

    protected override void Dispose(bool disposing)
    {
        if (disposing) _classicIcon?.Dispose();
        base.Dispose(disposing);
    }

    /// <summary>Conversational date (mirrors the grid's Added column); "—" when unset.</summary>
    private static string DateStr(DateTime? d)
    {
        if (d is not { } dt || dt.Year <= 1970) return "—";
        var today = DateTime.Today;
        if (dt.Date == today) return Loc.T("Today");
        if (dt.Date == today.AddDays(-1)) return Loc.T("Yesterday");
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        return dt.Year == today.Year ? dt.ToString("MMM d", ci) : dt.ToString("MMM d, yyyy", ci);
    }

    /// <summary>"MPEG audio file · 320 kbps · 44.1 kHz · 8.4 MB" — whichever parts the track has.
    /// InvariantCulture decimals, matching the app's English-invariant number style.</summary>
    private static string FormatStr(Track t) { string s = TrackFormat.Line(t, withSize: true); return s.Length > 0 ? s : "—"; }

    private void BuildEdit()
    {
        var e = new TrackEdit();
        if (_multi)
        {
            // Editing several songs: apply ONLY the fields the user actually edited, to every one. Title and
            // Track # are per-song and disabled, so they're never in the edit.
            if (_touched.Contains(_artist)) e.Artist = _artist.Text;
            if (_touched.Contains(_album)) e.Album = _album.Text;
            if (_touched.Contains(_albumArtist)) e.AlbumArtist = _albumArtist.Text;
            if (_touched.Contains(_genre)) e.Genre = _genre.Text;
            if (_touched.Contains(_composer)) e.Composer = _composer.Text;
            if (_touched.Contains(_comment)) e.Comment = _comment.Text;
            if (_touched.Contains(_year)) e.Year = uint.TryParse(_year.Text.Trim(), out var yv) ? yv : 0;
            if (_touched.Contains(_trackTotal)) e.TotalTracks = uint.TryParse(_trackTotal.Text.Trim(), out var ttv) ? ttv : 0;
            if (_touched.Contains(_discTotal)) e.TotalDiscs = uint.TryParse(_discTotal.Text.Trim(), out var tdv) ? tdv : 0;
            if (_ratingTouched) e.Rating = (byte)(Math.Clamp(_rating.Value, 0, 5) * 20);
            Edit = e;
            return;
        }

        // Single song: include a field only when it actually changed, so unchanged tags keep their exact bytes.
        if (_title.Text != (_t.Title ?? "")) e.Title = _title.Text;
        if (_artist.Text != (_t.Artist ?? "")) e.Artist = _artist.Text;
        if (_album.Text != (_t.Album ?? "")) e.Album = _album.Text;
        if (_albumArtist.Text != (_t.AlbumArtist ?? "")) e.AlbumArtist = _albumArtist.Text;
        if (_genre.Text != (_t.Genre ?? "")) e.Genre = _genre.Text;
        if (_composer.Text != (_t.Composer ?? "")) e.Composer = _composer.Text;
        if (_comment.Text != (_t.Comment ?? "")) e.Comment = _comment.Text;

        uint newY = uint.TryParse(_year.Text.Trim(), out var y2) ? y2 : 0;
        if (newY != _t.Year) e.Year = newY;
        uint newTrack = uint.TryParse(_track.Text.Trim(), out var tv) ? tv : 0;
        if (newTrack != _t.TrackNumber) e.TrackNumber = newTrack;
        uint newTT = uint.TryParse(_trackTotal.Text.Trim(), out var tt2) ? tt2 : 0;
        if (newTT != _t.TotalTracks) e.TotalTracks = newTT;
        uint newDisc = uint.TryParse(_disc.Text.Trim(), out var dv) ? dv : 0;
        if (newDisc != _t.DiscNumber) e.DiscNumber = newDisc;
        uint newTD = uint.TryParse(_discTotal.Text.Trim(), out var td2) ? td2 : 0;
        if (newTD != _t.TotalDiscs) e.TotalDiscs = newTD;

        // Only write the rating if the user actually changed the displayed star count. (The control shows
        // whole stars; a half-star song reads as N stars but its byte is N*20+10 — comparing the byte would
        // round a 2.5★ song down to 2★ on any unrelated edit. Gate on the star count instead.)
        int origStars = Math.Min(5, _t.Rating / 20);
        if (_rating.Value != origStars) e.Rating = (byte)(Math.Clamp(_rating.Value, 0, 5) * 20);

        Edit = e;
    }

    /// <summary>True when at least one field differs from the track's current values.</summary>
    public bool HasChanges =>
        Edit.Title is not null || Edit.Artist is not null || Edit.Album is not null ||
        Edit.AlbumArtist is not null || Edit.Genre is not null || Edit.Composer is not null ||
        Edit.Comment is not null || Edit.Year is not null || Edit.TrackNumber is not null ||
        Edit.TotalTracks is not null || Edit.DiscNumber is not null || Edit.TotalDiscs is not null ||
        Edit.Rating is not null;

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try { int on = 1; DwmSetWindowAttribute(Handle, 20, ref on, sizeof(int)); } catch { }
        try { var bg = Theme.Bg; int caption = (bg.B << 16) | (bg.G << 8) | bg.R;   /* the caption in the theme's own surface colour (was a baked Graphite grey) */ DwmSetWindowAttribute(Handle, 35, ref caption, sizeof(int)); } catch { }
    }

    /// <summary>A 0–5 clickable star rating, owner-painted in the theme accent.</summary>
    private sealed class StarRating : Control
    {
        private int _value;
        private int _hover = -1;

        public event Action? UserChanged;   // fired when the user clicks to change the rating (not on programmatic set)
        public int Value { get => _value; set { _value = Math.Clamp(value, 0, 5); Invalidate(); } }

        public StarRating()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Bg;
            Cursor = Theme.HandCursor;
            MouseMove += (_, e) => { int s = StarAt(e.X); if (s != _hover) { _hover = s; Invalidate(); } };
            MouseLeave += (_, _) => { _hover = -1; Invalidate(); };
            MouseClick += (_, e) => { int s = StarAt(e.X); Value = (s == 1 && _value == 1) ? 0 : s; UserChanged?.Invoke(); }; // click the lone filled star again → clear
        }

        private int Cell => Math.Max(18, Height);
        private int StarAt(int x) => Math.Clamp(x / Cell + 1, 1, 5);

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent?.BackColor ?? Theme.Bg);
            int show = _hover > 0 ? _hover : _value;
            for (int i = 0; i < 5; i++)
            {
                var c = new PointF(i * Cell + Cell / 2f, Height / 2f);
                bool filled = i < show;
                using var b = new SolidBrush(filled ? Theme.Accent : Theme.Blend(Theme.PanelBg, Color.White, 0.10));
                DrawStar(g, c, Cell * 0.42f, b);
            }
        }

        private static void DrawStar(Graphics g, PointF c, float r, Brush b)
        {
            var pts = new PointF[10];
            for (int i = 0; i < 10; i++)
            {
                double ang = -Math.PI / 2 + i * Math.PI / 5;
                float rr = (i % 2 == 0) ? r : r * 0.42f;
                pts[i] = new PointF(c.X + (float)(Math.Cos(ang) * rr), c.Y + (float)(Math.Sin(ang) * rr));
            }
            g.FillPolygon(b, pts);
        }
    }
}
