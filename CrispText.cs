using System.Reflection;
using System.Runtime.InteropServices;

namespace iPodCommander;

/// <summary>
/// The Windows 95 look's type: what <see cref="TextRenderer"/> draws, without ClearType. 1995 had no font smoothing -
/// every glyph was solid pixels - and TextRenderer cannot be asked for that: it takes its font quality from the
/// Graphics' TextRenderingHint, and even SingleBitPerPixel maps to PROOF_QUALITY, which Windows still ClearTypes (the
/// blue and orange fringe round every letter). So in Classic this draws the way TextRenderer does - WinForms' own
/// DrawTextEx path ported (TextRenderer / TextExtensions / DeviceContextHdcScope / FontCache of WindowsDesktop 8): the
/// same flags, the same glyph-overhang margins, the same vertical alignment of wrapped text, the same clip and offset
/// handling, the same face, height and weight - with the font made at NONANTIALIASED_QUALITY. The glyph metrics do not
/// change, so no layout moves. In the modern look every call goes straight to TextRenderer.
/// </summary>
internal static class CrispText
{
    // ---- TextRenderer.DrawText's string overloads, with its defaults ----
    public static void DrawText(IDeviceContext dc, string? text, Font? font, Point pt, Color foreColor)
    {
        if (Theme.Classic) Draw(dc, text, font, new Rectangle(pt, MaxSize), foreColor, Color.Empty, TextFormatFlags.Default);
        else TextRenderer.DrawText(dc, text, font, pt, foreColor);
    }

    public static void DrawText(IDeviceContext dc, string? text, Font? font, Point pt, Color foreColor, Color backColor)
    {
        if (Theme.Classic) Draw(dc, text, font, new Rectangle(pt, MaxSize), foreColor, backColor, TextFormatFlags.Default);
        else TextRenderer.DrawText(dc, text, font, pt, foreColor, backColor);
    }

    public static void DrawText(IDeviceContext dc, string? text, Font? font, Point pt, Color foreColor, TextFormatFlags flags)
    {
        if (Theme.Classic) Draw(dc, text, font, new Rectangle(pt, MaxSize), foreColor, Color.Empty, flags);
        else TextRenderer.DrawText(dc, text, font, pt, foreColor, flags);
    }

    public static void DrawText(IDeviceContext dc, string? text, Font? font, Point pt, Color foreColor, Color backColor, TextFormatFlags flags)
    {
        if (Theme.Classic) Draw(dc, text, font, new Rectangle(pt, MaxSize), foreColor, backColor, flags);
        else TextRenderer.DrawText(dc, text, font, pt, foreColor, backColor, flags);
    }

    public static void DrawText(IDeviceContext dc, string? text, Font? font, Rectangle bounds, Color foreColor)
    {
        if (Theme.Classic) Draw(dc, text, font, bounds, foreColor, Color.Empty, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        else TextRenderer.DrawText(dc, text, font, bounds, foreColor);
    }

    public static void DrawText(IDeviceContext dc, string? text, Font? font, Rectangle bounds, Color foreColor, Color backColor)
    {
        if (Theme.Classic) Draw(dc, text, font, bounds, foreColor, backColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        else TextRenderer.DrawText(dc, text, font, bounds, foreColor, backColor);
    }

    public static void DrawText(IDeviceContext dc, string? text, Font? font, Rectangle bounds, Color foreColor, TextFormatFlags flags)
    {
        if (Theme.Classic) Draw(dc, text, font, bounds, foreColor, Color.Empty, flags);
        else TextRenderer.DrawText(dc, text, font, bounds, foreColor, flags);
    }

    public static void DrawText(IDeviceContext dc, string? text, Font? font, Rectangle bounds, Color foreColor, Color backColor, TextFormatFlags flags)
    {
        if (Theme.Classic) Draw(dc, text, font, bounds, foreColor, backColor, flags);
        else TextRenderer.DrawText(dc, text, font, bounds, foreColor, backColor, flags);
    }

    private static readonly Size MaxSize = new(int.MaxValue, int.MaxValue);

    // TextRenderer.DrawTextInternal + DeviceContextHdcScope: a Graphics' clip and offset reach the HDC only when asked for.
    private static void Draw(IDeviceContext dc, string? text, Font? font, Rectangle bounds, Color fore, Color back, TextFormatFlags flags)
    {
        ArgumentNullException.ThrowIfNull(dc);
        if (string.IsNullOrEmpty(text) || fore == Color.Transparent) return;
        var g = dc as Graphics;
        bool keepClip = g is not null && flags.HasFlag(TextFormatFlags.PreserveGraphicsClipping);
        bool keepOffset = g is not null && flags.HasFlag(TextFormatFlags.PreserveGraphicsTranslateTransform);
        Region? clip = null;
        PointF offset = default;
        if (keepClip) g!.GetContextInfo(out offset, out clip);
        else if (keepOffset) g!.GetContextInfo(out offset);
        IntPtr rgn = IntPtr.Zero;
        try
        {
            keepOffset &= !offset.IsEmpty;
            if (clip is not null && !clip.IsInfinite(g!)) rgn = clip.GetHrgn(g!);
            IntPtr hdc = dc.GetHdc();
            int saved = 0;
            try
            {
                if (rgn != IntPtr.Zero || keepOffset) saved = SaveDC(hdc);
                if (rgn != IntPtr.Zero)
                {
                    IntPtr current = CreateRectRgn(0, 0, 0, 0);
                    if (GetClipRgn(hdc, current) == 1) CombineRgn(rgn, current, rgn, 1 /* RGN_AND */);
                    DeleteObject(current);
                    SelectClipRgn(hdc, rgn);
                }
                if (keepOffset) OffsetViewportOrgEx(hdc, (int)offset.X, (int)offset.Y, IntPtr.Zero);
                DrawOnHdc(hdc, text, font, bounds, fore, back, flags);
            }
            finally
            {
                if (saved != 0) RestoreDC(hdc, saved);
                dc.ReleaseHdc();
            }
        }
        finally
        {
            if (rgn != IntPtr.Zero) DeleteObject(rgn);
            clip?.Dispose();
        }
    }

    // TextExtensions.DrawText (+ SplitTextFormatFlags, GetTextMargins, AdjustForVerticalAlignment).
    private static void DrawOnHdc(IntPtr hdc, string text, Font? font, Rectangle bounds, Color fore, Color back, TextFormatFlags flags)
    {
        uint fmt = (uint)flags;
        int padding = 0;   // 0 glyph-overhang (TextRenderer's default), 1 none, 2 left-and-right
        if ((fmt & 0xFF000000u) != 0)   // TextRenderer's own flags ride in the top byte and never reach DrawTextEx
        {
            padding = flags.HasFlag(TextFormatFlags.LeftAndRightPadding) ? 2 : flags.HasFlag(TextFormatFlags.NoPadding) ? 1 : 0;
            fmt &= 0x00FFFFFFu;
        }
        fmt &= ~0x10000u;   // DT_MODIFYSTRING: GDI must never write into a .NET string
        IntPtr hfont;
        int tmHeight;
        if (font is not null) hfont = Hfont(font, out tmHeight);
        else { hfont = GetCurrentObject(hdc, 6 /* OBJ_FONT */); tmHeight = TextHeight(hdc, hfont); }   // no font: the DC's own, as TextRenderer
        uint oldAlign = SetTextAlign(hdc, 0 /* TA_NOUPDATECP */);
        int oldColor = fore.IsEmpty ? -1 : SetTextColor(hdc, ColorTranslator.ToWin32(fore));
        IntPtr oldFont = SelectObject(hdc, hfont);
        bool opaque = !(back.IsEmpty || back == Color.Transparent);
        int oldMode = SetBkMode(hdc, opaque ? 2 /* OPAQUE */ : 1 /* TRANSPARENT */);
        int oldBack = opaque ? SetBkColor(hdc, ColorTranslator.ToWin32(back)) : 0;
        try
        {
            var p = new DRAWTEXTPARAMS { cbSize = (uint)Marshal.SizeOf<DRAWTEXTPARAMS>() };
            if (padding != 1)
            {
                float overhang = tmHeight / 6f;
                p.iLeftMargin = (int)Math.Ceiling(padding == 2 ? 2f * overhang : overhang);
                p.iRightMargin = (int)Math.Ceiling(overhang * (padding == 2 ? 2.5f : 1.5f));
            }
            // DrawText centres or bottoms only a SINGLE line; for wrapped text the box is measured and moved
            const uint VCenter = 0x4, Bottom = 0x8, SingleLine = 0x20, CalcRect = 0x400;
            if ((fmt & (VCenter | Bottom)) != 0 && (fmt & (SingleLine | CalcRect)) == 0)
            {
                var measure = new RECT(bounds);
                int h = DrawTextExW(hdc, text, text.Length, ref measure, fmt | CalcRect, ref p);
                if (h <= bounds.Height) bounds.Y = (fmt & VCenter) != 0 ? bounds.Top + bounds.Height / 2 - h / 2 : bounds.Bottom - h;
            }
            if (bounds.Width == int.MaxValue) bounds.Width -= bounds.X;
            if (bounds.Height == int.MaxValue) bounds.Height -= bounds.Y;
            var r = new RECT(bounds);
            DrawTextExW(hdc, text, text.Length, ref r, fmt, ref p);
        }
        finally
        {
            if (opaque) SetBkColor(hdc, oldBack);
            SetBkMode(hdc, oldMode);
            SelectObject(hdc, oldFont);
            if (oldColor != -1) SetTextColor(hdc, oldColor);
            SetTextAlign(hdc, oldAlign);
        }
    }

    // ---- the crisp twin of a Font ----
    private static readonly Dictionary<(string Face, float Size, FontStyle Style, byte CharSet), (IntPtr Font, int Height)> Fonts = new();
    private static readonly object FontsLock = new();
    private static int _dpi;

    /// <summary>The HFONT TextRenderer would make for <paramref name="font"/> (WinForms' FontCache.FromFont: the family
    /// name, -ceil(dpi * points / 72) high, weight, styles, charset, OUT_TT_PRECIS) at NONANTIALIASED_QUALITY, and its
    /// tmHeight. Cached for the life of the app and never deleted, so a native control may keep it (WM_SETFONT does
    /// not take ownership).</summary>
    internal static IntPtr Hfont(Font font, out int tmHeight)
    {
        string face = font.FontFamily.Name;
        if (face.Length > 1 && face[0] == '@') face = face[1..];
        var key = (face, font.SizeInPoints, font.Style, font.GdiCharSet);
        lock (FontsLock)
        {
            if (!Fonts.TryGetValue(key, out var entry))
            {
                IntPtr screen = GetDC(IntPtr.Zero);
                try
                {
                    if (_dpi == 0) _dpi = GetDeviceCaps(screen, 90 /* LOGPIXELSY */);
                    var lf = new LOGFONT
                    {
                        lfHeight = -(int)Math.Ceiling(_dpi * font.SizeInPoints / 72f),
                        lfWeight = font.Bold ? 700 : 400,
                        lfItalic = font.Italic ? (byte)1 : (byte)0,
                        lfUnderline = font.Underline ? (byte)1 : (byte)0,
                        lfStrikeOut = font.Strikeout ? (byte)1 : (byte)0,
                        lfCharSet = font.GdiCharSet,
                        lfOutPrecision = 3,   // OUT_TT_PRECIS
                        lfQuality = 3,        // NONANTIALIASED_QUALITY - the whole point
                        lfFaceName = face.Length == 0 ? "Microsoft Sans Serif" : face,
                    };
                    IntPtr h = CreateFontIndirectW(ref lf);
                    if (h == IntPtr.Zero) { lf.lfFaceName = "Microsoft Sans Serif"; lf.lfOutPrecision = 7; /* OUT_TT_ONLY_PRECIS */ h = CreateFontIndirectW(ref lf); }
                    entry = (h, TextHeight(screen, h));
                    Fonts[key] = entry;
                }
                finally { ReleaseDC(IntPtr.Zero, screen); }
            }
            tmHeight = entry.Height;
            return entry.Font;
        }
    }

    private static int TextHeight(IntPtr hdc, IntPtr hfont)
    {
        IntPtr old = SelectObject(hdc, hfont);
        GetTextMetricsW(hdc, out var tm);
        SelectObject(hdc, old);
        return tm.tmHeight;
    }

    // ---- interop ----
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
        public RECT(Rectangle r) { Left = r.Left; Top = r.Top; Right = r.Right; Bottom = r.Bottom; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DRAWTEXTPARAMS { public uint cbSize; public int iTabLength, iLeftMargin, iRightMargin; public uint uiLengthDrawn; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct LOGFONT
    {
        public int lfHeight, lfWidth, lfEscapement, lfOrientation, lfWeight;
        public byte lfItalic, lfUnderline, lfStrikeOut, lfCharSet, lfOutPrecision, lfClipPrecision, lfQuality, lfPitchAndFamily;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string lfFaceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TEXTMETRIC
    {
        public int tmHeight, tmAscent, tmDescent, tmInternalLeading, tmExternalLeading, tmAveCharWidth, tmMaxCharWidth,
            tmWeight, tmOverhang, tmDigitizedAspectX, tmDigitizedAspectY;
        public char tmFirstChar, tmLastChar, tmDefaultChar, tmBreakChar;
        public byte tmItalic, tmUnderlined, tmStruckOut, tmPitchAndFamily, tmCharSet;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int DrawTextExW(IntPtr hdc, string text, int length, ref RECT rect, uint format, ref DRAWTEXTPARAMS p);
    [DllImport("gdi32.dll")] private static extern uint SetTextAlign(IntPtr hdc, uint align);
    [DllImport("gdi32.dll")] private static extern int SetTextColor(IntPtr hdc, int color);
    [DllImport("gdi32.dll")] private static extern int SetBkColor(IntPtr hdc, int color);
    [DllImport("gdi32.dll")] private static extern int SetBkMode(IntPtr hdc, int mode);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern IntPtr GetCurrentObject(IntPtr hdc, uint type);
    [DllImport("gdi32.dll")] private static extern int SaveDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool RestoreDC(IntPtr hdc, int saved);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int l, int t, int r, int b);
    [DllImport("gdi32.dll")] private static extern int GetClipRgn(IntPtr hdc, IntPtr rgn);
    [DllImport("gdi32.dll")] private static extern int CombineRgn(IntPtr dst, IntPtr a, IntPtr b, int mode);
    [DllImport("gdi32.dll")] private static extern int SelectClipRgn(IntPtr hdc, IntPtr rgn);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool OffsetViewportOrgEx(IntPtr hdc, int x, int y, IntPtr previous);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateFontIndirectW(ref LOGFONT lf);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern bool GetTextMetricsW(IntPtr hdc, out TEXTMETRIC tm);
    [DllImport("gdi32.dll")] private static extern int GetDeviceCaps(IntPtr hdc, int index);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("user32.dll")] internal static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
}

/// <summary>A Label whose text, in the Windows 95 look, is drawn crisp (<see cref="CrispText"/>) - in the rectangle and
/// with the flags the Label itself uses (its own CreateTextFormatFlags, read by reflection). An image, a disabled label
/// or GDI+ text rendering keeps the Label's own paint.</summary>
internal class CrispLabel : Label
{
    private static readonly MethodInfo? FlagsOf = typeof(Label).GetMethod("CreateTextFormatFlags", BindingFlags.Instance | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
    private static readonly FieldInfo? ShowToolTip = typeof(Label).GetField("_showToolTip", BindingFlags.Instance | BindingFlags.NonPublic);

    protected override void OnPaint(PaintEventArgs e)
    {
        if (!Theme.Classic || FlagsOf is null || UseCompatibleTextRendering || Image is not null || !Enabled) { base.OnPaint(e); return; }
        var c = ClientRectangle;
        var face = new Rectangle(c.X + Padding.Left, c.Y + Padding.Top, c.Width - Padding.Horizontal, c.Height - Padding.Vertical);
        if (AutoEllipsis && ShowToolTip is not null)   // the Label's own "show the whole text on hover" when it is cut short
        {
            var want = GetPreferredSize(new Size(c.Width, c.Height));
            ShowToolTip.SetValue(this, c.Width < want.Width || c.Height < want.Height);
        }
        CrispText.DrawText(e.Graphics, Text, Font, face, ForeColor, (TextFormatFlags)FlagsOf.Invoke(this, null)!);
    }
}

/// <summary>A TextBox whose type, in the Windows 95 look, is crisp: the native edit control draws with whatever font it
/// is handed, so after WinForms hands it the ClearType one it gets <see cref="CrispText"/>'s twin (same metrics).</summary>
internal class CrispTextBox : TextBox
{
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Crisp(); }
    protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); Crisp(); }

    private void Crisp()
    {
        if (Theme.Classic && IsHandleCreated) CrispText.SendMessage(Handle, 0x0030 /* WM_SETFONT */, CrispText.Hfont(Font, out _), (IntPtr)1);
    }
}
