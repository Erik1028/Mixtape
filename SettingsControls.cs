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
        Cursor = Cursors.Hand;
        Click += (_, _) => Checked = !Checked;
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
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Theme.PanelBg);
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
        if (disposing) _tw?.Cancel();
        base.Dispose(disposing);
    }
}

/// <summary>A row of accent swatches (presets + a custom colour picker), the current one ringed.</summary>
internal sealed class AccentPicker : Control
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
        g.Clear(Parent?.BackColor ?? Theme.PanelBg);

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
    private readonly Font _font = Theme.UiFont(9.5f);
    private readonly Font _fontBold = Theme.UiFont(9.5f, FontStyle.Bold);

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
        g.Clear(Theme.SidebarBg);
        _hit.Clear();

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

    public CardPanel(int width)
    {
        Width = width;
        Height = 0;
        BackColor = Theme.PanelBg;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        // Owner-painted rounded card: an anti-aliased FillPath (the old Region clip hard-aliased every
        // corner). The transparent corners outside the path reveal whatever sits behind the card.
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Bg);
        g.SmoothingMode = SmoothingMode.AntiAlias;
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
    public void AddRow(string label, string? desc, Control? ctrl, int rowH = 56)
    {
        int y = Height;
        if (Controls.Count > 0)
            Controls.Add(new Panel { Height = 1, BackColor = Theme.HairLine, Left = 16, Width = Width - 32, Top = y });

        // Size the label column from the control's actual left edge (not a fixed 240px reserve), so a
        // wide control (e.g. a 330px segmented control) never sits under the opaque label rectangle.
        const int labelLeft = 18, gap = 16, rightPad = 18;
        int ctrlLeft = ctrl is not null ? Width - rightPad - ctrl.Width : Width - rightPad;
        int labelW = Math.Max(80, ctrlLeft - gap - labelLeft);

        Controls.Add(new Label
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
            Controls.Add(new Label
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
        if (Controls.Count > 0)
            Controls.Add(new Panel { Height = 1, BackColor = Theme.HairLine, Left = 16, Width = Width - 32, Top = y });
        Controls.Add(new Label { Text = label, Font = F(9.5f), ForeColor = Theme.TextCol, BackColor = Theme.PanelBg, AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, Left = 18, Top = y, Width = 200, Height = 38 });
        Controls.Add(new Label { Text = value, Font = F(9.5f), ForeColor = Theme.Subtle, BackColor = Theme.PanelBg, AutoSize = false, TextAlign = ContentAlignment.MiddleRight, Left = 210, Top = y, Width = Width - 210 - 18, Height = 38 });
        Height = y + 38;
    }

    public void Finish()
    {
        // The rounded card is now owner-painted (anti-aliased) in OnPaint — just trigger a repaint at
        // the final size. (Was a Region clip, which hard-aliased the corners.)
        Invalidate();
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
        Cursor = Cursors.Hand;
        Font = Theme.UiFont(9f, FontStyle.Bold);
        MouseMove += (_, e) => { int h = SegAt(e.X); if (h != _hover) { _hover = h; Invalidate(); } };
        MouseLeave += (_, _) => { _hover = -1; Invalidate(); };
        Click += (_, e) => { if (e is MouseEventArgs me) { int s = SegAt(me.X); if (s >= 0) SelectedIndex = s; } };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _tw?.Cancel(); Font?.Dispose(); } // the ctor assigned a fresh Theme.UiFont; free it with the control
        base.Dispose(disposing);
    }

    private int SegW => _options.Length == 0 ? Width : Width / _options.Length;
    private int SegAt(int x) { int w = SegW; return w == 0 ? -1 : Math.Min(_options.Length - 1, Math.Max(0, x / w)); }

    protected override void OnPaint(PaintEventArgs e)
    {
        _painted = true;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Theme.PanelBg);
        var outer = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
        using var op = Theme.RoundedRect(outer, Theme.RadControl);
        using (var bg = new SolidBrush(Theme.Blend(Theme.PanelBg, Color.Black, 0.18))) g.FillPath(bg, op);

        // Clip fills to the track so segment corners can't spill outside the container's rounded corners.
        var savedClip = g.Clip;
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
