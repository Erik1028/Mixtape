namespace iPodCommander;

/// <summary>
/// A themed tooltip: a small dark chip in the app's own colours that appears under a control (or an owner-drawn
/// region) after a short hover, instead of the system's yellow box. One shared window, never activated, so the
/// window under the cursor keeps its focus.
/// </summary>
internal static class Tip
{
    private sealed class TipForm : Form
    {
        public string Caption = "";
        private readonly Font _f = Theme.UiFont(9f);

        public TipForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Theme.Blend(Theme.PanelBg, Color.White, 0.08);
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        }

        protected override bool ShowWithoutActivation => true;
        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.ExStyle |= 0x08000000 | 0x00000080; return cp; }   // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW
        }

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try { int dark = 1; DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int)); } catch { }
            try { int round = 3; DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int)); } catch { }   // DWMWCP_ROUNDSMALL
            try { int bc = Theme.Border.R | (Theme.Border.G << 8) | (Theme.Border.B << 16); DwmSetWindowAttribute(Handle, 34, ref bc, sizeof(int)); } catch { }
        }

        /// <summary>Re-read the palette. The window is created once and kept, so a theme change would
        /// otherwise leave the tooltip painted in the old colours until the app restarts.</summary>
        public void Restyle()
        {
            BackColor = Theme.Blend(Theme.PanelBg, Color.White, 0.08);
            if (IsHandleCreated)
                try { int bc = Theme.Border.R | (Theme.Border.G << 8) | (Theme.Border.B << 16); DwmSetWindowAttribute(Handle, 34, ref bc, sizeof(int)); } catch { }
            Invalidate();
        }

        public Size Measure()
        {
            var s = TextRenderer.MeasureText(Caption, _f, new Size(420, 100), TextFormatFlags.NoPrefix);
            return new Size(s.Width + 18, s.Height + 12);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            TextRenderer.DrawText(g, Caption, _f, ClientRectangle, Theme.TextCol, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }
    }

    private static TipForm? _form;
    private static readonly System.Windows.Forms.Timer _delay = new() { Interval = 550 };
    private static Rectangle _anchor;
    private static string _text = "";

    static Tip() { _delay.Tick += (_, _) => { _delay.Stop(); ShowNow(); }; }

    /// <summary>Show <paramref name="text"/> under <paramref name="anchorScreen"/> after the hover delay. Re-arming with
    /// another text replaces the pending one; while a tip is already up it moves straight over. Empty text = none.</summary>
    public static void Arm(Rectangle anchorScreen, string text)
    {
        if (string.IsNullOrEmpty(text)) { Disarm(); return; }
        if (_form is { Visible: true } && _text == text && _anchor == anchorScreen) return;
        _anchor = anchorScreen; _text = text;
        if (_form is { Visible: true }) { ShowNow(); return; }
        _delay.Stop(); _delay.Start();
    }

    public static void Disarm()
    {
        _delay.Stop();
        if (_form is { Visible: true }) _form.Hide();
    }

    private static void ShowNow()
    {
        try
        {
            _form ??= new TipForm();
            _form.Restyle();   // the chip outlives a theme change: its colours are re-read every time it appears
            _form.Caption = _text;
            var sz = _form.Measure();
            var scr = Screen.FromPoint(new Point(_anchor.X, _anchor.Y)).WorkingArea;
            int x = Math.Clamp(_anchor.X + _anchor.Width / 2 - sz.Width / 2, scr.Left + 4, Math.Max(scr.Left + 4, scr.Right - sz.Width - 4));
            int y = _anchor.Bottom + 6;
            if (y + sz.Height > scr.Bottom - 4) y = _anchor.Top - sz.Height - 6;
            _form.Bounds = new Rectangle(x, y, sz.Width, sz.Height);
            if (!_form.Visible) _form.Show(); else _form.Invalidate();
        }
        catch { /* a tip is never worth an error */ }
    }

    /// <summary>Render harness: show the chip at once (no hover delay) and hand back its window to capture.</summary>
    internal static Form PreviewShow(Rectangle anchorScreen, string text) { _anchor = anchorScreen; _text = text; ShowNow(); return _form!; }

    /// <summary>Attach a hover tip to a control; the text is evaluated when it is about to appear (empty = no tip).</summary>
    public static void Attach(Control c, Func<string> text)
    {
        c.MouseEnter += (_, _) => Arm(c.RectangleToScreen(c.ClientRectangle), text());
        c.MouseLeave += (_, _) => Disarm();
        c.MouseDown += (_, _) => Disarm();
        c.VisibleChanged += (_, _) => { if (!c.Visible) Disarm(); };
    }
}
