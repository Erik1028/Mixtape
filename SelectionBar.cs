using System.Drawing.Drawing2D;

namespace iPodCommander;

/// <summary>
/// The floating action bar over the song list while several rows are selected (Apple Music / Finder): the count, then
/// the actions a selection is most often made for. The host positions it at the bottom of the list and calls
/// <see cref="Reveal"/>; it slides up into place. Transparent outside its pill, so the list shows around the corners.
/// </summary>
internal sealed class SelectionBar : Control
{
    public event Action? AddToPlaylist, PlayNext, AddToQueue, Delete, Clear;
    public const int H = 42;
    private const int Pad = 18, BtnH = 26, Gap = 8, CloseD = 22;

    private readonly ThemedButton _bAdd = new() { Primary = true, Pill = true, Height = BtnH };
    private readonly ThemedButton _bNext = new() { Pill = true, Height = BtnH };
    private readonly ThemedButton _bQueue = new() { Pill = true, Height = BtnH };
    private readonly ThemedButton _bDel = new() { Pill = true, Height = BtnH, Danger = true };
    private readonly Font _fLabel = Theme.UiFont(9.5f, FontStyle.Bold);
    private string _label = "", _delLabel = "";
    private int _labelW, _count, _divX, _closeX;
    private bool _closeHover;
    private Tween? _tw;
    private Point _restingAt;

    private static Color Fill => Theme.Blend(Theme.PanelBg, Color.White, 0.07);
    private Rectangle CloseRect => new(_closeX, (H - CloseD) / 2, CloseD, CloseD);
    /// <summary>The playlist button's screen rect, so the host can open the playlist menu above it.</summary>
    public Rectangle PlaylistAnchor => _bAdd.RectangleToScreen(_bAdd.ClientRectangle);

    public SelectionBar()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor | ControlStyles.ResizeRedraw, true);
        BackColor = Color.Transparent;
        Height = H;
        Visible = false;
        _bAdd.Click += (_, _) => AddToPlaylist?.Invoke();
        _bNext.Click += (_, _) => PlayNext?.Invoke();
        _bQueue.Click += (_, _) => AddToQueue?.Invoke();
        _bDel.Click += (_, _) => Delete?.Invoke();
        foreach (var b in new[] { _bAdd, _bNext, _bQueue, _bDel }) { b.Font = Theme.UiFont(9f, FontStyle.Bold); b.Surface = Fill; Controls.Add(b); }
        MouseMove += (_, e) => { bool h = CloseRect.Contains(e.Location); if (h != _closeHover) { _closeHover = h; Invalidate(); } };
        MouseLeave += (_, _) => { if (_closeHover) { _closeHover = false; Invalidate(); } };
        MouseDown += (_, e) => { if (e.Button == MouseButtons.Left && CloseRect.Contains(e.Location)) Clear?.Invoke(); };
    }

    /// <summary>What the bar offers for the current selection. It is laid out to FIT <paramref name="maxWidth"/>:
    /// the labels shorten before anything is dropped (Hungarian runs a third longer than the English the sizes
    /// were drawn for, and a narrow window leaves the list barely 600 px).</summary>
    public void Configure(int count, bool canPlaylist, bool canDelete, string deleteLabel, int maxWidth)
    {
        _count = count; _delLabel = deleteLabel;
        _bAdd.Visible = canPlaylist;
        _bDel.Visible = canDelete;
        for (int step = 0; ; step++)
        {
            ApplyTexts(step);
            Relayout();
            if (Width <= maxWidth || step >= 3) break;
        }
        Invalidate();
    }

    /// <summary>Step 0 = full labels; 1 = short ones; 2 = the count as a bare number; 3 = the queue action drops
    /// (it stays one right-click away).</summary>
    private void ApplyTexts(int step)
    {
        bool brief = step >= 1;
        _label = step >= 2 ? _count.ToString() : Loc.T("{0} songs selected", _count);
        _bAdd.Text = (brief ? Loc.T("Playlist") : Loc.T("Add to playlist")) + "  ▾";
        _bNext.Text = brief ? Loc.T("Next up") : Loc.T("Play next");
        _bQueue.Text = brief ? Loc.T("Queue") : Loc.T("Add to queue");
        _bDel.Text = _delLabel;
        _bQueue.Visible = step < 3;
    }


    private void Relayout()
    {
        // The count keeps its natural width (Segoe UI's digits are all the same width, so 2..9 selected
        // measure identically and the centred bar does not wobble as a drag-select grows).
        _labelW = TextRenderer.MeasureText(_label, _fLabel, new Size(600, H), TextFormatFlags.NoPrefix).Width;
        int x = Pad + _labelW + 16;
        foreach (var b in new[] { _bAdd, _bNext, _bQueue, _bDel })
        {
            if (!b.Visible) continue;
            b.Width = b.NeededWidth + 6;
            b.Location = new Point(x, (H - BtnH) / 2);
            x += b.Width + Gap;
        }
        x -= Gap;              // the last button's trailing gap is not part of the row
        _divX = x + 13;        // a hairline rule: the × dismisses, it is not a fifth action
        _closeX = _divX + 13;
        Width = _closeX + CloseD + Pad - 2;
    }

    /// <summary>Theme change: the buttons clear their corners to the bar's own fill.</summary>
    public void Restyle() { foreach (var b in new[] { _bAdd, _bNext, _bQueue, _bDel }) b.Surface = Fill; Invalidate(); }

    /// <summary>Show at <paramref name="at"/>, sliding up the last few pixels; already visible: just move there.</summary>
    public void Reveal(Point at)
    {
        _restingAt = at;
        if (Visible) { Location = at; BringToFront(); return; }
        _tw?.Cancel();
        Location = Anim.MotionEnabled ? new Point(at.X, at.Y + 14) : at;
        Visible = true;
        BringToFront();
        if (!Anim.MotionEnabled) return;
        int y0 = at.Y + 14;
        _tw = Anim.Run(200, v => { if (!IsDisposed) Location = new Point(_restingAt.X, (int)Math.Round(y0 + (_restingAt.Y - y0) * v)); }, () => _tw = null, Easings.OutCubic);
    }

    public void Dismiss() { _tw?.Cancel(); _tw = null; Visible = false; }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        var pill = new RectangleF(0.5f, 0.5f, Width - 1, H - 1);
        // a soft lift under the bar (the list is dark; three rings of low alpha read as a shadow, not a halo)
        for (int i = 3; i >= 1; i--)
            using (var sh = new SolidBrush(Color.FromArgb(22, 0, 0, 0)))
            using (var sp = Theme.RoundedRect(new RectangleF(pill.X - i, pill.Y + i + 1, pill.Width + 2 * i, pill.Height + i), H / 2f + i))
                g.FillPath(sh, sp);
        using (var fill = new SolidBrush(Fill))
        using (var fp = Theme.RoundedRect(pill, H / 2f)) g.FillPath(fill, fp);
        using (var line = new Pen(Color.FromArgb(44, 255, 255, 255)))
        using (var lp = Theme.RoundedRect(pill, H / 2f)) g.DrawPath(line, lp);
        TextRenderer.DrawText(g, _label, _fLabel, new Rectangle(Pad, 0, _labelW + 4, H), Theme.TextCol, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        g.SmoothingMode = SmoothingMode.None;
        using (var dv = new Pen(Color.FromArgb(30, 255, 255, 255))) g.DrawLine(dv, _divX, H / 2 - 9, _divX, H / 2 + 8);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var cr = CloseRect;
        if (_closeHover) { using var hb = new SolidBrush(Color.FromArgb(40, 255, 255, 255)); g.FillEllipse(hb, cr); }
        using var pen = new Pen(_closeHover ? Theme.TextCol : Theme.Subtle, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        float cx = cr.X + cr.Width / 2f, cy = cr.Y + cr.Height / 2f, k = 3.6f;
        g.DrawLine(pen, cx - k, cy - k, cx + k, cy + k);
        g.DrawLine(pen, cx + k, cy - k, cx - k, cy + k);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _tw?.Cancel(); _fLabel.Dispose(); }
        base.Dispose(disposing);
    }
}
