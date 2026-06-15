using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace iPodCommander;

/// <summary>A 10-band graphic-EQ editor: drag each band's knob between −12 and +12 dB.</summary>
internal sealed class EqBandsControl : Control
{
    public event Action<float[]>? GainsChanged;
    private const float Range = 12f;
    private readonly float[] _gains = new float[EqualizerSampleProvider.BandCount];
    private int _drag = -1;
    private static readonly string[] Labels = { "31", "62", "125", "250", "500", "1k", "2k", "4k", "8k", "16k" };

    public EqBandsControl(float[] gains)
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.PanelBg;
        Cursor = Cursors.Hand;
        SetGains(gains);
        MouseDown += (_, e) => { _drag = BandAt(e.X); if (_drag >= 0) SetFromY(_drag, e.Y); };
        MouseMove += (_, e) => { if (_drag >= 0) SetFromY(_drag, e.Y); };
        MouseUp += (_, _) => _drag = -1;
        MouseLeave += (_, _) => _drag = -1;
    }

    public float[] Gains => (float[])_gains.Clone();

    public void SetGains(float[] g)
    {
        for (int i = 0; i < _gains.Length; i++) _gains[i] = Math.Clamp(i < g.Length ? g[i] : 0f, -Range, Range);
        Invalidate();
    }

    private int Cols => EqualizerSampleProvider.BandCount;
    private int ColW => Math.Max(1, Width / Cols);
    private int BandAt(int x) { int i = x / ColW; return i >= 0 && i < Cols ? i : -1; }
    private Rectangle TrackArea => new(0, 12, Width, Math.Max(20, Height - 12 - 22));

    private void SetFromY(int band, int y)
    {
        var ta = TrackArea;
        float frac = 1f - Math.Clamp((y - ta.Top) / (float)ta.Height, 0, 1); // top = +Range, bottom = −Range
        _gains[band] = (frac * 2 - 1) * Range;
        Invalidate();
        GainsChanged?.Invoke(Gains);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(BackColor);
        var ta = TrackArea;
        int w = ColW, midY = ta.Top + ta.Height / 2;
        using (var cp = new Pen(Theme.Blend(Theme.PanelBg, Color.White, 0.12f))) g.DrawLine(cp, 6, midY, Width - 6, midY); // 0 dB line
        for (int i = 0; i < Cols; i++)
        {
            int cx = i * w + w / 2;
            using (var tp = new Pen(Theme.Blend(Theme.PanelBg, Color.White, 0.10f), 3f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawLine(tp, cx, ta.Top, cx, ta.Bottom);
            float frac = (_gains[i] / Range + 1f) / 2f;
            int ky = (int)(ta.Bottom - frac * ta.Height);
            using (var fp = new Pen(Theme.Accent, 3f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawLine(fp, cx, midY, cx, ky);
            using (var kb = new SolidBrush(Color.White)) g.FillEllipse(kb, cx - 6, ky - 6, 12, 12);
            TextRenderer.DrawText(g, Labels[i], Theme.UiFont(7.5f), new Rectangle(i * w, Height - 20, w, 18), Theme.Subtle,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.Top);
        }
    }
}

/// <summary>Equalizer window: an on/off switch, preset buttons, and the 10-band editor. Changes apply
/// live through the supplied callback (which updates the player and persists the settings).</summary>
internal sealed class EqualizerDialog : Form
{
    private static readonly (string Name, float[] Gains)[] Presets =
    {
        ("Flat",   new float[10]),
        ("Bass",   new float[] { 6, 5, 4, 2, 0, 0, 0, 0, 0, 0 }),
        ("Treble", new float[] { 0, 0, 0, 0, 0, 0, 2, 4, 5, 6 }),
        ("Vocal",  new float[] { -2, -1, 0, 2, 4, 4, 3, 1, 0, -1 }),
        ("Rock",   new float[] { 4, 3, 1, -1, -1, 0, 2, 3, 4, 4 }),
        ("Pop",    new float[] { -1, 0, 2, 3, 3, 2, 0, -1, -1, -2 }),
    };

    private readonly Action<bool, float[]> _onChange;
    private readonly ToggleSwitch _toggle;
    private readonly SegmentedControl _presets;
    private readonly EqBandsControl _bands;

    public EqualizerDialog(bool enabled, float[] gains, Action<bool, float[]> onChange)
    {
        _onChange = onChange;
        Text = "Equalizer";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
        ClientSize = new Size(480, 330);
        BackColor = Theme.Bg; ForeColor = Theme.TextCol; Font = Theme.UiFont(9.5f);

        Controls.Add(new Label { Text = "Equalizer", Font = Theme.DisplayFont(15f, FontStyle.Bold), ForeColor = Theme.TextCol, AutoSize = false, Bounds = new Rectangle(22, 18, 240, 28), TextAlign = ContentAlignment.MiddleLeft });

        _toggle = new ToggleSwitch { Checked = enabled, Location = new Point(ClientSize.Width - 22 - 46, 20) };
        _toggle.CheckedChanged += Push;
        Controls.Add(_toggle);

        _presets = new SegmentedControl { Options = Array.ConvertAll(Presets, p => p.Name), Width = ClientSize.Width - 44, Height = 30, Location = new Point(22, 58), SelectedIndex = MatchPreset(gains) };
        _presets.SelectedChanged += () => { _bands.SetGains(Presets[_presets.SelectedIndex].Gains); Push(); };
        Controls.Add(_presets);

        _bands = new EqBandsControl(gains) { Bounds = new Rectangle(22, 102, ClientSize.Width - 44, ClientSize.Height - 102 - 18) };
        _bands.GainsChanged += _ => Push();
        Controls.Add(_bands);
    }

    private void Push() => _onChange(_toggle.Checked, _bands.Gains);

    private static int MatchPreset(float[] g)
    {
        for (int i = 0; i < Presets.Length; i++)
        {
            bool same = true;
            for (int b = 0; b < EqualizerSampleProvider.BandCount; b++)
                if (Math.Abs(Presets[i].Gains[b] - (b < g.Length ? g[b] : 0f)) > 0.5f) { same = false; break; }
            if (same) return i;
        }
        return 0;
    }

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try { int on = 1; DwmSetWindowAttribute(Handle, 20, ref on, sizeof(int)); } catch { }
    }
}
