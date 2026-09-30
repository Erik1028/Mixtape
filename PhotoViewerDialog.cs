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
    // Cached fonts — OnPaint redraws on every navigate/resize, so inline Theme fonts leaked a handle per repaint.
    private readonly Font _fCap = Theme.UiFont(10.5f, FontStyle.Bold);
    private readonly Font _fCounter = Theme.UiFont(9.5f);
    private readonly Font _fMsg = Theme.UiFont(11f);

    private enum Hit { None, Prev, Next, Close }
    private Hit _hover = Hit.None;
    // Classic: a 95 window of its own - the frame, the navy caption and its close box painted here (the OS frame is
    // taken away in WndProc, as the main window's is), a toolbar with the era's "< Previous" / "Next >" buttons, the
    // photo in a sunken black viewport in 256 colours, and a status bar with the grip.
    private readonly Font _fClassicCap = Theme.UiFont(Theme.SzBody, FontStyle.Bold);
    private readonly Font _fClassicBtn = Theme.UiFont(Theme.SzTitle);
    private const int ClassicStatusH = 20;

    public PhotoViewerDialog(IReadOnlyList<uint> ids, int startIndex, Func<uint, Bitmap?> decode, Func<uint, string?> caption)
    {
        _ids = ids.ToList();
        _index = Math.Clamp(startIndex, 0, Math.Max(0, _ids.Count - 1));
        _decode = decode;
        _caption = caption;

        FormBorderStyle = FormBorderStyle.Sizable;
        if (Theme.Classic) { MaximizeBox = false; MinimumSize = new Size(360, 300); }   // no maximize: the frame is ours
        Text = Loc.T("Photo");
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Theme.Classic ? Theme.Face : Color.FromArgb(12, 12, 14);
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

    private Rectangle CloseRect => Theme.Classic ? ClassicCloseRect : new(ClientSize.Width - 46, 14, 30, 30);
    private Rectangle PrevRect => Theme.Classic ? new(8, ClassicToolY, 96, 23) : new(16, ClientSize.Height / 2 - 26, 44, 52);
    private Rectangle NextRect => Theme.Classic ? new(8 + 96 + 6, ClassicToolY, 96, 23) : new(ClientSize.Width - 16 - 44, ClientSize.Height / 2 - 26, 44, 52);

    // ---- Classic geometry ----
    private Rectangle ClassicCaption => new(3, 3, Math.Max(0, ClientSize.Width - 6), Theme.ClassicCaptionH);
    private Rectangle ClassicCloseRect { get { var c = ClassicCaption; return new(c.Right - 2 - 16, c.Y + 3, 16, 14); } }
    private int ClassicToolY => ClassicCaption.Bottom + 6;
    private Rectangle ClassicStatus => new(3, ClientSize.Height - 3 - ClassicStatusH, Math.Max(0, ClientSize.Width - 6), ClassicStatusH);
    private Rectangle ClassicView => Rectangle.FromLTRB(8, ClassicToolY + 23 + 6, Math.Max(9, ClientSize.Width - 8), Math.Max(ClassicToolY + 30, ClassicStatus.Y - 4));

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        if (Theme.Classic) { PaintClassic(g); return; }
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
            TextRenderer.DrawText(g, Loc.T("This photo can't be previewed."), _fMsg,
                new Rectangle(0, 0, ClientSize.Width, ClientSize.Height), Theme.Faint,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        // top bar: caption + counter
        string cap = _ids.Count > 0 ? (_caption(_ids[_index]) ?? "") : "";
        string counter = _ids.Count > 0 ? $"{_index + 1} / {_ids.Count}" : "";
        TextRenderer.DrawText(g, cap, _fCap, new Rectangle(20, 12, ClientSize.Width - 200, 34),
            Theme.TextCol, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(g, counter, _fCounter, new Rectangle(ClientSize.Width - 200, 12, 140, 34),
            Theme.Subtle, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);

        // arrows
        if (_ids.Count > 1)
        {
            DrawArrow(g, PrevRect, true, _hover == Hit.Prev);
            DrawArrow(g, NextRect, false, _hover == Hit.Next);
        }
        DrawClose(g, CloseRect, _hover == Hit.Close);
    }

    /// <summary>Classic: the whole window, 1995 style (see the fields' note).</summary>
    private void PaintClassic(Graphics g)
    {
        string cap = _ids.Count > 0 ? (_caption(_ids[_index]) ?? "") : "";
        Theme.PaintClassicWindow(g, ClientRectangle, cap.Length > 0 ? $"{cap} - {Loc.T("Photo")}" : Loc.T("Photo"), _fClassicCap);
        Theme.PaintClassicClose(g, ClassicCloseRect, down: false);

        bool many = _ids.Count > 1;
        ClassicPush(g, PrevRect, Loc.T("< Previous"), many, _hover == Hit.Prev);
        ClassicPush(g, NextRect, Loc.T("Next >"), many, _hover == Hit.Next);

        var view = ClassicView;
        using (var bk = new SolidBrush(Color.Black)) g.FillRectangle(bk, view);
        Theme.Bevel(g, Rectangle.Inflate(view, 2, 2), raised: false);   // the client edge round the picture
        var area = Rectangle.Inflate(view, -8, -8);
        if (_current is not null && area.Width > 0 && area.Height > 0)
        {
            double s = Math.Min(area.Width / (double)_current.Width, area.Height / (double)_current.Height);
            int w = Math.Max(1, (int)(_current.Width * s)), h = Math.Max(1, (int)(_current.Height * s));
            var dest = new Rectangle(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h);
            if (Theme.DitherCovers) g.DrawImageUnscaled(Halftone.For(_current, dest.Size), dest.X, dest.Y);   // as a 256-colour display showed it
            else
            {
                g.InterpolationMode = s > 1.5 ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic;
                g.DrawImage(_current, dest);
            }
        }
        else if (_ids.Count > 0)
            TextRenderer.DrawText(g, Loc.T("This photo can't be previewed."), _fClassicBtn, view, Theme.Face,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

        // the status bar: which photo, and the picture's own size
        var st = ClassicStatus;
        string where = _ids.Count > 0 ? Loc.T("Photo {0} of {1}", _index + 1, _ids.Count) : "";
        string size = _current is not null ? $"{_current.Width} × {_current.Height}" : "";
        int sizeW = Math.Max(90, TextRenderer.MeasureText(size, _fClassicBtn).Width + 12);
        var pSize = new Rectangle(st.Right - 16 - sizeW, st.Y + 2, sizeW, st.Height - 2);
        var pMain = Rectangle.FromLTRB(st.X, st.Y + 2, pSize.X - 2, st.Bottom);
        foreach (var (pr, txt) in new[] { (pMain, where), (pSize, size) })
        {
            if (pr.Width < 10) continue;
            Theme.Bevel(g, pr, raised: false, thin: true);
            TextRenderer.DrawText(g, txt, _fClassicBtn, new Rectangle(pr.X + 4, pr.Y, pr.Width - 8, pr.Height), Theme.TextCol,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
        var gs = g.Save();
        g.TranslateTransform(st.X, st.Y);
        ClassicStatusBar.DrawGrip(g, st.Width, st.Height);
        g.Restore(gs);
    }

    /// <summary>Classic: a 95 push button - lit a little under the pointer, its label embossed when it can do nothing.</summary>
    private void ClassicPush(Graphics g, Rectangle r, string text, bool enabled, bool hover)
    {
        using (var b = new SolidBrush(enabled && hover ? Theme.Blend(Theme.Face, Color.White, 0.22) : Theme.Face)) g.FillRectangle(b, r);
        Theme.Bevel(g, r, raised: true);
        const TextFormatFlags f = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix;
        if (enabled) { TextRenderer.DrawText(g, text, _fClassicBtn, r, Theme.TextCol, f); return; }
        TextRenderer.DrawText(g, text, _fClassicBtn, new Rectangle(r.X + 1, r.Y + 1, r.Width, r.Height), Theme.FaceHi, f);
        TextRenderer.DrawText(g, text, _fClassicBtn, r, Theme.FaceShadow, f);
    }

    // Classic: the OS frame goes (the whole window is client, as the main window's is) and the edges, the caption and
    // the grip answer the hit test - so it still moves, resizes and snaps like any window.
    private const int WM_NCCALCSIZE = 0x0083, WM_NCHITTEST = 0x0084;
    private const int HTCLIENT = 1, HTCAPTION = 2, HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13, HTTOPRIGHT = 14, HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;

    protected override void WndProc(ref Message m)
    {
        if (Theme.Classic)
        {
            if (m.Msg == WM_NCCALCSIZE && m.WParam != IntPtr.Zero) { m.Result = IntPtr.Zero; return; }
            if (m.Msg == WM_NCHITTEST) { m.Result = (IntPtr)ClassicHitTest(); return; }
        }
        base.WndProc(ref m);
    }

    private int ClassicHitTest()
    {
        var p = PointToClient(Cursor.Position);
        int w = ClientSize.Width, h = ClientSize.Height;
        const int b = 4;
        bool l = p.X < b, r = p.X >= w - b, t = p.Y < b, bot = p.Y >= h - b;
        if (p.X >= w - 18 && p.Y >= h - 18) return HTBOTTOMRIGHT;   // the grip
        if (t && l) return HTTOPLEFT;
        if (t && r) return HTTOPRIGHT;
        if (bot && l) return HTBOTTOMLEFT;
        if (bot && r) return HTBOTTOMRIGHT;
        if (l) return HTLEFT;
        if (r) return HTRIGHT;
        if (t) return HTTOP;
        if (bot) return HTBOTTOM;
        return ClassicCaption.Contains(p) && !ClassicCloseRect.Contains(p) ? HTCAPTION : HTCLIENT;
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
        Theme.ClassicNoTransitions(Handle);   // Classic: no Windows 11 open/close/minimize animation
        if (Theme.Classic)
        {
            // square, and no DWM line round the frame we paint ourselves
            try { int round = Theme.DwmCorner(2); DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int)); } catch { }
            try { int bc = Theme.DwmBorder(Theme.Border); DwmSetWindowAttribute(Handle, 34, ref bc, sizeof(int)); } catch { }
            return;
        }
        try { int on = 1; DwmSetWindowAttribute(Handle, 20, ref on, sizeof(int)); } catch { }
        try { int caption = 0x000E0C0C; DwmSetWindowAttribute(Handle, 35, ref caption, sizeof(int)); } catch { }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _loadGen++; _current?.Dispose(); _fCap.Dispose(); _fCounter.Dispose(); _fMsg.Dispose(); _fClassicCap.Dispose(); _fClassicBtn.Dispose(); } // discard any in-flight decode result
        base.Dispose(disposing);
    }
}
