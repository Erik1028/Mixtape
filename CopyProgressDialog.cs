using System.Drawing.Drawing2D;

namespace iPodCommander;

/// <summary>A slim rounded progress bar painted in the theme accent (no native green).</summary>
internal sealed class ThemedProgressBar : Control
{
    public int Maximum { get; set; } = 100;
    private int _value;
    public int Value { get => _value; set { _value = Math.Max(0, value); Invalidate(); } }

    public ThemedProgressBar()
    {
        DoubleBuffered = true;
        Height = 8;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.PanelBg;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Theme.PanelBg);
        float r = Height / 2f;
        using (var tp = Theme.RoundedRect(new RectangleF(0, 0, Width - 1, Height - 1), r))
        using (var tb = new SolidBrush(Theme.RowBg))
            g.FillPath(tb, tp);

        float frac = Maximum > 0 ? Math.Min(1f, (float)_value / Maximum) : 0;
        int w = (int)((Width - 1) * frac);
        if (w >= Height)
        {
            using var fp = Theme.RoundedRect(new RectangleF(0, 0, w, Height - 1), r);
            using var fb = new SolidBrush(Theme.Accent);
            g.FillPath(fb, fp);
        }
    }
}

/// <summary>
/// Modal progress dialog that runs a long copy operation on a BACKGROUND thread so the main
/// window stays responsive. The work delegate receives a <c>report(done, text)</c> callback and
/// a <c>cancelled()</c> check; the dialog closes itself when the work finishes (or is cancelled).
/// </summary>
internal sealed class CopyProgressDialog : Form
{
    private readonly ThemedProgressBar _bar = new() { Dock = DockStyle.Top, Height = 8, Margin = new Padding(0) };
    private readonly Label _status = new() { Dock = DockStyle.Top, Height = 22, ForeColor = Theme.Subtle, AutoEllipsis = true };
    private readonly Label _heading;
    private readonly ThemedButton _cancel = new() { Text = Loc.T("Cancel"), Width = 96, Height = 30, Pill = true };
    private readonly Action<Action<int, string>, Func<bool>> _work;
    private volatile bool _cancelled;

    public Exception? Error { get; private set; }
    public bool WasCancelled => _cancelled;

    public CopyProgressDialog(string heading, int total, Action<Action<int, string>, Func<bool>> work)
    {
        _work = work;
        Text = "Mixtape";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        ControlBox = false; // force Cancel / completion; no stray X mid-copy
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(440, 150);
        BackColor = Theme.Bg;
        ForeColor = Theme.TextCol;
        Font = Theme.UiFont(9.5f);

        _bar.Maximum = Math.Max(1, total);
        _heading = new Label { Text = heading, Dock = DockStyle.Top, Height = 26, Font = Theme.UiFont(11f, FontStyle.Bold), ForeColor = Theme.TextCol };
        _status.Text = Loc.T("Preparing…");
        _cancel.Location = new Point(ClientSize.Width - 96 - 20, ClientSize.Height - 30 - 18);
        _cancel.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        _cancel.Click += (_, _) => { _cancelled = true; _cancel.Enabled = false; _status.Text = Loc.T("Finishing the current file…"); };

        // A padded host so the docked rows have margins.
        var host = new Panel { Dock = DockStyle.Fill, Padding = new Padding(20, 18, 20, 0), BackColor = Theme.Bg };
        host.Controls.Add(_bar);
        host.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 10, BackColor = Theme.Bg });
        host.Controls.Add(_status);
        host.Controls.Add(_heading);
        // Dock z-order: last added sits on top, so heading→status→spacer→bar top-to-bottom.
        Controls.Add(host);
        Controls.Add(_cancel);
        _cancel.BringToFront(); // host is Dock=Fill and opaque; without this it covers the Cancel button (invisible + unclickable)

        Shown += (_, _) => Start();
    }

    private void Start()
    {
        Task.Run(() =>
        {
            try { _work(Report, () => _cancelled); }
            catch (Exception ex) { Error = ex; }
        }).ContinueWith(_ =>
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke(() => { DialogResult = DialogResult.OK; Close(); }); } catch { }
        });
    }

    private void Report(int done, string text)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(() => { _bar.Value = done; _status.Text = text; }); } catch { }
    }
}
