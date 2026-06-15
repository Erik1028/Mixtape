using System.Drawing.Drawing2D;

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
    public static readonly string[] ThemeVariants = { "Graphite", "Midnight", "Carbon", "Mocha" };

    public static void SetThemeVariant(string name)
    {
        (Bg, SidebarBg, PanelBg, RowHover, HairLine, Border) = name switch
        {
            "Midnight" => (Color.FromArgb(19, 24, 43), Color.FromArgb(13, 16, 32), Color.FromArgb(32, 40, 64), Color.FromArgb(28, 35, 58), Color.FromArgb(30, 37, 60), Color.FromArgb(38, 47, 76)),
            "Carbon"   => (Color.FromArgb(12, 12, 14), Color.FromArgb(3, 3, 4),     Color.FromArgb(26, 26, 30), Color.FromArgb(24, 24, 28), Color.FromArgb(26, 27, 31), Color.FromArgb(34, 35, 40)),
            "Mocha"    => (Color.FromArgb(36, 29, 25), Color.FromArgb(27, 20, 17),  Color.FromArgb(54, 43, 37), Color.FromArgb(50, 40, 34), Color.FromArgb(50, 41, 35), Color.FromArgb(62, 50, 43)),
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

    public static Color Blend(Color a, Color b, double f) => Color.FromArgb(
        (int)(a.R + (b.R - a.R) * f), (int)(a.G + (b.G - a.G) * f), (int)(a.B + (b.B - a.B) * f));

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
        Color top = WallpaperTop;                                        // deep near-black base
        Color mid = Blend(Blend(SidebarBg, Color.Black, 0.30), Accent, 0.16); // subtle accent-tinted band
        Color bot = Blend(SidebarBg, Color.Black, 0.50);
        using (var br = new LinearGradientBrush(r, top, bot, 58f))
        {
            br.InterpolationColors = new ColorBlend
            {
                Colors = new[] { top, mid, bot },
                Positions = new[] { 0f, 0.45f, 1f },
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
            double h = 150 + (seed % 360) / 360.0 * 130; // teal→blue→violet family
            Color c1 = HsvToColor(h, 0.46, 0.50);
            Color c2 = HsvToColor(h + 26, 0.58, 0.30);
            float radius = Math.Max(3, size * 0.13f);
            using var path = RoundedRect(new RectangleF(0, 0, size - 1, size - 1), radius);
            using (var br = new LinearGradientBrush(new Rectangle(0, 0, size, size), c1, c2, 55f))
            {
                g.SetClip(path);
                g.FillRectangle(br, 0, 0, size, size);
                g.ResetClip();
            }
            if (size >= 44)
            {
                using var f = new Font(TextFamily, size * 0.42f, FontStyle.Bold);
                using var tb = new SolidBrush(Color.FromArgb(54, 255, 255, 255));
                var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString("♪", f, tb, new RectangleF(0, 0, size, size), sf);
            }
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
        g.ColumnHeadersDefaultCellStyle.ForeColor = Subtle;
        g.ColumnHeadersDefaultCellStyle.Font = UiFont(8.5f, FontStyle.Bold);
        g.ColumnHeadersDefaultCellStyle.SelectionBackColor = Bg;
        g.ColumnHeadersDefaultCellStyle.Padding = new Padding(8, 0, 4, 0);

        // No alternating band — Apple uses hairline separators (drawn in RowPostPaint) instead.
        g.DefaultCellStyle.BackColor = Bg;
        g.DefaultCellStyle.ForeColor = TextCol;
        g.DefaultCellStyle.SelectionBackColor = Blend(Bg, Accent, 0.22);
        g.DefaultCellStyle.SelectionForeColor = Color.White;
        g.DefaultCellStyle.Padding = new Padding(8, 0, 4, 0);
        g.AlternatingRowsDefaultCellStyle.BackColor = Bg;
        g.AlternatingRowsDefaultCellStyle.ForeColor = TextCol;
        g.AlternatingRowsDefaultCellStyle.SelectionBackColor = Blend(Bg, Accent, 0.22);
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
    public string? Glyph { get; init; }
    private bool _hover;

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
        MouseEnter += (_, _) => { _hover = true; Invalidate(); };
        MouseLeave += (_, _) => { _hover = false; Invalidate(); };
        EnabledChanged += (_, _) => Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Theme.Bg);

        var r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
        float radius = Pill ? (Height - 1) / 2f : 7f;
        using var path = Theme.RoundedRect(r, radius);

        if (Ghost)
        {
            if (_hover) { using var hb = new SolidBrush(Theme.RowHover); g.FillPath(hb, path); }
            TextRenderer.DrawText(g, string.IsNullOrEmpty(Glyph) ? Text : Glyph, Font, Rectangle.Round(r),
                _hover ? Theme.TextCol : Color.FromArgb(205, 210, 214), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }

        Color fill, text, border;
        if (!Enabled) { fill = Theme.RowBg; text = Theme.Faint; border = Theme.Border; }
        else if (Primary) { fill = _hover ? Theme.Blend(Theme.Accent, Color.White, 0.14) : Theme.Accent; text = Theme.OnAccent; border = fill; }
        else { fill = _hover ? Theme.RowHover : Theme.RowBg; text = Theme.TextCol; border = Theme.Border; }

        using (var b = new SolidBrush(fill)) g.FillPath(b, path);
        if (!Primary) using (var p = new Pen(border)) g.DrawPath(p, path);

        string label = string.IsNullOrEmpty(Glyph) ? Text : $"{Glyph}  {Text}";
        TextRenderer.DrawText(g, label, Font, Rectangle.Round(r), text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }
}
