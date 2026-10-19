using System.Buffers;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace iPodCommander;

/// <summary>What the main window hands the remote API. Every member is called ON THE UI THREAD (the server marshals each
/// request over) and must stay quick: it copies references, and the server builds the reply on its own thread.</summary>
internal interface IRemoteHost
{
    /// <summary>The library the API serves: the iPod's name, "Local Music", or null with neither.</summary>
    string? LibraryName();
    RemoteState State();
    RemoteLibrary Library();
    /// <summary>Where the playing song's lyrics would come from (null = nothing plays).</summary>
    RemoteLyrics? Lyrics();
    /// <summary>Run a command the way its button does. Null = done, else why it could not be.</summary>
    string? Run(RemoteCommand c);
}

/// <summary>The player as the window had it, copied on the UI thread.</summary>
internal sealed class RemoteState
{
    public Track? Track;                               // what the player holds (null = idle)
    public double DurationMs, PositionMs, Volume;
    public bool Playing, Shuffle;
    public NowPlayingBar.RepeatMode Repeat;
    public Track[] Queue = Array.Empty<Track>();       // Up Next, the first RemoteServer.QueueMax of it
    public int QueueSig, LibrarySig, LyricsSig;        // fingerprints: the whole queue, the library, the playing song's lyrics
    public Func<Track, string?> PathOf = _ => null;    // a song's file (for its cover) - safe to call off the UI thread
}

/// <summary>The library's songs (in the song list's order) and its own playlists, as references.</summary>
internal sealed class RemoteLibrary
{
    public List<Track> Songs = new();
    public List<(string Key, string Name, List<Track> Songs)> Playlists = new();
    public int Sig;
    public Func<Track, string?> PathOf = _ => null;
}

/// <summary>What the lyrics views would look the playing song's lyrics up with.</summary>
internal sealed class RemoteLyrics
{
    public string TrackId = "";
    public string? Artist, Title, Path;
    public TimeSpan Duration;
    public int OffsetMs;                               // the listener's own sync nudge for this song (+ = sung later)
}

/// <summary>A command, its arguments already read and checked off the UI thread.</summary>
internal sealed class RemoteCommand
{
    public string Name = "";
    public List<string> Ids = new();
    public int Start, Index, From, To, Stars;
    public double Ms, Volume;
    public bool On;
    public NowPlayingBar.RepeatMode Mode;
}

/// <summary>
/// The remote API, "Mixtape Remote API v1" (the protocol lives with its client: OrbitOverlay's docs/mixtape-remote.md).
/// OrbitOverlay's VR watch opens into a full player - what plays, Up Next, the songs, albums and playlists, the lyrics -
/// and presses Mixtape's buttons, over a named pipe: one UTF-8 JSON object per line each way, answered in order.
///
/// One client at a time; when it goes, the next one is waited for. The pipe is read on its own background thread, and
/// anything the window owns is read or changed on the UI thread (<see cref="IRemoteHost"/>), handed over with a short
/// timeout so a busy window answers "busy" instead of hanging the client. The big replies (the library, covers, lyrics)
/// are built here, off the UI thread, from what the window already holds. With no client the thread only waits.
///
/// Access is this Windows user's alone: the pipe's ACL grants the user's SID and nobody else. By the SID on purpose -
/// OrbitOverlay runs elevated as the same user, and PipeOptions.CurrentUserOnly compares the elevation too and would
/// lock it out.
/// </summary>
internal sealed class RemoteServer : IDisposable
{
    public const string PipeName = "Mixtape.Remote.v1";
    public const int Api = 1;
    public const int QueueMax = 100;                   // Up Next entries a state reply carries
    private const int UiTimeoutMs = 2000;              // the window must take a request up within this, or the answer is "busy"
    private const int WriteTimeoutMs = 10000;          // a client that stops reading for this long is let go
    private const int MaxLine = 8 << 20;               // a request line (playSongs with a whole library is ~250 KB)
    private const string Busy = "Mixtape is busy - try again in a moment";

    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly JsonDocumentOptions ReaderOptions = new() { MaxDepth = 16 };

    private readonly string _name;
    private readonly Control _ui;
    private readonly IRemoteHost _host;
    private readonly Thread _thread;
    private readonly CancellationTokenSource _stop = new();
    private NamedPipeServerStream? _pipe;              // the one instance, reused client after client (only this thread touches it)
    private bool _disposed;

    // the reply being written (one per connection, so a client that asked for the library doesn't keep 2 MB after it left)
    private ArrayBufferWriter<byte> _out = new();
    private Utf8JsonWriter? _w;

    public static RemoteServer Start<THost>(THost host, string name = PipeName) where THost : Control, IRemoteHost => new(host, host, name);

    private RemoteServer(Control ui, IRemoteHost host, string name)
    {
        _ui = ui; _host = host; _name = name;
        _thread = new Thread(Loop) { IsBackground = true, Name = "Mixtape remote" };
        _thread.Start();
    }

    /// <summary>Stop serving: the waiting thread is woken and leaves, the pipe closes (a connected client sees it go).
    /// Called on the UI thread; returns within a second.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stop.Cancel();
        if (!_thread.Join(1000)) try { _pipe?.Dispose(); } catch { }   // (only if the thread is stuck somewhere unforeseen)
    }

    private static string AppVersion =>
        typeof(RemoteServer).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "";

    /// <summary>A song's identity on the API: the persistent Dbid as a decimal string; a PC file by its path (the same
    /// "file:" form the "Continue listening" bookmark uses).</summary>
    public static string IdOf(Track t) => !string.IsNullOrEmpty(t.LocalPath) ? "file:" + t.LocalPath : t.Dbid.ToString(CultureInfo.InvariantCulture);

    // ---- the connection ----

    private void Loop()
    {
        var ct = _stop.Token;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                _pipe ??= CreatePipe(_name);
                _pipe.WaitForConnectionAsync(ct).GetAwaiter().GetResult();
                Serve(_pipe, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) when (_pipe is null)
            {
                // the name is taken (another copy serves it) or the pipe can't be made: try again in a while, quietly
                MainForm.Trace("remote: no pipe - " + ex.Message);
                if (ct.WaitHandle.WaitOne(5000)) break;
                continue;
            }
            catch (Exception ex) { MainForm.Trace("remote: the client went - " + ex.Message); }   // hung up mid-reply, stopped reading, ...
            // ready for the next client: the same pipe disconnected, or a fresh one if this one broke
            try { _pipe?.Disconnect(); }
            catch { try { _pipe?.Dispose(); } catch { } _pipe = null; }
            _out = new ArrayBufferWriter<byte>(); _w = null;
        }
        try { _pipe?.Dispose(); } catch { }
        _pipe = null;
    }

    /// <summary>The pipe, open to this Windows user's SID only (full access) - whatever the integrity level of the process
    /// that connects, so an elevated OrbitOverlay of the same user gets in. FirstPipeInstance: never a second instance
    /// beside someone else's pipe of the same name.</summary>
    private static NamedPipeServerStream CreatePipe(string name)
    {
        var me = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("no user SID");
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(me, PipeAccessRights.FullControl, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 64 * 1024, 64 * 1024, security);
    }

    /// <summary>Read request lines until the client hangs up. A line longer than <see cref="MaxLine"/> is skipped to its
    /// end and answered with an error; the connection carries on.</summary>
    private void Serve(NamedPipeServerStream pipe, CancellationToken ct)
    {
        MainForm.Trace("remote: a client connected");
        var buf = new byte[16 * 1024];
        var line = new MemoryStream();
        bool tooLong = false;
        while (true)
        {
            int n = pipe.ReadAsync(buf, ct).AsTask().GetAwaiter().GetResult();
            if (n == 0) return;   // hung up
            int from = 0;
            for (int i = 0; i < n; i++)
            {
                if (buf[i] != (byte)'\n') continue;
                if (!tooLong) line.Write(buf, from, i - from);
                Answer(pipe, tooLong ? null : line, ct);
                line.SetLength(0); tooLong = false;
                from = i + 1;
            }
            if (!tooLong && line.Length + (n - from) > MaxLine) { tooLong = true; line.SetLength(0); }
            if (!tooLong) line.Write(buf, from, n - from);
        }
    }

    /// <summary>One request line in, one reply line out (none for a blank line). <paramref name="line"/> null = the line
    /// was too long to read.</summary>
    private void Answer(Stream pipe, MemoryStream? line, CancellationToken ct)
    {
        var bytes = line is null ? ReadOnlyMemory<byte>.Empty : new ReadOnlyMemory<byte>(line.GetBuffer(), 0, (int)line.Length);
        bytes = Trim(bytes);
        if (line is not null && bytes.IsEmpty) return;

        JsonDocument? doc = null;
        string? error = line is null ? "the line is too long" : null;
        if (error is null)
            try { doc = JsonDocument.Parse(bytes, ReaderOptions); }
            catch { error = "not JSON"; }
        using (doc)
        {
            var root = doc?.RootElement ?? default;
            bool isObject = doc is not null && root.ValueKind == JsonValueKind.Object;
            JsonElement? id = isObject && root.TryGetProperty("id", out var idEl) ? idEl : null;
            if (error is null && !isObject) error = "a request is a JSON object";
            string? cmd = isObject && root.TryGetProperty("cmd", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
            if (error is null && cmd is null) error = "no cmd";

            var w = Begin(id);
            try
            {
                if (error is null) error = Dispatch(cmd!, root, w);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                MainForm.Trace("remote: " + cmd + " failed - " + ex);
                w = Begin(id);   // start the reply over: whatever was half-written goes
                error = "internal error: " + ex.Message;
            }
            if (error is not null) { w.WriteBoolean("ok", false); w.WriteString("error", error); }
            w.WriteEndObject();
            w.Flush();
        }
        _out.GetSpan(1)[0] = (byte)'\n';
        _out.Advance(1);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(WriteTimeoutMs);
        try { pipe.WriteAsync(_out.WrittenMemory, timeout.Token).AsTask().GetAwaiter().GetResult(); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new IOException("the client stopped reading"); }
        pipe.Flush();
    }

    /// <summary>A fresh reply: {"id": the request's id (echoed as it came), ...</summary>
    private Utf8JsonWriter Begin(JsonElement? id)
    {
        _out.Clear();
        if (_w is null) _w = new Utf8JsonWriter(_out, WriterOptions); else _w.Reset(_out);
        _w.WriteStartObject();
        _w.WritePropertyName("id");
        if (id is { } e) e.WriteTo(_w); else _w.WriteNullValue();
        return _w;
    }

    private static ReadOnlyMemory<byte> Trim(ReadOnlyMemory<byte> m)
    {
        var s = m.Span;
        int a = 0, b = s.Length;
        if (b >= 3 && s[0] == 0xEF && s[1] == 0xBB && s[2] == 0xBF) a = 3;   // a stray BOM
        while (a < b && s[a] is (byte)' ' or (byte)'\t' or (byte)'\r') a++;
        while (b > a && s[b - 1] is (byte)' ' or (byte)'\t' or (byte)'\r') b--;
        return m.Slice(a, b - a);
    }

    // ---- the requests ----

    private RemoteState? _lastState;   // the last reading: what a busy window's state request gets

    /// <summary>Answer one request. Returns null once the reply's fields (from "ok": true on) are written, or an error
    /// before anything is.</summary>
    private string? Dispatch(string cmd, JsonElement r, Utf8JsonWriter w)
    {
        switch (cmd)
        {
            case "hello":
            {
                if (!TryUi(_host.LibraryName, out var name)) return Busy;
                w.WriteBoolean("ok", true);
                w.WriteString("app", "Mixtape");
                w.WriteString("version", AppVersion);
                w.WriteNumber("api", Api);
                if (name is null) w.WriteNull("library"); else w.WriteString("library", name);
                return null;
            }
            case "state":
            {
                // a busy window (a long write, a big read) still answers: with what it said last
                if (TryUi(_host.State, out var fresh)) { Note(fresh); _lastState = fresh; }
                var s = _lastState;
                if (s is null) return Busy;
                w.WriteBoolean("ok", true);
                WriteState(w, s);
                return null;
            }
            case "library":
            {
                if (!TryUi(_host.Library, out var lib)) return Busy;
                NoteLibrary(lib.Sig);
                w.WriteBoolean("ok", true);
                WriteLibrary(w, lib);
                return null;
            }
            case "cover": return WriteCover(r, w);
            case "lyrics": return WriteLyrics(w);
            default:
            {
                string? bad = ReadCommand(cmd, r, out var c);
                if (bad is not null) return bad;
                if (!TryUi(() => _host.Run(c), out var refused)) return Busy;
                if (refused is not null) return refused;
                w.WriteBoolean("ok", true);
                return null;
            }
        }
    }

    private void WriteState(Utf8JsonWriter w, RemoteState s)
    {
        w.WriteNumber("v", _v);
        w.WriteBoolean("playing", s.Playing);
        if (s.Track is { } t)
        {
            w.WriteStartObject("track");
            w.WriteString("id", IdOf(t));
            w.WriteString("title", t.DisplayTitle);
            w.WriteString("artist", t.Artist ?? "");
            w.WriteString("album", t.Album ?? "");
            w.WriteNumber("durationMs", (long)Math.Round(s.DurationMs));
            w.WriteNumber("rating", Math.Clamp(t.Rating / 20, 0, 5));
            WriteCoverKey(w, t, s.PathOf);
            w.WriteEndObject();
        }
        else w.WriteNull("track");
        w.WriteNumber("positionMs", (long)Math.Round(Math.Clamp(s.PositionMs, 0, Math.Max(0, s.DurationMs))));
        w.WriteNumber("volume", Math.Round(s.Volume, 3));
        w.WriteBoolean("shuffle", s.Shuffle);
        w.WriteString("repeat", s.Repeat switch { NowPlayingBar.RepeatMode.All => "all", NowPlayingBar.RepeatMode.One => "one", _ => "off" });
        w.WriteStartArray("queue");
        foreach (var q in s.Queue)
        {
            w.WriteStartObject();
            w.WriteString("id", IdOf(q));
            w.WriteString("title", q.DisplayTitle);
            w.WriteString("artist", q.Artist ?? "");
            w.WriteNumber("durationMs", q.LengthMs);
            WriteCoverKey(w, q, s.PathOf);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteNumber("queueV", _queueV);
        w.WriteNumber("libraryV", _libraryV);
        w.WriteNumber("lyricsV", _lyricsV);
    }

    private void WriteLibrary(Utf8JsonWriter w, RemoteLibrary lib)
    {
        w.WriteNumber("v", _libraryV);
        w.WriteStartArray("songs");
        foreach (var t in lib.Songs)
        {
            w.WriteStartObject();
            w.WriteString("id", IdOf(t));
            w.WriteString("title", t.DisplayTitle);
            w.WriteString("artist", t.Artist ?? "");
            w.WriteString("album", t.Album ?? "");
            w.WriteString("albumArtist", t.AlbumArtist ?? "");
            w.WriteNumber("durationMs", t.LengthMs);
            w.WriteNumber("track", t.TrackNumber);
            w.WriteNumber("disc", t.DiscNumber);
            w.WriteNumber("year", t.Year);
            WriteCoverKey(w, t, lib.PathOf);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteStartArray("playlists");
        foreach (var (key, name, songs) in lib.Playlists)
        {
            w.WriteStartObject();
            w.WriteString("key", key);
            w.WriteString("name", name);
            w.WriteStartArray("songs");
            foreach (var t in songs) w.WriteStringValue(IdOf(t));
            w.WriteEndArray();
            w.WriteEndObject();
        }
        w.WriteEndArray();
        _coversFull = true;   // every cover of this library is known by key now
    }

    /// <summary>The playing song's lyrics, from wherever Mixtape's own lyrics views would take them - a .lrc beside the file,
    /// a sheet picked or found before, the file's tags - but never the internet: that lookup stays with the lyrics panel.
    /// Synced lines carry the listener's sync nudge, so they light up when the panel's would.</summary>
    private string? WriteLyrics(Utf8JsonWriter w)
    {
        if (!TryUi(_host.Lyrics, out var src)) return Busy;
        IReadOnlyList<LyricLine> lines = Array.Empty<LyricLine>();
        bool synced = false;
        if (src is not null)
            try { lines = LyricsLookup.Find(src.Artist, src.Title, src.Duration, src.Path, online: false, out synced); }
            catch { lines = Array.Empty<LyricLine>(); synced = false; }
        w.WriteBoolean("ok", true);
        // The protocol names the song "id" - the key the request's own id already has. Both go out: the request's id
        // first (the envelope every reply has), the song's last, which is the one JSON readers keep for a repeated name
        // (System.Text.Json, JavaScript, Python); "trackId" says it once more for a reader that keeps the first.
        if (src is null) w.WriteNull("trackId"); else w.WriteString("trackId", src.TrackId);
        if (src is null) w.WriteNull("id"); else w.WriteString("id", src.TrackId);
        w.WriteBoolean("synced", synced && lines.Count > 0);
        w.WriteStartArray("lines");
        int offset = src?.OffsetMs ?? 0;
        foreach (var l in lines)
        {
            w.WriteStartObject();
            w.WriteNumber("ms", synced ? Math.Max(0, (long)Math.Round(l.At.TotalMilliseconds) + offset) : 0);
            w.WriteString("text", l.Text);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        return null;
    }

    // ---- covers ----

    // An opaque key per album art (the album's tracks share it), and where to read that art: Mixtape's own cover key for
    // the track and a song file of it. Filled as songs go out in state and library replies; only this thread touches it.
    private readonly Dictionary<string, (string Art, string? Path)> _covers = new(StringComparer.Ordinal);
    private bool _coversFull;   // the whole current library has been keyed (a library reply, or a lookup after a miss)

    private void WriteCoverKey(Utf8JsonWriter w, Track t, Func<Track, string?> pathOf)
    {
        if (CoverKey(t, pathOf) is { } key) w.WriteString("cover", key); else w.WriteNull("cover");
    }

    /// <summary>The key of the song's album art, or null when there is nothing to read one from (no file, nothing
    /// downloaded for the album). The key is a hash of Mixtape's own cover key, so it is the same from run to run.</summary>
    private string? CoverKey(Track t, Func<Track, string?> pathOf)
    {
        string art = ArtworkService.KeyFor(t);
        string key = "c" + Fnv(art).ToString("x16", CultureInfo.InvariantCulture);
        if (_covers.TryGetValue(key, out var known) && known.Path is not null) return key;
        string? path = pathOf(t);
        if (path is null && CoverDownloads.PathFor(art) is null) return known.Art is null ? null : key;
        if (_covers.Count > 100_000) _covers.Clear();   // (a bound for a session that sees many libraries)
        _covers[key] = (art, path);
        return key;
    }

    /// <summary>{"cmd": "cover", "key": ..., "size": 64|128|256} → the art fitted whole into a size×size square (aspect
    /// kept, centred, transparent around), 32-bit BGRA, premultiplied, rows top to bottom, in base64.</summary>
    private string? WriteCover(JsonElement r, Utf8JsonWriter w)
    {
        string? key = Str(r, "key");
        if (key is null) return "cover needs key";
        int size = Int(r, "size", out int sz) ? sz : 256;
        size = size <= 64 ? 64 : size <= 128 ? 128 : 256;   // the protocol's three sizes; anything else takes the next one up
        if (!_covers.ContainsKey(key) && !_coversFull && TryUi(_host.Library, out var lib))
        {
            // a key from before (the client kept it across a reconnect, or asked before the library): key the library now
            foreach (var t in lib.Songs) CoverKey(t, lib.PathOf);
            _coversFull = true;
        }
        if (!_covers.TryGetValue(key, out var src)) return "unknown cover";
        using var bmp = ArtworkService.LoadFit(src.Art, src.Path, size);
        if (bmp is null) return "no cover";
        var data = bmp.LockBits(new Rectangle(0, 0, size, size), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        byte[] bgra = new byte[size * size * 4];
        try
        {
            for (int y = 0; y < size; y++)
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, bgra, y * size * 4, size * 4);
        }
        finally { bmp.UnlockBits(data); }
        w.WriteBoolean("ok", true);
        w.WriteNumber("w", size);
        w.WriteNumber("h", size);
        w.WriteBase64String("bgra", bgra);
        return null;
    }

    private static ulong Fnv(string s)
    {
        ulong h = 14695981039346656037UL;
        foreach (char c in s) { h ^= c; h *= 1099511628211UL; }
        return h;
    }

    // ---- the versions: each moves when what it covers differs from the last reading of it ----

    private int _v = 1, _queueV = 1, _libraryV = 1, _lyricsV = 1;
    private bool _seen;   // a first reading set the baseline
    private Track? _sTrack;
    private int _sRating, _sQueue, _sLibrary, _sLyrics;
    private long _sVolume;
    private bool _sPlaying, _sShuffle;
    private NowPlayingBar.RepeatMode _sRepeat;

    /// <summary>A fresh state reading: move the versions it changes. "v" moves on anything but the position.</summary>
    private void Note(RemoteState s)
    {
        int rating = s.Track is null ? 0 : Math.Clamp(s.Track.Rating / 20, 0, 5);
        long volume = (long)Math.Round(s.Volume * 1000);
        if (_seen)
        {
            bool queue = s.QueueSig != _sQueue, library = s.LibrarySig != _sLibrary, lyrics = s.LyricsSig != _sLyrics;
            if (queue) _queueV++;
            if (library) { _libraryV++; _coversFull = false; }
            if (lyrics) _lyricsV++;
            if (queue || library || lyrics || !ReferenceEquals(s.Track, _sTrack) || rating != _sRating || s.Playing != _sPlaying
                || s.Shuffle != _sShuffle || s.Repeat != _sRepeat || volume != _sVolume) _v++;
        }
        _seen = true;
        _sTrack = s.Track; _sRating = rating; _sPlaying = s.Playing; _sShuffle = s.Shuffle; _sRepeat = s.Repeat; _sVolume = volume;
        _sQueue = s.QueueSig; _sLibrary = s.LibrarySig; _sLyrics = s.LyricsSig;
    }

    /// <summary>A library reading: the same library version the state replies carry.</summary>
    private void NoteLibrary(int sig)
    {
        if (_seen && sig != _sLibrary) { _libraryV++; _v++; _coversFull = false; }
        _sLibrary = sig;
    }

    // ---- the commands' arguments ----

    private static string? ReadCommand(string cmd, JsonElement r, out RemoteCommand c)
    {
        c = new RemoteCommand { Name = cmd };
        switch (cmd)
        {
            case "play": case "pause": case "toggle": case "next": case "prev": case "queueClear":
                return null;
            case "seek":
                return Num(r, "ms", out c.Ms) ? null : "seek needs ms";
            case "volume":
                return Num(r, "v", out c.Volume) ? null : "volume needs v (0..1)";
            case "shuffle":
                return Bool(r, "on", out c.On) ? null : "shuffle needs on (true or false)";
            case "repeat":
                switch (Str(r, "mode"))
                {
                    case "off": c.Mode = NowPlayingBar.RepeatMode.Off; return null;
                    case "all": c.Mode = NowPlayingBar.RepeatMode.All; return null;
                    case "one": c.Mode = NowPlayingBar.RepeatMode.One; return null;
                    default: return "repeat needs mode \"off\", \"all\" or \"one\"";
                }
            case "playSongs":
                if (!Ids(r, c.Ids)) return "playSongs needs ids";
                if (!r.TryGetProperty("start", out _)) c.Start = 0;
                else if (!Int(r, "start", out c.Start)) return "start is a number";
                return null;
            case "queueAdd": case "queueNext":
                return Ids(r, c.Ids) ? null : cmd + " needs ids";
            case "queueRemove": case "queueJump":
                return Int(r, "index", out c.Index) ? null : cmd + " needs index";
            case "queueMove":
                return Int(r, "from", out c.From) && Int(r, "to", out c.To) ? null : "queueMove needs from and to";
            case "rate":
                return Int(r, "stars", out c.Stars) && c.Stars is >= 0 and <= 5 ? null : "rate needs stars (0..5)";
            default:
                return "unknown command: " + cmd;
        }
    }

    private static string? Str(JsonElement r, string name) =>
        r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Num(JsonElement r, string name, out double d)
    {
        d = 0;
        return r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out d) && double.IsFinite(d);
    }

    private static bool Int(JsonElement r, string name, out int i)
    {
        i = 0;
        if (!Num(r, name, out double d) || d != Math.Floor(d) || d < int.MinValue || d > int.MaxValue) return false;
        i = (int)d;
        return true;
    }

    private static bool Bool(JsonElement r, string name, out bool b)
    {
        b = false;
        if (!r.TryGetProperty(name, out var v) || v.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
        b = v.GetBoolean();
        return true;
    }

    /// <summary>"ids": song ids, as strings (a Dbid may also come as a plain number). At least one.</summary>
    private static bool Ids(JsonElement r, List<string> ids)
    {
        if (!r.TryGetProperty("ids", out var a) || a.ValueKind != JsonValueKind.Array) return false;
        foreach (var e in a.EnumerateArray())
        {
            if (e.ValueKind == JsonValueKind.String) ids.Add(e.GetString() ?? "");
            else if (e.ValueKind == JsonValueKind.Number && e.TryGetUInt64(out ulong n)) ids.Add(n.ToString(CultureInfo.InvariantCulture));
            else return false;
        }
        return ids.Count > 0;
    }

    // ---- the UI thread ----

    /// <summary>Run <paramref name="f"/> on the UI thread and wait for it - false when the window doesn't take it up in
    /// <see cref="UiTimeoutMs"/> (busy, or closing). A job the wait gave up on never runs late.</summary>
    private bool TryUi<T>(Func<T> f, out T result)
    {
        result = default!;
        var job = new UiJob<T>(f);
        try
        {
            if (_ui.IsDisposed || !_ui.IsHandleCreated) return false;
            _ui.BeginInvoke(job.Run);
        }
        catch { return false; }   // the window is going away
        if (!job.Wait(UiTimeoutMs, _stop.Token)) return false;
        if (job.Error is { } ex) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex).Throw();
        result = job.Result;
        return true;
    }

    private sealed class UiJob<T>
    {
        private readonly Func<T> _f;
        private int _state;                                  // 0 waiting, 1 taken up, 2 given up
        private readonly ManualResetEventSlim _done = new(); // (not disposed: a late Set must never throw on the UI thread)
        public T Result = default!;
        public Exception? Error;

        public UiJob(Func<T> f) => _f = f;

        public void Run()
        {
            if (Interlocked.CompareExchange(ref _state, 1, 0) != 0) return;   // the server gave up on it
            try { Result = _f(); }
            catch (Exception ex) { Error = ex; }
            finally { _done.Set(); }
        }

        public bool Wait(int ms, CancellationToken ct)
        {
            try
            {
                if (_done.Wait(ms, ct)) return true;
                if (Interlocked.CompareExchange(ref _state, 2, 0) == 0) return false;   // never taken up: it won't be now
                return _done.Wait(30000, ct);   // running on the UI thread right now: let it finish
            }
            catch (OperationCanceledException) { Interlocked.CompareExchange(ref _state, 2, 0); throw; }
        }
    }
}
