using System.Drawing.Drawing2D;

namespace iPodCommander;

/// <summary>
/// A full-window dark "lightbox" for viewing the photos stored on the iPod. The iPod keeps only the
/// pre-rendered RGB565 thumbnails (no originals), so this shows the largest decodable slot
/// (≈320×240) scaled to fit. Left/right arrows (or ← →) navigate; Esc or the × closes. Photos are
/// decoded lazily through a callback so a 1500-photo library stays light.
/// </summary>
internal sealed class PhotoViewerDialog : Form
{
    private readonly List<uint> _ids;
    private readonly Func<uint, Bitmap?> _decode;
    private readonly Func<uint, string?> _caption;
    private int _index;
    private Bitmap? _current;
    private int _loadGen; // bumped each navigation; a slower decode for a stale photo is discarded

    private enum Hit { None, Prev, Next, Close }
    private Hit _hover = Hit.None;

    public PhotoViewerDialog(IReadOnlyList<uint> ids, int startIndex, Func<uint, Bitmap?> decode, Func<uint, string?> caption)
    {
        _ids = ids.ToList();
        _index = Math.Clamp(startIndex, 0, Math.Max(0, _ids.Count - 1));
        _decode = decode;
        _caption = caption;

        FormBorderStyle = FormBorderStyle.Sizable;
        Text = "Photo";
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Color.FromArgb(12, 12, 14);
        KeyPreview = true;
        DoubleBuffered = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        ClientSize = new Size(900, 640);

        MouseDown += (_, e) =>
        {
            if (CloseRect.Contains(e.Location)) { Close(); return; }
            if (_ids.Count > 1 && PrevRect.Contains(e.Location)) { Step(-1); return; }
            if (_ids.Count > 1 && NextRect.Contains(e.Location)) { Step(+1); return; }
        };
        MouseMove += (_, e) =>
        {
            var h = CloseRect.Contains(e.Location) ? Hit.Close
                : _ids.Count > 1 && PrevRect.Contains(e.Location) ? Hit.Prev
                : _ids.Count > 1 && NextRect.Contains(e.Location) ? Hit.Next : Hit.None;
            if (h != _hover) { _hover = h; Invalidate(); }
        };
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape) Close();
            else if (e.KeyCode == Keys.Left) Step(-1);
            else if (e.KeyCode is Keys.Right or Keys.Space) Step(+1);
        };
        LoadCurrent();
    }

    private void Step(int d)
    {
        if (_ids.Count == 0) return;
        _index = (_index + d + _ids.Count) % _ids.Count;
        LoadCurrent();
    }

    private void LoadCurrent()
    {
        _current?.Dispose();
        _current = null;          // OnPaint shows a blank/placeholder while the slot decodes
        Invalidate();
        if (_ids.Count == 0) return;
        int gen = ++_loadGen;
        uint id = _ids[_index];
        var decode = _decode;
        // The .ithmb seek-read + RGB565 decode can be slow on a USB iPod; do it off the UI thread.
        System.Threading.Tasks.Task.Run(() => decode(id)).ContinueWith(t =>
        {
            var bmp = t.Status == System.Threading.Tasks.TaskStatus.RanToCompletion ? t.Result : null;
            if (!IsHandleCreated) { bmp?.Dispose(); return; }
            try
            {
                BeginInvoke(() =>
                {
                    if (gen != _loadGen) { bmp?.Dispose(); return; } // the user already navigated on
                    _current?.Dispose();
                    _current = bmp;
                    Invalidate();
                });
            }
            catch { bmp?.Dispose(); } // form closing
        });
    }

    private Rectangle CloseRect => new(ClientSize.Width - 46, 14, 30, 30);
    private Rectangle PrevRect => new(16, ClientSize.Height / 2 - 26, 44, 52);
    private Rectangle NextRect => new(ClientSize.Width - 16 - 44, ClientSize.Height / 2 - 26, 44, 52);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);

        // image, fit-to-window, letterboxed
        var area = new Rectangle(24, 56, ClientSize.Width - 48, ClientSize.Height - 96);
        if (_current is not null && area.Width > 0 && area.Height > 0)
        {
            double s = Math.Min(area.Width / (double)_current.Width, area.Height / (double)_current.Height);
            int w = Math.Max(1, (int)(_current.Width * s)), h = Math.Max(1, (int)(_current.Height * s));
            var dest = new Rectangle(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h);
            g.InterpolationMode = s > 1.5 ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic;
            using (var sh = new SolidBrush(Color.FromArgb(120, 0, 0, 0)))
                g.FillRectangle(sh, dest.X + 4, dest.Y + 8, dest.Width, dest.Height);
            g.DrawImage(_current, dest);
        }
        else
        {
            TextRenderer.DrawText(g, "This photo can't be previewed.", Theme.UiFont(11f),
                new Rectangle(0, 0, ClientSize.Width, ClientSize.Height), Theme.Faint,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        // top bar: caption + counter
        string cap = _ids.Count > 0 ? (_caption(_ids[_index]) ?? "") : "";
        string counter = _ids.Count > 0 ? $"{_index + 1} / {_ids.Count}" : "";
        TextRenderer.DrawText(g, cap, Theme.UiFont(10.5f, FontStyle.Bold), new Rectangle(20, 12, ClientSize.Width - 200, 34),
            Theme.TextCol, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(g, counter, Theme.UiFont(9.5f), new Rectangle(ClientSize.Width - 200, 12, 140, 34),
            Theme.Subtle, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);

        // arrows
        if (_ids.Count > 1)
        {
            DrawArrow(g, PrevRect, true, _hover == Hit.Prev);
            DrawArrow(g, NextRect, false, _hover == Hit.Next);
        }
        DrawClose(g, CloseRect, _hover == Hit.Close);
    }

    private static void DrawArrow(Graphics g, Rectangle r, bool left, bool hover)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var b = new SolidBrush(Color.FromArgb(hover ? 150 : 90, 0, 0, 0))) { using var p = Theme.RoundedRect(r, 10); g.FillPath(b, p); }
        using var pen = new Pen(hover ? Color.White : Color.FromArgb(220, 235, 235, 235), 2.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        var c = new Point(r.X + r.Width / 2, r.Y + r.Height / 2);
        int dx = left ? 6 : -6;
        g.DrawLines(pen, new[] { new Point(c.X + dx, c.Y - 9), new Point(c.X - dx, c.Y), new Point(c.X + dx, c.Y + 9) });
    }

    private static void DrawClose(Graphics g, Rectangle r, bool hover)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (hover) { using var hb = new SolidBrush(Color.FromArgb(150, 0, 0, 0)); using var hp = Theme.RoundedRect(r, r.Width / 2f); g.FillPath(hb, hp); }
        using var p = new Pen(hover ? Color.White : Color.FromArgb(210, 235, 235, 235), 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        int m = 9;
        g.DrawLine(p, r.Left + m, r.Top + m, r.Right - m, r.Bottom - m);
        g.DrawLine(p, r.Right - m, r.Top + m, r.Left + m, r.Bottom - m);
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try { int on = 1; DwmSetWindowAttribute(Handle, 20, ref on, sizeof(int)); } catch { }
        try { int caption = 0x000E0C0C; DwmSetWindowAttribute(Handle, 35, ref caption, sizeof(int)); } catch { }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _loadGen++; _current?.Dispose(); } // discard any in-flight decode result
        base.Dispose(disposing);
    }
}
