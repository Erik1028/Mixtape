using System.Drawing.Drawing2D;

namespace iPodCommander;

/// <summary>A modal multi-select picker for the generated <see cref="Wallpaper"/> pack. Returns the chosen
/// design indices so the host can render them full-size and add them to the iPod's Photos library.</summary>
internal sealed class WallpaperPickerDialog : Form
{
    private readonly WallpaperGrid _grid;
    private readonly ThemedButton _add;
    public IReadOnlyList<int> SelectedIndices => _grid.Selected;

    public WallpaperPickerDialog()
    {
        Text = Loc.T("Add wallpapers");
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
        ClientSize = new Size(596, 396);
        BackColor = Theme.Bg; ForeColor = Theme.TextCol; Font = Theme.UiFont(9.5f);

        var caption = new Label
        {
            Text = Loc.T("Pick wallpapers to add to your iPod's Photos. View them full-screen or as a slideshow on the device."),
            AutoSize = false, Left = 18, Top = 14, Width = ClientSize.Width - 36, Height = 36,
            ForeColor = Theme.Subtle, Font = Theme.UiFont(9.5f),
        };
        _grid = new WallpaperGrid { Left = 0, Top = 52, Width = ClientSize.Width, Height = 286, BackColor = Theme.Bg };
        _grid.Changed += UpdateAdd;
        _grid.Confirmed += () => { if (_grid.Selected.Count > 0) { DialogResult = DialogResult.OK; Close(); } };

        _add = new ThemedButton { Primary = true, Pill = true, Width = 150, Height = 32, DialogResult = DialogResult.OK, Anchor = AnchorStyles.Bottom | AnchorStyles.Right, Location = new Point(ClientSize.Width - 150 - 18, 350) };
        var cancel = new ThemedButton { Text = Loc.T("Cancel"), Pill = true, Width = 96, Height = 32, DialogResult = DialogResult.Cancel, Anchor = AnchorStyles.Bottom | AnchorStyles.Right, Location = new Point(ClientSize.Width - 150 - 96 - 28, 350) };
        Controls.Add(caption);
        Controls.Add(_grid);
        Controls.Add(_add);
        Controls.Add(cancel);
        AcceptButton = _add; CancelButton = cancel;
        UpdateAdd();
        if (Anim.MotionEnabled) Opacity = 0;
    }

    private void UpdateAdd()
    {
        int n = _grid.Selected.Count;
        _add.Text = n > 0 ? Loc.T("Add {0} to iPod", n) : Loc.T("Add to iPod");
        _add.Enabled = n > 0;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (!Anim.MotionEnabled) { Opacity = 1; return; }
        int home = Top; Top = home + 10;
        Anim.Run(190, v => { if (IsDisposed) return; Opacity = v; Top = home + (int)Math.Round(10 * (1 - v)); },
            () => { if (!IsDisposed) { Opacity = 1; Top = home; } }, Easings.OutCubic);
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try { int on = 1; DwmSetWindowAttribute(Handle, 20, ref on, sizeof(int)); } catch { }
        try { int cap = 0x001A1716; DwmSetWindowAttribute(Handle, 35, ref cap, sizeof(int)); } catch { }
    }
}

/// <summary>Owner-drawn multi-select grid of the wallpaper pack (cached 4:3 thumbnails + name + check).</summary>
internal sealed class WallpaperGrid : Panel
{
    public event Action? Changed;
    public event Action? Confirmed;
    private readonly HashSet<int> _sel = new();
    public IReadOnlyList<int> Selected => _sel.OrderBy(x => x).ToList();

    private const int TileW = 128, TileH = 96, NameH = 18, Gap = 16, Pad = 18, Cols = 4;
    private readonly Bitmap[] _thumbs;
    private readonly List<(Rectangle Rect, int Idx)> _hit = new();
    private int _hover = -1;

    public WallpaperGrid()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        _thumbs = new Bitmap[Wallpaper.Count];
        for (int i = 0; i < _thumbs.Length; i++) _thumbs[i] = Wallpaper.Render(i, TileW, TileH);
        MouseMove += (_, e) => { int h = HitAt(e.Location); if (h != _hover) { _hover = h; Invalidate(); } };
        MouseLeave += (_, _) => { if (_hover != -1) { _hover = -1; Invalidate(); } };
        MouseDown += (_, e) => { int h = HitAt(e.Location); if (h >= 0) { if (!_sel.Add(h)) _sel.Remove(h); Changed?.Invoke(); Invalidate(); } };
        MouseDoubleClick += (_, e) => { if (HitAt(e.Location) >= 0) Confirmed?.Invoke(); };
    }

    private int HitAt(Point p) { foreach (var (r, i) in _hit) if (r.Contains(p)) return i; return -1; }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Theme.Bg);
        _hit.Clear();
        for (int i = 0; i < _thumbs.Length; i++)
        {
            int col = i % Cols, row = i / Cols;
            int x = Pad + col * (TileW + Gap), y = Pad + row * (TileH + NameH + Gap);
            var rect = new Rectangle(x, y, TileW, TileH);
            _hit.Add((rect, i));

            using (var path = Theme.RoundedRect(rect, 9))
            {
                var save = g.Clip; g.SetClip(path, CombineMode.Intersect);
                g.DrawImage(_thumbs[i], rect);
                g.Clip = save;
                using var edge = new Pen(Color.FromArgb(40, 255, 255, 255)); g.DrawPath(edge, path);
            }

            TextRenderer.DrawText(g, Loc.T(Wallpaper.Names[i]), Theme.UiFont(8.25f), new Rectangle(x, y + TileH + 1, TileW, NameH),
                _sel.Contains(i) ? Theme.TextCol : Theme.Subtle, TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.EndEllipsis);

            bool selected = _sel.Contains(i);
            if (selected)
            {
                using var pen = new Pen(Theme.Accent, 3);
                using var sp = Theme.RoundedRect(new RectangleF(rect.X - 1, rect.Y - 1, rect.Width + 2, rect.Height + 2), 10);
                g.DrawPath(pen, sp);
                // check badge, top-right
                var badge = new RectangleF(rect.Right - 22, rect.Y + 6, 16, 16);
                using (var bb = new SolidBrush(Theme.Accent)) g.FillEllipse(bb, badge);
                using var cp = new Pen(Theme.OnAccent, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawLines(cp, new[] { new PointF(badge.X + 4, badge.Y + 8.5f), new PointF(badge.X + 7, badge.Y + 11.5f), new PointF(badge.X + 12, badge.Y + 5f) });
            }
            else if (i == _hover)
            {
                using var pen = new Pen(Theme.Blend(Theme.Bg, Color.White, 0.3), 2);
                using var sp = Theme.RoundedRect(rect, 9);
                g.DrawPath(pen, sp);
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) foreach (var b in _thumbs) b.Dispose();
        base.Dispose(disposing);
    }
}
