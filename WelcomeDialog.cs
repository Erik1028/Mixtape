namespace iPodCommander;

/// <summary>
/// The Windows 95 look's welcome screen - "Welcome to Windows 95" and its "Did you know..." tips, retold for Mixtape:
/// one tip about something the app can do, Next Tip for another, Settings, Close, and the box that decides whether it
/// greets you next time. Shown once the main window is up (Classic only) and from Help, Welcome Screen; each start
/// shows the tip after the one shown last.
/// </summary>
internal sealed class WelcomeDialog : CardDialog
{
    /// <summary>The tips, in English (each is its own translation key). Every one of them names something that is there.</summary>
    internal static readonly string[] Tips =
    {
        "To find a song fast, press Ctrl+F and start typing.",
        "Ctrl+Up and Ctrl+Down step through the list on the left: your library, your playlists and the other pages.",
        "To add songs to a playlist, drag them onto the playlist's name in the list on the left.",
        "Drop music files, or whole folders, anywhere on the Mixtape window and they are copied to your iPod.",
        "In Cover Flow, type a letter to jump to the first album that starts with it. Enter plays the album in the middle.",
        "Click the cover in the player to jump to the song that is playing.",
        "Click the time in the player to see how much of the song is left.",
        "If the lyrics run ahead of the song or behind it, nudge them with the - and + buttons under the words. Mixtape remembers it for that song.",
        "The + next to Playlists also makes smart playlists: songs chosen by rules, such as an artist, a genre or a rating.",
        "Library Doctor, on the iPod's page, finds songs whose files are missing, duplicate songs, and stray files wasting space.",
        "The mini player keeps the controls in a small strip while the main window is out of the way. It is the first of the buttons at the top right.",
        "Your keyboard's play, next and previous keys work even while Mixtape's window is in the background.",
        "Pro Features has a sleep timer: after 15, 30 or 60 minutes the music fades out and stops.",
        "Eject the iPod before you unplug it. If you forget, Mixtape reminds you when you close it.",
        "Settings, Appearance: turn off the 256-colour pictures to see your covers in full colour.",
        "Click the speaker in the player for the volume window, where you can also mute.",
    };

    private int _tip;
    private readonly ToggleSwitch _show;
    private readonly Panel _heading, _box;
    private readonly Font _fWelcome = Theme.DisplayFont(Theme.SzDisplay);
    private readonly Font _fWelcomeBold = Theme.DisplayFont(Theme.SzDisplay, FontStyle.Bold);
    private readonly Font _fKnow = Theme.UiFont(Theme.SzBody, FontStyle.Bold);
    private readonly Font _fTip = Theme.UiFont(Theme.SzBody);

    /// <summary>The tip to show next time (the one after the tip on screen when it closed).</summary>
    public int NextTip => (_tip + 1) % Tips.Length;
    public bool ShowAtStartup => _show.Checked;
    /// <summary>"Settings..." closed it: the host opens Settings next.</summary>
    public bool OpenSettingsAfter { get; private set; }

    public WelcomeDialog(int tip, bool showAtStartup)
    {
        _tip = ((tip % Tips.Length) + Tips.Length) % Tips.Length;
        Text = Loc.T("Welcome");
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        BackColor = Theme.Bg;
        ForeColor = Theme.TextCol;
        Font = Theme.UiFont(Theme.SzBody);
        ClientSize = new Size(500, 262);

        _heading = new Panel { Location = new Point(16, 12), Size = new Size(360, 34), BackColor = Theme.Bg };
        _heading.Paint += (_, e) =>
        {
            // "Welcome to" in the plain face, the name in bold - the way 95 set "Welcome to Windows 95"
            string lead = Loc.T("Welcome to ");
            const TextFormatFlags f = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
            int w = TextRenderer.MeasureText(e.Graphics, lead, _fWelcome, new Size(int.MaxValue, 34), f).Width;
            TextRenderer.DrawText(e.Graphics, lead, _fWelcome, new Rectangle(0, 0, w + 2, 34), Theme.TextCol, f);
            TextRenderer.DrawText(e.Graphics, "Mixtape", _fWelcomeBold, new Rectangle(w, 0, 300, 34), Theme.TextCol, f);
        };
        Controls.Add(_heading);

        _box = new Panel { Location = new Point(16, 54), Size = new Size(358, 162), BackColor = Color.White };
        _box.Paint += (_, e) => PaintTip(e.Graphics);
        Controls.Add(_box);

        ThemedButton Btn(string text, int y) => new() { Text = text, Width = 100, Height = 23, Location = new Point(ClientSize.Width - 16 - 100, y) };
        var next = Btn(Loc.T("Next Tip"), 54);
        next.Click += (_, _) => { _tip = (_tip + 1) % Tips.Length; _box.Invalidate(); };
        var settings = Btn(Loc.T("Settings…"), 54 + 30);
        settings.Click += (_, _) => { OpenSettingsAfter = true; DialogResult = DialogResult.OK; Close(); };
        var close = new ThemedButton { Text = Loc.T("Close"), Primary = true, Width = 100, Height = 23, Location = new Point(ClientSize.Width - 16 - 100, 54 + 162 - 23) };
        close.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        Controls.Add(next); Controls.Add(settings); Controls.Add(close);
        AcceptButton = close;
        CancelButton = close;

        _show = new ToggleSwitch { ClassicLabel = Loc.T("Show this Welcome Screen next time you start Mixtape"), Checked = showAtStartup };
        _show.Size = _show.ClassicLabelSize();
        _show.Location = new Point(16, 54 + 162 + 14);
        Controls.Add(_show);

        AdoptCard();
        ActiveControl = close;
    }

    /// <summary>The tip box: a white sunken panel, the light bulb, "Did you know..." and the tip under it.</summary>
    private void PaintTip(Graphics g)
    {
        var r = _box.ClientRectangle;
        g.Clear(Color.White);
        Theme.Bevel(g, r, raised: false);
        DrawBulb(g, 14, 14);
        TextRenderer.DrawText(g, Loc.T("Did you know..."), _fKnow, new Rectangle(58, 20, r.Width - 70, 20), Theme.TextCol,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(g, Loc.T(Tips[_tip]), _fTip, new Rectangle(58, 50, r.Width - 72, r.Height - 62), Theme.TextCol,
            TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
    }

    // The light bulb of 95's welcome screen, in pixels (Y yellow, W the glint, S / G the silver and grey of the screw
    // base, K black), drawn at twice its size like every large icon of the look.
    private static readonly string[] Bulb =
    {
        ".....KKKKK......",
        "....KYYYYYK.....",
        "...KYYWYYYYK....",
        "..KYYWYYYYYYK...",
        "..KYWYYYYYYYK...",
        "..KYYYYYYYYYK...",
        "..KYYYYYYYYYK...",
        "...KYYYYYYYK....",
        "....KYYYYYK.....",
        ".....KYYYK......",
        ".....KSSSK......",
        ".....KGGGK......",
        ".....KSSSK......",
        ".....KGGGK......",
        "......KKK.......",
    };

    private static void DrawBulb(Graphics g, int x0, int y0)
    {
        for (int y = 0; y < Bulb.Length; y++)
            for (int x = 0; x < Bulb[y].Length; x++)
            {
                Color? c = Bulb[y][x] switch
                {
                    'K' => Color.Black, 'Y' => Color.FromArgb(255, 255, 0), 'W' => Color.White,
                    'S' => Color.FromArgb(192, 192, 192), 'G' => Color.FromArgb(128, 128, 128), _ => null,
                };
                if (c is null) continue;
                using var b = new SolidBrush(c.Value);
                g.FillRectangle(b, x0 + x * 2, y0 + y * 2, 2, 2);
            }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _fWelcome.Dispose(); _fWelcomeBold.Dispose(); _fKnow.Dispose(); _fTip.Dispose(); }
        base.Dispose(disposing);
    }
}
