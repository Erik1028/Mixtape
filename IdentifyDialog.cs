using System.Drawing.Drawing2D;

namespace iPodCommander;

/// <summary>
/// What the database thinks this file is. The closest match by length is picked for you, but nothing is
/// written until you say so - the host reads <see cref="Chosen"/> and does the writing.
/// </summary>
internal sealed class IdentifyDialog : CardDialog
{
    private readonly List<Identify.Candidate> _all;
    private readonly PickList _list;

    public Identify.Candidate? Chosen => _list.Index >= 0 && _list.Index < _all.Count ? _all[_list.Index] : null;

    public IdentifyDialog(string fileName, double fileSeconds, List<Identify.Candidate> candidates)
    {
        _all = candidates;
        Text = Loc.T("Identify song");
        StartPosition = FormStartPosition.CenterParent;
        int listH = Math.Clamp(candidates.Count * 40 + 14, 94, 320);
        ClientSize = new Size(600, listH + 146);
        BackColor = Theme.Bg;
        ForeColor = Theme.TextCol;
        Font = Theme.UiFont(9.5f);

        var file = new GlassLabel
        {
            Text = fileName,
            Font = Theme.UiFont(Theme.SzTitle, FontStyle.Bold),
            ForeColor = Theme.TextCol,
            AutoSize = false,
            AutoEllipsis = true,
        };
        file.SetBounds(22, 12, ClientSize.Width - 44, 22);
        Controls.Add(file);

        var hint = new GlassLabel
        {
            Text = candidates.Count == 0
                ? Loc.T("Nothing came back for this name. Renaming the file closer to the song's title usually helps.")
                : Loc.T("{0} long. The closest match by length is picked; only the title, artist and album are written.",
                        NowPlayingBar.Fmt(fileSeconds)),
            ForeColor = Theme.Faint,
            AutoSize = false,
        };
        hint.SetBounds(22, 36, ClientSize.Width - 44, 34);
        Controls.Add(hint);

        _list = new PickList(candidates);
        _list.SetBounds(20, 72, ClientSize.Width - 40, listH);
        _list.Chosen += () => { DialogResult = DialogResult.OK; Close(); };
        Controls.Add(_list);

        int by = 72 + listH + 20;
        var cancel = new ThemedButton { Text = Loc.T("Cancel"), Pill = true, Width = 96, Height = 32, Location = new Point(ClientSize.Width - 250, by), DialogResult = DialogResult.Cancel };
        var ok = new ThemedButton { Text = Loc.T("Write tags"), Primary = true, Pill = true, Width = 140, Height = 32, Location = new Point(ClientSize.Width - 140 - 20, by), DialogResult = DialogResult.OK, Enabled = candidates.Count > 0 };
        Controls.Add(cancel); Controls.Add(ok);
        AcceptButton = ok; CancelButton = cancel;
        AdoptCard();
    }

    /// <summary>One row per candidate: artist and title, the album under it, the length and how far it is.</summary>
    private sealed class PickList : Control
    {
        public event Action? Chosen;
        public int Index { get; private set; }
        private readonly List<Identify.Candidate> _c;
        private int _hover = -1, _scroll;
        private const int RowH = 40;
        private readonly Font _fTitle = Theme.UiFont(Theme.SzTitle, FontStyle.Bold);
        private readonly Font _fSub = Theme.UiFont(Theme.SzCaption);

        public PickList(List<Identify.Candidate> c)
        {
            _c = c;
            Index = c.Count > 0 ? 0 : -1;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            MouseMove += (_, e) => { int h = RowAt(e.Y); if (h != _hover) { _hover = h; Cursor = h >= 0 ? Cursors.Hand : Cursors.Default; Invalidate(); } };
            MouseLeave += (_, _) => { if (_hover >= 0) { _hover = -1; Cursor = Cursors.Default; Invalidate(); } };
            MouseWheel += (_, e) =>
            {
                int max = Math.Max(0, _c.Count * RowH - Height + 12);
                int v = Math.Clamp(_scroll - Math.Sign(e.Delta) * RowH * 2, 0, max);
                if (v != _scroll) { _scroll = v; Invalidate(); }
            };
            MouseClick += (_, e) => { int i = RowAt(e.Y); if (i >= 0) { Index = i; Invalidate(); } };
            MouseDoubleClick += (_, e) => { if (RowAt(e.Y) >= 0) Chosen?.Invoke(); };
        }

        private int RowAt(int y)
        {
            int i = (y - 6 + _scroll) / RowH;
            return i >= 0 && i < _c.Count ? i : -1;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            g.Clear(Parent?.BackColor ?? Theme.Bg);
            using (var b = new SolidBrush(Theme.PanelBg))
            using (var p = Theme.RoundedRect(new Rectangle(0, 0, Width, Height), Theme.RadCard)) g.FillPath(b, p);

            if (_c.Count == 0)
            {
                TextRenderer.DrawText(g, Loc.T("No match"), _fSub, new Rectangle(16, 0, Width - 32, Height), Theme.Faint,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                return;
            }

            var clip = g.Clip;
            g.SetClip(new Rectangle(0, 4, Width, Height - 8));
            for (int i = 0; i < _c.Count; i++)
            {
                int y = 6 + i * RowH - _scroll;
                if (y + RowH < 0 || y > Height) continue;
                var row = new Rectangle(6, y, Width - 12, RowH - 2);
                bool sel = i == Index;
                if (sel || i == _hover)
                {
                    using var hb = new SolidBrush(sel ? Color.FromArgb(48, Theme.Accent) : Theme.RowHover);
                    using var hp = Theme.RoundedRect(row, Theme.RadControl);
                    g.FillPath(hb, hp);
                }
                if (sel)
                {
                    using var bar = new SolidBrush(Theme.AccentBright);
                    using var bp = Theme.RoundedRect(new RectangleF(row.X + 3, row.Y + 7f, 3, row.Height - 14f), 1.5f);
                    g.FillPath(bar, bp);
                }
                var c = _c[i];
                TextRenderer.DrawText(g, c.Artist + "  —  " + c.Title, _fTitle, new Rectangle(row.X + 16, row.Y + 3, row.Width - 130, 18), Theme.TextCol,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
                TextRenderer.DrawText(g, c.Album.Length > 0 ? c.Album : Loc.T("(no album)"), _fSub, new Rectangle(row.X + 16, row.Y + 19, row.Width - 130, 16), Theme.Faint,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
                TextRenderer.DrawText(g, NowPlayingBar.Fmt(c.Seconds), _fTitle, new Rectangle(row.Right - 110, row.Y + 3, 92, 18), Theme.Subtle,
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                if (c.Delta > 0.9)
                    TextRenderer.DrawText(g, (c.Delta < 60 ? $"±{c.Delta:0} s" : "±" + NowPlayingBar.Fmt(c.Delta)), _fSub, new Rectangle(row.Right - 110, row.Y + 19, 92, 16),
                        c.Delta <= 10 ? Theme.Faint : Theme.Blend(Theme.Faint, Color.Red, 0.35),   // a few seconds apart is the same recording; a minute is not
                        TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }
            g.Clip = clip;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _fTitle.Dispose(); _fSub.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
