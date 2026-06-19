namespace iPodCommander;

/// <summary>
/// A session-only "Up Next" play queue. Holds live <see cref="Track"/> references (identity-based, matching
/// <c>_navHistory</c>/<c>RowIndexOf</c>); the file path is always re-resolved at play time. The queue takes
/// priority over shuffle/sequential selection. It is cleared when the visible track instances stop being
/// valid (view/playlist change, stop, device eject). <see cref="Changed"/> fires on every mutation so the
/// Up-Next panel can re-render. <see cref="JumpedToFront"/> fires only when an item is inserted at the very
/// front (a "Play next"), so the host can invalidate an already-committed gapless prefetch.
/// </summary>
internal sealed class PlayQueue
{
    private readonly List<Track> _items = new();
    public event Action? Changed;        // any mutation → panel re-renders
    public event Action? JumpedToFront;   // a new head was inserted → re-evaluate the pending gapless prefetch

    public IReadOnlyList<Track> Items => _items;
    public int Count => _items.Count;
    public bool IsEmpty => _items.Count == 0;

    public Track? Peek() => _items.Count > 0 ? _items[0] : null;

    public Track? Dequeue()
    {
        if (_items.Count == 0) return null;
        var t = _items[0]; _items.RemoveAt(0); Changed?.Invoke(); return t;
    }

    /// <summary>"Play next": insert at the front (in selection order), ahead of anything already queued.</summary>
    public void PlayNext(IEnumerable<Track> tracks)
    {
        int i = 0; bool any = false;
        foreach (var t in tracks) if (!Contains(t)) { _items.Insert(i++, t); any = true; }
        if (any) { Changed?.Invoke(); JumpedToFront?.Invoke(); }
    }

    /// <summary>"Add to queue": append, skipping ones already present.</summary>
    public void Add(IEnumerable<Track> tracks)
    {
        bool any = false, wasEmpty = _items.Count == 0;
        foreach (var t in tracks) if (!Contains(t)) { _items.Add(t); any = true; }
        if (any) { Changed?.Invoke(); if (wasEmpty) JumpedToFront?.Invoke(); }   // first item added is also the new head
    }

    public void Remove(Track t)
    {
        int idx = IndexOf(t);
        if (idx < 0) return;
        _items.RemoveAt(idx);
        Changed?.Invoke();
        if (idx == 0) JumpedToFront?.Invoke();   // removing the head changes what plays next
    }

    public void Move(int from, int to)
    {
        if (from < 0 || from >= _items.Count) return;
        var t = _items[from]; _items.RemoveAt(from);
        if (to > from) to--;
        to = Math.Clamp(to, 0, _items.Count);
        _items.Insert(to, t);
        Changed?.Invoke();
        if (from == 0 || to == 0) JumpedToFront?.Invoke();
    }

    public void Clear() { if (_items.Count > 0) { _items.Clear(); Changed?.Invoke(); } }

    public bool Contains(Track t) => IndexOf(t) >= 0;
    private int IndexOf(Track t) { for (int i = 0; i < _items.Count; i++) if (ReferenceEquals(_items[i], t)) return i; return -1; }
}
