using System.Drawing.Drawing2D;

namespace iPodCommander;

/// <summary>A slim horizontal slider for the crossfade duration (1..12 s), styled like the EQ knobs.</summary>
internal sealed class DurationSlider : Control
{
    public event Action<double>? ValueChanged;
    private double _value;
    private bool _drag;
    private const double Min = 1, Max = 12;

    public DurationSlider(double seconds)
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
        Size = new Size(200, 30);
        _value = Math.Clamp(seconds, Min, Max);
        MouseDown += (_, e) => { _drag = true; SetFromX(e.X); };
        MouseMove += (_, e) => { if (_drag) SetFromX(e.X); };
        MouseUp += (_, _) => _drag = false;
        MouseLeave += (_, _) => _drag = false;
    }

    public double Value
    {
        get => _value;
        set { _value = Math.Clamp(value, Min, Max); Invalidate(); }
    }

    private Rectangle Track => new(6, Height / 2 - 2, Width - 12 - 40, 4);   // leave room for the "Ns" label

    private void SetFromX(int x)
    {
        var t = Track;
        double frac = Math.Clamp((x - t.Left) / (double)Math.Max(1, t.Width), 0, 1);
        double v = Math.Round(Min + frac * (Max - Min));   // whole seconds
        if (Math.Abs(v - _value) < 0.001) { Invalidate(); return; }
        _value = v; Invalidate(); ValueChanged?.Invoke(_value);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Theme.PanelBg);
        var t = Track;
        float frac = (float)((_value - Min) / (Max - Min));
        using (var bp = Theme.RoundedRect(new RectangleF(t.X, t.Y, t.Width, t.Height), t.Height / 2f))
        using (var bb = new SolidBrush(Theme.Blend(Theme.PanelBg, Color.White, 0.16))) g.FillPath(bb, bp);
        float fw = t.Width * frac;
        if (fw > 1)
            using (var fp = Theme.RoundedRect(new RectangleF(t.X, t.Y, fw, t.Height), t.Height / 2f))
            using (var fb = new SolidBrush(Theme.Accent)) g.FillPath(fb, fp);
        float kx = t.X + fw, ky = t.Y + t.Height / 2f;
        using (var ks = new SolidBrush(Color.FromArgb(70, 0, 0, 0))) g.FillEllipse(ks, kx - 7, ky - 6.5f, 14, 14);
        using (var kb = new SolidBrush(Color.White)) g.FillEllipse(kb, kx - 6.5f, ky - 6.5f, 13, 13);
        TextRenderer.DrawText(g, $"{(int)_value}s", Theme.UiFont(9.5f), new Rectangle(Width - 38, 0, 38, Height), Theme.TextCol,
            TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }
}

/// <summary>The "Pro Features" hub: advanced playback toggles (gapless, crossfade + length, volume
/// normalization, mono) plus a sleep timer. Drops from the wand button and dismisses on click-away. Toggle
/// changes apply live through <paramref name="onChange"/> (which updates the player + persists); the sleep
/// timer is session-only and applied through <paramref name="onSleep"/>.</summary>
internal sealed class ProFeaturesDialog : FlyoutForm
{
    private static readonly int[] SleepOpts = { 0, 15, 30, 60 };   // minutes per segment

    private readonly Action<bool, double, bool, bool, bool> _onChange;   // gapless, crossSecs, crossOn, normalize, mono
    private readonly Action<int> _onSleep;                               // sleep minutes (0 = off)
    private readonly ToggleSwitch _gapless, _crossfade, _normalize, _mono;
    private readonly DurationSlider _secs;
    private readonly SegmentedControl _sleep;

    public ProFeaturesDialog(bool gapless, bool crossOn, double crossSecs, bool normalize, bool mono, int sleepMin,
        Action<bool, double, bool, bool, bool> onChange, Action<int> onSleep)
    {
        _onChange = onChange; _onSleep = onSleep;
        Text = "Pro Features";
        ClientSize = new Size(404, 300);
        ForeColor = Theme.TextCol; Font = Theme.UiFont(9.5f);   // borderless/anchored chrome comes from FlyoutForm

        Controls.Add(new Label
        {
            Text = "Pro Features", Font = Theme.DisplayFont(13f, FontStyle.Bold), ForeColor = Theme.TextCol,
            AutoSize = false, Bounds = new Rectangle(18, 14, 300, 24), TextAlign = ContentAlignment.MiddleLeft,
        });

        _gapless = new ToggleSwitch { Checked = gapless };
        _crossfade = new ToggleSwitch { Checked = crossOn };
        _normalize = new ToggleSwitch { Checked = normalize };
        _mono = new ToggleSwitch { Checked = mono };
        _secs = new DurationSlider(crossSecs) { Width = 172 };
        int sleepIdx = Math.Max(0, Array.IndexOf(SleepOpts, sleepMin));
        _sleep = new SegmentedControl { Options = new[] { "Off", "15m", "30m", "60m" }, SelectedIndex = sleepIdx, Width = 196 };

        const int row = 46;
        var card = new CardPanel(ClientSize.Width - 36) { Location = new Point(18, 46) };
        card.AddRow("Gapless playback", "No silence between back-to-back tracks", _gapless, row);
        card.AddRow("Crossfade", "Blend the end of one track into the next", _crossfade, row);
        card.AddRow("Crossfade length", null, _secs, 42);
        card.AddRow("Volume normalization", "Even out loud and quiet tracks", _normalize, row);
        card.AddRow("Mono", "Combine left and right into one channel", _mono, row);
        card.AddRow("Sleep timer", "Fade out and pause", _sleep, row);
        card.Finish();
        Controls.Add(card);
        ClientSize = new Size(ClientSize.Width, card.Bottom + 16);

        _gapless.CheckedChanged += Push;
        _crossfade.CheckedChanged += Push;
        _normalize.CheckedChanged += Push;
        _mono.CheckedChanged += Push;
        _secs.ValueChanged += _ => Push();
        _sleep.SelectedChanged += () => _onSleep(SleepOpts[Math.Clamp(_sleep.SelectedIndex, 0, SleepOpts.Length - 1)]);
    }

    private void Push() => _onChange(_gapless.Checked, _secs.Value, _crossfade.Checked, _normalize.Checked, _mono.Checked);
}
