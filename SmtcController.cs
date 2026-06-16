using System.Drawing.Imaging;
using Windows.Media;
using Windows.Storage.Streams;

namespace iPodCommander;

/// <summary>
/// Bridges playback to Windows' System Media Transport Controls — the Windows 10/11 media flyout
/// (volume pop-up / lock screen) and the global media keys that work even when Mixtape isn't focused.
/// A Win32/WinForms app can't use GetForCurrentView(), so we obtain the controls for the app window via
/// the CsWinRT interop class <see cref="SystemMediaTransportControlsInterop"/>. Everything is guarded:
/// if SMTC is unavailable (older OS, headless render) the whole thing quietly no-ops and the app's own
/// WM_APPCOMMAND media-key handling still works.
/// </summary>
internal sealed class SmtcController : IDisposable
{
    private readonly SystemMediaTransportControls? _smtc;
    private readonly SystemMediaTransportControlsDisplayUpdater? _updater;
    private readonly Action<Action> _toUi;   // marshal a button-press callback onto the UI thread

    public event Action? PlayPause;
    public event Action? Next;
    public event Action? Previous;

    public bool IsAvailable => _smtc is not null;

    public SmtcController(IntPtr hwnd, Action<Action> toUiThread)
    {
        _toUi = toUiThread;
        try
        {
            _smtc = SystemMediaTransportControlsInterop.GetForWindow(hwnd);
            _smtc.IsEnabled = true;
            _smtc.IsPlayEnabled = true;
            _smtc.IsPauseEnabled = true;
            _smtc.IsNextEnabled = true;
            _smtc.IsPreviousEnabled = true;
            _smtc.ButtonPressed += OnButton;
            _updater = _smtc.DisplayUpdater;
            _updater.Type = MediaPlaybackType.Music;
        }
        catch { _smtc = null; _updater = null; } // SMTC not available — fall back to in-app handling
    }

    // Fires on a thread-pool thread → hop to the UI thread before touching playback.
    private void OnButton(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs e)
    {
        Action? a = e.Button switch
        {
            SystemMediaTransportControlsButton.Play or SystemMediaTransportControlsButton.Pause => PlayPause,
            SystemMediaTransportControlsButton.Next => Next,
            SystemMediaTransportControlsButton.Previous => Previous,
            _ => null,
        };
        if (a is not null) _toUi(a);
    }

    public void Playing() => SetStatus(MediaPlaybackStatus.Playing);
    public void Paused() => SetStatus(MediaPlaybackStatus.Paused);
    public void Stopped() => SetStatus(MediaPlaybackStatus.Stopped);

    private void SetStatus(MediaPlaybackStatus status)
    {
        if (_smtc is null) return;
        try { _smtc.PlaybackStatus = status; } catch { }
    }

    /// <summary>Push the current track's title/artist/album (+ cover) to the system media UI.</summary>
    public void SetMetadata(string title, string? artist, string? album, System.Drawing.Bitmap? cover)
    {
        if (_updater is null) return;
        try
        {
            _updater.MusicProperties.Title = title ?? "";
            _updater.MusicProperties.Artist = artist ?? "";
            _updater.MusicProperties.AlbumTitle = album ?? "";
            _updater.Thumbnail = ThumbFrom(cover);
            _updater.Update();
        }
        catch { }
    }

    private static RandomAccessStreamReference? ThumbFrom(System.Drawing.Bitmap? cover)
    {
        if (cover is null) return null;
        try
        {
            var ms = new MemoryStream();                 // kept alive by the returned reference
            cover.Save(ms, ImageFormat.Png);
            ms.Position = 0;
            return RandomAccessStreamReference.CreateFromStream(ms.AsRandomAccessStream());
        }
        catch { return null; }
    }

    public void Dispose()
    {
        if (_smtc is null) return;
        try { _smtc.ButtonPressed -= OnButton; _smtc.IsEnabled = false; } catch { }
    }
}
