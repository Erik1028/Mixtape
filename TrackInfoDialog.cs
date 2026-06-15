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
    private readonly TextBox _title, _artist, _album, _albumArtist, _genre, _year, _track;
    private readonly StarRating _rating;

    /// <summary>The edit to apply (only changed fields), valid after the dialog returns OK.</summary>
    public TrackEdit Edit { get; private set; } = new();

    public TrackInfoDialog(Track t)
    {
        _t = t;
        Text = "Song info";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
        ClientSize = new Size(440, 372);
        BackColor = Theme.Bg;
        ForeColor = Theme.TextCol;
        Font = Theme.UiFont(9.5f);

        int y = 18;
        TextBox Row(string label, string? value, int width = 300)
        {
            Controls.Add(new Label { Text = label, ForeColor = Theme.Subtle, AutoSize = false, TextAlign = ContentAlignment.MiddleRight, Location = new Point(16, y), Size = new Size(96, 26) });
            var tb = new TextBox { Text = value ?? "", Location = new Point(122, y), Width = width, BackColor = Theme.RowBg, ForeColor = Theme.TextCol, BorderStyle = BorderStyle.FixedSingle };
            Controls.Add(tb);
            y += 36;
            return tb;
        }

        _title = Row("Title", t.Title);
        _artist = Row("Artist", t.Artist);
        _album = Row("Album", t.Album);
        _albumArtist = Row("Album artist", t.AlbumArtist);
        _genre = Row("Genre", t.Genre);

        // Year + Track # share a row.
        Controls.Add(new Label { Text = "Year", ForeColor = Theme.Subtle, AutoSize = false, TextAlign = ContentAlignment.MiddleRight, Location = new Point(16, y), Size = new Size(96, 26) });
        _year = new TextBox { Text = t.Year > 0 ? t.Year.ToString() : "", Location = new Point(122, y), Width = 70, BackColor = Theme.RowBg, ForeColor = Theme.TextCol, BorderStyle = BorderStyle.FixedSingle };
        Controls.Add(_year);
        Controls.Add(new Label { Text = "Track #", ForeColor = Theme.Subtle, AutoSize = false, TextAlign = ContentAlignment.MiddleRight, Location = new Point(206, y), Size = new Size(66, 26) });
        _track = new TextBox { Text = t.TrackNumber > 0 ? t.TrackNumber.ToString() : "", Location = new Point(282, y), Width = 70, BackColor = Theme.RowBg, ForeColor = Theme.TextCol, BorderStyle = BorderStyle.FixedSingle };
        Controls.Add(_track);
        y += 38;

        // Rating stars.
        Controls.Add(new Label { Text = "Rating", ForeColor = Theme.Subtle, AutoSize = false, TextAlign = ContentAlignment.MiddleRight, Location = new Point(16, y), Size = new Size(96, 26) });
        _rating = new StarRating { Location = new Point(120, y - 2), Size = new Size(160, 30), Value = Math.Min(5, t.Rating / 20) };
        Controls.Add(_rating);
        y += 44;

        var save = new ThemedButton { Text = "Save", Primary = true, Pill = true, Width = 100, Height = 32, Location = new Point(ClientSize.Width - 116, y), DialogResult = DialogResult.OK };
        var cancel = new ThemedButton { Text = "Cancel", Pill = true, Width = 96, Height = 32, Location = new Point(ClientSize.Width - 116 - 106, y), DialogResult = DialogResult.Cancel };
        save.Click += (_, _) => BuildEdit();
        Controls.Add(save);
        Controls.Add(cancel);
        AcceptButton = save;
        CancelButton = cancel;
    }

    private void BuildEdit()
    {
        var e = new TrackEdit();
        // Only include a field when it actually changed, so unchanged tags keep their exact bytes.
        if (_title.Text != (_t.Title ?? "")) e.Title = _title.Text;
        if (_artist.Text != (_t.Artist ?? "")) e.Artist = _artist.Text;
        if (_album.Text != (_t.Album ?? "")) e.Album = _album.Text;
        if (_albumArtist.Text != (_t.AlbumArtist ?? "")) e.AlbumArtist = _albumArtist.Text;
        if (_genre.Text != (_t.Genre ?? "")) e.Genre = _genre.Text;

        uint newYear = uint.TryParse(_year.Text.Trim(), out var yv) ? yv : 0;
        if (newYear != _t.Year) e.Year = newYear;
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

        public int Value { get => _value; set { _value = Math.Clamp(value, 0, 5); Invalidate(); } }

        public StarRating()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Bg;
            Cursor = Cursors.Hand;
            MouseMove += (_, e) => { int s = StarAt(e.X); if (s != _hover) { _hover = s; Invalidate(); } };
            MouseLeave += (_, _) => { _hover = -1; Invalidate(); };
            MouseClick += (_, e) => { int s = StarAt(e.X); Value = (s == 1 && _value == 1) ? 0 : s; }; // click the lone filled star again → clear
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
