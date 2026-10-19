using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace iPodCommander;

/// <summary>
/// The window's side of the remote API (RemoteApi.cs; the protocol is OrbitOverlay's docs/mixtape-remote.md): what
/// OrbitOverlay's VR watch reads - the player, Up Next, the library, the lyrics - and the buttons it presses. The server
/// calls every member here on the UI thread, and every command runs the code the window's own button or menu runs, so a
/// song started from VR counts its play, reaches the Listening page, the Windows media flyout and Discord like any other.
/// </summary>
internal sealed partial class MainForm : IRemoteHost
{
    private RemoteServer? _remote;

    /// <summary>The list the remote last started (playSongs: an album, a playlist, the song list from VR). The songs after
    /// the one playing wait in Up Next; when Up Next runs out on one of these songs, this list says what follows - Repeat
    /// All goes round it again, otherwise playback rests at its end - not the list on screen, which VR never saw. Prev
    /// (without shuffle) steps back through it. Dropped with the library it came from.</summary>
    private List<Track>? _remoteList;
    private int _lyricsRev;   // moves when the lyrics views (re)load the playing song's sheet: found online, another version picked
    private int _localRev;    // moves with every finished scan of the PC library
    private Dictionary<ulong, Track>? _remoteByDbid; private ITunesDb? _remoteByDbidOf;              // the remote's ids → songs,
    private Dictionary<string, Track>? _remoteByPath; private int _remoteByPathOf = -1;              // rebuilt when their library changes

    /// <summary>Test harness (--remotetest): every song plays this file instead of its own (the sandbox iPod has none).</summary>
    internal static Func<Track, string?>? PlayPathOverride;

    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr hWnd);

    /// <summary>Start or stop the server to match the setting - in the app only, never in a render or the start-up bench
    /// (that one runs beside the user's own copy, which owns the pipe).</summary>
    private void ApplyRemote()
    {
        bool want = _autoDetect && _settings.RemoteApi && !StartupClock.On;
        if (want && _remote is null) _remote = RemoteServer.Start(this);
        else if (!want && _remote is not null) { _remote.Dispose(); _remote = null; }
    }

    /// <summary>Harness (--remotetest): serve on a pipe of the test's own, beside any running Mixtape.</summary>
    internal void StartRemoteForTest(string pipeName) => _remote ??= RemoteServer.Start(this, pipeName);
    /// <summary>Harness (--remotetest): as if the first-write question had been answered (the sandbox iPod is the test's).</summary>
    internal void PreviewConfirmWrite() => _writeConfirmed = true;
    internal bool PreviewCanEditRatings => CanEditRatings;
    internal bool PreviewWillAskBeforeWrite => _settings.ConfirmWrites && !_writeConfirmed;
    /// <summary>Harness (--remotetest): the desktop's own moves - a playlist opened in the list (its rows' ids back), a
    /// row double-clicked.</summary>
    internal List<string> PreviewOpenPlaylist(int index)
    {
        var pl = _shownPlaylists.Where(p => _db is not null && !ReferenceEquals(p, _db.Master)).ElementAtOrDefault(index);
        if (pl is null) return new List<string>();
        _viewKind = SidebarRowKind.Playlist; _current = pl; _browseFilter = null;
        BuildSidebar(); ShowCurrent();
        return _tracks.Rows.Cast<DataGridViewRow>().Select(r => r.Tag is Track t ? RemoteServer.IdOf(t) : "").ToList();
    }
    internal void PreviewActivateRow(int row) => ActivateTrackRow(row);

    // ---- a list the remote started ----

    private bool RemoteListActive => _remoteList is not null && _playingTrack is not null && _remoteList.Contains(_playingTrack);

    /// <summary>Up Next has run out on a song of the remote's list under Repeat All: the list again, from its top (Shuffle
    /// on: all of it in a new random order, the song just heard not first).</summary>
    private void RemoteListWrap()
    {
        if (!_queue.IsEmpty || _nowPlaying.Repeat != NowPlayingBar.RepeatMode.All || !RemoteListActive) return;
        _queue.Add(_nowPlaying.Shuffle ? Shuffled(_remoteList!, _playingTrack) : _remoteList!);
    }

    /// <summary>Prev inside the remote's list (not in shuffle, which walks the history as ever): the song before in that
    /// list, the one we leave back at the front of Up Next - an iPod's album. On its first song, the song starts over.</summary>
    private bool RemoteListPrev()
    {
        if (_nowPlaying.Shuffle || !RemoteListActive) return false;
        var leaving = _playingTrack!;
        for (int i = _remoteList!.IndexOf(leaving) - 1; i >= 0; i--)
        {
            if (!PlayTrack(_remoteList[i])) continue;   // (a song whose file went: the one before it)
            _queue.PlayNext(new[] { leaving });
            return true;
        }
        _nowPlaying.SeekFraction(0);
        return true;
    }

    /// <summary>The list in a random order; <paramref name="notFirst"/> (the song just heard) is never the first.</summary>
    private static List<Track> Shuffled(List<Track> list, Track? notFirst)
    {
        var s = new List<Track>(list);
        for (int i = s.Count - 1; i > 0; i--) { int j = Random.Shared.Next(i + 1); (s[i], s[j]) = (s[j], s[i]); }
        if (s.Count > 1 && ReferenceEquals(s[0], notFirst)) (s[0], s[^1]) = (s[^1], s[0]);
        return s;
    }

    // ---- what the remote reads ----

    /// <summary>The remote serves ONE library: the iPod's while one is loaded, else the PC's Local Music.</summary>
    string? IRemoteHost.LibraryName()
    {
        if (_db is not null)
            return _db.Master?.Name is { Length: > 0 } name ? name : _device?.Profile.ModelName ?? _device?.Profile.ModelNumber ?? "iPod";
        return _localTracks.Count > 0 || _settings.LocalMusicFolders.Count > 0 ? "Local Music" : null;
    }

    RemoteState IRemoteHost.State()
    {
        var t = _nowPlaying.NowTrack;
        var queue = _queue.Items;
        int qsig = queue.Count;
        foreach (var q in queue) qsig = HashCode.Combine(qsig, RuntimeHelpers.GetHashCode(q));
        double dur = t is null ? 0 : _nowPlaying.DurationSeconds > 0 ? _nowPlaying.DurationSeconds * 1000 : t.LengthMs;
        return new RemoteState
        {
            Track = t,
            Playing = t is not null && _nowPlaying.Playing,
            DurationMs = dur,
            PositionMs = t is null ? 0 : LyricsClock().TotalMilliseconds,   // what is HEARD (the lyrics views' clock), not the decoder's read head
            Volume = _nowPlaying.VolumeLevel,
            Shuffle = _nowPlaying.Shuffle,
            Repeat = _nowPlaying.Repeat,
            Queue = queue.Take(RemoteServer.QueueMax).ToArray(),
            QueueSig = qsig,
            LibrarySig = RemoteLibrarySig(),
            LyricsSig = t is null ? 0 : HashCode.Combine(RuntimeHelpers.GetHashCode(t), _lyricsRev, _settings.GetLyricSync(LyricsLookup.SongKey(t.Artist, t.DisplayTitle))),
            PathOf = RemotePathOf(),
        };
    }

    /// <summary>Moves with whatever the library reply holds: a reloaded iPod database (every edit reads it back), a new
    /// scan of the PC library, the PC playlists, the song list's sort.</summary>
    private int RemoteLibrarySig()
    {
        if (_db is not null) return HashCode.Combine(RuntimeHelpers.GetHashCode(_db), _sortCol, _sortAsc);
        int sig = HashCode.Combine(_localRev, _sortCol, _sortAsc);
        foreach (var lp in _settings.LocalPlaylists)
        {
            sig = HashCode.Combine(sig, RuntimeHelpers.GetHashCode(lp), lp.Name, lp.Paths.Count);
            foreach (var p in lp.Paths) sig = HashCode.Combine(sig, RuntimeHelpers.GetHashCode(p));
        }
        return sig;
    }

    /// <summary>Where a song's file is - for the server's cover reads, off the UI thread (it holds no window state).</summary>
    private Func<Track, string?> RemotePathOf()
    {
        string? mount = _device?.MountRoot;
        var over = PlayPathOverride;
        return t => over?.Invoke(t) ?? t.LocalPath ?? (mount is null ? null : t.ResolveFilePath(mount));
    }

    RemoteLibrary IRemoteHost.Library()
    {
        var lib = new RemoteLibrary { Sig = RemoteLibrarySig(), PathOf = RemotePathOf() };
        if (_db is not null)
        {
            lib.Songs = _db.Tracks.Where(t => MediaType.IsAudio(t.MediaType)).ToList();
            SortTracks(lib.Songs);   // the order the song list has them in
            var master = _db.Master;
            for (int i = 0; i < _shownPlaylists.Count; i++)   // the sidebar's playlists, the master left out
            {
                var pl = _shownPlaylists[i];
                if (ReferenceEquals(pl, master)) continue;
                var songs = new List<Track>(pl.TrackIds.Count);
                foreach (uint id in pl.TrackIds) if (_db.FindByUniqueId(id) is { } t && MediaType.IsAudio(t.MediaType)) songs.Add(t);
                lib.Playlists.Add((pl.PersistentId != 0 ? "pl:" + pl.PersistentId.ToString("X16") : "pl#" + i,
                    pl.Name.Length == 0 ? Loc.T("Untitled playlist") : pl.Name, songs));
            }
        }
        else
        {
            lib.Songs = _localTracks.Where(t => MediaType.IsAudio(t.MediaType)).ToList();
            SortTracks(lib.Songs);
            var byPath = RemoteByPath();
            for (int i = 0; i < _settings.LocalPlaylists.Count; i++)   // ON THIS PC's playlists: the songs the scan has
            {
                var lp = _settings.LocalPlaylists[i];
                var songs = new List<Track>(lp.Paths.Count);
                foreach (var p in lp.Paths) if (byPath.TryGetValue(p, out var t)) songs.Add(t);
                lib.Playlists.Add(("pc:" + i, lp.Name.Length == 0 ? Loc.T("Untitled") : lp.Name, songs));
            }
        }
        return lib;
    }

    RemoteLyrics? IRemoteHost.Lyrics()
    {
        var t = _nowPlaying.NowTrack;
        if (t is null) return null;
        return new RemoteLyrics
        {
            TrackId = RemoteServer.IdOf(t), Artist = t.Artist, Title = t.DisplayTitle, Duration = LyricsDuration(t),
            Path = ResolvePlayPath(t), OffsetMs = _settings.GetLyricSync(LyricsLookup.SongKey(t.Artist, t.DisplayTitle)),
        };
    }

    // ---- the buttons it presses ----

    string? IRemoteHost.Run(RemoteCommand c)
    {
        switch (c.Name)
        {
            case "play": case "pause": case "toggle":
                if (_nowPlaying.NowTrack is null) return "nothing is loaded - playSongs starts something";   // the play button does nothing then either
                if (c.Name == "toggle" || _nowPlaying.Playing == (c.Name == "pause")) _nowPlaying.TogglePlayback();
                return null;
            case "next":
                if (_playingTrack is null) return "nothing is playing";
                PlayRelative(+1);
                return null;
            case "prev":
                if (_playingTrack is null) return "nothing is playing";
                if (_nowPlaying.PositionSeconds > 3) _nowPlaying.SeekFraction(0);   // the protocol's iPod rule: well into a song, Prev starts it over
                else PlayRelative(-1);
                return null;
            case "seek":
            {
                double dur = _nowPlaying.DurationSeconds;
                if (_nowPlaying.NowTrack is null || dur <= 0) return "nothing is playing";
                _nowPlaying.SeekFraction(c.Ms / 1000.0 / dur);
                return null;
            }
            case "volume":
                _nowPlaying.SetVolumeLevel(Math.Clamp(c.Volume, 0, 1));   // 0 mutes, as the slider's end does
                return null;
            case "shuffle":
                if (_nowPlaying.Shuffle != c.On) _nowPlaying.ToggleShuffle();
                return null;
            case "repeat":
                _nowPlaying.SetRepeat(c.Mode);
                return null;
            case "playSongs":
                return RemotePlaySongs(c.Ids, c.Start);
            case "queueAdd": case "queueNext":
            {
                var songs = RemoteTracks(c.Ids);
                if (songs.Count == 0) return "no such songs";
                if (c.Name == "queueAdd") _queue.Add(songs); else _queue.PlayNext(songs);   // (a song already queued stays where it is)
                // lined up from VR while a list from VR plays: they join that list, so it ends (or goes round) after them too
                if (RemoteListActive) foreach (var t in songs) if (!_remoteList!.Contains(t)) _remoteList.Add(t);
                return null;
            }
            case "queueRemove":
                if (c.Index < 0 || c.Index >= _queue.Count) return "no such queue entry";
                _queue.Remove(_queue.Items[c.Index]);
                return null;
            case "queueMove":
                if (c.From < 0 || c.From >= _queue.Count || c.To < 0 || c.To >= _queue.Count) return "no such queue entry";
                _queue.Move(c.From, c.To > c.From ? c.To + 1 : c.To);   // "to" = where it ends up; the panel's Move takes the gap it is dropped into
                return null;
            case "queueClear":
                _queue.Clear();
                return null;
            case "queueJump":
                if (c.Index < 0 || c.Index >= _queue.Count) return "no such queue entry";
                JumpToQueued(_queue.Items[c.Index]);
                return null;
            case "rate":
                return RemoteRate(c.Stars);
        }
        return "unknown command: " + c.Name;
    }

    /// <summary>playSongs: VR plays an album, a playlist or its song list from one song on. That song plays now and Up Next
    /// becomes the rest of the list: the songs after it, in order - or, with Shuffle on, all the others in a random order
    /// (the watch's own "Shuffle" turns Shuffle on and starts from a random song; Up Next never reshuffles by itself in
    /// Mixtape, so this is where the shuffle happens). The list is kept for what follows its end (<see cref="_remoteList"/>).</summary>
    private string? RemotePlaySongs(List<string> ids, int start)
    {
        if (start < 0 || start >= ids.Count) return "start is outside the list";
        var list = new List<Track>(ids.Count);
        int at = -1;
        for (int i = 0; i < ids.Count; i++)
        {
            var t = RemoteTrack(ids[i]);
            if (t is null || !MediaType.IsAudio(t.MediaType)) { if (i == start) return "no such song: " + ids[i]; continue; }
            if (i == start) at = list.Count;
            list.Add(t);
        }
        var first = list[at];
        if (!CanPlay(first)) return "the song's file can't be found";
        _remoteList = list;
        _queue.Clear();
        _queue.Add(_nowPlaying.Shuffle ? Shuffled(list.Where((_, i) => i != at).ToList(), null) : list.Skip(at + 1));
        PlayFromNav(first);
        return null;
    }

    private List<Track> RemoteTracks(List<string> ids)
    {
        var found = new List<Track>(ids.Count);
        foreach (var id in ids) if (RemoteTrack(id) is { } t && MediaType.IsAudio(t.MediaType)) found.Add(t);
        return found;
    }

    /// <summary>A song by its API id, from the loaded iPod library or the PC's (see <see cref="RemoteServer.IdOf"/>).</summary>
    private Track? RemoteTrack(string id)
    {
        if (id.StartsWith("file:", StringComparison.Ordinal)) return RemoteByPath().GetValueOrDefault(id[5..]);
        if (_db is null || !ulong.TryParse(id, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out ulong dbid)) return null;
        if (!ReferenceEquals(_remoteByDbidOf, _db) || _remoteByDbid is null)
        {
            _remoteByDbid = new Dictionary<ulong, Track>(_db.Tracks.Count);
            foreach (var t in _db.Tracks) _remoteByDbid.TryAdd(t.Dbid, t);
            _remoteByDbidOf = _db;
        }
        return _remoteByDbid.GetValueOrDefault(dbid);
    }

    private Dictionary<string, Track> RemoteByPath()
    {
        if (_remoteByPath is null || _remoteByPathOf != _localRev)
        {
            _remoteByPath = new Dictionary<string, Track>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in _localTracks) if (t.LocalPath is { } p) _remoteByPath.TryAdd(p, t);
            _remoteByPathOf = _localRev;
        }
        return _remoteByPath;
    }

    /// <summary>The deck's stars, from VR: the same write to the iPod - but never behind a question nobody in VR can see.
    /// While Mixtape still has to ask before its first write of the session, or a write or a dialog is open on the
    /// desktop, the rating is refused with a reason instead.</summary>
    private string? RemoteRate(int stars)
    {
        if (_playingTrack is not Track t || _nowPlaying.NowTrack is null) return "nothing is playing";
        if (t.LocalPath is not null || !CanEditRatings || !MediaType.IsAudio(t.MediaType)) return "this song can't be rated (a PC file, or a read-only iPod)";
        if (_bgWriteRunning || !IsWindowEnabled(Handle)) return "Mixtape is busy on the desktop - try again in a moment";
        if (_settings.ConfirmWrites && !_writeConfirmed) return "Mixtape asks before its first change to the iPod each session - rate a song in Mixtape once first";
        RatePlayingTrack(stars);
        return null;
    }
}
