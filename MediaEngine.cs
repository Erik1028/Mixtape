namespace iPodCommander;

/// <summary>
/// A thin WinForms wrapper around a WPF <c>MediaElement</c> hosted in an <c>ElementHost</c> so its
/// events fire under the WinForms message loop (proven via <c>--mediatest</c>). It plays whatever
/// Windows Media Foundation supports — MP3 / AAC-M4A / ALAC / WAV / AIFF audio and H.264 MP4/M4V
/// video — which covers everything a click-wheel iPod stores on its volume. The now-playing bar
/// embeds a tiny (audio-only) instance; the video preview fills a dialog with a visible one.
///
/// WPF and WinForms share many type names (Control, Panel, Orientation…), so this file deliberately
/// uses fully-qualified <c>System.Windows.*</c> names rather than a <c>using</c> that would clash.
/// </summary>
internal sealed class MediaEngine : Control
{
    private readonly System.Windows.Forms.Integration.ElementHost _host;
    private readonly System.Windows.Controls.MediaElement _me;
    private readonly System.Windows.Forms.Timer _tick = new() { Interval = 200 };

    /// <summary>Media finished loading — <see cref="Duration"/> / <see cref="HasVideo"/> are now valid.</summary>
    public event Action? Opened;
    /// <summary>Playback reached the end of the file.</summary>
    public event Action? Ended;
    /// <summary>The file could not be played (unsupported codec, missing file, …) with a human message.</summary>
    public event Action<string>? Failed;
    /// <summary>Fires ~5×/sec while media is loaded — drive the seek bar / time labels off this.</summary>
    public event Action? PositionTick;

    public bool IsOpen { get; private set; }

    public MediaEngine()
    {
        _me = new System.Windows.Controls.MediaElement
        {
            LoadedBehavior = System.Windows.Controls.MediaState.Manual,
            UnloadedBehavior = System.Windows.Controls.MediaState.Manual,
            Stretch = System.Windows.Media.Stretch.Uniform,
            Volume = 1.0,
        };
        _me.MediaOpened += (_, _) => { IsOpen = true; Opened?.Invoke(); };
        _me.MediaEnded += (_, _) => { _tick.Stop(); Ended?.Invoke(); };       // stop idle wakeups once playback rests
        _me.MediaFailed += (_, e) => { _tick.Stop(); IsOpen = false; Failed?.Invoke(e.ErrorException?.Message ?? "This file can't be previewed."); };

        _host = new System.Windows.Forms.Integration.ElementHost { Dock = DockStyle.Fill, BackColor = Color.Black, Child = _me };
        Controls.Add(_host);

        _tick.Tick += (_, _) => { if (IsOpen) PositionTick?.Invoke(); };
    }

    public bool HasVideo => _me.NaturalVideoWidth > 0 && _me.NaturalVideoHeight > 0;
    public Size VideoSize => new(Math.Max(0, _me.NaturalVideoWidth), Math.Max(0, _me.NaturalVideoHeight));
    public TimeSpan Duration => _me.NaturalDuration.HasTimeSpan ? _me.NaturalDuration.TimeSpan : TimeSpan.Zero;

    public TimeSpan Position
    {
        get => _me.Position;
        set { if (IsOpen) _me.Position = value; }
    }

    /// <summary>0..1.</summary>
    public double Volume { get => _me.Volume; set => _me.Volume = Math.Clamp(value, 0, 1); }

    /// <summary>Begin loading a local file; <see cref="Opened"/> fires when it's ready.</summary>
    public void Load(string path)
    {
        IsOpen = false;
        _me.Source = new Uri(path);
        _tick.Start();
    }

    public void Play() { _tick.Start(); _me.Play(); } // restart ticks when resuming a rested/ended track
    public void Pause() => _me.Pause();

    /// <summary>Stop and fully release the current media (so the file isn't held open).</summary>
    public void CloseMedia()
    {
        _tick.Stop();
        try { _me.Stop(); _me.Close(); } catch { }
        _me.Source = null;
        IsOpen = false;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _tick.Dispose(); try { _me.Close(); } catch { } }
        base.Dispose(disposing);
    }
}
