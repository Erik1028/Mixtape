using System.Text;

namespace iPodCommander;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // Headless self-test: build a synthetic iTunesDB, parse it back, assert. Lets us
        // validate the chunk walker with no device attached. Output: ipod-test.txt next to exe.
        if (args.Contains("--test")) { RunSelfTest(); return; }

        // Read a REAL device's iTunesDB and dump it to ipod-dump.txt. Usage:
        //   iPodCommander.exe --dump E:\        (or any folder containing iPod_Control)
        if (args.Length >= 2 && args[0] == "--dump") { RunDump(args[1]); return; }

        // Dump the low-level chunk STRUCTURE (datasets, header lengths) → ipod-inspect.txt.
        // This is the ground truth the Milestone-2 writer must reproduce byte-for-byte.
        if (args.Length >= 2 && args[0] == "--inspect") { RunInspect(args[1]); return; }

        // Prove the byte-preserving writer: parse an iTunesDB (file path or device root) and
        // re-serialize with NO edits; the output must be byte-identical. → ipod-roundtrip.txt
        if (args.Length >= 2 && args[0] == "--roundtrip") { RunRoundtrip(args[1]); return; }

        // Prove add/delete are correct & perfectly inverse on a real DB. → ipod-mutatetest.txt
        if (args.Length >= 2 && args[0] == "--mutatetest") { RunMutateTest(args[1]); return; }

        // Full offline end-to-end: build a sandbox iPod from a fixture, copy a real audio file
        // in, save, verify, then delete. Usage: --addtest <sourceAudio> <fixtureDb> → ipod-addtest.txt
        if (args.Length >= 3 && args[0] == "--addtest") { RunAddTest(args[1], args[2]); return; }

        // Controlled real-device write helpers (used for the cautious first test). They go
        // through the exact same IpodLibrary/SafeDbWriter path the Add/Delete buttons use.
        //   --add <root> <audioFile>      → ipod-add.txt
        //   --remove <root> <uniqueId>    → ipod-remove.txt
        if (args.Length >= 3 && args[0] == "--add") { RunAdd(args[1], args[2]); return; }
        if (args.Length >= 3 && args[0] == "--remove") { RunRemove(args[1], args[2]); return; }

        // Headless UI render: paint the populated window (from a fixture DB) into a PNG so the
        // design can be reviewed without a live screenshot. Usage: --render <db|root> <out.png> [view]
        // view = songs (default) | videos | photos | settings.
        if (args.Length >= 3 && args[0] == "--render") { RunRender(args[1], args[2], args.Length >= 4 ? args[3] : "songs"); return; }

        // Regenerate the app icon (app.ico) from a source PNG: crop to the artwork's bounding box, round
        // the corners (transparent), and write a multi-resolution PNG-in-ICO. Usage:
        //   Mixtape.exe --makeicon <source.png> [out.ico]   (also writes icon-preview.png to eyeball)
        if (args.Length >= 2 && args[0] == "--makeicon") { RunMakeIcon(args[1], args.Length >= 3 ? args[2] : "app.ico"); return; }

        // Design-review the context menu without a device: build a representative right-click menu,
        // show it off-screen, highlight a row, and capture the (rounded) popup window. Usage:
        //   Mixtape.exe --menupreview <out.png>
        if (args.Length >= 2 && args[0] == "--menupreview") { RunMenuPreview(args[1]); return; }

        // Full cover-art chain diagnostic for a CONNECTED iPod: device detection → artwork capability →
        // existing ArtworkDB + .ithmb files → iTunesDB track flags → cross-check the track↔art links.
        // Pinpoints exactly where art sync breaks. Usage: Mixtape.exe --artworkdiag <iPod root, e.g. E:\>
        if (args.Length >= 2 && args[0] == "--artworkdiag") { RunArtworkDiag(args[1]); return; }

        // Render Now-Playing-bar layout OPTIONS (stacked, labelled) to a PNG for the user to choose.
        if (args.Length >= 2 && args[0] == "--npopts") { RunNowPlayingOptions(args[1]); return; }

        // Decode raw .ithmb slots to PNGs so we can eyeball whether the stored pixel format (RGB565-LE,
        // rotation) matches what we'd write. Usage: Mixtape.exe --ithmbslot <file.ithmb> <w> <h> <i0> <count> <outDir>
        if (args.Length >= 7 && args[0] == "--ithmbslot") { RunIthmbSlot(args[1], int.Parse(args[2]), int.Parse(args[3]), int.Parse(args[4]), int.Parse(args[5]), args[6]); return; }

        // Walk the ArtworkDB and print every mhii's child-mhod TYPES + mhni fields, so we can see exactly
        // how iTunes structures cover art vs how we write it. Usage: Mixtape.exe --artmhiidump <root> <count>
        if (args.Length >= 4 && args[0] == "--artmhiidump") { RunArtMhiiDump(args[1], int.Parse(args[2]), int.Parse(args[3])); return; }

        // Write cover art for songs ALREADY on the iPod (no re-copy): read each track's embedded art,
        // encode the device's thumbnail formats, link the mhit, and save. Optional 3rd arg limits how
        // many tracks to process (for a small first test). Usage: Mixtape.exe --rebuildart <root> [maxCount]
        if (args.Length >= 2 && args[0] == "--rebuildart") { RunRebuildArt(args[1], args.Length >= 3 ? int.Parse(args[2]) : int.MaxValue); return; }

        // Create N synthetic colourful photos and run the real PhotoLibrary add+save pipeline on a
        // device root. Offline integration test for the photo write path. Usage: --photodemo <root> [n]
        if (args.Length >= 2 && args[0] == "--photodemo") { RunPhotoDemo(args[1], args.Length >= 3 && int.TryParse(args[2], out int n) ? n : 8); return; }

        // Prove the playlist edits (delete keeping songs / remove-from / rename) on a real DB. → ipod-pltest.txt
        if (args.Length >= 2 && args[0] == "--pltest") { RunPlTest(args[1]); return; }

        // Dump the de-duped playlist list with master/podcast flags + the "addable" count. → ipod-pldump.txt
        if (args.Length >= 2 && args[0] == "--pldump") { RunPlDump(args[1]); return; }

        // Prove the Photo Database writer/reader round-trips + RGB565 thumbnail packing. → ipod-phototest.txt
        if (args.Contains("--phototest")) { RunPhotoTest(); return; }

        // Prove the binary-plist reader extracts FireWireGUID/DBVersion (hash58 devices). → ipod-bplisttest.txt
        if (args.Contains("--bplisttest")) { RunBplistTest(); return; }

        // Parse a REAL device's Photo Database and report photos/albums/formats. → ipod-photodump.txt
        if (args.Length >= 2 && args[0] == "--photodump") { RunPhotoDump(args[1]); return; }

        // Read-only safety check: Parse a real Photo Database → rebuild it → re-parse, and confirm
        // every existing photo's mhii is preserved byte-identical. Never writes to the device. → ipod-photort.txt
        if (args.Length >= 2 && args[0] == "--photoroundtrip") { RunPhotoRoundtrip(args[1]); return; }

        // hash58 known-answer test: recompute the signature over the device's existing DB and compare
        // to the stored one. Read-only. Tells you if writing is safe on a hash58 iPod. → ipod-verifysign.txt
        if (args.Length >= 2 && args[0] == "--verifysign") { RunVerifySign(args[1]); return; }

        // Diagnostic device report (same content as the device page's "Save report…" button) → ipod-devreport.txt
        if (args.Length >= 2 && args[0] == "--devreport") { RunDevReport(args[1]); return; }

        // Read the FireWire GUID straight off the device (SCSI INQUIRY 0xC0 + USB serial). Read-only. → ipod-readguid.txt
        if (args.Length >= 2 && args[0] == "--readguid") { RunReadGuid(args[1]); return; }

        // Offline ArtworkDB build/parse/round-trip + DBID-linkage self-test → ipod-artworktest.txt
        if (args.Length >= 1 && args[0] == "--artworktest") { RunArtworkTest(); return; }

        // End-to-end cover-art sync into a temp Nano3 iPod from a real audio file → ipod-artworksynctest.txt
        if (args.Length >= 2 && args[0] == "--artworksynctest") { RunArtworkSyncTest(args[1]); return; }

        // Diagnostic: how many tracks have ratings/play counts in the DB, and what the Play Counts file
        // (on-device deltas) holds. Read-only. Usage: --ratings <root|db> → ipod-ratings.txt
        if (args.Length >= 2 && args[0] == "--ratings") { RunRatingScan(args[1]); return; }

        // Spike: prove WPF MediaElement (hosted in an ElementHost under the WinForms loop) opens and
        // plays an audio/video file and reports its duration/position. Usage: --mediatest <file> → ipod-mediatest.txt
        if (args.Length >= 2 && args[0] == "--mediatest") { RunMediaTest(args[1]); return; }

        // Verify generation inference (FamilyID map + photo/artwork .ithmb fingerprint). → ipod-identifytest.txt
        if (args.Contains("--identifytest")) { RunIdentifyTest(); return; }

        // Verify recursive folder gather + batched photo save (the folder-import feature). → ipod-photofoldertest.txt
        if (args.Contains("--photofoldertest")) { RunPhotoFolderTest(); return; }

        // Verify exporting a track off the iPod (Artist/Album/NN Title + retag). → ipod-exporttest.txt
        if (args.Contains("--exporttest")) { RunExportTest(); return; }

        // Verify track metadata/rating editing is lossless + correct on a real DB. → ipod-edittest.txt
        if (args.Length >= 2 && args[0] == "--edittest") { RunEditTest(args[1]); return; }

        // Verify playlist track reordering on a real DB. → ipod-reordertest.txt
        if (args.Length >= 2 && args[0] == "--reordertest") { RunReorderTest(args[1]); return; }

        // GUI launch — enforce a single instance. Two copies racing the same iPod database swap
        // (or both writing %APPDATA%\Mixtape\settings.json) corrupt each other; a second launch
        // just surfaces the running window instead of starting a rival process.
        using var mutex = new System.Threading.Mutex(initiallyOwned: true, @"Local\MixtapeSingleInstance", out bool isFirst);
        if (!isFirst)
        {
            PostMessage(HWND_BROADCAST, ShowInstanceMessage, IntPtr.Zero, IntPtr.Zero);
            return;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.Run(new MainForm());
        GC.KeepAlive(mutex); // keep the handle (and thus the lock) alive for the app's lifetime
    }

    /// <summary>Broadcast window message a second launch posts so the running instance restores + activates itself.</summary>
    internal static readonly uint ShowInstanceMessage = RegisterWindowMessage("MixtapeShowExistingInstance");
    private static readonly IntPtr HWND_BROADCAST = (IntPtr)0xFFFF;

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
    private static extern uint RegisterWindowMessage(string msg);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private static readonly string[] TestImageExt = { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff", ".webp" };

    private static void RunReorderTest(string path)
    {
        var log = new StringBuilder();
        int pass = 0, fail = 0;
        void Check(string what, object? got, object? want)
        {
            bool ok = Equals(got?.ToString(), want?.ToString());
            log.AppendLine($"{(ok ? "PASS" : "FAIL")}  {what}: got {got}, want {want}");
            if (ok) pass++; else fail++;
        }
        try
        {
            byte[] orig = Directory.Exists(path) ? File.ReadAllBytes(DeviceDetector.Build(path)!.ITunesDbPath) : File.ReadAllBytes(path);
            var view = ITunesDbReader.Read(orig);
            var master = view.Master;
            var pl = view.Playlists.FirstOrDefault(p => !ReferenceEquals(p, master) && !p.IsPodcast && p.TrackIds.Count >= 3);
            Check("found a playlist with >=3 tracks", pl is not null, true);
            if (pl is not null)
            {
                var origOrder = pl.TrackIds.ToList();
                var reversed = Enumerable.Reverse(origOrder).ToList();

                // No-op: reorder to the SAME order must be byte-identical.
                var raw0 = RawDb.Parse(orig);
                raw0.ReorderPlaylist(pl.PersistentId, origOrder);
                Check("same-order reorder byte-identical", raw0.Serialize().SequenceEqual(orig), true);

                // Reverse it and re-read.
                var raw = RawDb.Parse(orig);
                Check("reorder applied", raw.ReorderPlaylist(pl.PersistentId, reversed), true);
                var v2 = ITunesDbReader.Read(raw.Serialize());
                var pl2 = v2.Playlists.First(p => p.PersistentId == pl.PersistentId && !p.IsMaster);
                Check("order is reversed", string.Join(",", pl2.TrackIds), string.Join(",", reversed));
                Check("same set of tracks", string.Join(",", pl2.TrackIds.OrderBy(x => x)), string.Join(",", origOrder.OrderBy(x => x)));
                Check("library track count unchanged", v2.Tracks.Count, view.Tracks.Count);

                var other = view.Playlists.FirstOrDefault(p => !ReferenceEquals(p, master) && p.PersistentId != pl.PersistentId && p.TrackIds.Count > 0);
                if (other is not null)
                {
                    var other2 = v2.Playlists.First(p => p.PersistentId == other.PersistentId && !p.IsMaster);
                    Check("a different playlist is untouched", string.Join(",", other2.TrackIds), string.Join(",", other.TrackIds));
                }
            }
        }
        catch (Exception ex) { log.AppendLine("EXCEPTION: " + ex); fail++; }
        log.AppendLine();
        log.AppendLine($"RESULT: {(fail == 0 ? "OK" : "FAIL")}  ({pass} passed, {fail} failed)");
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "ipod-reordertest.txt"), log.ToString());
    }

    private static void RunEditTest(string path)
    {
        var log = new StringBuilder();
        int pass = 0, fail = 0;
        void Check(string what, object? got, object? want)
        {
            bool ok = Equals(got?.ToString(), want?.ToString());
            log.AppendLine($"{(ok ? "PASS" : "FAIL")}  {what}: got {got}, want {want}");
            if (ok) pass++; else fail++;
        }
        try
        {
            byte[] orig = Directory.Exists(path) ? File.ReadAllBytes(DeviceDetector.Build(path)!.ITunesDbPath) : File.ReadAllBytes(path);
            var view = ITunesDbReader.Read(orig);
            Check("fixture has tracks", view.Tracks.Count > 1, true);

            // (1) A no-op edit (all fields null) on every track must reproduce the file byte-for-byte.
            var raw = RawDb.Parse(orig);
            foreach (var tr in view.Tracks) raw.EditTrack(tr.UniqueId, new TrackEdit());
            byte[] noop = raw.Serialize();
            Check("no-op edit is byte-identical", noop.SequenceEqual(orig), true);

            // (2) A real edit on the first track changes exactly those fields; others untouched.
            uint id = view.Tracks[0].UniqueId;
            uint otherId = view.Tracks[1].UniqueId;
            string? otherTitleBefore = view.Tracks.First(t => t.UniqueId == otherId).Title;
            var raw2 = RawDb.Parse(orig);
            bool found = raw2.EditTrack(id, new TrackEdit { Title = "EDITED TITLE", Artist = "EDITED ARTIST", Album = "EDITED ALBUM", Genre = "EDITED GENRE", Year = 2020, TrackNumber = 7, Rating = 100 });
            Check("EditTrack found the track", found, true);
            var v2 = ITunesDbReader.Read(raw2.Serialize());
            Check("track count unchanged", v2.Tracks.Count, view.Tracks.Count);
            var t2 = v2.FindByUniqueId(id)!;
            Check("title set", t2.Title, "EDITED TITLE");
            Check("artist set", t2.Artist, "EDITED ARTIST");
            Check("album set", t2.Album, "EDITED ALBUM");
            Check("genre set", t2.Genre, "EDITED GENRE");
            Check("year set", t2.Year, (uint)2020);
            Check("track# set", t2.TrackNumber, (uint)7);
            Check("rating set (5★=100)", t2.Rating, (byte)100);
            Check("a DIFFERENT track is untouched", v2.FindByUniqueId(otherId)!.Title, otherTitleBefore);

            // (3) Rating-only edit is in-place: the edited mhit keeps its exact byte length (no resize).
            var rawL = RawDb.Parse(orig);
            var trks = rawL.Datasets.First(d => d.Type == 1).Tracks!;
            int idx = trks.FindIndex(b => System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(0x10)) == id);
            int lenBefore = trks[idx].Length;
            rawL.EditTrack(id, new TrackEdit { Rating = 80 });
            Check("rating-only keeps mhit length", rawL.Datasets.First(d => d.Type == 1).Tracks![idx].Length, lenBefore);

            // (4) Clearing a tag removes it.
            var raw4 = RawDb.Parse(orig);
            raw4.EditTrack(id, new TrackEdit { Genre = "" });
            var t4 = ITunesDbReader.Read(raw4.Serialize()).FindByUniqueId(id)!;
            Check("cleared genre is empty", string.IsNullOrEmpty(t4.Genre), true);
        }
        catch (Exception ex) { log.AppendLine("EXCEPTION: " + ex); fail++; }

        log.AppendLine();
        log.AppendLine($"RESULT: {(fail == 0 ? "OK" : "FAIL")}  ({pass} passed, {fail} failed)");
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "ipod-edittest.txt"), log.ToString());
    }

    private static void RunExportTest()
    {
        var log = new StringBuilder();
        int pass = 0, fail = 0;
        void Check(string what, object? got, object? want)
        {
            bool ok = Equals(got?.ToString(), want?.ToString());
            log.AppendLine($"{(ok ? "PASS" : "FAIL")}  {what}: got {got}, want {want}");
            if (ok) pass++; else fail++;
        }
        try
        {
            string baseTmp = Path.Combine(Path.GetTempPath(), "mixtape-exporttest");
            try { if (Directory.Exists(baseTmp)) Directory.Delete(baseTmp, true); } catch { }
            string pod = Path.Combine(baseTmp, "pod"), dest = Path.Combine(baseTmp, "out");
            string music = Path.Combine(pod, "iPod_Control", "Music", "F00");
            Directory.CreateDirectory(music);
            Directory.CreateDirectory(dest);

            string sample = @"C:\Users\Erik\Music\M4A\A Moment Apart - ODESZA.m4a";
            Check("sample audio present", File.Exists(sample), true);
            if (File.Exists(sample))
            {
                File.Copy(sample, Path.Combine(music, "ipcm000001.m4a"), true);
                var t = new Track { Title = "A Moment Apart", Artist = "ODESZA", Album = "A Moment Apart", TrackNumber = 3, Year = 2017, Genre = "Electronic", Location = ":iPod_Control:Music:F00:ipcm000001.m4a" };
                string? outp = MusicExporter.ExportOne(t, pod, dest, organize: true, applyTags: true);
                Check("exported a file", outp is not null && File.Exists(outp), true);
                Check("organized Artist/Album", outp?.Replace('\\', '/').Contains("/ODESZA/A Moment Apart/"), true);
                Check("named 'NN Title'", outp is null ? null : Path.GetFileName(outp), "03 A Moment Apart.m4a");
                if (outp is not null)
                {
                    using var f = TagLib.File.Create(outp);
                    Check("retag title", f.Tag.Title, "A Moment Apart");
                    Check("retag artist", f.Tag.FirstPerformer, "ODESZA");
                    Check("retag track#", f.Tag.Track, (uint)3);
                }
            }
            var missing = new Track { Title = "x", Location = ":iPod_Control:Music:F00:nope.m4a" };
            Check("missing source → null", MusicExporter.ExportOne(missing, pod, dest, true, true), null);
        }
        catch (Exception ex) { log.AppendLine("EXCEPTION: " + ex); fail++; }

        log.AppendLine();
        log.AppendLine($"RESULT: {(fail == 0 ? "OK" : "FAIL")}  ({pass} passed, {fail} failed)");
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "ipod-exporttest.txt"), log.ToString());
    }

    private static void RunPhotoFolderTest()
    {
        var log = new StringBuilder();
        int pass = 0, fail = 0;
        void Check(string what, object? got, object? want)
        {
            bool ok = Equals(got?.ToString(), want?.ToString());
            log.AppendLine($"{(ok ? "PASS" : "FAIL")}  {what}: got {got}, want {want}");
            if (ok) pass++; else fail++;
        }
        try
        {
            string baseTmp = Path.Combine(Path.GetTempPath(), "mixtape-folderimport");
            try { if (Directory.Exists(baseTmp)) Directory.Delete(baseTmp, true); } catch { }
            string src = Path.Combine(baseTmp, "src"), pod = Path.Combine(baseTmp, "pod");
            Directory.CreateDirectory(Path.Combine(src, "a"));
            Directory.CreateDirectory(Path.Combine(src, "b", "c"));   // nested two levels deep
            Directory.CreateDirectory(Path.Combine(pod, "iPod_Control", "iTunes"));
            Directory.CreateDirectory(Path.Combine(pod, "Photos"));   // makes the sandbox photo-capable

            const int n = 7;
            MakeDemoImage(Path.Combine(src, "a", "0.png"), 0, n);
            MakeDemoImage(Path.Combine(src, "a", "1.png"), 1, n);
            MakeDemoImage(Path.Combine(src, "b", "2.png"), 2, n);
            MakeDemoImage(Path.Combine(src, "b", "c", "3.png"), 3, n);
            MakeDemoImage(Path.Combine(src, "b", "c", "4.png"), 4, n);
            MakeDemoImage(Path.Combine(src, "b", "c", "5.png"), 5, n);
            MakeDemoImage(Path.Combine(src, "b", "c", "6.png"), 6, n);
            File.WriteAllText(Path.Combine(src, "b", "notes.txt"), "not an image"); // must be excluded

            // 1) Recursive gather (mirrors MainForm.GatherFiles): finds images in every subfolder, skips non-images.
            var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
            var images = Directory.EnumerateFiles(src, "*", opts)
                .Where(f => TestImageExt.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();
            Check("recursive gather across subfolders", images.Length, n);

            // 2) Batched add (batch size 3, like the folder-import loop): multiple Save() calls must accumulate.
            var device = DeviceDetector.Build(pod);
            Check("sandbox supports photos", device?.Profile.SupportsPhotos, true);
            var lib = PhotoLibrary.Load(device!);
            int staged = 0;
            foreach (var img in images) { lib.AddPhoto(img); if (++staged >= 3) { lib.Save(); staged = 0; } }
            if (staged > 0) lib.Save();
            Check("count after batched saves", lib.Photos.Count, n);

            // 3) A fresh reload from disk sees all of them (durable, not just in-memory).
            var reload = PhotoLibrary.Load(device!);
            Check("count after fresh reload", reload.Photos.Count, n);
        }
        catch (Exception ex) { log.AppendLine("EXCEPTION: " + ex); fail++; }

        log.AppendLine();
        log.AppendLine($"RESULT: {(fail == 0 ? "OK" : "FAIL")}  ({pass} passed, {fail} failed)");
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "ipod-photofoldertest.txt"), log.ToString());
    }

    private static void RunPlDump(string path)
    {
        var log = new StringBuilder();
        try
        {
            byte[] bytes;
            if (Directory.Exists(path)) { var dev = DeviceDetector.Build(path); bytes = File.ReadAllBytes(dev!.ITunesDbPath); }
            else bytes = File.ReadAllBytes(path);
            var db = ITunesDbReader.Read(bytes);

            // De-dup exactly like MainForm.RebuildPlaylists.
            var seen = new HashSet<ulong>();
            var shown = new List<Playlist>();
            foreach (var pl in db.Playlists) { if (pl.PersistentId != 0 && !seen.Add(pl.PersistentId)) continue; shown.Add(pl); }
            var master = db.Master;

            log.AppendLine($"{db.Playlists.Count} raw playlists; {shown.Count} after de-dup:");
            foreach (var pl in shown)
                log.AppendLine($"  '{(pl.Name.Length == 0 ? "(untitled)" : pl.Name)}'  master={pl.IsMaster}  podcast={pl.IsPodcast}  tracks={pl.Count}  pid={pl.PersistentId:X}");
            int addable = shown.Count(p => !ReferenceEquals(p, master) && !p.IsPodcast);
            log.AppendLine();
            log.AppendLine($"ADDABLE in 'Add to playlist' (non-master, non-podcast): {addable}");
            log.AppendLine(addable > 0 ? "RESULT: OK — existing playlists are offered." : "RESULT: none addable (device may genuinely have no user playlists).");
        }
        catch (Exception ex) { log.AppendLine("ERROR: " + ex); }
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "ipod-pldump.txt"), log.ToString());
    }

    private static void RunIdentifyTest()
    {
        var log = new StringBuilder();
        int pass = 0, fail = 0;
        void Check(string what, object? got, object? want)
        {
            bool ok = Equals(got?.ToString(), want?.ToString());
            log.AppendLine($"{(ok ? "PASS" : "FAIL")}  {what}: got {got}, want {want}");
            if (ok) pass++; else fail++;
        }

        log.AppendLine("== photo/artwork .ithmb format-id fingerprint ==");
        Check("5G Video {1036,1015,1024,1019}", IpodIdentify.FromArtworkFingerprint(new HashSet<int> { 1036, 1015, 1024, 1019 })?.Gen, IPodGeneration.Video);
        Check("iPod photo {1009,1015}", IpodIdentify.FromArtworkFingerprint(new HashSet<int> { 1009, 1015 })?.Gen, IPodGeneration.Photo);
        Check("Classic/Nano3 {1067,1024,1066}", IpodIdentify.FromArtworkFingerprint(new HashSet<int> { 1067, 1024, 1066 })?.Gen, IPodGeneration.Classic1);
        Check("Nano4 {1083,1079,1024,1066}", IpodIdentify.FromArtworkFingerprint(new HashSet<int> { 1083, 1079, 1024, 1066 })?.Gen, IPodGeneration.Nano4);
        Check("Nano5 {1087,1079,1066}", IpodIdentify.FromArtworkFingerprint(new HashSet<int> { 1087, 1079, 1066 })?.Gen, IPodGeneration.Nano5);
        Check("Nano1/2 {1032,1023}", IpodIdentify.FromArtworkFingerprint(new HashSet<int> { 1032, 1023 })?.Gen, IPodGeneration.Nano1);
        Check("no fingerprint {9999}", IpodIdentify.FromArtworkFingerprint(new HashSet<int> { 9999 }), null);

        log.AppendLine();
        log.AppendLine("== SysInfoExtended FamilyID ==");
        Check("FamilyID 6 → Video", IpodIdentify.FromFamilyId(6, null)?.Gen, IPodGeneration.Video);
        Check("FamilyID 12 → Nano3", IpodIdentify.FromFamilyId(12, null)?.Gen, IPodGeneration.Nano3);
        Check("FamilyID 11 (+updater 38) → Classic3", IpodIdentify.FromFamilyId(11, 38)?.Gen, IPodGeneration.Classic3);
        Check("FamilyID 3 (+updater 7) → Mini2", IpodIdentify.FromFamilyId(3, 7)?.Gen, IPodGeneration.Mini2);
        Check("FamilyID 1 (unconfirmed) → null", IpodIdentify.FromFamilyId(1, null), null);
        Check("FamilyID 10000 (Touch) → null", IpodIdentify.FromFamilyId(10000, null), null);

        log.AppendLine();
        log.AppendLine("== ScanFormatIds from F<id>_<n>.ithmb filenames ==");
        string tmp = Path.Combine(Path.GetTempPath(), "ipodcmd-idtest", "Photos", "Thumbs");
        try
        {
            Directory.CreateDirectory(tmp);
            foreach (var fn in new[] { "F1036_1.ithmb", "F1015_1.ithmb", "F1024_1.ithmb", "F1019_1.ithmb", "F1019_2.ithmb" })
                File.WriteAllBytes(Path.Combine(tmp, fn), Array.Empty<byte>());
            var root = Path.Combine(Path.GetTempPath(), "ipodcmd-idtest");
            var ids = IpodIdentify.ScanFormatIds(root);
            Check("scanned ids contain 1036", ids.Contains(1036), true);
            Check("scanned set → Video", IpodIdentify.FromArtworkFingerprint(ids)?.Gen, IPodGeneration.Video);
        }
        catch (Exception ex) { log.AppendLine("scan EXCEPTION: " + ex.Message); fail++; }

        log.AppendLine();
        log.AppendLine($"RESULT: {(fail == 0 ? "OK" : "FAIL")}  ({pass} passed, {fail} failed)");
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "ipod-identifytest.txt"), log.ToString());
    }

    private static void RunMediaTest(string file)
    {
        string outPath = Path.Combine(AppContext.BaseDirectory, "ipod-mediatest.txt");
        var log = new StringBuilder();
        log.AppendLine($"--mediatest {file}");
        log.AppendLine($"exists: {File.Exists(file)}");
        try
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            var form = new Form { StartPosition = FormStartPosition.Manual, Location = new System.Drawing.Point(-3000, -3000), Size = new System.Drawing.Size(320, 240), ShowInTaskbar = false };
            var host = new System.Windows.Forms.Integration.ElementHost { Dock = DockStyle.Fill };
            var me = new System.Windows.Controls.MediaElement
            {
                LoadedBehavior = System.Windows.Controls.MediaState.Manual,
                UnloadedBehavior = System.Windows.Controls.MediaState.Manual,
                Stretch = System.Windows.Media.Stretch.Uniform,
            };
            bool opened = false, failed = false, ended = false; string err = "";
            double durSec = -1, posSec = -1, w = -1, h = -1;
            me.MediaOpened += (_, _) =>
            {
                opened = true;
                if (me.NaturalDuration.HasTimeSpan) durSec = me.NaturalDuration.TimeSpan.TotalSeconds;
                w = me.NaturalVideoWidth; h = me.NaturalVideoHeight;
            };
            me.MediaFailed += (_, e) => { failed = true; err = e.ErrorException?.Message ?? "(unknown)"; };
            me.MediaEnded += (_, _) => ended = true;
            host.Child = me;
            form.Controls.Add(host);

            int ticks = 0;
            var timer = new System.Windows.Forms.Timer { Interval = 250 };
            timer.Tick += (_, _) =>
            {
                ticks++;
                if (ticks == 1) { me.Source = new Uri(Path.GetFullPath(file)); me.Play(); }
                if (me.Position.TotalSeconds > 0) posSec = me.Position.TotalSeconds;
                if (ticks >= 12 || ended) // ~3s
                {
                    timer.Stop();
                    me.Stop(); me.Close();
                    form.Close();
                }
            };
            form.Shown += (_, _) => timer.Start();
            Application.Run(form);

            log.AppendLine($"opened: {opened}");
            log.AppendLine($"failed: {failed}{(failed ? " — " + err : "")}");
            log.AppendLine($"durationSec: {durSec:0.00}");
            log.AppendLine($"positionReachedSec: {posSec:0.00}  (playback advanced if > 0)");
            log.AppendLine($"naturalVideo: {w}x{h}  (0x0 = audio-only)");
            log.AppendLine($"ended: {ended}");
            log.AppendLine(opened && !failed && posSec > 0 ? "RESULT: PASS — engine opened and played the file." : "RESULT: FAIL — see above.");
        }
        catch (Exception ex) { log.AppendLine("EXCEPTION: " + ex); }
        File.WriteAllText(outPath, log.ToString());
    }

    private static void RunSelfTest()
    {
        var log = new StringBuilder();
        int failures = 0;
        void Check(bool ok, string what)
        {
            log.AppendLine((ok ? "PASS " : "FAIL ") + what);
            if (!ok) failures++;
        }

        try
        {
            byte[] bytes = SyntheticDb.Build();
            log.AppendLine($"Synthetic iTunesDB built: {bytes.Length} bytes");
            var db = ITunesDbReader.Read(bytes);

            Check(db.Version == 0x13, $"mhbd version = 0x{db.Version:X} (expected 0x13)");
            Check(db.PersistentId == 0xABCDEF0123456789UL, $"db persistent id = 0x{db.PersistentId:X16}");
            Check(db.Tracks.Count == 2, $"track count = {db.Tracks.Count} (expected 2)");

            if (db.Tracks.Count == 2)
            {
                var t0 = db.Tracks[0];
                Check(t0.UniqueId == 101, $"track[0] uniqueId = {t0.UniqueId}");
                Check(t0.Title == "Bohemian Rhapsody", $"track[0] title = '{t0.Title}'");
                Check(t0.Artist == "Queen", $"track[0] artist = '{t0.Artist}'");
                Check(t0.Album == "A Night at the Opera", $"track[0] album = '{t0.Album}'");
                Check(t0.Genre == "Rock", $"track[0] genre = '{t0.Genre}'");
                Check(t0.Location == ":iPod_Control:Music:F00:AAAA.mp3", $"track[0] location = '{t0.Location}'");
                Check(t0.LengthMs == 354000, $"track[0] length = {t0.LengthMs} ms");
                Check(t0.FileSize == 8_500_000, $"track[0] size = {t0.FileSize}");
                Check(t0.Bitrate == 192, $"track[0] bitrate = {t0.Bitrate}");
                Check(t0.SampleRate == 44100, $"track[0] sampleRate = {t0.SampleRate} Hz");
                Check(t0.Year == 1975, $"track[0] year = {t0.Year}");
                Check(t0.TrackNumber == 11, $"track[0] track# = {t0.TrackNumber}");
                Check(t0.Dbid == 0x1111_1111_1111_1111UL, $"track[0] dbid = 0x{t0.Dbid:X16}");
                Check(t0.MediaType == 1, $"track[0] mediaType = {t0.MediaType}");

                var t1 = db.Tracks[1];
                Check(t1.UniqueId == 102 && t1.Title == "Imagine" && t1.Artist == "John Lennon",
                    $"track[1] = {t1.UniqueId}/'{t1.Title}'/'{t1.Artist}'");
            }

            Check(db.Playlists.Count == 2, $"playlist count = {db.Playlists.Count} (expected 2)");
            var master = db.Master;
            Check(master is not null, "master playlist present");
            if (master is not null)
            {
                Check(master.Name == "iPod", $"master name = '{master.Name}'");
                Check(master.TrackIds.SequenceEqual(new uint[] { 101, 102 }),
                    $"master members = [{string.Join(",", master.TrackIds)}] (expected 101,102)");
            }
            var fav = db.Playlists.FirstOrDefault(p => !p.IsMaster);
            Check(fav is not null && fav.Name == "Favourites" && fav.TrackIds.SequenceEqual(new uint[] { 102 }),
                $"playlist 'Favourites' members = [{(fav is null ? "" : string.Join(",", fav.TrackIds))}] (expected 102)");
            Check(fav is not null && fav.SortOrder == 5, $"playlist 'Favourites' sortOrder = {fav?.SortOrder} (expected 5)");
            Check(db.Warnings.Count == 0, $"reader warnings = {db.Warnings.Count} (expected 0): {string.Join("; ", db.Warnings)}");
        }
        catch (Exception ex)
        {
            Check(false, "exception during reader self-test: " + ex);
        }

        Check(Hash58.SelfCheck(), "hash58 substitution tables are valid inverse AES S-boxes (transcription OK)");

        log.AppendLine();
        log.AppendLine("--- device detection ---");
        try
        {
            var devices = DeviceDetector.DetectAll();
            log.AppendLine($"iPods detected: {devices.Count}");
            foreach (var d in devices) AppendDeviceInfo(log, d);
        }
        catch (Exception ex)
        {
            log.AppendLine("detection error: " + ex.Message);
        }

        log.AppendLine();
        log.AppendLine(failures == 0 ? "RESULT: OK" : $"RESULT: FAILED ({failures} check(s))");
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "ipod-test.txt"), log.ToString());
    }

    private static void RunDump(string mountRoot)
    {
        var log = new StringBuilder();
        try
        {
            var device = DeviceDetector.Build(mountRoot);
            if (device is null)
            {
                log.AppendLine($"No iPod_Control folder under '{mountRoot}'. Is this the iPod's drive root?");
            }
            else
            {
                AppendDeviceInfo(log, device);
                log.AppendLine();
                if (!device.HasDatabase)
                {
                    log.AppendLine($"No iTunesDB at {device.ITunesDbPath}");
                }
                else
                {
                    var db = ITunesDbReader.ReadFile(device.ITunesDbPath);
                    log.AppendLine($"iTunesDB version 0x{db.Version:X}, header 0x{db.HeaderLength:X}, {db.Tracks.Count} tracks, {db.Playlists.Count} playlists");
                    log.AppendLine();
                    log.AppendLine("PLAYLISTS:");
                    foreach (var p in db.Playlists)
                        log.AppendLine($"  {(p.IsMaster ? "*" : " ")} {p.DisplayName} — {p.Count} tracks");
                    log.AppendLine();
                    log.AppendLine("TRACKS:");
                    foreach (var t in db.Tracks)
                        log.AppendLine($"  [{t.UniqueId}] {t.Artist} — {t.DisplayTitle} ({t.Duration:mm\\:ss}) {t.Bitrate}kbps  {t.Location}");

                    if (db.Warnings.Count > 0)
                    {
                        log.AppendLine();
                        log.AppendLine("WARNINGS:");
                        foreach (var wmsg in db.Warnings) log.AppendLine("  ! " + wmsg);
                    }
                }
            }
            log.AppendLine();
            log.AppendLine("RESULT: OK");
        }
        catch (Exception ex)
        {
            log.AppendLine("RESULT: FAILED - " + ex);
        }
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "ipod-dump.txt"), log.ToString());
    }

    private static void RunRatingScan(string path)
    {
        var log = new StringBuilder();
        try
        {
            IPodDevice? dev = Directory.Exists(path) ? DeviceDetector.Build(path) : null;
            // Use the full library load when we have a device, so the on-device Play Counts overlay is applied.
            var db = dev is not null ? IpodLibrary.Load(dev).View : ITunesDbReader.Read(File.ReadAllBytes(path));
            int rated = db.Tracks.Count(t => t.Rating > 0);
            int played = db.Tracks.Count(t => t.PlayCount > 0);
            log.AppendLine($"iTunesDB tracks: {db.Tracks.Count}  (after Play Counts overlay)");
            log.AppendLine($"  rating>0    : {rated}");
            log.AppendLine($"  playcount>0 : {played}");
            foreach (var t in db.Tracks.Where(t => t.Rating > 0 || t.PlayCount > 0).Take(15))
                log.AppendLine($"    '{t.DisplayTitle}'  rating={t.Rating} ({t.Rating / 20}*)  plays={t.PlayCount}");

            if (dev is not null)
            {
                string pc = Path.Combine(Path.GetDirectoryName(dev.ITunesDbPath)!, "Play Counts");
                log.AppendLine();
                if (File.Exists(pc))
                {
                    byte[] p = File.ReadAllBytes(pc);
                    string sig = System.Text.Encoding.ASCII.GetString(p, 0, 4);
                    uint hdr = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(4));
                    uint elen = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(8));
                    uint cnt = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(12));
                    log.AppendLine($"Play Counts: sig={sig} headerLen={hdr} entryLen={elen} count={cnt} size={p.Length}");
                    int pcPlays = 0, pcRated = 0;
                    for (int e = 0; e < cnt; e++)
                    {
                        int o = (int)hdr + e * (int)elen;
                        if (o + 16 > p.Length) break;
                        uint play = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(o));
                        uint rate = elen >= 16 ? System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(o + 12)) : 0;
                        if (play > 0) pcPlays++;
                        if (rate > 0) pcRated++;
                    }
                    log.AppendLine($"  entries with plays>0: {pcPlays}  rating>0: {pcRated}");
                }
                else log.AppendLine("Play Counts: (none)");
            }
            log.AppendLine("RESULT: OK");
        }
        catch (Exception ex) { log.AppendLine("ERROR: " + ex); }
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "ipod-ratings.txt"), log.ToString());
    }

    private static void RunRender(string dbPath, string outPng, string view)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.SystemAware);

        IPodDevice? device;
        if (Directory.Exists(dbPath))
        {
            // Live device root (real audio files present → real embedded cover art renders).
            device = DeviceDetector.Build(dbPath);
        }
        else
        {
            // Sandbox around a bare DB file (no audio files → artwork falls back to gradients).
            string sandbox = Path.Combine(Path.GetTempPath(), "ipodcmd-render");
            if (Directory.Exists(sandbox)) Directory.Delete(sandbox, true);
            string control = Path.Combine(sandbox, "iPod_Control");
            Directory.CreateDirectory(Path.Combine(control, "iTunes"));
            Directory.CreateDirectory(Path.Combine(control, "Device"));
            for (int i = 0; i < 10; i++) Directory.CreateDirectory(Path.Combine(control, "Music", $"F{i:00}"));
            File.Copy(dbPath, Path.Combine(control, "iTunes", "iTunesDB"));
            File.WriteAllText(Path.Combine(control, "Device", "SysInfo"), "ModelNumStr: M9807\n");
            device = DeviceDetector.Build(sandbox);
        }

        // The equalizer dialog renders on its own.
        if (view == "equalizer")
        {
            using var eq = new EqualizerDialog(true, new float[] { 6, 5, 4, 2, 0, 0, 2, 4, 5, 6 }, (_, _) => { })
            { StartPosition = FormStartPosition.Manual, Location = new Point(-2600, -2600) };
            eq.Show();
            for (int i = 0; i < 6; i++) { Application.DoEvents(); Thread.Sleep(60); }
            using var ebmp = new Bitmap(eq.Width, eq.Height);
            eq.DrawToBitmap(ebmp, new Rectangle(0, 0, eq.Width, eq.Height));
            ebmp.Save(outPng, System.Drawing.Imaging.ImageFormat.Png);
            eq.Close();
            return;
        }

        // The Pro-features dialog renders on its own.
        if (view == "profeatures")
        {
            using var pf = new ProFeaturesDialog(gapless: true, crossOn: true, crossSecs: 6, normalize: false, mono: false, sleepMin: 0, (_, _, _, _, _) => { }, _ => { })
            { StartPosition = FormStartPosition.Manual, Location = new Point(-2600, -2600) };
            pf.Show();
            for (int i = 0; i < 6; i++) { Application.DoEvents(); Thread.Sleep(60); }
            using var pbmp = new Bitmap(pf.Width, pf.Height);
            pf.DrawToBitmap(pbmp, new Rectangle(0, 0, pf.Width, pf.Height));
            pbmp.Save(outPng, System.Drawing.Imaging.ImageFormat.Png);
            pf.Close();
            return;
        }

        // The Library Doctor dialog renders on its own with a synthetic report.
        if (view == "librarydoctor" || view == "librarydoctorclean")
        {
            var rep = new DoctorReport { TotalTracks = 185 };
            if (view != "librarydoctorclean")
            {
                rep.MissingFiles.Add(new Track()); rep.MissingFiles.Add(new Track()); rep.MissingFiles.Add(new Track());
                rep.OrphanFiles.Add(("a", 4_200_000)); rep.OrphanFiles.Add(("b", 3_900_000)); rep.OrphanBytes = 8_100_000;
                rep.DuplicateGroups.Add(new List<Track> { new(), new() });
                rep.DuplicateGroups.Add(new List<Track> { new(), new(), new() });
                rep.DuplicateExtras = 3;
                rep.IncompleteTags = 9; rep.AlbumGaps = 2;
            }
            using var dlg = new LibraryDoctorDialog(rep) { StartPosition = FormStartPosition.Manual, Location = new Point(-2600, -2600) };
            dlg.Show();
            for (int i = 0; i < 6; i++) { Application.DoEvents(); Thread.Sleep(60); }
            using var dbmp = new Bitmap(dlg.Width, dlg.Height);
            dlg.DrawToBitmap(dbmp, new Rectangle(0, 0, dlg.Width, dlg.Height));
            dbmp.Save(outPng, System.Drawing.Imaging.ImageFormat.Png);
            dlg.Close();
            return;
        }

        // The Up Next queue panel renders on its own with synthetic tracks.
        if (view == "upnext")
        {
            // Preview the panel content at the floating flyout's content size (DWM corner-rounding is live-only).
            using var f = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(-2600, -2600), FormBorderStyle = FormBorderStyle.None, Size = new Size(304, UpNextPanel.DesiredHeight(8, true, false)), BackColor = Theme.PanelBg };
            var panel = new UpNextPanel { Dock = DockStyle.Fill };
            f.Controls.Add(panel);
            f.Show();
            var now = new Track { Title = "A Moment Apart", Artist = "ODESZA", MediaType = 1 };
            string[] titles = { "Higher Ground", "Late Night", "Loyal", "Corners of the Earth", "Just a Memory", "Across the Room", "Line of Sight", "Falls" };
            var items = new List<(Track, Bitmap?)>();
            for (int i = 0; i < titles.Length; i++) items.Add((new Track { Title = titles[i], Artist = "ODESZA", MediaType = 1 }, CoverArt.Generate(i * 7 + 3, 48)));
            panel.SetData(now, CoverArt.Generate(45, 48), items, null);
            for (int i = 0; i < 4; i++) { Application.DoEvents(); Thread.Sleep(40); }
            using var bmp = new Bitmap(panel.Width, panel.Height);
            panel.DrawToBitmap(bmp, new Rectangle(0, 0, panel.Width, panel.Height));
            bmp.Save(outPng, System.Drawing.Imaging.ImageFormat.Png);
            return;
        }

        // The cover picker renders on its own.
        if (view == "coverpicker")
        {
            using var cp = new CoverPickerDialog("Cover for “Summer 2024”", 5)
            { StartPosition = FormStartPosition.Manual, Location = new Point(-2600, -2600) };
            cp.Show();
            for (int i = 0; i < 6; i++) { Application.DoEvents(); Thread.Sleep(60); }
            using var cbmp = new Bitmap(cp.Width, cp.Height);
            cp.DrawToBitmap(cbmp, new Rectangle(0, 0, cp.Width, cp.Height));
            cbmp.Save(outPng, System.Drawing.Imaging.ImageFormat.Png);
            cp.Close();
            return;
        }

        // The Settings window renders on its own. "settings" = Appearance; "settingsN" = category N;
        // "settingsall" = every category written next to outPng as <stem>_<i>.png.
        if (view.StartsWith("settings"))
        {
            var settings = AppSettings.Load();
            Theme.SetAccent(settings.Accent); // match the live app so the render is accurate
            string tail = view.Substring("settings".Length);

            void Snap(SettingsForm form, string path)
            {
                for (int i = 0; i < 6; i++) { Application.DoEvents(); Thread.Sleep(60); }
                using var bmp = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(bmp, new Rectangle(0, 0, form.Width, form.Height));
                bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }

            if (tail == "all")
            {
                string stem = Path.Combine(Path.GetDirectoryName(outPng) ?? ".", Path.GetFileNameWithoutExtension(outPng));
                for (int cat = 0; cat < 7; cat++)
                {
                    using var sf = new SettingsForm(settings, device, () => { }, () => { })
                    { StartPosition = FormStartPosition.Manual, Location = new Point(-2600, -2600) };
                    sf.Show();
                    sf.RenderCategory(cat);
                    Snap(sf, $"{stem}_{cat}.png");
                    sf.Close();
                }
                return;
            }

            using var single = new SettingsForm(settings, device, () => { }, () => { })
            { StartPosition = FormStartPosition.Manual, Location = new Point(-2600, -2600) };
            single.Show();
            if (int.TryParse(tail, out int catIndex)) single.RenderCategory(catIndex);
            Snap(single, outPng);
            single.Close();
            return;
        }

        // A contact sheet of every generation's iPod illustration (no device needed).
        if (view == "ipodgallery")
        {
            var gens = new[] { IPodGeneration.First, IPodGeneration.Photo, IPodGeneration.Video, IPodGeneration.Classic1,
                IPodGeneration.Mini2, IPodGeneration.Nano1, IPodGeneration.Nano2, IPodGeneration.Nano3, IPodGeneration.Nano4,
                IPodGeneration.Nano5, IPodGeneration.Nano6, IPodGeneration.Nano7, IPodGeneration.Shuffle, IPodGeneration.Unknown };
            int cell = 150, cols = 5, pad = 18, labelH = 22;
            int rows = (gens.Length + cols - 1) / cols;
            using var sheet = new Bitmap(cols * cell + (cols + 1) * pad, rows * (cell + labelH) + (rows + 1) * pad);
            using (var g = Graphics.FromImage(sheet))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(Theme.Bg);
                for (int i = 0; i < gens.Length; i++)
                {
                    int c = i % cols, r = i / cols;
                    int x = pad + c * (cell + pad), y = pad + r * (cell + labelH + pad);
                    using (var art = IpodArt.Render(gens[i], cell)) g.DrawImage(art, x, y);
                    TextRenderer.DrawText(g, gens[i].ToString(), Theme.UiFont(9f), new Rectangle(x, y + cell, cell, labelH), Theme.Subtle, TextFormatFlags.HorizontalCenter);
                }
            }
            sheet.Save(outPng, System.Drawing.Imaging.ImageFormat.Png);
            return;
        }

        // The now-playing bar renders on its own (plays a real sample if one is present locally).
        if (view == "nowplaying")
        {
            using var f = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(-2600, -2600), FormBorderStyle = FormBorderStyle.None, Size = new Size(960, NowPlayingBar.H), BackColor = Theme.Bg };
            var bar = new NowPlayingBar { Dock = DockStyle.Fill };
            f.Controls.Add(bar);
            f.Show();
            var t = new Track { Title = "A Moment Apart", Artist = "ODESZA", Album = "A Moment Apart", MediaType = 1, LengthMs = 234000 };
            string sample = @"C:\Users\Erik\Music\M4A\A Moment Apart - ODESZA.m4a";
            if (File.Exists(sample)) bar.Play(t, sample, null);
            for (int i = 0; i < 14; i++) { Application.DoEvents(); Thread.Sleep(120); }
            using var bbmp = new Bitmap(f.Width, f.Height);
            f.DrawToBitmap(bbmp, new Rectangle(0, 0, f.Width, f.Height));
            bbmp.Save(outPng, System.Drawing.Imaging.ImageFormat.Png);
            bar.StopAndHide();
            return;
        }

        // The detached mini player renders on its own with a synthetic track + cover ("miniplayer" =
        // normal, "miniplayercompact" = the small control UI).
        if (view == "miniplayer" || view == "miniplayercompact" || view == "miniplayeridle")
        {
            using var mp = new MiniPlayerForm { StartPosition = FormStartPosition.Manual, Location = new Point(-2600, -2600) };
            mp.Show();
            if (view == "miniplayercompact") { mp.SetCompact(true); mp.Location = new Point(-2600, -2600); }
            bool idle = view == "miniplayeridle";
            var t = new Track { Title = "A Moment Apart", Artist = "ODESZA", Album = "A Moment Apart", MediaType = 1, LengthMs = 234000 };
            // Prefer a real embedded cover (more honest preview); fall back to synthetic art.
            Bitmap? cover = null;
            const string sample = @"C:\Users\Erik\Music\M4A\A Moment Apart - ODESZA.m4a";
            try { if (File.Exists(sample) && MetadataExtractor.ReadArt(sample) is { Length: > 0 } b) using (var ms = new MemoryStream(b)) cover = new Bitmap(Image.FromStream(ms)); } catch { cover = null; }
            if (!idle) mp.SetTrack(t, cover ?? CoverArt.Generate(45, 300));
            mp.SetProgress(playing: !idle, posSec: idle ? 0 : 78, durSec: idle ? 0 : 234, volume: 0.72, muted: false, shuffle: !idle, repeat: NowPlayingBar.RepeatMode.All);
            if (!idle)   // no live audio in a render → inject a representative spectrum so the bars are visible
            {
                var demo = new float[28];
                for (int i = 0; i < demo.Length; i++) demo[i] = (float)(0.30 + 0.55 * Math.Abs(Math.Sin(i * 0.7)) * (1.0 - i / 40.0));
                mp.DebugSpectrum(demo);
            }
            for (int i = 0; i < 6; i++) { Application.DoEvents(); Thread.Sleep(60); }
            using var mbmp = new Bitmap(mp.ClientSize.Width, mp.ClientSize.Height);
            mp.DrawToBitmap(mbmp, new Rectangle(0, 0, mp.ClientSize.Width, mp.ClientSize.Height));
            mbmp.Save(outPng, System.Drawing.Imaging.ImageFormat.Png);
            return;
        }

        // The sidebar with playlist cover icons (verifies the rounded crisp mini-cover rendering).
        if (view == "sidebaricon")
        {
            using var f = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(-2600, -2600), FormBorderStyle = FormBorderStyle.None, Size = new Size(236, 360), BackColor = Theme.SidebarBg };
            var sb = new Sidebar { Dock = DockStyle.Fill };
            f.Controls.Add(sb);
            f.Show();
            sb.Begin();
            sb.AddSection("PLAYLISTS");
            var pls = new[] { "Current Playlist", "Summer 2024", "Workout", "Chill" };
            var objs = pls.Select(n => { var p = new Playlist { Name = n }; sb.AddItem(SidebarRowKind.Playlist, n, p, n == "Summer 2024"); return p; }).ToArray();
            sb.End();
            for (int i = 0; i < objs.Length; i++) sb.SetIcon(objs[i], CoverArt.Generate(i * 5 + 2, 36));
            for (int i = 0; i < 4; i++) { Application.DoEvents(); Thread.Sleep(60); }
            using var sbm = new Bitmap(f.Width, f.Height);
            f.DrawToBitmap(sbm, new Rectangle(0, 0, f.Width, f.Height));
            sbm.Save(outPng, System.Drawing.Imaging.ImageFormat.Png);
            return;
        }

        // Cover Flow renders on its own with synthetic album covers.
        if (view == "coverflow")
        {
            using var f = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(-2600, -2600), FormBorderStyle = FormBorderStyle.None, Size = new Size(960, 600), BackColor = Theme.Bg };
            var cf = new CoverFlowView { Dock = DockStyle.Fill };
            f.Controls.Add(cf);
            f.Show();
            var items = new List<CoverFlowView.Item>();
            string[] titles = { "A Moment Apart", "Starboy", "SOUR", "Brightest Lights", "Settle", "Discovery", "Currents", "In Return", "Wasteland", "Random Access", "Nectar", "Voyage" };
            string[] artists = { "ODESZA", "The Weeknd", "Olivia Rodrigo", "Lane 8", "Disclosure", "Daft Punk", "Tame Impala", "ODESZA", "Brent Faiyaz", "Daft Punk", "Joji", "ABBA" };
            for (int i = 0; i < titles.Length; i++)
                items.Add(new CoverFlowView.Item(CoverArt.Generate(i * 7 + 3, 300), titles[i], artists[i], i));
            cf.SetItems(items, titles.Length / 2);
            cf.PlayingTag = items[titles.Length / 2].Tag; // so the "Now Playing" chip shows in the preview
            for (int i = 0; i < 8; i++) { Application.DoEvents(); Thread.Sleep(60); }
            using var cbmp2 = new Bitmap(f.Width, f.Height);
            f.DrawToBitmap(cbmp2, new Rectangle(0, 0, f.Width, f.Height));
            cbmp2.Save(outPng, System.Drawing.Imaging.ImageFormat.Png);
            return;
        }

        // The photo grid renders on its own with synthetic tiles (enough to overflow → shows the scrollbar).
        if (view == "photogrid")
        {
            using var f = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(-2600, -2600), FormBorderStyle = FormBorderStyle.None, Size = new Size(720, 480), BackColor = Theme.Bg };
            var grid = new PhotoGridView { Dock = DockStyle.Fill };
            f.Controls.Add(grid);
            f.Show();
            var tiles = new List<(uint, Bitmap?)>();
            for (uint i = 1; i <= 48; i++)
            {
                var b = new Bitmap(120, 120);
                using (var g = Graphics.FromImage(b))
                using (var br = new System.Drawing.Drawing2D.LinearGradientBrush(new Rectangle(0, 0, 120, 120), Theme.HsvToColor(i * 30 % 360, 0.6, 0.8), Theme.HsvToColor(i * 30 % 360 + 30, 0.7, 0.5), 45f))
                    g.FillRectangle(br, 0, 0, 120, 120);
                tiles.Add((i, b));
            }
            grid.SetPhotos(tiles);
            for (int i = 0; i < 4; i++) { Application.DoEvents(); Thread.Sleep(60); }
            using var gbmp = new Bitmap(f.Width, f.Height);
            f.DrawToBitmap(gbmp, new Rectangle(0, 0, f.Width, f.Height));
            gbmp.Save(outPng, System.Drawing.Imaging.ImageFormat.Png);
            return;
        }

        // The photo viewer (lightbox) renders on its own using synthetic images.
        if (view == "photoviewer")
        {
            var ids = new List<uint> { 1, 2, 3, 4, 5 };
            using var dlg = new PhotoViewerDialog(ids, 2,
                id => { var b = new Bitmap(320, 240); using var g = Graphics.FromImage(b); using var br = new System.Drawing.Drawing2D.LinearGradientBrush(new Rectangle(0, 0, 320, 240), Color.FromArgb(40, 120, 200), Color.FromArgb(200, 80, 140), 35f); g.FillRectangle(br, 0, 0, 320, 240); return b; },
                id => $"VRChat — sample photo {id}")
            { StartPosition = FormStartPosition.Manual, Location = new Point(-2600, -2600) };
            dlg.Show();
            for (int i = 0; i < 6; i++) { Application.DoEvents(); Thread.Sleep(60); }
            using var pbmp = new Bitmap(dlg.Width, dlg.Height);
            dlg.DrawToBitmap(pbmp, new Rectangle(0, 0, dlg.Width, dlg.Height));
            pbmp.Save(outPng, System.Drawing.Imaging.ImageFormat.Png);
            dlg.Close();
            return;
        }

        var form = new MainForm(autoDetect: false)
        {
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-2600, -2600),
            Size = new Size(1080, view == "device" ? 1180 : 700), // device page is tall — show it all for review
        };
        form.Show();
        Application.DoEvents();
        if (device is not null) form.PreviewDevice(device);
        if (view is "videos" or "photos" or "device" or "albums" or "artists" or "local") form.PreviewSelectView(view);
        // Pump the message loop so background cover-art loads land before we capture.
        for (int i = 0; i < 45; i++) { Application.DoEvents(); Thread.Sleep(100); }

        using (var bmp = new Bitmap(form.Width, form.Height))
        {
            form.DrawToBitmap(bmp, new Rectangle(0, 0, form.Width, form.Height));
            bmp.Save(outPng, System.Drawing.Imaging.ImageFormat.Png);
        }
        form.Close();
    }

    /// <summary>Regenerate app.ico from a source PNG: crop to the artwork's bounding box (ignoring white /
    /// soft-shadow background), round the corners with transparency, and emit a multi-resolution PNG-in-ICO.</summary>
    private static void RunMakeIcon(string srcPng, string outIco)
    {
        using var orig = (Bitmap)Image.FromFile(srcPng);
        int W = orig.Width, H = orig.Height;
        using var src = new Bitmap(W, H, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g0 = Graphics.FromImage(src)) { g0.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic; g0.DrawImage(orig, 0, 0, W, H); }

        // Bounding box of real content: skip transparent pixels and light, low-saturation background
        // (white margin + the soft drop shadow), so the box hugs the coloured artwork.
        int minX = W, minY = H, maxX = -1, maxY = -1;
        var d = src.LockBits(new Rectangle(0, 0, W, H), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        unsafe
        {
            byte* b0 = (byte*)d.Scan0; int st = d.Stride;
            for (int y = 0; y < H; y++)
            {
                byte* row = b0 + y * st;
                for (int x = 0; x < W; x++)
                {
                    byte* p = row + x * 4;
                    int bl = p[0], gr = p[1], re = p[2], al = p[3];
                    if (al <= 12) continue;
                    int mx = Math.Max(re, Math.Max(gr, bl)), mn = Math.Min(re, Math.Min(gr, bl));
                    bool bg = mx >= 232 && (mx - mn) <= 18; // white-ish / grey shadow
                    if (bg) continue;
                    if (x < minX) minX = x; if (x > maxX) maxX = x;
                    if (y < minY) minY = y; if (y > maxY) maxY = y;
                }
            }
        }
        src.UnlockBits(d);
        if (maxX < minX) { minX = minY = 0; maxX = W - 1; maxY = H - 1; }

        int side = Math.Min(Math.Min(W, H), Math.Max(maxX - minX + 1, maxY - minY + 1));
        int cxp = (minX + maxX) / 2, cyp = (minY + maxY) / 2;
        int sx = Math.Max(0, Math.Min(cxp - side / 2, W - side)), sy = Math.Max(0, Math.Min(cyp - side / 2, H - side));
        var crop = new Rectangle(sx, sy, side, side);

        int[] sizes = { 256, 128, 64, 48, 32, 24, 16 };
        var pngs = new List<byte[]>();
        foreach (int S in sizes)
        {
            int SS = S * 2; // supersample → smooth rounded edge after downscale
            using var hi = new Bitmap(SS, SS, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(hi))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using var path = RoundedIconPath(SS, SS * 0.18f);
                g.SetClip(path);
                g.DrawImage(src, new Rectangle(0, 0, SS, SS), crop, GraphicsUnit.Pixel);
                g.ResetClip();
            }
            var sm = new Bitmap(S, S, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(sm))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                g.DrawImage(hi, new Rectangle(0, 0, S, S), new Rectangle(0, 0, SS, SS), GraphicsUnit.Pixel);
            }
            using (var ms = new MemoryStream()) { sm.Save(ms, System.Drawing.Imaging.ImageFormat.Png); pngs.Add(ms.ToArray()); }
            if (S == 256) sm.Save("icon-preview.png", System.Drawing.Imaging.ImageFormat.Png);
            sm.Dispose();
        }

        using (var fs = new FileStream(outIco, FileMode.Create, FileAccess.Write))
        using (var w = new BinaryWriter(fs))
        {
            w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);   // ICONDIR
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                byte dim = (byte)(sizes[i] >= 256 ? 0 : sizes[i]);
                w.Write(dim); w.Write(dim); w.Write((byte)0); w.Write((byte)0);     // w, h, palette, reserved
                w.Write((short)1); w.Write((short)32);                              // planes, bpp
                w.Write(pngs[i].Length); w.Write(offset);
                offset += pngs[i].Length;
            }
            foreach (var p in pngs) w.Write(p);
        }
        Console.WriteLine($"Wrote {outIco} ({sizes.Length} sizes) from {srcPng}; crop={crop} of {W}x{H}");
    }

    private static System.Drawing.Drawing2D.GraphicsPath RoundedIconPath(int size, float rad)
    {
        float d = rad * 2;
        var p = new System.Drawing.Drawing2D.GraphicsPath();
        p.AddArc(0, 0, d, d, 180, 90);
        p.AddArc(size - d, 0, d, d, 270, 90);
        p.AddArc(size - d, size - d, d, d, 0, 90);
        p.AddArc(0, size - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    /// <summary>Add cover art to songs already on the iPod, in place, without re-copying them.</summary>
    private static void RunRebuildArt(string root, int maxCount)
    {
        var log = new StringBuilder();
        void L(string s = "") { log.AppendLine(s); Console.WriteLine(s); }
        try
        {
            var dev = DeviceDetector.Build(root);
            if (dev is null) { L("No iPod at " + root); return; }
            L($"Device: {dev.DisplayName}  ({dev.Profile.Generation})  CanWrite={dev.Profile.CanWrite}");
            if (!dev.Profile.CanWrite) { L("Device is read-only — aborting."); return; }

            var lib = IpodLibrary.Load(dev);
            var art = ArtworkLibrary.Load(dev);
            art.Force = true;                 // we've confirmed the format out-of-band
            lib.Artwork = art;
            L($"Artwork: SupportsArtwork={art.SupportsArtwork} SafeToWrite={art.SafeToWrite} formats=[{string.Join(",", art.AddFormats.Select(f => f.FormatId))}] fromDevice={art.FormatsFromDevice}");
            if (!art.SupportsArtwork) { L("Artwork sink not writable — aborting."); return; }

            // Clean rebuild: drop all existing images (orphans + earlier broken writes) and re-link from zero.
            art.StartCleanRebuild();
            lib.Raw.ClearAllTrackArtwork();

            int added = 0, noFile = 0, noArt = 0, linkFail = 0;
            foreach (var t in lib.View.Tracks)
            {
                if (added >= maxCount) break;
                string? path = t.ResolveFilePath(root);
                if (path is null || !File.Exists(path)) { noFile++; continue; }
                var staged = art.Stage(t.Dbid, path);
                if (staged is null) { noArt++; continue; }
                if (!lib.Raw.SetTrackArtwork(t.UniqueId, staged.Value.MhiiId, staged.Value.Size)) { linkFail++; continue; }
                added++;
            }
            L($"Staged art for {added} track(s).  noFile={noFile} noEmbeddedArt={noArt} linkFail={linkFail} (of {lib.View.Tracks.Count} tracks)");
            if (added == 0) { L("Nothing to write."); return; }

            L("Saving (signs iTunesDB + commits ArtworkDB, with backups)…");
            lib.Save();
            L("Done. iTunesDB + ArtworkDB written safely.");
        }
        catch (Exception ex) { L("REBUILD ERROR: " + ex); }
        finally { try { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "ipod-rebuildart.txt"), log.ToString()); } catch { } }
    }

    /// <summary>Dump the raw mhii/mhod/mhni structure iTunes used, so we can match it byte-for-byte.</summary>
    private static void RunArtMhiiDump(string root, int start, int count)
    {
        string dbPath = File.Exists(root) ? root : Path.Combine(root, "iPod_Control", "Artwork", "ArtworkDB");
        byte[] d = File.ReadAllBytes(dbPath);
        var sb = new StringBuilder();
        uint U32(int o) => System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(o, 4));
        ushort U16(int o) => System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(o, 2));
        ulong U64(int o) => System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(d.AsSpan(o, 8));
        string Tag(int o) => System.Text.Encoding.ASCII.GetString(d, o, 4);

        // mhfd → first mhsd → mhli → mhii*
        uint mhfdLen = U32(0x04);
        int off = (int)mhfdLen;
        sb.AppendLine($"mhfd header_len={mhfdLen} datasets={U32(0x14)} next_id={U32(0x1C)}");
        // find the type-1 mhsd
        int datasets = (int)U32(0x14);
        int mhliOff = -1;
        for (int i = 0; i < datasets && off + 16 <= d.Length; i++)
        {
            string t = Tag(off); uint hl = U32(off + 0x04), tl = U32(off + 0x08); uint type = U32(off + 0x0C);
            sb.AppendLine($"mhsd @0x{off:X} type={type} header_len={hl} total_len={tl}");
            if (type == 1) { mhliOff = off + (int)hl; }
            off += (int)tl;
        }
        if (mhliOff < 0 || Tag(mhliOff) != "mhli") { sb.AppendLine("no mhli"); File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "ipod-artmhiidump.txt"), sb.ToString()); Console.WriteLine(sb.ToString()); return; }
        uint mhliHl = U32(mhliOff + 0x04), mhiiCount = U32(mhliOff + 0x08);
        sb.AppendLine($"mhli header_len={mhliHl} count={mhiiCount}");
        int p = mhliOff + (int)mhliHl;
        // skip to the start index
        for (int n = 0; n < start && n < mhiiCount && Tag(p) == "mhii"; n++) p += (int)U32(p + 0x08);
        for (int n = start; n < start + count && n < mhiiCount; n++)
        {
            if (Tag(p) != "mhii") { sb.AppendLine($"[{n}] not mhii @0x{p:X}"); break; }
            uint mhl = U32(p + 0x04), tl = U32(p + 0x08), nch = U32(p + 0x0C), id = U32(p + 0x10);
            sb.AppendLine($"[{n}] mhii @0x{p:X} header_len={mhl} total_len={tl} num_children={nch} id={id}");
            sb.AppendLine($"      dbid@0x14=0x{U64(p + 0x14):X16}  dbid@0x18=0x{U64(p + 0x18):X16}  u32@0x30={U32(p + 0x30)}  u32@0x34={U32(p + 0x34)}");
            sb.Append("      hex 0x00-0x3F: ");
            for (int b = 0; b < 0x40 && p + b < d.Length; b++) sb.Append(d[p + b].ToString("X2")).Append(b % 4 == 3 ? " " : "");
            sb.AppendLine();
            int cp = p + (int)mhl;
            for (uint c = 0; c < nch && cp + 12 <= p + (int)tl; c++)
            {
                string ct = Tag(cp); uint chl = U32(cp + 0x04), ctl = U32(cp + 0x08); ushort mt = U16(cp + 0x0C);
                sb.AppendLine($"     child @0x{cp:X} '{ct}' header_len={chl} total_len={ctl} type@0x0C={mt}");
                // does it contain an mhni?
                int q = cp + (int)chl;
                if (q + 4 <= cp + (int)ctl && Tag(q) == "mhni")
                {
                    uint nhl = U32(q + 0x04), ntl = U32(q + 0x08), nnc = U32(q + 0x0C);
                    uint corr = U32(q + 0x10); uint imgOff = U32(q + 0x14); uint imgSize = U32(q + 0x18);
                    ushort vpad = U16(q + 0x1C), hpad = U16(q + 0x1E), hgt = U16(q + 0x20), wid = U16(q + 0x22);
                    sb.AppendLine($"        mhni header_len={nhl} total_len={ntl} num_children={nnc} corr_id={corr} ithmb_off={imgOff} image_size={imgSize} pad(v={vpad},h={hpad}) dims={wid}x{hgt}");
                    // walk the mhni's child mhod (the .ithmb filename string)
                    int fp = q + (int)nhl;
                    if (fp + 0x18 <= q + (int)ntl && Tag(fp) == "mhod")
                    {
                        ushort ftype = U16(fp + 0x0C); uint fstrlen = U32(fp + 0x18); byte enc = d[fp + 0x1C];
                        int s0 = fp + 0x24; int slen = (int)Math.Min(fstrlen, (uint)(q + (int)ntl - s0));
                        string str = enc == 2 ? System.Text.Encoding.Unicode.GetString(d, s0, slen) : System.Text.Encoding.UTF8.GetString(d, s0, slen);
                        sb.AppendLine($"        filename-mhod type@0x0C={ftype} enc(0x1C)={enc}(1=utf8,2=utf16) strlen={fstrlen} total_len={U32(fp + 0x08)} str='{str}'");
                    }
                    else sb.AppendLine("        (mhni has NO child filename mhod)");
                }
                cp += (int)ctl;
            }
            p += (int)tl;
        }
        string outp = Path.Combine(AppContext.BaseDirectory, "ipod-artmhiidump.txt");
        File.WriteAllText(outp, sb.ToString());
        Console.WriteLine(sb.ToString());
    }

    /// <summary>Decode <paramref name="count"/> RGB565-LE slots from an .ithmb starting at slot <paramref name="i0"/>
    /// and save each as a PNG, so the stored pixel format/rotation can be eyeballed.</summary>
    private static void RunIthmbSlot(string file, int w, int h, int i0, int count, string outDir)
    {
        Directory.CreateDirectory(outDir);
        byte[] all = File.ReadAllBytes(file);
        int slot = w * h * 2;
        for (int k = 0; k < count; k++)
        {
            int idx = i0 + k;
            int off = idx * slot;
            if (off + slot > all.Length) { Console.WriteLine($"slot {idx}: past EOF"); break; }
            var px = new byte[slot];
            Buffer.BlockCopy(all, off, px, 0, slot);
            using var bmp = Ithmb.Decode(px, w, h);
            if (bmp is null) { Console.WriteLine($"slot {idx}: decode failed"); continue; }
            string outp = Path.Combine(outDir, $"slot_{idx:000}.png");
            bmp.Save(outp, System.Drawing.Imaging.ImageFormat.Png);
            Console.WriteLine($"slot {idx}: {outp}");
        }
    }

    private static readonly string? Mdl2 = ResolveMdl2();
    private static string? ResolveMdl2()
    {
        foreach (var n in new[] { "Segoe MDL2 Assets", "Segoe Fluent Icons" })
            try { using var ff = new FontFamily(n); return n; } catch { }
        return null;
    }

    /// <summary>Draw Now-Playing-bar layout options stacked into one PNG so the user can pick a design.</summary>
    private static void RunNowPlayingOptions(string outPng)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        var s = AppSettings.Load();
        try { Theme.SetThemeVariant(s.ThemeVariant); } catch { }
        Theme.SetAccent(s.Accent);

        int W = 940, barH = 88, label = 30, gap = 26, pad = 18, n = 3;
        string[] labels =
        {
            "A — Centred transport, scrubber below it  (Spotify-style; info left, volume right)",
            "B — Centred transport, full-width progress line along the bottom",
            "C — Fully centred: title, transport and scrubber all centred",
        };
        using var bmp = new Bitmap(W + pad * 2, pad + n * (label + barH + gap));
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        g.Clear(Theme.Bg);
        int y = pad;
        for (int i = 0; i < n; i++)
        {
            using (var lf = Theme.UiFont(10f, FontStyle.Bold))
                TextRenderer.DrawText(g, labels[i], lf, new Rectangle(pad, y, W, label), Theme.TextCol, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            y += label;
            DrawBarMock(g, pad, y, W, barH, i);
            y += barH + gap;
        }
        bmp.Save(outPng, System.Drawing.Imaging.ImageFormat.Png);
    }

    private static void DrawBarMock(Graphics g, int x, int y, int w, int h, int style)
    {
        // backdrop + top seam (matches the real bar)
        using (var bg = new System.Drawing.Drawing2D.LinearGradientBrush(new Rectangle(x, y, w, h), Theme.Blend(Theme.SidebarBg, Color.White, 0.03), Theme.Blend(Theme.SidebarBg, Color.Black, 0.12), 90f))
            g.FillRectangle(bg, x, y, w, h);
        using (var seam = new Pen(Theme.Border)) g.DrawLine(seam, x, y, x + w, y);

        float cx = x + w / 2f;
        int cz = 56;
        var cr = new Rectangle(x + 16, y + (h - cz) / 2, cz, cz);

        // local draw helpers ---------------------------------------------------
        void Tile(Rectangle r)
        {
            int rad = (int)Math.Round(r.Width * Theme.TileFrac);
            using (var ph = new System.Drawing.Drawing2D.LinearGradientBrush(r, Theme.Blend(Theme.SidebarBg, Color.White, 0.10), Theme.Blend(Theme.SidebarBg, Color.Black, 0.06), Theme.ArtAngle))
            using (var pp = Theme.RoundedRect(new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1, r.Height - 1), rad)) g.FillPath(ph, pp);
            Theme.DrawNote(g, r, Color.FromArgb(110, 255, 255, 255));
            using var bp = new Pen(Theme.Blend(Theme.SidebarBg, Color.White, 0.10));
            using var bpp = Theme.RoundedRect(new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1, r.Height - 1), rad); g.DrawPath(bp, bpp);
        }
        void Play(float c, float cy, float rad)
        {
            using (var b = new SolidBrush(Theme.Accent)) g.FillEllipse(b, c - rad, cy - rad, rad * 2, rad * 2);
            using var fg = new SolidBrush(Theme.OnAccent);
            float t = rad * 0.34f;
            g.FillPolygon(fg, new[] { new PointF(c - t + 1.5f, cy - t), new PointF(c - t + 1.5f, cy + t), new PointF(c + t + 1.5f, cy) });
        }
        void Step(float c, float cy, bool next)
        {
            using var b = new SolidBrush(Theme.Subtle);
            float sQ = 5f; int d = next ? 1 : -1;
            g.FillPolygon(b, new[] { new PointF(c - d, cy - sQ), new PointF(c - d, cy + sQ), new PointF(c + d * sQ - d, cy) });
            g.FillRectangle(b, next ? c + sQ - 0.7f : c - sQ - 1.5f, cy - sQ, 2.2f, sQ * 2);
        }
        void Mode(float c, float cy, string glyph, bool active)
        {
            if (Mdl2 is null) return;
            using var f = new Font(Mdl2, 13f, FontStyle.Regular, GraphicsUnit.Pixel);
            using var b = new SolidBrush(active ? Theme.Accent : Theme.Subtle);
            using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            var hint = g.TextRenderingHint; g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
            string gl = string.IsNullOrEmpty(glyph) ? (c < cx ? "" : "") : glyph; // left=shuffle, right=repeat
            g.DrawString(gl, f, b, new RectangleF(c - 13, cy - 13, 26, 26), sf);
            g.TextRenderingHint = hint;
        }
        void Slider(float sx, float sy, float sw, float frac, bool knob)
        {
            using (var tb = new SolidBrush(Theme.Blend(Theme.PanelBg, Color.Black, 0.1)))
            using (var tp = Theme.RoundedRect(new RectangleF(sx, sy, sw, 5), 2.5f)) g.FillPath(tb, tp);
            float fw = sw * Math.Clamp(frac, 0, 1);
            using (var fb = new SolidBrush(Theme.Accent))
            using (var fp = Theme.RoundedRect(new RectangleF(sx, sy, Math.Max(2, fw), 5), 2.5f)) g.FillPath(fb, fp);
            if (knob) { using var kb = new SolidBrush(Color.White); g.FillEllipse(kb, sx + fw - 5, sy + 2.5f - 5, 10, 10); }
        }
        void Time(string t, float rx, float cy, bool right)
        {
            using var f = Theme.UiFont(8f);
            TextRenderer.DrawText(g, t, f, new Rectangle((int)rx, (int)cy - 9, 42, 18), Theme.Faint, (right ? TextFormatFlags.Right : TextFormatFlags.Left) | TextFormatFlags.VerticalCenter);
        }
        void Eq(float ex, float cy)
        {
            using var pen = new Pen(Theme.Subtle, 2f) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round };
            using var dot = new SolidBrush(Theme.Subtle);
            float[] xs = { ex, ex + 6, ex + 12 }; float[] kn = { cy - 1, cy - 5, cy + 1 };
            for (int i = 0; i < 3; i++) { g.DrawLine(pen, xs[i], cy - 9, xs[i], cy + 9); g.FillEllipse(dot, xs[i] - 2.6f, kn[i] - 2.6f, 5.2f, 5.2f); }
        }
        void Speaker(float sx, float cy)
        {
            using var b = new SolidBrush(Theme.Subtle);
            using var p = new Pen(Theme.Subtle, 1.6f) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round };
            g.FillPolygon(b, new[] { new PointF(sx, cy - 3), new PointF(sx + 5, cy - 3), new PointF(sx + 10, cy - 7), new PointF(sx + 10, cy + 7), new PointF(sx + 5, cy + 3), new PointF(sx, cy + 3) });
            g.DrawArc(p, sx + 9, cy - 5, 8, 10, -55, 110);
            g.DrawArc(p, sx + 9, cy - 8, 12, 16, -50, 100);
        }
        void Title(int tx, int ty, int tw, bool centre)
        {
            var fl = (centre ? TextFormatFlags.HorizontalCenter : TextFormatFlags.Left) | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis;
            using (var tf = Theme.UiFont(10.5f, FontStyle.Bold)) TextRenderer.DrawText(g, "3korty", tf, new Rectangle(tx, ty, tw, 20), Theme.TextCol, fl | TextFormatFlags.VerticalCenter);
            using (var sf = Theme.UiFont(8.75f)) TextRenderer.DrawText(g, "Azahriah  •  memento", sf, new Rectangle(tx, ty + 20, tw, 16), Theme.Subtle, fl);
        }

        // transport cluster, centred on cx -------------------------------------
        void Transport(float ty)
        {
            Play(cx, ty, 20);
            Step(cx - 50, ty, false); Step(cx + 50, ty, true);
            Mode(cx - 92, ty, "", false); Mode(cx + 92, ty, "", false);
        }

        // right cluster (EQ • speaker • volume) --------------------------------
        void RightCluster(float cy)
        {
            float rx = x + w - 20;
            Slider(rx - 92, cy - 2, 92, 0.7f, false); rx -= 92 + 14;
            Speaker(rx - 20, cy); rx -= 20 + 16;
            Eq(rx - 18, cy);
        }

        Tile(cr);
        switch (style)
        {
            case 0: // A — centred transport, scrubber centred below
                Title(cr.Right + 12, y + 18, (int)(cx - 92 - 26) - (cr.Right + 12) - 12, false);
                Transport(y + 30);
                {
                    float sw = 420; float sx = cx - sw / 2;
                    Slider(sx, y + 60, sw, 0.36f, true);
                    Time("1:12", sx - 46, y + 62, true); Time("3:48", sx + sw + 6, y + 62, false);
                }
                RightCluster(y + 30);
                break;

            case 1: // B — centred transport, full-width progress line at the very bottom
                Title(cr.Right + 12, y + (h - 36) / 2, (int)(cx - 92 - 26) - (cr.Right + 12) - 12, false);
                Transport(y + h / 2f - 3);
                RightCluster(y + h / 2f - 3);
                // full-width progress line
                using (var tb = new SolidBrush(Theme.Blend(Theme.PanelBg, Color.Black, 0.1))) g.FillRectangle(tb, x, y + h - 3, w, 3);
                using (var fb = new SolidBrush(Theme.Accent)) g.FillRectangle(fb, x, y + h - 3, w * 0.36f, 3);
                break;

            default: // C — fully centred (single-line title above, transport, scrubber centred below)
                using (var tf = Theme.UiFont(10f, FontStyle.Bold))
                    TextRenderer.DrawText(g, "3korty   •   Azahriah", tf, new Rectangle((int)(cx - 220), y + 8, 440, 16), Theme.TextCol,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
                Transport(y + 46);
                { float sw = 360; Slider(cx - sw / 2, y + h - 13, sw, 0.36f, true); }
                RightCluster(y + 24);
                break;
        }
    }

    /// <summary>Print the entire cover-art chain for a connected iPod and flag where it breaks.</summary>
    private static void RunArtworkDiag(string root)
    {
        string outp = Path.Combine(AppContext.BaseDirectory, "ipod-artworkdiag.txt");
        var log = new StringBuilder();
        void L(string s = "") => log.AppendLine(s);

        try
        {
            L("=== Mixtape cover-art diagnostic ===");
            L("root: " + root);
            L();

            var dev = DeviceDetector.Build(root);
            if (dev is null) { L("No iPod_Control found at this path — is the iPod mounted there?"); File.WriteAllText(outp, log.ToString()); Console.WriteLine(log.ToString()); return; }
            var p = dev.Profile;

            L("--- 1. Device detection ---");
            L("Model:        " + (p.ModelName ?? "(unknown)"));
            L("ModelNumber:  " + (p.ModelNumber ?? "(none)"));
            L("Generation:   " + p.Generation + (p.IdentifiedBy is { } by ? $"  [identified by {by}]" : "  [NOT identified]"));
            L("Signature:    " + p.Scheme);
            L("CanWrite:     " + p.CanWrite + (p.WriteBlockReason.Length > 0 ? "  (" + p.WriteBlockReason + ")" : ""));
            L("FireWireGUID: " + (string.IsNullOrEmpty(p.FirewireGuid) ? "(none)" : "present"));
            L("SupportsPhotos:  " + p.SupportsPhotos);
            L("SupportsVideo:   " + p.SupportsVideo);
            L("SupportsArtwork: " + p.SupportsArtwork + (p.SupportsArtwork ? "" : "   <-- if false, NO cover art is ever written"));
            L();

            L("--- 2. Formats this generation would write ---");
            var genFmts = ArtworkFormats.For(p.Generation);
            L("ArtworkFormats.For(" + p.Generation + "): " + (genFmts.Length == 0 ? "(EMPTY — unknown/unsupported generation)" : string.Join(", ", genFmts.Select(f => $"F{f.FormatId} {f.Width}x{f.Height}"))));

            var sink = ArtworkLibrary.Load(dev);
            L("ArtworkLibrary.SafeToWrite:    " + sink.SafeToWrite);
            L("ArtworkLibrary.SupportsArtwork: " + sink.SupportsArtwork + (sink.SupportsArtwork ? "" : "   <-- the actual gate AddMediaFile checks"));
            L("ArtworkLibrary will add in:    " + (sink.AddFormats.Length == 0 ? "(NOTHING — no formats resolved)" : string.Join(", ", sink.AddFormats.Select(f => $"F{f.FormatId} {f.Width}x{f.Height}"))));
            L();

            L("--- 3. Existing Artwork folder ---");
            string artDir = Path.Combine(root, "iPod_Control", "Artwork");
            L("path: " + artDir + (Directory.Exists(artDir) ? "" : "   (MISSING)"));
            string dbPath = Path.Combine(artDir, "ArtworkDB");
            ArtworkDbModel? model = null;
            if (Directory.Exists(artDir))
            {
                foreach (var f in Directory.GetFiles(artDir))
                    L($"  {Path.GetFileName(f),-24} {new FileInfo(f).Length,12:n0} bytes");
                if (File.Exists(dbPath))
                {
                    try
                    {
                        model = ArtworkDb.Parse(File.ReadAllBytes(dbPath));
                        L($"ArtworkDB parsed: {model.Items.Count} image(s), maxId={model.MaxId}" + (model.Warnings.Count > 0 ? "  WARNINGS: " + string.Join(" | ", model.Warnings) : ""));
                        var byFmt = model.Items.SelectMany(it => it.Thumbs).GroupBy(t => t.FormatId).OrderBy(g => g.Key);
                        foreach (var g in byFmt) L($"  format F{g.Key}: {g.Count()} thumb(s), sizes {string.Join("/", g.Select(t => t.Size).Distinct())}");
                    }
                    catch (Exception ex) { L("ArtworkDB parse FAILED: " + ex.Message); }
                }
                else L("ArtworkDB: (none yet)");
            }
            L();

            L("--- 4. iTunesDB track flags ---");
            byte[] dbBytes = File.ReadAllBytes(dev.ITunesDbPath);
            var raw = RawDb.Parse(dbBytes);
            var mhits = raw.Datasets.FirstOrDefault(d => d.Type == 1)?.Tracks ?? new List<byte[]>();
            int withArt = 0, withLink = 0;
            var trackDbids = new HashSet<ulong>();
            var artFlaggedDbids = new List<ulong>();
            foreach (var mhit in mhits)
            {
                if (mhit.Length < 0x164) continue;
                ulong dbid = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(mhit.AsSpan(0x70, 8));
                byte hasArt = mhit[0xA4];
                uint mhii = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(mhit.AsSpan(0x160, 4));
                trackDbids.Add(dbid);
                if (hasArt == 1) { withArt++; artFlaggedDbids.Add(dbid); }
                if (mhii != 0) withLink++;
            }
            L($"tracks: {mhits.Count}   has_artwork=1: {withArt}   mhii_link!=0: {withLink}");
            // Name the tracks that currently carry artwork (so they can be checked on the iPod screen).
            try
            {
                var flaggedIds = new HashSet<uint>();
                foreach (var mhit in mhits)
                    if (mhit.Length > 0xA4 && mhit[0xA4] == 1)
                        flaggedIds.Add(System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(mhit.AsSpan(0x10, 4)));
                var view = IpodLibrary.Load(dev).View;
                foreach (var t in view.Tracks)
                    if (flaggedIds.Contains(t.UniqueId))
                        L($"   has art → {t.Artist} — {t.Title}");
            }
            catch { }
            L();

            L("--- 5. Cross-check links (the smoking gun) ---");
            if (model is null) L("No ArtworkDB to cross-check.");
            else
            {
                int artMatched = 0, artOrphan = 0;
                foreach (var it in model.Items)
                    if (trackDbids.Contains(it.TrackDbid)) artMatched++; else artOrphan++;
                L($"ArtworkDB images whose TrackDbid matches a real track: {artMatched}");
                L($"ArtworkDB images with NO matching track (orphans):       {artOrphan}");
                var artDbidSet = model.Items.Select(it => it.TrackDbid).ToHashSet();
                int flaggedWithArt = artFlaggedDbids.Count(d => artDbidSet.Contains(d));
                L($"Tracks flagged has_artwork=1 that HAVE a matching image:  {flaggedWithArt} / {withArt}");
                if (withArt > 0 && flaggedWithArt < withArt) L("  <-- some flagged tracks have no image in the ArtworkDB (link broken or art not written)");
                if (withArt == 0) L("  <-- NO track is flagged has_artwork: art was never written on copy (capability gate or old build)");
            }

            File.WriteAllText(outp, log.ToString());
            Console.WriteLine(log.ToString());
        }
        catch (Exception ex)
        {
            log.AppendLine().AppendLine("DIAG ERROR: " + ex);
            File.WriteAllText(outp, log.ToString());
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rc);
    private struct RECT { public int Left, Top, Right, Bottom; }

    /// <summary>Render the themed context menu to a PNG so its look can be reviewed without a device.</summary>
    private static void RunMenuPreview(string outPng)
    {
        try { RunMenuPreviewCore(outPng); }
        catch (Exception ex) { File.WriteAllText(Path.ChangeExtension(outPng, ".err.txt"), ex.ToString()); }
    }

    private static void RunMenuPreviewCore(string outPng)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.SystemAware);

        var settings = AppSettings.Load();
        Theme.SetThemeVariant(settings.ThemeVariant); // match the live background (e.g. Forest)
        Theme.SetAccent(settings.Accent);             // and the accent so the hover pill colour is accurate

        using var host = new Form
        {
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-3000, -3000),
            Size = new Size(420, 320),
            FormBorderStyle = FormBorderStyle.None,
            BackColor = Theme.Bg,
        };
        host.Show();
        Application.DoEvents();

        // Mirror the song-row right-click menu from the screenshot (incl. a submenu).
        var m = ThemedMenu.New();
        m.Items.Add(new ToolStripMenuItem("Play"));
        m.Items.Add(new ToolStripMenuItem("Copy to PC…   (1)"));
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add(new ToolStripMenuItem("Edit info…"));
        m.Items.Add(new ToolStripSeparator());
        var addTo = new ToolStripMenuItem("Add to playlist");
        addTo.DropDownItems.Add(new ToolStripMenuItem("Summer 2024"));
        addTo.DropDownItems.Add(new ToolStripMenuItem("Workout"));
        addTo.DropDownItems.Add(new ToolStripSeparator());
        addTo.DropDownItems.Add(new ToolStripMenuItem("New playlist…"));
        m.Items.Add(addTo);
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add(new ToolStripMenuItem("Delete from iPod   (1)"));

        m.Show(host, new Point(30, 30));
        // Capture the FIRST-PAINT state faithfully (minimal pumping, no forced re-layout) — the way the
        // live app shows it. Then open the submenu so it's captured too.
        Application.DoEvents(); Thread.Sleep(40); Application.DoEvents();
        addTo.ShowDropDown();
        Application.DoEvents(); Thread.Sleep(40); Application.DoEvents();

        var sub = addTo.DropDown;
        GetWindowRect(m.Handle, out var mr);
        GetWindowRect(sub.Handle, out var sr);
        int baseX = Math.Min(mr.Left, sr.Left), baseY = Math.Min(mr.Top, sr.Top);
        int totalW = Math.Max(mr.Right, sr.Right) - baseX, totalH = Math.Max(mr.Bottom, sr.Bottom) - baseY;

        using var outb = new Bitmap(totalW + 48, totalH + 48);
        using (var g = Graphics.FromImage(outb))
        {
            g.Clear(Theme.Bg);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            DrawPopup(g, m.Handle, mr.Left - baseX + 24, mr.Top - baseY + 24);
            DrawPopup(g, sub.Handle, sr.Left - baseX + 24, sr.Top - baseY + 24);
        }
        outb.Save(outPng, System.Drawing.Imaging.ImageFormat.Png);
        m.Close();
        host.Close();

        // Capture one popup window via PrintWindow and paint it (rounded-clipped so the corner pixels
        // outside the region don't show as black) onto the composite at (dx,dy).
        static void DrawPopup(Graphics g, IntPtr hwnd, int dx, int dy)
        {
            GetWindowRect(hwnd, out var rc);
            int w = Math.Max(1, rc.Right - rc.Left), h = Math.Max(1, rc.Bottom - rc.Top);
            using var shot = new Bitmap(w, h);
            using (var sg = Graphics.FromImage(shot))
            {
                IntPtr hdc = sg.GetHdc();
                PrintWindow(hwnd, hdc, 2); // PW_RENDERFULLCONTENT
                sg.ReleaseHdc(hdc);
            }
            var state = g.Save();
            using var clip = Theme.RoundedRect(new RectangleF(dx, dy, w, h), MenuStyle.Radius);
            g.SetClip(clip);
            g.DrawImage(shot, dx, dy);
            g.Restore(state);
        }
    }

    private static void RunPhotoDemo(string root, int count)
    {
        var log = new StringBuilder();
        try
        {
            var device = DeviceDetector.Build(root);
            if (device is null) { log.AppendLine($"No iPod_Control under '{root}'."); }
            else if (!device.Profile.SupportsPhotos) { log.AppendLine($"Device generation {device.Profile.Generation} does not support photos."); }
            else
            {
                var lib = PhotoLibrary.Load(device);
                log.AppendLine($"before: {lib.Photos.Count} photos; safeToWrite={lib.SafeToWrite}");
                string tmp = Path.Combine(Path.GetTempPath(), "mixtape-photodemo");
                Directory.CreateDirectory(tmp);
                for (int i = 0; i < count; i++)
                {
                    string p = Path.Combine(tmp, $"demo{i}.png");
                    MakeDemoImage(p, i, count);
                    lib.AddPhoto(p);
                }
                lib.Save();
                log.AppendLine($"after : {lib.Photos.Count} photos written + reloaded");
                log.AppendLine($"thumbs: " + string.Join(", ", Directory.GetFiles(Path.Combine(root, "Photos", "Thumbs"), "F*.ithmb").Select(Path.GetFileName)));
                log.AppendLine("RESULT: OK");
            }
        }
        catch (Exception ex) { log.AppendLine("RESULT: FAILED - " + ex); }
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "ipod-photodemo.txt"), log.ToString());
    }

    private static void MakeDemoImage(string path, int i, int total)
    {
        int w = 480 + (i % 3) * 80, h = 360 + (i % 2) * 120; // varied aspect ratios
        using var bmp = new Bitmap(w, h);
        using (var g = Graphics.FromImage(bmp))
        {
            double hue = i / (double)Math.Max(1, total) * 360;
            using var br = new System.Drawing.Drawing2D.LinearGradientBrush(new Rectangle(0, 0, w, h),
                Theme.HsvToColor(hue, 0.6, 0.85), Theme.HsvToColor(hue + 40, 0.7, 0.5), 35f);
            g.FillRectangle(br, 0, 0, w, h);
            using var f = new Font("Segoe UI", h * 0.3f, FontStyle.Bold);
            var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString((i + 1).ToString(), f, Brushes.White, new RectangleF(0, 0, w, h), sf);
        }
        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    private static void RunAdd(string root, string file)
    {
        var log = new StringBuilder();
        try
        {
            var device = DeviceDetector.Build(root);
            if (device is null) { log.AppendLine($"No iPod_Control under '{root}'."); }
            else if (!device.Profile.CanWrite) { log.AppendLine("Device is read-only: " + device.Profile.WriteBlockReason); }
            else
            {
                var lib = IpodLibrary.Load(device);
                int before = lib.View.Tracks.Count;
                var beforeIds = lib.View.Tracks.Select(t => t.UniqueId).ToHashSet();
                log.AppendLine($"device : {device.DisplayName}");
                log.AppendLine($"before : {before} tracks");

                lib.AddFile(file);
                lib.Save();

                var nt = lib.View.Tracks.FirstOrDefault(t => !beforeIds.Contains(t.UniqueId));
                log.AppendLine($"after  : {lib.View.Tracks.Count} tracks");
                log.AppendLine(nt is null
                    ? "WARNING: new track not found after save"
                    : $"ADDED  : '{nt.Title}' by '{nt.Artist}' ({nt.Duration:mm\\:ss}) {nt.Bitrate}kbps {nt.SampleRate}Hz  uid={nt.UniqueId}  @ {nt.Location}");
                log.AppendLine($"in master playlist: {(nt is not null && lib.View.Master?.TrackIds.Contains(nt.UniqueId) == true)}");
            }
            log.AppendLine("RESULT: OK");
        }
        catch (Exception ex) { log.AppendLine("RESULT: FAILED - " + ex); }
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "ipod-add.txt"), log.ToString());
    }

    private static void RunRemove(string root, string uidStr)
    {
        var log = new StringBuilder();
        try
        {
            var device = DeviceDetector.Build(root);
            if (device is null) { log.AppendLine($"No iPod_Control under '{root}'."); }
            else if (!uint.TryParse(uidStr, out uint uid)) { log.AppendLine("Invalid uniqueId: " + uidStr); }
            else
            {
                var lib = IpodLibrary.Load(device);
                int before = lib.View.Tracks.Count;
                var t = lib.View.FindByUniqueId(uid);
                log.AppendLine($"before : {before} tracks; target uid={uid} ({t?.DisplayTitle ?? "not found"})");
                lib.DeleteTrack(uid, deleteFile: true);
                lib.Save();
                log.AppendLine($"after  : {lib.View.Tracks.Count} tracks; gone={lib.View.FindByUniqueId(uid) is null}");
            }
            log.AppendLine("RESULT: OK");
        }
        catch (Exception ex) { log.AppendLine("RESULT: FAILED - " + ex); }
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "ipod-remove.txt"), log.ToString());
    }

    private static void RunAddTest(string sourceAudio, string fixtureDb)
    {
        var log = new StringBuilder();
        int failures = 0;
        void Check(bool ok, string what) { log.AppendLine((ok ? "PASS " : "FAIL ") + what); if (!ok) failures++; }

        try
        {
            // 1) Build a throwaway sandbox iPod tree.
            string sandbox = Path.Combine(Path.GetTempPath(), "ipodcmd-sandbox");
            if (Directory.Exists(sandbox)) Directory.Delete(sandbox, recursive: true);
            string control = Path.Combine(sandbox, "iPod_Control");
            Directory.CreateDirectory(Path.Combine(control, "iTunes"));
            Directory.CreateDirectory(Path.Combine(control, "Device"));
            for (int i = 0; i < 10; i++) Directory.CreateDirectory(Path.Combine(control, "Music", $"F{i:00}"));
            File.Copy(fixtureDb, Path.Combine(control, "iTunes", "iTunesDB"));
            File.WriteAllText(Path.Combine(control, "Device", "SysInfo"), "ModelNumStr: M9807\n");
            log.AppendLine($"sandbox: {sandbox}");
            log.AppendLine($"source : {sourceAudio}  ({new FileInfo(sourceAudio).Length:N0} bytes)");

            var device = DeviceDetector.Build(sandbox);
            Check(device is not null, "sandbox recognised as an iPod");
            Check(device!.Profile.CanWrite, $"sandbox writable (scheme {device.Profile.SchemeLabel})");

            var lib = IpodLibrary.Load(device);
            int before = lib.View.Tracks.Count;
            var beforeIds = lib.View.Tracks.Select(t => t.UniqueId).ToHashSet();
            log.AppendLine($"before: {before} tracks");

            // 2) Add the real audio file.
            string title = lib.AddFile(sourceAudio);
            lib.Save();
            int after = lib.View.Tracks.Count;
            Check(after == before + 1, $"track count after add = {after} (expected {before + 1})");

            var newTrack = lib.View.Tracks.FirstOrDefault(t => !beforeIds.Contains(t.UniqueId));
            Check(newTrack is not null, $"new track present (title '{title}')");
            if (newTrack is not null)
            {
                log.AppendLine($"  new: '{newTrack.Title}' by '{newTrack.Artist}'  {newTrack.Duration:mm\\:ss}  {newTrack.Bitrate}kbps  {newTrack.SampleRate}Hz  @ {newTrack.Location}");
                Check(newTrack.LengthMs > 0, $"duration read from tags = {newTrack.LengthMs} ms");
                string? fp = newTrack.ResolveFilePath(device.MountRoot);
                Check(fp is not null && File.Exists(fp), $"audio file copied to {fp}");
                Check(lib.View.Master is not null && lib.View.Master!.TrackIds.Contains(newTrack.UniqueId), "new track is in the master playlist");
            }

            // 3) Delete it again — counts return, file removed.
            uint newId = newTrack?.UniqueId ?? 0;
            string? path = newTrack?.ResolveFilePath(device.MountRoot);
            lib.DeleteTrack(newId, deleteFile: true);
            lib.Save();
            Check(lib.View.Tracks.Count == before, $"track count after delete = {lib.View.Tracks.Count} (expected {before})");
            Check(path is null || !File.Exists(path), "audio file removed on delete");
            Check(lib.View.Tracks.All(t => t.UniqueId != newId), "deleted track is gone from the db");

            Directory.Delete(sandbox, recursive: true);
            log.AppendLine();
            log.AppendLine(failures == 0 ? "RESULT: OK" : $"RESULT: FAILED ({failures})");
        }
        catch (Exception ex)
        {
            log.AppendLine("RESULT: FAILED - " + ex);
        }
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "ipod-addtest.txt"), log.ToString());
    }

    private static void RunPlTest(string pathOrRoot)
    {
        var log = new StringBuilder();
        int failures = 0;
        void Check(bool ok, string what) { log.AppendLine((ok ? "PASS " : "FAIL ") + what); if (!ok) failures++; }

        try
        {
            string dbPath = File.Exists(pathOrRoot)
                ? pathOrRoot
                : (DeviceDetector.Build(pathOrRoot)?.ITunesDbPath ?? Path.Combine(pathOrRoot, "iPod_Control", "iTunes", "iTunesDB"));
            byte[] orig = File.ReadAllBytes(dbPath);
            var before = ITunesDbReader.Read(orig);
            int trackCount = before.Tracks.Count;
            var target = before.Playlists.FirstOrDefault(p => !p.IsMaster && p.TrackIds.Count > 0);
            if (target is null) { log.AppendLine("No non-empty user playlist to test against."); }
            else
            {
                ulong pid = target.PersistentId;
                var members = target.TrackIds.Distinct().ToList();
                log.AppendLine($"target: '{target.Name}' pid=0x{pid:X}, {members.Count} members; library {trackCount} tracks");

                // 1) delete playlist, keep songs
                var raw = RawDb.Parse(orig);
                Check(raw.RemovePlaylist(pid), "RemovePlaylist reported a change");
                var d = ITunesDbReader.Read(raw.Serialize());
                Check(d.Tracks.Count == trackCount, $"library tracks unchanged after delete ({d.Tracks.Count})");
                Check(!d.Playlists.Any(p => p.PersistentId == pid), "playlist gone from every dataset");
                Check(members.All(id => d.FindByUniqueId(id) != null), "every member song still in the library");

                // 2) remove some tracks from the playlist, keep songs
                var raw2 = RawDb.Parse(orig);
                var some = new HashSet<uint>(members.Take(Math.Min(3, members.Count)));
                raw2.RemoveTracksFromPlaylist(pid, some);
                var r2 = ITunesDbReader.Read(raw2.Serialize());
                var tgt2 = r2.Playlists.FirstOrDefault(p => p.PersistentId == pid);
                Check(r2.Tracks.Count == trackCount, "library tracks unchanged after remove-from-playlist");
                Check(tgt2 is not null && some.All(id => !tgt2.TrackIds.Contains(id)), "removed members gone from the playlist");
                Check(some.All(id => r2.FindByUniqueId(id) != null), "removed members still in the library");

                // 3) rename
                var raw3 = RawDb.Parse(orig);
                raw3.RenamePlaylist(pid, "Renamed Test PL");
                var r3 = ITunesDbReader.Read(raw3.Serialize());
                Check(r3.Tracks.Count == trackCount, "library tracks unchanged after rename");
                Check(r3.Playlists.Any(p => p.PersistentId == pid && p.Name == "Renamed Test PL"), "playlist renamed in the db");
                var rtgt = r3.Playlists.FirstOrDefault(p => p.PersistentId == pid);
                Check(rtgt is not null && rtgt.TrackIds.Count == target.TrackIds.Count, "rename preserved the members");

                // 4) create a new playlist + add tracks (the "make a mixtape" path)
                var raw5 = RawDb.Parse(orig);
                ulong newPid = raw5.CreatePlaylist("Mixtape Test");
                raw5.AddTracksToPlaylist(newPid, members.Take(3));
                var r5 = ITunesDbReader.Read(raw5.Serialize());
                Check(r5.Tracks.Count == trackCount, "library tracks unchanged after create + add");
                var created = r5.Playlists.FirstOrDefault(p => p.Name == "Mixtape Test");
                Check(created is not null, "new playlist created");
                Check(created is not null && members.Take(3).All(id => created.TrackIds.Contains(id)), "added tracks are in the new playlist");
                Check(r5.Playlists.Count(p => p.Name == "Mixtape Test") == 2, "new playlist mirrored into both playlist datasets");

                // 5) no-op round trip still byte-identical
                Check(RawDb.Parse(orig).Serialize().AsSpan().SequenceEqual(orig), "no-op round trip still byte-identical");
            }
            log.AppendLine();
            log.AppendLine(failures == 0 ? "RESULT: OK" : $"RESULT: FAILED ({failures})");
        }
        catch (Exception ex) { log.AppendLine("RESULT: FAILED - " + ex); }
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "ipod-pltest.txt"), log.ToString());
    }

    private static void RunMutateTest(string pathOrRoot)
    {
        var log = new StringBuilder();
        int failures = 0;
        void Check(bool ok, string what) { log.AppendLine((ok ? "PASS " : "FAIL ") + what); if (!ok) failures++; }

        try
        {
            string dbPath = File.Exists(pathOrRoot)
                ? pathOrRoot
                : (DeviceDetector.Build(pathOrRoot)?.ITunesDbPath ?? Path.Combine(pathOrRoot, "iPod_Control", "iTunes", "iTunesDB"));
            byte[] orig = File.ReadAllBytes(dbPath);
            var before = ITunesDbReader.Read(orig);
            log.AppendLine($"source: {dbPath} — {before.Tracks.Count} tracks, {before.Playlists.Count} playlists");

            // 1) ADD a synthetic track.
            var raw = RawDb.Parse(orig);
            uint newId = raw.MaxUniqueId() + 1;
            const string testTitle = "iPodCommander Test Track";
            var nt = new NewTrack
            {
                UniqueId = newId,
                Dbid = 0x7E57_0000_0000_0001UL,
                Title = testTitle,
                Artist = "Claude",
                Album = "Round Trip",
                Genre = "Test",
                FileTypeDescription = "MPEG audio file",
                Location = ":iPod_Control:Music:F00:ZZTEST.mp3",
                FileSize = 1234567,
                LengthMs = 222000,
                Bitrate = 320,
                SampleRate = 44100,
                Year = 2026,
                TrackNumber = 1,
            };
            raw.AddTrack(RawDb.BuildMhitChunk(nt), newId);
            byte[] added = raw.Serialize();

            var after = ITunesDbReader.Read(added);
            Check(after.Tracks.Count == before.Tracks.Count + 1, $"track count after add = {after.Tracks.Count} (expected {before.Tracks.Count + 1})");
            var newTrack = after.Tracks.FirstOrDefault(t => t.UniqueId == newId);
            Check(newTrack is not null, $"new track present (uniqueId {newId})");
            if (newTrack is not null)
            {
                Check(newTrack.Title == testTitle, $"new title = '{newTrack.Title}'");
                Check(newTrack.Artist == "Claude", $"new artist = '{newTrack.Artist}'");
                Check(newTrack.Bitrate == 320 && newTrack.SampleRate == 44100, $"new bitrate/rate = {newTrack.Bitrate}/{newTrack.SampleRate}");
                Check(newTrack.Location == nt.Location, $"new location = '{newTrack.Location}'");
            }
            int mastersWithNew = after.Playlists.Count(p => p.IsMaster && p.TrackIds.Contains(newId));
            int masterCount = after.Playlists.Count(p => p.IsMaster);
            Check(mastersWithNew == masterCount && masterCount > 0, $"new track in all {masterCount} master playlists (got {mastersWithNew})");
            // every original track must still be present
            var beforeIds = before.Tracks.Select(t => t.UniqueId).ToHashSet();
            var afterIds = after.Tracks.Select(t => t.UniqueId).ToHashSet();
            Check(beforeIds.IsSubsetOf(afterIds), "all original tracks still present after add");

            // 2) REMOVE it again — the DB must return byte-for-byte to the original.
            var raw2 = RawDb.Parse(added);
            bool removed = raw2.RemoveTrack(newId);
            Check(removed, "RemoveTrack reported success");
            byte[] restored = raw2.Serialize();
            bool identical = restored.Length == orig.Length && restored.AsSpan().SequenceEqual(orig);
            Check(identical, $"add+remove restores the original byte-for-byte ({restored.Length:N0} vs {orig.Length:N0} bytes)");
            if (!identical)
            {
                int n = Math.Min(orig.Length, restored.Length), fd = -1;
                for (int i = 0; i < n; i++) if (orig[i] != restored[i]) { fd = i; break; }
                log.AppendLine($"   first diff at {(fd < 0 ? "n/a (length only)" : "0x" + fd.ToString("X"))}");
            }

            log.AppendLine();
            log.AppendLine(failures == 0 ? "RESULT: OK" : $"RESULT: FAILED ({failures})");
        }
        catch (Exception ex)
        {
            log.AppendLine("RESULT: FAILED - " + ex);
        }
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "ipod-mutatetest.txt"), log.ToString());
    }

    private static void RunRoundtrip(string pathOrRoot)
    {
        var log = new StringBuilder();
        try
        {
            string dbPath = File.Exists(pathOrRoot)
                ? pathOrRoot
                : (DeviceDetector.Build(pathOrRoot)?.ITunesDbPath ?? Path.Combine(pathOrRoot, "iPod_Control", "iTunes", "iTunesDB"));
            byte[] orig = File.ReadAllBytes(dbPath);
            log.AppendLine($"source: {dbPath}  ({orig.Length:N0} bytes)");

            var raw = RawDb.Parse(orig);
            log.AppendLine($"parsed: version=0x{raw.Version:X}, {raw.Datasets.Count} datasets");
            foreach (var ds in raw.Datasets)
            {
                string kind = ds.Verbatim is not null ? $"verbatim ({ds.Verbatim.Length:N0} B)"
                    : ds.Tracks is not null ? $"{ds.Tracks.Count} tracks"
                    : $"{ds.Playlists!.Count} playlists";
                log.AppendLine($"  dataset type {ds.Type}: {kind}");
            }

            byte[] rebuilt = raw.Serialize();
            bool sameLen = orig.Length == rebuilt.Length;
            int firstDiff = -1;
            int n = Math.Min(orig.Length, rebuilt.Length);
            for (int i = 0; i < n; i++) { if (orig[i] != rebuilt[i]) { firstDiff = i; break; } }
            bool identical = sameLen && firstDiff < 0;

            log.AppendLine();
            log.AppendLine($"rebuilt: {rebuilt.Length:N0} bytes (orig {orig.Length:N0})");
            if (identical)
            {
                log.AppendLine("BYTE-IDENTICAL ✓  — the writer reproduces the database exactly.");
            }
            else
            {
                log.AppendLine($"DIFFERENCE: sameLength={sameLen}, firstDiffOffset={(firstDiff < 0 ? "n/a" : "0x" + firstDiff.ToString("X"))}");
                if (firstDiff >= 0)
                {
                    int s = Math.Max(0, firstDiff - 8), e = Math.Min(n, firstDiff + 8);
                    log.AppendLine($"  orig   [{s:X}..{e:X}): {BitConverter.ToString(orig, s, e - s)}");
                    log.AppendLine($"  rebuilt[{s:X}..{e:X}): {BitConverter.ToString(rebuilt, s, e - s)}");
                }
            }

            // Semantic cross-check: the rebuilt bytes must still parse to the same counts.
            var a = ITunesDbReader.Read(orig);
            var b = ITunesDbReader.Read(rebuilt);
            log.AppendLine();
            log.AppendLine($"semantic: orig {a.Tracks.Count} tracks / {a.Playlists.Count} playlists  vs  rebuilt {b.Tracks.Count} / {b.Playlists.Count}");

            log.AppendLine();
            log.AppendLine(identical && a.Tracks.Count == b.Tracks.Count ? "RESULT: OK" : "RESULT: FAILED");
        }
        catch (Exception ex)
        {
            log.AppendLine("RESULT: FAILED - " + ex);
        }
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "ipod-roundtrip.txt"), log.ToString());
    }

    private static void RunInspect(string mountRoot)
    {
        var log = new StringBuilder();
        try
        {
            var device = DeviceDetector.Build(mountRoot);
            string dbPath = device?.ITunesDbPath ?? Path.Combine(mountRoot, "iPod_Control", "iTunes", "iTunesDB");
            byte[] data = File.ReadAllBytes(dbPath);
            var r = new ChunkReader(data);

            log.AppendLine($"file : {dbPath}  ({data.Length} bytes)");
            log.AppendLine($"mhbd : headerLen=0x{r.U32(4):X} totalLen={r.U32(8)} version=0x{r.U32(0x10):X} datasets={r.U32(0x14)} pid=0x{r.U64(0x48):X16} hashingScheme@0x30={r.U16(0x30)}");

            int off = (int)r.U32(4);
            uint n = r.U32(0x14);
            for (uint i = 0; i < n && off + 16 <= r.Length; i++)
            {
                if (r.Tag(off) != "mhsd") { log.AppendLine($"  [dataset {i}] expected mhsd, got '{r.Tag(off)}' — stop"); break; }
                uint hl = r.U32(off + 4), tl = r.U32(off + 8), type = r.U32(off + 0x0C);
                int listOff = off + (int)hl;
                string listTag = r.Tag(listOff);
                uint listHl = r.U32(listOff + 4), childCount = r.U32(listOff + 8);
                log.AppendLine($"  [dataset {i}] type={type} headerLen=0x{hl:X} totalLen={tl}  ->  {listTag} headerLen=0x{listHl:X} childCount={childCount}");

                int c = listOff + (int)listHl;
                if (childCount > 0 && c + 16 <= r.Length)
                {
                    string ct = r.Tag(c);
                    uint chl = r.U32(c + 4), ctl = r.U32(c + 8), cmhod = r.U32(c + 0x0C);
                    log.AppendLine($"      first child: {ct} headerLen=0x{chl:X} totalLen={ctl} mhodCount={cmhod}");
                    if (ct == "mhit")
                    {
                        int m = c + (int)chl;
                        for (uint k = 0; k < cmhod && k < 12 && m + 16 <= r.Length && r.Tag(m) == "mhod"; k++)
                        {
                            log.AppendLine($"        mhod type={r.U32(m + 0x0C)} headerLen=0x{r.U32(m + 4):X} totalLen={r.U32(m + 8)} byteLen@0x1C={r.U32(m + 0x1C)}");
                            uint mt = r.U32(m + 8); if (mt == 0) break; m += (int)mt;
                        }
                    }
                    else if (ct == "mhyp")
                    {
                        log.AppendLine($"        mhyp isMaster={r.U8(c + 0x14)} mhipCount={r.U32(c + 0x10)} podcastFlag@0x2A={r.U16(c + 0x2A)} sortOrder@0x2C={r.U32(c + 0x2C)}");
                        int m = c + (int)chl;
                        for (uint k = 0; k < cmhod && m + 16 <= r.Length && r.Tag(m) == "mhod"; k++)
                        {
                            log.AppendLine($"        name mhod type={r.U32(m + 0x0C)} headerLen=0x{r.U32(m + 4):X} totalLen={r.U32(m + 8)}");
                            uint mt = r.U32(m + 8); if (mt == 0) break; m += (int)mt;
                        }
                        if (m + 16 <= r.Length && r.Tag(m) == "mhip")
                            log.AppendLine($"        first mhip headerLen=0x{r.U32(m + 4):X} totalLen={r.U32(m + 8)} mhodCount={r.U32(m + 0x0C)} trackId={r.U32(m + 0x18)}");
                    }
                }
                if (tl == 0) break;
                off += (int)tl;
            }
            log.AppendLine("RESULT: OK");
        }
        catch (Exception ex)
        {
            log.AppendLine("RESULT: FAILED - " + ex);
        }
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "ipod-inspect.txt"), log.ToString());
    }

    private static void RunPhotoTest()
    {
        var log = new StringBuilder();
        int failures = 0;
        void Check(bool ok, string what) { log.AppendLine((ok ? "PASS " : "FAIL ") + what); if (!ok) failures++; }

        try
        {
            // 1) RGB565 slot encode/decode round-trip (the heart of .ithmb).
            using (var src = new Bitmap(100, 80))
            {
                using (var g = Graphics.FromImage(src)) g.Clear(Color.FromArgb(230, 30, 20)); // a strong red
                var fmt = new PhotoFormat(1024, 320, 240, true);
                var slot = Ithmb.Encode(src, fmt);
                Check(slot.Pixels.Length == 320 * 240 * 2, $"slot size = {slot.Pixels.Length} (expected {320 * 240 * 2})");
                // 100x80 scaled to fit 320x240 → 300x240, so 10px black bars left/right.
                Check(slot.HPad == 10 && slot.VPad == 0, $"letterbox padding h={slot.HPad} v={slot.VPad} (expected 10/0)");
                Check(slot.ImageWidth == 310 && slot.ImageHeight == 240, $"image region {slot.ImageWidth}x{slot.ImageHeight} (expected 310x240)");
                using var dec = Ithmb.Decode(slot.Pixels, 320, 240);
                Check(dec is not null, "decoded slot back to a bitmap");
                if (dec is not null)
                {
                    var center = dec.GetPixel(160, 120);
                    var leftBar = dec.GetPixel(2, 120);
                    Check(center.R > 200 && center.G < 70 && center.B < 70, $"centre pixel ≈ red (got {center.R},{center.G},{center.B})");
                    Check(leftBar.R < 20 && leftBar.G < 20 && leftBar.B < 20, $"left letterbox pixel ≈ black (got {leftBar.R},{leftBar.G},{leftBar.B})");
                }
            }

            // 2) Photo Database build → parse round-trip.
            var model = new PhotoDbModel { MaxImageId = 65 };
            model.Photos.Add(MakeTestPhoto(64, (1036, 50, 41, 0), (1024, 320, 240, 100)));
            model.Photos.Add(MakeTestPhoto(65, (1036, 50, 41, 4100), (1024, 320, 240, 153700)));

            byte[] bytes = PhotoDb.Build(model);
            log.AppendLine($"Photo Database built: {bytes.Length} bytes");

            // raw mhfd constant checks
            var r = new ChunkReader(bytes);
            Check(r.Tag(0) == "mhfd", "root tag = mhfd");
            Check(r.U32(0x04) == 0x84, $"mhfd header_len = 0x{r.U32(0x04):X} (expected 0x84)");
            Check(r.U32(0x08) == (uint)bytes.Length, "mhfd total_len = file length");
            Check(r.U32(0x10) == 2, $"mhfd unknown2 = {r.U32(0x10)} (expected 2)");
            Check(r.U32(0x14) == 3, $"mhfd num_children = {r.U32(0x14)} (expected 3)");
            Check(r.U32(0x1C) == 66, $"mhfd next_id = {r.U32(0x1C)} (expected 66)");
            Check(r.U8(0x30) == 2, $"mhfd unknown_flag1 = {r.U8(0x30)} (expected 2)");
            // first mhsd index is a 16-bit 1
            Check(r.Tag(0x84) == "mhsd" && r.U16(0x84 + 0x0C) == 1, "first mhsd is the image list (index 1, 16-bit)");

            var back = PhotoDb.Parse(bytes);
            Check(back.Warnings.Count == 0, $"parse warnings = {back.Warnings.Count} ({string.Join("; ", back.Warnings)})");
            Check(back.Photos.Count == 2, $"photo count = {back.Photos.Count} (expected 2)");
            if (back.Photos.Count == 2)
            {
                var p0 = back.Photos[0];
                Check(p0.ImageId == 64, $"photo[0] id = {p0.ImageId}");
                Check(p0.Thumbs.Count == 2, $"photo[0] thumbs = {p0.Thumbs.Count} (expected 2)");
                if (p0.Thumbs.Count == 2)
                {
                    var t0 = p0.Thumbs[0];
                    Check(t0.FormatId == 1036, $"photo[0] thumb[0] format = {t0.FormatId}");
                    Check(t0.Offset == 0 && t0.Size == 50 * 41 * 2, $"photo[0] thumb[0] offset/size = {t0.Offset}/{t0.Size}");
                    var t1 = p0.Thumbs[1];
                    Check(t1.FormatId == 1024 && t1.Offset == 100 && t1.Size == 320 * 240 * 2, $"photo[0] thumb[1] format/offset/size = {t1.FormatId}/{t1.Offset}/{t1.Size}");
                    Check(t1.SlotWidth == 320 && t1.SlotHeight == 240, $"photo[0] thumb[1] slot {t1.SlotWidth}x{t1.SlotHeight}");
                }
                Check(back.Photos[1].ImageId == 65, $"photo[1] id = {back.Photos[1].ImageId}");
            }
            var master = back.Albums.FirstOrDefault(a => a.IsMaster);
            Check(master is not null, "master 'Photo Library' album present");
            Check(master is not null && master.ImageIds.SequenceEqual(new uint[] { 64, 65 }), $"master album lists all photos [{(master is null ? "" : string.Join(",", master.ImageIds))}]");
            Check(master is not null && master.Name == "Photo Library", $"master album name = '{master?.Name}'");

            log.AppendLine();
            log.AppendLine(failures == 0 ? "RESULT: OK" : $"RESULT: FAILED ({failures})");
        }
        catch (Exception ex)
        {
            log.AppendLine("RESULT: FAILED - " + ex);
        }
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "ipod-phototest.txt"), log.ToString());
    }

    private static void RunPhotoDump(string root)
    {
        var log = new StringBuilder();
        try
        {
            string dbPath = Path.Combine(root, "Photos", "Photo Database");
            if (!File.Exists(dbPath)) { log.AppendLine($"No Photo Database at {dbPath}"); }
            else
            {
                byte[] bytes = File.ReadAllBytes(dbPath);
                log.AppendLine($"file   : {dbPath}  ({bytes.Length:N0} bytes)");
                var m = PhotoDb.Parse(bytes);
                log.AppendLine($"photos : {m.Photos.Count}");
                log.AppendLine($"albums : {m.Albums.Count}");
                foreach (var a in m.Albums)
                    log.AppendLine($"   {(a.IsMaster ? "*" : " ")} '{a.Name}'  ({a.ImageIds.Count} images)");
                if (m.Warnings.Count > 0) { log.AppendLine("warnings:"); foreach (var w in m.Warnings) log.AppendLine("   ! " + w); }

                log.AppendLine();
                log.AppendLine("FORMATS used (this is what new photos must match):");
                foreach (var g in m.Photos.SelectMany(p => p.Thumbs).GroupBy(t => t.FormatId).OrderBy(g => g.Key))
                {
                    var s = g.First();
                    var fmt = PhotoFormats.Lookup(g.Key);
                    string known = fmt is not null ? $"KNOWN {fmt.Width}x{fmt.Height} RGB565-LE" : $"unknown (mhni {s.ImageWidth}x{s.ImageHeight}, {s.Size} bytes)";
                    log.AppendLine($"   F{g.Key}: {g.Count()} thumbs, fileIndex {s.FileIndex}  → {known}");
                }

                log.AppendLine();
                log.AppendLine("first 3 photos:");
                foreach (var p in m.Photos.Take(3))
                {
                    log.AppendLine($"   image {p.ImageId}  date {p.Date:yyyy-MM-dd}  origSize {p.OrigImageSize}");
                    foreach (var t in p.Thumbs)
                        log.AppendLine($"      F{t.FormatId} off {t.Offset} size {t.Size}  img {t.ImageWidth}x{t.ImageHeight}  pad {t.HPad}/{t.VPad}  file {t.IthmbFileName}");
                }

                // does each referenced .ithmb exist + cover the offsets?
                log.AppendLine();
                log.AppendLine(".ithmb files in Photos/Thumbs:");
                string thumbs = Path.Combine(root, "Photos", "Thumbs");
                if (Directory.Exists(thumbs))
                    foreach (var f in Directory.GetFiles(thumbs, "F*.ithmb"))
                        log.AppendLine($"   {Path.GetFileName(f)}  {new FileInfo(f).Length:N0} bytes");
                else log.AppendLine("   (no Thumbs folder)");
            }
            log.AppendLine();
            log.AppendLine("RESULT: OK");
        }
        catch (Exception ex) { log.AppendLine("RESULT: FAILED - " + ex); }
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "ipod-photodump.txt"), log.ToString());
    }

    private static void RunBplistTest()
    {
        var log = new StringBuilder();
        int failures = 0;
        void Check(bool ok, string what) { log.AppendLine((ok ? "PASS " : "FAIL ") + what); if (!ok) failures++; }
        try
        {
            // Build a synthetic bplist00 dict { FireWireGUID, DBVersion, SerialNumber } and read it back.
            byte[] bytes = BuildBplist(new (string, object)[]
            {
                ("FireWireGUID", "0x000A270012345678"),
                ("DBVersion", 3),
                ("SerialNumber", "ABC1234XYZ"),
            });
            log.AppendLine($"synthetic bplist: {bytes.Length} bytes");

            var map = BinaryPlist.Flatten(bytes);
            Check(map is not null, "Flatten returned a map");
            if (map is not null)
            {
                Check(map.TryGetValue("FireWireGUID", out var g) && g == "0x000A270012345678", $"FireWireGUID = '{(map.TryGetValue("FireWireGUID", out var gg) ? gg : "?")}'");
                Check(map.TryGetValue("DBVersion", out var v) && v == "3", $"DBVersion = '{(map.TryGetValue("DBVersion", out var vv) ? vv : "?")}'");
                Check(map.TryGetValue("SerialNumber", out var s) && s == "ABC1234XYZ", $"SerialNumber = '{(map.TryGetValue("SerialNumber", out var ss) ? ss : "?")}'");
            }

            // Full path: write to a temp SysInfoExtended and parse via SysInfoExtended.TryParse.
            string tmp = Path.Combine(Path.GetTempPath(), "mixtape-sysinfoext-test");
            File.WriteAllBytes(tmp, bytes);
            var ext = SysInfoExtended.TryParse(tmp);
            Check(ext is not null, "SysInfoExtended.TryParse read the binary plist");
            Check(ext?.FirewireGuid == "000A270012345678", $"normalised GUID = '{ext?.FirewireGuid}' (expected 000A270012345678)");
            Check(ext?.DbVersion == 3, $"DBVersion = {ext?.DbVersion}");
            Check(ext?.SerialNumber == "ABC1234XYZ", $"serial = '{ext?.SerialNumber}'");
            try { File.Delete(tmp); } catch { }

            log.AppendLine();
            log.AppendLine(failures == 0 ? "RESULT: OK" : $"RESULT: FAILED ({failures})");
        }
        catch (Exception ex) { log.AppendLine("RESULT: FAILED - " + ex); }
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "ipod-bplisttest.txt"), log.ToString());
    }

    /// <summary>Build a minimal bplist00 from a top-level dict of string/int values (test fixture).</summary>
    private static byte[] BuildBplist((string Key, object Val)[] entries)
    {
        var objs = new List<byte[]>();
        byte[] Ascii(string s)
        {
            var b = new List<byte>();
            if (s.Length < 15) b.Add((byte)(0x50 | s.Length));
            else { b.Add(0x5F); b.Add(0x10); b.Add((byte)s.Length); }
            b.AddRange(Encoding.ASCII.GetBytes(s));
            return b.ToArray();
        }
        // obj0 = dict; then keys and values interleaved as separate objects.
        int n = entries.Length;
        var keyRefs = new int[n];
        var valRefs = new int[n];
        var bodies = new List<byte[]> { Array.Empty<byte>() }; // placeholder for dict at index 0
        int idx = 1;
        for (int i = 0; i < n; i++) { bodies.Add(Ascii(entries[i].Key)); keyRefs[i] = idx++; }
        for (int i = 0; i < n; i++)
        {
            object v = entries[i].Val;
            bodies.Add(v is int iv ? new byte[] { 0x10, (byte)iv } : Ascii((string)v));
            valRefs[i] = idx++;
        }
        // dict body
        var dict = new List<byte> { (byte)(0xD0 | n) };
        foreach (var r in keyRefs) dict.Add((byte)r);
        foreach (var r in valRefs) dict.Add((byte)r);
        bodies[0] = dict.ToArray();

        var ms = new MemoryStream();
        ms.Write(Encoding.ASCII.GetBytes("bplist00"));
        var offs = new long[bodies.Count];
        for (int i = 0; i < bodies.Count; i++) { offs[i] = ms.Position; ms.Write(bodies[i]); }
        long offTable = ms.Position;
        foreach (var o in offs) ms.WriteByte((byte)o); // offsetSize = 1
        // trailer
        ms.Write(new byte[6]);            // 5 unused + sortVersion
        ms.WriteByte(1);                  // offsetSize
        ms.WriteByte(1);                  // refSize
        void U64(long val) { for (int i = 7; i >= 0; i--) ms.WriteByte((byte)(val >> (i * 8))); }
        U64(bodies.Count);               // numObjects
        U64(0);                          // topObject
        U64(offTable);                   // offsetTableOffset
        return ms.ToArray();
    }

    // Drive the whole Windows-side cover-art pipeline against a throwaway Nano3 iPod folder: load,
    // stage a real audio file's embedded art, commit, then re-parse the written ArtworkDB + .ithmb files.
    private static void RunArtworkSyncTest(string audioFile)
    {
        var log = new System.Text.StringBuilder();
        int pass = 0, fail = 0;
        void Check(bool ok, string what) { if (ok) { pass++; log.AppendLine("  PASS  " + what); } else { fail++; log.AppendLine("  FAIL  " + what); } }
        log.AppendLine("=== ArtworkDB sync end-to-end ===");
        log.AppendLine("audio: " + audioFile);
        try
        {
            string sandbox = Path.Combine(Path.GetTempPath(), "ipodcmd-artsync");
            if (Directory.Exists(sandbox)) Directory.Delete(sandbox, true);
            Directory.CreateDirectory(Path.Combine(sandbox, "iPod_Control", "Artwork"));
            var device = new IPodDevice { MountRoot = sandbox, Profile = new DeviceProfile { Generation = IPodGeneration.Nano3, SupportsArtwork = true } };

            var art = ArtworkLibrary.Load(device);
            Check(art.SupportsArtwork, "Nano3 library supports artwork (formats resolved)");

            const ulong dbid = 0xDEADBEEFCAFEF00DUL;
            var staged = art.Stage(dbid, audioFile);
            Check(staged is not null, "Stage returned art (embedded image found + encoded)");
            if (staged is { } s) { Check(s.Size > 0, $"staged size = {s.Size} bytes, mhii id = {s.MhiiId}"); }
            art.Commit();

            string dbFile = Path.Combine(sandbox, "iPod_Control", "Artwork", "ArtworkDB");
            Check(File.Exists(dbFile), "ArtworkDB written");
            var m = ArtworkDb.Parse(File.ReadAllBytes(dbFile));
            Check(m.Warnings.Count == 0, "written ArtworkDB parses cleanly (" + string.Join("; ", m.Warnings) + ")");
            Check(m.Items.Count == 1, $"1 artwork item (got {m.Items.Count})");
            if (m.Items.Count == 1)
            {
                var it = m.Items[0];
                Check(it.TrackDbid == dbid, "item DBID links back to the track (mhii@0x18 == mhit@0x70)");
                int want = ArtworkFormats.For(IPodGeneration.Nano3).Length;
                Check(it.Thumbs.Count == want, $"{want} thumbs (got {it.Thumbs.Count}) — Nano3 cover-art formats");
                foreach (var t in it.Thumbs)
                {
                    string f = Path.Combine(sandbox, "iPod_Control", "Artwork", $"F{t.FormatId}_{t.FileIndex}.ithmb");
                    long len = File.Exists(f) ? new FileInfo(f).Length : -1;
                    Check(len >= t.Offset + t.Size, $"F{t.FormatId}_{t.FileIndex}.ithmb holds slot ({t.Offset}+{t.Size} ≤ {len}), {t.Width}x{t.Height}");
                }
            }
            log.AppendLine();
            log.AppendLine(fail == 0 ? "RESULT: OK" : $"RESULT: {fail} FAILED");
        }
        catch (Exception ex) { log.AppendLine("RESULT: EXCEPTION - " + ex); }
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "ipod-artworksynctest.txt"), log.ToString());
    }

    private static void RunArtworkTest()
    {
        var log = new System.Text.StringBuilder();
        int pass = 0, fail = 0;
        void Check(bool ok, string what) { if (ok) { pass++; log.AppendLine("  PASS  " + what); } else { fail++; log.AppendLine("  FAIL  " + what); } }
        log.AppendLine("=== ArtworkDB self-test ===");
        try
        {
            const ulong dbid0 = 0x1122334455667788UL, dbid1 = 0x99AABBCCDDEEFF00UL;
            var m = new ArtworkDbModel();
            m.Items.Add(new ArtworkItem { Id = 64, TrackDbid = dbid0, SourceImageSize = 12345, Thumbs = {
                new ArtworkThumb { FormatId = 1028, Offset = 0,     Size = 20000, Width = 100, Height = 100 },
                new ArtworkThumb { FormatId = 1029, Offset = 0,     Size = 80000, Width = 200, Height = 200 } } });
            m.Items.Add(new ArtworkItem { Id = 65, TrackDbid = dbid1, Thumbs = {
                new ArtworkThumb { FormatId = 1028, Offset = 20000, Size = 20000, Width = 100, Height = 100 },
                new ArtworkThumb { FormatId = 1029, Offset = 80000, Size = 80000, Width = 200, Height = 200 } } });

            byte[] built = ArtworkDb.Build(m);
            var r = new ChunkReader(built);
            Check(r.Tag(0) == "mhfd", "root is mhfd");
            Check(r.U32(0x14) == 3, "mhfd num_children = 3");
            Check(r.U32(0x1C) == 66, "mhfd next_id = maxId(65)+1");
            Check(built[0x30] == 2, "mhfd unknown_flag1 = 2");
            // first mhsd: 32-bit type == 1
            int ds1 = (int)r.U32(0x04);
            Check(r.Tag(ds1) == "mhsd" && r.U32(ds1 + 0x0C) == 1, "dataset 1 mhsd, 32-bit type = 1");

            var m2 = ArtworkDb.Parse(built);
            Check(m2.Warnings.Count == 0, "parse: no warnings (" + string.Join("; ", m2.Warnings) + ")");
            Check(m2.Items.Count == 2, "parse: 2 artwork items");
            if (m2.Items.Count == 2)
            {
                var a = m2.Items[0];
                Check(a.Id == 64 && a.TrackDbid == dbid0 && a.SourceImageSize == 12345, "item0 id/dbid/srcsize");
                Check(a.Thumbs.Count == 2, "item0 has 2 thumbs");
                Check(a.Thumbs[0].FormatId == 1028 && a.Thumbs[0].Size == 20000 && a.Thumbs[0].Width == 100 && a.Thumbs[0].Height == 100, "item0 thumb0 = 1028 100x100");
                Check(a.Thumbs[1].FormatId == 1029 && a.Thumbs[1].Width == 200 && a.Thumbs[1].Height == 200, "item0 thumb1 = 1029 200x200");
                var b = m2.Items[1];
                Check(b.Id == 65 && b.TrackDbid == dbid1, "item1 id/dbid");
                Check(b.Thumbs.Count == 2 && b.Thumbs[0].Offset == 20000 && b.Thumbs[1].Offset == 80000, "item1 thumb offsets 20000/80000");
            }

            // Round-trip: re-building the parsed model (entries carry RawMhii) is byte-identical.
            byte[] rebuilt = ArtworkDb.Build(m2);
            Check(rebuilt.Length == built.Length && rebuilt.AsSpan().SequenceEqual(built), $"round-trip byte-identical ({built.Length} bytes)");

            // The link: the parsed DBID equals what a track's mhit @0x70 would carry.
            Check(m2.Items[0].TrackDbid == dbid0 && m2.Items[1].TrackDbid == dbid1, "DBID link survives (mhii@0x18 == track dbid)");

            log.AppendLine();
            log.AppendLine(fail == 0 ? "RESULT: OK" : $"RESULT: {fail} FAILED");
        }
        catch (Exception ex) { log.AppendLine("RESULT: EXCEPTION - " + ex); }
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "ipod-artworktest.txt"), log.ToString());
    }

    private static void RunReadGuid(string driveArg)
    {
        string outp = Path.Combine(AppContext.BaseDirectory, "ipod-readguid.txt");
        var log = new System.Text.StringBuilder();
        log.AppendLine("=== Mixtape readguid (read-only) ===");
        char drive;
        try { drive = driveArg.First(char.IsLetter); }
        catch { File.WriteAllText(outp, "No drive letter found in argument: " + driveArg); return; }
        log.AppendLine("Drive: " + drive + ":");
        log.AppendLine();

        // Route 1 — SCSI INQUIRY vendor page 0xC0 → SysInfoExtended → FireWireGUID (try both target ids).
        log.AppendLine("--- Route 1: SCSI INQUIRY (vendor page 0xC0) ---");
        byte[]? doc = null;
        foreach (byte tid in new byte[] { 0, 1 })
        {
            try { doc = IpodGuidReader.ReadSysInfoExtendedScsi(drive, tid); log.AppendLine($"targetId={tid}: OK — {doc.Length} bytes assembled"); break; }
            catch (Exception ex) { log.AppendLine($"targetId={tid}: FAILED — {ex.Message}"); }
        }
        if (doc is { Length: > 0 })
        {
            log.AppendLine("first bytes: " + Convert.ToHexString(doc, 0, Math.Min(16, doc.Length)));
            log.AppendLine("text preview:");
            log.AppendLine(System.Text.Encoding.UTF8.GetString(doc, 0, Math.Min(600, doc.Length)));
            log.AppendLine("FireWireGUID (parsed): " + (IpodGuidReader.ExtractFireWireGuid(doc) ?? "(NOT found in document)"));
            log.AppendLine("base64 of full document (for exact re-parse):");
            log.AppendLine(Convert.ToBase64String(doc));
        }
        log.AppendLine();

        // Route 2 — USB storage serial (== the GUID for click-wheel iPods; some stacks byte-swap it).
        log.AppendLine("--- Route 2: USB storage serial (IOCTL_STORAGE_QUERY_PROPERTY) ---");
        try
        {
            string? serial = IpodGuidReader.ReadStorageSerial(drive);
            log.AppendLine("raw serial: " + (serial ?? "(none)"));
            if (serial != null)
            {
                log.AppendLine("  as-is normalized : " + (SysInfoParser.NormalizeGuid(serial) ?? "(not 16-hex)"));
                string swapped = IpodGuidReader.SwapPairs(serial);
                log.AppendLine("  pair-swapped     : " + swapped + "  → " + (SysInfoParser.NormalizeGuid(swapped) ?? "(not 16-hex)"));
            }
        }
        catch (Exception ex) { log.AppendLine("error: " + ex.Message); }

        File.WriteAllText(outp, log.ToString());
    }

    private static void RunDevReport(string root)
    {
        string outp = Path.Combine(AppContext.BaseDirectory, "ipod-devreport.txt");
        var dev = DeviceDetector.Build(root);
        File.WriteAllText(outp, dev is null ? "No iPod_Control found at: " + root : MainForm.BuildDeviceReportText(dev));
    }

    private static void RunVerifySign(string root)
    {
        var log = new StringBuilder();
        try
        {
            var device = DeviceDetector.Build(root);
            if (device is null) { log.AppendLine($"No iPod_Control under '{root}'."); }
            else
            {
                var p = device.Profile;
                log.AppendLine($"device : {device.DisplayName}");
                log.AppendLine($"scheme : {p.SchemeLabel}");
                log.AppendLine($"GUID   : {p.FirewireGuid ?? "(none)"}");
                log.AppendLine();
                if (p.Scheme != ChecksumScheme.Hash58)
                    log.AppendLine($"This iPod is '{p.SchemeLabel}'. The hash58 self-test only applies to hash58 devices (Classic / Nano 3G-4G) — nothing to verify here, writing works without a signature.");
                else if (string.IsNullOrEmpty(p.FirewireGuid))
                    log.AppendLine("No FireWire GUID found — a hash58 signature can't be computed, so writing is disabled.");
                else
                {
                    byte[] orig = File.ReadAllBytes(device.ITunesDbPath);
                    byte[] stored = orig.AsSpan(0x58, 20).ToArray();
                    if (stored.All(b => b == 0))
                        log.AppendLine("This iPod's database has no existing hash58 signature to compare against (it may be empty/new). Can't verify in advance — the first write will be the real test (DB is backed up).");
                    else
                    {
                        byte[] clone = (byte[])orig.Clone();
                        ChecksumWriter.Apply(clone, ChecksumScheme.Hash58, p.FirewireGuid);
                        byte[] computed = clone.AsSpan(0x58, 20).ToArray();
                        bool match = stored.AsSpan().SequenceEqual(computed);
                        log.AppendLine($"stored signature : {Convert.ToHexString(stored)}");
                        log.AppendLine($"Mixtape computes : {Convert.ToHexString(computed)}");
                        log.AppendLine();
                        log.AppendLine(match
                            ? "MATCH — Mixtape signs this iPod's database exactly as its firmware expects. Writing (add/delete/playlists) is SAFE on this device."
                            : "MISMATCH — Mixtape's hash58 differs from the device's. Writing is blocked automatically; do NOT force it. Please send this output so the signing can be corrected.");
                    }
                }
            }
            log.AppendLine();
            log.AppendLine("RESULT: OK");
        }
        catch (Exception ex) { log.AppendLine("RESULT: FAILED - " + ex); }
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "ipod-verifysign.txt"), log.ToString());
    }

    private static void RunPhotoRoundtrip(string root)
    {
        var log = new StringBuilder();
        int failures = 0;
        void Check(bool ok, string what) { log.AppendLine((ok ? "PASS " : "FAIL ") + what); if (!ok) failures++; }
        try
        {
            string dbPath = Path.Combine(root, "Photos", "Photo Database");
            if (!File.Exists(dbPath)) { log.AppendLine($"No Photo Database at {dbPath}"); }
            else
            {
                byte[] orig = File.ReadAllBytes(dbPath);
                var m = PhotoDb.Parse(orig);
                log.AppendLine($"original: {orig.Length:N0} bytes, {m.Photos.Count} photos, {m.Albums.Count} albums, warnings {m.Warnings.Count}");
                Check(m.Warnings.Count == 0, "original parsed with no warnings (writing would be allowed)");

                // Rebuild exactly as Save would (existing photos verbatim, master album rebuilt).
                byte[] rebuilt = PhotoDb.Build(m);
                var m2 = PhotoDb.Parse(rebuilt);
                log.AppendLine($"rebuilt : {rebuilt.Length:N0} bytes, {m2.Photos.Count} photos, {m2.Albums.Count} albums, warnings {m2.Warnings.Count}");

                Check(m2.Warnings.Count == 0, "rebuilt parses cleanly");
                Check(m2.Photos.Count == m.Photos.Count, $"photo count preserved ({m2.Photos.Count})");
                var ids1 = m.Photos.Select(p => p.ImageId).OrderBy(x => x).ToArray();
                var ids2 = m2.Photos.Select(p => p.ImageId).OrderBy(x => x).ToArray();
                Check(ids1.SequenceEqual(ids2), "every image id preserved");

                // Each existing photo's mhii must be byte-identical (verbatim preservation).
                int mismatched = 0;
                var byId = m2.Photos.ToDictionary(p => p.ImageId, p => p.RawMhii);
                foreach (var p in m.Photos)
                    if (p.RawMhii is null || !byId.TryGetValue(p.ImageId, out var r2) || r2 is null || !p.RawMhii.AsSpan().SequenceEqual(r2)) mismatched++;
                Check(mismatched == 0, $"all {m.Photos.Count} existing mhii chunks byte-identical after rebuild (mismatched {mismatched})");

                var f1 = m.Photos.SelectMany(p => p.Thumbs).Select(t => t.FormatId).Distinct().OrderBy(x => x).ToArray();
                var f2 = m2.Photos.SelectMany(p => p.Thumbs).Select(t => t.FormatId).Distinct().OrderBy(x => x).ToArray();
                Check(f1.SequenceEqual(f2), $"formats preserved [{string.Join(",", f2)}]");
                var master1 = m.Albums.FirstOrDefault(a => a.IsMaster);
                var master2 = m2.Albums.FirstOrDefault(a => a.IsMaster);
                Check(master2 is not null && master1 is not null && master2.ImageIds.Count == m.Photos.Count, $"master album lists all photos ({master2?.ImageIds.Count})");
                Check(m2.Albums.Count == m.Albums.Count, $"album count preserved ({m2.Albums.Count})");
            }
            log.AppendLine();
            log.AppendLine(failures == 0 ? "RESULT: OK" : $"RESULT: FAILED ({failures})");
        }
        catch (Exception ex) { log.AppendLine("RESULT: FAILED - " + ex); }
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "ipod-photort.txt"), log.ToString());
    }

    private static Photo MakeTestPhoto(uint id, params (int fmt, int w, int h, int offset)[] thumbs)
    {
        var p = new Photo { ImageId = id, Date = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc), OrigImageSize = 123456 };
        foreach (var (fmt, w, h, offset) in thumbs)
        {
            int size = w * h * 2;
            p.Thumbs.Add(new PhotoThumb
            {
                FormatId = fmt,
                SlotWidth = w,
                SlotHeight = h,
                ImageWidth = w,
                ImageHeight = h,
                HPad = 0,
                VPad = 0,
                Size = size,
                Offset = offset,
                FileIndex = 1,
                Pixels = new byte[size],
            });
        }
        return p;
    }

    private static void AppendDeviceInfo(StringBuilder log, IPodDevice d)
    {
        var p = d.Profile;
        log.AppendLine($"  mount        : {d.MountRoot}");
        log.AppendLine($"  model        : {p.ModelName ?? "(unknown)"}  [{p.ModelNumber ?? "?"}]");
        log.AppendLine($"  generation   : {p.Generation}");
        log.AppendLine($"  scheme       : {p.SchemeLabel}");
        log.AppendLine($"  firewire GUID: {p.FirewireGuid ?? "(none)"}");
        log.AppendLine($"  serial       : {p.SerialNumber ?? "(none)"}");
        log.AppendLine($"  music dirs   : {p.MusicDirCount}");
        log.AppendLine($"  writable     : {p.CanWrite}{(p.CanWrite ? "" : "  — " + p.WriteBlockReason)}");
        log.AppendLine($"  has database : {d.HasDatabase}");
    }
}
