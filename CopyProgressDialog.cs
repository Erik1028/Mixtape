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
        if (Theme.Classic)
        {
            g.Clear(Parent?.BackColor ?? Theme.Face);
            float f0 = Maximum > 0 ? Math.Min(1f, (float)_value / Maximum) : 0;
            Theme.ClassicProgress(g, new Rectangle(0, 0, Width, Height), (int)Math.Round((Width - 4) * f0));
            return;
        }
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

/// <summary>Which way the files go - for the Classic copy animation's two ends. None: no animation (a job that is not
/// a copy, like saving song info).</summary>
internal enum CopyFlight { None, ToIPod, FromIPod }

/// <summary>
/// Classic: Windows 95's file-copy animation - a sheet of paper tumbling in an arc out of one folder and into the
/// other - in the look's own pixel art, with the iPod at one end. The one animation 1995 did play: it said "working"
/// while the bar crawled, so it runs on its own clock while every other motion of the look is switched off.
/// </summary>
internal sealed class PaperFlight : Control
{
    private readonly ClassicIcons.Id _from, _to;
    private readonly System.Windows.Forms.Timer _tick = new() { Interval = 70 };
    private int _frame;
    private const int Steps = 13, Rest = 3;   // frames in the air, then a beat before the next sheet leaves

    // The sheet as it tumbles: face on, turning, edge on, turning back (K black, W white).
    private static readonly string[][] Poses =
    {
        new[] { "KKKKK..", "KWWWKK.", "KWKWKWK", "KWWWKKK", "KWKKKWK", "KWWWWWK", "KWKKKWK", "KWWWWWK", "KKKKKKK" },
        new[] { "KKKK.", "KWWKK", "KWKWK", "KWWWK", "KWKWK", "KWWWK", "KWKWK", "KWWWK", "KKKKK" },
        new[] { "....K", "...KK", "...K.", "..KK.", "..K..", ".KK..", ".K...", "KK...", "K...." },
        new[] { ".KKKK", "KKWWK", "KWKWK", "KWWWK", "KWKWK", "KWWWK", "KWKWK", "KWWWK", "KKKKK" },
    };

    public PaperFlight(ClassicIcons.Id from, ClassicIcons.Id to)
    {
        _from = from; _to = to;
        Height = 56;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        _tick.Tick += (_, _) => { _frame = (_frame + 1) % (Steps + Rest); Invalidate(); };
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible) _tick.Start(); else _tick.Stop();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Face);
        int iy = Height - 34, x0 = 4, x1 = Width - 4 - 32;
        ClassicIcons.Draw(g, _from, x0, iy, 2);
        ClassicIcons.Draw(g, _to, x1, iy, 2);
        if (_frame >= Steps) return;
        double t = _frame / (double)(Steps - 1);
        int cx = (int)Math.Round(x0 + 16 + (x1 - x0) * t), cy = (int)Math.Round(iy + 4 - Math.Sin(t * Math.PI) * (iy - 10));
        var pose = Poses[_frame % Poses.Length];
        int w = pose[0].Length * 2, h = pose.Length * 2;
        using var k = new SolidBrush(Color.Black);
        using var wh = new SolidBrush(Color.White);
        for (int y = 0; y < pose.Length; y++)
            for (int x = 0; x < pose[y].Length; x++)
            {
                char c = pose[y][x];
                if (c != '.') g.FillRectangle(c == 'K' ? k : wh, cx - w / 2 + x * 2, cy - h / 2 + y * 2, 2, 2);
            }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _tick.Stop(); _tick.Dispose(); }
        base.Dispose(disposing);
    }
}

/// <summary>
/// Modal progress dialog that runs a long copy operation on a BACKGROUND thread so the main
/// window stays responsive. The work delegate receives a <c>report(done, text)</c> callback and
/// a <c>cancelled()</c> check; the dialog closes itself when the work finishes (or is cancelled).
/// </summary>
internal sealed class CopyProgressDialog : CardDialog
{
    private readonly ThemedProgressBar _bar = new() { Dock = DockStyle.Top, Height = Theme.Classic ? 18 : 8, Margin = new Padding(0) };   // Classic: the era's 18 px bar
    private readonly Label _status = new() { Dock = DockStyle.Top, Height = 22, ForeColor = Theme.Subtle, AutoEllipsis = true };
    private readonly ThemedButton _cancel = new() { Text = Loc.T("Cancel"), Width = 96, Height = 30, Pill = true };
    private readonly Action<Action<int, string>, Func<bool>> _work;
    private volatile bool _cancelled;

    public Exception? Error { get; private set; }
    public bool WasCancelled => _cancelled;

    public CopyProgressDialog(string heading, int total, Action<Action<int, string>, Func<bool>> work, CopyFlight flight = CopyFlight.None)
    {
        _work = work;
        Text = heading;      // the card's title strip carries it (it used to be a label in the body)
        ShowClose = false;   // force Cancel / completion; no stray X mid-copy
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(440, 124);
        bool fly = Theme.Classic && flight != CopyFlight.None;   // Classic: the flying paper over the bar
        if (fly) ClientSize = new Size(440, 124 + 62);
        BackColor = Theme.Bg;
        ForeColor = Theme.TextCol;
        Font = Theme.UiFont(9.5f);

        _bar.Maximum = Math.Max(1, total);
        _status.Text = Loc.T("Preparing…");
        _cancel.Location = new Point(ClientSize.Width - 96 - 20, ClientSize.Height - 30 - 18);
        _cancel.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        _cancel.Click += (_, _) => { _cancelled = true; _cancel.Enabled = false; _status.Text = Loc.T("Finishing the current file…"); };

        // A padded host so the docked rows have margins.
        var host = new Panel { Dock = DockStyle.Fill, Padding = new Padding(20, 18, 20, 0), BackColor = Theme.Bg };
        host.Controls.Add(_bar);
        host.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 10, BackColor = Theme.Bg });
        host.Controls.Add(_status);
        if (fly)
        {
            host.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 6, BackColor = Theme.Bg });
            host.Controls.Add(flight == CopyFlight.ToIPod
                ? new PaperFlight(ClassicIcons.Id.Folder, ClassicIcons.Id.IPod) { Dock = DockStyle.Top }
                : new PaperFlight(ClassicIcons.Id.IPod, ClassicIcons.Id.Folder) { Dock = DockStyle.Top });
        }
        // Dock z-order: last added sits on top, so (flight→) status→spacer→bar top-to-bottom.
        Controls.Add(host);
        Controls.Add(_cancel);
        _cancel.BringToFront(); // host is Dock=Fill and opaque; without this it covers the Cancel button (invisible + unclickable)
        AdoptCard();

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
