using System.Drawing;

namespace iPodCommander;

/// <summary>
/// Writes album cover art to one iPod's <c>iPod_Control/Artwork</c> — the <c>ArtworkDB</c> plus the
/// <c>F&lt;fmt&gt;_n.ithmb</c> pixel files. Mirrors <see cref="PhotoLibrary"/>: the (small) database is
/// parsed in full and rebuilt with every existing image preserved verbatim, while new thumbnails are
/// APPENDED to the end of the .ithmb files so existing covers' bytes and offsets are never touched.
///
/// Implements <see cref="IArtworkSink"/> so <see cref="IpodLibrary"/> can stage art during a normal
/// add and flush it on save. Writing is blocked if the existing ArtworkDB couldn't be fully parsed,
/// so a partial read can never overwrite a good database.
/// </summary>
internal sealed class ArtworkLibrary : IArtworkSink
{
    private const long IthmbMaxSize = 256L * 1000L * 1000L; // libgpod ITHUMB_MAX_SIZE
    private const uint FirstArtworkId = 64;

    public IPodDevice Device { get; }
    public ArtworkDbModel Model { get; private set; }
    public bool SafeToWrite { get; private set; } = true;

    private string ArtworkDir => Path.Combine(
        Path.GetDirectoryName(Path.GetDirectoryName(Device.ITunesDbPath)!)!, "Artwork"); // …/iPod_Control/Artwork
    private string DbPath => Path.Combine(ArtworkDir, "ArtworkDB");

    private PhotoFormat[] _addFormats = Array.Empty<PhotoFormat>();
    private bool _formatsFromDevice;   // true when _addFormats were copied from the device's OWN ArtworkDB
    private uint _nextId;

    /// <summary>Bypass the generation capability gate (set by the "rebuild artwork" tool once the device's
    /// real format has been confirmed). Writing still requires <see cref="SafeToWrite"/> and resolved formats.</summary>
    public bool Force { get; set; }

    private bool _clean;   // clean-rebuild mode: drop all existing images and rewrite the .ithmb files from scratch

    /// <summary>Begin a clean rebuild: forget every existing image (orphans + any prior writes) so the
    /// committed ArtworkDB + .ithmb contain ONLY the art staged from here on. Pairs with
    /// <see cref="RawDb.ClearAllTrackArtwork"/> on the iTunesDB side.</summary>
    public void StartCleanRebuild()
    {
        Model.Items.Clear();
        Model.MaxId = 0;
        _nextId = FirstArtworkId;
        _clean = true;
    }

    private ArtworkLibrary(IPodDevice device, ArtworkDbModel model) { Device = device; Model = model; }

    // We can safely add art when we know valid thumbnail formats AND it's a colour-art device. "Colour-art"
    // is proven either by the detected generation OR by the device's own existing ArtworkDB (formats copied
    // from it) — the latter rescues Windows-managed iPods that detect as an Unknown generation. Force skips
    // the capability check entirely (the format has been confirmed out-of-band).
    public bool SupportsArtwork => SafeToWrite && _addFormats.Length > 0
        && (Force || Device.Profile.SupportsArtwork || _formatsFromDevice);

    /// <summary>The thumbnail formats new art would be written in (diagnostics).</summary>
    public PhotoFormat[] AddFormats => _addFormats;

    /// <summary>True when <see cref="AddFormats"/> were copied from the device's existing ArtworkDB.</summary>
    public bool FormatsFromDevice => _formatsFromDevice;

    public static ArtworkLibrary Load(IPodDevice device)
    {
        var lib = new ArtworkLibrary(device, new ArtworkDbModel());
        try
        {
            if (File.Exists(lib.DbPath))
            {
                lib.Model = ArtworkDb.Parse(File.ReadAllBytes(lib.DbPath));
                if (lib.Model.Warnings.Count > 0) lib.SafeToWrite = false; // don't risk overwriting a DB we couldn't fully read
            }
        }
        catch { lib.SafeToWrite = false; }
        lib.DetermineAddFormats();
        lib._nextId = Math.Max(FirstArtworkId, lib.Model.MaxId + 1);
        return lib;
    }

    /// <summary>Match the formats the device already uses (so new art looks like iTunes'); else the per-generation defaults.</summary>
    private void DetermineAddFormats()
    {
        var fromDevice = Model.Items.SelectMany(it => it.Thumbs)
            .Select(t => t.FormatId).Distinct()
            .Select(ArtworkFormats.Lookup).Where(f => f is not null).Select(f => f!)
            .GroupBy(f => f.FormatId).Select(g => g.First()).ToArray();
        _formatsFromDevice = fromDevice.Length > 0;
        _addFormats = _formatsFromDevice ? fromDevice : ArtworkFormats.For(Device.Profile.Generation);
    }

    // ---- IArtworkSink ----

    public (uint MhiiId, uint Size)? Stage(ulong trackDbid, string sourcePath)
    {
        if (!SupportsArtwork) return null;
        byte[]? bytes;
        try { bytes = MetadataExtractor.ReadArt(sourcePath); } catch { bytes = null; }
        if (bytes is null || bytes.Length == 0) return null;

        Image src;
        try { using var ms = new MemoryStream(bytes); src = Image.FromStream(ms); }
        catch { return null; }

        try
        {
            var item = new ArtworkItem { Id = _nextId++, TrackDbid = trackDbid, SourceImageSize = (uint)bytes.Length };
            uint total = 0;
            foreach (var fmt in _addFormats)
            {
                var slot = Ithmb.Encode(src, fmt);
                item.Thumbs.Add(new ArtworkThumb
                {
                    FormatId = fmt.FormatId,
                    Width = fmt.Width,
                    Height = fmt.Height,
                    VPad = slot.VPad,
                    HPad = slot.HPad,
                    Size = slot.Pixels.Length,
                    Pixels = slot.Pixels,
                });
                total += (uint)slot.Pixels.Length;
            }
            Model.Items.Add(item);
            Model.MaxId = Math.Max(Model.MaxId, item.Id);
            return (item.Id, total);
        }
        finally { src.Dispose(); }
    }

    public void Commit()
    {
        if (!SafeToWrite) return;
        var newThumbs = Model.Items.Where(it => it.RawMhii is null).SelectMany(it => it.Thumbs).Where(t => t.Pixels.Length > 0).ToList();
        if (newThumbs.Count == 0) return;
        Directory.CreateDirectory(ArtworkDir);

        if (_clean)
        {
            // Clean rebuild: rewrite each format's F<fmt>_1.ithmb from scratch (no orphans, no bloat) and
            // delete any rolled-over higher-index files. All current items were staged this pass.
            foreach (var grp in newThumbs.GroupBy(t => t.FormatId))
            {
                foreach (string old in Directory.GetFiles(ArtworkDir, $"F{grp.Key}_*.ithmb"))
                    try { File.Delete(old); } catch { }
                using var fs = new FileStream(Path.Combine(ArtworkDir, $"F{grp.Key}_1.ithmb"), FileMode.Create, FileAccess.Write, FileShare.Read);
                long off = 0;
                foreach (var t in grp) { t.FileIndex = 1; t.Offset = (uint)off; fs.Write(t.Pixels, 0, t.Size); off += t.Size; }
                fs.Flush(true);
            }
            byte[] cdb = ArtworkDb.Build(Model);
            WriteDbSafely(cdb, Model.Items.Count);
            var rl = Load(Device);
            Model = rl.Model; SafeToWrite = rl.SafeToWrite; _addFormats = rl._addFormats; _nextId = rl._nextId; _clean = false;
            return;
        }

        // 1) Append each NEW thumbnail's pixels to its format's current .ithmb (rolling at 256 MB); set offsets.
        foreach (var grp in newThumbs.GroupBy(t => t.FormatId))
        {
            int idx = LatestFileIndex(grp.Key, out long curLen);
            FileStream? fs = null;
            try
            {
                foreach (var t in grp)
                {
                    if (curLen > 0 && curLen + t.Size > IthmbMaxSize) { fs?.Dispose(); fs = null; idx++; curLen = 0; }
                    if (fs is null)
                    {
                        fs = new FileStream(Path.Combine(ArtworkDir, $"F{grp.Key}_{idx}.ithmb"), FileMode.Append, FileAccess.Write, FileShare.Read);
                        curLen = fs.Length;
                    }
                    t.FileIndex = idx;
                    t.Offset = (uint)curLen;
                    fs.Write(t.Pixels, 0, t.Size);
                    curLen += t.Size;
                }
                fs?.Flush(true);
            }
            finally { fs?.Dispose(); }
        }

        // 2) Rebuild + safely write the (small) ArtworkDB; verify; then reload so staged items become "existing".
        byte[] db = ArtworkDb.Build(Model);
        WriteDbSafely(db, Model.Items.Count);
        var reloaded = Load(Device);
        Model = reloaded.Model; SafeToWrite = reloaded.SafeToWrite; _addFormats = reloaded._addFormats; _nextId = reloaded._nextId;
    }

    private int LatestFileIndex(int formatId, out long length)
    {
        int best = 0;
        try
        {
            foreach (string f in Directory.GetFiles(ArtworkDir, $"F{formatId}_*.ithmb"))
            {
                string name = Path.GetFileNameWithoutExtension(f);
                int us = name.LastIndexOf('_');
                if (us >= 0 && int.TryParse(name.AsSpan(us + 1), out int n) && n > best) best = n;
            }
        }
        catch { }
        if (best == 0) { length = 0; return 1; }
        try { length = new FileInfo(Path.Combine(ArtworkDir, $"F{formatId}_{best}.ithmb")).Length; }
        catch { length = 0; }
        return best;
    }

    private void WriteDbSafely(byte[] bytes, int expectedItems)
    {
        string original = DbPath + ".original", bak = DbPath + ".bak", tmp = DbPath + ".tmp";
        if (File.Exists(DbPath) && !File.Exists(original)) File.Copy(DbPath, original);
        if (File.Exists(DbPath)) File.Copy(DbPath, bak, overwrite: true);

        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None)) { fs.Write(bytes, 0, bytes.Length); fs.Flush(true); }
        try { if (File.Exists(DbPath)) File.Replace(tmp, DbPath, null); else File.Move(tmp, DbPath); }
        catch { File.Copy(tmp, DbPath, overwrite: true); try { File.Delete(tmp); } catch { } }

        try
        {
            var check = ArtworkDb.Parse(File.ReadAllBytes(DbPath));
            if (check.Items.Count != expectedItems)
                throw new InvalidDataException($"Verify failed: wrote {check.Items.Count} artwork items, expected {expectedItems}.");
            var sizes = new Dictionary<string, long>();
            foreach (var it in check.Items)
                foreach (var t in it.Thumbs)
                {
                    string f = Path.Combine(ArtworkDir, $"F{t.FormatId}_{t.FileIndex}.ithmb");
                    if (!sizes.TryGetValue(f, out long len)) sizes[f] = len = File.Exists(f) ? new FileInfo(f).Length : 0;
                    if (t.Offset + t.Size > len)
                        throw new InvalidDataException($"Verify failed: thumbnail F{t.FormatId}_{t.FileIndex} range {t.Offset}+{t.Size} exceeds {len} bytes.");
                }
        }
        catch
        {
            if (File.Exists(bak)) File.Copy(bak, DbPath, overwrite: true);
            throw;
        }
    }
}
