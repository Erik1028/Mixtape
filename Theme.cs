using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace iPodCommander;

/// <summary>
/// Dark, Apple-Music-inspired look for Mixtape: layered near-black surfaces (sidebar darker than
/// content), a teal accent, generated gradient artwork, pill buttons, hairline row separators.
/// Self-contained; uses the Prisma palette + Segoe UI Variable typography.
/// </summary>
internal static class Theme
{
    // Base surfaces are switchable via SetThemeVariant (the "Background" customization). Owner-painted
    // controls read these in OnPaint, so they update on Invalidate; the few BackColor-baked panels are
    // re-coloured by MainForm.RestyleEverything when the variant changes.
    public static Color Bg { get; private set; } = Color.FromArgb(29, 30, 34);        // content surface
    public static Color SidebarBg { get; private set; } = Color.FromArgb(22, 23, 26); // darker rail (≈8 luma below content)
    public static Color PanelBg { get; private set; } = Color.FromArgb(40, 43, 48);
    public static Color RowBg { get; private set; } = Color.FromArgb(40, 43, 48);
    public static Color RowHover { get; private set; } = Color.FromArgb(38, 42, 46);
    public static Color HairLine { get; private set; } = Color.FromArgb(38, 40, 45);  // row separators
    public static Color Accent { get; private set; } = Color.FromArgb(0, 200, 170);
    public static Color AccentBright { get; private set; } = Color.FromArgb(64, 210, 188);
    public static Color AccentDim { get; private set; } = Color.FromArgb(0, 110, 95);

    /// <summary>User-selectable accent colours (customization).</summary>
    public static readonly (string Name, Color Color)[] AccentPresets =
    {
        ("Teal", Color.FromArgb(0, 200, 170)),
        ("Blue", Color.FromArgb(42, 130, 246)),
        ("Indigo", Color.FromArgb(120, 110, 245)),
        ("Purple", Color.FromArgb(176, 92, 246)),
        ("Pink", Color.FromArgb(245, 92, 138)),
        ("Red", Color.FromArgb(240, 82, 76)),
        ("Orange", Color.FromArgb(255, 149, 56)),
        ("Green", Color.FromArgb(54, 200, 110)),
    };

    public static void SetAccent(Color c)
    {
        Accent = c;
        AccentBright = Blend(c, Color.White, 0.28);
        AccentDim = Color.FromArgb((int)(c.R * 0.55), (int)(c.G * 0.55), (int)(c.B * 0.55));
        // Pick legible pill text per accent luminance: dark text on bright accents, white on dark ones.
        OnAccent = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) > 130 ? Color.FromArgb(12, 16, 18) : Color.White;
    }

    public static void SetAccent(string nameOrHex)
    {
        if (nameOrHex.StartsWith('#') && AppSettings.TryParseHex(nameOrHex, out var custom)) { SetAccent(custom); return; }
        foreach (var p in AccentPresets) if (p.Name == nameOrHex) { SetAccent(p.Color); return; }
        SetAccent(AccentPresets[0].Color);
    }
    public static readonly Color TextCol = Color.FromArgb(242, 242, 243);
    public static readonly Color Subtle = Color.FromArgb(168, 172, 176);
    public static readonly Color Faint = Color.FromArgb(138, 143, 152);
    public static Color Border { get; private set; } = Color.FromArgb(44, 47, 49);
    public static readonly Color ErrorCol = Color.FromArgb(255, 120, 110);

    /// <summary>The selectable background palettes (the "Background" customization). Text stays light on all.</summary>
    public static readonly string[] ThemeVariants = { "Graphite", "Midnight", "Carbon", "Mocha", "Forest", "Plum" };

    public static void SetThemeVariant(string name)
    {
        (Bg, SidebarBg, PanelBg, RowHover, HairLine, Border) = name switch
        {
            "Midnight" => (Color.FromArgb(19, 24, 43), Color.FromArgb(13, 16, 32), Color.FromArgb(32, 40, 64), Color.FromArgb(28, 35, 58), Color.FromArgb(30, 37, 60), Color.FromArgb(38, 47, 76)),
            "Carbon"   => (Color.FromArgb(12, 12, 14), Color.FromArgb(3, 3, 4),     Color.FromArgb(26, 26, 30), Color.FromArgb(24, 24, 28), Color.FromArgb(26, 27, 31), Color.FromArgb(34, 35, 40)),
            "Mocha"    => (Color.FromArgb(36, 29, 25), Color.FromArgb(27, 20, 17),  Color.FromArgb(54, 43, 37), Color.FromArgb(50, 40, 34), Color.FromArgb(50, 41, 35), Color.FromArgb(62, 50, 43)),
            "Forest"   => (Color.FromArgb(20, 34, 28), Color.FromArgb(13, 24, 19),  Color.FromArgb(31, 49, 40), Color.FromArgb(27, 43, 35), Color.FromArgb(29, 45, 37), Color.FromArgb(39, 58, 48)),
            "Plum"     => (Color.FromArgb(33, 26, 42), Color.FromArgb(24, 18, 32),  Color.FromArgb(48, 39, 60), Color.FromArgb(43, 35, 54), Color.FromArgb(45, 37, 56), Color.FromArgb(57, 47, 71)),
            _          => (Color.FromArgb(29, 30, 34), Color.FromArgb(22, 23, 26),  Color.FromArgb(40, 43, 48), Color.FromArgb(38, 42, 46), Color.FromArgb(44, 46, 51), Color.FromArgb(44, 47, 49)),
        };
        RowBg = Blend(PanelBg, Color.White, 0.05); // a touch lighter than PanelBg so secondary buttons read as raised
    }
    public static Color OnAccent { get; private set; } = Color.FromArgb(8, 14, 13);

    private static readonly string TextFamily = FirstInstalled("Segoe UI Variable Text") ?? "Segoe UI";
    private static readonly string? TextSemibold = FirstInstalled("Segoe UI Variable Text Semibold");
    private static readonly string DisplayFamily = FirstInstalled("Segoe UI Variable Display") ?? "Segoe UI";
    private static readonly string? DisplaySemibold = FirstInstalled("Segoe UI Variable Display Semib");

    private static string? FirstInstalled(params string[] names)
    {
        try
        {
            using var installed = new System.Drawing.Text.InstalledFontCollection();
            var have = installed.Families.Select(f => f.Name).ToHashSet();
            return names.FirstOrDefault(have.Contains);
        }
        catch { return null; }
    }

    public static Font UiFont(float size, FontStyle style = FontStyle.Regular) =>
        style.HasFlag(FontStyle.Bold) && TextSemibold != null
            ? new Font(TextSemibold, size, style & ~FontStyle.Bold)
            : new Font(TextFamily, size, style);

    public static Font DisplayFont(float size, FontStyle style = FontStyle.Regular) =>
        style.HasFlag(FontStyle.Bold) && DisplaySemibold != null
            ? new Font(DisplaySemibold, size, style & ~FontStyle.Bold)
            : new Font(DisplayFamily, size, style);

    public static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        float d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>A crisp vector eighth-note, optically centred in <paramref name="r"/> (the head sits
    /// low-left and the flag top-right, so the whole mark is nudged right a hair to read centred).
    /// One shared placeholder mark for the idle player cover and generated album art.</summary>
    public static void DrawNote(Graphics g, RectangleF r, Color c)
    {
        var savedSmooth = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float s = Math.Min(r.Width, r.Height);
        float cx = r.X + r.Width / 2f + s * 0.03f;
        float cy = r.Y + r.Height / 2f;
        using var b = new SolidBrush(c);

        float headRx = s * 0.165f, headRy = s * 0.12f;
        float hx = cx - s * 0.11f, hy = cy + s * 0.17f;     // note head, low-left
        float stemW = s * 0.05f;
        float stemX = cx + s * 0.05f;                        // stem rises from the head's right side
        float stemTop = cy - s * 0.30f;

        using (var sp = RoundedRect(new RectangleF(stemX - stemW, stemTop, stemW, hy - stemTop), stemW / 2f))
            g.FillPath(b, sp);

        using (var flag = new GraphicsPath())               // a small filled flag off the stem top
        {
            flag.AddBezier(new PointF(stemX, stemTop),
                new PointF(stemX + s * 0.17f, stemTop + s * 0.05f),
                new PointF(stemX + s * 0.15f, stemTop + s * 0.20f),
                new PointF(stemX + s * 0.05f, stemTop + s * 0.23f));
            flag.AddBezier(new PointF(stemX + s * 0.05f, stemTop + s * 0.23f),
                new PointF(stemX + s * 0.10f, stemTop + s * 0.13f),
                new PointF(stemX + s * 0.085f, stemTop + s * 0.06f),
                new PointF(stemX, stemTop));
            flag.CloseFigure();
            g.FillPath(b, flag);
        }

        var st = g.Save();                                   // tilted oval head
        g.TranslateTransform(hx, hy);
        g.RotateTransform(-22f);
        g.FillEllipse(b, -headRx, -headRy, headRx * 2, headRy * 2);
        g.Restore(st);
        g.SmoothingMode = savedSmooth;
    }

    // ---- corner-radius scale (one language across the whole UI) ----
    public const int RadControl = 8;       // non-pill buttons, selection/hover pills, segmented outer track, hover chips
    public const int RadChipInset = 6;     // pills nested inside the segmented track only (= RadControl - 2)
    public const int RadCard = 14;         // inner content cards (CardPanel: Settings + device ABOUT/BACKUPS/OPTIONS)
    public const int RadShell = 16;        // top-level floating sidebar/content shells
    public const float TileFrac = 0.12f;   // cover-tile radius as a fraction of size (round(TileFrac*size))
    public const int RadTileSmall = 4;     // the tiny 18px sidebar mini-cover only
    public const float ArtAngle = 60f;     // single light direction for all diagonally-lit generated art

    public static Color Blend(Color a, Color b, double f) => Color.FromArgb(
        (int)(a.R + (b.R - a.R) * f), (int)(a.G + (b.G - a.G) * f), (int)(a.B + (b.B - a.B) * f));

    /// <summary>Draw an image into <paramref name="dest"/> at a fractional opacity (for cross-fades).</summary>
    public static void DrawImageAlpha(Graphics g, Image img, RectangleF dest, float alpha)
    {
        if (alpha <= 0.001f) return;
        if (alpha >= 0.999f) { g.DrawImage(img, dest.X, dest.Y, dest.Width, dest.Height); return; }
        var cm = new ColorMatrix { Matrix33 = alpha };
        using var ia = new ImageAttributes();
        ia.SetColorMatrix(cm);
        g.DrawImage(img, new[] { new PointF(dest.X, dest.Y), new PointF(dest.Right, dest.Y), new PointF(dest.X, dest.Bottom) },
            new RectangleF(0, 0, img.Width, img.Height), GraphicsUnit.Pixel, ia);
    }

    public static Color HsvToColor(double h, double s, double v)
    {
        h = (h % 360 + 360) % 360;
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        (double r, double g, double b) = ((int)(h / 60)) switch
        {
            0 => (c, x, 0.0), 1 => (x, c, 0.0), 2 => (0.0, c, x),
            3 => (0.0, x, c), 4 => (x, 0.0, c), _ => (c, 0.0, x)
        };
        return Color.FromArgb((int)((r + m) * 255), (int)((g + m) * 255), (int)((b + m) * 255));
    }

    public static int StableHash(string? s)
    {
        unchecked
        {
            int h = 17;
            foreach (char ch in s ?? "") h = h * 31 + ch;
            return h & 0x7FFFFFFF;
        }
    }

    /// <summary>
    /// Paints the window "wallpaper": a themed dark diagonal gradient with two soft accent glows, so the
    /// sidebar/content cards (laid out with margins + rounded corners) appear to float in front of it.
    /// Colours derive from the active variant + accent, so it re-themes with the Background setting.
    /// </summary>
    /// <summary>The colour at the very top of the wallpaper — also used for the window caption/border so
    /// the title bar melts into the app instead of reading as a separate bar.</summary>
    public static Color WallpaperTop => Blend(SidebarBg, Color.Black, 0.38);

    public static void PaintWallpaper(Graphics g, Rectangle r)
    {
        if (r.Width <= 0 || r.Height <= 0) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Color a0 = WallpaperTop;                                              // deep near-black base
        Color a1 = Blend(SidebarBg, Color.Black, 0.42);
        Color mid = Blend(Blend(SidebarBg, Color.Black, 0.30), Accent, 0.12); // subtle accent-tinted band (centred)
        Color a3 = Blend(SidebarBg, Color.Black, 0.52);
        using (var br = new LinearGradientBrush(r, a0, a3, Theme.ArtAngle))
        {
            br.InterpolationColors = new ColorBlend
            {
                Colors = new[] { a0, a1, mid, a3 },
                Positions = new[] { 0f, 0.30f, 0.62f, 1f },
            };
            g.FillRectangle(br, r);
        }
        Glow(g, r, r.Left - r.Width * 0.18f, r.Top - r.Height * 0.30f, Math.Max(r.Width, r.Height) * 0.95f, Color.FromArgb(55, Accent));
        Glow(g, r, r.Right - r.Width * 0.35f, r.Bottom - r.Height * 0.10f, Math.Max(r.Width, r.Height) * 0.85f, Color.FromArgb(40, HsvToColor((AccentHue() + 55) % 360, 0.55, 0.72)));
    }

    /// <summary>A soft drop shadow behind a floating card (painted on the wallpaper before the card draws).</summary>
    public static void PaintCardShadow(Graphics g, Rectangle card, int radius)
    {
        if (card.Width <= 0 || card.Height <= 0) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        for (int i = 7; i >= 1; i--)
        {
            using var p = RoundedRect(new RectangleF(card.X - i, card.Y - i + 4, card.Width + 2 * i, card.Height + 2 * i), radius + i);
            using var b = new SolidBrush(Color.FromArgb(7, 0, 0, 0));
            g.FillPath(b, p);
        }
    }

    private static void Glow(Graphics g, Rectangle clip, float cx, float cy, float d, Color color)
    {
        using var path = new GraphicsPath();
        path.AddEllipse(cx, cy, d, d);
        using var pgb = new PathGradientBrush(path)
        {
            CenterColor = color,
            SurroundColors = new[] { Color.FromArgb(0, color) },
            CenterPoint = new PointF(cx + d / 2, cy + d / 2),
        };
        var saved = g.Clip; g.SetClip(clip);
        g.FillPath(pgb, path);
        g.Clip = saved;
    }

    private static double AccentHue()
    {
        var c = Accent;
        double max = Math.Max(c.R, Math.Max(c.G, c.B)) / 255.0, min = Math.Min(c.R, Math.Min(c.G, c.B)) / 255.0, dl = max - min;
        if (dl < 0.001) return 180;
        double rr = c.R / 255.0, gg = c.G / 255.0, bb = c.B / 255.0, h;
        if (max == rr) h = (gg - bb) / dl % 6; else if (max == gg) h = (bb - rr) / dl + 2; else h = (rr - gg) / dl + 4;
        h *= 60; if (h < 0) h += 360; return h;
    }

    /// <summary>Set a control's region to a rounded rectangle (so its corners reveal the wallpaper behind).</summary>
    public static void RoundRegion(Control c, int radius)
    {
        if (c.Width <= 1 || c.Height <= 1) { c.Region = null; return; }
        var old = c.Region;
        c.Region = new Region(RoundedRect(new RectangleF(0, 0, c.Width, c.Height), radius));
        old?.Dispose();
    }

    private static readonly Dictionary<(int, int), Bitmap> ArtCache = new();

    /// <summary>Generated square artwork: a rounded teal-family gradient keyed by seed, a faint ♪, and a hairline inner frame.</summary>
    public static Bitmap MakeArt(int size, int seed)
    {
        var key = (size, seed);
        if (ArtCache.TryGetValue(key, out var cached)) return cached;

        var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            double h = 150 + (seed % 360) / 360.0 * 130; // teal→blue→violet family
            Color c1 = HsvToColor(h, 0.50, 0.56);
            Color c2 = HsvToColor(h + 24, 0.60, 0.34);
            float radius = Math.Max(3, size * Theme.TileFrac);
            using var path = RoundedRect(new RectangleF(0, 0, size - 1, size - 1), radius);
            using (var br = new LinearGradientBrush(new Rectangle(0, 0, size, size), c1, c2, Theme.ArtAngle))
            {
                g.SetClip(path);
                g.FillRectangle(br, 0, 0, size, size);
                g.ResetClip();
            }
            if (size >= 44)
                DrawNote(g, new RectangleF(0, 0, size, size), Color.FromArgb(64, 255, 255, 255));
            using (var ip = RoundedRect(new RectangleF(0.5f, 0.5f, size - 2, size - 2), radius))
            using (var pen = new Pen(Color.FromArgb(30, 255, 255, 255)))
                g.DrawPath(pen, ip);
        }
        ArtCache[key] = bmp;
        return bmp;
    }

    public static void StyleGrid(DataGridView g)
    {
        g.EnableHeadersVisualStyles = false;
        g.BackgroundColor = Bg;
        g.GridColor = Bg;
        g.BorderStyle = BorderStyle.None;
        g.CellBorderStyle = DataGridViewCellBorderStyle.None;
        g.Font = UiFont(10f);
        g.RowHeadersVisible = false;
        g.ColumnHeadersHeight = 34;
        g.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
        g.ColumnHeadersDefaultCellStyle.BackColor = Bg;
        g.ColumnHeadersDefaultCellStyle.ForeColor = Faint;   // recede headers into the tertiary tier (was Subtle)
        g.ColumnHeadersDefaultCellStyle.Font = UiFont(8.5f, FontStyle.Bold);
        g.ColumnHeadersDefaultCellStyle.SelectionBackColor = Bg;
        g.ColumnHeadersDefaultCellStyle.Padding = new Padding(8, 0, 4, 0);

        // No alternating band — Apple uses hairline separators (drawn in RowPostPaint) instead.
        g.DefaultCellStyle.BackColor = Bg;
        g.DefaultCellStyle.ForeColor = TextCol;
        g.DefaultCellStyle.SelectionBackColor = Blend(Bg, Accent, 0.12);   // whisper-tint; the accent bar carries selection
        g.DefaultCellStyle.SelectionForeColor = Color.White;
        g.DefaultCellStyle.Padding = new Padding(8, 0, 4, 0);
        g.AlternatingRowsDefaultCellStyle.BackColor = Bg;
        g.AlternatingRowsDefaultCellStyle.ForeColor = TextCol;
        g.AlternatingRowsDefaultCellStyle.SelectionBackColor = Blend(Bg, Accent, 0.12);
        g.AlternatingRowsDefaultCellStyle.SelectionForeColor = Color.White;
        g.RowTemplate.Height = 52;
        g.ColumnHeadersDefaultCellStyle.WrapMode = DataGridViewTriState.False;
    }
}

/// <summary>Flat, hover-aware button. Pill = fully rounded; Primary = accent fill; optional left glyph.</summary>
internal sealed class ThemedButton : Button
{
    public bool Primary { get; init; }
    public bool Pill { get; init; }
    public bool Ghost { get; init; }   // borderless icon button (no chip)
    private bool _danger;
    public bool Danger { get => _danger; set { if (_danger != value) { _danger = value; Invalidate(); } } }  // destructive: red label + tinted border
    public string? Glyph { get; init; }
    public enum Ico { None, Play, Settings }
    public Ico Icon { get; init; }   // crisp vector icon (Ghost buttons) instead of a symbol-font glyph
    protected override bool ShowFocusCues => false;   // no dotted focus rectangle on our custom buttons
    private float _hoverT;  // 0→1 hover wash
    private float _pressT;  // 0→1 press (insets the content → a scale-down that reads as a tap)
    private bool _painted;
    private Tween? _hoverTw, _pressTw;
    private string? _blockedReason;

    /// <summary>
    /// When set, the button paints as disabled but STILL accepts a click — which raises
    /// <see cref="BlockedClicked"/> (with this reason) instead of the normal Click. Lets a greyed-out
    /// action explain why it's unavailable instead of silently doing nothing. Null = behave normally.
    /// </summary>
    public string? BlockedReason
    {
        get => _blockedReason;
        set
        {
            var v = string.IsNullOrEmpty(value) ? null : value;
            if (v == _blockedReason) return;
            _blockedReason = v;
            if (v != null) { _hoverTw?.Cancel(); _pressTw?.Cancel(); _hoverT = 0f; _pressT = 0f; } // stop any motion so it reads as inert
            Invalidate();
        }
    }
    /// <summary>Raised when a soft-disabled (BlockedReason) button is clicked. Argument is the reason.</summary>
    public event Action<string>? BlockedClicked;
    private bool IsBlocked => _blockedReason != null;

    public ThemedButton()
    {
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        ForeColor = Theme.TextCol;
        Cursor = Cursors.Hand;
        Font = Theme.UiFont(9.5f, FontStyle.Bold);
        Height = 34;
        MouseEnter += (_, _) => { if (Enabled && !IsBlocked) AnimHover(1f); };
        MouseLeave += (_, _) => { AnimHover(0f); AnimPress(0f); };
        MouseDown += (_, me) => { if (me.Button == MouseButtons.Left && Enabled && !IsBlocked) AnimPress(1f); };
        MouseUp += (_, me) => { if (me.Button == MouseButtons.Left) AnimPress(0f); };
        EnabledChanged += (_, _) => Invalidate();
    }

    protected override void OnClick(EventArgs e)
    {
        // A soft-disabled button swallows the normal Click and explains itself instead.
        if (IsBlocked) { BlockedClicked?.Invoke(_blockedReason!); return; }
        base.OnClick(e);
    }

    private void AnimHover(float to)
    {
        if (!_painted || !Anim.MotionEnabled) { _hoverT = to; Invalidate(); return; }
        _hoverTw?.Cancel();
        float from = _hoverT;
        _hoverTw = Anim.Run(130, v => { _hoverT = from + (float)((to - from) * v); if (!IsDisposed) Invalidate(); }, null, Easings.OutCubic);
    }

    private void AnimPress(float to)
    {
        if (!_painted || !Anim.MotionEnabled) { _pressT = to; Invalidate(); return; }
        _pressTw?.Cancel();
        float from = _pressT;
        // press goes down fast; release eases back with a hint of spring
        _pressTw = Anim.Run(to > 0 ? 80 : 170, v => { _pressT = from + (float)((to - from) * v); if (!IsDisposed) Invalidate(); }, null, to > 0 ? Easings.OutCubic : Easings.OutBack);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        _painted = true;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Theme.Bg);

        float h = Math.Clamp(_hoverT, 0f, 1f);
        float inset = 3f * Math.Clamp(_pressT, 0f, 1f);   // shrink toward centre while pressed
        var r = new RectangleF(0.5f + inset, 0.5f + inset, Width - 1 - inset * 2, Height - 1 - inset * 2);
        float radius = Pill ? r.Height / 2f : Theme.RadControl;
        using var path = Theme.RoundedRect(r, radius);
        var textRect = Rectangle.Round(r);

        bool disabled = !Enabled || IsBlocked;

        if (Ghost)
        {
            if (h > 0.001f) { using var hb = new SolidBrush(Color.FromArgb((int)(h * 255), Theme.RowHover)); g.FillPath(hb, path); }
            Color ic = disabled ? Theme.Faint : Theme.Blend(Color.FromArgb(205, 210, 214), Theme.TextCol, h);
            if (Icon != Ico.None) DrawIcon(g, r, Icon, ic);
            else TextRenderer.DrawText(g, string.IsNullOrEmpty(Glyph) ? Text : Glyph, Font, textRect, ic,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }

        Color fill, text, border;
        if (disabled) { fill = Theme.RowBg; text = Theme.Faint; border = Theme.Border; }
        else if (Primary) { fill = Theme.Blend(Theme.Accent, Color.White, 0.14 * h); text = Theme.OnAccent; border = fill; }
        else if (Danger) { fill = Theme.Blend(Theme.RowBg, Theme.ErrorCol, 0.14 * h); text = Theme.ErrorCol; border = Theme.Blend(Theme.Border, Theme.ErrorCol, 0.55); }
        else { fill = Theme.Blend(Theme.RowBg, Theme.RowHover, h); text = Theme.TextCol; border = Theme.Border; }

        using (var b = new SolidBrush(fill)) g.FillPath(b, path);
        if (!Primary) using (var p = new Pen(border)) g.DrawPath(p, path);

        string label = string.IsNullOrEmpty(Glyph) ? Text : $"{Glyph}  {Text}";
        TextRenderer.DrawText(g, label, Font, textRect, text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    /// <summary>Crisp, perfectly-centred vector icon (symbol-font glyphs sit off-centre at this size).</summary>
    private static void DrawIcon(Graphics g, RectangleF r, Ico icon, Color c)
    {
        float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f;
        using var b = new SolidBrush(c);
        if (icon == Ico.Play)
        {
            float s = Math.Min(r.Width, r.Height) * 0.26f;
            // nudge the triangle a hair right so it reads optically centred
            g.FillPolygon(b, new[] { new PointF(cx - s * 0.78f, cy - s), new PointF(cx - s * 0.78f, cy + s), new PointF(cx + s * 1.02f, cy) });
        }
        else if (icon == Ico.Settings)
        {
            float ro = Math.Min(r.Width, r.Height) * 0.30f;   // tooth-tip radius
            float ri = ro * 0.74f;                             // valley radius
            const int teeth = 8;
            var poly = new PointF[teeth * 2];
            for (int i = 0; i < teeth * 2; i++)
            {
                double a = -Math.PI / 2 + i * Math.PI / teeth;
                float rad = (i % 2 == 0) ? ro : ri;
                poly[i] = new PointF(cx + (float)Math.Cos(a) * rad, cy + (float)Math.Sin(a) * rad);
            }
            using var gp = new GraphicsPath { FillMode = FillMode.Alternate };
            gp.AddPolygon(poly);
            float rh = ro * 0.38f;                             // centre hole (even-odd punches it out)
            gp.AddEllipse(cx - rh, cy - rh, rh * 2, rh * 2);
            g.FillPath(b, gp);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _hoverTw?.Cancel(); _pressTw?.Cancel(); }
        base.Dispose(disposing);
    }
}
