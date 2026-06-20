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
    public event Action? ModesChanged;   // user toggled shuffle/repeat — the host persists it

    public enum RepeatMode { Off, All, One }
    public bool Shuffle => _shuffle;
    public RepeatMode Repeat => _repeat;
    /// <summary>Restore the saved shuffle/repeat modes (does not raise <see cref="ModesChanged"/>).</summary>
    public void SetModes(bool shuffle, RepeatMode repeat) { _shuffle = shuffle; _repeat = repeat; Invalidate(); }

    private Track? _track;
    private Bitmap? _cover;
    private const int CoverArtPx = 80;   // resolution to decode the bar cover at (drawn ~56px; headroom for DPI scaling)
    private Bitmap? _coverPrev;          // outgoing cover, held during a track-change cross-dissolve
    private float _coverFade = 1f;       // 0 = cover just changed (show _coverPrev), 1 = settled (show _cover)
    private Tween? _coverTween;
    private Bitmap? _backdrop;     // a tiny, heavily-downsampled snapshot of the list above — stretched = a frosted blur
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
    private double _eqPhase;       // animated "now playing" equaliser bars overlaid on the cover
    private Tween? _eqAnim;
    private int _eqTick;
    private readonly float[] _coverViz = new float[4], _coverTmp = new float[4];   // real-audio cover bars
    private static readonly Rectangle CoverRect = new(16, (H - 56) / 2, 56, 56);

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
            _playing = false; Invalidate();
            if (!Application.MessageLoop) return;        // never block a headless/automation run
            var form = FindForm();
            if (form is null || !form.Visible) return;    // offscreen render form — don't pop an invisible modal
            BeginInvoke(() => MessageDialog.Show(form, msg, "Preview", MessageBoxButtons.OK, MessageBoxIcon.Information));
        };

        MouseDown += OnDown;
        MouseMove += OnMove;
        MouseUp += OnUp;
        MouseLeave += (_, _) => { _hover = Hit.None; Invalidate(); };
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
    public void SeekFraction(double f)
    {
        if (_engine.IsOpen) { _engine.Position = TimeSpan.FromSeconds(Math.Clamp(f, 0, 1) * _engine.Duration.TotalSeconds); InvalidatePrefetch(); }
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
        if (_playing) { _engine.Pause(); _playing = false; _smtc?.Paused(); StopEq(); }
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
    public bool Pause() { if (!_playing) return false; _engine.Pause(); _playing = false; _smtc?.Paused(); StopEq(); Invalidate(); Changed?.Invoke(); return true; }

    /// <summary>Resume after an external pause (e.g. when the video preview closes).</summary>
    public void Resume() { if (_track is not null && !_playing) { _engine.Play(); _playing = true; _smtc?.Playing(); StartEq(); Invalidate(); Changed?.Invoke(); } }

    /// <summary>Load and play a track's file. <paramref name="cover"/> may be null (a gradient is used).</summary>
    public void Play(Track track, string filePath, Bitmap? cover)
    {
        CancelSleepFade();   // a (re)start during the final fade means the user is still listening — don't fade/pause it
        _track = track;
        _path = filePath;
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
        return supplied is null ? null : new Bitmap(supplied);
    }

    /// <summary>Swap the bar cover, cross-dissolving from the outgoing one (same 220 ms art fade the header uses) so
    /// a track change doesn't pop. TAKES OWNERSHIP of <paramref name="next"/>; disposes the outgoing when the fade ends.</summary>
    private void SwapCover(Bitmap? next)
    {
        _coverTween?.Cancel(); _coverTween = null;
        _coverPrev?.Dispose();
        _coverPrev = _cover;     // hold the outgoing cover for the dissolve
        _cover = next;
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
        _playing = false;
        _scrubFrac = -1;
        _smtc?.Stopped();
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
            Invalidate();
            Changed?.Invoke();
            return;
        }
        // Otherwise advance (the host applies shuffle / repeat-all); if there's no next it rests, paused.
        _playing = false;
        _smtc?.Paused();   // Play() will flip back to Playing if a next track starts
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
        if (_playing) { _engine.Pause(); _playing = false; _smtc?.Paused(); StopEq(); }
        else { _engine.Play(); _playing = true; _smtc?.Playing(); StartEq(); }
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

    /// <summary>Set the frosted-glass backdrop — a tiny snapshot of the content above the bar (the host captures
    /// it on scroll/list change). It's stretched across the bar and dimmed, so the list shows through as a blur.
    /// TAKES OWNERSHIP of the bitmap.</summary>
    public void SetBackdrop(Bitmap? tiny)
    {
        var old = _backdrop; _backdrop = tiny; old?.Dispose();
        Invalidate();
    }

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
        public Rectangle Speaker; public bool ShowSpeaker;
        public Rectangle Vol; public bool ShowVol;
    }

    private Lo Layout()
    {
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

    // ---- interaction ----
    private enum Hit { None, Prev, Play, Next, Speaker, Eq, Pro, Queue, Shuffle, Repeat }
    private Hit _hover = Hit.None;

    private void OnDown(object? s, MouseEventArgs e)
    {
        var l = Layout();
        // Shuffle / repeat / EQ / volume are modes & settings — usable even with nothing loaded.
        if (l.ShowModes && l.Shuffle.Contains(e.Location)) { _shuffle = !_shuffle; ModesChanged?.Invoke(); Invalidate(); return; }
        if (l.ShowModes && l.Repeat.Contains(e.Location)) { _repeat = (RepeatMode)(((int)_repeat + 1) % 3); if (_repeat == RepeatMode.One) ClearPending(); ModesChanged?.Invoke(); Invalidate(); return; }
        if (l.ShowEq && l.Eq.Contains(e.Location)) { EqualizerRequested?.Invoke(RectangleToScreen(l.Eq)); return; }
        if (l.ShowPro && l.Pro.Contains(e.Location)) { ProRequested?.Invoke(RectangleToScreen(l.Pro)); return; }
        if (l.ShowQueue && l.Queue.Contains(e.Location)) { QueueRequested?.Invoke(RectangleToScreen(l.Queue)); return; }
        if (l.ShowSpeaker && l.Speaker.Contains(e.Location))
        {
            _muted = !_muted;
            if (!_muted && _volume <= 0.001) _volume = _lastVol > 0.001 ? _lastVol : 0.5;
            _engine.Volume = _muted ? 0 : _volume;
            Invalidate(); Changed?.Invoke(); return;
        }
        if (l.ShowVol && Inflate(l.Vol, 0, 9).Contains(e.Location)) { _drag = Drag.Volume; SetVolumeFromX(l.Vol, e.X); return; }

        if (_track is null) return; // transport needs a loaded track
        if (l.Play.Contains(e.Location)) { TogglePlay(); return; }
        if (l.Prev.Contains(e.Location)) { PrevRequested?.Invoke(); return; }
        if (l.Next.Contains(e.Location)) { NextRequested?.Invoke(); return; }
        if (l.ShowSeek && Inflate(l.Seek, 0, 10).Contains(e.Location)) { _drag = Drag.Seek; ScrubTo(l.Seek, e.X); return; }
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
            : l.ShowModes && l.Shuffle.Contains(e.Location) ? Hit.Shuffle
            : l.ShowModes && l.Repeat.Contains(e.Location) ? Hit.Repeat
            : _track is null ? Hit.None
            : l.Play.Contains(e.Location) ? Hit.Play
            : l.Prev.Contains(e.Location) ? Hit.Prev
            : l.Next.Contains(e.Location) ? Hit.Next
            : Hit.None;
        if (h != _hover) { _hover = h; Invalidate(); }
    }

    private void OnUp(object? s, MouseEventArgs e)
    {
        bool wasDragging = _drag != Drag.None;
        if (_drag == Drag.Seek && _scrubFrac >= 0 && _engine.IsOpen)
        {
            _engine.Position = TimeSpan.FromSeconds(_scrubFrac * _engine.Duration.TotalSeconds);
            InvalidatePrefetch();   // a seek can drop the crossfade's pre-decoded next voice; re-stage it so the next boundary stays seamless
        }
        _scrubFrac = -1;
        _drag = Drag.None;
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

    // ---- paint ----
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (_backdrop is not null)
        {
            // Frosted glass: stretch the tiny snapshot of the list (a smooth blur), then a dark translucent
            // tint + a soft top sheen so the controls stay legible and the list reads as colour behind glass.
            var pom = g.PixelOffsetMode; g.PixelOffsetMode = PixelOffsetMode.Half;
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.DrawImage(_backdrop, new Rectangle(0, 0, Width, H));
            g.PixelOffsetMode = pom; g.InterpolationMode = InterpolationMode.Default;
            // a translucent tint keeps the bar on-theme while the list colour bleeds through; a soft top sheen
            // + the bright top edge sell the pane-of-glass look.
            using (var tint = new SolidBrush(Color.FromArgb(150, Theme.SidebarBg))) g.FillRectangle(tint, 0, 0, Width, H);
            using (var sheen = new LinearGradientBrush(new Rectangle(0, 0, Width, H), Color.FromArgb(32, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 90f))
                g.FillRectangle(sheen, 0, 0, Width, H * 2 / 3);
        }
        else using (var bg = new LinearGradientBrush(new Rectangle(0, 0, Width, H),
            Theme.Blend(Theme.SidebarBg, Color.White, 0.03), Theme.Blend(Theme.SidebarBg, Color.Black, 0.12), 90f))
        {
            bg.InterpolationColors = new ColorBlend
            {
                Colors = new[] { Theme.Blend(Theme.SidebarBg, Color.White, 0.03), Theme.SidebarBg, Theme.Blend(Theme.SidebarBg, Color.Black, 0.12) },
                Positions = new[] { 0f, 0.5f, 1f },
            };
            g.FillRectangle(bg, 0, 0, Width, H);
        }
        // a bright top edge sells the "pane of glass" elevation over the list
        using (var seam = new Pen(Color.FromArgb(_backdrop is not null ? 40 : 255, _backdrop is not null ? Color.White : Theme.Border))) g.DrawLine(seam, 0, 0, Width, 0);

        var l = Layout();
        bool idle = _track is null;
        var cr = l.Cover;
        int cvr = (int)Math.Round(cr.Width * Theme.TileFrac);

        // cover (soft shadow + rounded, clipped art or an idle placeholder). Fill + stroke share a
        // half-pixel-inset rect so every corner antialiases identically (no soft bottom-right edge).
        var crF = new RectangleF(cr.X + 0.5f, cr.Y + 0.5f, cr.Width - 1, cr.Height - 1);
        // Soft drop shadow: aligned left/right with the tile and offset only DOWNWARD, so it reads as an even
        // shadow under the whole tile rather than a darker squared notch poking out of the bottom-right corner.
        using (var shp = Theme.RoundedRect(new RectangleF(cr.X, cr.Y + 2, cr.Width, cr.Height), cvr))
        using (var sh = new SolidBrush(Color.FromArgb(50, 0, 0, 0))) g.FillPath(sh, shp);
        using (var cp = Theme.RoundedRect(crF, cvr))
        {
            var saved = g.Clip; g.SetClip(cp, CombineMode.Intersect);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            if (idle)
                // a quiet recessed tile (just above the bar's own shade), not a bright grey box
                using (var ph = new LinearGradientBrush(cr, Theme.Blend(Theme.SidebarBg, Color.White, 0.09), Theme.Blend(Theme.SidebarBg, Color.Black, 0.06), Theme.ArtAngle)) g.FillRectangle(ph, cr);
            else
            {
                var nv = _cover ?? Theme.MakeArt(cr.Width, (int)(_track!.Dbid & 0xffff));
                if (_coverPrev is not null && _coverFade < 1f)
                {
                    g.DrawImage(_coverPrev, cr);                                                            // outgoing holds underneath
                    Theme.DrawImageAlpha(g, nv, new RectangleF(cr.X, cr.Y, cr.Width, cr.Height), _coverFade); // incoming dissolves in
                }
                else g.DrawImage(nv, cr);
            }
            g.Clip = saved;
        }
        if (idle) Theme.DrawNote(g, cr, Color.FromArgb(110, 255, 255, 255));   // crisp, optically-centred eighth note
        using (var bp = new Pen(Theme.Blend(Theme.SidebarBg, Color.White, 0.10))) { using var cp2 = Theme.RoundedRect(crF, cvr); g.DrawPath(bp, cp2); }
        if (!idle && _playing) DrawEqBars(g, cr);   // animated "now playing" equaliser, bottom-right of the cover

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

        // transport (dimmed + inert when idle); shuffle/repeat are modes — always live
        if (l.ShowModes) DrawShuffle(g, l.Shuffle, _shuffle, _hover == Hit.Shuffle);
        DrawCircleGlyph(g, l.Prev, _hover == Hit.Prev, GlyphPrev, idle);
        DrawPlayButton(g, l.Play, _hover == Hit.Play, idle);
        DrawCircleGlyph(g, l.Next, _hover == Hit.Next, GlyphNext, idle);
        if (l.ShowModes) DrawRepeat(g, l.Repeat, _repeat, _hover == Hit.Repeat);

        // seek
        if (l.ShowSeek)
        {
            double dur = _engine.Duration.TotalSeconds;
            double pos = _engine.IsOpen ? _engine.Position.TotalSeconds : 0;
            double frac = _scrubFrac >= 0 ? _scrubFrac : (dur > 0 ? Math.Clamp(pos / dur, 0, 1) : 0);
            DrawSlider(g, l.Seek, idle ? 0 : frac, !idle);
            if (l.ShowTimes)
            {
                double shown = _scrubFrac >= 0 ? _scrubFrac * dur : pos;
                TextRenderer.DrawText(g, idle ? "0:00" : Fmt(shown), _fTime, new Rectangle(l.Seek.Left - 46, SeekY - 9, 42, 20), Theme.Faint, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
                TextRenderer.DrawText(g, idle ? "0:00" : Fmt(dur), _fTime, new Rectangle(l.Seek.Right + 6, SeekY - 9, 42, 20), Theme.Faint, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            }
        }

        // queue + pro features + equalizer + volume (always interactive when shown)
        if (l.ShowQueue) DrawQueueGlyph(g, l.Queue, _hover == Hit.Queue);
        if (l.ShowPro) DrawProGlyph(g, l.Pro, _hover == Hit.Pro);
        if (l.ShowEq) DrawEqGlyph(g, l.Eq, _hover == Hit.Eq);
        if (l.ShowSpeaker) DrawSpeaker(g, l.Speaker, _muted, _hover == Hit.Speaker);
        if (l.ShowVol) DrawSlider(g, l.Vol, _muted ? 0 : _volume, true);   // knob, matching the seek bar (consistency + a grab target)

        Theme.CarveCardCorners(g, this, Theme.RadShell, false, false, true, true);   // content card's BOTTOM corners
    }

    private static string Fmt(double sec)
    {
        if (sec < 0 || double.IsNaN(sec)) sec = 0;
        var t = TimeSpan.FromSeconds(sec);
        return t.Hours > 0 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }

    private static void DrawSlider(Graphics g, Rectangle track, double frac, bool knob)
    {
        var t = new RectangleF(track.X + 0.5f, track.Y + 0.5f, track.Width - 1, track.Height - 1);
        using (var tb = new SolidBrush(Theme.Blend(Theme.PanelBg, Color.Black, 0.1)))
        using (var tp = Theme.RoundedRect(t, t.Height / 2f)) g.FillPath(tb, tp);
        float fw = (float)(t.Width * Math.Clamp(frac, 0, 1));
        if (fw > 0)
            using (var fb = new SolidBrush(Theme.Accent))
            using (var fp = Theme.RoundedRect(new RectangleF(t.X, t.Y, fw, t.Height), t.Height / 2f)) g.FillPath(fb, fp);
        if (knob)
        {
            float kx = t.X + fw, ky = t.Y + t.Height / 2f;
            using var kb = new SolidBrush(Color.White);
            g.FillEllipse(kb, kx - 5, ky - 5, 10, 10);
        }
    }

    /// <summary>Four little accent equaliser bars bouncing in the cover's bottom-right corner — the
    /// universally-recognised "this is playing" cue. Driven by <see cref="_eqPhase"/>; a soft scrim keeps
    /// them legible over any artwork.</summary>
    private static readonly double[] EqOff = { 0.0, 1.7, 3.3, 5.0 }, EqSpd = { 1.0, 1.35, 0.85, 1.15 };
    private void DrawEqBars(Graphics g, Rectangle cover)
    {
        const int n = 4, bw = 3, gap = 2, maxH = 16;
        int totalW = n * bw + (n - 1) * gap;
        float baseY = cover.Bottom - 7;
        float x0 = cover.Right - 7 - totalW;
        // soft scrim so the bars read on light covers
        using (var scrim = new System.Drawing.Drawing2D.LinearGradientBrush(
            new RectangleF(cover.Left, cover.Bottom - 24, cover.Width, 24), Color.FromArgb(0, 0, 0, 0), Color.FromArgb(120, 0, 0, 0), 90f))
        {
            var save = g.Clip;
            using (var clip = Theme.RoundedRect(new RectangleF(cover.X + 0.5f, cover.Y + 0.5f, cover.Width - 1, cover.Height - 1), cover.Width * Theme.TileFrac))
                g.SetClip(clip, CombineMode.Intersect);
            g.FillRectangle(scrim, cover.Left, cover.Bottom - 24, cover.Width, 24);
            g.Clip = save;
        }
        using var b = new SolidBrush(Theme.AccentBright);
        for (int i = 0; i < n; i++)
        {
            double idle = 0.18 + 0.14 * (0.5 + 0.5 * Math.Sin(_eqPhase * EqSpd[i] + EqOff[i]));  // gentle baseline so it stays alive
            double v = Math.Max(idle, _coverViz[i]);                                          // …but rises with the actual music
            float bh = (float)(maxH * Math.Clamp(v, 0.12, 1.0));
            g.FillRectangle(b, x0 + i * (bw + gap), baseY - bh, bw, bh);
        }
    }

    private void DrawPlayButton(Graphics g, Rectangle r, bool hover, bool dim)
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
        // Keep the morph honest: snap to the current state when idle between tweens, and if a non-toggle path
        // (media key, video preview, track switch) flipped _playing mid-tween, abandon the now-stale tween.
        float want = _playing ? 1f : 0f;
        if (_playTween is null) _playMorph = want;
        else if (_playTarget != want) { _playTween.Cancel(); _playTween = null; _playMorph = want; }
        float m = _playMorph;                                       // 0 = triangle, 1 = pause bars
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

    private static void DrawCircleGlyph(Graphics g, Rectangle r, bool hover, Action<Graphics, Rectangle, Color> glyph, bool dim)
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

    private void DrawShuffle(Graphics g, Rectangle r, bool active, bool hover)
    {
        if (hover) { using var hb = new SolidBrush(Theme.RowHover); using var hp = Theme.RoundedRect(r, Theme.RadControl); g.FillPath(hb, hp); }
        DrawModeGlyph(g, r, "\uE8B1", ModeColor(active, hover));   // Shuffle
    }

    private void DrawRepeat(Graphics g, Rectangle r, RepeatMode mode, bool hover)
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

    private static void GlyphPrev(Graphics g, Rectangle r, Color c)
    {
        var m = new PointF(r.X + r.Width / 2f, r.Y + r.Height / 2f);
        using var b = new SolidBrush(c);
        float s = 5f;
        g.FillPolygon(b, new[] { new PointF(m.X + 1, m.Y - s), new PointF(m.X + 1, m.Y + s), new PointF(m.X - s + 1, m.Y) });
        g.FillRectangle(b, m.X - s - 1.5f, m.Y - s, 2.2f, s * 2);
    }

    private static void GlyphNext(Graphics g, Rectangle r, Color c)
    {
        var m = new PointF(r.X + r.Width / 2f, r.Y + r.Height / 2f);
        using var b = new SolidBrush(c);
        float s = 5f;
        g.FillPolygon(b, new[] { new PointF(m.X - 1, m.Y - s), new PointF(m.X - 1, m.Y + s), new PointF(m.X + s - 1, m.Y) });
        g.FillRectangle(b, m.X + s - 0.7f, m.Y - s, 2.2f, s * 2);
    }

    private static void DrawSpeaker(Graphics g, Rectangle r, bool muted, bool hover)
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
        if (hover) { using var hb = new SolidBrush(Theme.RowHover); using var hp = Theme.RoundedRect(r, Theme.RadControl); g.FillPath(hb, hp); }
        Color c = _queueCount > 0 ? Theme.Accent : hover ? Theme.TextCol : Theme.Subtle;
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
        if (disposing) { _eqAnim?.Cancel(); _coverTween?.Cancel(); _playTween?.Cancel(); _sleepFade?.Cancel(); _sleepTimer?.Dispose(); _smtc?.Dispose(); _engine.Dispose(); _cover?.Dispose(); _coverPrev?.Dispose(); _pendingCover?.Dispose(); _backdrop?.Dispose(); _fTitle.Dispose(); _fSub.Dispose(); _fTime.Dispose(); }
        base.Dispose(disposing);
    }
}
