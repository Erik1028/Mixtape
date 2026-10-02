using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace iPodCommander;

/// <summary>
/// Things nobody is told about, the way the iPod and Windows 95 had theirs:
///   - Brick: hold the centre of the capacity ring on the iPod's page (the click wheel's centre button) for two
///     seconds. The first iPods hid the same game behind a long press in About.
///   - The Konami code (Up Up Down Down Left Right Left Right B A) in the main window: every cover turns over once.
///     In the Windows 95 look, which moves nothing, it rolls the credits instead.
///   - Credits: click the icon in Settings > About seven times.
/// Nothing here writes anything except Brick's best score.
/// </summary>
internal static class Easter
{
    // ---- the Konami code ----

    private static readonly Keys[] Code = { Keys.Up, Keys.Up, Keys.Down, Keys.Down, Keys.Left, Keys.Right, Keys.Left, Keys.Right, Keys.B, Keys.A };
    private static int _at;

    /// <summary>Feed it every key the main window sees; true on the key that completes the code. It only watches.</summary>
    public static bool Konami(Keys keyData)
    {
        if ((keyData & Keys.Modifiers) != 0) { _at = 0; return false; }
        Keys k = keyData & Keys.KeyCode;
        if (k == Code[_at])
        {
            if (++_at < Code.Length) return false;
            _at = 0;
            return true;
        }
        // A third Up still leaves "Up Up" typed; any other slip starts over (from one Up when it was an Up).
        _at = k == Keys.Up ? (_at == 2 ? 2 : 1) : 0;
        return false;
    }

    // ---- covers turning over ----

    /// <summary>Progress of the Konami turn, 0..1, or negative when nothing turns (the normal case).</summary>
    public static double SpinT = -1;
    /// <summary>The spin benchmark: how many different moments of the turn actually reached the screen.</summary>
    public static int SpinFrames;
    private static double _drawnT = -1;

    /// <summary>Where to draw a cover while the covers turn: narrowed about its centre as it rotates about its vertical
    /// axis, the turn rolling across the host from left to right. <paramref name="back"/> is true while its back faces
    /// out. Returns <paramref name="r"/> untouched when nothing turns.</summary>
    public static Rectangle Spin(Rectangle r, int hostWidth, out bool back)
    {
        back = false;
        if (SpinT < 0) return r;
        if (SpinT != _drawnT) { _drawnT = SpinT; SpinFrames++; }
        const double stagger = 0.45;
        double x = hostWidth > 0 ? Math.Clamp((r.X + r.Width / 2.0) / hostWidth, 0, 1) : 0;
        double local = Math.Clamp(SpinT * (1 + stagger) - x * stagger, 0, 1);
        double c = Math.Cos(Easings.InOutCubic(local) * 2 * Math.PI);
        back = c < 0;
        int w = Math.Max(2, (int)Math.Round(r.Width * Math.Abs(c)));
        return new Rectangle(r.X + (r.Width - w) / 2, r.Y, w, r.Height);
    }

    /// <summary>The back of a turning cover: the same picture in shadow.</summary>
    public static void ShadeBack(Graphics g, Rectangle r, bool back)
    {
        if (!back) return;
        using var b = new SolidBrush(Color.FromArgb(140, 0, 0, 0));
        g.FillRectangle(b, r);
    }

    // ---- the credits ----

    public enum Kind { Title, Sub, Heading, Name, Note, Gap }

    public static string Version =>
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "";

    public static (string Text, Kind Kind)[] Credits() => new[]
    {
        ("Mixtape", Kind.Title),
        (Loc.T("Version {0}", Version), Kind.Sub),
        ("", Kind.Gap),
        (Loc.T("Made for everyone who still carries a click wheel."), Kind.Note),
        ("", Kind.Gap),
        (Loc.T("Idea, testing and taste"), Kind.Heading),
        ("Erik1028", Kind.Name),
        ("", Kind.Gap),
        (Loc.T("Code"), Kind.Heading),
        ("Claude (Anthropic)", Kind.Name),
        ("", Kind.Gap),
        (Loc.T("Built with"), Kind.Heading),
        (".NET 8 · Windows Forms", Kind.Name),
        ("TagLib#", Kind.Name),
        ("NAudio", Kind.Name),
        ("", Kind.Gap),
        (Loc.T("With thanks to"), Kind.Heading),
        ("libgpod & the iPodLinux wiki", Kind.Name),
        (Loc.T("for mapping the iTunesDB"), Kind.Note),
        ("LRCLIB", Kind.Name),
        (Loc.T("for the lyrics"), Kind.Note),
        ("MusicBrainz Cover Art Archive", Kind.Name),
        (Loc.T("for the covers"), Kind.Note),
        ("", Kind.Gap),
        (Loc.T("And"), Kind.Heading),
        ("Apple", Kind.Name),
        (Loc.T("for the iPod, 23 October 2001"), Kind.Note),
        ("", Kind.Gap),
        ("", Kind.Gap),
        (Loc.T("No copies of iTunes were harmed in the making of this program."), Kind.Note),
    };

    /// <summary>Seven clicks, each within a second and a half of the last, open the credits.</summary>
    public static void ArmCreditsClicks(Control c, IWin32Window owner)
    {
        int count = 0;
        long last = 0;
        c.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            long now = Environment.TickCount64;
            count = now - last < 1500 ? count + 1 : 1;
            last = now;
            if (count < 7) return;
            count = 0;
            using var d = new CreditsDialog();
            d.ShowDialog(owner);
        };
    }
}

/// <summary>The iPod's Brick, in a small window: the picture is the iPod's 160 x 128 screen at three times the size.</summary>
internal sealed class BrickDialog : CardDialog
{
    private readonly BrickGame _game;

    public BrickDialog(AppSettings settings)
    {
        Text = "Brick";
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Theme.Classic ? Theme.Face : Theme.Bg;
        ForeColor = Theme.TextCol;
        Font = Theme.UiFont(9.5f);
        _game = new BrickGame(settings);
        ClientSize = new Size(_game.Width + 40, _game.Height + 20 + 36);
        _game.Location = new Point(20, 16);
        Controls.Add(_game);
        var hint = new Label
        {
            Text = Loc.T("Wheel or arrow keys: paddle   ·   click or Space: centre button"),
            AutoSize = false, TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Theme.Classic ? Theme.TextCol : Theme.Faint, BackColor = Color.Transparent,
            Font = Theme.UiFont(Theme.Classic ? Theme.SzBody : 8.5f),
        };
        hint.SetBounds(20, _game.Bottom + 8, _game.Width, 22);
        Controls.Add(hint);
        AdoptCard();
    }

    /// <summary>The render harness: a game in a given state, to look at.</summary>
    public static BrickDialog Preview(string state)
    {
        var d = new BrickDialog(new AppSettings { BrickBest = 1280 });
        d._game.Demo(state);
        return d;
    }

    protected override void OnShown(EventArgs e) { base.OnShown(e); _game.Focus(); }

    protected override void OnDeactivate(EventArgs e) { base.OnDeactivate(e); _game.Pause(); }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape) { Close(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }
}

internal sealed class BrickGame : Control
{
    public const int W = 160, H = 128, S = 3;           // the iPod's screen, and how much bigger it is shown
    private const int Field = 16;                      // below the title bar
    private const int Cols = 8, Rows = 5, BrickW = 18, BrickH = 5, BrickX0 = 4, BrickY0 = 27, BrickStepX = 19, BrickStepY = 7;
    private const int PaddleW = 26, PaddleH = 3, PaddleY = 118, Ball = 3;
    private const double KeySpeed = 150;               // px/s for a held arrow key

    private enum State { Ready, Playing, Paused, Over }

    private readonly AppSettings _settings;
    private readonly Bitmap _screen = new(W, H);
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 15 };
    private readonly System.Diagnostics.Stopwatch _clock = new();
    // Tahoma at 11 px is hinted for aliased drawing, so one-bit text stays crisp and fringe-free when blown up
    private readonly Font _font = new("Tahoma", 11f, FontStyle.Regular, GraphicsUnit.Pixel);
    private readonly Font _fontBold = new("Tahoma", 11f, FontStyle.Bold, GraphicsUnit.Pixel);
    private readonly bool[,] _bricks = new bool[Rows, Cols];
    private State _state = State.Ready;
    private double _px = W / 2.0, _bx, _by, _vx, _vy;
    private double _last;
    private int _score, _lives = 3, _level = 1;
    private bool _left, _right, _newBest, _levelUp;

    private int Inset => Theme.Classic ? 2 : 12;      // the 95 sunken edge, or the iPod's dark bezel
    private Rectangle Lcd => new(Inset, Inset, W * S, H * S);

    public BrickGame(AppSettings settings)
    {
        _settings = settings;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.Selectable, true);
        TabStop = true;
        Size = new Size(W * S + 2 * Inset, H * S + 2 * Inset);
        Cursor = Cursors.Default;
        FillBricks();
        ResetBall();
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        _clock.Start();
        Render();
    }

    private double Speed => 80 * (1 + 0.12 * (_level - 1));

    private void FillBricks() { for (int r = 0; r < Rows; r++) for (int c = 0; c < Cols; c++) _bricks[r, c] = true; }

    private void ResetBall() { _bx = _px - Ball / 2.0; _by = PaddleY - Ball; }

    /// <summary>The click wheel's centre button: start, pause, carry on, play again.</summary>
    private void Centre()
    {
        switch (_state)
        {
            case State.Ready:
                double a = (Random.Shared.NextDouble() * 50 - 25) * Math.PI / 180;
                _vx = Speed * Math.Sin(a); _vy = -Speed * Math.Cos(a);
                _state = State.Playing; _levelUp = false; break;
            case State.Playing: _state = State.Paused; break;
            case State.Paused: _state = State.Playing; break;
            case State.Over:
                _score = 0; _lives = 3; _level = 1; _newBest = _levelUp = false;
                FillBricks(); _state = State.Ready; break;
        }
        Render();
    }

    public void Pause() { if (_state == State.Playing) { _state = State.Paused; Render(); } }

    private void Tick()
    {
        double now = _clock.Elapsed.TotalSeconds, dt = Math.Min(0.05, now - _last);
        _last = now;
        Advance(dt);
        Render();
    }

    private void Advance(double dt)
    {
        if (_left != _right && _state is State.Ready or State.Playing) MovePaddle((_right ? 1 : -1) * KeySpeed * dt);
        if (_state == State.Ready) ResetBall();
        if (_state == State.Playing)
        {
            int steps = Math.Max(1, (int)Math.Ceiling(dt * 240));
            for (int i = 0; i < steps && _state == State.Playing; i++) Step(dt / steps);
        }
    }

    /// <summary>The egg test: play <paramref name="seconds"/> of game time in 1/60 s frames, with no window and no
    /// clock. <paramref name="follow"/> keeps the paddle under the ball, or leaves it in the left corner. Any ball
    /// outside the screen above the paddle line is reported; play stops at the first game over.</summary>
    public (int Score, int Lives, int Level, bool Over, int Escapes) Headless(double seconds, bool follow)
    {
        _timer.Stop();
        int escapes = 0;
        for (double t = 0; t < seconds && _state != State.Over; t += 1 / 60.0)
        {
            if (_state == State.Ready) Centre();
            // a player aims: where on the paddle the ball lands keeps changing, so it doesn't settle into one loop
            _px = follow ? Math.Clamp(_bx + Ball / 2.0 + 9 * Math.Sin(t * 1.3), PaddleW / 2.0, W - PaddleW / 2.0) : PaddleW / 2.0;
            Advance(1 / 60.0);
            if (_state == State.Playing && (_bx < 0 || _bx + Ball > W || _by < Field)) escapes++;
        }
        return (_score, _lives, _level, _state == State.Over, escapes);
    }

    private void MovePaddle(double dx) => _px = Math.Clamp(_px + dx, PaddleW / 2.0, W - PaddleW / 2.0);

    private void Step(double h)
    {
        _bx += _vx * h; _by += _vy * h;
        if (_bx < 0) { _bx = 0; _vx = Math.Abs(_vx); }
        if (_bx + Ball > W) { _bx = W - Ball; _vx = -Math.Abs(_vx); }
        if (_by < Field) { _by = Field; _vy = Math.Abs(_vy); }

        // The paddle sends the ball off at an angle set by where it lands: the middle straight up, the ends at 60 degrees.
        if (_vy > 0 && _by + Ball >= PaddleY && _by + Ball <= PaddleY + PaddleH + 2
            && _bx + Ball >= _px - PaddleW / 2.0 && _bx <= _px + PaddleW / 2.0)
        {
            double off = Math.Clamp((_bx + Ball / 2.0 - _px) / (PaddleW / 2.0), -1, 1);
            double a = off * 60 * Math.PI / 180;
            _vx = Speed * Math.Sin(a); _vy = -Speed * Math.Cos(a);
            _by = PaddleY - Ball;
        }

        if (_by > H)
        {
            if (--_lives > 0) { _state = State.Ready; return; }
            _state = State.Over;
            if (_score > _settings.BrickBest) { _settings.BrickBest = _score; _newBest = true; _settings.Save(); }
            return;
        }

        for (int r = 0; r < Rows; r++)
            for (int c = 0; c < Cols; c++)
            {
                if (!_bricks[r, c]) continue;
                int x0 = BrickX0 + c * BrickStepX, y0 = BrickY0 + r * BrickStepY;
                if (_bx + Ball <= x0 || _bx >= x0 + BrickW || _by + Ball <= y0 || _by >= y0 + BrickH) continue;
                _bricks[r, c] = false;
                _score += 10 * (Rows - r);
                double ox = Math.Min(_bx + Ball - x0, x0 + BrickW - _bx), oy = Math.Min(_by + Ball - y0, y0 + BrickH - _by);
                if (ox < oy) _vx = -_vx; else _vy = -_vy;
                if (!AnyBricks()) { _level++; _levelUp = true; FillBricks(); _state = State.Ready; }
                return;
            }
    }

    private bool AnyBricks() { foreach (bool b in _bricks) if (b) return true; return false; }

    // ---- drawing: everything goes onto the 160 x 128 screen, then up three times, pixel for pixel ----

    private static readonly Color[] VgaRows =
        { Color.FromArgb(255, 0, 0), Color.FromArgb(255, 255, 0), Color.FromArgb(0, 255, 0), Color.FromArgb(0, 255, 255), Color.FromArgb(255, 0, 255) };
    private static readonly Color LcdBg = Color.FromArgb(196, 206, 182), LcdInk = Color.FromArgb(40, 46, 36);

    private void Render()
    {
        bool c95 = Theme.Classic;
        Color bg = c95 ? Color.Black : LcdBg, ink = c95 ? Color.White : LcdInk;
        using (var g = Graphics.FromImage(_screen))
        {
            g.SmoothingMode = SmoothingMode.None;
            g.TextRenderingHint = TextRenderingHint.SingleBitPerPixelGridFit;
            g.Clear(bg);
            using var inkB = new SolidBrush(ink);

            // the title bar, as on the iPod: score, the name, the balls left
            DrawText(g, _score.ToString(), _font, ink, 3, 1, center: false);
            DrawText(g, "Brick", _fontBold, ink, W / 2, 1, center: true);
            for (int i = 0; i < (_state == State.Over ? 0 : _lives - 1); i++)   // the spare balls; the one in play is on screen
                g.FillRectangle(inkB, W - 6 - i * 5, 5, 3, 3);
            g.FillRectangle(inkB, 0, Field - 3, W, 1);

            for (int r = 0; r < Rows; r++)
            {
                using var bb = new SolidBrush(c95 ? VgaRows[r] : ink);
                for (int c = 0; c < Cols; c++)
                    if (_bricks[r, c]) g.FillRectangle(bb, BrickX0 + c * BrickStepX, BrickY0 + r * BrickStepY, BrickW, BrickH);
            }
            using (var pb = new SolidBrush(c95 ? Color.FromArgb(192, 192, 192) : ink))
                g.FillRectangle(pb, (int)Math.Round(_px - PaddleW / 2.0), PaddleY, PaddleW, PaddleH);
            if (_state != State.Over) g.FillRectangle(inkB, (int)Math.Round(_bx), (int)Math.Round(_by), Ball, Ball);

            switch (_state)
            {
                case State.Ready:
                    Message(g, bg, ink, _levelUp ? Loc.T("Level {0}", _level) : null, Loc.T("Click to start"));
                    break;
                case State.Paused: Message(g, bg, ink, null, Loc.T("Paused")); break;
                case State.Over:
                    Message(g, bg, ink, Loc.T("Game over"),
                        _newBest ? Loc.T("New best: {0}", _score) : Loc.T("Score {0} · Best {1}", _score, _settings.BrickBest));
                    break;
            }
        }
        Invalidate();
    }

    // GDI+ with a one-bit hint: GDI's TextRenderer would ClearType it, and the colour fringes show at three times the size
    private static int TextW(Graphics g, string s, Font f) =>
        (int)Math.Ceiling(g.MeasureString(s, f, PointF.Empty, StringFormat.GenericTypographic).Width);

    private static void DrawText(Graphics g, string s, Font f, Color c, int x, int y, bool center)
    {
        if (center) x -= TextW(g, s, f) / 2;
        using var b = new SolidBrush(c);
        g.DrawString(s, f, b, x, y, StringFormat.GenericTypographic);
    }

    /// <summary>A line or two in a cleared box in the middle of the field.</summary>
    private void Message(Graphics g, Color bg, Color ink, string? top, string main)
    {
        const int lineH = 13;
        int lines = top is null ? 1 : 2, h = lines * lineH + 6, y = 76;
        int bw = Math.Max(TextW(g, main, _fontBold), top is null ? 0 : TextW(g, top, _fontBold)) + 12;
        var box = new Rectangle((W - bw) / 2, y, bw, h);
        using (var b = new SolidBrush(bg)) g.FillRectangle(b, box);
        using (var p = new Pen(ink)) g.DrawRectangle(p, box.X, box.Y, box.Width - 1, box.Height - 1);
        int ty = y + 3;
        if (top is not null) { DrawText(g, top, _fontBold, ink, W / 2, ty, center: true); ty += lineH; }
        DrawText(g, main, top is null ? _fontBold : _font, ink, W / 2, ty, center: true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var lcd = Lcd;
        if (Theme.Classic)
        {
            g.Clear(Theme.Face);
            Theme.Bevel(g, ClientRectangle, raised: false);
        }
        else
        {
            g.Clear(Parent?.BackColor ?? Theme.Bg);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var bezel = Theme.RoundedRect(new RectangleF(0, 0, Width - 1, Height - 1), 14))
            using (var bb = new SolidBrush(Color.FromArgb(22, 23, 26))) g.FillPath(bb, bezel);
            g.SmoothingMode = SmoothingMode.None;
        }
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.DrawImage(_screen, lcd);
        g.PixelOffsetMode = PixelOffsetMode.Default;
        if (!Theme.Classic)
        {
            // the LCD's pixel grid: a hairline of the lit background between the dots
            using var grid = new Pen(Color.FromArgb(70, 214, 222, 202));
            for (int x = 1; x < W; x++) g.DrawLine(grid, lcd.X + x * S, lcd.Y, lcd.X + x * S, lcd.Bottom - 1);
            for (int y = 1; y < H; y++) g.DrawLine(grid, lcd.X, lcd.Y + y * S, lcd.Right - 1, lcd.Y + y * S);
            using var shade = new LinearGradientBrush(new Rectangle(lcd.X, lcd.Y, lcd.Width, 24), Color.FromArgb(60, 0, 0, 0), Color.FromArgb(0, 0, 0, 0), 90f);
            g.FillRectangle(shade, lcd.X, lcd.Y, lcd.Width, 24);
        }
    }

    // ---- input: the wheel turns the paddle like the click wheel, the arrows hold it, a click is the centre button ----

    protected override bool IsInputKey(Keys keyData) =>
        (keyData & Keys.KeyCode) is Keys.Left or Keys.Right or Keys.Up or Keys.Down or Keys.Space or Keys.Enter || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            case Keys.Left or Keys.A: _left = true; break;
            case Keys.Right or Keys.D: _right = true; break;
            case Keys.Space or Keys.Enter: if (!e.Handled && !_keyHeld) Centre(); _keyHeld = true; break;
            case Keys.P: if (_state is State.Playing or State.Paused) Centre(); break;
        }
        e.Handled = true;
        base.OnKeyDown(e);
    }
    private bool _keyHeld;   // a held Space would otherwise start and pause on the key's auto-repeat

    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Left or Keys.A) _left = false;
        if (e.KeyCode is Keys.Right or Keys.D) _right = false;
        if (e.KeyCode is Keys.Space or Keys.Enter) _keyHeld = false;
        base.OnKeyUp(e);
    }

    protected override void OnLostFocus(EventArgs e) { _left = _right = false; base.OnLostFocus(e); }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if (_state is State.Ready or State.Playing) MovePaddle(-e.Delta / 120.0 * 12);
        base.OnMouseWheel(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_state is State.Ready or State.Playing && Lcd.Contains(e.Location)) _px = Math.Clamp((e.X - Lcd.X) / (double)S, PaddleW / 2.0, W - PaddleW / 2.0);
        base.OnMouseMove(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        Focus();
        if (e.Button == MouseButtons.Left) Centre();
        base.OnMouseDown(e);
    }

    /// <summary>The render harness: "play" mid-game, "over" the last screen, otherwise the first.</summary>
    public void Demo(string state)
    {
        _timer.Stop();
        if (state is "play" or "over")
        {
            int[] gone = { 3, 4, 10, 11, 12, 13, 19, 20, 27 };
            foreach (int i in gone) _bricks[i / Cols, i % Cols] = false;
            _score = 260; _lives = 2; _px = 92; _bx = 70; _by = 70;
            _state = State.Playing;
        }
        if (state == "over") { _state = State.Over; _score = 1440; _newBest = true; }
        if (_state == State.Ready) ResetBall();
        Render();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _timer.Dispose(); _screen.Dispose(); _font.Dispose(); _fontBold.Dispose(); }
        base.Dispose(disposing);
    }
}

/// <summary>The credits, rolling up the window. Windows 95's own rolled over clouds, and so do these in that look.</summary>
internal sealed class CreditsDialog : CardDialog
{
    public CreditsDialog(double startAt = 0)
    {
        Text = Loc.T("About Mixtape");
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Theme.Classic ? Theme.Face : Theme.Bg;
        ForeColor = Theme.TextCol;
        ClientSize = new Size(440, 340);
        var roll = new CreditsRoll(startAt);
        if (Theme.Classic) roll.SetBounds(8, 8, ClientSize.Width - 16, ClientSize.Height - 16);
        else roll.SetBounds(0, 0, ClientSize.Width, ClientSize.Height);
        roll.MouseDown += (_, _) => Close();
        Controls.Add(roll);
        AdoptCard();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape) { Close(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }
}

internal sealed class CreditsRoll : Control
{
    private const double PxPerSec = 30;
    private readonly (string Text, Easter.Kind Kind)[] _lines = Easter.Credits();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 15 };
    private readonly System.Diagnostics.Stopwatch _clock = new();
    private readonly double _startAt;
    private readonly Dictionary<Easter.Kind, Font> _fonts = new();
    private Bitmap? _clouds, _icon;

    public CreditsRoll(double startAt)
    {
        _startAt = startAt;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        bool c95 = Theme.Classic;
        _fonts[Easter.Kind.Title] = c95 ? new Font("Arial", 20f, FontStyle.Bold, GraphicsUnit.Pixel) : Theme.DisplayFont(20f, FontStyle.Bold);
        _fonts[Easter.Kind.Sub] = c95 ? new Font("Arial", 12f, FontStyle.Regular, GraphicsUnit.Pixel) : Theme.UiFont(9.5f);
        _fonts[Easter.Kind.Heading] = c95 ? new Font("Arial", 12f, FontStyle.Bold, GraphicsUnit.Pixel) : Theme.UiFont(8.5f, FontStyle.Bold);
        _fonts[Easter.Kind.Name] = c95 ? new Font("Arial", 15f, FontStyle.Bold, GraphicsUnit.Pixel) : Theme.UiFont(12f);
        _fonts[Easter.Kind.Note] = c95 ? new Font("Arial", 12f, FontStyle.Italic, GraphicsUnit.Pixel) : Theme.UiFont(9f, FontStyle.Italic);
        _fonts[Easter.Kind.Gap] = _fonts[Easter.Kind.Sub];
        try { if (Environment.ProcessPath is string p) using (var ic = Icon.ExtractAssociatedIcon(p)) _icon = ic?.ToBitmap(); } catch { }
        _timer.Tick += (_, _) => Invalidate();
        if (startAt <= 0) { _timer.Start(); _clock.Start(); }
    }

    private int LineH(Easter.Kind k) => k switch
    {
        Easter.Kind.Title => 30, Easter.Kind.Heading => 20, Easter.Kind.Name => 22, Easter.Kind.Gap => 18, _ => 18,
    };

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        bool c95 = Theme.Classic;
        double t = _startAt > 0 ? _startAt : _clock.Elapsed.TotalSeconds;
        if (c95) PaintSky(g, t); else PaintWash(g, t);

        int total = 72 + _lines.Sum(l => LineH(l.Kind));
        int period = Height + total;
        int y = Height - (int)((t * PxPerSec + Height * 0.45) % period);   // opens with the icon and the name already in view

        g.TextRenderingHint = c95 ? TextRenderingHint.SingleBitPerPixelGridFit : Theme.TextHint;
        if (_icon is not null)
        {
            var ir = new Rectangle((Width - 48) / 2, y, 48, 48);
            g.InterpolationMode = c95 ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic;
            g.DrawImage(_icon, ir);
        }
        y += 60;
        foreach (var (text, kind) in _lines)
        {
            int h = LineH(kind);
            if (text.Length > 0 && y > -h && y < Height)
            {
                string s = kind == Easter.Kind.Heading && !c95 ? text.ToUpper(Loc.Lang == "hu" ? new System.Globalization.CultureInfo("hu-HU") : System.Globalization.CultureInfo.InvariantCulture) : text;
                Color col = c95 ? Color.White
                    : kind switch { Easter.Kind.Heading => Theme.Accent, Easter.Kind.Note or Easter.Kind.Sub => Theme.Subtle, _ => Theme.TextCol };
                var r = new Rectangle(12, y, Width - 24, h);
                const TextFormatFlags F = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.WordEllipsis;
                if (c95)   // a black outline all round: white type stays readable over white cloud
                    for (int ox = -1; ox <= 1; ox++)
                        for (int oy = -1; oy <= 1; oy++)
                            if (ox != 0 || oy != 0) TextRenderer.DrawText(g, s, _fonts[kind], new Rectangle(r.X + ox, r.Y + oy, r.Width, r.Height), Color.Black, F);
                TextRenderer.DrawText(g, s, _fonts[kind], r, col, F);
            }
            y += h;
        }
        if (c95) Theme.Bevel(g, ClientRectangle, raised: false);
        else
        {
            // the lines fade in at the bottom and out at the top
            using var top = new LinearGradientBrush(new Rectangle(0, 0, Width, 40), Theme.Bg, Color.FromArgb(0, Theme.Bg), 90f);
            g.FillRectangle(top, 0, 0, Width, 40);
            using var bot = new LinearGradientBrush(new Rectangle(0, Height - 40, Width, 40), Color.FromArgb(0, Theme.Bg), Theme.Bg, 90f);
            g.FillRectangle(bot, 0, Height - 40, Width, 40);
        }
    }

    /// <summary>Modern: two soft glows in the accent drift behind the text.</summary>
    private void PaintWash(Graphics g, double t)
    {
        g.Clear(Theme.Bg);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        void Glow(double cx, double cy, float r, Color c)
        {
            using var path = new GraphicsPath();
            path.AddEllipse((float)(cx - r), (float)(cy - r), 2 * r, 2 * r);
            using var b = new PathGradientBrush(path) { CenterColor = c, SurroundColors = new[] { Color.FromArgb(0, c) } };
            g.FillPath(b, path);
        }
        Glow(Width * (0.3 + 0.12 * Math.Sin(t * 0.37)), Height * (0.35 + 0.15 * Math.Cos(t * 0.29)), Width * 0.55f, Color.FromArgb(46, Theme.Accent));
        Glow(Width * (0.72 + 0.1 * Math.Cos(t * 0.23)), Height * (0.7 + 0.12 * Math.Sin(t * 0.31)), Width * 0.45f, Color.FromArgb(30, Theme.Blend(Theme.Accent, Color.White, 0.4)));
        g.SmoothingMode = SmoothingMode.None;
    }

    /// <summary>Classic: a blue sky with clouds drifting by, the backdrop of Windows 95's hidden credits.</summary>
    private void PaintSky(Graphics g, double t)
    {
        if (_clouds is null || _clouds.Width != Width || _clouds.Height != Height) { _clouds?.Dispose(); _clouds = MakeClouds(Width, Height); }
        int dx = (int)(t * 8) % Width;
        Theme.PaintClassicPicture(g, new Rectangle(-dx, 0, Width, Height), _clouds, frame: false);
        Theme.PaintClassicPicture(g, new Rectangle(Width - dx, 0, Width, Height), _clouds, frame: false);
    }

    /// <summary>Sky and clouds from a few octaves of value noise that wrap left to right, so the sky can scroll forever.</summary>
    private static Bitmap MakeClouds(int w, int h)
    {
        var rnd = new Random(1995);
        const int cell = 48;
        int gw = Math.Max(1, w / cell + 1);
        var lattice = new double[4][,];
        for (int o = 0; o < 4; o++)
        {
            int cols = gw << o, rows = (h / (cell >> o)) + 2;
            lattice[o] = new double[cols, rows];
            for (int x = 0; x < cols; x++) for (int y = 0; y < rows; y++) lattice[o][x, y] = rnd.NextDouble();
        }
        double Noise(int o, double x, double y)
        {
            var l = lattice[o];
            int cols = l.GetLength(0), rows = l.GetLength(1);
            int x0 = (int)Math.Floor(x), y0 = Math.Min((int)Math.Floor(y), rows - 2);
            double fx = x - x0, fy = y - y0;
            fx = fx * fx * (3 - 2 * fx); fy = fy * fy * (3 - 2 * fy);
            double a = l[((x0 % cols) + cols) % cols, y0], b = l[((x0 + 1) % cols + cols) % cols, y0];
            double c = l[((x0 % cols) + cols) % cols, y0 + 1], d = l[((x0 + 1) % cols + cols) % cols, y0 + 1];
            return (a + (b - a) * fx) * (1 - fy) + (c + (d - c) * fx) * fy;
        }
        // the lattice is gw cells wide at the first octave, so the noise repeats every gw * cell pixels: stretch it to w
        double sx = gw * (double)cell / w;
        var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), System.Drawing.Imaging.ImageLockMode.WriteOnly, bmp.PixelFormat);
        var px = new int[w * h];
        for (int y = 0; y < h; y++)
        {
            double sky = y / (double)h;
            int sr = (int)(40 + 70 * sky), sg = (int)(96 + 70 * sky), sb = (int)(200 + 40 * sky);
            for (int x = 0; x < w; x++)
            {
                double n = 0, amp = 0.5, norm = 0;
                for (int o = 0; o < 4; o++)
                {
                    double cs = cell >> o;
                    n += amp * Noise(o, x * sx / cs, y / cs);
                    norm += amp; amp *= 0.5;
                }
                n /= norm;
                double k = Math.Clamp((n - 0.48) / 0.22, 0, 1);
                k = k * k * (3 - 2 * k);
                int r = (int)(sr + (255 - sr) * k), gg = (int)(sg + (255 - sg) * k), bb = (int)(sb + (255 - sb) * k);
                px[y * w + x] = unchecked((int)0xFF000000) | (r << 16) | (gg << 8) | bb;
            }
        }
        System.Runtime.InteropServices.Marshal.Copy(px, 0, data.Scan0, px.Length);
        bmp.UnlockBits(data);
        return bmp;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose(); _clouds?.Dispose(); _icon?.Dispose();
            foreach (var f in _fonts.Values.Distinct()) f.Dispose();
        }
        base.Dispose(disposing);
    }
}
