using Avalonia;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ShapePath = Avalonia.Controls.Shapes.Path;

namespace Mixtape.App.Controls;

/// <summary>
/// A Cover Flow deck, rebuilt in Avalonia from the Windows CoverFlowView. Fanned covers with a fake-perspective
/// turn (horizontal squash + skew) and a faded reflection; the centre cover is flat and on top. Mouse wheel /
/// arrows step one cover (accumulating onto a target and coasting with OutQuint), drag scrubs, clicking a side
/// cover centres it, clicking the centred cover (or Enter) plays it. The 3D warp the Windows app hand-rolls is
/// replaced by Avalonia render transforms — the geometry (70° turn, side1/sideStep spacing, half-height mirror)
/// is carried over so the silhouette matches.
/// </summary>
public sealed class CoverFlowView : UserControl
{
    public sealed record CoverItem(Bitmap? Cover, IBrush Tile, string Title, string Sub, object? Tag);

    private readonly List<CoverItem> _items = new();
    private double _pos;               // fractional centre index (animated)
    private int _target;
    private const double MaxAngleDeg = 70;

    private readonly Canvas _deck = new() { ClipToBounds = false };
    private readonly Dictionary<int, Border> _cards = new();
    private readonly TextBlock _title = new(), _sub = new();
    private readonly StackPanel _modeSwitch = new() { Orientation = Orientation.Horizontal, Spacing = 0 };
    private readonly Border _npChip;
    private readonly Ellipse _glow = new();

    private DispatcherTimer? _anim;
    private double _animFrom, _animTo, _animDur, _animT;
    private string _mode = "Albums";

    private object? _playingTag;
    private bool _showNp;   // cached "is the playing item in this deck?" — recomputed only when PlayingTag / _items change
    public object? PlayingTag
    {
        get => _playingTag;
        set { _playingTag = value; RecomputeNp(); if (IsVisible && Bounds.Width > 0) Relayout(); }
    }
    private void RecomputeNp() => _showNp = _playingTag is not null && _items.Any(x => Equals(x.Tag, _playingTag));
    public event Action<CoverItem>? Activated;
    public event Action? CloseRequested;
    public event Action<string>? ModeChanged;

    public CoverFlowView()
    {
        Focusable = true;
        Background = Brushes.Transparent;

        // ---- backdrop: theme surface + a soft centre spotlight ----
        var bg = new Border { Background = App.Brush("AppBrush") };
        var root = new Panel();
        root.Children.Add(bg);
        _glow.IsHitTestVisible = false;
        _glow.HorizontalAlignment = HorizontalAlignment.Center;
        _glow.VerticalAlignment = VerticalAlignment.Top;
        root.Children.Add(_glow);
        root.Children.Add(_deck);

        // ---- centre text ----
        _title.FontSize = 15; _title.FontWeight = FontWeight.SemiBold; _title.Foreground = Brushes.White;
        _title.HorizontalAlignment = HorizontalAlignment.Center; _title.TextTrimming = TextTrimming.CharacterEllipsis;
        _sub.FontSize = 12; _sub.HorizontalAlignment = HorizontalAlignment.Center; _sub.TextTrimming = TextTrimming.CharacterEllipsis;
        _sub.Foreground = App.Brush("SubtleBrush");
        var textStack = new StackPanel { Spacing = 2, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 30) };
        textStack.Children.Add(_title); textStack.Children.Add(_sub);
        root.Children.Add(textStack);

        // ---- chrome: mode switch (centre-top), now-playing chip (top-left), close (top-right) ----
        BuildModeSwitch();
        _modeSwitch.HorizontalAlignment = HorizontalAlignment.Center;
        _modeSwitch.VerticalAlignment = VerticalAlignment.Top;
        _modeSwitch.Margin = new Thickness(0, 14, 0, 0);
        root.Children.Add(_modeSwitch);

        _npChip = BuildNowPlayingChip();
        root.Children.Add(_npChip);

        var close = new Button { Width = 34, Height = 34, CornerRadius = new CornerRadius(17), Padding = new Thickness(0), Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 14, 14, 0), Content = new ShapePath { Data = Geometry.Parse("M8,8 L18,18 M18,8 L8,18"), Stroke = new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)), StrokeThickness = 1.8, StrokeLineCap = PenLineCap.Round }, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
        close.Classes.Add("cfclose");
        close.Click += (_, _) => CloseRequested?.Invoke();
        root.Children.Add(close);

        Content = root;

        PointerWheelChanged += OnWheel;
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        KeyDown += OnKeyDown;
        SizeChanged += (_, _) => Relayout();
    }

    // ============================ data ============================
    public void SetItems(IReadOnlyList<CoverItem> items, int start)
    {
        StopAnim(); _animT = 0;   // cancel an in-flight coast so a mode-switch mid-flick can't lurch to a stale index
        _items.Clear();
        _items.AddRange(items);
        foreach (var b in _cards.Values) _deck.Children.Remove(b);
        _cards.Clear();
        _pos = _target = Math.Clamp(start, 0, Math.Max(0, _items.Count - 1));
        RecomputeNp();
        Relayout();
        Focus();
    }

    public string Mode => _mode;
    public void SetMode(string mode) { _mode = mode; RefreshModeSwitch(); }

    // ============================ navigation ============================
    private void OnWheel(object? s, PointerWheelEventArgs e) { Move(e.Delta.Y > 0 ? -1 : 1); e.Handled = true; }

    private bool _dragging; private double _downX, _downPos; private double _stepPx = 100;

    private void OnPointerPressed(object? s, PointerPressedEventArgs e)
    {
        Focus();
        _downX = e.GetPosition(this).X; _downPos = _pos; _dragging = false;
    }

    private void OnPointerMoved(object? s, PointerEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        double dx = e.GetPosition(this).X - _downX;
        if (!_dragging && Math.Abs(dx) > 4) _dragging = true;
        if (_dragging) { StopAnim(); _pos = Math.Clamp(_downPos - dx / Math.Max(1, _stepPx), 0, Math.Max(0, _items.Count - 1)); Relayout(); }
    }

    private void OnPointerReleased(object? s, PointerReleasedEventArgs e)
    {
        if (_dragging) { _dragging = false; MoveTo((int)Math.Round(_pos)); return; }
        // a click (no drag): centre the hit cover, or activate the already-centred one
        int hit = HitTest(e.GetPosition(this));
        if (hit < 0) return;
        int ci = Math.Clamp((int)Math.Round(_pos), 0, Math.Max(0, _items.Count - 1));
        if (hit == ci && Settled) ActivateCentre();
        else MoveTo(hit);
    }

    private void OnKeyDown(object? s, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Left: Move(-1); e.Handled = true; break;
            case Key.Right: Move(1); e.Handled = true; break;
            case Key.Home: MoveTo(0); e.Handled = true; break;
            case Key.End: MoveTo(_items.Count - 1); e.Handled = true; break;
            case Key.Enter: ActivateCentre(); e.Handled = true; break;
            case Key.Escape: CloseRequested?.Invoke(); e.Handled = true; break;
        }
    }

    private bool Settled => Math.Abs(_pos - _target) < 0.01;
    private void ActivateCentre()
    {
        int ci = Math.Clamp((int)Math.Round(_pos), 0, Math.Max(0, _items.Count - 1));
        if (ci >= 0 && ci < _items.Count) Activated?.Invoke(_items[ci]);
    }

    private int HitTest(Point p)
    {
        // front-to-back: nearest to centre wins
        int best = -1; double bestA = double.MaxValue;
        foreach (var (idx, card) in _cards)
        {
            var b = card.Bounds;
            if (p.X >= b.Left && p.X <= b.Right && p.Y >= b.Top && p.Y <= b.Top + b.Height)
            {
                double a = Math.Abs(idx - _pos);
                if (a < bestA) { bestA = a; best = idx; }
            }
        }
        return best;
    }

    private void Move(int delta) => MoveTo(_target + delta);
    private void MoveTo(int target)
    {
        _target = Math.Clamp(target, 0, Math.Max(0, _items.Count - 1));
        double dist = Math.Abs(_target - _pos);
        if (dist < 0.001) { StopAnim(); Relayout(); return; }
        _animFrom = _pos; _animTo = _target; _animT = 0;
        _animDur = Math.Clamp(260 + 95 * Math.Sqrt(dist), 260, 620);
        StartAnim();
    }

    private static readonly QuinticEaseOut _ease = new();
    private void StartAnim()
    {
        _anim ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(15) };
        _anim.Tick -= OnAnimTick; _anim.Tick += OnAnimTick;
        _anim.Start();
    }
    private void StopAnim() => _anim?.Stop();

    private void OnAnimTick(object? s, EventArgs e)
    {
        _animT += 15;
        double v = Math.Clamp(_animT / _animDur, 0, 1);
        _pos = _animFrom + (_animTo - _animFrom) * _ease.Ease(v);
        Relayout();
        if (v >= 1) { _pos = _animTo; StopAnim(); Relayout(); }
    }

    // ============================ layout / rendering ============================
    private void Relayout()
    {
        double w = Bounds.Width, h = Bounds.Height;
        if (w <= 0 || h <= 0 || _items.Count == 0) return;

        double baseH = Math.Clamp(Math.Min(h * 0.46, w * 0.34), 130, 420);
        double cx = w / 2, centreY = h * 0.42;
        double cw = baseH, ch = baseH;
        double cos = Math.Cos(MaxAngleDeg * Math.PI / 180);
        double projFull = cw * cos;
        double side1 = cw * 0.5 + projFull / 2 - cw * 0.04;
        double sideStep = projFull * 0.52;
        _stepPx = sideStep;

        // centre spotlight
        double gd = Math.Max(w, h) * 0.9;
        _glow.Width = gd; _glow.Height = gd;
        _glow.Margin = new Thickness(0, centreY - gd * 0.5, 0, 0);
        if (_glow.Fill is null)
            _glow.Fill = new RadialGradientBrush { GradientStops = { new GradientStop(Color.FromArgb(26, 255, 255, 255), 0), new GradientStop(Color.FromArgb(0, 255, 255, 255), 1) } };

        int range = 8;
        int lo = Math.Max(0, (int)Math.Floor(_pos) - range);
        int hi = Math.Min(_items.Count - 1, (int)Math.Ceiling(_pos) + range);

        // drop cards outside the window
        foreach (var idx in _cards.Keys.Where(k => k < lo || k > hi).ToList())
        { _deck.Children.Remove(_cards[idx]); _cards.Remove(idx); }

        for (int i = lo; i <= hi; i++)
        {
            if (!_cards.TryGetValue(i, out var card)) { card = BuildCard(_items[i]); _cards[i] = card; _deck.Children.Add(card); }
            double d = i - _pos, a = Math.Abs(d), sgn = Math.Sign(d);
            double turn = Math.Clamp(a, 0, 1);
            double o = a <= 1 ? d * side1 : sgn * (side1 + (a - 1) * sideStep);
            double xc = cx + o;

            card.Width = cw; card.Height = ch + ch * 0.5;
            Canvas.SetLeft(card, xc - cw / 2);
            Canvas.SetTop(card, centreY - ch / 2);
            card.ZIndex = 1000 - (int)Math.Round(a * 10);

            double scaleX = 1 - (1 - cos) * turn;        // 1 (flat) → cos70 (fully turned)
            double skewY = -sgn * turn * 12;              // lean into the deck
            double depth = 1 - 0.05 * Math.Max(0, a - 1); // far covers a touch smaller
            card.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
            card.RenderTransform = new TransformGroup
            {
                Children =
                {
                    new ScaleTransform(scaleX * depth, depth),
                    new SkewTransform(0, skewY),
                }
            };
            card.Opacity = 1 - 0.06 * Math.Max(0, a - 1);
        }

        // centre text fades out during a flick
        int ci = Math.Clamp((int)Math.Round(_pos), 0, _items.Count - 1);
        var it = _items[ci];
        _title.Text = it.Title; _sub.Text = it.Sub;
        double alpha = Math.Clamp(1 - Math.Abs(_pos - ci), 0, 1);
        _title.Opacity = alpha; _sub.Opacity = alpha;

        // now-playing chip visibility (cached — the scan runs only on PlayingTag/_items change, not per frame)
        _npChip.IsVisible = _showNp;
    }

    private Border BuildCard(CoverItem item)
    {
        // cover (flat, on top)
        var coverPanel = new Panel();
        coverPanel.Children.Add(new Border { Background = item.Tile });
        if (item.Cover is not null) coverPanel.Children.Add(new Image { Source = item.Cover, Stretch = Stretch.UniformToFill });
        coverPanel.Children.Add(new Border { BorderBrush = new SolidColorBrush(Color.FromArgb(30, 255, 255, 255)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10) });
        var cover = new Border { CornerRadius = new CornerRadius(10), ClipToBounds = true, Child = coverPanel };
        Grid.SetRow(cover, 0);

        // reflection (mirrored + faded copy hanging below)
        var reflInner = new Panel { RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative), RenderTransform = new ScaleTransform(1, -1) };
        reflInner.Children.Add(new Border { Background = item.Tile });
        if (item.Cover is not null) reflInner.Children.Add(new Image { Source = item.Cover, Stretch = Stretch.UniformToFill });
        var refl = new Border
        {
            ClipToBounds = true,
            CornerRadius = new CornerRadius(0, 0, 10, 10),
            Child = reflInner,
            OpacityMask = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.FromArgb(90, 255, 255, 255), 0), new GradientStop(Color.FromArgb(0, 255, 255, 255), 1) },
            },
        };
        Grid.SetRow(refl, 1);

        var grid = new Grid { RowDefinitions = new RowDefinitions("*,0.5*") };
        grid.Children.Add(cover);
        grid.Children.Add(refl);
        return new Border { Child = grid, Background = Brushes.Transparent };
    }

    // ============================ chrome ============================
    private void BuildModeSwitch()
    {
        _modeSwitch.Children.Clear();
        foreach (var m in new[] { "Songs", "Albums", "Artists" })
        {
            var b = new Button { Content = m, Tag = m, Padding = new Thickness(17, 6), CornerRadius = new CornerRadius(15), FontSize = 12, FontWeight = FontWeight.SemiBold, BorderThickness = new Thickness(0) };
            b.Classes.Add("cfseg");
            b.Click += (_, _) => { if (_mode != m) { _mode = m; RefreshModeSwitch(); ModeChanged?.Invoke(m); } };
            _modeSwitch.Children.Add(b);
        }
        RefreshModeSwitch();
    }

    private void RefreshModeSwitch()
    {
        foreach (var b in _modeSwitch.Children.OfType<Button>())
        {
            bool on = (string?)b.Tag == _mode;
            b.Classes.Set("active", on);
            b.Background = on ? App.Brush("AccentBrush") : new SolidColorBrush(Color.FromArgb(28, 255, 255, 255));
            b.Foreground = on ? App.Brush("OnAccentBrush") : new SolidColorBrush(Color.FromArgb(210, 255, 255, 255));
        }
    }

    private Border BuildNowPlayingChip()
    {
        var bars = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2.5, VerticalAlignment = VerticalAlignment.Center };
        double[] hs = { 8, 13, 6 };
        foreach (var hgt in hs) bars.Children.Add(new Border { Width = 2.6, Height = hgt, CornerRadius = new CornerRadius(1), Background = App.Brush("AccentBrightBrush"), VerticalAlignment = VerticalAlignment.Center });
        var label = new TextBlock { Text = "Now Playing", Foreground = Brushes.White, FontSize = 11.5, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(bars); row.Children.Add(label);
        var chip = new Border
        {
            Child = row,
            Background = new SolidColorBrush(Color.FromArgb(38, 255, 255, 255)),
            CornerRadius = new CornerRadius(15),
            Padding = new Thickness(14, 7),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(16, 14, 0, 0),
            IsVisible = false,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        chip.PointerPressed += (_, _) => { int i = _items.FindIndex(x => Equals(x.Tag, PlayingTag)); if (i >= 0) MoveTo(i); };
        return chip;
    }
}
