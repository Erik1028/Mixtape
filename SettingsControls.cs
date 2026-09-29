using System.Drawing.Drawing2D;

namespace iPodCommander;

/// <summary>An iOS-style on/off switch, painted in the theme accent.</summary>
internal sealed class ToggleSwitch : Control
{
    public event Action? CheckedChanged;
    private bool _checked;
    private float _t;        // animated knob position: 0 = off, 1 = on
    private bool _painted;   // suppresses the slide on the initial (programmatic) value
    private Tween? _tw;
    public bool Checked { get => _checked; set { if (_checked == value) return; _checked = value; AnimateKnob(); CheckedChanged?.Invoke(); } }

    public ToggleSwitch()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Size = new Size(46, 26);
        Cursor = Theme.Classic ? Cursors.Default : Cursors.Hand;
        Click += (_, _) => Checked = !Checked;
    }

    /// <summary>
    /// 1995 had no switches: an on/off setting was a CHECK BOX - a 13 px sunken white square with the era's
    /// three-pixel-thick tick in it. It sits at the right of the switch's bounds, where the switch was, so
    /// every settings row lines up exactly as before.
    /// </summary>
    private string? _classicLabel;
    private Font? _classicFont;

    /// <summary>Classic: a 1995 check box carries its own label, to the RIGHT of the box, and clicking the words
    /// ticks it too. Null = the bare box at the right of the bounds (a modern-layout row).</summary>
    public string? ClassicLabel
    {
        get => _classicLabel;
        set { _classicLabel = value; _classicFont ??= Theme.UiFont(Theme.SzBody); Invalidate(); }
    }

    /// <summary>The size a labelled Classic check box needs.</summary>
    public Size ClassicLabelSize() =>
        new(20 + TextRenderer.MeasureText(_classicLabel ?? "", _classicFont ??= Theme.UiFont(Theme.SzBody)).Width + 2, 18);

    private void PaintClassic(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.None;
        g.Clear(Parent?.BackColor ?? Theme.Face);
        if (_classicLabel is not null)
        {
            Theme.ClassicCheckBox(g, new Rectangle(0, (Height - 13) / 2, 13, 13), _checked, Enabled);
            TextRenderer.DrawText(g, _classicLabel, _classicFont!, new Rectangle(19, 0, Width - 19, Height), Enabled ? Theme.TextCol : Theme.FaceShadow,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            return;
        }
        Theme.ClassicCheckBox(g, new Rectangle(Width - 13 - 2, (Height - 13) / 2, 13, 13), _checked, Enabled);
    }

    private void AnimateKnob()
    {
        float to = _checked ? 1f : 0f;
        if (!_painted || !Anim.MotionEnabled) { _t = to; Invalidate(); return; }
        _tw?.Cancel();
        float from = _t;
        _tw = Anim.Run(190, v => { _t = from + (float)((to - from) * v); if (!IsDisposed) Invalidate(); }, null, Easings.OutBack);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        _painted = true;
        var g = e.Graphics;
        if (Theme.Classic) { PaintClassic(g); return; }
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (!Glass.PaintBackground(g, this, Glass.SurfaceTint)) g.Clear(Parent?.BackColor ?? Theme.PanelBg);
        float tc = Math.Clamp(_t, 0f, 1f);
        var r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
        using (var track = Theme.RoundedRect(r, (Height - 1) / 2f))
        using (var b = new SolidBrush(Theme.Blend(Theme.Blend(Theme.PanelBg, Color.White, 0.14), Theme.Accent, tc)))
            g.FillPath(b, track);
        int d = Height - 8;
        float x = 4 + (Width - d - 8) * _t;   // slides between the off/on insets (OutBack adds a tiny overshoot)
        using var knob = new SolidBrush(Theme.Blend(Color.FromArgb(220, 225, 230), Theme.OnAccent, tc));
        g.FillEllipse(knob, x, 4, d, d);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _tw?.Cancel(); _classicFont?.Dispose(); }
        base.Dispose(disposing);
    }
}

/// <summary>A row of accent swatches (presets + a custom colour picker), the current one ringed.</summary>
/// <summary>A control whose painted content stops short of its own edge (room for a ring or a shadow),
/// so a right-aligned layout must push it out by that much for its VISIBLE edge to line up.</summary>
internal interface IEdgeInset { int RightInset { get; } }

internal sealed class AccentPicker : Control, IEdgeInset
{
    public event Action<string>? AccentChosen; // preset name or "#RRGGBB"
    private string _current;
    private int _hover = -1;
    // D = dot diameter, Gap = space between dots, Pad = top/bottom/left margin that gives the selection
    // ring (which sits OUTSIDE the dot) room to draw — without it the ring's bottom clipped the control.
    private const int D = 22, Gap = 9, Pad = 7;

    public AccentPicker(string current)
    {
        _current = current;
        DoubleBuffered = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Height = D + Pad * 2;                                    // full room for the ring, top and bottom
        Width = (CountSwatches - 1) * (D + Gap) + D + Pad * 2;   // last dot + its ring room lines up on the right edge
        Cursor = Cursors.Hand;
        Click += OnClick;
        MouseMove += (_, e) => { int h = HitAt(e.X); if (h != _hover) { _hover = h; Invalidate(); } };
        MouseLeave += (_, _) => { if (_hover != -1) { _hover = -1; Invalidate(); } };
    }

    /// <summary>The ring room after the last dot: the row lines the DOTS up with the other controls.</summary>
    public int RightInset => Pad;

    private int CountSwatches => Theme.AccentPresets.Length + 1; // + custom
    private int SwatchX(int i) => Pad + i * (D + Gap);
    private int HitAt(int mouseX)
    {
        for (int i = 0; i < CountSwatches; i++) { int x = SwatchX(i); if (mouseX >= x - Gap / 2 && mouseX < x + D + Gap / 2) return i; }
        return -1;
    }

    private void OnClick(object? sender, EventArgs e)
    {
        if (e is not MouseEventArgs me) return;
        int i = HitAt(me.X);
        if (i < 0 || i >= CountSwatches) return;
        if (i < Theme.AccentPresets.Length) { _current = Theme.AccentPresets[i].Name; AccentChosen?.Invoke(_current); Invalidate(); }
        else
        {
            using var dlg = new ColorDialog { FullOpen = true };
            if (AppSettings.TryParseHex(_current, out var c0)) dlg.Color = c0;
            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                _current = $"#{dlg.Color.R:X2}{dlg.Color.G:X2}{dlg.Color.B:X2}";
                AccentChosen?.Invoke(_current);
                Invalidate();
            }
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (!Glass.PaintBackground(g, this, Glass.SurfaceTint)) g.Clear(Parent?.BackColor ?? Theme.PanelBg);

        bool customSelected = _current.StartsWith('#');
        for (int i = 0; i < Theme.AccentPresets.Length; i++)
        {
            var (name, color) = Theme.AccentPresets[i];
            DrawSwatch(g, i, color, !customSelected && _current == name, addButton: false);
        }
        Color custom = AppSettings.TryParseHex(_current, out var cc) ? cc : Theme.Blend(Theme.PanelBg, Color.White, 0.16);
        DrawSwatch(g, Theme.AccentPresets.Length, custom, customSelected, addButton: !customSelected);
    }

    private void DrawSwatch(Graphics g, int i, Color color, bool selected, bool addButton)
    {
        int x = SwatchX(i), y = Pad;
        float cx = x + D / 2f, cy = y + D / 2f;
        bool hover = _hover == i;

        if (addButton)
        {
            // "Add custom colour": a dashed-feel outlined ring + a crisp vector "+" (a font glyph fringes on dark).
            float t = hover ? 0.46f : 0.34f;
            using var ring = new Pen(Theme.Blend(Theme.PanelBg, Color.White, t), 1.5f);
            g.DrawEllipse(ring, x + 0.75f, y + 0.75f, D - 1.5f, D - 1.5f);
            using var plus = new Pen(Theme.Blend(Theme.PanelBg, Color.White, hover ? 0.78f : 0.62f), 1.7f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            float r = D * 0.20f;
            g.DrawLine(plus, cx - r, cy, cx + r, cy);
            g.DrawLine(plus, cx, cy - r, cx, cy + r);
            return;
        }

        using (var b = new SolidBrush(color)) g.FillEllipse(b, x, y, D, D);
        // A faint dark rim so light swatches don't melt into the panel.
        using (var rim = new Pen(Color.FromArgb(45, 0, 0, 0), 1f)) g.DrawEllipse(rim, x + 0.5f, y + 0.5f, D - 1, D - 1);

        if (selected)
        {
            // Selection ring drawn in the swatch's OWN (brightened) colour with a clean dark gap — it reads
            // as "selected" and stays in the palette instead of a clashing white outline.
            using var pen = new Pen(Theme.Blend(color, Color.White, 0.40f), 2f);
            g.DrawEllipse(pen, x - 3.5f, y - 3.5f, D + 7, D + 7);
        }
        else if (hover)
        {
            // A whisper ring on hover hints the dot is clickable.
            using var pen = new Pen(Theme.Blend(color, Color.White, 0.18f), 1.6f);
            g.DrawEllipse(pen, x - 3f, y - 3f, D + 6, D + 6);
        }
    }
}

/// <summary>A Windows-11-Settings-style left category rail: icon + label rows, accent selection pill.</summary>
internal sealed class SettingsNav : Panel
{
    public event Action<int>? Selected;
    private readonly string[] _labels;
    private int _sel;
    private int _hover = -1;
    private float _visSel;    // animated position of the selection pill
    private bool _painted;
    private Tween? _tw;
    private readonly List<Rectangle> _hit = new();
    private const int RowH = 40, Gap = 4, Pad = 10, TopPad = 14;
    // Cached once — OnPaint runs on every hover/selection change; allocating a Font per paint leaks GDI.
    private readonly Font _font = Theme.UiFont(Theme.SzTitle);
    private readonly Font _fontBold = Theme.UiFont(Theme.SzTitle, FontStyle.Bold);

    public SettingsNav(string[] labels)
    {
        _labels = labels;
        DoubleBuffered = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.SidebarBg;
        MouseMove += (_, e) => { int h = HitAt(e.Location); if (h != _hover) { _hover = h; Invalidate(); } };
        MouseLeave += (_, _) => { if (_hover != -1) { _hover = -1; Invalidate(); } };
        MouseClick += (_, e) => { int h = HitAt(e.Location); if (h >= 0) SelectedIndex = h; };
    }

    public int SelectedIndex
    {
        get => _sel;
        set { if (_sel == value) return; _sel = value; AnimateSel(value); Selected?.Invoke(value); }
    }

    private void AnimateSel(int to)
    {
        if (!_painted || !Anim.MotionEnabled) { _visSel = to; Invalidate(); return; }
        _tw?.Cancel();
        float from = _visSel;
        _tw = Anim.Run(220, v => { _visSel = from + (float)((to - from) * v); if (!IsDisposed) Invalidate(); }, null, Easings.OutCubic);
    }

    private int HitAt(Point p) { for (int i = 0; i < _hit.Count; i++) if (_hit[i].Contains(p)) return i; return -1; }

    protected override void OnPaint(PaintEventArgs e)
    {
        _painted = true;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (!Glass.PaintBackground(g, this, Glass.SurfaceTint)) g.Clear(Theme.SidebarBg);
        _hit.Clear();
        if (Theme.Classic) { PaintClassic(g); return; }

        // A single accent selection pill that slides between categories.
        {
            float y = TopPad + _visSel * (RowH + Gap);
            var selRow = new RectangleF(Pad, y, Width - Pad * 2, RowH);
            using var b = new SolidBrush(Color.FromArgb(48, Theme.Accent));
            using var p = Theme.RoundedRect(selRow, Theme.RadControl);
            g.FillPath(b, p);
        }

        for (int i = 0; i < _labels.Length; i++)
        {
            int y = TopPad + i * (RowH + Gap);
            var row = new Rectangle(Pad, y, Width - Pad * 2, RowH);
            _hit.Add(row);
            bool sel = i == _sel, hov = i == _hover;
            if (hov && !sel)
            {
                using var b = new SolidBrush(Theme.Blend(Theme.SidebarBg, Color.White, 0.06));
                using var p = Theme.RoundedRect(row, Theme.RadControl);
                g.FillPath(b, p);
            }
            var iconR = new Rectangle(row.X + 9, y + (RowH - 18) / 2, 18, 18);
            DrawCategoryIcon(g, iconR, i, sel ? Theme.AccentBright : Theme.Subtle);
            TextRenderer.DrawText(g, _labels[i], sel ? _fontBold : _font,
                new Rectangle(iconR.Right + 10, y, row.Right - iconR.Right - 16, RowH), sel ? Color.White : Theme.TextCol,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
    }

    /// <summary>The 95 category list: the chosen row is a solid navy band with white text, and it JUMPS there -
    /// list selection in 1995 did not glide.</summary>
    private void PaintClassic(Graphics g)
    {
        for (int i = 0; i < _labels.Length; i++)
        {
            int y = TopPad + i * (RowH + Gap);
            var row = new Rectangle(Pad, y, Width - Pad * 2, RowH);
            _hit.Add(row);
            bool sel = i == _sel;
            if (sel) using (var nb = new SolidBrush(Theme.ClassicNavy)) g.FillRectangle(nb, row);
            var iconR = new Rectangle(row.X + 9, y + (RowH - 18) / 2, 18, 18);
            DrawCategoryIcon(g, iconR, i, sel ? Color.White : Theme.TextCol);
            TextRenderer.DrawText(g, _labels[i], _font, new Rectangle(iconR.Right + 10, y, row.Right - iconR.Right - 16, RowH),
                sel ? Color.White : Theme.TextCol, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _tw?.Cancel(); _font.Dispose(); _fontBold.Dispose(); }
        base.Dispose(disposing);
    }

    /// <summary>Compact vector icons per settings category (Appearance/Library/Video/Photos/Safety/Device/About).</summary>
    private static void DrawCategoryIcon(Graphics g, Rectangle t, int kind, Color c)
    {
        float s = t.Width, x = t.X, y = t.Y;
        using var br = new SolidBrush(c);
        using var pen = new Pen(c, Math.Max(1.4f, s * 0.1f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        switch (kind)
        {
            case 0: // Appearance — overlapping colour swatches
                g.FillEllipse(br, x + s * 0.06f, y + s * 0.30f, s * 0.5f, s * 0.5f);
                using (var b2 = new SolidBrush(Color.FromArgb(150, c))) g.FillEllipse(b2, x + s * 0.42f, y + s * 0.16f, s * 0.5f, s * 0.5f);
                break;
            case 1: // Library — music note
                g.FillEllipse(br, x + s * 0.22f, y + s * 0.60f, s * 0.26f, s * 0.22f);
                g.FillRectangle(br, x + s * 0.44f, y + s * 0.20f, Math.Max(1.4f, s * 0.085f), s * 0.5f);
                g.FillEllipse(br, x + s * 0.60f, y + s * 0.50f, s * 0.26f, s * 0.22f);
                g.FillRectangle(br, x + s * 0.82f, y + s * 0.12f, Math.Max(1.4f, s * 0.085f), s * 0.42f);
                g.DrawLine(pen, x + s * 0.48f, y + s * 0.22f, x + s * 0.86f, y + s * 0.14f);
                break;
            case 2: // Video — play triangle in a rounded frame
                using (var fp = Theme.RoundedRect(new RectangleF(x + s * 0.12f, y + s * 0.18f, s * 0.76f, s * 0.64f), s * 0.14f)) g.DrawPath(pen, fp);
                g.FillPolygon(br, new[] { new PointF(x + s * 0.42f, y + s * 0.36f), new PointF(x + s * 0.42f, y + s * 0.64f), new PointF(x + s * 0.66f, y + s * 0.50f) });
                break;
            case 3: // Photos — landscape
                using (var fp = Theme.RoundedRect(new RectangleF(x + s * 0.14f, y + s * 0.20f, s * 0.72f, s * 0.60f), s * 0.12f)) g.DrawPath(pen, fp);
                g.FillEllipse(br, x + s * 0.26f, y + s * 0.30f, s * 0.14f, s * 0.14f);
                g.FillPolygon(br, new[] { new PointF(x + s * 0.18f, y + s * 0.76f), new PointF(x + s * 0.42f, y + s * 0.52f), new PointF(x + s * 0.58f, y + s * 0.64f), new PointF(x + s * 0.82f, y + s * 0.42f), new PointF(x + s * 0.82f, y + s * 0.76f) });
                break;
            case 4: // Safety — shield
                using (var sh = new GraphicsPath())
                {
                    sh.AddLines(new[] { new PointF(x + s * 0.5f, y + s * 0.12f), new PointF(x + s * 0.85f, y + s * 0.26f), new PointF(x + s * 0.85f, y + s * 0.52f), new PointF(x + s * 0.5f, y + s * 0.88f), new PointF(x + s * 0.15f, y + s * 0.52f), new PointF(x + s * 0.15f, y + s * 0.26f) });
                    sh.CloseFigure();
                    g.DrawPath(pen, sh);
                }
                break;
            case 5: // Device — iPod (rounded body + click wheel)
                using (var bp = Theme.RoundedRect(new RectangleF(x + s * 0.24f, y + s * 0.08f, s * 0.52f, s * 0.84f), s * 0.12f)) g.DrawPath(pen, bp);
                g.DrawEllipse(pen, x + s * 0.36f, y + s * 0.52f, s * 0.28f, s * 0.28f);
                break;
            default: // About — i in a circle
                g.DrawEllipse(pen, x + s * 0.14f, y + s * 0.14f, s * 0.72f, s * 0.72f);
                g.FillEllipse(br, x + s * 0.45f, y + s * 0.30f, s * 0.1f, s * 0.1f);
                g.FillRectangle(br, x + s * 0.45f, y + s * 0.46f, s * 0.1f, s * 0.26f);
                break;
        }
    }
}

/// <summary>A rounded settings "card" (PanelBg) that stacks label/control rows with hairline separators.</summary>
internal sealed class CardPanel : Panel
{
    // Fonts this card created (Theme.UiFont returns a fresh Font each call). Disposed with the card —
    // a user-assigned Control.Font is NOT freed by Control.Dispose, so without this they leak GDI on
    // every Settings category switch / Rebuild().
    private readonly List<Font> _fonts = new();
    private Font F(float size, FontStyle style = FontStyle.Regular) { var f = Theme.UiFont(size, style); _fonts.Add(f); return f; }

    private bool _finished;

    public CardPanel(int width)
    {
        Width = width;
        Height = Theme.Classic ? 2 : 0;   // Classic: the rows start inside the group box's 2 px etched frame, not on top of it
        BackColor = Theme.PanelBg;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        // Owner-painted rounded card: an anti-aliased FillPath (the old Region clip hard-aliased every corner).
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        // Glass card: a uniform sheet of frosted glass, no extra frame — the flyout's own edge plus the row
        // dividers already give structure, so an inner card outline just reads as a redundant box-in-a-box.
        if (Glass.PaintBackground(g, this, Glass.SurfaceTint)) return;
        if (Theme.Classic)
        {
            // a 95 group box: the window face inside an ETCHED frame (a shadow line with a highlight beside it)
            g.SmoothingMode = SmoothingMode.None;
            g.Clear(Theme.Face);
            using var sh = new Pen(Theme.FaceShadow);
            using var hi = new Pen(Theme.FaceHi);
            g.DrawRectangle(hi, 1, 1, Width - 2, Height - 2);
            g.DrawRectangle(sh, 0, 0, Width - 2, Height - 2);
            return;
        }
        // Opaque card (non-glass windows, e.g. Settings): rounded PanelBg fill; the transparent corners reveal
        // whatever sits behind the card.
        g.Clear(Parent?.BackColor ?? Theme.Bg);
        using var p = Theme.RoundedRect(new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), Theme.RadCard);
        using var b = new SolidBrush(Theme.PanelBg);
        g.FillPath(b, p);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) foreach (var f in _fonts) f.Dispose();
        base.Dispose(disposing);
    }

    /// <summary>Add a row: a left label (+ optional description) and an optional right-aligned control.</summary>
    /// <summary>The rule between two rows: a hairline, or in Classic the era's etched line (shadow over highlight).</summary>
    private void AddRule(int y)
    {
        var rule = new Panel { Height = 1, BackColor = Theme.Classic ? Theme.FaceShadow : Theme.HairLine, Left = 16, Width = Width - 32, Top = y };
        Controls.Add(rule);
        if (!Theme.Classic) return;
        // Classic: an etched rule, and it has to sit ABOVE the row labels that are added after it - they start at
        // the same y, so otherwise only the rule's two ends show, as a pair of stray dots.
        var light = new Panel { Height = 1, BackColor = Theme.FaceHi, Left = 16, Width = Width - 32, Top = y + 1 };
        Controls.Add(light);
        _rules.Add(rule); _rules.Add(light);
    }

    private readonly List<Control> _rules = new();

    protected override void OnControlAdded(ControlEventArgs e)
    {
        base.OnControlAdded(e);
        foreach (var r in _rules) r.BringToFront();
    }

    public void AddRow(string label, string? desc, Control? ctrl, int rowH = 56)
    {
        int y = Height;
        if (Controls.Count > 0) AddRule(y);

        // Size the label column from the control's actual left edge (not a fixed 240px reserve), so a
        // wide control (e.g. a 330px segmented control) never sits under the opaque label rectangle.
        const int labelLeft = 18, gap = 16, rightPad = 18;
        int inset = ctrl is IEdgeInset ei ? ei.RightInset : 0;   // the swatch row's ring room is not part of the picture
        int ctrlLeft = ctrl is not null ? Width - rightPad - ctrl.Width + inset : Width - rightPad;
        int labelW = Math.Max(80, ctrlLeft - gap - labelLeft);

        Controls.Add(new GlassLabel
        {
            Text = label,
            Font = F(10f),
            ForeColor = Theme.TextCol,
            BackColor = Theme.PanelBg,
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft,
            Left = labelLeft,
            Top = y,
            Width = labelW,
            Height = desc is null ? rowH : 32,
        });
        if (desc is not null)
            Controls.Add(new GlassLabel
            {
                Text = desc,
                Font = F(9f),
                ForeColor = Theme.Subtle,
                BackColor = Theme.PanelBg,
                AutoSize = false,
                TextAlign = ContentAlignment.TopLeft,
                Left = labelLeft,
                Top = y + 28,
                Width = labelW,
                Height = rowH - 30,
            });
        if (ctrl is not null)
        {
            ctrl.Top = y + (rowH - ctrl.Height) / 2;
            ctrl.Left = ctrlLeft;
            ctrl.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            Controls.Add(ctrl);
        }
        Height = y + rowH;
    }

    /// <summary>A read-only "label … value" row. The label is the bright anchor (matching control-row
    /// titles) and the value is the dim secondary element, so the scan direction matches every other page.</summary>
    public void AddInfoRow(string label, string value)
    {
        int y = Height;
        if (Controls.Count > 0) AddRule(y);
        Controls.Add(new GlassLabel { Text = label, Font = F(9.5f), ForeColor = Theme.TextCol, BackColor = Theme.PanelBg, AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, Left = 18, Top = y, Width = 200, Height = 38 });
        Controls.Add(new GlassLabel { Text = value, Font = F(9.5f), ForeColor = Theme.Subtle, BackColor = Theme.PanelBg, AutoSize = false, TextAlign = ContentAlignment.MiddleRight, Left = 210, Top = y, Width = Width - 210 - 18, Height = 38 });
        Height = y + 38;
    }

    public void Finish()
    {
        if (Theme.Classic && !_finished) Height += 2;   // and end inside it
        _finished = true;
        // The rounded card is now owner-painted (anti-aliased) in OnPaint — just trigger a repaint at
        // the final size. (Was a Region clip, which hard-aliased the corners.)
        Invalidate();
    }
}

/// <summary>
/// The Windows 95 property sheet: a row of tabs across the top and ONE raised page under them, the chosen tab
/// standing taller and joined to the page (the page's top edge stops under it). It paints only the tabs and the
/// page's frame; the page's content is whatever the host puts inside <see cref="PageRect"/>.
/// </summary>
internal sealed class ClassicPropertySheet : Panel
{
    public event Action<int>? Selected;
    private readonly string[] _tabs;
    private int _sel;
    private readonly List<Rectangle> _hit = new();
    private readonly Font _font = Theme.UiFont(Theme.SzBody);
    public const int TabH = 20;

    public ClassicPropertySheet(string[] tabs)
    {
        _tabs = tabs;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Face;
        MouseDown += (_, e) => { for (int i = 0; i < _hit.Count; i++) if (_hit[i].Contains(e.Location)) { SelectedIndex = i; return; } };
    }

    public int SelectedIndex
    {
        get => _sel;
        set { value = Math.Clamp(value, 0, _tabs.Length - 1); if (_sel == value) return; _sel = value; Invalidate(); Selected?.Invoke(value); }
    }

    /// <summary>The raised page under the tabs, in this control's coordinates.</summary>
    public Rectangle PageRect => new(0, TabH, Width, Math.Max(1, Height - TabH));

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.None;
        g.Clear(Theme.Face);
        _hit.Clear();
        int x = 2;
        var rects = new Rectangle[_tabs.Length];
        for (int i = 0; i < _tabs.Length; i++)
        {
            int w = TextRenderer.MeasureText(_tabs[i], _font).Width + 12;
            rects[i] = new Rectangle(x, 2, w, TabH - 2);
            _hit.Add(rects[i]);
            x += w;
        }
        Theme.Bevel(g, PageRect, raised: true);
        for (int i = 0; i < _tabs.Length; i++) if (i != _sel) Tab(g, rects[i], _tabs[i], false);
        if (_sel >= 0 && _sel < rects.Length)
        {
            var r = rects[_sel];
            Tab(g, new Rectangle(r.X - 2, 0, r.Width + 4, TabH + 1), _tabs[_sel], true);
        }
    }

    /// <summary>One tab: white on the left and the top (its two top corners cut by a pixel), grey and black on the
    /// right, no bottom edge - the chosen one is two pixels taller and wider, and wipes the page's edge under it.</summary>
    private void Tab(Graphics g, Rectangle r, string text, bool on)
    {
        using (var face = new SolidBrush(Theme.Face)) g.FillRectangle(face, r.X + 1, r.Y + 1, r.Width - 2, r.Height - 1);
        using var hi = new Pen(Theme.FaceHi);
        using var sh = new Pen(Theme.FaceShadow);
        using var dk = new Pen(Theme.FaceDark);
        int bottom = r.Bottom - 1;
        g.DrawLine(hi, r.X, bottom, r.X, r.Y + 2);
        g.DrawLine(hi, r.X + 1, r.Y + 1, r.X + 1, r.Y + 1);
        g.DrawLine(hi, r.X + 2, r.Y, r.Right - 3, r.Y);
        g.DrawLine(dk, r.Right - 1, r.Y + 2, r.Right - 1, bottom);
        g.DrawLine(dk, r.Right - 2, r.Y + 1, r.Right - 2, r.Y + 1);
        g.DrawLine(sh, r.Right - 2, r.Y + 2, r.Right - 2, bottom);
        var tr = new Rectangle(r.X, r.Y + (on ? 0 : 1), r.Width, r.Height - 2);
        TextRenderer.DrawText(g, text, _font, tr, Theme.TextCol, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _font.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>A 1995 group box: an etched frame with its caption sitting on the top line. Its children are the
/// group's controls, placed by the host.</summary>
internal sealed class ClassicGroupBox : Panel
{
    private readonly Font _font = Theme.UiFont(Theme.SzBody);

    public ClassicGroupBox()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Face;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Face);
        Theme.EtchedFrame(g, new Rectangle(0, 7, Width, Math.Max(2, Height - 7)));
        if (string.IsNullOrEmpty(Text)) return;
        var sz = TextRenderer.MeasureText(g, Text, _font, new Size(int.MaxValue, 16), TextFormatFlags.NoPrefix);
        var cap = new Rectangle(7, 0, Math.Min(Width - 14, sz.Width + 2), sz.Height);
        using (var face = new SolidBrush(Theme.Face)) g.FillRectangle(face, cap);
        TextRenderer.DrawText(g, Text, _font, cap, Theme.TextCol, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }

    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _font.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>A small segmented control (mutually-exclusive choices), accent-filled selection.</summary>
internal sealed class SegmentedControl : Control
{
    public event Action? SelectedChanged;
    private string[] _options = Array.Empty<string>();
    private int _selected;
    private int _hover = -1;

    private float _visSel;    // animated position of the selection pill
    private bool _painted;
    private Tween? _tw;

    public string[] Options { get => _options; set { _options = value; Invalidate(); } }
    public int SelectedIndex { get => _selected; set { if (_selected == value) return; _selected = value; AnimateSel(value); SelectedChanged?.Invoke(); } }

    private void AnimateSel(int to)
    {
        if (!_painted || !Anim.MotionEnabled) { _visSel = to; Invalidate(); return; }
        _tw?.Cancel();
        float from = _visSel;
        _tw = Anim.Run(220, v => { _visSel = from + (float)((to - from) * v); if (!IsDisposed) Invalidate(); }, null, Easings.OutCubic);
    }

    public SegmentedControl()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Height = 30;
        Cursor = Theme.Classic ? Cursors.Default : Cursors.Hand;
        Font = Theme.UiFont(9f, Theme.Classic ? FontStyle.Regular : FontStyle.Bold);
        MouseMove += (_, e) => { int h = SegAt(e.X); if (h != _hover) { _hover = h; Invalidate(); } };
        MouseLeave += (_, _) => { _hover = -1; Invalidate(); };
        Click += (_, e) => { if (e is MouseEventArgs me) { int s = Theme.Classic ? ClassicHit(me.Location) : SegAt(me.X); if (s >= 0) SelectedIndex = s; } };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _tw?.Cancel(); Font?.Dispose(); } // the ctor assigned a fresh Theme.UiFont; free it with the control
        base.Dispose(disposing);
    }

    private int SegW => _options.Length == 0 ? Width : Width / _options.Length;
    private int SegAt(int x) { int w = SegW; return w == 0 ? -1 : Math.Min(_options.Length - 1, Math.Max(0, x / w)); }

    /// <summary>The width a Classic radio group needs to show every option in full: one slot per option, each as
    /// wide as the longest (the control lays its options out in equal slots).</summary>
    public int ClassicRadioWidth() => Math.Max(1, _options.Length) * ClassicSlot;

    /// <summary>One option's slot in the Classic radio layout: the widest label, its radio and some air.</summary>
    private int ClassicSlot
    {
        get
        {
            int widest = 0;
            foreach (var o in _options) widest = Math.Max(widest, TextRenderer.MeasureText(o, Font).Width);
            return widest + 26;
        }
    }

    /// <summary>Classic: let the radio buttons run onto more lines. Only a host that sizes the control for it (the
    /// property sheet) turns this on; everywhere else the options share one line in equal slots.</summary>
    public bool ClassicWrap { get; set; }

    /// <summary>How many options fit on one line of <paramref name="width"/>, and how many lines that makes.</summary>
    public (int PerRow, int Rows) ClassicGrid(int width)
    {
        if (!ClassicWrap) return (Math.Max(1, _options.Length), 1);
        int per = Math.Max(1, Math.Min(Math.Max(1, _options.Length), width / Math.Max(1, ClassicSlot)));
        return (per, (Math.Max(1, _options.Length) + per - 1) / per);
    }

    private int ClassicHit(Point p)
    {
        var (per, _) = ClassicGrid(Width);
        int slot = Math.Max(1, Width / per), row = Math.Max(0, p.Y / 18);
        int i = row * per + Math.Min(per - 1, p.X / slot);
        return i >= 0 && i < _options.Length ? i : -1;
    }

    /// <summary>A 95 choice of a few is a row of RADIO BUTTONS - the round well with the black dot, the label to
    /// its right. (The latched-button row below is the toolbar's idiom; a dialog asked with option buttons.)</summary>
    private void PaintClassicRadios(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.None;
        g.Clear(Parent?.BackColor ?? Theme.Face);
        var (per, rows) = ClassicGrid(Width);
        int w = Math.Max(1, Width / per), lineH = rows > 1 ? 18 : Height;
        for (int i = 0; i < _options.Length; i++)
        {
            var seg = new Rectangle((i % per) * w, (i / per) * lineH, w, lineH);
            ClassicIcons.Draw(g, i == _selected ? ClassicIcons.Id.RadioOn : ClassicIcons.Id.RadioOff, seg.X, seg.Y + (lineH - 12) / 2);
            TextRenderer.DrawText(g, _options[i], Font, new Rectangle(seg.X + 17, seg.Y, seg.Width - 17, lineH), Theme.TextCol,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
    }

    /// <summary>A 95 choice of a few: a row of push buttons, the chosen one LATCHED - pushed in over the white
    /// dither the era used for a button that stays down (the toolbar's Bold / Italic / Underline).</summary>
    private void PaintClassic(Graphics g)
    {
        PaintClassicRadios(g);
    }

    private void PaintClassicLatched(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.None;
        g.Clear(Parent?.BackColor ?? Theme.Face);
        int n = Math.Max(1, _options.Length), w = Width / n;
        for (int i = 0; i < _options.Length; i++)
        {
            var seg = new Rectangle(i * w, 0, i == _options.Length - 1 ? Width - i * w : w, Height);
            bool on = i == _selected;
            if (on)
            {
                using var dither = new HatchBrush(HatchStyle.Percent50, Theme.FaceHi, Theme.Face);
                g.FillRectangle(dither, seg);
                Theme.Bevel(g, seg, raised: false);
            }
            else
            {
                using (var fb = new SolidBrush(i == _hover ? Theme.Blend(Theme.Face, Color.White, 0.22) : Theme.Face)) g.FillRectangle(fb, seg);
                Theme.Bevel(g, seg, raised: true);
            }
            var tr = seg;
            if (on) tr.Offset(1, 1);
            TextRenderer.DrawText(g, _options[i], Font, tr, Theme.TextCol,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        _painted = true;
        var g = e.Graphics;
        if (Theme.Classic) { PaintClassic(g); return; }
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (!Glass.PaintBackground(g, this, Glass.SurfaceTint)) g.Clear(Parent?.BackColor ?? Theme.PanelBg);
        var outer = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
        using var op = Theme.RoundedRect(outer, Theme.RadControl);
        using (var bg = new SolidBrush(Theme.Blend(Theme.PanelBg, Color.Black, 0.18))) g.FillPath(bg, op);

        // Clip fills to the track so segment corners can't spill outside the container's rounded corners.
        using var savedClip = g.Clip;
        g.SetClip(op, CombineMode.Intersect);
        int w = SegW;

        // Hover wash on a non-selected segment the mouse is over.
        if (_hover >= 0 && Math.Abs(_hover - _visSel) > 0.02f)
        {
            var hseg = new RectangleF(_hover * w + 2, 2, w - 4, Height - 4);
            using var hb = new SolidBrush(Theme.RowHover);
            using var hp = Theme.RoundedRect(hseg, Theme.RadChipInset);
            g.FillPath(hb, hp);
        }

        // A single accent pill that slides between segments.
        var sel = new RectangleF(_visSel * w + 2, 2, w - 4, Height - 4);
        using (var b = new SolidBrush(Theme.Accent))
        using (var p = Theme.RoundedRect(sel, Theme.RadChipInset))
            g.FillPath(b, p);

        for (int i = 0; i < _options.Length; i++)
        {
            var seg = new RectangleF(i * w + 2, 2, w - 4, Height - 4);
            float cover = Math.Max(0f, 1f - Math.Abs(_visSel - i)); // text crossfades to OnAccent as the pill arrives
            Color tc = Theme.Blend(Theme.TextCol, Theme.OnAccent, cover);
            TextRenderer.DrawText(g, _options[i], Font, Rectangle.Round(seg), tc,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
        g.Clip = savedClip;
    }
}
