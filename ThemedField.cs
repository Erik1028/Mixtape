using System.Drawing.Drawing2D;

namespace iPodCommander;

/// <summary>
/// A rounded, themed frame around a plain TextBox. The dialogs used the Windows FixedSingle border,
/// a hard square grey rectangle that read as another program's input next to the app's own controls.
/// Build the TextBox exactly as before and add <see cref="Wrap"/>(tb) to the parent instead of the
/// box: it keeps the box's position and width, the caller keeps the box itself, and the field gains
/// the accent focus ring that a flat border never had.
/// </summary>
internal sealed class ThemedField : Panel
{
    private const int Inset = 9;
    public TextBox Box { get; }
    private bool _focused;

    private ThemedField(TextBox tb)
    {
        Box = tb;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        TabStop = false;
        var at = tb.Location;
        int w = tb.Width;
        tb.BorderStyle = BorderStyle.None;          // the frame is ours now (PreferredHeight follows it)
        int th = tb.PreferredHeight, h = th + 7;
        Location = at;
        Size = new Size(w, h);
        tb.SetBounds(Inset, (h - th) / 2, w - 2 * Inset, th);
        tb.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;
        tb.Enter += (_, _) => { _focused = true; Invalidate(); };
        tb.Leave += (_, _) => { _focused = false; Invalidate(); };
        Controls.Add(tb);
        MouseDown += (_, _) => { try { tb.Focus(); } catch { } };   // the frame is part of the field
    }

    /// <summary>Wrap <paramref name="tb"/>; add the result where the box would have gone.</summary>
    public static ThemedField Wrap(TextBox tb) => new(tb);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Theme.Bg);
        using var p = Theme.RoundedRect(new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), Theme.RadControl);
        using (var b = new SolidBrush(Box.BackColor)) g.FillPath(b, p);
        using var pen = new Pen(_focused ? Theme.Accent : Theme.Border, _focused ? 1.4f : 1f);
        g.DrawPath(pen, p);
    }
}
