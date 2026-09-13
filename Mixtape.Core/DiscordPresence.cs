using System.Buffers.Binary;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;

namespace iPodCommander;

/// <summary>
/// Publishes "now playing" to the local Discord client as Rich Presence — the Spotify-style card with the
/// song, the artist and a live progress bar.
///
/// It speaks Discord's local IPC protocol directly (a named pipe on Windows, a unix socket elsewhere), so
/// there is no library, no network call and no token: only the Application ID, which is public by design.
/// Frames are int32 opcode (LE) + int32 length (LE) + UTF-8 JSON.
///
/// TWO HARD RULES shape this class:
///   1. It must never block the caller. Every public method only stores a payload and pokes a background
///      worker — the connect/write/read (which can hang on a wedged Discord client) happens off-thread, on
///      a background thread that can never keep the app alive at exit.
///   2. It must never disturb playback. Discord missing, a stale socket, a refused handshake or a mid-write
///      disconnect are all normal: they just drop the connection, and the next update reconnects.
/// </summary>
public sealed class DiscordPresence : IDisposable
{
    private const int OpHandshake = 0, OpFrame = 1, OpClose = 2;

    /// <summary>Discord accepts a short BURST of activity updates (documented as 5 per 20 s), so a track
    /// change goes out immediately and only a rapid skip-storm is deferred. Sending on a fixed 15 s tick
    /// instead — the obvious reading of the limit — makes every song change look seconds late.</summary>
    private static readonly TimeSpan RateWindow = TimeSpan.FromSeconds(20);
    private const int MaxPerWindow = 5;   // the documented ceiling

    /// <summary>How long to wait between attempts to (re)connect when Discord isn't there.</summary>
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(15);

    private readonly Queue<DateTime> _sendTimes = new();
    private long _seq;   // bumped per publish; a cover that resolves after a track change is discarded

    private readonly object _gate = new();
    private readonly string _appId;
    private readonly SemaphoreSlim _signal = new(0, 1);
    private Thread? _worker;

    private Stream? _pipe;            // NamedPipeClientStream (Windows) or NetworkStream over AF_UNIX
    private Socket? _unix;            // kept so the socket is disposed alongside the stream
    private bool _connected;
    private DateTime _lastAttempt = DateTime.MinValue;
    private Card? _pending;           // newest state waiting for the rate-limit window
    private bool _hasPending;         // set even when _pending is null (a queued "clear")
    private volatile bool _disposed;

    /// <param name="applicationId">The Discord Application ID (Developer Portal, "Application ID"). Public
    /// information — it names the app shown in Discord and needs no secret. Empty = feature off.</param>
    /// <param name="coverArt">Look the album cover up online (Apple's public search API) instead of showing
    /// the app logo. Opt-in: it is the ONLY part of this feature that leaves the machine.</param>
    public DiscordPresence(string applicationId, bool coverArt = false)
    {
        _appId = (applicationId ?? "").Trim();
        _coverArt = coverArt;
    }

    private readonly bool _coverArt;

    /// <summary>What the card should show. Captured on the caller's thread; rendered to JSON on the worker,
    /// where the (blocking) cover lookup is allowed to happen.</summary>
    /// <param name="CoverOnly">True when this card only exists to attach a cover that resolved after the
    /// card was already shown. Such a refresh must never delay a real state change (pause/stop/next): it is
    /// DROPPED rather than queued when the rate-limit window is nearly full.</param>
    private sealed record Card(string Details, string? State, string? Artist, string? Album, string? Title,
                               long StartMs, long EndMs, bool Playing, bool CoverOnly = false);

    /// <summary>True once a handshake has succeeded. Informational only — callers never need to check.</summary>
    public bool IsConnected { get { lock (_gate) return _connected; } }

    /// <summary>
    /// Show a song. <paramref name="position"/> and <paramref name="duration"/> drive the progress bar:
    /// Discord is handed the wall-clock start and end and animates between them itself. Pass
    /// <paramref name="playing"/> = false for a paused song (the bar is dropped, the song stays).
    /// Returns immediately — the actual send happens on the worker thread.
    /// </summary>
    public void SetTrack(string? title, string? artist, string? album, TimeSpan position, TimeSpan duration, bool playing)
    {
        if (_disposed || _appId.Length == 0) return;

        string details = Fit(string.IsNullOrWhiteSpace(title) ? "Unknown song" : title)!;
        string? state = Fit(artist);

        // Stamp the timestamps NOW, on the caller's thread: the worker may only get to this a few seconds
        // later, and a start computed there would drift the progress bar by exactly that delay.
        long startMs = 0, endMs = 0;
        if (playing && duration > TimeSpan.Zero && duration < TimeSpan.FromHours(12))
        {
            if (position < TimeSpan.Zero) position = TimeSpan.Zero;
            if (position > duration) position = duration;
            var start = DateTimeOffset.UtcNow - position;
            startMs = start.ToUnixTimeMilliseconds();
            endMs = (start + duration).ToUnixTimeMilliseconds();
        }

        Publish(new Card(details, state, artist, album, title, startMs, endMs, playing),
                keyed: Key(details, state, playing));
    }

    /// <summary>Remove the presence card (playback stopped, or the user switched the feature off).</summary>
    public void Clear()
    {
        if (_disposed || _appId.Length == 0) return;
        Publish(null, keyed: ClearKey);
    }

    // The nonce and the timestamps differ on every build, so raw payloads can never compare equal. Dedupe on
    // what the user actually SEES instead — otherwise a 5 Hz position tick would queue a send every frame.
    private static string Key(string details, string? state, bool playing) => details + "\u001f" + state + "\u001f" + playing;
    private string? _lastKey;
    private long _lastStartMs;   // the timeline we last published; a JUMP means replay/seek, not tick churn

    private const string ClearKey = "\u001fclear";

    private void Publish(Card? card, string keyed)
    {
        lock (_gate)
        {
            if (_disposed) return;
            // Dedupe on what the card SHOWS, so a position tick can't spam Discord — but let a moved
            // timeline through: repeat-one, replaying the same song and seeking all keep the same text
            // while the progress bar must restart. Steady playback keeps start within a few hundred ms.
            long startMs = card?.StartMs ?? 0;
            if (keyed == _lastKey && !_hasPending && Math.Abs(startMs - _lastStartMs) < 2000) return;
            _lastKey = keyed; _lastStartMs = startMs;
            _pending = card; _hasPending = true; _seq++;
            EnsureWorkerLocked();
        }
        try { _signal.Release(); } catch (SemaphoreFullException) { /* already signalled */ }
    }

    /// <summary>Render a card to the SET_ACTIVITY payload. <paramref name="large"/> is the cover URL, or
    /// null for a text-only card.</summary>
    private string BuildPayload(Card? c, string? large)
    {
        string nonce = Json(Guid.NewGuid().ToString("N"));
        if (c is null)
            return "{\"cmd\":\"SET_ACTIVITY\",\"nonce\":" + nonce +
                   ",\"args\":{\"pid\":" + Environment.ProcessId + "}}";

        var sb = new StringBuilder();
        sb.Append("{\"cmd\":\"SET_ACTIVITY\",\"nonce\":").Append(nonce)
          .Append(",\"args\":{\"pid\":").Append(Environment.ProcessId)
          .Append(",\"activity\":{\"type\":2,\"details\":").Append(Json(c.Details));
        if (c.State is not null) sb.Append(",\"state\":").Append(Json(c.State));
        if (c.Playing && c.EndMs > c.StartMs)
            sb.Append(",\"timestamps\":{\"start\":").Append(c.StartMs).Append(",\"end\":").Append(c.EndMs).Append('}');

        if (large is not null)
        {
            sb.Append(",\"assets\":{\"large_image\":").Append(Json(large));
            string? albumText = Fit(c.Album);
            if (albumText is not null) sb.Append(",\"large_text\":").Append(Json(albumText));
            sb.Append('}');
        }
        return sb.Append("}}}").ToString();
    }

    private void EnsureWorkerLocked()
    {
        if (_worker is not null) return;
        // IsBackground: a wedged Discord write must never hold the process open at exit.
        _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "DiscordPresence" };
        _worker.Start();
    }

    private void WorkerLoop()
    {
        while (!_disposed)
        {
            try
            {
                if (!_signal.Wait(TimeSpan.FromSeconds(30))) { if (_disposed) return; continue; }
                if (_disposed) return;

                // Look at WHAT is queued before deciding to wait. A cover refresh is cosmetic: if the
                // burst allowance is spent it is dropped, never slept on — otherwise the pause the user just
                // pressed would sit behind it for the rest of the window while Discord kept animating the
                // old progress bar. A real state change is worth the wait.
                Card? card; bool has;
                lock (_gate)
                {
                    card = _pending; has = _hasPending;
                    _pending = null; _hasPending = false;
                }
                if (!has) continue;

                TimeSpan wait;
                lock (_gate) wait = WaitForSlotLocked();
                if (wait > TimeSpan.Zero)
                {
                    if (card?.CoverOnly == true) continue;   // cosmetic — let it go, the next update carries it
                    Thread.Sleep(wait > RateWindow ? RateWindow : wait);
                    // The user may have moved on while we waited (paused, skipped): send the NEWEST state,
                    // not the one we picked up before the sleep, so a slot is never spent on stale news.
                    lock (_gate)
                        if (_hasPending) { card = _pending; _pending = null; _hasPending = false; }
                }
                if (_disposed) return;
                long seq; lock (_gate) seq = _seq;

                // Phase 1 — go out NOW with whatever we already know. A cover we have cached is included;
                // an unknown one must never hold the card back, because the lookup can take seconds.
                string? cover = null;
                bool cached = true;
                if (_coverArt)
                {
                    try { cached = CoverArtLookup.TryGetCached(card!.Artist, card.Album, card.Title, out cover); }
                    catch { cached = true; }
                }
                // A failed send must re-arm the dedupe, or the identical state can never be published
                // again (the card would stay stale for the rest of the session).
                if (!Send(BuildPayload(card, cover))) { lock (_gate) _lastKey = null; continue; }

                // Phase 2 — first time we see this album: resolve the cover on a SEPARATE short-lived
                // thread. Doing it here would block the only thread that can deliver a pause or a track
                // change, leaving a stale card animating its progress bar for as long as the lookup takes.
                // The helper only warms CoverArtLookup's cache and re-queues the same card; the worker then
                // picks it up through the normal path, where TryGetCached now answers instantly.
                if (_coverArt && !cached && card is { Playing: true })
                {
                    var c = card; long mySeq = seq;
                    new Thread(() =>
                    {
                        try { CoverArtLookup.Find(c.Artist, c.Album, c.Title); } catch { }
                        if (_disposed) return;
                        lock (_gate)
                        {
                            // Deliberately no _seq bump and no _lastKey touch: a genuinely newer publish
                            // must still win, and this is the same card the user is already seeing.
                            if (_disposed || _seq != mySeq || _hasPending) return;
                            _pending = c with { CoverOnly = true }; _hasPending = true;
                        }
                        try { _signal.Release(); } catch (SemaphoreFullException) { }
                    }) { IsBackground = true, Name = "DiscordCover" }.Start();
                }
            }
            catch { try { Drop(); } catch { } }            // a worker must never die on an exception
        }
    }

    /// <summary>Write one payload, recording it against the rate-limit window. False = not connected or
    /// the pipe broke (already dropped).</summary>
    private bool Send(string payload)
    {
        if (!EnsureConnected()) return false;
        if (!WriteFrame(OpFrame, payload)) { Drop(); return false; }
        lock (_gate) _sendTimes.Enqueue(DateTime.UtcNow);
        return true;
    }

    /// <summary>Zero when a send may go out now; otherwise how long until a slot frees up.</summary>
    private TimeSpan WaitForSlotLocked()
    {
        var now = DateTime.UtcNow;
        while (_sendTimes.Count > 0 && now - _sendTimes.Peek() >= RateWindow) _sendTimes.Dequeue();
        if (_sendTimes.Count < MaxPerWindow) return TimeSpan.Zero;
        return _sendTimes.Peek() + RateWindow - now;
    }

    private bool EnsureConnected()
    {
        lock (_gate) { if (_connected && _pipe is not null) return true; }

        // Don't hammer the socket when Discord isn't running: one attempt per rate-limit window is plenty.
        lock (_gate)
        {
            if (DateTime.UtcNow - _lastAttempt < RetryInterval) return false;
            _lastAttempt = DateTime.UtcNow;
        }

        for (int i = 0; i < 10 && !_disposed; i++)   // Discord numbers its sockets discord-ipc-0 … discord-ipc-9
        {
            try
            {
                if (!TryOpen(i)) continue;
                if (WriteFrame(OpHandshake, "{\"v\":1,\"client_id\":" + Json(_appId) + "}") && ReadFrame())
                {
                    lock (_gate) { _connected = true; _lastKey = null; }   // a fresh connection re-arms a full resend
                    return true;
                }
            }
            catch { /* fall through and try the next socket */ }
            Drop();
        }
        return false;
    }

    private bool TryOpen(int index)
    {
        if (OperatingSystem.IsWindows())
        {
            var pipe = new NamedPipeClientStream(".", $"discord-ipc-{index}", PipeDirection.InOut, PipeOptions.Asynchronous);
            try { pipe.Connect(300); }
            catch { pipe.Dispose(); return false; }
            lock (_gate) _pipe = pipe;
            return true;
        }

        // Linux/macOS: a unix socket under the runtime dir, plus the subdirectories Flatpak/Snap builds use.
        string baseDir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR")
                      ?? Environment.GetEnvironmentVariable("TMPDIR")
                      ?? "/tmp";
        string[] subs = { "", "app/com.discordapp.Discord", "app/com.discordapp.DiscordCanary", "snap.discord" };
        foreach (var sub in subs)
        {
            string path;
            try { path = Path.Combine(baseDir, sub, $"discord-ipc-{index}"); } catch { continue; }
            if (!File.Exists(path)) continue;
            var sock = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified)
            {
                SendTimeout = 2000,      // a wedged client must not hold the worker forever
                ReceiveTimeout = 2000,
            };
            try
            {
                sock.Connect(new UnixDomainSocketEndPoint(path));
                lock (_gate) { _unix = sock; _pipe = new NetworkStream(sock, ownsSocket: false); }
                return true;
            }
            catch { sock.Dispose(); }
        }
        return false;
    }

    private bool WriteFrame(int opcode, string json)
    {
        Stream? s;
        lock (_gate) s = _pipe;
        if (s is null) return false;
        try
        {
            byte[] body = Encoding.UTF8.GetBytes(json);
            var frame = new byte[8 + body.Length];
            BinaryPrimitives.WriteInt32LittleEndian(frame, opcode);
            BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4), body.Length);
            body.CopyTo(frame, 8);
            s.Write(frame, 0, frame.Length);
            s.Flush();
            return true;
        }
        catch { return false; }
    }

    /// <summary>Read one reply frame and discard its body; false means the peer closed or misbehaved.</summary>
    private bool ReadFrame()
    {
        Stream? s;
        lock (_gate) s = _pipe;
        if (s is null) return false;
        try
        {
            var head = new byte[8];
            if (!ReadExact(s, head)) return false;
            int len = BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(4));
            if (len < 0 || len > 1 << 20) return false;   // a sane cap — Discord's replies are tiny
            if (len > 0 && !ReadExact(s, new byte[len])) return false;
            return BinaryPrimitives.ReadInt32LittleEndian(head) != OpClose;
        }
        catch { return false; }
    }

    /// <summary>Read exactly buf.Length bytes, but never wait forever: a pipe that connects and then says
    /// nothing (a wedged Discord, or another program squatting on the name) would otherwise park the worker
    /// permanently. NetworkStream honours its own ReceiveTimeout; the Windows pipe is opened Asynchronous so
    /// a cancelled ReadAsync genuinely aborts the pending read.</summary>
    private static bool ReadExact(Stream s, byte[] buf)
    {
        int got = 0;
        while (got < buf.Length)
        {
            int n;
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                n = s.ReadAsync(buf.AsMemory(got, buf.Length - got), cts.Token).AsTask().GetAwaiter().GetResult();
            }
            catch { return false; }   // timeout, cancellation or a broken pipe — all mean "give up and drop"
            if (n <= 0) return false;
            got += n;
        }
        return true;
    }

    private void Drop()
    {
        Stream? p; Socket? u;
        lock (_gate)
        {
            _connected = false;
            _lastKey = null;      // the next identical state must be allowed through after a drop
            p = _pipe; u = _unix;
            _pipe = null; _unix = null;
        }
        try { p?.Dispose(); } catch { }
        try { u?.Dispose(); } catch { }
    }

    /// <summary>Discord shows at most 128 characters and rejects a field shorter than 2 — trim/pad to fit.</summary>
    private static string? Fit(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        if (s.Length > 128) s = s[..127] + "…";
        return s.Length < 2 ? s + " " : s;
    }

    private static string Json(string s)
    {
        var sb = new StringBuilder(s.Length + 2).Append('"');
        foreach (char c in s)
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        return sb.Append('"').ToString();
    }

    /// <summary>Closing the socket is what removes the card — Discord drops a client's presence the moment
    /// its IPC connection goes away, so there is no need to (and no time to) send anything on the way out.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _signal.Release(); } catch { }
        Drop();
        try { _signal.Dispose(); } catch { }
    }
}
