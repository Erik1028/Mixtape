using System.Drawing.Drawing2D;

namespace iPodCommander;

/// <summary>
/// An always-visible media transport docked under the content (iTunes/Spotify-style "now playing"):
/// cover, title/artist, prev / play-pause / next, a draggable seek bar with elapsed/total time, an
/// equalizer toggle and a volume slider. It owns a tiny audio-only engine and plays the file straight
/// off the iPod (or a local PC file). When nothing is playing it shows a quiet idle state instead of
/// hiding. The layout is RESPONSIVE — as the window narrows it drops the volume slider, then the EQ,
/// then the seek times, then the seek bar, then the title — so the controls never overlap.
/// </summary>
internal sealed class NowPlayingBar : Panel
{
    private readonly AudioPlayer _engine = new(EqualizerSampleProvider.FlatGains(), false);

    public event Action? PrevRequested;
    public event Action? NextRequested;
    public event Action<Rectangle>? EqualizerRequested;   // arg = the button's screen rect (flyout anchor)
    public event Action<Rectangle>? ProRequested;         // opened the Pro-features hub
    public event Action<Rectangle>? QueueRequested;       // opened the Up Next queue popover (arg = button screen rect)
    public event Action<Rectangle>? LyricsRequested;      // opened the live-lyrics popover (arg = button screen rect)
    public event Action? ModesChanged;   // user toggled shuffle/repeat — the host persists it
    public event Action? ArtistClicked, AlbumClicked;   // deck: the card's subtitle links (artist / album)
    public event Action<bool>? RemainingToggled;        // deck: the total time was clicked - it now counts down (or not)
    private bool _showRemaining;
    /// <summary>The card's total-time slot shows "-remaining" instead of the length (a click toggles; the host persists it).</summary>
    public bool ShowRemaining { get => _showRemaining; set { if (_showRemaining == value) return; _showRemaining = value; Invalidate(); } }
    private double _hoverFrac = -1;      // where the pointer sits along the seek line (0..1), -1 when it is elsewhere
    private float _textSlide = 1f;       // the card's two lines arriving with a new song
    private Tween? _textTw;

    public event Action? CoverFlowRequested;            // corner group: open Cover Flow
    public event Action<Point>? AddToPlaylistRequested; // corner group: add the playing song to a playlist (screen anchor)
    public event Action<int>? RateRequested;            // corner group: rate the playing song 0..5
    /// <summary>The host can write ratings back (an iPod track on a writable device); without it the stars stay hidden.</summary>
    public bool CanRate { get; set; }
    /// <summary>The card's text arrives with the song instead of switching under the cover's dissolve.</summary>
    private void SlideCardText()
    {
        _textTw?.Cancel();
        if (!Anim.MotionEnabled) { _textSlide = 1f; return; }
        _textSlide = 0f;
        _textTw = Anim.Run(260, v => { _textSlide = (float)v; if (!IsDisposed) Invalidate(); }, () => _textTw = null, Easings.OutQuint);
    }

    private void ResetStars() { _ratingTw?.Cancel(); _ratingTw = null; _ratingShown = -1; _ratingT = 1f; _starHover = -1; }
    private int _starHover = -1;      // 1..5 while the pointer previews a rating, -1 otherwise
    private float _ratingT = 1f;      // fades a changed rating in
    private int _ratingShown = -1;
    private Tween? _ratingTw;
    /// <summary>The y the host centres the gear and the window buttons on. The deck normally carries everything on
    /// one axis; when it stacks its utilities in two rows the buttons join the TOP one, so the strip reads as a
    /// line of controls instead of a third row floating between the other two.</summary>
    public int CaptionAxis => OnTop && Layout().TwoRow ? TopH / 2 - 13 : TopH / 2;

    public event Action? CoverClicked;   // deck: the card's cover was clicked → the host reveals the playing row
    public event Action<Point>? CardMenuRequested;   // deck: right-click on the card (screen point) → the host's menu
    public event Action<Rectangle>? OverflowRequested;   // deck: the "···" holding what the width could not fit (arg = its screen rect)
    /// <summary>Deck: which utilities the current width folded into the "···" menu.</summary>
    public (bool Eq, bool Pro, bool Queue, bool Modes) Folded { get { var l = Layout(); return (!l.ShowEq, !l.ShowPro, !l.ShowQueue, !l.ShowModes); } }
    /// <summary>Set the repeat mode outright (the "···" menu); raises the same events as the button.</summary>
    public void SetRepeat(RepeatMode m) { if (_repeat == m) return; _repeat = m; if (m == RepeatMode.One) ClearPending(); ModesChanged?.Invoke(); Invalidate(); Changed?.Invoke(); }

    // ---- the top deck (LAB concept) ----
    /// <summary>True when the bar is the window's top deck: transport on the left, a now-playing CARD in the
    /// middle, the utilities on the right, all on the wallpaper. False = the classic bar under the content.</summary>
    public bool OnTop { get; private set; }
    private bool _lyricsOpen;   // the full lyrics view is open → its button reads as pressed
    public void SetLyricsOpen(bool on) { if (_lyricsOpen == on) return; _lyricsOpen = on; Invalidate(); }
    private bool _queueOpen;    // the Up Next side card is docked open → its button reads as pressed
    public void SetQueueOpen(bool on) { if (_queueOpen == on) return; _queueOpen = on; Invalidate(); }
    /// <summary>Deck mode: px at the right edge kept clear for the window buttons + gear (root children over us).</summary>
    public int RightReserve { get; set; } = 220;
    public const int TopH = 78;                      // 12 + card 54 + 12
    internal const int CardH = 54, CardY = 12;
    private readonly Font _fWordmark = Theme.DisplayFont(Theme.SzDisplay, FontStyle.Bold);
    private readonly Font _fCardTitle = Theme.UiFont(Theme.SzTitle, FontStyle.Bold);
    private int _wordW = -1;                         // measured "Mixtape" width (deck wordmark)
    private double _previewPos = -1, _previewDur;    // render harness: a fake position/duration with no audio open

    public void UseTopLayout() { OnTop = true; Dock = DockStyle.None; Height = TopH; Invalidate(); }

    /// <summary>Render harness only: show <paramref name="track"/> as playing at <paramref name="atSec"/> with no
    /// audio. TAKES OWNERSHIP of <paramref name="cover"/>.</summary>
    public void Preview(Track track, Bitmap? cover, double atSec, double durSec, string? path = null)
    {
        _track = track; _path = path;   // a real path lets the lyrics stage load the file's own art (LoadHeroCover)
        SwapCover(cover);
        _playing = true; _playMorph = 1f;
        _previewPos = Math.Max(0, atSec); _previewDur = Math.Max(1, durSec);
        string? hov = Environment.GetEnvironmentVariable("MIX_NP_HOVER");
        if (hov is "seek" or "drag") { _hover = Hit.Seek; _seekKnobR = OnTop ? 6f : 8f; }
        if (hov == "drag") { _drag = Drag.Seek; _scrubFrac = 0.62; }
        Invalidate();
    }

    // The clock the paint reads: the engine's, or the harness preview's.
    private double CurPos => _previewPos >= 0 ? _previewPos : (_engine.IsOpen ? _engine.Position.TotalSeconds : 0);
    private double CurDur => _previewPos >= 0 ? _previewDur : _engine.Duration.TotalSeconds;

    public enum RepeatMode { Off, All, One }
    public bool Shuffle => _shuffle;
    public RepeatMode Repeat => _repeat;
    /// <summary>Restore the saved shuffle/repeat modes (does not raise <see cref="ModesChanged"/>).</summary>
    public void SetModes(bool shuffle, RepeatMode repeat) { _shuffle = shuffle; _repeat = repeat; Invalidate(); }

    private Track? _track;
    private Bitmap? _cover;
    internal const int CoverArtPx = 80;  // resolution to decode the bar cover at (drawn ~56px; headroom for DPI scaling)
    private Bitmap? _coverPrev;          // outgoing cover, held during a track-change cross-dissolve
    private float _coverFade = 1f;       // 0 = cover just changed (show _coverPrev), 1 = settled (show _cover)
    private Tween? _coverTween;
    private bool _playing;
    private float _playMorph;      // play button: 0 = play triangle, 1 = pause bars (cross-faded on the click)
    private Tween? _playTween;
    private float _playTarget;     // the value the in-flight play-morph tween is animating toward (to detect a stale target)
    // Cached per-paint fonts — Theme.UiFont allocates a fresh GDI Font each call, and OnPaint runs ~33fps while
    // playing (the eq tween), so building them inline leaked a font handle every frame. Disposed in Dispose().
    private readonly Font _fTitle = Theme.UiFont(10.5f, FontStyle.Bold);
    private readonly Font _fSub = Theme.UiFont(8.75f);
    private readonly Font _fTime = Theme.UiFont(8f);
    private double _volume = 1.0;
    private double _lastVol = 1.0; // last audible level, restored when unmuting from a dragged-to-zero slider
    private bool _muted;
    private bool _eqOn;            // reflected for the EQ icon tint
    private bool _proOn;           // any Pro feature on → tints the Pro icon
    private int _queueCount;       // Up Next size → tints the queue icon when non-empty
    private bool _gaplessOn, _crossOn, _normalizeOn, _monoOn;  // Pro playback features
    private double _crossSecs = 6; // crossfade length
    private int _sleepMin;         // sleep timer: minutes remaining target (0 = off)
    private int _sleepRemainingSec;
    private System.Windows.Forms.Timer? _sleepTimer;
    private Tween? _sleepFade;
    private bool _prefetched;      // the next track has been queued for this track's boundary
    private Track? _pendingTrack;  // the prefetched next track (flips in at the gapless boundary)
    private string? _pendingPath;
    private Bitmap? _pendingCover;
    private bool _shuffle;
    private RepeatMode _repeat = RepeatMode.Off;
    private string? _path;        // last loaded file path, kept so repeat-one can restart the track
    private SmtcController? _smtc; // Windows media flyout + global media keys (created lazily once the window exists)
    private DiscordPresence? _discord;   // optional "now playing" card on the user's Discord profile
    private bool _discordOn;             // the Settings toggle (off by default)
    private string _discordAppId = "";   // Discord Application ID the user pasted in Settings
    private bool _discordCovers;         // look real album covers up online (opt-in)
    private double _eqPhase;       // animated "now playing" equaliser bars overlaid on the cover
    private Tween? _eqAnim;
    private int _eqTick;
    private readonly float[] _coverViz = new float[4], _coverTmp = new float[4];   // real-audio cover bars
    private readonly EqBarsPainter _eqBars = new();
    private static readonly Rectangle BottomCoverRect = new(16, (H - 56) / 2, 56, 56);
    private Rectangle CoverRect => OnTop ? Layout().Card : BottomCoverRect;   // what a cover change repaints: on the deck the whole card (its text fades with the art)

    private enum Drag { None, Seek, Volume }
    private Drag _drag = Drag.None;
    private double _scrubFrac = -1; // while dragging the seek bar

    public const int H = 88;
    private const int RightPad = 20, ControlsY = 13;
    private int SeekY => H - 24;

    public NowPlayingBar()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.SidebarBg;
        Height = H;

        _engine.Opened += () => Invalidate();
        _engine.PositionTick += () => { MaybePrefetch(); if (_playing && _drag != Drag.Seek) Invalidate(); Tick?.Invoke(); }; // don't repaint when rested/paused (but always notify the mini player)
        _engine.Ended += OnEnded;
        _engine.TrackSwitched += OnGaplessAdvanced;   // the gapless head crossed a boundary internally
        _engine.Failed += msg =>
        {
            if (_track is null) return; // a late failure delivered after StopAndHide (e.g. an iPod switch) — ignore
            _playing = false; _smtc?.Paused(); PushDiscord(false); Invalidate();
            if (!Application.MessageLoop) return;        // never block a headless/automation run
            var form = FindForm();
            if (form is null || !form.Visible) return;    // offscreen render form — don't pop an invisible modal
            BeginInvoke(() => MessageDialog.Show(form, msg, "Preview", MessageBoxButtons.OK, MessageBoxIcon.Information));
        };

        MouseDown += OnDown;
        MouseMove += OnMove;
        MouseUp += OnUp;
        MouseLeave += (_, _) => { _hover = Hit.None; _hoverFrac = -1; _starHover = -1; Tip.Disarm(); RetargetKnobs(); Invalidate(); };
    }

    public bool IsActive => _track is not null;

    // ---- mini-player facade ----
    // A detached MiniPlayerForm mirrors and drives THIS engine (there is only ever one audio engine).
    // It reads the state below and forwards its controls back here; Changed/Tick tell it when to repaint.
    public event Action? Changed; // metadata / play-state / volume changed
    public event Action? Tick;    // playback position advanced (engine tick, ~5 Hz)

    // ---- gapless / crossfade prefetch ----
    /// <summary>Host supplies the next track (+ path + small cover) to pre-decode for gapless/crossfade,
    /// honoring shuffle/repeat, WITHOUT playing it. Null = nothing to queue.</summary>
    public Func<(Track track, string path, Bitmap? cover)?>? NextTrackProvider;
    /// <summary>Raised when gapless/crossfade has advanced to the queued track (so the host updates the
    /// current-track pointer, nav history, and Cover-Flow highlight).</summary>
    public event Action<Track>? AdvancedToNext;

    public Track? NowTrack => _track;
    public bool Playing => _playing;
    public bool Muted => _muted;
    public double VolumeLevel => _muted ? 0 : _volume;
    public double PositionSeconds => _engine.IsOpen ? _engine.Position.TotalSeconds : 0;

    /// <summary>How far <see cref="PositionSeconds"/> leads the sound coming out of the speakers
    /// (see <see cref="AudioPlayer.OutputLead"/>). Subtract it wherever the UI must match what is HEARD.</summary>
    public TimeSpan OutputLead => _engine.OutputLead;
    public double DurationSeconds { get { double d = _engine.Duration.TotalSeconds; return double.IsNaN(d) || d < 0 ? 0 : d; } }
    /// <summary>A private copy of the current cover (caller owns it), or null.</summary>
    public Bitmap? CloneCover() => _cover is null ? null : new Bitmap(_cover);

    /// <summary>A SHARP, high-res square cover for the mini-player hero (the small grid thumbnail held by the
    /// bar would look blurry blown up). Reads the playing file's embedded art at <paramref name="size"/>, falling
    /// back to a crisp generated tile, then to the small thumbnail. Caller owns the returned bitmap.</summary>
    public Bitmap? LoadHeroCover(int size)
    {
        if (_track is null) return _cover is null ? null : new Bitmap(_cover);
        if (!string.IsNullOrEmpty(_path) && ArtworkService.LoadSquare(ArtworkService.KeyFor(_track), _path, size) is { } hi)
            return new Bitmap(hi);
        return Theme.MakeArt(size, (int)(_track.Dbid & 0xffff));   // no embedded art → full-size generated tile
    }

    /// <summary>Play/pause from the mini player (same as the bar's own play button).</summary>
    public void TogglePlayback() => TogglePlay();

    /// <summary>Toggle shuffle from the mini player (mirrors the bar's own shuffle control).</summary>
    public void ToggleShuffle() { _shuffle = !_shuffle; ModesChanged?.Invoke(); Invalidate(); Changed?.Invoke(); }

    /// <summary>Cycle repeat Off → All → One from the mini player.</summary>
    public void CycleRepeat() { _repeat = (RepeatMode)(((int)_repeat + 1) % 3); if (_repeat == RepeatMode.One) ClearPending(); ModesChanged?.Invoke(); Invalidate(); Changed?.Invoke(); }

    /// <summary>Seek to a 0..1 fraction of the track (mini-player seek bar).</summary>
    /// <summary>Re-publish the timeline after a jump (seek / scrub). The Discord card keeps the same text,
    /// so only the moved start time tells the client to redraw the bar — which <see cref="DiscordPresence"/>
    /// now detects.</summary>
    private void DiscordSeeked() { if (_discord is not null && _track is not null) PushDiscord(_playing); }

    public void SeekFraction(double f)
    {
        if (_engine.IsOpen) { _engine.Position = TimeSpan.FromSeconds(Math.Clamp(f, 0, 1) * _engine.Duration.TotalSeconds); InvalidatePrefetch(); DiscordSeeked(); }
        Invalidate(); Changed?.Invoke();
    }

    /// <summary>Set the audible volume 0..1 (mini-player volume slider); 0 mutes.</summary>
    public void SetVolumeLevel(double v)
    {
        _volume = Math.Clamp(v, 0, 1);
        if (_volume > 0.001) _lastVol = _volume;
        _muted = _volume <= 0.001;
        _engine.Volume = _muted ? 0 : _volume;
        Invalidate(); Changed?.Invoke();
    }

    /// <summary>Toggle mute (mini-player speaker icon), restoring the last audible level.</summary>
    public void ToggleMute()
    {
        _muted = !_muted;
        if (!_muted && _volume <= 0.001) _volume = _lastVol > 0.001 ? _lastVol : 0.5;
        _engine.Volume = _muted ? 0 : _volume;
        Invalidate(); Changed?.Invoke();
    }

    /// <summary>Apply equalizer settings to the audio engine (live + on the next track).</summary>
    public void ApplyEq(bool enabled, float[] gains) { _eqOn = enabled; _engine.SetEqEnabled(enabled); _engine.SetEqGains(gains); Invalidate(); }

    /// <summary>Apply Pro-playback settings: gapless, crossfade (+ length), volume normalization, mono.
    /// Crossfade length + normalization + mono apply live; turning gapless/crossfade on or off takes effect on
    /// the next track.</summary>
    /// <summary>Apply the Discord Rich Presence setting live: turning it off (or changing the Application
    /// ID) drops the connection immediately, which is what removes the card from the user's profile.</summary>
    public void ApplyDiscord(bool enabled, string? appId, bool coverArt = false)
    {
        string id = (appId ?? "").Trim();
        if (enabled == _discordOn && id == _discordAppId && coverArt == _discordCovers) return;
        _discordOn = enabled; _discordAppId = id; _discordCovers = coverArt;
        _discord?.Dispose(); _discord = null;                 // rebuilt by EnsureDiscord on the next push
        if (_discordOn && _track is not null) PushDiscord(_playing);
    }

    public void ApplyPro(bool gapless, double crossSecs, bool crossOn, bool normalize, bool mono)
    {
        _gaplessOn = gapless; _crossSecs = crossSecs; _crossOn = crossOn; _normalizeOn = normalize; _monoOn = mono;
        _proOn = gapless || crossOn || normalize || mono || _sleepMin > 0;
        _engine.SetNormalizationEnabled(normalize);
        _engine.SetCrossfade(crossOn, crossSecs);
        _engine.SetMono(mono);
        if (!gapless && !crossOn) ClearPending();   // no seamless advance anymore → stop staging a next
        Invalidate();
    }

    /// <summary>Current sleep-timer setting in minutes (0 = off). Read by the Pro-features dialog.</summary>
    public int SleepMinutes => _sleepMin;

    /// <summary>Arm/cancel the sleep timer: after <paramref name="minutes"/> it fades the audio out and pauses.
    /// 0 cancels and restores full volume. Session-only (not persisted).</summary>
    public void SetSleepMinutes(int minutes)
    {
        _sleepFade?.Cancel(); _sleepFade = null;
        _engine.SetSleepGain(1f);                 // cancel any in-progress fade
        _sleepTimer?.Stop();
        _sleepMin = Math.Max(0, minutes);
        _proOn = _gaplessOn || _crossOn || _normalizeOn || _monoOn || _sleepMin > 0;
        if (_sleepMin == 0) { Invalidate(); return; }
        _sleepRemainingSec = _sleepMin * 60;
        _sleepTimer ??= MakeSleepTimer();
        _sleepTimer.Start();
        Invalidate();
    }

    private System.Windows.Forms.Timer MakeSleepTimer()
    {
        var t = new System.Windows.Forms.Timer { Interval = 1000 };
        t.Tick += (_, _) =>
        {
            if (_sleepRemainingSec <= 0) { t.Stop(); return; }
            if (--_sleepRemainingSec <= 0) { t.Stop(); BeginSleepFade(); }
        };
        return t;
    }

    private void BeginSleepFade()
    {
        if (!Anim.MotionEnabled) { _engine.SetSleepGain(0f); FinishSleep(); return; }
        _sleepFade = Anim.Run(5000, v => _engine.SetSleepGain(1f - (float)v), FinishSleep, Easings.Linear);   // 5 s fade
    }

    private void FinishSleep()
    {
        _sleepFade = null;
        _sleepMin = 0;
        if (_playing) { _engine.Pause(); _playing = false; _smtc?.Paused(); PushDiscord(false); StopEq(); }
        _engine.SetSleepGain(1f);                 // restore for the next play
        _proOn = _gaplessOn || _crossOn || _normalizeOn || _monoOn;
        Invalidate(); Changed?.Invoke();
    }

    /// <summary>Abort an IN-FLIGHT sleep fade (the final 5 s ramp) when playback is (re)started or torn down — else
    /// the fade would keep ramping the NEW track's gain to silence and FinishSleep would pause it. A sleep timer
    /// that is still COUNTING DOWN (no fade yet) is intentionally left armed, so it survives normal track changes.</summary>
    private void CancelSleepFade()
    {
        if (_sleepFade is null) return;
        _sleepFade.Cancel(); _sleepFade = null;
        _engine.SetSleepGain(1f);
        _sleepMin = 0;
        _proOn = _gaplessOn || _crossOn || _normalizeOn || _monoOn;
        Invalidate();
    }

    /// <summary>Pause playback (e.g. when a video preview opens) without clearing the bar. Returns true if it was playing.</summary>
    public bool Pause() { if (!_playing) return false; _engine.Pause(); _playing = false; _smtc?.Paused(); PushDiscord(false); StopEq(); Invalidate(); Changed?.Invoke(); return true; }

    /// <summary>Resume after an external pause (e.g. when the video preview closes).</summary>
    public void Resume() { if (_track is not null && !_playing) { _engine.Play(); _playing = true; _smtc?.Playing(); PushDiscord(true); StartEq(); Invalidate(); Changed?.Invoke(); } }

    /// <summary>Load and play a track's file. <paramref name="cover"/> may be null (a gradient is used).</summary>
    public void Play(Track track, string filePath, Bitmap? cover)
    {
        CancelSleepFade();   // a (re)start during the final fade means the user is still listening — don't fade/pause it
        _track = track;
        _path = filePath;
        SlideCardText();
        ResetStars();        // the new song's rating shows outright, it does not fade in from the old one
        SwapCover(ResolveCover(track, filePath, cover));   // prefer the file's own embedded art; cross-dissolve in
        ClearPending();
        _engine.Volume = _muted ? 0 : _volume;
        if (_gaplessOn || _crossOn)
        {
            _engine.StartGapless(filePath, _crossOn, _crossSecs, _normalizeOn);   // persistent chain; starts playback itself
        }
        else
        {
            _engine.CloseMedia();
            _engine.Load(filePath);
            _engine.Play();
        }
        _playing = true;
        _scrubFrac = -1;
        EnsureSmtc();
        _smtc?.SetMetadata(track.DisplayTitle, track.Artist, track.Album, _cover);
        _smtc?.Playing();
        PushDiscord(true);
        StartEq();
        Invalidate();
        Changed?.Invoke();
    }

    /// <summary>The cover to show for a track: its OWN embedded art (album-cached, rounded), or — only if the file
    /// has none — the caller's bitmap (a song-row thumbnail), or null. The caller keeps ownership of
    /// <paramref name="supplied"/>; the returned bitmap is always a fresh copy the bar owns. This is what stops the
    /// bar from showing a generated ♪ placeholder while a track that actually has art is playing: the row thumbnail
    /// the caller passes can still be an unreplaced placeholder (or null in text-only list mode).</summary>
    private static Bitmap? ResolveCover(Track track, string? filePath, Bitmap? supplied)
    {
        if (!string.IsNullOrEmpty(filePath) && ArtworkService.Load(ArtworkService.KeyFor(track), filePath, CoverArtPx) is { } art)
            return new Bitmap(art);   // ArtworkService returns a shared cached bitmap → clone so our Dispose() can't free it
        CoverDownloads.Request(track, filePath);   // no cover anywhere: ask the internet (no-op while that is off)
        return supplied is null ? null : new Bitmap(supplied);
    }

    /// <summary>A downloaded cover landed for <paramref name="baseKey"/>: if that is the playing album, take it.</summary>
    public void RefreshCover(string baseKey)
    {
        if (_track is null || _cover is not null || CoverDownloads.BaseKey(ArtworkService.KeyFor(_track)) != baseKey) return;
        if (!string.IsNullOrEmpty(_path) && ArtworkService.Load(ArtworkService.KeyFor(_track), _path, CoverArtPx) is { } art)
        {
            SwapCover(new Bitmap(art));
            _smtc?.SetMetadata(_track.DisplayTitle, _track.Artist, _track.Album, _cover);
            Changed?.Invoke();
        }
    }

    /// <summary>Swap the bar cover, cross-dissolving from the outgoing one (same 220 ms art fade the header uses) so
    /// a track change doesn't pop. TAKES OWNERSHIP of <paramref name="next"/>; disposes the outgoing when the fade ends.</summary>
    private void SwapCover(Bitmap? next)
    {
        _coverTween?.Cancel(); _coverTween = null;
        _coverPrev?.Dispose();
        _coverPrev = _cover;     // hold the outgoing cover for the dissolve
        _cover = next;
        ComputeAccentTint();     // seek fill + eq bars drift toward the new cover's dominant colour
        if (_coverPrev is null || !Anim.MotionEnabled)
        {
            _coverPrev?.Dispose(); _coverPrev = null; _coverFade = 1f;   // nothing to dissolve from (idle → first cover)
        }
        else
        {
            _coverFade = 0f;
            _coverTween = Anim.Run(220,
                v => { _coverFade = (float)v; if (!IsDisposed) Invalidate(CoverRect); },
                () => { _coverTween = null; _coverPrev?.Dispose(); _coverPrev = null; _coverFade = 1f; if (!IsDisposed) Invalidate(CoverRect); },
                Easings.OutCubic);
        }
        Invalidate(CoverRect);
    }

    /// <summary>Derive the seek-fill / eq-bar accent tint from the current cover's dominant colour (a 1px downscale sample).
    /// Falls back to the theme Accent for grey/near-black/near-white covers; otherwise blends the sample toward AccentBright
    /// so it stays vivid + on-brand rather than muddy. Cheap, one-shot per cover change.</summary>
    private void ComputeAccentTint() => _accentTint = AccentTintFor(_cover);

    /// <summary>The cover-derived accent (see <see cref="ComputeAccentTint"/>) for any surface that shows a cover —
    /// the mini player's card too.</summary>
    internal static Color AccentTintFor(Bitmap? cover)
    {
        try
        {
            if (cover is null) return Theme.Accent;
            using var tiny = new Bitmap(1, 1);
            using (var g = Graphics.FromImage(tiny)) { g.InterpolationMode = InterpolationMode.HighQualityBilinear; g.DrawImage(cover, 0, 0, 1, 1); }
            Color c = tiny.GetPixel(0, 0);
            float max = Math.Max(c.R, Math.Max(c.G, c.B)) / 255f, min = Math.Min(c.R, Math.Min(c.G, c.B)) / 255f;
            float sat = max <= 0f ? 0f : (max - min) / max;
            float lum = (0.299f * c.R + 0.587f * c.G + 0.114f * c.B) / 255f;
            return (sat < 0.22f || lum < 0.12f || lum > 0.9f) ? Theme.Accent : Theme.Blend(c, Theme.AccentBright, 0.42);
        }
        catch { return Theme.Accent; }
    }

    /// <summary>Stop playback and return the bar to its idle state (it stays visible).</summary>
    public void StopAndHide()
    {
        CancelSleepFade();
        _engine.CloseMedia();
        ClearPending();
        _track = null;
        _path = null;
        _coverTween?.Cancel(); _coverTween = null; _coverPrev?.Dispose(); _coverPrev = null; _coverFade = 1f;
        _cover?.Dispose(); _cover = null;
        _accentTint = Theme.Accent;
        _playing = false;
        _scrubFrac = -1;
        _smtc?.Stopped();
        _discord?.Clear();   // _track is already null here, so clear explicitly rather than via PushDiscord
        StopEq();
        Invalidate();
        Changed?.Invoke();
    }

    // Create the system-media-controls bridge once the hosting window exists (skipped in headless renders).
    private void EnsureSmtc()
    {
        if (_smtc is not null || !Application.MessageLoop) return;
        var form = FindForm();
        if (form is null || !form.IsHandleCreated) return;
        _smtc = new SmtcController(form.Handle, a => { try { if (IsHandleCreated) BeginInvoke(a); } catch { } });
        _smtc.PlayPause += MediaPlayPause;
        _smtc.Next += () => NextRequested?.Invoke();
        _smtc.Previous += () => PrevRequested?.Invoke();
    }

    // Same lazy shape as EnsureSmtc, minus the window handle (Discord needs none). Skipped in headless
    // renders so the --render harness never opens a socket, and while the setting is off / unconfigured.
    private void EnsureDiscord()
    {
        if (_discord is not null || !_discordOn || _discordAppId.Length == 0 || !Application.MessageLoop) return;
        _discord = new DiscordPresence(_discordAppId, _discordCovers);
    }

    /// <summary>Publish the current song to Discord. Cheap and non-blocking: the controller de-duplicates by
    /// what the card shows and does all I/O on its own thread, so calling it from any state change is safe.</summary>
    private void PushDiscord(bool playing)
    {
        EnsureDiscord();
        if (_discord is null) return;
        var t = _track;
        if (t is null) { _discord.Clear(); return; }
        double dur = DurationSeconds > 0 ? DurationSeconds : t.LengthMs / 1000.0;   // LengthMs when the engine hasn't parsed one
        _discord.SetTrack(t.DisplayTitle, t.Artist, t.Album,
                          TimeSpan.FromSeconds(PositionSeconds), TimeSpan.FromSeconds(dur), playing);
    }

    private void OnEnded()
    {
        // Repeat-one: restart the same file (a clean reload — WaveOut can't simply resume past its end).
        if (_repeat == RepeatMode.One && _track is not null && _path is not null)
        {
            ClearPending();   // drop any staged next (its reader is freed by CloseMedia anyway)
            _engine.CloseMedia();
            _engine.Volume = _muted ? 0 : _volume;
            _engine.Load(_path);
            _engine.Play();
            _playing = true; _scrubFrac = -1;
            _smtc?.Playing();
            PushDiscord(true);   // same song, but the position jumped back to 0 -> fresh timestamps
            Invalidate();
            Changed?.Invoke();
            return;
        }
        // Otherwise advance (the host applies shuffle / repeat-all); if there's no next it rests, paused.
        _playing = false;
        _smtc?.Paused();   // Play() will flip back to Playing if a next track starts
        PushDiscord(false);
        StopEq();          // (Play() restarts it if a next track begins)
        Invalidate();
        Changed?.Invoke();
        NextRequested?.Invoke();
    }

    /// <summary>Play/pause from a hardware media key or the system transport controls.</summary>
    public void MediaPlayPause() => TogglePlay();

    private void TogglePlay()
    {
        if (_track is null) return;
        if (_playing) { _engine.Pause(); _playing = false; _smtc?.Paused(); PushDiscord(false); StopEq(); }
        else { _engine.Play(); _playing = true; _smtc?.Playing(); PushDiscord(true); StartEq(); }
        AnimatePlay();
        Changed?.Invoke();
    }

    /// <summary>Cross-fade the play button between the triangle and the pause bars when the user toggles it
    /// (the most-clicked control). Other state changes are reflected instantly by DrawPlayButton.</summary>
    private void AnimatePlay()
    {
        float to = _playing ? 1f : 0f;
        _playTween?.Cancel(); _playTween = null;
        _playTarget = to;
        if (!Anim.MotionEnabled || _track is null) { _playMorph = to; Invalidate(); return; }
        float from = _playMorph;
        _playTween = Anim.Run(160, v => { _playMorph = from + (to - from) * (float)v; if (!IsDisposed) Invalidate(); },
            () => { _playTween = null; _playMorph = to; if (!IsDisposed) Invalidate(); }, Easings.OutCubic);
    }

    // The animated equaliser bars (on the cover, while playing). A looping tween advances a phase and
    // repaints ONLY the cover region (throttled) so it costs almost nothing and stops the moment playback does.
    private void StartEq()
    {
        if (_eqAnim is { IsRunning: true } || !Anim.MotionEnabled) { Invalidate(CoverRect); return; }
        _eqAnim = Anim.Run(1_000_000_000, _ => { _eqPhase += 0.22; UpdateCoverViz(); if ((++_eqTick & 1) == 0) Invalidate(CoverRect); }, null, Easings.Linear);
    }
    private void StopEq() { _eqAnim?.Cancel(); _eqAnim = null; Invalidate(CoverRect); }

    // Ease the cover bars toward the live spectrum (fast attack, slow decay) — falls to a gentle baseline in quiet passages.
    private void UpdateCoverViz()
    {
        bool live = _playing && _engine.Visualizer.Read(_coverTmp);
        for (int i = 0; i < _coverViz.Length; i++)
        {
            float target = live ? _coverTmp[i] : 0f;
            _coverViz[i] += (target - _coverViz[i]) * (target > _coverViz[i] ? 0.5f : 0.16f);
        }
    }

    /// <summary>Spectrum for the mini-player strip (0..1 per band); false when nothing is audible/playing.</summary>
    public bool ReadSpectrum(float[] bands) => _playing && _engine.Visualizer.Read(bands);

    /// <summary>Drop any already-committed gapless prefetch so the next tick re-evaluates "what's next"
    /// (call after the Up Next queue changes, e.g. a late "Play next").</summary>
    public void InvalidatePrefetch() { if (_engine.GaplessActive) ClearPending(); }

    private void ClearPending()
    {
        _prefetched = false;
        _pendingTrack = null; _pendingPath = null;
        _pendingCover?.Dispose(); _pendingCover = null;
        _engine.ClearNext();
    }

    // On a position tick: when close to the end of the current track in gapless/crossfade mode, ask the host
    // for the next track and pre-decode it so the boundary is seamless. Runs once per track.
    private void MaybePrefetch()
    {
        if (!_engine.GaplessActive || _prefetched || _repeat == RepeatMode.One) return;
        double dur = _engine.Duration.TotalSeconds, pos = _engine.Position.TotalSeconds;
        if (dur <= 0) return;
        double lead = Math.Max(_crossOn ? _crossSecs + 2.0 : 1.5, 1.5);   // leave time for the decode before the boundary
        if (dur - pos > lead) return;
        var nx = NextTrackProvider?.Invoke();
        if (nx is null) return;                   // nothing to queue yet (e.g. list refreshing) — retry next tick
        try { _engine.EnqueueNext(nx.Value.path); }
        catch { nx.Value.cover?.Dispose(); return; }   // a corrupt next file — don't latch, don't crash the UI tick
        _prefetched = true;                       // latch only AFTER a successful enqueue
        _pendingTrack = nx.Value.track; _pendingPath = nx.Value.path;
        // Same as Play: prefer the next file's own embedded art so a gapless/crossfade advance never flips to a
        // placeholder. ResolveCover clones, so dispose the provider's bitmap afterwards.
        _pendingCover?.Dispose();
        _pendingCover = ResolveCover(nx.Value.track, nx.Value.path, nx.Value.cover);
        nx.Value.cover?.Dispose();
    }

    // The gapless head crossed into the queued track (marshaled to the UI thread by AudioPlayer): flip the
    // now-playing metadata/cover exactly once, then let the host update its pointer + highlight.
    private void OnGaplessAdvanced(string path)
    {
        if (_pendingTrack is null) { _prefetched = false; return; }   // unstaged switch — resync so prefetch can recover
        _track = _pendingTrack;
        _path = _pendingPath ?? path;
        SwapCover(_pendingCover);                   // cross-dissolve to the prefetched cover (takes ownership)
        _pendingTrack = null; _pendingPath = null; _pendingCover = null;
        _prefetched = false;                       // prefetch the following track on the next tick
        _playing = true;
        EnsureSmtc();
        _smtc?.SetMetadata(_track.DisplayTitle, _track.Artist, _track.Album, _cover);
        _smtc?.Playing();
        PushDiscord(true);
        StartEq();
        Invalidate();
        Changed?.Invoke();
        AdvancedToNext?.Invoke(_track);
    }

    // ---- responsive layout (one source of truth for paint + hit-testing) ----
    private struct Lo
    {
        public Rectangle Cover; public int TextX, TextW; public bool ShowTitle;
        public Rectangle Prev, Play, Next;
        public Rectangle Shuffle, Repeat; public bool ShowModes;
        public Rectangle Seek; public bool ShowSeek, ShowTimes;
        public Rectangle Eq; public bool ShowEq;
        public Rectangle Pro; public bool ShowPro;
        public Rectangle Queue; public bool ShowQueue;
        public Rectangle Lyrics; public bool ShowLyrics;
        public Rectangle Speaker; public bool ShowSpeaker;
        public Rectangle Vol; public bool ShowVol;
        // deck only
        public Rectangle Card; public bool ShowCard;
        public Rectangle Overflow; public bool ShowOverflow;   // the "···" for the folded utilities
        public Rectangle Times;                       // elapsed over total, right-aligned inside the card
        public Rectangle Logo, Wordmark; public bool ShowWordmark;
        public Rectangle ArtistR, AlbumR, TotalR;     // the card's two subtitle links and the total-time toggle
        public bool TwoRow;                           // the utilities are stacked in two rows (narrow window)
        public Rectangle Flow, AddTo, StarsR; public bool ShowExtras;   // the corner group under the window buttons
    }

    private Lo Layout()
    {
        if (OnTop) return LayoutTop();
        int w = Width;
        var l = new Lo { Cover = new Rectangle(16, (H - 56) / 2, 56, 56) };
        int leftBound = l.Cover.Right + 8;

        // Right cluster, built from the right edge inward; widgets appear only when there's room.
        // Two rows: the volume slider with its speaker (mute) icon paired on top, the control icons
        // (eq · pro · queue) in a row beneath. Stacking frees horizontal space, so the icons appear
        // earlier as the window narrows.
        l.ShowVol = w >= 520;
        l.ShowSpeaker = w >= 470;
        l.ShowEq = w >= 520;
        l.ShowPro = w >= 560;
        l.ShowQueue = w >= 600;
        l.ShowLyrics = w >= 660;   // the widest-window extra: it drops out first when space runs short
        int volY = 26, iconY = l.ShowVol ? 47 : (H - 24) / 2;   // icons drop below the slider; centre them if no slider

        // Top row: the volume slider hard against the right pad, with the speaker (mute) icon just to its
        // left so the two read as one volume control (the conventional pairing).
        if (l.ShowVol) l.Vol = new Rectangle(w - RightPad - 92, volY, 92, 4);
        if (l.ShowSpeaker && l.ShowVol)
            l.Speaker = new Rectangle(l.Vol.Left - 8 - 20, volY - 9, 20, 22);   // centred on the slider, to its left

        // Bottom row, built right → left: queue · pro · eq — plus the speaker here only when the window is
        // too narrow for the slider (so the mute toggle stays reachable).
        int rc = w - RightPad;
        if (l.ShowSpeaker && !l.ShowVol) { l.Speaker = new Rectangle(rc - 20, iconY + 1, 20, 22); rc = l.Speaker.Left - 14; }
        if (l.ShowEq) { l.Eq = new Rectangle(rc - 24, iconY, 24, 24); rc = l.Eq.Left - 14; }
        if (l.ShowPro) { l.Pro = new Rectangle(rc - 24, iconY, 24, 24); rc = l.Pro.Left - 14; }
        if (l.ShowQueue) { l.Queue = new Rectangle(rc - 24, iconY, 24, 24); rc = l.Queue.Left - 14; }
        if (l.ShowLyrics) { l.Lyrics = new Rectangle(rc - 24, iconY, 24, 24); rc = l.Lyrics.Left - 14; }
        // Keep the centred transport clear of BOTH rows' left-most widget.
        int topLeft = l.ShowVol ? (l.ShowSpeaker ? l.Speaker.Left : l.Vol.Left) - 12 : int.MaxValue;
        int rightStart = Math.Min(rc, topLeft);

        // Transport centred on the WINDOW centre (not just between cover and cluster), clamped so it never
        // collides with the left info or the right cluster. Shuffle/repeat flank prev/play/next when wide.
        const int half = 61, modeW = 26, modeGap = 12;
        l.ShowModes = w >= 600;
        int blockHalf = l.ShowModes ? half + modeGap + modeW : half;
        int cx = Math.Clamp(w / 2, leftBound + blockHalf, Math.Max(leftBound + blockHalf, rightStart - blockHalf));
        l.Play = new Rectangle(cx - 19, ControlsY, 38, 38);
        l.Prev = new Rectangle(l.Play.Left - 12 - 30, ControlsY + 4, 30, 30);
        l.Next = new Rectangle(l.Play.Right + 12, ControlsY + 4, 30, 30);
        if (l.ShowModes)
        {
            l.Shuffle = new Rectangle(l.Prev.Left - modeGap - modeW, ControlsY + 6, modeW, modeW);
            l.Repeat = new Rectangle(l.Next.Right + modeGap, ControlsY + 6, modeW, modeW);
        }

        // Title zone on the left (cover → just before the transport block).
        l.TextX = l.Cover.Right + 12;
        l.TextW = (l.ShowModes ? l.Shuffle.Left : l.Prev.Left) - 14 - l.TextX;
        l.ShowTitle = l.TextW >= 90;

        // Seek bar CENTRED beneath the transport (fixed max width, centred on cx); times at its ends when wide.
        l.ShowSeek = w >= 460;
        l.ShowTimes = w >= 740;
        int tm = l.ShowTimes ? 50 : 8;
        int seekHalf = Math.Max(40, Math.Min(230, Math.Min(cx - leftBound - tm, rightStart - cx - tm)));
        l.Seek = new Rectangle(cx - seekHalf, SeekY, seekHalf * 2, 5);
        return l;
    }

    /// <summary>The deck: ONE axis (y = TopH/2) carries everything. Left → right: logo + wordmark, the transport
    /// (shuffle · prev · play · next · repeat), the now-playing card centred in whatever is left, then lyrics ·
    /// queue · pro · eq · speaker · volume, and (root children, not ours) the gear + window buttons inside
    /// <see cref="RightReserve"/>. Narrower windows shed, in order: the volume slider, pro, eq, shuffle/repeat,
    /// the queue, the wordmark text — the card and the core transport stay.</summary>
    private Lo LayoutTop()
    {
        int w = Width, cy = TopH / 2;
        var l = new Lo();
        if (_wordW < 0) _wordW = TextRenderer.MeasureText("Mixtape", _fWordmark, new Size(400, 40), TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width + 4;
        // What fits: the CARD comes first. With only the essentials placed (logo, prev/play/next, the card at
        // its 300 px minimum, lyrics, speaker, the window buttons' reserve) whatever is left is handed out in
        // order of usefulness: the volume slider, the wordmark, shuffle/repeat, queue, EQ, Pro. (Fixed width
        // thresholds let the modes and the slider both appear at ~1000 px and squeeze the card to 226 px,
        // narrower than at the smallest window - the "artist / album" line lost its album.)
        const int CardMin = 300;
        int slack0 = (w - RightReserve - 72 - 22) - (44 + 22 + 108 + 22) - CardMin;
        // Nothing is ever lost: a utility that does not fit folds into a "···" menu (32 px). Allocate once without
        // it; if a utility got shed, allocate again with the "···" taken off the top.
        int slack = slack0;
        bool Take(int cost) { if (slack < cost) return false; slack -= cost; return true; }
        void Allocate()
        {
            l.ShowVol = Take(84 + 10);
            l.ShowWordmark = Take(8 + _wordW);
            l.ShowModes = Take(34 + 34);
            l.ShowQueue = Take(24 + 8);
            l.ShowEq = Take(24 + 8);
            l.ShowPro = Take(24 + 8);
        }
        Allocate();
        // Below the width where every utility fits on the axis nothing folds into a "..." menu any more: the strip
        // is 78 px tall, so the compact deck lays the utilities out in TWO ROWS instead (LayoutTopCompact).
        if (!(l.ShowModes && l.ShowQueue && l.ShowEq && l.ShowPro)) return LayoutTopCompact(w, cy);
        l.ShowOverflow = false;
        l.ShowSpeaker = true; l.ShowLyrics = true;
        l.Logo = new Rectangle(24, cy - 10, 20, 20);
        l.Wordmark = new Rectangle(52, cy - 15, _wordW, 30);
        int leftEdge = (l.ShowWordmark ? l.Wordmark.Right : l.Logo.Right) + 22;
        int rightEdge = w - RightReserve;

        // Right cluster, right → left.
        int rc = rightEdge - 8;
        if (l.ShowVol) { l.Vol = new Rectangle(rc - 84, cy - 2, 84, 4); rc = l.Vol.Left - 10; }
        if (l.ShowSpeaker) { l.Speaker = new Rectangle(rc - 20, cy - 11, 20, 22); rc = l.Speaker.Left - 12; }
        if (l.ShowEq) { l.Eq = new Rectangle(rc - 24, cy - 12, 24, 24); rc = l.Eq.Left - 8; }
        if (l.ShowPro) { l.Pro = new Rectangle(rc - 24, cy - 12, 24, 24); rc = l.Pro.Left - 8; }
        if (l.ShowQueue) { l.Queue = new Rectangle(rc - 24, cy - 12, 24, 24); rc = l.Queue.Left - 8; }
        if (l.ShowLyrics) { l.Lyrics = new Rectangle(rc - 24, cy - 12, 24, 24); rc = l.Lyrics.Left - 8; }
        if (l.ShowOverflow) { l.Overflow = new Rectangle(rc - 24, cy - 12, 24, 24); rc = l.Overflow.Left - 8; }
        int clusterLeft = rc;

        // Transport, left-aligned after the wordmark.
        int tx = leftEdge;
        if (l.ShowModes) { l.Shuffle = new Rectangle(tx, cy - 13, 26, 26); tx = l.Shuffle.Right + 8; }
        l.Prev = new Rectangle(tx, cy - 15, 30, 30); tx = l.Prev.Right + 6;
        l.Play = new Rectangle(tx, cy - 18, 36, 36); tx = l.Play.Right + 6;
        l.Next = new Rectangle(tx, cy - 15, 30, 30); tx = l.Next.Right;
        if (l.ShowModes) { l.Repeat = new Rectangle(tx + 8, cy - 13, 26, 26); tx = l.Repeat.Right; }

        // On one axis the window buttons already own the corner, so the group joins the cluster instead —
        // but only while the card can still keep its minimum, since the card comes first at every width.
        if (_track is not null)
        {
            int starW = CanRate ? 15 + 5 * 17 : 0;
            int extrasW = 24 + 8 + 24 + starW;
            if (clusterLeft - extrasW - 8 - 22 - (tx + 22) >= CardMin)
            {
                int ex = clusterLeft - extrasW;
                l.Flow = new Rectangle(ex, cy - 12, 24, 24);
                l.AddTo = new Rectangle(ex + 32, cy - 12, 24, 24);
                if (CanRate) l.StarsR = new Rectangle(ex + 32 + 24 + 15, cy - 10, 5 * 17, 20);
                l.ShowExtras = true;
                clusterLeft = ex - 14;   // a little more air than the 8 px between icons: the group is its own unit
            }
        }

        // The card: centred in the free span, capped so it stays a card and not a banner.
        int spanL = tx + 22, spanR = clusterLeft - 22;
        int cardW = Math.Clamp(spanR - spanL, 0, 520);
        l.ShowCard = cardW >= 150;
        int cardX = spanL + (spanR - spanL - cardW) / 2;
        l.Card = new Rectangle(cardX, CardY, cardW, CardH);
        var cg = LayoutCard(l.Card);
        l.Cover = cg.Cover; l.TextX = cg.TextX; l.TextW = cg.TextW; l.ShowTimes = cg.ShowTimes; l.Times = cg.Times;
        l.ShowTitle = l.ShowCard && cg.ShowTitle;
        l.ShowSeek = l.ShowCard;
        l.Seek = cg.Seek;
        if (_track is not null && l.ShowTitle) { var (ar, al) = SubtitleSpans(_track, cg, _fSub); l.ArtistR = ar; l.AlbumR = al; }
        if (l.ShowCard && cg.ShowTimes) l.TotalR = new Rectangle(cg.Times.X - 4, l.Card.Y + 25, cg.Times.Width + 4, 12);
        return l;
    }

    /// <summary>The deck below the width where every utility fits on the axis. Instead of folding into a "..." menu
    /// it uses the strip's HEIGHT: shuffle and repeat stack beside the transport, and on the right the speaker and
    /// the volume ride the upper row with lyrics, queue, pro and eq on the lower one - every control stays in
    /// reach down to the window's minimum width, and the card takes whatever the middle leaves. The wordmark is
    /// the one extra that still needs spare room.</summary>
    /// <summary>The corner group (Cover Flow · add to playlist │ rating), right-aligned to the window buttons' own
    /// edge on the row below them. It appears only where it does not reach into the utilities beside it.</summary>
    private void PlaceExtras(ref Lo l, int w, int row, int rightEdge)
    {
        const int StarCell = 17, Stars = 5;
        int gRight = w - 10;
        l.StarsR = new Rectangle(gRight - Stars * StarCell, row - 10, Stars * StarCell, 20);
        int gx = (CanRate && _track is not null ? l.StarsR.Left - 15 : gRight) - 24;   // 15 = 7 + the rule + 7
        l.AddTo = new Rectangle(gx, row - 12, 24, 24);
        l.Flow = new Rectangle(gx - 8 - 24, row - 12, 24, 24);
        l.ShowExtras = l.Flow.Left > rightEdge + 4;
    }

    private Lo LayoutTopCompact(int w, int cy)
    {
        var l = new Lo();
        l.ShowSpeaker = l.ShowLyrics = l.ShowQueue = l.ShowEq = l.ShowPro = l.ShowModes = l.ShowVol = true;
        l.ShowOverflow = false;
        l.TwoRow = true;
        int rowA = cy - 13, rowB = cy + 13;   // the two rows' centres, 26 px apart, inside the card's height
        const int Block = 120;                // the right block: max(speaker 20 + 10 + slider 84, four 24 px glyphs at a 32 px pitch)
        const int CardMin = 300;
        int slack = (w - RightReserve - 8 - Block - 22) - (44 + 22 + 108 + 10 + 26 + 22) - CardMin;
        l.ShowWordmark = slack >= 8 + _wordW;
        l.Logo = new Rectangle(24, cy - 10, 20, 20);
        l.Wordmark = new Rectangle(52, cy - 15, _wordW, 30);
        int leftEdge = (l.ShowWordmark ? l.Wordmark.Right : l.Logo.Right) + 22;
        int rightEdge = w - RightReserve;

        // Right block: both rows right-aligned to the same edge.
        int rc = rightEdge - 8;
        l.Vol = new Rectangle(rc - 84, rowA - 2, 84, 4);
        l.Speaker = new Rectangle(l.Vol.Left - 10 - 20, rowA - 11, 20, 22);
        int bx = rc;
        l.Eq = new Rectangle(bx - 24, rowB - 12, 24, 24); bx = l.Eq.Left - 8;
        l.Pro = new Rectangle(bx - 24, rowB - 12, 24, 24); bx = l.Pro.Left - 8;
        l.Queue = new Rectangle(bx - 24, rowB - 12, 24, 24); bx = l.Queue.Left - 8;
        l.Lyrics = new Rectangle(bx - 24, rowB - 12, 24, 24);
        int clusterLeft = Math.Min(l.Lyrics.Left, l.Speaker.Left) - 8;

        PlaceExtras(ref l, w, rowB, rightEdge);   // the corner group, in the space the window buttons left free

        // Transport, then shuffle over repeat in one column.
        int tx = leftEdge;
        l.Prev = new Rectangle(tx, cy - 15, 30, 30); tx = l.Prev.Right + 6;
        l.Play = new Rectangle(tx, cy - 18, 36, 36); tx = l.Play.Right + 6;
        l.Next = new Rectangle(tx, cy - 15, 30, 30); tx = l.Next.Right + 10;
        l.Shuffle = new Rectangle(tx, rowA - 13, 26, 26);
        l.Repeat = new Rectangle(tx, rowB - 13, 26, 26);
        tx = l.Repeat.Right;

        // The card: centred in the free span, capped so it stays a card and not a banner.
        int spanL = tx + 22, spanR = clusterLeft - 22;
        int cardW = Math.Clamp(spanR - spanL, 0, 520);
        l.ShowCard = cardW >= 150;
        int cardX = spanL + (spanR - spanL - cardW) / 2;
        l.Card = new Rectangle(cardX, CardY, cardW, CardH);
        var cg = LayoutCard(l.Card);
        l.Cover = cg.Cover; l.TextX = cg.TextX; l.TextW = cg.TextW; l.ShowTimes = cg.ShowTimes; l.Times = cg.Times;
        l.ShowTitle = l.ShowCard && cg.ShowTitle;
        l.ShowSeek = l.ShowCard;
        l.Seek = cg.Seek;
        if (_track is not null && l.ShowTitle) { var (ar, al) = SubtitleSpans(_track, cg, _fSub); l.ArtistR = ar; l.AlbumR = al; }
        if (l.ShowCard && cg.ShowTimes) l.TotalR = new Rectangle(cg.Times.X - 4, l.Card.Y + 25, cg.Times.Width + 4, 12);
        return l;
    }

    // ---- the now-playing card, shared with the mini player (which is this card, detached) ----

    /// <summary>The card's inner geometry from its rect: the 40 px cover, the text column, the times column (only
    /// from 300 px — the subtitle falls back to the artist alone when squeezed) and the seek line under the text.</summary>
    internal struct CardGeom { public Rectangle Card, Cover, Times, Seek; public int TextX, TextW; public bool ShowTitle, ShowTimes; }

    internal static CardGeom LayoutCard(Rectangle card)
    {
        var c = new CardGeom { Card = card, Cover = new Rectangle(card.X + 7, card.Y + 7, 40, 40) };
        c.TextX = c.Cover.Right + 12;
        c.ShowTimes = card.Width >= 300;
        int timesW = c.ShowTimes ? 34 : 0;
        c.Times = new Rectangle(card.Right - 14 - timesW, card.Y, timesW, card.Height);
        c.TextW = card.Right - 14 - (c.ShowTimes ? timesW + 8 : 0) - c.TextX;
        c.ShowTitle = c.TextW >= 60;
        c.Seek = new Rectangle(c.TextX, card.Bottom - 7, card.Right - 14 - c.TextX, 3);
        return c;
    }

    /// <summary>Everything the card draws from: the track + its cover (with the outgoing one mid-dissolve), the
    /// hover/drag state of its two controls, the clock, and the cover-derived tint.</summary>
    internal struct CardState
    {
        public Track? Track; public Bitmap? Cover, CoverPrev; public float CoverFade;
        public bool Playing, CoverHover, SeekHot; public double ScrubFrac;   // ScrubFrac >= 0 while the seek line is being dragged
        public double Pos, Dur; public Color Tint; public float KnobR; public double EqPhase; public float[]? Viz;
        public float? TextSlide;                          // 0..1 while a new song's two lines rise into place; null from a caller that does not animate
        public double? HoverFrac;                         // set while the pointer hovers the seek line: THAT time reads in the accent
        public bool Remaining, ArtistHover, AlbumHover;   // the total slot counts down; a subtitle link is hovered
    }

    private CardState CardStateNow() => new()
    {
        Track = _track, Cover = _cover, CoverPrev = _coverPrev, CoverFade = _coverFade, Playing = _playing,
        CoverHover = _hover == Hit.Cover, SeekHot = _hover == Hit.Seek || _drag == Drag.Seek, ScrubFrac = _scrubFrac,
        Pos = CurPos, Dur = CurDur, Tint = _accentTint, KnobR = _seekKnobR, EqPhase = _eqPhase, Viz = _coverViz,
        TextSlide = _textSlide,
        HoverFrac = _hoverFrac >= 0 ? _hoverFrac : null, Remaining = _showRemaining,
        ArtistHover = _hover == Hit.Artist, AlbumHover = _hover == Hit.Album,
    };

    /// <summary>The now-playing card: a translucent slab with a hairline, the 40 px cover (cross-dissolving on a
    /// track change, live bars while playing, a wash while hovered), two left-aligned lines, elapsed over total on
    /// the right, and the accent seek line inset under the text (a knob only while hovered; a scrub pill when the
    /// times column is not there to show the scrubbed time). One drawer for the deck and the mini player.</summary>
    internal static void DrawCard(Graphics g, in CardGeom c, in CardState s, Font fTitle, Font fSub, Font fTime, EqBarsPainter eq)
    {
        bool idle = s.Track is null;
        var card = c.Card;
        var cardF = new RectangleF(card.X + 0.5f, card.Y + 0.5f, card.Width - 1, card.Height - 1);
        using (var fill = new SolidBrush(Color.FromArgb(200, Theme.Blend(Theme.SidebarBg, Color.White, 0.07))))
        using (var cp = Theme.RoundedRect(cardF, Theme.RadShell)) g.FillPath(fill, cp);
        using (var line = new Pen(Color.FromArgb(34, 255, 255, 255)))
        using (var cp = Theme.RoundedRect(cardF, Theme.RadShell)) g.DrawPath(line, cp);

        DrawCoverTile(g, c.Cover, idle, s.Cover, s.CoverPrev, s.CoverFade, idle ? 0 : (int)(s.Track!.Dbid & 0xffff));
        if (!idle && s.Playing && s.Viz is not null) eq.Draw(g, c.Cover, s.EqPhase, s.Viz, s.Tint);   // animated "now playing" equaliser, bottom-right of the cover
        if (s.CoverHover && !idle)   // the cover is a button: "show me this song in the list"
        {
            using var hv = new SolidBrush(Color.FromArgb(46, 255, 255, 255));
            using var hp = Theme.RoundedRect(new RectangleF(c.Cover.X + 0.5f, c.Cover.Y + 0.5f, c.Cover.Width - 1, c.Cover.Height - 1), (int)Math.Round(c.Cover.Width * Theme.TileFrac));
            g.FillPath(hv, hp);
        }

        if (c.ShowTitle)
        {
            string title = idle ? Loc.T("Nothing playing") : s.Track!.DisplayTitle;
            string sub = idle ? Loc.T("Pick a song to start") : CardSubtitle(s.Track!, c.TextW, fSub);
            // A track change: the words fade in with the cover (GDI text has no alpha, so the colour walks from
            // the slab's own tone to the text tone over the same 220 ms the art dissolves).
            Color slab = Theme.Blend(Theme.SidebarBg, Color.White, 0.07);
            float tf = idle ? 1f : Math.Clamp(s.CoverFade, 0f, 1f);
            // A new song's lines rise the last few pixels into place while its cover dissolves, so the card
            // changes as one thing. A slide and not a fade: GDI text ignores alpha.
            int ts = s.TextSlide is { } slide ? (int)Math.Round((1 - Math.Clamp(slide, 0f, 1f)) * 7) : 0;
            TextRenderer.DrawText(g, title, fTitle, new Rectangle(c.TextX, card.Y + 8 + ts, c.TextW, 17), Theme.Blend(slab, idle ? Theme.Subtle : Theme.TextCol, tf),
                TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter);
            TextRenderer.DrawText(g, sub, fSub, new Rectangle(c.TextX, card.Y + 25 + ts, c.TextW, 15), Theme.Blend(slab, idle ? Theme.Faint : Theme.Subtle, tf),
                TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter);
            if (!idle && (s.ArtistHover || s.AlbumHover))   // the hovered half of the subtitle brightens and underlines, the way a link does
            {
                var (ar, al) = SubtitleSpans(s.Track!, c, fSub);
                var u = s.ArtistHover ? ar : al;
                int right = Math.Min(u.Right, c.TextX + c.TextW);
                if (right - u.X > 4)
                {
                    // Redraw the SAME line clipped to the hovered half: identical layout, no second measurement.
                    var save = g.Clip;
                    g.SetClip(new Rectangle(u.X, card.Y + 24, right - u.X, 17), CombineMode.Intersect);
                    TextRenderer.DrawText(g, sub, fSub, new Rectangle(c.TextX, card.Y + 25, c.TextW, 15), Theme.Blend(slab, Theme.TextCol, tf),
                        TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter);
                    g.Clip = save;
                    using var up = new Pen(Theme.Blend(slab, Theme.Subtle, tf));
                    g.DrawLine(up, u.X + 2, u.Y + 14, right - 2, u.Y + 14);
                }
            }
        }

        double dur = s.Dur, pos = s.Pos;
        bool scrubbing = s.ScrubFrac >= 0;
        double frac = scrubbing ? s.ScrubFrac : (dur > 0 ? Math.Clamp(pos / dur, 0, 1) : 0);
        if (c.ShowTimes && !idle)
        {
            double? peek = scrubbing ? null : s.HoverFrac;   // hovering the seek line: the time UNDER THE POINTER, in the accent
            double shown = scrubbing ? s.ScrubFrac * dur : peek is { } hf ? hf * dur : pos;
            TextRenderer.DrawText(g, Fmt(shown), fTime, new Rectangle(c.Times.X, card.Y + 8, c.Times.Width, 17), scrubbing ? Theme.TextCol : peek is not null ? Theme.AccentBright : s.Playing ? Theme.Subtle : Theme.Faint,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            string total = s.Remaining && dur > 0 ? "-" + Fmt(Math.Max(0, dur - pos)) : Fmt(dur);   // the length, or (one click away) what is left of it
            TextRenderer.DrawText(g, total, fTime, new Rectangle(c.Times.X, card.Y + 25, c.Times.Width, 15), Theme.Faint,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
        DrawSlider(g, c.Seek, idle ? 0 : frac, !idle && s.SeekHot, s.Tint, s.KnobR, s.SeekHot, Color.FromArgb(38, 255, 255, 255), lift: false);
        if (scrubbing && !idle && !c.ShowTimes)   // scrub readout: the times column shows it when present; else a small pill INSIDE the card (below the deck it was clipped to a dark sliver)
        {
            string txt = Fmt(frac * dur);
            var sz = TextRenderer.MeasureText(txt, fTime);
            int bw2 = sz.Width + 14, bh2 = 19;
            var bub = new RectangleF(card.Right - 14 - bw2, card.Y + 6, bw2, bh2);
            using (var bb = new SolidBrush(Theme.Blend(Theme.SidebarBg, Color.Black, 0.28)))
            using (var bp = Theme.RoundedRect(bub, 5f)) g.FillPath(bb, bp);
            using (var bpen = new Pen(Color.FromArgb(40, 255, 255, 255)))
            using (var bp2 = Theme.RoundedRect(new RectangleF(bub.X + 0.5f, bub.Y + 0.5f, bub.Width - 1, bub.Height - 1), 5f)) g.DrawPath(bpen, bp2);
            TextRenderer.DrawText(g, txt, fTime, Rectangle.Round(bub), Theme.TextCol, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }

    /// <summary>The cover tile: soft shadow + rounded, clipped art (cross-dissolving on a track change) or the idle
    /// placeholder, and a hairline. Shared by the bar, the deck and the mini player. The bitmaps are cache-owned or
    /// the caller's — never disposed here.</summary>
    internal static void DrawCoverTile(Graphics g, Rectangle cr, bool idle, Bitmap? cover, Bitmap? prev, float fade, int seed)
    {
        int cvr = (int)Math.Round(cr.Width * Theme.TileFrac);
        // Fill + stroke share a half-pixel-inset rect so every corner antialiases identically (no soft bottom-right edge).
        var crF = new RectangleF(cr.X + 0.5f, cr.Y + 0.5f, cr.Width - 1, cr.Height - 1);
        // Soft drop shadow: aligned left/right with the tile and offset only DOWNWARD, so it reads as an even
        // shadow under the whole tile rather than a darker squared notch poking out of the bottom-right corner.
        using (var shp = Theme.RoundedRect(new RectangleF(cr.X, cr.Y + 2, cr.Width, cr.Height), cvr))
        using (var sh = new SolidBrush(Color.FromArgb(50, 0, 0, 0))) g.FillPath(sh, shp);
        using (var cp = Theme.RoundedRect(crF, cvr))
        {
            using var saved = g.Clip; g.SetClip(cp, CombineMode.Intersect);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            if (idle)
                // a quiet recessed tile (just above the bar's own shade), not a bright grey box
                using (var ph = new LinearGradientBrush(cr, Theme.Blend(Theme.SidebarBg, Color.White, 0.09), Theme.Blend(Theme.SidebarBg, Color.Black, 0.06), Theme.ArtAngle)) g.FillRectangle(ph, cr);
            else
            {
                var nv = cover ?? Theme.MakeArt(cr.Width, seed);
                if (prev is not null && fade < 1f)
                {
                    g.DrawImage(prev, cr);                                                            // outgoing holds underneath
                    Theme.DrawImageAlpha(g, nv, new RectangleF(cr.X, cr.Y, cr.Width, cr.Height), fade); // incoming dissolves in
                }
                else g.DrawImage(nv, cr);
            }
            g.Clip = saved;
        }
        if (idle) Theme.DrawNote(g, cr, Color.FromArgb(120, 255, 255, 255));   // "Nothing playing" placeholder
        using (var bp = new Pen(Theme.Blend(Theme.SidebarBg, Color.White, 0.10))) { using var cp2 = Theme.RoundedRect(crF, cvr); g.DrawPath(bp, cp2); }
    }

    /// <summary>Deck: is the point on something we handle? Everything else is the window's caption — the
    /// hit-test falls through to the wallpaper so the whole strip drags / snaps / double-click-maximizes.</summary>
    private bool HitsControl(Point p)
    {
        var l = Layout();
        if (l.ShowCard && l.Card.Contains(p)) return true;
        if (l.Play.Contains(p) || l.Prev.Contains(p) || l.Next.Contains(p)) return true;
        if (l.ShowModes && (l.Shuffle.Contains(p) || l.Repeat.Contains(p))) return true;
        if (l.ShowEq && l.Eq.Contains(p) || l.ShowPro && l.Pro.Contains(p) || l.ShowQueue && l.Queue.Contains(p) || l.ShowLyrics && l.Lyrics.Contains(p)) return true;
        if (l.ShowExtras && _track is not null && (l.Flow.Contains(p) || l.AddTo.Contains(p) || (CanRate && l.StarsR.Contains(p)))) return true;
        if (l.ShowSpeaker && l.Speaker.Contains(p)) return true;
        if (l.ShowVol && Inflate(l.Vol, 4, 10).Contains(p)) return true;
        if (l.ShowOverflow && l.Overflow.Contains(p)) return true;
        return false;
    }

    protected override void WndProc(ref Message m)
    {
        const int WM_NCHITTEST = 0x0084, HTTRANSPARENT = -1;
        if (m.Msg == WM_NCHITTEST && OnTop && !HitsControl(PointToClient(Cursor.Position))) { m.Result = (IntPtr)HTTRANSPARENT; return; }
        base.WndProc(ref m);
    }

    // ---- interaction ----
    private enum Hit { None, Prev, Play, Next, Speaker, Eq, Pro, Queue, Lyrics, Shuffle, Repeat, Seek, Vol, Cover, Overflow, Artist, Album, Times, Flow, AddTo, Stars }
    /// <summary>Harness (MIX_DECK_HOVER=prev|play|next|speaker|eq|pro|queue|lyrics|shuffle|repeat|seek|vol|cover): paint that hover state.</summary>
    internal void PreviewHover(string name) { if (Enum.TryParse<Hit>(name, true, out var h)) { _hover = h; if (h == Hit.Seek) _hoverFrac = 0.62; Invalidate(); } }
    private Hit _hover = Hit.None;
    private float _seekKnobR = 5f, _volKnobR = 5f;   // grab-knob radii — grow on hover/drag (tweened by RetargetKnobs)
    private Tween? _knobTween;
    private Color _accentTint = Theme.Accent;         // seek fill + eq bars drift toward the current cover's dominant colour

    private void OnDown(object? s, MouseEventArgs e)
    {
        var l = Layout();
        Tip.Disarm();
        if (e.Button == MouseButtons.Right)
        {
            if (OnTop && _track is not null && l.ShowCard && l.Card.Contains(e.Location)) CardMenuRequested?.Invoke(PointToScreen(e.Location));
            return;
        }
        if (e.Button != MouseButtons.Left) return;
        // Shuffle / repeat / EQ / volume are modes & settings — usable even with nothing loaded.
        if (l.ShowModes && l.Shuffle.Contains(e.Location)) { _shuffle = !_shuffle; ModesChanged?.Invoke(); Invalidate(); return; }
        if (l.ShowModes && l.Repeat.Contains(e.Location)) { _repeat = (RepeatMode)(((int)_repeat + 1) % 3); if (_repeat == RepeatMode.One) ClearPending(); ModesChanged?.Invoke(); Invalidate(); return; }
        if (l.ShowEq && l.Eq.Contains(e.Location)) { EqualizerRequested?.Invoke(RectangleToScreen(l.Eq)); return; }
        if (l.ShowPro && l.Pro.Contains(e.Location)) { ProRequested?.Invoke(RectangleToScreen(l.Pro)); return; }
        if (l.ShowQueue && l.Queue.Contains(e.Location)) { QueueRequested?.Invoke(RectangleToScreen(l.Queue)); return; }
        if (l.ShowLyrics && l.Lyrics.Contains(e.Location)) { LyricsRequested?.Invoke(RectangleToScreen(l.Lyrics)); return; }
        if (l.ShowOverflow && l.Overflow.Contains(e.Location)) { OverflowRequested?.Invoke(RectangleToScreen(l.Overflow)); return; }
        if (l.ShowSpeaker && l.Speaker.Contains(e.Location))
        {
            _muted = !_muted;
            if (!_muted && _volume <= 0.001) _volume = _lastVol > 0.001 ? _lastVol : 0.5;
            _engine.Volume = _muted ? 0 : _volume;
            Invalidate(); Changed?.Invoke(); return;
        }
        if (l.ShowVol && Inflate(l.Vol, 0, 9).Contains(e.Location)) { _drag = Drag.Volume; RetargetKnobs(); SetVolumeFromX(l.Vol, e.X); return; }

        if (_track is null) return; // transport needs a loaded track
        if (l.ShowExtras)          // the corner group: Cover Flow, add to playlist, the rating
        {
            if (l.Flow.Contains(e.Location)) { CoverFlowRequested?.Invoke(); return; }
            if (l.AddTo.Contains(e.Location)) { AddToPlaylistRequested?.Invoke(PointToScreen(new Point(l.AddTo.Left, l.AddTo.Bottom))); return; }
            if (CanRate && l.StarsR.Contains(e.Location))
            {
                int star = Math.Clamp((e.X - l.StarsR.X) * 5 / l.StarsR.Width + 1, 1, 5);
                int now = Math.Clamp(_track.Rating / 20, 0, 5);
                RateRequested?.Invoke(star == now ? 0 : star);   // clicking the lit star clears it, as in the list
                return;
            }
        }
        if (OnTop && l.ShowCard)   // the card's text is live too: the artist, the album, and the total time
        {
            if (l.ArtistR.Contains(e.Location)) { ArtistClicked?.Invoke(); return; }
            if (l.AlbumR.Contains(e.Location)) { AlbumClicked?.Invoke(); return; }
            if (l.TotalR.Contains(e.Location)) { ShowRemaining = !ShowRemaining; RemainingToggled?.Invoke(ShowRemaining); return; }
        }
        if (OnTop && l.ShowCard && l.Cover.Contains(e.Location)) { CoverClicked?.Invoke(); return; }
        if (l.Play.Contains(e.Location)) { TogglePlay(); return; }
        if (l.Prev.Contains(e.Location)) { PrevRequested?.Invoke(); return; }
        if (l.Next.Contains(e.Location)) { NextRequested?.Invoke(); return; }
        if (l.ShowSeek && Inflate(l.Seek, 0, 10).Contains(e.Location)) { _drag = Drag.Seek; RetargetKnobs(); ScrubTo(l.Seek, e.X); return; }
    }

    private void OnMove(object? s, MouseEventArgs e)
    {
        var l = Layout();
        if (_drag == Drag.Volume && l.ShowVol) { SetVolumeFromX(l.Vol, e.X); return; }
        if (_drag == Drag.Seek && l.ShowSeek) { ScrubTo(l.Seek, e.X); return; }
        var h = l.ShowSpeaker && l.Speaker.Contains(e.Location) ? Hit.Speaker
            : l.ShowEq && l.Eq.Contains(e.Location) ? Hit.Eq
            : l.ShowPro && l.Pro.Contains(e.Location) ? Hit.Pro
            : l.ShowQueue && l.Queue.Contains(e.Location) ? Hit.Queue
            : l.ShowLyrics && l.Lyrics.Contains(e.Location) ? Hit.Lyrics
            : l.ShowOverflow && l.Overflow.Contains(e.Location) ? Hit.Overflow
            : l.ShowModes && l.Shuffle.Contains(e.Location) ? Hit.Shuffle
            : l.ShowModes && l.Repeat.Contains(e.Location) ? Hit.Repeat
            : l.ShowExtras && _track is not null && l.Flow.Contains(e.Location) ? Hit.Flow
            : l.ShowExtras && _track is not null && l.AddTo.Contains(e.Location) ? Hit.AddTo
            : l.ShowExtras && _track is not null && CanRate && l.StarsR.Contains(e.Location) ? Hit.Stars
            : OnTop && _track is not null && l.ArtistR.Contains(e.Location) ? Hit.Artist
            : OnTop && _track is not null && l.AlbumR.Contains(e.Location) ? Hit.Album
            : OnTop && _track is not null && l.TotalR.Contains(e.Location) ? Hit.Times
            : l.ShowSeek && Inflate(l.Seek, 0, 10).Contains(e.Location) ? Hit.Seek
            : l.ShowVol && Inflate(l.Vol, 0, 9).Contains(e.Location) ? Hit.Vol
            : _track is null ? Hit.None
            : OnTop && l.ShowCard && l.Cover.Contains(e.Location) ? Hit.Cover
            : l.Play.Contains(e.Location) ? Hit.Play
            : l.Prev.Contains(e.Location) ? Hit.Prev
            : l.Next.Contains(e.Location) ? Hit.Next
            : Hit.None;
        if (h != _hover) { _hover = h; RetargetKnobs(); Cursor = h is Hit.Cover or Hit.Artist or Hit.Album or Hit.Times or Hit.Stars ? Cursors.Hand : Cursors.Default; UpdateTip(h, l); Invalidate(); }
        int sh = h == Hit.Stars && l.StarsR.Width > 0 ? Math.Clamp((e.X - l.StarsR.X) * 5 / l.StarsR.Width + 1, 1, 5) : -1;
        if (sh != _starHover) { _starHover = sh; Invalidate(l.StarsR); }
        // The time under the pointer while it rides the seek line (the card's elapsed slot reads it in the accent).
        double hf = h == Hit.Seek && _drag == Drag.None && _track is not null && l.Seek.Width > 0 ? Math.Clamp((e.X - l.Seek.X) / (double)l.Seek.Width, 0, 1) : -1;
        if (Math.Abs(hf - _hoverFrac) > 0.0015) { _hoverFrac = hf; if (OnTop && l.ShowCard) Invalidate(l.Card); else Invalidate(); }
    }

    /// <summary>The themed tooltip for whatever is hovered (the sliders and the card's own text carry none).</summary>
    private void UpdateTip(Hit h, in Lo l)
    {
        string t; Rectangle r;
        switch (h)
        {
            case Hit.Prev: t = Loc.T("Previous"); r = l.Prev; break;
            case Hit.Play: t = _playing ? Loc.T("Pause") : Loc.T("Play"); r = l.Play; break;
            case Hit.Next: t = Loc.T("Next"); r = l.Next; break;
            case Hit.Shuffle: t = Loc.T("Shuffle"); r = l.Shuffle; break;
            case Hit.Repeat: t = Loc.T("Repeat"); r = l.Repeat; break;
            case Hit.Lyrics: t = Loc.T("Lyrics"); r = l.Lyrics; break;
            case Hit.Queue: t = Loc.T("Up Next"); r = l.Queue; break;
            case Hit.Pro: t = Loc.T("Pro features"); r = l.Pro; break;
            case Hit.Eq: t = Loc.T("Equalizer"); r = l.Eq; break;
            case Hit.Speaker: t = _muted ? Loc.T("Unmute") : Loc.T("Mute"); r = l.Speaker; break;
            case Hit.Cover: t = Loc.T("Show in list"); r = l.Cover; break;
            case Hit.Overflow: t = Loc.T("More"); r = l.Overflow; break;
            case Hit.Flow: t = Loc.T("Cover Flow"); r = l.Flow; break;
            case Hit.AddTo: t = Loc.T("Add to playlist"); r = l.AddTo; break;
            case Hit.Stars: t = Loc.T("Rating"); r = l.StarsR; break;
            default: Tip.Disarm(); return;
        }
        Tip.Arm(RectangleToScreen(r), t);
    }

    /// <summary>Smoothly grow/shrink the seek + volume grab-knobs toward their hover/drag target radius (5→8px, 120ms).
    /// One tween drives both. Called whenever hover or drag state changes so the knobs feel like grabbable widgets.</summary>
    private void RetargetKnobs()
    {
        float seekTo = (_hover == Hit.Seek || _drag == Drag.Seek) ? (OnTop ? 6f : 8f) : (OnTop ? 4f : 5f);
        float volTo = (_hover == Hit.Vol || _drag == Drag.Volume) ? 8f : 5f;
        if (Math.Abs(seekTo - _seekKnobR) < 0.1f && Math.Abs(volTo - _volKnobR) < 0.1f) return;
        _knobTween?.Cancel();
        float seekFrom = _seekKnobR, volFrom = _volKnobR;
        if (!Anim.MotionEnabled) { _seekKnobR = seekTo; _volKnobR = volTo; Invalidate(); return; }
        _knobTween = Anim.Run(120, v => { float f = (float)v; _seekKnobR = seekFrom + (seekTo - seekFrom) * f; _volKnobR = volFrom + (volTo - volFrom) * f; if (!IsDisposed) Invalidate(); },
            () => { _seekKnobR = seekTo; _volKnobR = volTo; _knobTween = null; }, Easings.OutCubic);
    }

    private void OnUp(object? s, MouseEventArgs e)
    {
        bool wasDragging = _drag != Drag.None;
        if (_drag == Drag.Seek && _scrubFrac >= 0 && _engine.IsOpen)
        {
            _engine.Position = TimeSpan.FromSeconds(_scrubFrac * _engine.Duration.TotalSeconds);
            InvalidatePrefetch();   // a seek can drop the crossfade's pre-decoded next voice; re-stage it so the next boundary stays seamless
            DiscordSeeked();        // the card's text is unchanged, so only a moved start time redraws the bar
        }
        _scrubFrac = -1;
        _drag = Drag.None;
        RetargetKnobs();
        Invalidate();
        if (wasDragging) Changed?.Invoke();
    }

    private void ScrubTo(Rectangle seek, int x)
    {
        _scrubFrac = Math.Clamp((x - seek.Left) / (double)seek.Width, 0, 1);
        Invalidate();
    }

    private void SetVolumeFromX(Rectangle vol, int x)
    {
        _volume = Math.Clamp((x - vol.Left) / (double)vol.Width, 0, 1);
        if (_volume > 0.001) _lastVol = _volume;
        _muted = _volume <= 0.001;
        _engine.Volume = _volume;
        Invalidate();
        Changed?.Invoke();
    }

    private static Rectangle Inflate(Rectangle r, int dx, int dy) { var c = r; c.Inflate(dx, dy); return c; }

    // EXPERIMENT (gated by AppSettings.FrostedBar): a small, pre-blurred slice of the list that the host slides as
    // you scroll (cached once → no per-frame capture → no lag). The whole bar reads as frosted glass over the list's
    // continuation: the blur is drawn at the list's NATURAL vertical scale (no squash) and a translucent glass layer
    // is laid over it. <see cref="_frostH"/> is that natural height in px from the bar's top (so when the list ends
    // mid-bar we don't stretch a short slice). Null = the plain seamless gradient. TAKES OWNERSHIP of the bitmap.
    private Bitmap? _frost;
    private int _frostH;          // natural display height (px from the bar top) — keeps the list's vertical scale
    private int _frostX, _frostW; // destination x + width, so the blurred columns line up with the real list columns above
    private Bitmap? _frostOld;    // outgoing frost held during a view-switch slide (old pushes left, new rides in)
    private int _frostOldH, _frostOldX, _frostOldW;
    private float _frostSlide = 1f;   // 1 = settled; <1 = mid-slide (eased progress, matched to the content transition)
    private Tween? _frostSlideTween;
    // Ownership: the per-scroll frost is a BORROWED reusable scratch bitmap (owned by MainForm) passed with owned=false,
    // so the bar must NOT dispose it. View-switch frosts are owned (allocated fresh for the slide). These flags keep a
    // borrowed scratch from being disposed out from under MainForm (it would crash the next scroll frame).
    private bool _frostOwned = true, _frostOldOwned = true;

    public void SetFrost(Bitmap? slice, int displayH = 0, int destX = 0, int destW = 0, bool owned = true)
    {
        if (OnTop) { if (owned) slice?.Dispose(); return; }   // the deck has no list under it
        int h = Math.Clamp(displayH, 0, H);
        if (slice is null && _frost is null && _frostOld is null && _frostSlide >= 1f) return;   // already a plain bar — nothing to do
        _frostSlideTween?.Cancel(); _frostSlideTween = null;
        if (_frostOldOwned) _frostOld?.Dispose();
        _frostOld = null; _frostOldOwned = true; _frostSlide = 1f;
        var old = _frost; bool oldOwned = _frostOwned;
        _frost = slice; _frostH = h; _frostX = destX; _frostW = destW; _frostOwned = owned;
        if (oldOwned && !ReferenceEquals(old, slice)) old?.Dispose();   // never dispose a borrowed scratch (or the same object)
        Invalidate();   // the frost spans the whole bar now → repaint it all (cheap: gradient + one blit + the controls)
    }

    /// <summary>Cross-SLIDE the frost from the current one to <paramref name="slice"/>, buffered, in sync with the
    /// content view-switch transition (old pushes off to the left, new rides in from the right; 360ms OutCubic).
    /// <paramref name="slice"/> is always an OWNED bitmap (the view-switch allocates a fresh one).</summary>
    public void SlideFrost(Bitmap? slice, int displayH = 0, int destX = 0, int destW = 0)
    {
        if (OnTop) { slice?.Dispose(); return; }
        int h = Math.Clamp(displayH, 0, H);
        if (!Anim.MotionEnabled || (_frost is null && slice is null)) { SetFrost(slice, h, destX, destW, owned: true); return; }
        _frostSlideTween?.Cancel();
        if (_frostOldOwned) _frostOld?.Dispose();
        _frostOld = _frost; _frostOldH = _frostH; _frostOldX = _frostX; _frostOldW = _frostW; _frostOldOwned = _frostOwned;   // outgoing keeps its ownership
        _frost = slice; _frostH = h; _frostX = destX; _frostW = destW; _frostOwned = true;                                     // incoming is owned
        _frostSlide = 0f;
        _frostSlideTween = Anim.Run(360, v => { _frostSlide = (float)v; if (!IsDisposed) Invalidate(); },
            () => { _frostSlideTween = null; if (_frostOldOwned) _frostOld?.Dispose(); _frostOld = null; _frostOldOwned = true; _frostSlide = 1f; if (!IsDisposed) Invalidate(); }, Easings.OutCubic);
        Invalidate();
    }

    // Draw one frost layer (the blurred slice + nothing else) at a horizontal offset — used for both the settled
    // frost and the two sliding layers. dx is added to the layer's own column-aligned x.
    private void DrawFrostLayer(Graphics g, Bitmap? frost, int fh0, int fx0, int fw0, int dx)
    {
        if (frost is null) return;
        int fh = Math.Clamp(fh0, 0, H);
        if (fh <= 0) return;
        int fw = fw0 > 0 ? fw0 : Width;
        var im = g.InterpolationMode; g.InterpolationMode = InterpolationMode.HighQualityBilinear;
        g.DrawImage(frost, new Rectangle(fx0 + dx, 0, fw, fh));
        g.InterpolationMode = im;
    }

    // The full-bar background gradients (no-frost `bg`, frost `glass`) + the frost base fill are rebuilt only when a
    // theme COLOUR changes (Theme.Revision) — NOT every paint. They ran on BOTH the ~16fps eq-viz playback loop AND
    // every scroll frame (the frost re-Invalidates the whole bar), so this kills 2 gradient brushes + 2 ColorBlend
    // arrays + a solid brush per frame. Width doesn't affect a 90° vertical gradient's stops, so resize needs no rebuild.
    private LinearGradientBrush? _gradBg, _gradGlass;
    private SolidBrush? _gradBase;
    private int _gradRev = -1;

    private void EnsureGradients()
    {
        if (_gradRev == Theme.Revision && _gradBg is not null) return;
        _gradBg?.Dispose(); _gradGlass?.Dispose(); _gradBase?.Dispose();
        var rect = new Rectangle(0, -1, Math.Max(1, Width), H + 1);
        _gradBg = new LinearGradientBrush(rect, Theme.Bg, Theme.Blend(Theme.SidebarBg, Color.Black, 0.14), 90f)
        {
            InterpolationColors = new ColorBlend
            {
                Colors = new[] { Theme.Bg, Theme.Blend(Theme.SidebarBg, Color.White, 0.04), Theme.SidebarBg, Theme.Blend(Theme.SidebarBg, Color.Black, 0.14) },
                Positions = new[] { 0f, 0.34f, 0.62f, 1f },
            },
        };
        _gradGlass = new LinearGradientBrush(rect, Color.FromArgb(150, Theme.Bg), Color.FromArgb(236, Theme.Blend(Theme.SidebarBg, Color.Black, 0.14)), 90f)
        {
            InterpolationColors = new ColorBlend
            {
                Colors = new[]
                {
                    Color.FromArgb(150, Theme.Bg),
                    Color.FromArgb(202, Theme.Blend(Theme.SidebarBg, Color.White, 0.04)),
                    Color.FromArgb(232, Theme.SidebarBg),
                    Color.FromArgb(236, Theme.Blend(Theme.SidebarBg, Color.Black, 0.14)),
                },
                Positions = new[] { 0f, 0.2f, 0.4f, 1f },   // ramp to near-opaque by 40% → the see-through blur is a SHORTER top band (Erik: the top blur was a bit long)
            },
        };
        _gradBase = new SolidBrush(Theme.Bg);
        _gradRev = Theme.Revision;
    }

    // ---- paint ----
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (OnTop) { PaintTop(g); return; }
        EnsureGradients();
        bool sliding = _frostSlide < 1f && (_frostOld is not null || _frost is not null);   // slide even if one side is empty (to/from a no-frost view)
        if (_frost is null && !sliding)
        {
            // No frost → a long, seamless gradient: the top eases out of the CONTENT colour (Theme.Bg) so the song
            // list flows into the player with no hard seam, then through a faint glass sheen down to a darker floor.
            g.FillRectangle(_gradBg!, 0, 0, Width, H);
        }
        else
        {
            // Frosted glass: the song list "continues" UNDER the whole bar. The pre-blurred slice is drawn at its
            // NATURAL vertical scale (top-aligned, height = _frostH) so the rows never squash/stretch, then a
            // TRANSLUCENT glass gradient is laid over it — so the list reads faintly through the glass while the
            // controls (painted opaque below) stay crisp. Most visible up top (the list flowing in), fading to a
            // near-solid floor where the seek bar + controls sit. During a view switch the two frosts slide
            // (old left / new in from the right, buffered, matched to the content transition).
            g.FillRectangle(_gradBase!, 0, 0, Width, H);
            if (sliding)
            {
                DrawFrostLayer(g, _frostOld, _frostOldH, _frostOldX, _frostOldW, -(int)Math.Round(Width * _frostSlide));   // old pushes off left
                DrawFrostLayer(g, _frost, _frostH, _frostX, _frostW, (int)Math.Round(Width * (1f - _frostSlide)));         // new rides in from the right
            }
            else
            {
                DrawFrostLayer(g, _frost, _frostH, _frostX, _frostW, 0);
            }
            g.FillRectangle(_gradGlass!, 0, 0, Width, H);
        }

        var l = Layout();
        bool idle = _track is null;
        DrawCover(g, l.Cover, idle);

        // title / artist (hidden when the window is too narrow)
        if (l.ShowTitle)
        {
            string title = idle ? Loc.T("Nothing playing") : _track!.DisplayTitle;
            string sub = idle ? Loc.T("Pick a song to start")
                              : string.Join("  •  ", new[] { _track!.Artist, _track.Album }.Where(x => !string.IsNullOrWhiteSpace(x)));
            // Sit the title/artist in the TOP band (aligned with the transport), not vertically centred —
            // otherwise the subtitle drops onto the seek bar + "0:00" times along the bottom.
            TextRenderer.DrawText(g, title, _fTitle,
                new Rectangle(l.TextX, 12, l.TextW, 22), idle ? Theme.Subtle : Theme.TextCol,
                TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter);
            TextRenderer.DrawText(g, sub, _fSub,
                new Rectangle(l.TextX, 34, l.TextW, 16), idle ? Theme.Faint : Theme.Subtle,
                TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }

        PaintControls(g, l, idle);
        Theme.CarveCardCorners(g, this, Theme.RadShell, false, false, true, true);   // content card's BOTTOM corners
    }

    /// <summary>The bottom bar's cover: the shared tile + the live equaliser bars while playing.</summary>
    private void DrawCover(Graphics g, Rectangle cr, bool idle)
    {
        DrawCoverTile(g, cr, idle, _cover, _coverPrev, _coverFade, idle ? 0 : (int)(_track!.Dbid & 0xffff));
        if (!idle && _playing) _eqBars.Draw(g, cr, _eqPhase, _coverViz, _accentTint);   // animated "now playing" equaliser, bottom-right of the cover
    }

    /// <summary>The bottom bar's transport, seek and utilities (shared glyph drawers; the deck paints its own set).</summary>
    private void PaintControls(Graphics g, Lo l, bool idle)
    {
        // transport (dimmed + inert when idle); shuffle/repeat are modes — always live
        if (l.ShowModes) DrawShuffle(g, l.Shuffle, _shuffle, _hover == Hit.Shuffle);
        DrawCircleGlyph(g, l.Prev, _hover == Hit.Prev, GlyphPrev, idle);
        DrawPlayButton(g, l.Play, _hover == Hit.Play, idle);
        DrawCircleGlyph(g, l.Next, _hover == Hit.Next, GlyphNext, idle);
        if (l.ShowModes) DrawRepeat(g, l.Repeat, _repeat, _hover == Hit.Repeat);

        // seek
        if (l.ShowSeek)
        {
            double dur = CurDur;
            double pos = CurPos;
            double frac = _scrubFrac >= 0 ? _scrubFrac : (dur > 0 ? Math.Clamp(pos / dur, 0, 1) : 0);
            DrawSlider(g, l.Seek, idle ? 0 : frac, !idle, _accentTint, _seekKnobR, _hover == Hit.Seek || _drag == Drag.Seek);
            if (l.ShowTimes)
            {
                double shown = _scrubFrac >= 0 ? _scrubFrac * dur : pos;
                TextRenderer.DrawText(g, idle ? "0:00" : Fmt(shown), _fTime, new Rectangle(l.Seek.Left - 46, SeekY - 9, 42, 20), Theme.Faint, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
                TextRenderer.DrawText(g, idle ? "0:00" : Fmt(dur), _fTime, new Rectangle(l.Seek.Right + 6, SeekY - 9, 42, 20), Theme.Faint, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            }
            // Scrub bubble: while dragging, a little time pill pops above the thumb — the ONLY position feedback when the
            // window is too narrow for the side time labels (ShowTimes needs w>=740). Fixes a real blind spot on small windows.
            if (_drag == Drag.Seek && !idle)
            {
                string txt = Fmt(frac * dur);
                var sz = TextRenderer.MeasureText(txt, _fTime);
                int bw2 = sz.Width + 14, bh2 = 19;
                float kx = l.Seek.X + (float)(l.Seek.Width * Math.Clamp(frac, 0, 1));
                float bx = Math.Clamp(kx - bw2 / 2f, l.Seek.Left - 12, l.Seek.Right - bw2 + 12);
                var bub = new RectangleF(bx, l.Seek.Y - 11 - bh2, bw2, bh2);
                var sm = g.SmoothingMode; g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var bb = new SolidBrush(Theme.Blend(Theme.SidebarBg, Color.Black, 0.28)))
                using (var bp = Theme.RoundedRect(bub, 5f)) g.FillPath(bb, bp);
                using (var bpen = new Pen(Color.FromArgb(40, 255, 255, 255)))
                using (var bp2 = Theme.RoundedRect(new RectangleF(bub.X + 0.5f, bub.Y + 0.5f, bub.Width - 1, bub.Height - 1), 5f)) g.DrawPath(bpen, bp2);
                g.SmoothingMode = sm;
                TextRenderer.DrawText(g, txt, _fTime, Rectangle.Round(bub), Theme.TextCol, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
        }

        // queue + pro features + equalizer + volume (always interactive when shown)
        if (l.ShowQueue) DrawQueueGlyph(g, l.Queue, _hover == Hit.Queue);
        if (l.ShowLyrics) DrawLyricsGlyph(g, l.Lyrics, _hover == Hit.Lyrics);
        if (l.ShowPro) DrawProGlyph(g, l.Pro, _hover == Hit.Pro);
        if (l.ShowEq) DrawEqGlyph(g, l.Eq, _hover == Hit.Eq);
        if (l.ShowSpeaker) DrawSpeaker(g, l.Speaker, _muted, _hover == Hit.Speaker);
        if (l.ShowVol) DrawSlider(g, l.Vol, _muted ? 0 : _volume, true, Theme.Accent, _volKnobR, _hover == Hit.Vol || _drag == Drag.Volume);   // knob, matching the seek bar (consistency + a grab target)
    }

    /// <summary>The corner group under the window buttons: Cover Flow, add the playing song to a playlist, and
    /// its rating. Everything here needs a song, so the whole group fades out when nothing is loaded.</summary>
    private void DrawExtras(Graphics g, in Lo l)
    {
        if (_track is null) return;
        DrawFlowGlyph(g, l.Flow, _hover == Hit.Flow);
        DrawAddGlyph(g, l.AddTo, _hover == Hit.AddTo);
        if (!CanRate) return;
        using (var rule = new Pen(Color.FromArgb(32, 255, 255, 255)))   // the two actions and the rating are two things
        {
            var sm0 = g.SmoothingMode; g.SmoothingMode = SmoothingMode.None;
            int rx = (l.AddTo.Right + l.StarsR.Left) / 2;
            g.DrawLine(rule, rx, l.AddTo.Y + 5, rx, l.AddTo.Bottom - 5);
            g.SmoothingMode = sm0;
        }
        DrawStars(g, l.StarsR);
    }

    private void DrawFlowGlyph(Graphics g, Rectangle r, bool hover)
    {
        if (hover) { using var hb = new SolidBrush(Theme.RowHover); using var hp = Theme.RoundedRect(r, Theme.RadControl); g.FillPath(hb, hp); }
        // The app's own Cover Flow mark, drawn from a box a little larger than the cell: at 24 px it comes out
        // smaller than the line glyphs beside it, and a weaker icon in a row of equals reads as a mistake.
        ThemedButton.DrawIcon(g, new RectangleF(r.X - 3, r.Y - 3, r.Width + 6, r.Height + 6), ThemedButton.Ico.CoverFlow, hover ? Theme.TextCol : Theme.Subtle);
    }

    /// <summary>Add to playlist: the queue glyph's three lines with a plus where the last one ends.</summary>
    private void DrawAddGlyph(Graphics g, Rectangle r, bool hover)
    {
        if (hover) { using var hb = new SolidBrush(Theme.RowHover); using var hp = Theme.RoundedRect(r, Theme.RadControl); g.FillPath(hb, hp); }
        Color c = hover ? Theme.TextCol : Theme.Subtle;
        using var pen = new Pen(c, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        float x = r.X + 5, x2 = r.Right - 5;
        g.DrawLine(pen, x, r.Y + 8, x2 - 8, r.Y + 8);
        g.DrawLine(pen, x, r.Y + 12, x2 - 8, r.Y + 12);
        g.DrawLine(pen, x, r.Y + 16, x2 - 8, r.Y + 16);
        g.DrawLine(pen, x2 - 3, r.Y + 12, x2 - 3, r.Y + 20);     // a plus beside the list, not inside it
        g.DrawLine(pen, x2 - 7, r.Y + 16, x2 + 1, r.Y + 16);
    }

    /// <summary>The playing song's rating. Filled stars read in the accent; hovering previews the rating under
    /// the pointer, and clicking the star that is already lit clears it (the same gesture the list uses).</summary>
    private void DrawStars(Graphics g, Rectangle r)
    {
        int rated = Math.Clamp((_track?.Rating ?? 0) / 20, 0, 5);
        if (_ratingShown != rated)   // a CHANGED rating fades in; the first paint of a song shows it outright
        {
            bool first = _ratingShown < 0;
            _ratingShown = rated; _ratingTw?.Cancel();
            if (first || !Anim.MotionEnabled) _ratingT = 1f;
            else { _ratingT = 0f; _ratingTw = Anim.Run(160, v => { _ratingT = (float)v; if (!IsDisposed) Invalidate(r); }, () => _ratingTw = null, Easings.OutCubic); }
        }
        bool hover = _hover == Hit.Stars;
        int shown = hover && _starHover > 0 ? _starHover : rated;
        if (hover) { using var hb = new SolidBrush(Theme.RowHover); using var hp = Theme.RoundedRect(r, Theme.RadControl); g.FillPath(hb, hp); }
        var sm = g.SmoothingMode; g.SmoothingMode = SmoothingMode.AntiAlias;
        float cell = r.Width / 5f, cy = r.Y + r.Height / 2f;
        Color on = hover ? Theme.AccentBright : Theme.Accent;
        if (!hover && _ratingT < 1f) on = Theme.Blend(Theme.Faint, on, _ratingT);
        using var fill = new SolidBrush(on);
        using var edge = new Pen(Color.FromArgb(hover ? 150 : 110, Theme.Faint), 1.3f);
        for (int i = 0; i < 5; i++)
        {
            var pts = Star(r.X + cell * (i + 0.5f), cy, 6.6f);
            if (i < shown) g.FillPolygon(fill, pts); else g.DrawPolygon(edge, pts);
        }
        g.SmoothingMode = sm;
    }

    private static PointF[] Star(float cx, float cy, float o)
    {
        var p = new PointF[10];
        for (int i = 0; i < 10; i++)
        {
            double a = -Math.PI / 2 + i * Math.PI / 5;
            float rr = i % 2 == 0 ? o : o * 0.42f;
            p[i] = new PointF(cx + rr * (float)Math.Cos(a), cy + rr * (float)Math.Sin(a));
        }
        return p;
    }

    /// <summary>"Artist  •  Album" when it fits the card's text column, else just the artist (a name cut to
    /// "mem…" says less than no album at all); the ellipsis is the last resort for a lone long artist.</summary>
    /// <summary>Where the artist and the album sit inside the card's subtitle (bar coordinates) - the deck turns
    /// them into links. The artist leads the subtitle, the album follows the separator. Empty when unknown.
    /// The rects stop short of the seek line's grab band so hovering a link never steals the scrub.</summary>
    private static (Rectangle artist, Rectangle album) SubtitleSpans(Track t, in CardGeom c, Font fSub)
    {
        string sub = CardSubtitle(t, c.TextW, fSub);
        string artist = (t.Artist ?? "").Trim();
        int y = c.Card.Y + 25, h = 12;
        if (artist.Length == 0 || !sub.StartsWith(artist, StringComparison.Ordinal)) return (Rectangle.Empty, Rectangle.Empty);
        var probe = new Size(int.MaxValue, 20);
        int aw = TextRenderer.MeasureText(artist, fSub, probe, TextFormatFlags.NoPrefix).Width;
        var ar = new Rectangle(c.TextX + 1, y, Math.Max(0, Math.Min(aw - 2, c.TextW)), h);
        var al = Rectangle.Empty;
        int sep = sub.IndexOf("  \u2022  ", StringComparison.Ordinal);
        if (sep > 0)
        {
            int pre = TextRenderer.MeasureText(sub.Substring(0, sep + 5), fSub, probe, TextFormatFlags.NoPrefix).Width;
            int full = TextRenderer.MeasureText(sub, fSub, probe, TextFormatFlags.NoPrefix).Width;
            al = new Rectangle(c.TextX + pre - 3, y, Math.Max(0, Math.Min(full, c.TextW) - pre + 2), h);
        }
        return (ar, al);
    }

    private static string CardSubtitle(Track t, int width, Font fSub)
    {
        string artist = (t.Artist ?? "").Trim(), album = (t.Album ?? "").Trim();
        string full = string.Join("  •  ", new[] { artist, album }.Where(x => x.Length > 0));
        if (album.Length == 0 || artist.Length == 0) return full;
        int need = TextRenderer.MeasureText(full, fSub, new Size(int.MaxValue, 20), TextFormatFlags.NoPrefix).Width;
        return need <= width ? full : artist;
    }

    /// <summary>The deck. Its background is the wallpaper under it (sampled from the root's cached bitmap, so it
    /// is pixel-identical to the strip around the window buttons); the only surface is the now-playing CARD —
    /// a translucent slab with a hairline, the 40 px cover, two left-aligned lines, elapsed over total on the
    /// right, and the accent seek line inset under the text (a knob only while hovered).</summary>
    private void PaintTop(Graphics g)
    {
        var wp = Parent as WallpaperPanel;
        if (wp?.Wallpaper is { } wall) Theme.BlitExact(g, wall, Bounds);
        else { var st = g.Save(); g.TranslateTransform(-Left, -Top); Theme.PaintWallpaper(g, Parent?.ClientRectangle ?? ClientRectangle); g.Restore(st); }

        var l = Layout();
        bool idle = _track is null;

        // identity
        if (wp?.Logo is { } logo)
        {
            var im = g.InterpolationMode; g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(logo, l.Logo);
            g.InterpolationMode = im;
        }
        if (l.ShowWordmark)
            TextRenderer.DrawText(g, "Mixtape", _fWordmark, l.Wordmark, Theme.TextCol, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

        // transport
        if (l.ShowModes) DrawShuffle(g, l.Shuffle, _shuffle, _hover == Hit.Shuffle);
        DrawCircleGlyph(g, l.Prev, _hover == Hit.Prev, GlyphPrevL, idle);
        DrawPlayButton(g, l.Play, _hover == Hit.Play, idle);
        DrawCircleGlyph(g, l.Next, _hover == Hit.Next, GlyphNextL, idle);
        if (l.ShowModes) DrawRepeat(g, l.Repeat, _repeat, _hover == Hit.Repeat);

        // the card
        if (l.ShowCard) DrawCard(g, LayoutCard(l.Card), CardStateNow(), _fCardTitle, _fSub, _fTime, _eqBars);

        // utilities
        if (l.ShowOverflow) DrawOverflowGlyph(g, l.Overflow, _hover == Hit.Overflow);
        if (l.ShowLyrics) DrawLyricsGlyph(g, l.Lyrics, _hover == Hit.Lyrics);
        if (l.ShowQueue) DrawQueueGlyph(g, l.Queue, _hover == Hit.Queue);
        if (l.ShowPro) DrawProGlyph(g, l.Pro, _hover == Hit.Pro);
        if (l.ShowEq) DrawEqGlyph(g, l.Eq, _hover == Hit.Eq);
        if (l.ShowSpeaker) DrawSpeaker(g, l.Speaker, _muted, _hover == Hit.Speaker);
        if (l.ShowVol) DrawSlider(g, l.Vol, _muted ? 0 : _volume, true, Theme.Accent, _volKnobR, _hover == Hit.Vol || _drag == Drag.Volume, Color.FromArgb(38, 255, 255, 255), lift: false);
        if (l.ShowExtras) DrawExtras(g, in l);   // the corner group under the window buttons
    }

    internal static string Fmt(double sec)
    {
        if (sec < 0 || double.IsNaN(sec)) sec = 0;
        var t = TimeSpan.FromSeconds(sec);
        return t.Hours > 0 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }

    internal static void DrawSlider(Graphics g, Rectangle track, double frac, bool knob, Color fill, float knobR = 5f, bool knobHot = false, Color? trackCol = null, bool lift = true)
    {
        var t = new RectangleF(track.X + 0.5f, track.Y + 0.5f, track.Width - 1, track.Height - 1);
        using (var tb = new SolidBrush(trackCol ?? Theme.Blend(Theme.PanelBg, Color.Black, 0.1)))
        using (var tp = Theme.RoundedRect(t, t.Height / 2f)) g.FillPath(tb, tp);
        float fw = (float)(t.Width * Math.Clamp(frac, 0, 1));
        if (fw > 0)
            using (var fb = new SolidBrush(fill))
            using (var fp = Theme.RoundedRect(new RectangleF(t.X, t.Y, fw, t.Height), t.Height / 2f)) g.FillPath(fb, fp);
        if (knob)
        {
            float kx = t.X + fw, ky = t.Y + t.Height / 2f, r = knobR;
            var sm = g.SmoothingMode; g.SmoothingMode = SmoothingMode.AntiAlias;
            if (lift && r > 5.4f)   // a soft shadow under the grown (grabbed) knob so it reads as lifted (bottom bar only - on the deck's translucent card it read as a dark smudge)
            {
                using var sh = new SolidBrush(Color.FromArgb(60, 0, 0, 0));
                g.FillEllipse(sh, kx - r - 0.5f, ky - r + 1f, r * 2 + 1, r * 2 + 1);
            }
            using var kb = new SolidBrush(knobHot ? Theme.AccentBright : Color.White);
            g.FillEllipse(kb, kx - r, ky - r, r * 2, r * 2);
            g.SmoothingMode = sm;
        }
    }

    private void DrawPlayButton(Graphics g, Rectangle r, bool hover, bool dim)
    {
        if (!dim)
        {
            // Keep the morph honest: snap to the current state when idle between tweens, and if a non-toggle path
            // (media key, video preview, track switch) flipped _playing mid-tween, abandon the now-stale tween.
            float want = _playing ? 1f : 0f;
            if (_playTween is null) _playMorph = want;
            else if (_playTarget != want) { _playTween.Cancel(); _playTween = null; _playMorph = want; }
        }
        DrawPlayDisc(g, r, hover, dim, _playMorph);
    }

    /// <summary>The play disc: accent (brighter on hover), a dim slab with a plain triangle when idle; otherwise the
    /// glyph cross-fades from the play triangle (<paramref name="morph"/> 0) to the pause bars (1). Shared with the
    /// mini player.</summary>
    internal static void DrawPlayDisc(Graphics g, Rectangle r, bool hover, bool dim, float morph)
    {
        Color disc = dim ? Theme.Blend(Theme.SidebarBg, Color.White, 0.12)
                         : hover ? Theme.AccentBright : Theme.Accent;
        using (var b = new SolidBrush(disc)) g.FillEllipse(b, r);
        var c = new PointF(r.X + r.Width / 2f, r.Y + r.Height / 2f);
        Color fg = dim ? Theme.Faint : Theme.OnAccent;
        if (dim)   // idle → just the play triangle
        {
            using var p = new SolidBrush(fg);
            float s = 6.5f;
            g.FillPolygon(p, new[] { new PointF(c.X - s + 1.5f, c.Y - s), new PointF(c.X - s + 1.5f, c.Y + s), new PointF(c.X + s + 1.5f, c.Y) });
            return;
        }
        float m = morph;                                            // 0 = triangle, 1 = pause bars
        if (m < 1f)   // play triangle fading out
        {
            using var p = new SolidBrush(Color.FromArgb(Math.Clamp((int)Math.Round((1f - m) * 255), 0, 255), fg));
            float s = 6.5f;
            g.FillPolygon(p, new[] { new PointF(c.X - s + 1.5f, c.Y - s), new PointF(c.X - s + 1.5f, c.Y + s), new PointF(c.X + s + 1.5f, c.Y) });
        }
        if (m > 0f)   // pause bars fading in
        {
            using var p = new SolidBrush(Color.FromArgb(Math.Clamp((int)Math.Round(m * 255), 0, 255), fg));
            float bw = 3.5f, gap = 3.5f, bh = 13;
            g.FillRectangle(p, c.X - gap / 2 - bw, c.Y - bh / 2, bw, bh);
            g.FillRectangle(p, c.X + gap / 2, c.Y - bh / 2, bw, bh);
        }
    }

    internal static void DrawCircleGlyph(Graphics g, Rectangle r, bool hover, Action<Graphics, Rectangle, Color> glyph, bool dim)
    {
        if (hover && !dim) { using var hb = new SolidBrush(Theme.RowHover); g.FillEllipse(hb, r); }
        glyph(g, r, dim ? Theme.Faint : hover ? Theme.TextCol : Theme.Subtle);
    }

    // Shuffle/repeat glyphs use Windows' designed icon font. The user picked the Segoe MDL2 Assets
    // rendering (plain "1" on repeat-one); it ships on Win10+ and Win11. Fluent is only a fallback.
    private static readonly string? ModeFont = ResolveModeFont();
    private static string? ResolveModeFont()
    {
        foreach (var n in new[] { "Segoe MDL2 Assets", "Segoe Fluent Icons" })
            try { using var ff = new FontFamily(n); return n; } catch { }
        return null;
    }

    private static Color ModeColor(bool active, bool hover) => active ? Theme.Accent : hover ? Theme.TextCol : Theme.Subtle;

    internal static void DrawShuffle(Graphics g, Rectangle r, bool active, bool hover)
    {
        if (hover) { using var hb = new SolidBrush(Theme.RowHover); using var hp = Theme.RoundedRect(r, Theme.RadControl); g.FillPath(hb, hp); }
        DrawModeGlyph(g, r, "\uE8B1", ModeColor(active, hover));   // Shuffle
    }

    internal static void DrawRepeat(Graphics g, Rectangle r, RepeatMode mode, bool hover)
    {
        if (hover) { using var hb = new SolidBrush(Theme.RowHover); using var hp = Theme.RoundedRect(r, Theme.RadControl); g.FillPath(hb, hp); }
        // RepeatAll glyph (greyed when Off), RepeatOne glyph when One.
        DrawModeGlyph(g, r, mode == RepeatMode.One ? "\uE8ED" : "\uE8EE", ModeColor(mode != RepeatMode.Off, hover));
    }

    // Cached for the ~33fps repaint: the glyph rects are a fixed size, so the font is rebuilt only if the size
    // ever changes, and the centre-format never changes. (Was a per-frame Font + StringFormat allocation.)
    private static readonly StringFormat ModeGlyphFormat = new() { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
    private static Font? _modeGlyphFont;
    private static float _modeGlyphSize = -1f;
    private static void DrawModeGlyph(Graphics g, RectangleF r, string glyph, Color c)
    {
        if (ModeFont is null) return;
        float sz = Math.Min(r.Width, r.Height) * 0.5f;
        if (_modeGlyphFont is null || _modeGlyphSize != sz) { _modeGlyphFont?.Dispose(); _modeGlyphFont = new Font(ModeFont, sz, FontStyle.Regular, GraphicsUnit.Pixel); _modeGlyphSize = sz; }
        using var b = new SolidBrush(c);
        var savedHint = g.TextRenderingHint;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
        g.DrawString(glyph, _modeGlyphFont, b, r, ModeGlyphFormat);
        g.TextRenderingHint = savedHint;
    }

    private static void GlyphPrev(Graphics g, Rectangle r, Color c) => SkipGlyph(g, r, c, 5f, 2.2f, 0.3f, next: false);

    /// <summary>The |&lt; / &gt;| skip glyph (a triangle and a bar) with its BOUNDING BOX centred in <paramref name="r"/>.
    /// The old builders pushed each glyph ~3 px outward from the centre, which read fine bare but sat visibly
    /// off-centre inside the hover disc.</summary>
    private static void SkipGlyph(Graphics g, Rectangle r, Color c, float s, float bw, float gap, bool next)
    {
        var m = new PointF(r.X + r.Width / 2f, r.Y + r.Height / 2f);
        using var b = new SolidBrush(c);
        float w = s + gap + bw, x0 = m.X - w / 2f;
        if (next)
        {
            g.FillPolygon(b, new[] { new PointF(x0, m.Y - s), new PointF(x0, m.Y + s), new PointF(x0 + s, m.Y) });
            g.FillRectangle(b, x0 + s + gap, m.Y - s, bw, s * 2);
        }
        else
        {
            g.FillRectangle(b, x0, m.Y - s, bw, s * 2);
            float tx = x0 + bw + gap;
            g.FillPolygon(b, new[] { new PointF(tx + s, m.Y - s), new PointF(tx + s, m.Y + s), new PointF(tx, m.Y) });
        }
    }

    // Deck-size prev/next (≈13 px tall) — the bar's 10 px pair sits next to a 36 px play disc up there.
    internal static void GlyphPrevL(Graphics g, Rectangle r, Color c) => SkipGlyph(g, r, c, 6.5f, 2.6f, 0.4f, next: false);

    internal static void GlyphNextL(Graphics g, Rectangle r, Color c) => SkipGlyph(g, r, c, 6.5f, 2.6f, 0.4f, next: true);

    private static void GlyphNext(Graphics g, Rectangle r, Color c) => SkipGlyph(g, r, c, 5f, 2.2f, 0.3f, next: true);

    internal static void DrawSpeaker(Graphics g, Rectangle r, bool muted, bool hover)
    {
        Color c = muted ? Theme.Faint : hover ? Theme.TextCol : Theme.Subtle;
        using var b = new SolidBrush(c);
        using var p = new Pen(c, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        float x = r.X, cy = r.Y + r.Height / 2f;
        g.FillPolygon(b, new[]
        {
            new PointF(x, cy - 3), new PointF(x + 5, cy - 3), new PointF(x + 10, cy - 7),
            new PointF(x + 10, cy + 7), new PointF(x + 5, cy + 3), new PointF(x, cy + 3),
        });
        if (muted)
        {
            g.DrawLine(p, x + 13, cy - 5, x + 19, cy + 5);
            g.DrawLine(p, x + 19, cy - 5, x + 13, cy + 5);
        }
        else
        {
            g.DrawArc(p, x + 9, cy - 5, 8, 10, -55, 110);
            g.DrawArc(p, x + 9, cy - 8, 12, 16, -50, 100);
        }
    }

    /// <summary>"···": the utilities the width folded away live under it.</summary>
    internal static void DrawOverflowGlyph(Graphics g, Rectangle r, bool hover)
    {
        if (hover) { using var hb = new SolidBrush(Theme.RowHover); using var hp = Theme.RoundedRect(r, Theme.RadControl); g.FillPath(hb, hp); }
        using var b = new SolidBrush(hover ? Theme.TextCol : Theme.Subtle);
        float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f;
        for (int k = -1; k <= 1; k++) g.FillEllipse(b, cx + k * 6f - 1.7f, cy - 1.7f, 3.4f, 3.4f);
    }

    /// <summary>Lyrics: a speech bubble with two lines of "words", the sung one accented. Deliberately NOT
    /// another stack of plain lines — that is the Up Next glyph sitting right beside it, and at 24 px the two
    /// would read as the same icon.</summary>
    private void DrawLyricsGlyph(Graphics g, Rectangle r, bool hover)
    {
        if (_lyricsOpen) { using var ob = new SolidBrush(Color.FromArgb(46, Theme.Accent)); using var op = Theme.RoundedRect(r, Theme.RadControl); g.FillPath(ob, op); }   // the words are open: the button reads as pressed
        else if (hover) { using var hb = new SolidBrush(Theme.RowHover); using var hp = Theme.RoundedRect(r, Theme.RadControl); g.FillPath(hb, hp); }
        var c = _lyricsOpen ? Theme.AccentBright : hover ? Theme.TextCol : Theme.Subtle;
        int cx = r.Left + r.Width / 2, cy = r.Top + r.Height / 2;

        var bubble = new Rectangle(cx - 8, cy - 8, 16, 12);
        using (var pen = new Pen(c, 1.5f))
        using (var path = Theme.RoundedRect(bubble, 4))
        {
            g.DrawPath(pen, path);
            // the tail, bottom-left, drawn as part of the outline so the bubble reads as speech
            using var tail = new System.Drawing.Drawing2D.GraphicsPath();
            tail.AddLines(new[] { new Point(cx - 4, bubble.Bottom - 1), new Point(cx - 5, cy + 7), new Point(cx, bubble.Bottom - 1) });
            using var fill = new SolidBrush(Theme.PanelBg);
            g.FillPath(fill, tail);
            g.DrawLines(pen, new[] { new Point(cx - 4, bubble.Bottom), new Point(cx - 5, cy + 7), new Point(cx, bubble.Bottom) });
        }

        using (var accent = new Pen(hover ? Theme.Accent : Theme.Blend(c, Theme.Accent, 0.6), 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawLine(accent, cx - 5, cy - 4, cx + 3, cy - 4);   // the line being sung
        using (var pen = new Pen(c, 1.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawLine(pen, cx - 5, cy - 1, cx + 5, cy - 1);
    }

    private void DrawEqGlyph(Graphics g, Rectangle r, bool hover)
    {
        if (hover) { using var hb = new SolidBrush(Theme.RowHover); using var hp = Theme.RoundedRect(r, Theme.RadControl); g.FillPath(hb, hp); }
        Color c = _eqOn ? Theme.Accent : hover ? Theme.TextCol : Theme.Subtle;
        using var bar = new Pen(c, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var dot = new SolidBrush(c);
        float[] xs = { r.X + 7, r.X + 12, r.X + 17 };
        float top = r.Y + 6, bot = r.Bottom - 6;
        float[] knob = { r.Y + 13, r.Y + 9, r.Y + 15 };
        for (int i = 0; i < 3; i++)
        {
            g.DrawLine(bar, xs[i], top, xs[i], bot);
            g.FillEllipse(dot, xs[i] - 2.6f, knob[i] - 2.6f, 5.2f, 5.2f);
        }
    }

    // Pro features icon: a magic wand — a diagonal shaft with a sparkle star at the tip plus a small companion
    // spark — accent-tinted when any Pro feature is on. Distinct from the EQ bars and the speaker.
    private void DrawProGlyph(Graphics g, Rectangle r, bool hover)
    {
        if (hover) { using var hb = new SolidBrush(Theme.RowHover); using var hp = Theme.RoundedRect(r, Theme.RadControl); g.FillPath(hb, hp); }
        Color c = _proOn ? Theme.Accent : hover ? Theme.TextCol : Theme.Subtle;
        float tipX = r.X + 15.5f, tipY = r.Y + 8f;     // sparkle star at the wand's tip (upper-right)
        using (var pen = new Pen(c, 2.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawLine(pen, r.X + 6.5f, r.Bottom - 6.5f, tipX - 2.2f, tipY + 2.2f);   // shaft: lower-left → just below the tip
        using (var b = new SolidBrush(c)) g.FillPolygon(b, Sparkle(tipX, tipY, 4.6f, 1.5f));
        using (var b2 = new SolidBrush(Color.FromArgb(170, c))) g.FillPolygon(b2, Sparkle(r.X + 8.5f, r.Y + 6.5f, 2.4f, 0.8f));
    }

    /// <summary>Reflect the Up Next size so the queue icon tints accent when there are queued tracks.</summary>
    public void SetQueueCount(int n) { if (_queueCount == n) return; _queueCount = n; Invalidate(); }

    // Up Next icon: a small "list" (three lines, the last shorter) — accent-tinted when the queue is non-empty.
    private void DrawQueueGlyph(Graphics g, Rectangle r, bool hover)
    {
        if (_queueOpen) { using var ob = new SolidBrush(Color.FromArgb(46, Theme.Accent)); using var op = Theme.RoundedRect(r, Theme.RadControl); g.FillPath(ob, op); }   // the side card is open: pressed
        else if (hover) { using var hb = new SolidBrush(Theme.RowHover); using var hp = Theme.RoundedRect(r, Theme.RadControl); g.FillPath(hb, hp); }
        Color c = _queueOpen ? Theme.AccentBright : _queueCount > 0 ? Theme.Accent : hover ? Theme.TextCol : Theme.Subtle;
        using var pen = new Pen(c, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        float x = r.X + 6, x2 = r.Right - 6;
        g.DrawLine(pen, x, r.Y + 8, x2, r.Y + 8);
        g.DrawLine(pen, x, r.Y + 12, x2, r.Y + 12);
        g.DrawLine(pen, x, r.Y + 16, x2 - 6, r.Y + 16);
    }

    // A concave 4-point star (outer radius o, inner radius i) centred at (cx,cy).
    private static PointF[] Sparkle(float cx, float cy, float o, float i) => new[]
    {
        new PointF(cx, cy - o), new PointF(cx + i, cy - i), new PointF(cx + o, cy), new PointF(cx + i, cy + i),
        new PointF(cx, cy + o), new PointF(cx - i, cy + i), new PointF(cx - o, cy), new PointF(cx - i, cy - i),
    };

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _eqAnim?.Cancel(); _coverTween?.Cancel(); _playTween?.Cancel(); _sleepFade?.Cancel(); _frostSlideTween?.Cancel(); _sleepTimer?.Dispose(); _smtc?.Dispose(); _discord?.Dispose(); _engine.Dispose(); _cover?.Dispose(); _coverPrev?.Dispose(); _pendingCover?.Dispose(); if (_frostOwned) _frost?.Dispose(); if (_frostOldOwned) _frostOld?.Dispose(); _eqBars.Dispose(); _gradBg?.Dispose(); _gradGlass?.Dispose(); _gradBase?.Dispose(); _fTitle.Dispose(); _fSub.Dispose(); _fTime.Dispose(); _fWordmark.Dispose(); _fCardTitle.Dispose(); }
        base.Dispose(disposing);
    }
}

/// <summary>Four little accent equaliser bars bouncing in a cover's bottom-right corner — the universally-recognised
/// "this is playing" cue — over a soft scrim that keeps them legible on any artwork. One per surface (the bar, the
/// mini player): the scrim gradient + clip path are cached per cover rect and the bar brush per tint, so the ~33 fps
/// playback repaint allocates nothing.</summary>
internal sealed class EqBarsPainter : IDisposable
{
    private static readonly double[] Off = { 0.0, 1.7, 3.3, 5.0 }, Spd = { 1.0, 1.35, 0.85, 1.15 };
    private LinearGradientBrush? _scrim;
    private GraphicsPath? _clip;
    private Rectangle _rect;         // the cover rect the two caches were built for
    private SolidBrush? _bar; private Color _barTint;

    /// <param name="phase">the bounce clock (advanced by the owner's playback loop)</param>
    /// <param name="viz">0..1 per bar from the live audio; the bars never fall below a gentle baseline</param>
    public void Draw(Graphics g, Rectangle cover, double phase, float[] viz, Color tint)
    {
        const int n = 4, bw = 3, gap = 2, maxH = 16;
        int totalW = n * bw + (n - 1) * gap;
        float baseY = cover.Bottom - 7;
        float x0 = cover.Right - 7 - totalW;
        if (_rect != cover) { _scrim?.Dispose(); _scrim = null; _clip?.Dispose(); _clip = null; _rect = cover; }
        _scrim ??= new LinearGradientBrush(new RectangleF(cover.Left, cover.Bottom - 24, cover.Width, 24), Color.FromArgb(0, 0, 0, 0), Color.FromArgb(120, 0, 0, 0), 90f);
        _clip ??= Theme.RoundedRect(new RectangleF(cover.X + 0.5f, cover.Y + 0.5f, cover.Width - 1, cover.Height - 1), cover.Width * Theme.TileFrac);
        using var save = g.Clip;
        g.SetClip(_clip, CombineMode.Intersect);
        g.FillRectangle(_scrim, cover.Left, cover.Bottom - 24, cover.Width, 24);
        g.Clip = save;
        if (_bar is null || _barTint != tint) { _bar?.Dispose(); _barTint = tint; _bar = new SolidBrush(Theme.Blend(tint, Color.White, 0.12)); }
        for (int i = 0; i < n; i++)
        {
            double idle = 0.18 + 0.14 * (0.5 + 0.5 * Math.Sin(phase * Spd[i] + Off[i]));   // gentle baseline so it stays alive
            double v = Math.Max(idle, i < viz.Length ? viz[i] : 0f);                       // …but rises with the actual music
            float bh = (float)(maxH * Math.Clamp(v, 0.12, 1.0));
            g.FillRectangle(_bar, x0 + i * (bw + gap), baseY - bh, bw, bh);
        }
    }

    public void Dispose() { _scrim?.Dispose(); _clip?.Dispose(); _bar?.Dispose(); _scrim = null; _clip = null; _bar = null; }
}
