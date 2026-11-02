using System.Text;

namespace iPodCommander;

/// <summary>
/// The window's side of Encore OS (Erik's own iPod firmware): its card on the iPod's page, and the three things Mixtape
/// puts on such an iPod for it - calendars (/Calendars), photos (/Photos/&lt;album&gt;) and videos (/Videos/*.evf). All
/// plain files Encore reads by itself, so none of it touches the iTunes database: it works on a read-only iPod too.
/// </summary>
internal sealed partial class MainForm
{
    private (string Root, int Photos, int Albums, int Videos, int Calendars)? _encoreCounts;

    /// <summary>What Encore will list, counted once per iPod (the photo folder can be big) and again after a change.</summary>
    private (int Photos, int Albums, int Videos, int Calendars) EncoreCounts(IPodDevice dev)
    {
        if (_encoreCounts is { } c && c.Root == dev.MountRoot) return (c.Photos, c.Albums, c.Videos, c.Calendars);
        var (photos, albums) = EncorePhotos.Count(dev.MountRoot);
        int videos = 0;
        try
        {
            string vd = EncoreFolders.VideoRoot(dev.MountRoot);
            if (Directory.Exists(vd)) videos = Directory.EnumerateFiles(vd).Count(f => f.EndsWith(".evf", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
        }
        catch { }
        int cals = EncoreCalendarSync.OnIpod(dev.MountRoot);
        _encoreCounts = (dev.MountRoot, photos, albums, videos, cals);
        return (photos, albums, videos, cals);
    }

    private void EncoreChanged(IPodDevice dev)
    {
        _encoreCounts = null;
        if (ReferenceEquals(_device, dev) && _viewKind == SidebarRowKind.Device) ShowDevice();
    }

    private CardPanel EncoreCard(IPodDevice dev, int cardW)
    {
        var (photos, albums, videos, cals) = EncoreCounts(dev);
        int set = (_settings.EncoreCalendars ??= new()).Count;
        var card = new CardPanel(cardW);

        var calBtn = new ThemedButton { Text = set > 0 ? Loc.T("Sync now") : Loc.T("Set up…"), Pill = true, Primary = set > 0, Width = 130, Height = 30 };
        calBtn.Click += (_, _) => { if (_settings.EncoreCalendars.Count > 0) SyncEncoreCalendars(dev); else { OpenSettings(6); ShowDevice(); } };
        card.AddRow(Loc.T("Calendars"), set == 0
            ? Loc.T("Add an .ics file or a calendar's address in Settings, then sync it here. Encore shows it under Extras → Calendar.")
            : Loc.T("{0} set up in Settings, {1} on the iPod. Sync puts their latest version on the iPod (Extras → Calendar).", set, cals), calBtn, 64);

        var photoBtn = new ThemedButton { Text = Loc.T("Add photos…"), Pill = true, Width = 130, Height = 30 };
        photoBtn.Click += (_, _) =>
        {
            var m = ThemedMenu.New();
            var files = new ToolStripMenuItem(Loc.T("Photos…")); files.Click += (_, _) => AddEncorePhotos(dev, folder: false);
            var folder = new ToolStripMenuItem(Loc.T("A folder, as an album…")); folder.Click += (_, _) => AddEncorePhotos(dev, folder: true);
            m.Items.Add(files);
            m.Items.Add(folder);
            m.Show(photoBtn, new Point(0, photoBtn.Height + 2));
        };
        card.AddRow(Loc.T("Photos"), photos == 0
            ? Loc.T("Add photos or a whole folder — each folder is an album. Encore shows the newest first, by the date each was taken.")
            : Loc.T("{0} on the iPod in {1} album(s), newest first. Big photos are made smaller on the way, the date each was taken is kept.", photos, albums), photoBtn, 64);

        var videoBtn = new ThemedButton { Text = Loc.T("Add videos…"), Pill = true, Width = 130, Height = 30 };
        videoBtn.Click += (_, _) => AddEncoreVideos(dev);
        card.AddRow(Loc.T("Videos"), videos == 0
            ? Loc.T("Any video, converted to Encore's own format on the way (needs ffmpeg). Encore picks up where you stopped watching.")
            : Loc.T("{0} on the iPod. Any video is converted to Encore's own format on the way (needs ffmpeg).", videos), videoBtn, 64);
        card.Finish();
        return card;
    }

    // ---- calendars ----

    private void SyncEncoreCalendars(IPodDevice dev)
    {
        var sources = _settings.EncoreCalendars.ToList();
        if (sources.Count == 0) return;
        MarkIpodWrite(dev);
        EncoreCalendarSync.Report? rep = null;
        using (var prog = new CopyProgressDialog(Loc.T("Putting calendars on your iPod"), sources.Count + 1, (report, cancelled) =>
        {
            rep = EncoreCalendarSync.Sync(dev.MountRoot, sources, DateTime.Now, EncoreCalendarSync.Fetch,
                (i, name) => report(i, name.Length > 0 ? Loc.T("Reading “{0}”…", name) : Loc.T("Writing to the iPod…")), cancelled);
            report(sources.Count + 1, Loc.T("Done"));
        }, CopyFlight.ToIPod))
        {
            prog.ShowDialog(this);
            EncoreChanged(dev);
            if (prog.Error is not null)
            {
                MessageDialog.Show(this, Loc.T("Couldn't put the calendars on the iPod:\n\n{0}", prog.Error.Message), Loc.T("Calendars"), MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
        }
        if (rep is null) return;
        var sb = new StringBuilder(Loc.T("{0} calendar(s) with {1} event(s) are on the iPod.", rep.Calendars, rep.Events));
        if (rep.Calendars > 0 && rep.Written == 0) sb.Append(' ').Append(Loc.T("Nothing had changed."));
        if (rep.Removed > 0) sb.Append("\n\n").Append(Loc.T("Removed {0} calendar(s) that are no longer set up.", rep.Removed));
        if (rep.Split + rep.SpelledOut > 0) sb.Append("\n\n").Append(Loc.T("{0} repeating event(s) were rewritten so that Encore shows their skipped and moved dates right.", rep.Split + rep.SpelledOut));
        if (rep.LeftOut > 0) sb.Append("\n\n").Append(Loc.T("{0} event(s) furthest in the past were left out: Encore reads at most 4,000.", rep.LeftOut));
        foreach (var (cal, kind, detail) in rep.Problems)
            sb.Append("\n\n• ").Append(kind switch
            {
                EncoreCalendarSync.ProblemKind.Fetch => Loc.T("“{0}” couldn't be read, so the iPod keeps its earlier copy: {1}", cal, detail),
                EncoreCalendarSync.ProblemKind.NameTaken => Loc.T("“{0}”: the iPod already has a calendar file named {1} that Mixtape didn't put there, so it was left alone. Rename the calendar in Settings.", cal, detail),
                _ => Loc.T("“{0}”: Encore reads at most 64 calendar files.", cal),
            });
        MessageDialog.Show(this, sb.ToString(), Loc.T("Calendars"), MessageBoxButtons.OK, rep.Problems.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
    }

    // ---- photos ----

    private void AddEncorePhotos(IPodDevice dev, bool folder)
    {
        string[] files;
        string album, baseDir = "";
        if (folder)
        {
            using var fd = new FolderBrowserDialog { Description = Loc.T("Choose a folder — its photos and its subfolders' go onto the iPod as an album"), ShowNewFolderButton = false };
            if (fd.ShowDialog(this) != DialogResult.OK || string.IsNullOrEmpty(fd.SelectedPath)) return;
            baseDir = fd.SelectedPath;
            try { UseWaitCursor = true; files = GatherFiles(baseDir, EncorePhotos.Images); }
            finally { UseWaitCursor = false; }
            if (files.Length == 0)
            {
                MessageDialog.Show(this, Loc.T("No photos (jpg, png, …) were found in that folder or its subfolders."), Loc.T("Add photos"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            album = Path.GetFileName(baseDir.TrimEnd(Path.DirectorySeparatorChar));
        }
        else
        {
            using var od = new OpenFileDialog
            {
                Multiselect = true, Title = Loc.T("Select photos to copy onto the iPod"),
                Filter = Loc.T("Image files") + "|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.tif;*.tiff;*.webp|" + Loc.T("All files") + "|*.*",
            };
            if (od.ShowDialog(this) != DialogResult.OK || od.FileNames.Length == 0) return;
            files = od.FileNames;
            album = Path.GetFileName(Path.GetDirectoryName(files[0])?.TrimEnd(Path.DirectorySeparatorChar)) ?? "";
        }
        string? chosen = PromptDialog.Show(this, Loc.T("Add photos"), Loc.T("Album on the iPod (leave it empty for none):"), album);
        if (chosen is null) return;
        album = chosen.Trim();

        MarkIpodWrite(dev);
        var rep = new EncorePhotos.Report();
        using (var prog = new CopyProgressDialog(Loc.T("Adding photos to your iPod"), files.Length, (report, cancelled) =>
        {
            for (int i = 0; i < files.Length; i++)
            {
                if (cancelled()) break;
                report(i, Loc.T("Copying {0} of {1}   ·   {2}", i + 1, files.Length, Path.GetFileName(files[i])));
                // a folder's subfolders stay albums inside the album ("Summer/Balaton")
                string sub = folder ? Path.GetRelativePath(baseDir, Path.GetDirectoryName(files[i])!) : ".";
                string where = sub == "." ? album : album.Length > 0 ? album + "/" + sub.Replace('\\', '/') : sub.Replace('\\', '/');
                try
                {
                    switch (EncorePhotos.Add(dev.MountRoot, files[i], where))
                    {
                        case EncorePhotos.Outcome.Copied: rep.Copied++; break;
                        case EncorePhotos.Outcome.Redrawn: rep.Redrawn++; break;
                        default: rep.AlreadyThere++; break;
                    }
                }
                catch (Exception ex) { rep.Errors.Add($"{Path.GetFileName(files[i])}: {ex.Message}"); }
            }
            report(files.Length, Loc.T("Done"));
        }, CopyFlight.ToIPod))
        {
            prog.ShowDialog(this);
            EncoreChanged(dev);
            var sb = new StringBuilder(prog.WasCancelled ? Loc.T("Stopped — added {0} photo(s).", rep.Added) : Loc.T("Added {0} photo(s).", rep.Added));
            if (rep.Redrawn > 0) sb.Append(' ').Append(Loc.T("{0} of them were made smaller or saved anew so the iPod opens them quickly.", rep.Redrawn));
            if (rep.AlreadyThere > 0) sb.Append("\n\n").Append(Loc.T("{0} were already on the iPod.", rep.AlreadyThere));
            if (rep.Errors.Count > 0) sb.Append(Loc.T("\n\n{0} could not be added:\n• ", rep.Errors.Count)).Append(string.Join("\n• ", rep.Errors.Take(12)));
            MessageDialog.Show(this, sb.ToString(), "Mixtape", MessageBoxButtons.OK, rep.Errors.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
        }
    }

    // ---- videos ----

    private void AddEncoreVideos(IPodDevice dev)
    {
        var ff = FfmpegService.Detect(_settings.FfmpegPath);
        if (ff is null)
        {
            if (MessageDialog.Show(this, Loc.T("Converting a video for Encore OS needs ffmpeg, and it wasn't found.\n\nInstall it (for example: winget install Gyan.FFmpeg) and it is found by itself, or show Mixtape where ffmpeg.exe is in Settings ▸ Video.\n\nOpen Settings now?"),
                    Loc.T("ffmpeg not found"), MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes) OpenSettings(2);
            return;
        }
        using var od = new OpenFileDialog
        {
            Multiselect = true, Title = Loc.T("Select videos to convert for the iPod"),
            Filter = Loc.T("Video files") + "|*.mp4;*.m4v;*.mov;*.avi;*.mkv;*.wmv;*.flv;*.webm;*.mpg;*.mpeg;*.ts;*.3gp|" + Loc.T("All files") + "|*.*",
        };
        if (od.ShowDialog(this) != DialogResult.OK || od.FileNames.Length == 0) return;
        string[] files = od.FileNames;
        var opts = new EvfOptions(EvfOptions.FpsChoices.Contains(_settings.EncoreVideoFps) ? _settings.EncoreVideoFps : 15,
                                  EvfOptions.Lambdas[Math.Clamp(_settings.EncoreVideoQuality, 0, 2)], _settings.EncoreVideoFill);
        string dir = EncoreFolders.VideoRoot(dev.MountRoot);
        int threads = Math.Max(1, Environment.ProcessorCount / 2);   // leave half the PC to whatever else runs

        MarkIpodWrite(dev);
        var added = new List<(string Name, long Bytes)>();
        var there = new List<string>();
        var errors = new List<string>();
        using (var prog = new CopyProgressDialog(Loc.T("Converting videos for your iPod"), files.Length * 1000, (report, cancelled) =>
        {
            Directory.CreateDirectory(dir);
            for (int i = 0; i < files.Length; i++)
            {
                if (cancelled()) break;
                string name = EncoreFolders.SafeName(Path.GetFileNameWithoutExtension(files[i]), "Video");
                if (EvfConverter.Existing(dir, name) is not null) { there.Add(name); continue; }
                int at = i * 1000;
                report(at, Loc.T("Converting {0} of {1}   ·   {2}", i + 1, files.Length, name));
                try
                {
                    string done = EvfConverter.Convert(ff.FfmpegPath, files[i], Path.Combine(dir, name + ".evf"), opts,
                        (f, t) => report(at + (int)(f * 999), Loc.T("Converting {0} of {1}   ·   {2}%   ·   {3}", i + 1, files.Length, (int)(f * 100), name)),
                        cancelled, threads);
                    added.Add((name, new FileInfo(done).Length));
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { errors.Add($"{name}: {ex.Message}"); }
            }
            report(files.Length * 1000, Loc.T("Done"));
        }, CopyFlight.ToIPod))
        {
            prog.ShowDialog(this);
            EncoreChanged(dev);
            var sb = new StringBuilder(prog.WasCancelled ? Loc.T("Stopped — added {0} video(s).", added.Count) : Loc.T("Added {0} video(s).", added.Count));
            if (added.Count > 0) sb.Append(' ').Append(Loc.T("They take {0} on the iPod.", CapacityBar.Human(added.Sum(a => a.Bytes))));
            if (there.Count > 0) sb.Append("\n\n").Append(Loc.T("Already on the iPod: {0}", string.Join(", ", there)));
            if (errors.Count > 0) sb.Append(Loc.T("\n\n{0} could not be added:\n• ", errors.Count)).Append(string.Join("\n• ", errors));
            MessageDialog.Show(this, sb.ToString(), "Mixtape", MessageBoxButtons.OK, errors.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
        }
    }
}
