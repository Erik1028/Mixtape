using System.Text;
using System.Text.RegularExpressions;

namespace iPodCommander;

/// <summary>
/// Where Encore OS looks for what Mixtape puts on the iPod. Encore reads plain folders on the disk and never makes one
/// itself (hal_ipod5g.c disk_init): of each list the first folder that exists is the one it reads - so Mixtape writes
/// into that one, and makes the English name only when there is none yet.
/// </summary>
internal static class EncoreFolders
{
    public static readonly string[] Photos = { "Fotók", "Fotok", "Photos", "Pictures", "Képek" };
    public static readonly string[] Videos = { "Videók", "Videok", "Videos", "Video" };
    public const string Calendars = "Calendars";

    /// <summary>The folder Encore reads: an existing one in Encore's order, else <paramref name="fallback"/> (not made here).</summary>
    public static string Find(string mountRoot, string[] names, string fallback)
    {
        foreach (string n in names)
        {
            string p = Path.Combine(mountRoot, n);
            if (Directory.Exists(p)) return p;
        }
        return Path.Combine(mountRoot, fallback);
    }

    public static string PhotoRoot(string mountRoot) => Find(mountRoot, Photos, "Photos");
    public static string VideoRoot(string mountRoot) => Find(mountRoot, Videos, "Videos");
    public static string CalendarDir(string mountRoot) => Path.Combine(mountRoot, Calendars);

    /// <summary>A name as a file name: what Windows and FAT refuse left out, no dots or spaces at the end.</summary>
    public static string SafeName(string name, string fallback)
    {
        var sb = new StringBuilder();
        foreach (char c in name)
            if (c >= 32 && "\\/:*?\"<>|".IndexOf(c) < 0 && (c != ' ' || (sb.Length > 0 && sb[^1] != ' '))) sb.Append(c);
        string s = sb.ToString().Trim().TrimEnd('.', ' ');
        if (s.Length > 80) s = s[..80].TrimEnd('.', ' ');
        return s.Length > 0 ? s : fallback;
    }
}

/// <summary>
/// Puts the calendars set up in Mixtape onto an Encore iPod as /Calendars/&lt;name&gt;.ics, each rewritten by
/// <see cref="EncoreCalendar.Simplify"/>. A file is written only when its text changed (Encore reads the folder again
/// when a file's size or time changes). Files Mixtape put there earlier for a calendar no longer set up are removed;
/// files anybody else put there are never touched - not even one with a calendar's name, which is then skipped.
/// </summary>
internal static class EncoreCalendarSync
{
    internal enum ProblemKind { Fetch, NameTaken, TooMany }

    internal sealed class Report
    {
        public int Calendars, Events, Written, Unchanged, Removed, LeftOut, Split, SpelledOut;
        public readonly List<(string Calendar, ProblemKind Kind, string Detail)> Problems = new();
    }

    /// <summary>The file name a calendar gets on the iPod.</summary>
    public static string FileName(string name) => EncoreFolders.SafeName(name, "Calendar") + ".ics";

    /// <summary>The calendar's text: a file's, or downloaded from its address (webcal:// is https://).</summary>
    public static string Fetch(EncoreCalendarSource s)
    {
        string text;
        if (!s.IsLink) text = File.ReadAllText(s.Location);
        else
        {
            string url = Regex.Replace(s.Location.Trim(), "^webcals?://", "https://", RegexOptions.IgnoreCase);
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(40) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Mixtape");
            byte[] b = http.GetByteArrayAsync(url).GetAwaiter().GetResult();
            text = Encoding.UTF8.GetString(b).TrimStart('﻿');
        }
        if (!text.Contains("BEGIN:VCALENDAR", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("not a calendar (.ics) file");
        return text;
    }

    /// <summary>Calendar files (and their events) in /Calendars that Mixtape did not write - Encore counts them too.</summary>
    private static (int Files, int Events) Others(string dir)
    {
        int files = 0, events = 0;
        if (!Directory.Exists(dir)) return (0, 0);
        try
        {
            foreach (string f in Directory.EnumerateFiles(dir, "*.ics").Concat(Directory.EnumerateDirectories(dir).SelectMany(d => Directory.EnumerateFiles(d, "*.ics"))))
            {
                if (EncoreCalendar.IsMixtapes(f)) continue;
                files++;
                try { events += Regex.Matches(File.ReadAllText(f), "^BEGIN:VEVENT", RegexOptions.Multiline).Count; } catch { }
            }
        }
        catch { }
        return (files, events);
    }

    /// <summary>How many calendar files Mixtape keeps in /Calendars now.</summary>
    public static int OnIpod(string mountRoot)
    {
        string dir = EncoreFolders.CalendarDir(mountRoot);
        try { return Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "*.ics").Count(EncoreCalendar.IsMixtapes) : 0; }
        catch { return 0; }
    }

    public static Report Sync(string mountRoot, IReadOnlyList<EncoreCalendarSource> sources, DateTime now,
                              Func<EncoreCalendarSource, string> fetch, Action<int, string>? progress = null, Func<bool>? cancelled = null)
    {
        var rep = new Report();
        string dir = EncoreFolders.CalendarDir(mountRoot);
        var (otherFiles, otherEvents) = Others(dir);

        // every calendar set up keeps its file name - one that can't be downloaded now keeps its old file on the iPod
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var plans = new List<(string Name, string File, List<EncoreCalendar.OutEvent> Events)>();
        for (int i = 0; i < sources.Count; i++)
        {
            if (cancelled?.Invoke() == true) return rep;
            var s = sources[i];
            string file = FileName(s.Name);
            for (int k = 2; keep.Contains(file); k++) file = FileName(s.Name + " " + k);
            keep.Add(file);
            if (otherFiles + plans.Count >= EncoreCalendar.MaxFiles) { rep.Problems.Add((s.Name, ProblemKind.TooMany, "")); continue; }
            progress?.Invoke(i, s.Name);
            try
            {
                var res = EncoreCalendar.Simplify(fetch(s), now);
                plans.Add((s.Name, file, res.Events));
                rep.Split += res.Split;
                rep.SpelledOut += res.SpelledOut;
            }
            catch (Exception ex) { rep.Problems.Add((s.Name, ProblemKind.Fetch, ex.InnerException?.Message ?? ex.Message)); }
        }

        rep.LeftOut = EncoreCalendar.Fit(plans.Select(p => p.Events).ToList(), now, Math.Max(0, EncoreCalendar.MaxEvents - otherEvents));
        progress?.Invoke(sources.Count, "");
        foreach (var (name, file, events) in plans)
        {
            if (cancelled?.Invoke() == true) break;
            string path = Path.Combine(dir, file);
            if (File.Exists(path) && !EncoreCalendar.IsMixtapes(path)) { rep.Problems.Add((name, ProblemKind.NameTaken, file)); continue; }
            byte[] bytes = Encoding.UTF8.GetBytes(EncoreCalendar.Write(events, name));
            rep.Calendars++;
            rep.Events += events.Count;
            if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes)) { rep.Unchanged++; continue; }
            Directory.CreateDirectory(dir);
            string tmp = path + ".part";   // not an .ics: Encore never reads a half-written one
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, path, overwrite: true);
            rep.Written++;
        }

        if (Directory.Exists(dir) && cancelled?.Invoke() != true)
            foreach (string f in Directory.EnumerateFiles(dir, "*.ics").ToList())
                if (!keep.Contains(Path.GetFileName(f)) && EncoreCalendar.IsMixtapes(f))
                {
                    try { File.Delete(f); rep.Removed++; } catch { }
                }
        return rep;
    }
}
