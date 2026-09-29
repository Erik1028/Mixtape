using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace iPodCommander;

/// <summary>
/// A borderless, DWM-rounded, fully themed stand-in for <see cref="MessageBox"/>. It mirrors the
/// <c>MessageBox.Show(owner, text, caption, buttons, icon)</c> shape so call sites only swap the type
/// name. Owner-drawn dark card with a themed status glyph, a title, a wrapped message, and
/// <see cref="ThemedButton"/>s mapped from the requested button set (primary/default on the right).
/// </summary>
internal sealed class MessageDialog : GlassDialog
{
    public static DialogResult Show(IWin32Window? owner, string text, string caption,
        MessageBoxButtons buttons = MessageBoxButtons.OK, MessageBoxIcon icon = MessageBoxIcon.None)
    {
        using var dlg = new MessageDialog(text ?? "", caption ?? "", DefsFor(buttons), icon);
        return owner is not null ? dlg.ShowDialog(owner) : dlg.ShowDialog();
    }

    /// <summary>Show with fully custom button captions (already Loc.T'd by the caller). Buttons lay out
    /// left→right in array order — keep the primary/default one last so it sits on the right.</summary>
    public static DialogResult Show(IWin32Window? owner, string text, string caption,
        (string label, DialogResult result, bool primary)[] customButtons, MessageBoxIcon icon = MessageBoxIcon.None)
    {
        using var dlg = new MessageDialog(text ?? "", caption ?? "", customButtons, icon);
        return owner is not null ? dlg.ShowDialog(owner) : dlg.ShowDialog();
    }

    /// <summary>Render harness: the dialog built but not shown, so it can be captured.</summary>
    internal static MessageDialog Preview(string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon) => new(text, caption, DefsFor(buttons), icon);

    private readonly string _caption, _message;
    private readonly MessageBoxIcon _icon;
    private Rectangle _iconRect, _titleRect, _msgRect;
    private readonly Font _titleFont, _msgFont;
    private ThemedButton? _default;

    private const int Pad = 24, IconSize = 38, IconGap = 16, BtnH = 34, BtnGap = 10, TitleGap = 6, MsgBtnGap = 22, MaxW = 480;

    private MessageDialog(string text, string caption, (string label, DialogResult result, bool primary)[] buttons, MessageBoxIcon icon)
    {
        _message = text;
        _caption = string.IsNullOrWhiteSpace(caption) ? "Mixtape" : caption;
        _icon = icon;
        _titleFont = Theme.Classic ? Theme.UiFont(Theme.SzTitle, FontStyle.Bold) : Theme.DisplayFont(14f, FontStyle.Bold);   // Classic: the caption goes up into the title bar
        _msgFont = Theme.UiFont(10f);

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MaximizeBox = MinimizeBox = false;
        BackColor = Theme.Blend(Theme.Bg, Color.White, 0.05);
        ForeColor = Theme.TextCol;
        Font = _msgFont;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

        bool hasIcon = icon != MessageBoxIcon.None;
        int textX = Pad + (hasIcon ? IconSize + IconGap : 0);

        // Buttons first — the dialog can't be narrower than the button row.
        var btns = BuildButtons(buttons);
        int btnTotal = btns.Sum(b => b.Width) + Math.Max(0, btns.Count - 1) * BtnGap;

        if (Theme.Classic) LayoutClassic(btns, hasIcon);
        else
        {
        // Width adapts to the content (short messages → narrow), capped so long text wraps instead of stretching.
        int maxContentW = MaxW - textX - Pad;
        int titleNat = TextRenderer.MeasureText(_caption, _titleFont, new Size(2000, 999), TextFormatFlags.NoPrefix).Width;
        int msgNat = TextRenderer.MeasureText(_message, _msgFont, new Size(2000, 6000), TextFormatFlags.NoPrefix).Width;
        int contentW = Math.Clamp(Math.Max(titleNat, msgNat), 210, maxContentW);
        int W = Math.Max(textX + contentW + Pad, 2 * Pad + btnTotal);
        contentW = W - textX - Pad;

        const TextFormatFlags wrap = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix;
        int titleH = TextRenderer.MeasureText(_caption, _titleFont, new Size(contentW, 999), wrap).Height;
        int msgH = Math.Min(640, TextRenderer.MeasureText(_message, _msgFont, new Size(contentW, 6000), wrap).Height);

        _iconRect = new Rectangle(Pad, Pad, IconSize, IconSize);
        _titleRect = new Rectangle(textX, Pad + 1, contentW, titleH);
        _msgRect = new Rectangle(textX, _titleRect.Bottom + TitleGap, contentW, msgH);
        int contentBottom = Math.Max(_msgRect.Bottom, hasIcon ? _iconRect.Bottom : 0);
        int btnTop = contentBottom + MsgBtnGap;
        ClientSize = new Size(W, btnTop + BtnH + Pad);

        int x = W - Pad - btnTotal;
        foreach (var b in btns) { b.Location = new Point(x, btnTop); x += b.Width + BtnGap; Controls.Add(b); }
        }

        _default = btns.FirstOrDefault(b => b.Primary) ?? btns.LastOrDefault();
        AcceptButton = _default;
        CancelButton = btns.FirstOrDefault(b => b.DialogResult == DialogResult.Cancel)
                    ?? btns.FirstOrDefault(b => b.DialogResult == DialogResult.No)
                    ?? btns.FirstOrDefault(b => b.DialogResult == DialogResult.OK);

        // Let the user drag the card by its body (it has no title bar).
        MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) { try { ReleaseCapture(); SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero); } catch { } } };
        Shown += (_, _) => _default?.Focus();
    }

    /// <summary>
    /// The 1995 message box: the caption goes up into a navy title bar, a 32 px icon and the text sit on the
    /// grey face, and the buttons - 75 x 23, the era's own size - are CENTRED under them, not pushed right.
    /// </summary>
    private void LayoutClassic(List<ThemedButton> btns, bool hasIcon)
    {
        int cap = Theme.ClassicCaptionH + 3;
        const int pad = 14, icon = 32, bw = 75, bh = 23, gap = 6;
        foreach (var b in btns) { b.Height = bh; b.Width = Math.Max(bw, TextRenderer.MeasureText(b.Text, b.Font).Width + 18); }
        int btnTotal = btns.Sum(b => b.Width) + Math.Max(0, btns.Count - 1) * gap;
        int textX = pad + (hasIcon ? icon + 14 : 0);
        const TextFormatFlags wrap = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix;
        int msgNat = TextRenderer.MeasureText(_message, _msgFont, new Size(2000, 6000), TextFormatFlags.NoPrefix).Width;
        int capNat = TextRenderer.MeasureText(_caption, _titleFont, new Size(2000, 99), TextFormatFlags.NoPrefix).Width + 48;
        int contentW = Math.Clamp(Math.Max(msgNat, capNat - textX - pad), 150, 380);
        int W = Math.Max(textX + contentW + pad, 2 * pad + btnTotal);
        contentW = W - textX - pad;
        int msgH = Math.Min(640, TextRenderer.MeasureText(_message, _msgFont, new Size(contentW, 6000), wrap).Height);
        _iconRect = new Rectangle(pad, cap + pad, icon, icon);
        _titleRect = new Rectangle(3, 3, W - 6, Theme.ClassicCaptionH);
        _msgRect = new Rectangle(textX, cap + pad + (hasIcon ? Math.Max(0, (icon - msgH) / 2) : 0), contentW, msgH);
        int bottom = Math.Max(_msgRect.Bottom, hasIcon ? _iconRect.Bottom : 0);
        int btnTop = bottom + 16;
        ClientSize = new Size(W, btnTop + bh + 12);
        int x = (W - btnTotal) / 2;
        foreach (var b in btns) { b.Location = new Point(x, btnTop); x += b.Width + gap; Controls.Add(b); }
    }

    private static (string, DialogResult, bool)[] DefsFor(MessageBoxButtons buttons) => buttons switch
    {
        MessageBoxButtons.OKCancel => new[] { (Loc.T("Cancel"), DialogResult.Cancel, false), (Loc.T("OK"), DialogResult.OK, true) },
        MessageBoxButtons.YesNo => new[] { (Loc.T("No"), DialogResult.No, false), (Loc.T("Yes"), DialogResult.Yes, true) },
        MessageBoxButtons.YesNoCancel => new[] { (Loc.T("Cancel"), DialogResult.Cancel, false), (Loc.T("No"), DialogResult.No, false), (Loc.T("Yes"), DialogResult.Yes, true) },
        MessageBoxButtons.RetryCancel => new[] { (Loc.T("Cancel"), DialogResult.Cancel, false), (Loc.T("Retry"), DialogResult.Retry, true) },
        _ => new[] { (Loc.T("OK"), DialogResult.OK, true) },
    };

    private List<ThemedButton> BuildButtons((string label, DialogResult result, bool primary)[] defs)
    {
        var list = new List<ThemedButton>();
        foreach (var (label, result, primary) in defs)
        {
            var b = new ThemedButton { Text = label, Pill = true, Primary = primary, Height = BtnH, DialogResult = result, TabStop = true };
            b.Width = Math.Max(88, TextRenderer.MeasureText(label, b.Font).Width + 42);
            list.Add(b);
        }
        return list;
    }

    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try { int on = 1; DwmSetWindowAttribute(Handle, 20, ref on, sizeof(int)); } catch { }            // dark immersive frame
        try { int round = Theme.DwmCorner(2); DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int)); } catch { }       // DWMWCP_ROUND
        try { int bc = Theme.DwmBorder(Theme.Border); DwmSetWindowAttribute(Handle, 34, ref bc, sizeof(int)); } catch { } // subtle border
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        if (Theme.Classic)
        {
            Theme.PaintClassicWindow(g, ClientRectangle, _caption, _titleFont);
            if (_icon != MessageBoxIcon.None) DrawClassicIcon(g, _iconRect);
            TextRenderer.DrawText(g, _message, _msgFont, _msgRect, Theme.TextCol, TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            return;
        }
        if (!Glass.PaintBackground(g, this, Glass.SurfaceTint)) g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = Theme.TextHint;

        if (_icon != MessageBoxIcon.None) DrawIcon(g, _iconRect);

        TextRenderer.DrawText(g, _caption, _titleFont, _titleRect, Theme.TextCol,
            TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(g, _message, _msgFont, _msgRect, Theme.Blend(Theme.TextCol, Theme.Subtle, 0.45),
            TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
    }

    /// <summary>The 95 message icons: a white speech balloon with a navy i or ? in it, a yellow warning
    /// triangle with a black !, and a red disc with a white cross for an error.</summary>
    private void DrawClassicIcon(Graphics g, Rectangle r)
    {
        var sm = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var black = new Pen(Theme.FaceDark, 1f);
        using var f = new Font("Times New Roman", 17f, FontStyle.Bold);
        var tf = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix;
        switch (_icon)
        {
            case MessageBoxIcon.Warning:
            {
                var tri = new[] { new PointF(r.X + r.Width / 2f, r.Y + 1), new PointF(r.Right - 1, r.Bottom - 2), new PointF(r.X + 1, r.Bottom - 2) };
                using (var yb = new SolidBrush(Color.FromArgb(255, 255, 0))) g.FillPolygon(yb, tri);
                g.DrawPolygon(black, tri);
                TextRenderer.DrawText(g, "!", f, new Rectangle(r.X, r.Y + 6, r.Width, r.Height - 6), Theme.FaceDark, tf);
                break;
            }
            case MessageBoxIcon.Error:
                using (var rb = new SolidBrush(Color.FromArgb(255, 0, 0))) g.FillEllipse(rb, r.X + 1, r.Y + 1, r.Width - 3, r.Height - 3);
                g.DrawEllipse(black, r.X + 1, r.Y + 1, r.Width - 3, r.Height - 3);
                using (var wp = new Pen(Color.White, 3f))
                {
                    float c = r.X + (r.Width - 1) / 2f, m = r.Y + (r.Height - 1) / 2f, k = 7f;
                    g.DrawLine(wp, c - k, m - k, c + k, m + k);
                    g.DrawLine(wp, c + k, m - k, c - k, m + k);
                }
                break;
            default:   // Information and Question: a speech balloon
            {
                var body = new Rectangle(r.X + 1, r.Y + 1, r.Width - 3, r.Height - 9);
                using var wb = new SolidBrush(Color.White);
                g.FillEllipse(wb, body);
                var tail = new[] { new PointF(r.X + 9, body.Bottom - 5), new PointF(r.X + 6, r.Bottom - 1), new PointF(r.X + 16, body.Bottom - 2) };
                g.FillPolygon(wb, tail);
                g.DrawEllipse(black, body);
                g.DrawLines(black, new[] { tail[0], tail[1], tail[2] });
                TextRenderer.DrawText(g, _icon == MessageBoxIcon.Question ? "?" : "i", f, body, Theme.ClassicNavy, tf);
                break;
            }
        }
        g.SmoothingMode = sm;
    }

    private void DrawIcon(Graphics g, Rectangle r)
    {
        (Color col, string glyph, float size) = _icon switch
        {
            MessageBoxIcon.Warning => (Color.FromArgb(226, 162, 70), "!", 18f),
            MessageBoxIcon.Error => (Theme.ErrorCol, "✕", 15f),
            MessageBoxIcon.Question => (Theme.Accent, "?", 16f),
            _ => (Theme.Accent, "i", 18f),   // Information / Asterisk / None-but-drawn
        };
        using (var b = new SolidBrush(col)) g.FillEllipse(b, r);
        double lum = (0.299 * col.R + 0.587 * col.G + 0.114 * col.B) / 255.0;
        Color gc = lum > 0.62 ? Theme.Bg : Color.White;     // dark glyph on light circles, white on dark
        using var f = Theme.DisplayFont(size, FontStyle.Bold);
        TextRenderer.DrawText(g, glyph, f, r, gc, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _titleFont.Dispose(); _msgFont.Dispose(); }
        base.Dispose(disposing);
    }
}
