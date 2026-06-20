using System.Drawing.Drawing2D;

namespace iPodCommander;

/// <summary>A modal picker showing the <see cref="CoverArt"/> gallery (plus a "Default / automatic"
/// tile) so the user can choose a cover for a playlist or the library. Returns the chosen art id,
/// or -1 for "default" (revert to the song-derived/auto cover).</summary>
internal sealed class CoverPickerDialog : Form
{
    private readonly CoverGrid _grid;
    public int SelectedCoverId => _grid.SelectedCoverId;

    public CoverPickerDialog(string title, int currentId, string? sampleName = null)
    {
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
        ClientSize = new Size(596, 540);
        BackColor = Theme.Bg;
        ForeColor = Theme.TextCol;
        Font = Theme.UiFont(9.5f);

        _grid = new CoverGrid(currentId, sampleName) { Dock = DockStyle.Top, Height = 472, BackColor = Theme.Bg };
        _grid.Confirmed += () => { DialogResult = DialogResult.OK; Close(); };

        var ok = new ThemedButton { Text = Loc.T("Use cover"), Primary = true, Pill = true, Width = 110, Height = 32, DialogResult = DialogResult.OK, Anchor = AnchorStyles.Bottom | AnchorStyles.Right, Location = new Point(ClientSize.Width - 110 - 18, 490) };
        var cancel = new ThemedButton { Text = Loc.T("Cancel"), Pill = true, Width = 96, Height = 32, DialogResult = DialogResult.Cancel, Anchor = AnchorStyles.Bottom | AnchorStyles.Right, Location = new Point(ClientSize.Width - 110 - 96 - 28, 490) };
        Controls.Add(_grid);
        Controls.Add(ok);
        Controls.Add(cancel);
        AcceptButton = ok;
        CancelButton = cancel;
        if (Anim.MotionEnabled) Opacity = 0;   // fade up in OnShown, matching the Settings / Library Doctor dialogs
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (!Anim.MotionEnabled) { Opacity = 1; return; }
        int home = Top;
        Top = home + 10;
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

/// <summary>Owner-drawn single-select grid: a "Default" tile then the CoverArt gallery.</summary>
internal sealed class CoverGrid : Panel
{
    public event Action? Confirmed;
    public int SelectedCoverId { get; private set; } // -1 = default

    private const int Tile = 78, Gap = 12, Pad = 16, Cols = 6;
    private int _hover = -2; // -2 none, -1 default tile, 0..N art
    private readonly List<(Rectangle Rect, int Id)> _hit = new();
    private readonly string _sampleName;

    public CoverGrid(int currentId, string? sampleName)
    {
        _sampleName = string.IsNullOrWhiteSpace(sampleName) ? "Mixtape" : sampleName!;
        SelectedCoverId = currentId;
        DoubleBuffered = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        MouseMove += (_, e) => { int h = HitId(e.Location); if (h != _hover) { _hover = h; Invalidate(); } };
        MouseLeave += (_, _) => { _hover = -2; Invalidate(); };
        MouseDown += (_, e) => { int h = HitId(e.Location); if (h != -2) { SelectedCoverId = h; Invalidate(); } };
        MouseDoubleClick += (_, e) => { if (HitId(e.Location) != -2) Confirmed?.Invoke(); };
    }

    private int HitId(Point p)
    {
        foreach (var (rect, id) in _hit) if (rect.Contains(p)) return id;
        return -2;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Theme.Bg);
        _hit.Clear();

        // tile -1 = Default, then 0..Count-1, then the cassette ("mixtape") tile last
        int total = CoverArt.Count + 2;
        for (int i = 0; i < total; i++)
        {
            int id = i == total - 1 ? CoverArt.CassetteId : i - 1; // last = cassette; else -1 default then 0..
            int col = i % Cols, row = i / Cols;
            int x = Pad + col * (Tile + Gap), y = Pad + row * (Tile + Gap);
            var rect = new Rectangle(x, y, Tile, Tile);
            _hit.Add((rect, id));

            if (id == -1)
            {
                using var bb = new SolidBrush(Theme.PanelBg);
                using var bp = Theme.RoundedRect(rect, 10);
                g.FillPath(bb, bp);
                TextRenderer.DrawText(g, Loc.T("Default"), Theme.UiFont(8.5f, FontStyle.Bold), rect, Theme.Subtle, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
            else if (id == CoverArt.CassetteId)
            {
                g.DrawImage(CoverArt.GenerateTitled(CoverArt.CassetteId, Tile, _sampleName), rect);
            }
            else
            {
                g.DrawImage(CoverArt.Generate(id, Tile), rect);
            }

            if (id == SelectedCoverId)
            {
                using var pen = new Pen(Theme.Accent, 3);
                using var sp = Theme.RoundedRect(new RectangleF(rect.X - 1, rect.Y - 1, rect.Width + 2, rect.Height + 2), 11);
                g.DrawPath(pen, sp);
            }
            else if (id == _hover)
            {
                using var pen = new Pen(Theme.Blend(Theme.Bg, Color.White, 0.3), 2);
                using var sp = Theme.RoundedRect(new RectangleF(rect.X, rect.Y, rect.Width, rect.Height), 10);
                g.DrawPath(pen, sp);
            }
        }
    }
}
