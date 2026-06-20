using System.Drawing.Drawing2D;

namespace iPodCommander;

/// <summary>
/// "Get Info"-style editor for one track's tags + star rating. Returns a <see cref="TrackEdit"/>
/// containing ONLY the fields the user actually changed (so untouched mhods are preserved verbatim
/// when written back). Themed dark dialog.
/// </summary>
internal sealed class TrackInfoDialog : Form
{
    private readonly Track _t;
    private readonly IReadOnlyList<Track> _tracks;
    private readonly bool _multi;
    private readonly HashSet<Control> _touched = new();   // fields the user actually edited (multi mode applies only these)
    private bool _ratingTouched;
    private readonly TextBox _title, _artist, _album, _albumArtist, _genre, _year, _track;
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
        Text = _multi ? Loc.T("Edit {0} songs", tracks.Count) : Loc.T("Song info");
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
        ClientSize = new Size(440, _multi ? 408 : 372);
        BackColor = Theme.Bg;
        ForeColor = Theme.TextCol;
        Font = Theme.UiFont(9.5f);

        // The shared value across the selection, or "" when the songs disagree (so a blank field = "leave each as-is").
        string Common(Func<Track, string> sel)
        {
            string first = sel(_t);
            return _tracks.All(x => sel(x) == first) ? first : "";
        }

        int y = 18;
        if (_multi)
        {
            Controls.Add(new Label { Text = Loc.T("Editing {0} songs. Type in a field to set it on all of them; leave a field blank to keep each song's own value.", tracks.Count),
                ForeColor = Theme.Subtle, AutoSize = false, Location = new Point(16, y), Size = new Size(410, 36) });
            y += 42;
        }

        TextBox Row(string label, string value, bool editable = true, int width = 300)
        {
            Controls.Add(new Label { Text = label, ForeColor = Theme.Subtle, AutoSize = false, TextAlign = ContentAlignment.MiddleRight, Location = new Point(16, y), Size = new Size(96, 26) });
            var tb = new TextBox { Text = value, Location = new Point(122, y), Width = width, BackColor = editable ? Theme.RowBg : Theme.PanelBg, ForeColor = editable ? Theme.TextCol : Theme.Faint, BorderStyle = BorderStyle.FixedSingle, ReadOnly = !editable, TabStop = editable };
            if (editable) tb.TextChanged += (_, _) => _touched.Add(tb);
            Controls.Add(tb);
            y += 36;
            return tb;
        }

        _title = Row(Loc.T("Title"), _multi ? Loc.T("(varies — not changed)") : (_t.Title ?? ""), !_multi);
        _artist = Row(Loc.T("Artist"), Common(x => x.Artist ?? ""));
        _album = Row(Loc.T("Album"), Common(x => x.Album ?? ""));
        _albumArtist = Row(Loc.T("Album artist"), Common(x => x.AlbumArtist ?? ""));
        _genre = Row(Loc.T("Genre"), Common(x => x.Genre ?? ""));

        // Year + Track # share a row. Track # is per-song, so it's disabled when editing several.
        Controls.Add(new Label { Text = Loc.T("Year"), ForeColor = Theme.Subtle, AutoSize = false, TextAlign = ContentAlignment.MiddleRight, Location = new Point(16, y), Size = new Size(96, 26) });
        _year = new TextBox { Text = Common(x => x.Year > 0 ? x.Year.ToString() : ""), Location = new Point(122, y), Width = 70, BackColor = Theme.RowBg, ForeColor = Theme.TextCol, BorderStyle = BorderStyle.FixedSingle };
        _year.TextChanged += (_, _) => _touched.Add(_year);
        Controls.Add(_year);
        Controls.Add(new Label { Text = Loc.T("Track #"), ForeColor = Theme.Subtle, AutoSize = false, TextAlign = ContentAlignment.MiddleRight, Location = new Point(206, y), Size = new Size(66, 26) });
        _track = new TextBox { Text = _multi ? "" : (_t.TrackNumber > 0 ? _t.TrackNumber.ToString() : ""), Location = new Point(282, y), Width = 70, BackColor = _multi ? Theme.PanelBg : Theme.RowBg, ForeColor = _multi ? Theme.Faint : Theme.TextCol, BorderStyle = BorderStyle.FixedSingle, ReadOnly = _multi, TabStop = !_multi };
        if (!_multi) _track.TextChanged += (_, _) => _touched.Add(_track);
        Controls.Add(_track);
        y += 38;

        // Rating stars.
        Controls.Add(new Label { Text = Loc.T("Rating"), ForeColor = Theme.Subtle, AutoSize = false, TextAlign = ContentAlignment.MiddleRight, Location = new Point(16, y), Size = new Size(96, 26) });
        int commonStars = _multi && !_tracks.All(x => Math.Min(5, x.Rating / 20) == Math.Min(5, _t.Rating / 20)) ? 0 : Math.Min(5, _t.Rating / 20);
        _rating = new StarRating { Location = new Point(120, y - 2), Size = new Size(160, 30), Value = commonStars };
        _rating.UserChanged += () => _ratingTouched = true;
        Controls.Add(_rating);
        y += 44;

        var save = new ThemedButton { Text = Loc.T("Save"), Primary = true, Pill = true, Width = 100, Height = 32, Location = new Point(ClientSize.Width - 116, y), DialogResult = DialogResult.OK };
        var cancel = new ThemedButton { Text = Loc.T("Cancel"), Pill = true, Width = 96, Height = 32, Location = new Point(ClientSize.Width - 116 - 106, y), DialogResult = DialogResult.Cancel };
        save.Click += (_, _) => BuildEdit();
        Controls.Add(save);
        Controls.Add(cancel);
        AcceptButton = save;
        CancelButton = cancel;
        if (_multi) ActiveControl = _artist;   // start on the first editable field, not the disabled Title
    }

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
            if (_touched.Contains(_year)) e.Year = uint.TryParse(_year.Text.Trim(), out var yv) ? yv : 0;
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

        uint newY = uint.TryParse(_year.Text.Trim(), out var y2) ? y2 : 0;
        if (newY != _t.Year) e.Year = newY;
        uint newTrack = uint.TryParse(_track.Text.Trim(), out var tv) ? tv : 0;
        if (newTrack != _t.TrackNumber) e.TrackNumber = newTrack;

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
        Edit.AlbumArtist is not null || Edit.Genre is not null || Edit.Year is not null ||
        Edit.TrackNumber is not null || Edit.Rating is not null;

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try { int on = 1; DwmSetWindowAttribute(Handle, 20, ref on, sizeof(int)); } catch { }
        try { int caption = 0x001A1716; DwmSetWindowAttribute(Handle, 35, ref caption, sizeof(int)); } catch { }
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
            Cursor = Cursors.Hand;
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
