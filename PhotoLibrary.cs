using System.Drawing.Drawing2D;

namespace iPodCommander;

/// <summary>
/// Manages one iPod's photos at real-library scale. The Photo Database (≈1–2 MB) is parsed in full,
/// but the thumbnail pixels (the <c>.ithmb</c> files can be GIGABYTES) are NEVER read eagerly:
/// the grid decodes only the tiny browse thumbnail on demand (cached), and adding photos APPENDS
/// new pixels to the end of the existing <c>.ithmb</c> files — existing photos' bytes and offsets
/// are never touched. The database is rewritten with every existing photo's mhii preserved verbatim
/// (only added photos are synthesized), so iTunes' dates/ratings/flags survive bit-for-bit.
///
/// Photos carry no checksum, so this is independent of the iTunesDB hash scheme. Writing is blocked
/// if the existing database couldn't be fully parsed, so a partial read can never overwrite a good DB.
/// </summary>
internal sealed class PhotoLibrary
{
    private const long IthmbMaxSize = 256L * 1000L * 1000L; // libgpod ITHUMB_MAX_SIZE
    private const long CacheableIthmb = 48L * 1024 * 1024;  // cache .ithmb files up to 48 MB for display
    private const uint FirstImageId = 64;

    public IPodDevice Device { get; }
    public PhotoDbModel Model { get; private set; }
    public bool SafeToWrite { get; private set; } = true;
    public string? BlockReason { get; private set; }
    /// <summary>Bumped whenever the photo set changes (add/delete), so the UI can tell when its decoded grid is
    /// still valid and skip re-decoding the whole library on a mere revisit of the Photos view.</summary>
    public int Generation { get; private set; }

    private string PhotosDir => Path.Combine(Device.MountRoot, "Photos");
    private string ThumbsDir => Path.Combine(PhotosDir, "Thumbs");
    private string DbPath => Path.Combine(PhotosDir, "Photo Database");

    private PhotoFormat[] _addFormats = Array.Empty<PhotoFormat>();
    private uint _nextId;
    private readonly Dictionary<string, byte[]> _ithmbCache = new(); // small files only, for display

    private PhotoLibrary(IPodDevice device, PhotoDbModel model) { Device = device; Model = model; }

    public IReadOnlyList<Photo> Photos => Model.Photos;

    public static PhotoLibrary Load(IPodDevice device)
    {
        var lib = new PhotoLibrary(device, new PhotoDbModel());
        try
        {
            if (File.Exists(lib.DbPath))
            {
                lib.Model = PhotoDb.Parse(File.ReadAllBytes(lib.DbPath));
                if (lib.Model.Warnings.Count > 0)
                    lib.Block("The existing Photo Database could not be fully read; photo writing is disabled to protect it.");
            }
        }
        catch (Exception ex) { lib.Block("Could not read the Photo Database: " + ex.Message); }
        lib.DetermineAddFormats();
        return lib;
    }

    private void Block(string reason) { SafeToWrite = false; BlockReason = reason; }

    /// <summary>New photos are rendered in the RGB565 formats the device already uses (so they match
    /// iTunes exactly), falling back to the per-generation defaults when the device has no photos yet.</summary>
    private void DetermineAddFormats()
    {
        var fromDevice = Model.Photos.SelectMany(p => p.Thumbs)
            .Select(t => t.FormatId).Distinct()
            .Select(PhotoFormats.Lookup).Where(f => f is not null).Select(f => f!)
            .GroupBy(f => f.FormatId).Select(g => g.First()).ToArray();
        _addFormats = fromDevice.Length > 0 ? fromDevice : PhotoFormats.For(Device.Profile.Generation);
    }

    // ---- display ----

    /// <summary>
    /// Decode a photo's thumbnail (read on demand) to a Bitmap for the grid. Prefers a ~tile-sized
    /// RGB565 thumb (≈130px, crisp) over the tiny 50px browse thumb, but avoids the huge full-screen
    /// one so a big library stays fast and light.
    /// </summary>
    private const int GridThumbCap = 280;   // downscale big slots to this max edge — crisp at any grid size, bounded memory

    public Bitmap? RenderThumb(Photo photo)
    {
        // Decode the LARGEST RGB565 slot we understand (the ~320×240 full-screen image), not the tiny 130px
        // preview — so the grid stays crisp when the photo is zoomed to fill the tile. Capped + downscaled so a
        // big library doesn't balloon memory. (TV-out slots have SlotWidth 0 and are excluded.)
        var cands = photo.Thumbs.Where(x => x.SlotWidth > 0 && x.Size > 0).OrderBy(x => x.SlotWidth).ToList();
        if (cands.Count == 0) return null;
        var t = cands.LastOrDefault(x => x.SlotWidth <= 400) ?? cands[^1];
        byte[]? px = ReadSlot(t);
        if (px is null) return null;
        using var full = Ithmb.Decode(px, t.SlotWidth, t.SlotHeight);
        if (full is null) return null;

        int cw = t.ImageWidth - t.HPad, ch = t.ImageHeight - t.VPad; // crop the letterbox out
        bool crop = cw > 0 && ch > 0 && t.HPad + cw <= t.SlotWidth && t.VPad + ch <= t.SlotHeight && (t.HPad > 0 || t.VPad > 0);
        var srcRect = crop ? new Rectangle(t.HPad, t.VPad, cw, ch) : new Rectangle(0, 0, full.Width, full.Height);

        int longEdge = Math.Max(srcRect.Width, srcRect.Height);
        if (longEdge <= GridThumbCap)
            return crop ? full.Clone(srcRect, full.PixelFormat) : (Bitmap)full.Clone();

        // downscale the cropped region to the cap (high-quality), centred — keeps memory bounded yet crisp
        double sc = (double)GridThumbCap / longEdge;
        int dw = Math.Max(1, (int)Math.Round(srcRect.Width * sc)), dh = Math.Max(1, (int)Math.Round(srcRect.Height * sc));
        var scaled = new Bitmap(dw, dh);
        using (var gg = Graphics.FromImage(scaled))
        {
            gg.InterpolationMode = InterpolationMode.HighQualityBicubic;
            gg.PixelOffsetMode = PixelOffsetMode.HighQuality;
            gg.DrawImage(full, new Rectangle(0, 0, dw, dh), srcRect, GraphicsUnit.Pixel);
        }
        return scaled;
    }

    /// <summary>
    /// Decode the LARGEST RGB565 slot we understand (typically the 320×240 full-screen image) for an
    /// on-screen full-size preview. The iPod keeps no original photo — these pre-rendered slots are all
    /// there is — so this is the best quality available. UYVY/I420 TV-out slots (SlotWidth 0 = unknown)
    /// are skipped since we only decode RGB565.
    /// </summary>
    public Bitmap? RenderFull(Photo photo)
    {
        foreach (var t in photo.Thumbs.Where(x => x.SlotWidth > 0 && x.Size > 0).OrderByDescending(x => x.SlotWidth * x.SlotHeight))
        {
            byte[]? px = ReadSlot(t);
            if (px is null) continue;
            using var full = Ithmb.Decode(px, t.SlotWidth, t.SlotHeight);
            if (full is null) continue;
            int cw = t.ImageWidth - t.HPad, ch = t.ImageHeight - t.VPad;
            if (cw > 0 && ch > 0 && t.HPad + cw <= t.SlotWidth && t.VPad + ch <= t.SlotHeight && (t.HPad > 0 || t.VPad > 0))
                return full.Clone(new Rectangle(t.HPad, t.VPad, cw, ch), full.PixelFormat);
            return (Bitmap)full.Clone();
        }
        return null;
    }

    /// <summary>
    /// A content fingerprint for duplicate detection: a hash of the raw pixel bytes of a stable mid-size
    /// thumbnail slot (the same ~130px slot the grid shows), prefixed with the slot dimensions so two
    /// different formats can't collide on a short byte run. Two photos that are the SAME image have
    /// byte-identical thumbnail pixels, hence the same key. Returns null when no slot is decodable (e.g. a
    /// TV-out-only photo we can't read) so such photos are simply never flagged as duplicates. Reads on
    /// demand (the .ithmb cache is shared with the grid); safe to call from a background thread.
    /// </summary>
    public string? ContentKey(Photo photo)
    {
        var cands = photo.Thumbs.Where(x => x.SlotWidth > 0 && x.Size > 0).OrderBy(x => x.SlotWidth).ToList();
        if (cands.Count == 0) return null;
        var t = cands.FirstOrDefault(x => x.SlotWidth is >= 110 and <= 200) ?? cands[0];
        // A NOT-yet-saved photo (Add photos, no Save) has its pixels only in memory; its Offset is still 0, so
        // ReadSlot would read SOME OTHER photo's bytes from the .ithmb and mis-key it (the dedup could then drop a
        // freshly-added photo). Prefer the in-memory pixels when present; fall back to the on-disk slot.
        byte[]? px = t.Pixels.Length > 0 ? t.Pixels : ReadSlot(t);
        if (px is null || px.Length == 0) return null;
        byte[] hash = System.Security.Cryptography.SHA1.HashData(px);
        return $"{t.SlotWidth}x{t.SlotHeight}:{Convert.ToHexString(hash)}";
    }

    private byte[]? ReadSlot(PhotoThumb t)
    {
        try
        {
            string path = Path.Combine(ThumbsDir, t.IthmbFileName);
            if (!File.Exists(path)) return null;
            byte[]? cached;
            lock (_ithmbCache) _ithmbCache.TryGetValue(path, out cached); // ReadSlot runs on a background thread
            if (cached is not null) return Slice(cached, t.Offset, t.Size);

            long len = new FileInfo(path).Length;
            if (t.Offset < 0 || t.Offset + t.Size > len) return null;
            if (len <= CacheableIthmb)
            {
                var all = File.ReadAllBytes(path);
                lock (_ithmbCache) _ithmbCache[path] = all;
                return Slice(all, t.Offset, t.Size);
            }
            // Large file (e.g. the 720×480 TV-out slabs): seek-read just this slot.
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            fs.Seek(t.Offset, SeekOrigin.Begin);
            var buf = new byte[t.Size];
            int read = 0; while (read < t.Size) { int n = fs.Read(buf, read, t.Size - read); if (n <= 0) break; read += n; }
            return read == t.Size ? buf : null;
        }
        catch { return null; }
    }

    private static byte[]? Slice(byte[] src, long offset, int size)
    {
        if (offset < 0 || offset + size > src.Length) return null;
        var r = new byte[size];
        Buffer.BlockCopy(src, (int)offset, r, 0, size);
        return r;
    }

    // ---- edits ----

    /// <summary>Render and stage one image from disk (does not write — batch, then <see cref="Save"/>).</summary>
    public void AddPhoto(string imagePath)
    {
        using var src = Image.FromFile(imagePath);
        AddPhoto(src, new FileInfo(imagePath).Length);
    }

    /// <summary>Render and stage one in-memory image (e.g. a generated wallpaper) — same path as a disk add.</summary>
    public void AddPhoto(Image src, long origSize)
    {
        if (_nextId == 0) _nextId = Math.Max(FirstImageId, (Model.Photos.Count > 0 ? Model.Photos.Max(p => p.ImageId) : 0) + 1);
        var photo = new Photo { ImageId = _nextId++, Date = DateTime.UtcNow, OrigImageSize = (uint)Math.Min(origSize, uint.MaxValue) }; // RawMhii null ⇒ new
        foreach (var fmt in _addFormats)
        {
            var slot = Ithmb.Encode(src, fmt);
            photo.Thumbs.Add(new PhotoThumb
            {
                FormatId = fmt.FormatId,
                SlotWidth = fmt.Width,
                SlotHeight = fmt.Height,
                ImageWidth = slot.ImageWidth,
                ImageHeight = slot.ImageHeight,
                HPad = slot.HPad,
                VPad = slot.VPad,
                Size = slot.Pixels.Length,
                Pixels = slot.Pixels,
            });
        }
        Model.Photos.Add(photo);
        Model.MaxImageId = Math.Max(Model.MaxImageId, photo.ImageId);
        foreach (var a in Model.Albums) if (a.IsMaster) a.ImageIds.Add(photo.ImageId);
        Generation++;
    }

    public void DeletePhotos(IEnumerable<uint> imageIds)
    {
        var set = new HashSet<uint>(imageIds);
        int countBefore = Model.Photos.Count;
        Model.Photos.RemoveAll(p => set.Contains(p.ImageId));
        if (Model.Photos.Count != countBefore) Generation++;
        foreach (var a in Model.Albums)
        {
            int before = a.ImageIds.Count;
            a.ImageIds.RemoveAll(set.Contains);
            if (a.ImageIds.Count != before) a.RawMhba = null; // rebuild this album so it drops the removed refs
        }
        // Deleted photos' pixels stay in the .ithmb as harmless dead space; remaining offsets are unchanged.
    }

    /// <summary>Append any new photos' pixels to the .ithmb files and rewrite the database. Throws on failure (DB + in-memory model restored).</summary>
    public void Save()
    {
        if (!SafeToWrite) throw new InvalidOperationException(BlockReason ?? "Photo writing is disabled.");
        Directory.CreateDirectory(ThumbsDir);
        try { SaveCore(); }
        catch
        {
            // The on-disk DB was already restored by WriteDbSafely; resync the in-memory model from
            // disk too, so the UI never shows photos that aren't actually on the device.
            try { var r = Load(Device); Model = r.Model; SafeToWrite = r.SafeToWrite; BlockReason = r.BlockReason; _addFormats = r._addFormats; } catch { }
            lock (_ithmbCache) _ithmbCache.Clear();
            _nextId = 0;
            throw;
        }
    }

    private void SaveCore()
    {
        // 1) Append each NEW thumbnail's pixels to the end of its format's current .ithmb file
        //    (rolling at 256 MB). Existing photos' files are never touched.
        var newByFormat = Model.Photos.Where(p => p.IsNew)
            .SelectMany(p => p.Thumbs).Where(t => t.Pixels.Length > 0)
            .GroupBy(t => t.FormatId);
        foreach (var grp in newByFormat)
        {
            int idx = LatestFileIndex(grp.Key, out long curLen);
            FileStream? fs = null;
            try
            {
                foreach (var t in grp)
                {
                    t.Size = t.Pixels.Length;
                    if (curLen > 0 && curLen + t.Size > IthmbMaxSize) { fs?.Dispose(); fs = null; idx++; curLen = 0; }
                    if (fs is null)
                    {
                        string path = Path.Combine(ThumbsDir, $"F{grp.Key}_{idx}.ithmb");
                        // FileShare.Read so a brief background thumbnail read can't cause a sharing violation here.
                        fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
                        curLen = fs.Length;
                    }
                    t.FileIndex = idx;
                    t.Offset = curLen;
                    fs.Write(t.Pixels, 0, t.Size);
                    curLen += t.Size;
                }
                fs?.Flush(true);
            }
            finally { fs?.Dispose(); }
        }

        // 2) Rebuild the (small) database — existing photos verbatim, new photos synthesized — and write it safely.
        byte[] db = PhotoDb.Build(Model);
        WriteDbSafely(db, Model.Photos.Count);

        // 3) Reload from the freshly-written DB so the model is canonical (new photos become "existing").
        var reloaded = Load(Device);
        Model = reloaded.Model;
        SafeToWrite = reloaded.SafeToWrite;
        BlockReason = reloaded.BlockReason;
        _addFormats = reloaded._addFormats;
        lock (_ithmbCache) _ithmbCache.Clear();
        _nextId = 0;
    }

    /// <summary>The highest existing F&lt;fmt&gt;_n.ithmb index and that file's current length (for appending).</summary>
    private int LatestFileIndex(int formatId, out long length)
    {
        int best = 0;
        try
        {
            foreach (string f in Directory.GetFiles(ThumbsDir, $"F{formatId}_*.ithmb"))
            {
                string name = Path.GetFileNameWithoutExtension(f);
                int us = name.LastIndexOf('_');
                if (us >= 0 && int.TryParse(name.AsSpan(us + 1), out int n) && n > best) best = n;
            }
        }
        catch { }
        if (best == 0) { length = 0; return 1; } // no file yet → start at _1
        try { length = new FileInfo(Path.Combine(ThumbsDir, $"F{formatId}_{best}.ithmb")).Length; }
        catch { length = 0; }
        return best;
    }

    private void WriteDbSafely(byte[] bytes, int expectedPhotos)
    {
        string original = DbPath + ".original";
        string bak = DbPath + ".bak";
        string tmp = DbPath + ".tmp";
        if (File.Exists(DbPath) && !File.Exists(original)) File.Copy(DbPath, original);
        if (File.Exists(DbPath)) File.Copy(DbPath, bak, overwrite: true);

        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.Write(bytes, 0, bytes.Length);
            fs.Flush(flushToDisk: true);
        }
        ReplaceFile(tmp, DbPath);

        try
        {
            var check = PhotoDb.Parse(File.ReadAllBytes(DbPath));
            if (check.Photos.Count != expectedPhotos)
                throw new InvalidDataException($"Verify failed: wrote {check.Photos.Count} photos, expected {expectedPhotos}.");
            // Every thumbnail the new DB references must fit inside its .ithmb file.
            var sizes = new Dictionary<string, long>();
            foreach (var photo in check.Photos)
                foreach (var t in photo.Thumbs)
                {
                    string f = Path.Combine(ThumbsDir, t.IthmbFileName);
                    if (!sizes.TryGetValue(f, out long len)) sizes[f] = len = File.Exists(f) ? new FileInfo(f).Length : 0;
                    if (t.Offset < 0 || t.Size <= 0 || t.Offset + t.Size > len)
                        throw new InvalidDataException($"Verify failed: thumbnail {t.IthmbFileName} range {t.Offset}+{t.Size} exceeds {len} bytes.");
                }
        }
        catch
        {
            if (File.Exists(bak)) File.Copy(bak, DbPath, overwrite: true);
            throw;
        }
    }

    private static void ReplaceFile(string tmp, string dst)
    {
        try { if (File.Exists(dst)) File.Replace(tmp, dst, null); else File.Move(tmp, dst); }
        catch
        {
            File.Copy(tmp, dst, overwrite: true);
            try { File.Delete(tmp); } catch { }
        }
    }
}
