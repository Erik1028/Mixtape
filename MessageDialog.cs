using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace iPodCommander;

/// <summary>
/// A borderless, DWM-rounded, fully themed stand-in for <see cref="MessageBox"/>. It mirrors the
/// <c>MessageBox.Show(owner, text, caption, buttons, icon)</c> shape so call sites only swap the type
/// name. Owner-drawn dark card with a themed status glyph, a title, a wrapped message, and
/// <see cref="ThemedButton"/>s mapped from the requested button set (primary/default on the right).
/// </summary>
internal sealed class MessageDialog : Form
{
    public static DialogResult Show(IWin32Window? owner, string text, string caption,
        MessageBoxButtons buttons = MessageBoxButtons.OK, MessageBoxIcon icon = MessageBoxIcon.None)
    {
        using var dlg = new MessageDialog(text ?? "", caption ?? "", buttons, icon);
        return owner is not null ? dlg.ShowDialog(owner) : dlg.ShowDialog();
    }

    private readonly string _caption, _message;
    private readonly MessageBoxIcon _icon;
    private Rectangle _iconRect, _titleRect, _msgRect;
    private readonly Font _titleFont, _msgFont;
    private ThemedButton? _default;

    private const int Pad = 24, IconSize = 38, IconGap = 16, BtnH = 34, BtnGap = 10, TitleGap = 6, MsgBtnGap = 22, MaxW = 480;

    private MessageDialog(string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon)
    {
        _message = text;
        _caption = string.IsNullOrWhiteSpace(caption) ? "Mixtape" : caption;
        _icon = icon;
        _titleFont = Theme.DisplayFont(14f, FontStyle.Bold);
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

        _default = btns.FirstOrDefault(b => b.Primary) ?? btns.LastOrDefault();
        AcceptButton = _default;
        CancelButton = btns.FirstOrDefault(b => b.DialogResult == DialogResult.Cancel)
                    ?? btns.FirstOrDefault(b => b.DialogResult == DialogResult.No)
                    ?? btns.FirstOrDefault(b => b.DialogResult == DialogResult.OK);

        // Let the user drag the card by its body (it has no title bar).
        MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) { try { ReleaseCapture(); SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero); } catch { } } };
        Shown += (_, _) => _default?.Focus();
    }

    private List<ThemedButton> BuildButtons(MessageBoxButtons buttons)
    {
        var defs = buttons switch
        {
            MessageBoxButtons.OKCancel => new[] { (Loc.T("Cancel"), DialogResult.Cancel, false), (Loc.T("OK"), DialogResult.OK, true) },
            MessageBoxButtons.YesNo => new[] { (Loc.T("No"), DialogResult.No, false), (Loc.T("Yes"), DialogResult.Yes, true) },
            MessageBoxButtons.YesNoCancel => new[] { (Loc.T("Cancel"), DialogResult.Cancel, false), (Loc.T("No"), DialogResult.No, false), (Loc.T("Yes"), DialogResult.Yes, true) },
            MessageBoxButtons.RetryCancel => new[] { (Loc.T("Cancel"), DialogResult.Cancel, false), (Loc.T("Retry"), DialogResult.Retry, true) },
            _ => new[] { (Loc.T("OK"), DialogResult.OK, true) },
        };
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
        try { int round = 2; DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int)); } catch { }       // DWMWCP_ROUND
        try { int bc = Theme.Border.R | (Theme.Border.G << 8) | (Theme.Border.B << 16); DwmSetWindowAttribute(Handle, 34, ref bc, sizeof(int)); } catch { } // subtle border
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        if (_icon != MessageBoxIcon.None) DrawIcon(g, _iconRect);

        TextRenderer.DrawText(g, _caption, _titleFont, _titleRect, Theme.TextCol,
            TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(g, _message, _msgFont, _msgRect, Theme.Blend(Theme.TextCol, Theme.Subtle, 0.45),
            TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
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
