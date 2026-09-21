using System.Drawing.Drawing2D;

namespace iPodCommander;

/// <summary>
/// Shows what <see cref="TagTidy"/> found and lets the user tick what to fix. It decides nothing on its own:
/// the host reads <see cref="Selected"/> and does the writing, so a closed dialog has changed nothing.
/// </summary>
internal sealed class TagTidyDialog : CardDialog
{
    private readonly List<TagTidy.Group> _groups;
    private readonly bool[] _on;
    private readonly FixList _list;
    private readonly ThemedButton _apply;
    private readonly GlassLabel _intro = new();

    public IReadOnlyList<TagTidy.Group> Selected => _groups.Where((_, i) => _on[i]).ToList();

    public TagTidyDialog(List<TagTidy.Group> groups)
    {
        _groups = groups;
        _on = new bool[groups.Count];
        for (int i = 0; i < _on.Length; i++) _on[i] = true;   // everything found is worth fixing; untick the odd one out

        Text = Loc.T("Tidy tags");
        StartPosition = FormStartPosition.CenterParent;
        int listH = Math.Clamp(groups.Count * 38 + 14, 96, 300);   // the box fits what was found instead of gaping
        ClientSize = new Size(620, listH + 130);
        BackColor = Theme.Bg;
        ForeColor = Theme.TextCol;
        Font = Theme.UiFont(9.5f);

        _intro.Text = groups.Count == 0 ? Loc.T("Nothing to tidy — the tags are consistent.")
            : groups.Count == 1 ? Loc.T("Found one thing to tidy. The song keeps everything else.")
            : Loc.T("Found {0} things to tidy. Tick what Mixtape should fix; the songs keep everything else.", groups.Count);
        _intro.ForeColor = Theme.Subtle;
        _intro.AutoSize = false;
        _intro.SetBounds(22, 14, ClientSize.Width - 44, 36);
        Controls.Add(_intro);

        _list = new FixList(groups, _on);
        _list.SetBounds(20, 54, ClientSize.Width - 40, listH);
        _list.Changed += UpdateApply;
        Controls.Add(_list);

        int by = 54 + listH + 22;
        var cancel = new ThemedButton { Text = Loc.T("Cancel"), Pill = true, Width = 96, Height = 32, Location = new Point(ClientSize.Width - 240, by), DialogResult = DialogResult.Cancel };
        _apply = new ThemedButton { Text = Loc.T("Fix"), Primary = true, Pill = true, Width = 130, Height = 32, Location = new Point(ClientSize.Width - 130 - 20, by), DialogResult = DialogResult.OK };
        Controls.Add(cancel); Controls.Add(_apply);
        CancelButton = cancel;
        UpdateApply();
        AdoptCard();
    }

    private void UpdateApply()
    {
        int songs = _groups.Where((_, i) => _on[i]).Sum(g => g.Tracks.Count);
        _apply.Text = songs == 0 ? Loc.T("Fix") : songs == 1 ? Loc.T("Fix 1 song") : Loc.T("Fix {0} songs", songs);
        _apply.Enabled = songs > 0;
    }

    /// <summary>One row per proposal: a tick, the field, the old value, the new one, and how many songs it touches.</summary>
    private sealed class FixList : Control
    {
        public event Action? Changed;
        private readonly List<TagTidy.Group> _g;
        private readonly bool[] _on;
        private int _hover = -1, _scroll;
        private const int RowH = 38;
        private readonly Font _fField = Theme.UiFont(Theme.SzLabel, FontStyle.Bold);
        private readonly Font _fFrom = Theme.UiFont(Theme.SzTitle);
        private readonly Font _fTo = Theme.UiFont(Theme.SzTitle, FontStyle.Bold);
        private readonly Font _fCount = Theme.UiFont(Theme.SzCaption, FontStyle.Bold);

        public FixList(List<TagTidy.Group> groups, bool[] on)
        {
            _g = groups; _on = on;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            MouseMove += (_, e) => { int h = RowAt(e.Y); if (h != _hover) { _hover = h; Cursor = h >= 0 ? Cursors.Hand : Cursors.Default; Invalidate(); } };
            MouseLeave += (_, _) => { if (_hover >= 0) { _hover = -1; Cursor = Cursors.Default; Invalidate(); } };
            MouseWheel += (_, e) =>
            {
                int max = Math.Max(0, _g.Count * RowH - Height + 12);
                int v = Math.Clamp(_scroll - Math.Sign(e.Delta) * RowH * 2, 0, max);
                if (v != _scroll) { _scroll = v; Invalidate(); }
            };
            MouseClick += (_, e) =>
            {
                int i = RowAt(e.Y);
                if (i < 0) return;
                _on[i] = !_on[i];
                Changed?.Invoke();
                Invalidate();
            };
        }

        private int RowAt(int y)
        {
            int i = (y - 6 + _scroll) / RowH;
            return i >= 0 && i < _g.Count ? i : -1;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            g.Clear(Parent?.BackColor ?? Theme.Bg);
            using (var b = new SolidBrush(Theme.PanelBg))
            using (var p = Theme.RoundedRect(new Rectangle(0, 0, Width, Height), Theme.RadCard)) g.FillPath(b, p);

            if (_g.Count == 0)
            {
                TextRenderer.DrawText(g, Loc.T("Nothing to tidy — the tags are consistent."), _fFrom, new Rectangle(16, 0, Width - 32, Height), Theme.Faint,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                return;
            }

            var clip = g.Clip;
            g.SetClip(new Rectangle(0, 4, Width, Height - 8));
            for (int i = 0; i < _g.Count; i++)
            {
                int y = 6 + i * RowH - _scroll;
                if (y + RowH < 0 || y > Height) continue;
                var row = new Rectangle(6, y, Width - 12, RowH - 2);
                if (i == _hover)
                {
                    using var hb = new SolidBrush(Theme.RowHover);
                    using var hp = Theme.RoundedRect(row, Theme.RadControl);
                    g.FillPath(hb, hp);
                }
                DrawTick(g, new Rectangle(row.X + 10, row.Y + (row.Height - 18) / 2, 18, 18), _on[i]);

                var gr = _g[i];
                TextRenderer.DrawText(g, TagTidy.Label(gr.What).ToUpperInvariant(), _fField, new Rectangle(row.X + 38, row.Y, 84, row.Height), Theme.Faint,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);

                int x = row.X + 126, right = row.Right - 56;
                int half = Math.Max(40, (right - x - 26) / 2);
                string from = gr.Spacing ? "“" + gr.From + "”" : gr.From;
                string to = gr.Spacing ? "“" + gr.To + "”" : gr.To;
                TextRenderer.DrawText(g, from, _fFrom, new Rectangle(x, row.Y, half, row.Height), Theme.Subtle,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
                TextRenderer.DrawText(g, "→", _fFrom, new Rectangle(x + half + 2, row.Y, 22, row.Height), Theme.Faint,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                TextRenderer.DrawText(g, to, _fTo, new Rectangle(x + half + 26, row.Y, right - x - half - 26, row.Height), Theme.TextCol,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
                TextRenderer.DrawText(g, gr.Tracks.Count.ToString(), _fCount, new Rectangle(row.Right - 48, row.Y, 36, row.Height), Theme.Faint,
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }
            g.Clip = clip;

            if (_g.Count * RowH > Height)   // the same slim bar the other lists use
            {
                int track = Height - 12, content = _g.Count * RowH + 12;
                int th = Math.Max(30, track * Height / content);
                int ty = 6 + (track - th) * _scroll / Math.Max(1, content - Height);
                using var sb = new SolidBrush(Color.FromArgb(46, 255, 255, 255));
                using var sp = Theme.RoundedRect(new Rectangle(Width - 9, ty, 5, th), 2.5f);
                g.FillPath(sb, sp);
            }
        }

        private static void DrawTick(Graphics g, Rectangle r, bool on)
        {
            using var p = Theme.RoundedRect(r, 5);
            if (on)
            {
                using var fill = new SolidBrush(Theme.Accent);
                g.FillPath(fill, p);
                using var pen = new Pen(Theme.OnAccent, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawLines(pen, new[] { new PointF(r.X + 4.5f, r.Y + 9f), new PointF(r.X + 7.5f, r.Y + 12.5f), new PointF(r.X + 13.5f, r.Y + 5.5f) });
            }
            else
            {
                using var pen = new Pen(Theme.Blend(Theme.PanelBg, Color.White, 0.25), 1.6f);
                g.DrawPath(pen, p);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _fField.Dispose(); _fFrom.Dispose(); _fTo.Dispose(); _fCount.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
