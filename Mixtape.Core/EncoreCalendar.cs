using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace iPodCommander;

/// <summary>
/// Calendars for Encore OS (Erik's own iPod firmware). Encore reads every .ics file in /Calendars (and one level of
/// subfolders) itself, with a small parser of its own (src/system/events.c): VEVENT with SUMMARY, LOCATION,
/// DESCRIPTION, DTSTART, DTEND or DURATION, and RRULE FREQ=DAILY/WEEKLY(+BYDAY)/MONTHLY/YEARLY with INTERVAL, COUNT and
/// UNTIL. It knows no EXDATE, no moved occurrences (RECURRENCE-ID), no "second Tuesday" (BYDAY=2TU) and no time zone
/// but its own: a "Z" time becomes Hungarian time (CET/CEST), any other time is taken as Hungarian time as it stands.
///
/// <see cref="Simplify"/> rewrites a calendar so that Encore shows exactly what it means: every time in Hungarian time,
/// a repeat Encore reads kept as a repeat, a repeat with skipped or moved dates split into runs around them, and only
/// a repeat Encore can't express spelled out date by date (a year back, two years ahead). Alarms, attendees and the
/// rest are left out - Encore reads none of it, and a reminder's DESCRIPTION after the event's would replace the
/// event's own on the iPod. The file is marked with <see cref="ProdId"/>, which is how a later sync knows the files
/// Mixtape put there from the ones somebody else did.
/// </summary>
internal static class EncoreCalendar
{
    public const string ProdId = "-//Mixtape//Encore OS calendar//EN";
    public const int MaxEvents = 4000;                  // events.c MAX_EVENTS, all files together
    public const int MaxFileBytes = 4 * 1024 * 1024;    // MAX_ICS_SIZE: a bigger file is skipped whole
    public const int MaxFiles = 64;                     // events_load's MAXF
    private const int MaxDescription = 4000;            // characters kept of a description (Teams / Zoom invites run long)
    private const int MaxSegments = 200;                // more skipped dates than this: spell the dates out instead
    private const int MaxExpanded = 1200;               // occurrences written for one spelled-out repeat

    /// <summary>One event as Encore will get it: times are Hungarian wall-clock time.</summary>
    internal sealed class OutEvent
    {
        public string Uid = "";
        public DateTime Start;          // all-day: the date
        public bool AllDay;
        public int LengthMin;
        public string? RRule;           // null: a single occurrence
        public DateTime LastDay;        // the last day it starts on (DateTime.MaxValue: repeats for ever)
        public string Summary = "", Location = "", Description = "";   // raw ICS text (escapes kept)
    }

    internal sealed class Result
    {
        public readonly List<OutEvent> Events = new();
        public int SourceEvents;        // VEVENTs read
        public int Repeats;             // repeats Encore reads as they are
        public int Split;               // repeats cut into runs around skipped or moved dates
        public int SpelledOut;          // repeats written date by date
        public int Cancelled;
        public string Name = "";        // X-WR-CALNAME, if the calendar has one
    }

    // ---- ICS lines ---------------------------------------------------------------------------------------

    private sealed class Prop
    {
        public string Name = "", Value = "";
        public readonly Dictionary<string, string> Params = new(StringComparer.OrdinalIgnoreCase);
        public string? Param(string k) => Params.TryGetValue(k, out var v) ? v : null;
    }

    private sealed class VEvent
    {
        public readonly List<Prop> Props = new();
        public Prop? Get(string name) => Props.FirstOrDefault(p => p.Name == name);
        public IEnumerable<Prop> All(string name) => Props.Where(p => p.Name == name);
        public string Text(string name) => Get(name)?.Value ?? "";
    }

    /// <summary>Unfolded content lines split into name, parameters and value (a ':' inside quotes is a parameter's).</summary>
    private static IEnumerable<Prop> ReadLines(string text)
    {
        var sb = new StringBuilder();
        var lines = new List<string>();
        foreach (string raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            if (raw.Length > 0 && (raw[0] == ' ' || raw[0] == '\t')) { sb.Append(raw, 1, raw.Length - 1); continue; }
            if (sb.Length > 0) lines.Add(sb.ToString());
            sb.Clear().Append(raw);
        }
        if (sb.Length > 0) lines.Add(sb.ToString());
        foreach (string line in lines)
        {
            int colon = -1;
            bool q = false;
            for (int i = 0; i < line.Length; i++)
            {
                if (line[i] == '"') q = !q;
                else if (line[i] == ':' && !q) { colon = i; break; }
            }
            if (colon <= 0) continue;
            var p = new Prop { Value = line[(colon + 1)..] };
            string head = line[..colon];
            var parts = SplitOutsideQuotes(head, ';');
            p.Name = parts[0].Trim().ToUpperInvariant();
            foreach (string kv in parts.Skip(1))
            {
                int eq = kv.IndexOf('=');
                if (eq > 0) p.Params[kv[..eq].Trim()] = kv[(eq + 1)..].Trim().Trim('"');
            }
            yield return p;
        }
    }

    private static List<string> SplitOutsideQuotes(string s, char sep)
    {
        var parts = new List<string>();
        bool q = false;
        int start = 0;
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '"') q = !q;
            else if (s[i] == sep && !q) { parts.Add(s[start..i]); start = i + 1; }
        }
        parts.Add(s[start..]);
        return parts;
    }

    // ---- time ----------------------------------------------------------------------------------------------

    /// <summary>A DATE or DATE-TIME as written: wall-clock time in its zone (Zone null and not Utc: floating).</summary>
    private readonly record struct IcsTime(DateTime Wall, TimeZoneInfo? Zone, bool Utc, bool DateOnly)
    {
        public bool Floating => !Utc && Zone is null && !DateOnly;
        public DateTime? Instant => DateOnly || Floating ? null : Utc ? Wall : WallToUtc(Wall, Zone!);
        /// <summary>The same moment as Encore shows it (Hungarian wall-clock time).</summary>
        public DateTime Encore => DateOnly || Floating ? Wall : UtcToEncore(Instant!.Value);
        public IcsTime At(DateTime wall) => this with { Wall = wall };
    }

    /// <summary>events.c utc_to_local, exactly: +1 hour, +2 from the last Sunday of March 01:00 UTC to the last Sunday
    /// of October 01:00 UTC.</summary>
    public static DateTime UtcToEncore(DateTime utc)
    {
        DateTime on = LastSunday(utc.Year, 3).AddHours(1), off = LastSunday(utc.Year, 10).AddHours(1);
        return utc.AddMinutes(utc >= on && utc < off ? 120 : 60);
    }

    private static DateTime LastSunday(int y, int m) { var d = new DateTime(y, m, 31); return d.AddDays(-(int)d.DayOfWeek); }

    private static DateTime WallToUtc(DateTime wall, TimeZoneInfo zone)
    {
        try
        {
            var w = DateTime.SpecifyKind(wall, DateTimeKind.Unspecified);
            if (zone.IsInvalidTime(w)) w = w.AddHours(1);   // in the spring-forward gap: the hour after it
            return TimeZoneInfo.ConvertTimeToUtc(w, zone);
        }
        catch { return wall - zone.BaseUtcOffset; }
    }

    private static readonly Dictionary<string, TimeZoneInfo?> Zones = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>TZID → zone: an IANA or Windows name, also behind a vendor prefix ("/mozilla.org/…/Europe/Budapest").
    /// Null when unknown - then the time is taken as Hungarian time, which is what Encore itself would do.</summary>
    private static TimeZoneInfo? ResolveZone(string tzid)
    {
        lock (Zones)
        {
            if (Zones.TryGetValue(tzid, out var hit)) return hit;
            TimeZoneInfo? z = null;
            string id = tzid.Trim().Trim('"');
            while (id.Length > 0 && z is null)
            {
                if (TimeZoneInfo.TryFindSystemTimeZoneById(id, out var found)) z = found;
                int slash = id.IndexOf('/');
                if (z is null) id = slash >= 0 ? id[(slash + 1)..] : "";
            }
            Zones[tzid] = z;
            return z;
        }
    }

    private static readonly Dictionary<string, int?> Shifts = new();

    /// <summary>How many minutes Encore's clock runs ahead of <paramref name="t"/>'s zone - when that is the same all
    /// year round (any zone that changes its clocks on EU dates: Budapest 0, London 60). Null when it varies (UTC,
    /// New York), so a repeat in it can't keep one time of day on the iPod.</summary>
    private static int? ShiftOf(IcsTime t, DateTime now)
    {
        if (t.DateOnly || t.Floating) return 0;
        if (t.Utc) return null;
        string key = t.Zone!.Id;
        lock (Shifts)
        {
            if (Shifts.TryGetValue(key, out var hit)) return hit;
            int? shift = null;
            for (var d = new DateTime(now.Year - 20, 1, 1, 12, 0, 0); d.Year <= now.Year + 10; d = d.AddDays(1))
            {
                int s = (int)Math.Round((UtcToEncore(d) - d).TotalMinutes - t.Zone.GetUtcOffset(DateTime.SpecifyKind(d, DateTimeKind.Utc)).TotalMinutes);
                if (shift is null) shift = s;
                else if (shift != s) { shift = null; break; }
            }
            Shifts[key] = shift;
            return shift;
        }
    }

    private static IcsTime? ParseTime(Prop? p, IcsTime? like = null)
    {
        if (p is null) return null;
        string v = p.Value.Trim();
        int slash = v.IndexOf('/');                 // RDATE;VALUE=PERIOD:start/end - the start
        if (slash > 0) v = v[..slash];
        int comma = v.IndexOf(',');
        if (comma > 0) v = v[..comma];
        if (v.Length < 8 || !DateTime.TryParseExact(v[..8], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) return null;
        bool dateOnly = v.Length < 15 || v[8] != 'T' || string.Equals(p.Param("VALUE"), "DATE", StringComparison.OrdinalIgnoreCase);
        if (dateOnly) return new IcsTime(date, null, false, true);
        if (!int.TryParse(v.AsSpan(9, 2), NumberStyles.None, CultureInfo.InvariantCulture, out int h) || h > 23 ||
            !int.TryParse(v.AsSpan(11, 2), NumberStyles.None, CultureInfo.InvariantCulture, out int mi) || mi > 59) return null;
        var wall = date.AddHours(h).AddMinutes(mi);
        if (v.Length > 15 && v[15] == 'Z') return new IcsTime(wall, null, true, false);
        if (p.Param("TZID") is { } tz) return new IcsTime(wall, ResolveZone(tz), false, false);
        return like is { } l && !l.DateOnly ? new IcsTime(wall, l.Zone, l.Utc, false) : new IcsTime(wall, null, false, false);
    }

    /// <summary>All the times a multi-valued property (EXDATE:a,b,c) lists.</summary>
    private static IEnumerable<IcsTime> ParseTimes(Prop p)
    {
        foreach (string one in p.Value.Split(','))
        {
            var q = new Prop { Name = p.Name, Value = one };
            foreach (var kv in p.Params) q.Params[kv.Key] = kv.Value;
            if (ParseTime(q) is { } t) yield return t;
        }
    }

    /// <summary>"PT1H30M", "P1D", "P2W" → minutes (negative or unreadable: -1).</summary>
    private static int ParseDuration(string v)
    {
        v = v.Trim();
        if (v.StartsWith('+')) v = v[1..];
        if (!v.StartsWith('P')) return -1;
        int total = 0, n = 0;
        foreach (char c in v.AsSpan(1))
        {
            if (char.IsAsciiDigit(c)) { n = n * 10 + (c - '0'); continue; }
            switch (c)
            {
                case 'W': total += n * 7 * 1440; break;
                case 'D': total += n * 1440; break;
                case 'H': total += n * 60; break;
                case 'M': total += n; break;
                case 'T': case 'S': break;
                default: return -1;
            }
            n = 0;
        }
        return total;
    }

    // ---- repeats -------------------------------------------------------------------------------------------

    private sealed class Rule
    {
        public string Freq = "";
        public int Interval = 1, Count;
        public IcsTime? Until;
        public readonly List<(int Ord, DayOfWeek Day)> ByDay = new();
        public readonly List<int> ByMonthDay = new(), ByMonth = new(), BySetPos = new();
        public DayOfWeek Wkst = DayOfWeek.Monday;
        public bool Exotic;      // BYYEARDAY, BYWEEKNO, BYHOUR…: parts this expander reads past
    }

    private static readonly string[] DayCodes = { "SU", "MO", "TU", "WE", "TH", "FR", "SA" };

    private static Rule? ParseRule(string v, IcsTime start)
    {
        var r = new Rule();
        foreach (string part in v.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=');
            if (eq <= 0) continue;
            string k = part[..eq].Trim().ToUpperInvariant(), x = part[(eq + 1)..].Trim().ToUpperInvariant();
            static List<int> Ints(string s) => s.Split(',').Select(t => int.TryParse(t, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int n) ? n : 0).Where(n => n != 0).ToList();
            switch (k)
            {
                case "FREQ": r.Freq = x; break;
                case "INTERVAL": r.Interval = int.TryParse(x, out int i) && i > 0 ? i : 1; break;
                case "COUNT": r.Count = int.TryParse(x, out int c) && c > 0 ? c : 0; break;
                case "UNTIL": r.Until = ParseTime(new Prop { Name = "UNTIL", Value = x }, start); break;
                case "WKST": r.Wkst = (DayOfWeek)Math.Max(0, Array.IndexOf(DayCodes, x)); break;
                case "BYMONTHDAY": r.ByMonthDay.AddRange(Ints(x)); break;
                case "BYMONTH": r.ByMonth.AddRange(Ints(x).Where(m => m is >= 1 and <= 12)); break;
                case "BYSETPOS": r.BySetPos.AddRange(Ints(x)); break;
                case "BYDAY":
                    foreach (string d in x.Split(','))
                    {
                        var m = Regex.Match(d, @"^([+-]?\d{1,2})?(SU|MO|TU|WE|TH|FR|SA)$");
                        if (m.Success) r.ByDay.Add((m.Groups[1].Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0, (DayOfWeek)Array.IndexOf(DayCodes, m.Groups[2].Value)));
                    }
                    break;
                default: r.Exotic = true; break;
            }
        }
        return r.Freq is "DAILY" or "WEEKLY" or "MONTHLY" or "YEARLY" ? r : null;
    }

    /// <summary>The days of one month the rule picks (BYMONTHDAY, BYDAY with or without "2nd"/"last"), or DTSTART's day.</summary>
    private static IEnumerable<DateTime> MonthDays(Rule r, int y, int m, DateTime start)
    {
        int dim = DateTime.DaysInMonth(y, m);
        var first = new DateTime(y, m, 1);
        if (r.ByMonthDay.Count > 0)
        {
            foreach (int n in r.ByMonthDay)
            {
                int d = n > 0 ? n : dim + 1 + n;
                if (d < 1 || d > dim) continue;
                var day = first.AddDays(d - 1);
                if (r.ByDay.Count == 0 || r.ByDay.Any(b => b.Day == day.DayOfWeek)) yield return day;
            }
            yield break;
        }
        if (r.ByDay.Count > 0)
        {
            foreach (var (ord, wd) in r.ByDay)
            {
                var all = Enumerable.Range(0, dim).Select(i => first.AddDays(i)).Where(d => d.DayOfWeek == wd).ToList();
                if (ord == 0) foreach (var d in all) yield return d;
                else if (ord > 0 && ord <= all.Count) yield return all[ord - 1];
                else if (ord < 0 && -ord <= all.Count) yield return all[all.Count + ord];
            }
            yield break;
        }
        if (start.Day <= dim) yield return first.AddDays(start.Day - 1);
    }

    private static List<DateTime> SetPos(Rule r, List<DateTime> days)
    {
        days = days.Distinct().OrderBy(d => d).ToList();
        if (r.BySetPos.Count == 0 || days.Count == 0) return days;
        return r.BySetPos.Select(p => p > 0 ? (p <= days.Count ? days[p - 1] : DateTime.MinValue) : (-p <= days.Count ? days[days.Count + p] : DateTime.MinValue))
                         .Where(d => d != DateTime.MinValue).Distinct().OrderBy(d => d).ToList();
    }

    /// <summary>The days of the k-th period (day, week, month or year, INTERVAL apart) from DTSTART's.</summary>
    private static List<DateTime> Period(Rule r, DateTime start, int k)
    {
        var days = new List<DateTime>();
        switch (r.Freq)
        {
            case "DAILY":
            {
                var d = start.Date.AddDays((double)k * r.Interval);
                if ((r.ByMonth.Count == 0 || r.ByMonth.Contains(d.Month)) &&
                    (r.ByMonthDay.Count == 0 || r.ByMonthDay.Any(n => n == d.Day || n == d.Day - DateTime.DaysInMonth(d.Year, d.Month) - 1)) &&
                    (r.ByDay.Count == 0 || r.ByDay.Any(b => b.Day == d.DayOfWeek)))
                    days.Add(d);
                return days;
            }
            case "WEEKLY":
            {
                var ws = start.Date.AddDays(-(((int)start.DayOfWeek - (int)r.Wkst + 7) % 7)).AddDays(7.0 * k * r.Interval);
                for (int i = 0; i < 7; i++)
                {
                    var d = ws.AddDays(i);
                    bool on = r.ByDay.Count > 0 ? r.ByDay.Any(b => b.Day == d.DayOfWeek) : d.DayOfWeek == start.DayOfWeek;
                    if (on && (r.ByMonth.Count == 0 || r.ByMonth.Contains(d.Month))) days.Add(d);
                }
                return SetPos(r, days);
            }
            case "MONTHLY":
            {
                var m0 = new DateTime(start.Year, start.Month, 1).AddMonths(k * r.Interval);
                if (r.ByMonth.Count > 0 && !r.ByMonth.Contains(m0.Month)) return days;
                days.AddRange(MonthDays(r, m0.Year, m0.Month, start));
                return SetPos(r, days);
            }
            default:   // YEARLY
            {
                int y = start.Year + k * r.Interval;
                if (y > 9998) return days;
                if (r.ByMonth.Count > 0)
                    foreach (int m in r.ByMonth) days.AddRange(MonthDays(r, y, m, start));
                else if (r.ByMonthDay.Count > 0)
                    for (int m = 1; m <= 12; m++) days.AddRange(MonthDays(r, y, m, start));
                else if (r.ByDay.Count > 0)
                {
                    var all = Enumerable.Range(0, DateTime.IsLeapYear(y) ? 366 : 365).Select(i => new DateTime(y, 1, 1).AddDays(i)).ToList();
                    foreach (var (ord, wd) in r.ByDay)
                    {
                        var hits = all.Where(d => d.DayOfWeek == wd).ToList();
                        if (ord == 0) days.AddRange(hits);
                        else if (ord > 0 && ord <= hits.Count) days.Add(hits[ord - 1]);
                        else if (ord < 0 && -ord <= hits.Count) days.Add(hits[hits.Count + ord]);
                    }
                }
                else if (start.Day <= DateTime.DaysInMonth(y, start.Month)) days.Add(new DateTime(y, start.Month, start.Day));
                return SetPos(r, days);
            }
        }
    }

    /// <summary>Occurrences (wall-clock time in the event's zone), DTSTART first. Stops at the rule's own end, or after
    /// the first occurrence past <paramref name="stop"/>. Complete: the rule ran out (COUNT, UNTIL) before that.</summary>
    private static (List<DateTime> Occ, bool Complete) Expand(Rule r, IcsTime start, DateTime stop, int cap = 100_000)
    {
        var occ = new List<DateTime> { start.Wall };
        var tod = start.Wall.TimeOfDay;
        bool Past(DateTime w)
        {
            if (r.Until is not { } u) return false;
            if (u.DateOnly || start.DateOnly) return w.Date > u.Wall.Date;
            if (start.At(w).Instant is { } a && u.Instant is { } b) return a > b;
            return w > u.Encore;
        }
        if (r.Count == 1) return (occ, true);
        int periods = r.Freq switch { "DAILY" => 80_000, "WEEKLY" => 12_000, "MONTHLY" => 2_400, _ => 200 };
        for (int k = 0; k < periods; k++)
        {
            foreach (var day in Period(r, start.Wall, k))
            {
                var w = day + tod;
                if (w <= start.Wall) continue;
                if (Past(w)) return (occ, true);
                occ.Add(w);
                if (r.Count > 0 && occ.Count >= r.Count) return (occ, true);
                if (w > stop || occ.Count >= cap) return (occ, false);
            }
        }
        return (occ, true);
    }

    /// <summary>The RRULE Encore reads with this rule's meaning (UNTIL and COUNT left to the caller), or null.</summary>
    private static string? EncoreRule(Rule r, IcsTime start, int shift)
    {
        if (r.Exotic || r.BySetPos.Count > 0) return null;
        var wd = start.Wall.DayOfWeek;
        if (shift != 0 && r.Freq != "DAILY" && start.Wall.AddMinutes(shift).Date != start.Wall.Date) return null;   // the iPod would see other days
        string Every(string f) => r.Interval > 1 ? $"FREQ={f};INTERVAL={r.Interval}" : $"FREQ={f}";
        switch (r.Freq)
        {
            case "DAILY":
                if (r.ByMonth.Count > 0 || r.ByMonthDay.Count > 0) return null;
                if (r.ByDay.Count == 0) return Every("DAILY");
                if (r.Interval != 1) return null;
                goto case "WEEKLY";     // every day of a few weekdays = those weekdays every week
            case "WEEKLY":
            {
                if (r.ByMonth.Count > 0 || r.ByMonthDay.Count > 0 || r.ByDay.Any(b => b.Ord != 0)) return null;
                var days = r.ByDay.Count > 0 ? r.ByDay.Select(b => b.Day).Distinct().ToList() : new List<DayOfWeek> { wd };
                if (!days.Contains(wd)) return null;                                  // DTSTART off the rule: Encore would skip it
                if (r.Interval > 1 && r.Wkst != DayOfWeek.Monday && days.Count > 1) return null;   // the iPod counts weeks from Monday
                var order = new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday };
                string by = string.Join(",", order.Where(days.Contains).Select(d => DayCodes[(int)d]));
                return (r.Freq == "DAILY" ? "FREQ=WEEKLY" : Every("WEEKLY")) + ";BYDAY=" + by;
            }
            case "MONTHLY":
                if (r.ByDay.Count > 0 || r.ByMonth.Count > 0) return null;
                if (r.ByMonthDay.Count > 1 || (r.ByMonthDay.Count == 1 && r.ByMonthDay[0] != start.Wall.Day)) return null;
                return Every("MONTHLY");
            default:
                if (r.ByDay.Count > 0) return null;
                if (r.ByMonth.Count > 1 || (r.ByMonth.Count == 1 && r.ByMonth[0] != start.Wall.Month)) return null;
                if (r.ByMonthDay.Count > 1 || (r.ByMonthDay.Count == 1 && r.ByMonthDay[0] != start.Wall.Day)) return null;
                return Every("YEARLY");
        }
    }

    // ---- the rewrite -----------------------------------------------------------------------------------------

    /// <summary>Rewrite <paramref name="ics"/> so Encore shows what it means. <paramref name="now"/> places the window
    /// a repeat Encore can't read is spelled out in (a year back, two years ahead).</summary>
    public static Result Simplify(string ics, DateTime now)
    {
        var res = new Result();
        var events = new List<VEvent>();
        VEvent? cur = null;
        int depth = 0;   // components nested in a VEVENT (VALARM): their lines aren't the event's
        foreach (var p in ReadLines(ics))
        {
            if (p.Name == "BEGIN")
            {
                if (cur is not null) { depth++; continue; }
                if (p.Value.Trim().Equals("VEVENT", StringComparison.OrdinalIgnoreCase)) { cur = new VEvent(); depth = 0; }
                continue;
            }
            if (p.Name == "END")
            {
                if (cur is null) continue;
                if (depth > 0) { depth--; continue; }
                events.Add(cur);
                cur = null;
                continue;
            }
            if (cur is not null && depth == 0) cur.Props.Add(p);
            else if (cur is null && p.Name == "X-WR-CALNAME" && res.Name.Length == 0) res.Name = Unescape(p.Value).Trim();
        }
        res.SourceEvents = events.Count;

        // moved or cancelled single dates of a repeat (RECURRENCE-ID), by the repeat's UID
        var overrides = events.Where(e => e.Get("RECURRENCE-ID") is not null).ToLookup(e => e.Text("UID"));
        int seq = 0;
        foreach (var ev in events)
        {
            seq++;
            bool cancelled = ev.Text("STATUS").Trim().Equals("CANCELLED", StringComparison.OrdinalIgnoreCase);
            if (ev.Get("RECURRENCE-ID") is not null)
            {
                if (cancelled) { res.Cancelled++; continue; }
                if (Single(ev, seq) is { } moved) res.Events.Add(moved);   // the moved date, on its own; the repeat skips the original
                continue;
            }
            if (cancelled) { res.Cancelled++; continue; }
            var start = ParseTime(ev.Get("DTSTART"));
            if (start is null) continue;
            var rule = ev.Get("RRULE") is { } rp ? ParseRule(rp.Value, start.Value) : null;
            if (rule is null) { if (Single(ev, seq) is { } one) res.Events.Add(one); }
            else Repeat(ev, start.Value, rule, ev.Text("UID") is { Length: > 0 } uid ? overrides[uid] : Enumerable.Empty<VEvent>(), seq, now, res);
            foreach (var rd in ev.All("RDATE"))   // extra dates of a repeat: each on its own
                foreach (var t in ParseTimes(rd))
                    if (Single(ev, seq, t) is { } extra) res.Events.Add(extra);
        }
        return res;
    }

    /// <summary>Length in minutes from DTEND or DURATION (all day without either: one day).</summary>
    private static int LengthOf(VEvent ev, IcsTime start)
    {
        if (ParseTime(ev.Get("DTEND"), start) is { } end)
        {
            var a = start.DateOnly ? start.Wall : start.Encore;
            var b = end.DateOnly ? end.Wall : end.Encore;
            return Math.Max(0, (int)(b - a).TotalMinutes);
        }
        if (ev.Get("DURATION") is { } d) return Math.Max(0, ParseDuration(d.Value));
        return start.DateOnly ? 1440 : 0;
    }

    private static OutEvent Shell(VEvent ev, int seq, string suffix = "")
    {
        string desc = ev.Text("DESCRIPTION");
        if (desc.Length > MaxDescription)
        {
            desc = desc[..MaxDescription];
            if (char.IsHighSurrogate(desc[^1])) desc = desc[..^1];
            int bs = 0;
            for (int i = desc.Length - 1; i >= 0 && desc[i] == '\\'; i--) bs++;
            if (bs % 2 == 1) desc = desc[..^1];   // don't end on half an escape
            desc += "…";
        }
        string uid = ev.Text("UID");
        return new OutEvent
        {
            Uid = (uid.Length > 0 ? uid : "mixtape-" + seq) + suffix,
            Summary = ev.Text("SUMMARY"), Location = ev.Text("LOCATION"), Description = desc,
        };
    }

    /// <summary>One occurrence: the event's own DTSTART, or <paramref name="at"/> (an RDATE) with the same length.</summary>
    private static OutEvent? Single(VEvent ev, int seq, IcsTime? at = null)
    {
        var start = ParseTime(ev.Get("DTSTART"));
        if (start is null) return null;
        int len = LengthOf(ev, start.Value);
        var t = at ?? start.Value;
        var o = Shell(ev, seq, at is null ? "" : "-" + t.Wall.ToString("yyyyMMddTHHmm", CultureInfo.InvariantCulture));
        o.AllDay = t.DateOnly;
        o.Start = t.DateOnly ? t.Wall.Date : t.Encore;
        o.LengthMin = len;
        o.LastDay = o.Start.Date;
        return o;
    }

    private static void Repeat(VEvent ev, IcsTime start, Rule rule, IEnumerable<VEvent> moved, int seq, DateTime now, Result res)
    {
        int len = LengthOf(ev, start);

        // dates the repeat skips: EXDATE, and the original dates of moved occurrences
        var skipAt = new HashSet<long>();
        var skipDay = new HashSet<DateTime>();
        DateTime lastSkip = DateTime.MinValue;
        long Key(DateTime minute) => minute.Ticks / TimeSpan.TicksPerMinute;
        void Skip(IcsTime t)
        {
            if (t.DateOnly && !start.DateOnly) { skipDay.Add(t.Wall.Date); lastSkip = Max(lastSkip, t.Wall.Date.AddDays(1)); return; }
            // the date in the repeat's own terms: its zone's wall-clock time
            DateTime w = start.DateOnly ? t.Wall.Date
                       : t.Instant is { } inst && start.Zone is not null ? TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(inst, DateTimeKind.Utc), start.Zone)
                       : t.Instant is { } inst2 && start.Utc ? inst2
                       : t.Instant is { } inst3 ? UtcToEncore(inst3)
                       : t.Wall;
            skipAt.Add(Key(w));
            lastSkip = Max(lastSkip, w);
        }
        foreach (var p in ev.All("EXDATE")) foreach (var t in ParseTimes(p)) Skip(t);
        foreach (var m in moved) if (ParseTime(m.Get("RECURRENCE-ID"), start) is { } rid) Skip(rid);
        bool Skipped(DateTime w) => skipAt.Contains(Key(w)) || skipDay.Contains(w.Date);

        OutEvent Make(DateTime wall, string? rrule, DateTime lastWall, string suffix)
        {
            var o = Shell(ev, seq, suffix);
            var t = start.At(wall);
            o.AllDay = start.DateOnly;
            o.Start = start.DateOnly ? wall.Date : t.Encore;
            o.LengthMin = len;
            o.RRule = rrule;
            o.LastDay = lastWall == DateTime.MaxValue ? DateTime.MaxValue : (start.DateOnly ? lastWall.Date : start.At(lastWall).Encore.Date);
            return o;
        }
        string Until(DateTime lastWall) => start.DateOnly ? lastWall.ToString("yyyyMMdd", CultureInfo.InvariantCulture)
                                                         : start.At(lastWall).Encore.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + "T235959";

        int? shift = ShiftOf(start, now);
        string? native = shift is { } s ? EncoreRule(rule, start, s) : null;
        bool forever = rule.Count == 0 && rule.Until is null;

        if (native is not null)
        {
            if (forever && skipAt.Count == 0 && skipDay.Count == 0)
            {
                res.Events.Add(Make(start.Wall, native, DateTime.MaxValue, ""));
                res.Repeats++;
                return;
            }
            // the repeat's dates up to its end, or just past the last skipped one when it runs on (UNTIL far away, or for ever)
            var stop = rule.Count > 0 ? DateTime.MaxValue : Max(lastSkip, start.Wall);
            var (occ, complete) = Expand(rule, start, stop);
            var runs = new List<List<DateTime>>();
            List<DateTime>? run = null;
            foreach (var w in occ)
            {
                if (Skipped(w)) { run = null; continue; }
                if (run is null) runs.Add(run = new List<DateTime>());
                run.Add(w);
            }
            if (runs.Count <= MaxSegments)
            {
                for (int i = 0; i < runs.Count; i++)
                {
                    var r = runs[i];
                    bool open = !complete && i == runs.Count - 1;   // the run the repeat goes on in, past what was listed
                    string suffix = runs.Count > 1 ? "-" + (i + 1) : "";
                    if (open)
                    {
                        // to the rule's own UNTIL (the last day an occurrence can start on), or for ever
                        DateTime last = DateTime.MaxValue;
                        if (rule.Until is { } u)
                        {
                            var uw = u.DateOnly ? u.Wall.Date.Add(start.Wall.TimeOfDay)
                                   : u.Instant is { } ui && start.Zone is not null ? TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(ui, DateTimeKind.Utc), start.Zone)
                                   : u.Instant is { } ui2 && start.Utc ? ui2 : u.Encore;
                            last = uw.TimeOfDay >= start.Wall.TimeOfDay ? uw.Date.Add(start.Wall.TimeOfDay) : uw.Date.AddDays(-1).Add(start.Wall.TimeOfDay);
                        }
                        res.Events.Add(Make(r[0], last == DateTime.MaxValue ? native : native + ";UNTIL=" + Until(last), last, suffix));
                    }
                    else if (r.Count == 1) res.Events.Add(Make(r[0], null, r[0], suffix));
                    else res.Events.Add(Make(r[0], native + ";UNTIL=" + Until(r[^1]), r[^1], suffix));
                }
                if (runs.Count > 1 || skipAt.Count + skipDay.Count > 0) res.Split++; else res.Repeats++;
                return;
            }
        }

        // a repeat the iPod can't read (or one with very many skipped dates): its dates, from a year back to two ahead
        DateTime from = now.AddYears(-1), to = now.AddYears(2);
        var (all, _) = Expand(rule, start, to.AddDays(2));
        int n = 0;
        foreach (var w in all)
        {
            if (Skipped(w)) continue;
            var o = Make(w, null, w, "-" + w.ToString("yyyyMMddTHHmm", CultureInfo.InvariantCulture));
            if (o.Start < from || o.Start > to) continue;
            res.Events.Add(o);
            if (++n >= MaxExpanded) break;
        }
        res.SpelledOut++;
    }

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;

    // ---- the file --------------------------------------------------------------------------------------------

    /// <summary>The ICS text Mixtape writes for Encore.</summary>
    public static string Write(IEnumerable<OutEvent> events, string calName)
    {
        var sb = new StringBuilder();
        sb.Append("BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:").Append(ProdId).Append("\r\n");
        if (calName.Length > 0) sb.Append("X-WR-CALNAME:").Append(Escape(calName)).Append("\r\n");
        foreach (var e in events) AppendEvent(sb, e);
        sb.Append("END:VCALENDAR\r\n");
        return sb.ToString();
    }

    private static void AppendEvent(StringBuilder sb, OutEvent e)
    {
        string Fmt(DateTime d) => e.AllDay ? d.ToString("yyyyMMdd", CultureInfo.InvariantCulture) : d.ToString("yyyyMMdd'T'HHmm'00'", CultureInfo.InvariantCulture);
        string val = e.AllDay ? ";VALUE=DATE:" : ":";
        sb.Append("BEGIN:VEVENT\r\nUID:").Append(e.Uid).Append("\r\n");
        sb.Append("DTSTART").Append(val).Append(Fmt(e.Start)).Append("\r\n");
        if (e.LengthMin > 0)
        {
            var end = e.AllDay ? e.Start.AddDays(Math.Max(1, (e.LengthMin + 1439) / 1440)) : e.Start.AddMinutes(e.LengthMin);
            sb.Append("DTEND").Append(val).Append(Fmt(end)).Append("\r\n");
        }
        if (e.RRule is not null) sb.Append("RRULE:").Append(e.RRule).Append("\r\n");
        sb.Append("SUMMARY:").Append(e.Summary).Append("\r\n");
        if (e.Location.Length > 0) sb.Append("LOCATION:").Append(e.Location).Append("\r\n");
        if (e.Description.Length > 0) sb.Append("DESCRIPTION:").Append(e.Description).Append("\r\n");
        sb.Append("END:VEVENT\r\n");
    }

    private static int EventBytes(OutEvent e) { var sb = new StringBuilder(); AppendEvent(sb, e); return Encoding.UTF8.GetByteCount(sb.ToString()); }

    /// <summary>Keep within what Encore reads: at most <see cref="MaxEvents"/> events in all, and every file under
    /// <see cref="MaxFileBytes"/> (a bigger file is skipped whole). What goes first is what is longest over: single
    /// dates furthest in the past, then (only if that isn't enough) spelled-out dates furthest in the future.
    /// Returns how many events were left out. <paramref name="maxEvents"/>: what is left of Encore's limit after the
    /// calendar files somebody else put there.</summary>
    public static int Fit(IReadOnlyList<List<OutEvent>> calendars, DateTime now, int maxEvents = MaxEvents)
    {
        int dropped = 0;
        var today = now.Date;
        foreach (var cal in calendars)
        {
            long bytes = 200 + cal.Sum(e => (long)EventBytes(e));
            if (bytes <= MaxFileBytes) continue;
            foreach (var e in cal.Where(e => e.RRule is null).OrderBy(e => e.LastDay).ToList())
            {
                if (bytes <= MaxFileBytes) break;
                bytes -= EventBytes(e);
                cal.Remove(e);
                dropped++;
            }
        }
        int total = calendars.Sum(c => c.Count);
        if (total <= maxEvents) return dropped;
        var singles = calendars.SelectMany(c => c.Where(e => e.RRule is null).Select(e => (Cal: c, Ev: e))).ToList();
        var order = singles.Where(x => x.Ev.LastDay < today).OrderBy(x => x.Ev.LastDay)
                           .Concat(singles.Where(x => x.Ev.LastDay >= today).OrderByDescending(x => x.Ev.LastDay));
        foreach (var (c, e) in order.ToList())
        {
            if (total <= maxEvents) break;
            c.Remove(e);
            total--;
            dropped++;
        }
        return dropped;
    }

    // ---- text ------------------------------------------------------------------------------------------------

    private static string Unescape(string v)
    {
        var sb = new StringBuilder(v.Length);
        for (int i = 0; i < v.Length; i++)
        {
            if (v[i] == '\\' && i + 1 < v.Length) { char c = v[++i]; sb.Append(c is 'n' or 'N' ? ' ' : c); }
            else sb.Append(v[i]);
        }
        return sb.ToString();
    }

    private static string Escape(string v) => v.Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,").Replace("\r", "").Replace("\n", "\\n");

    /// <summary>True when the file at <paramref name="path"/> is one Mixtape wrote (its PRODID, in the first lines).</summary>
    public static bool IsMixtapes(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var buf = new byte[512];
            int n = fs.Read(buf, 0, buf.Length);
            return Encoding.UTF8.GetString(buf, 0, n).Contains("PRODID:" + ProdId, StringComparison.Ordinal);
        }
        catch { return false; }
    }

    // ---- Encore's own reading, for the tests -----------------------------------------------------------------

    /// <summary>
    /// events.c, line for line: how Encore reads a calendar file and on which days it shows each event. The self-test
    /// checks Mixtape's files through this - what the iPod will show, not what the file says.
    /// </summary>
    internal static class EncoreReader
    {
        internal sealed class Ev
        {
            public string Summary = "", Location = "", Desc = "";
            public int Day0, Until = int.MaxValue, LenMin, StartMin, Interval = 1, Count, Freq;
            public byte ByDay;
            public bool AllDay;
        }

        private static readonly DateTime Epoch = new(2000, 1, 1);
        public static int Days(DateTime d) => (int)(d.Date - Epoch).TotalDays;
        private static int Weekday(int day) => ((day % 7) + 7 + 5) % 7;

        private static int Num(string s, int at, int n)
        {
            int v = 0;
            for (int i = 0; i < n; i++)
            {
                if (at + i >= s.Length || s[at + i] < '0' || s[at + i] > '9') return -1;
                v = v * 10 + s[at + i] - '0';
            }
            return v;
        }

        private static bool ParseDt(string v, out long minutes, out bool dateOnly)
        {
            minutes = 0; dateOnly = false;
            int y = Num(v, 0, 4), mo = Num(v, 4, 2), d = Num(v, 6, 2);
            if (y < 1900 || mo < 1 || mo > 12 || d < 1 || d > 31) return false;
            DateTime day;
            try { day = new DateTime(y, mo, 1).AddDays(d - 1); } catch { return false; }
            long m = (long)Days(day) * 1440;
            dateOnly = v.Length <= 8 || v[8] != 'T';
            if (!dateOnly)
            {
                int h = Num(v, 9, 2), mi = Num(v, 11, 2);
                if (h < 0 || h > 23 || mi < 0 || mi > 59) return false;
                m += h * 60 + mi;
                if (v.Length > 15 && v[15] == 'Z') m = (long)(UtcToEncore(Epoch.AddMinutes(m)) - Epoch).TotalMinutes;
            }
            minutes = m;
            return true;
        }

        private static string Unesc(string v, bool keepNewlines)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < v.Length; i++)
            {
                if (v[i] == '\\' && i + 1 < v.Length) { char c = v[++i]; sb.Append(c is 'n' or 'N' ? (keepNewlines ? '\n' : ' ') : c); }
                else sb.Append(v[i]);
            }
            return sb.ToString();
        }

        public static List<Ev> Parse(string text)
        {
            var evs = new List<Ev>();
            var t = text.Replace("\r", "").Replace("\n ", "").Replace("\n\t", "");
            Ev e = new();
            bool inside = false, haveStart = false, haveEnd = false, startDateOnly = false;
            long start = 0, end = 0;
            int dur = -1;
            foreach (string line in t.Split('\n'))
            {
                int colon = -1;
                bool q = false;
                for (int i = 0; i < line.Length; i++) { if (line[i] == '"') q = !q; else if (line[i] == ':' && !q) { colon = i; break; } }
                if (colon < 0) continue;
                string name = line[..colon], val = line[(colon + 1)..];
                int semi = name.IndexOf(';');
                if (semi >= 0) name = name[..semi];
                if (name == "BEGIN" && val == "VEVENT") { e = new Ev(); inside = true; haveStart = haveEnd = false; dur = -1; continue; }
                if (!inside) continue;
                if (name == "END" && val == "VEVENT")
                {
                    inside = false;
                    if (!haveStart || evs.Count >= MaxEvents) continue;
                    e.AllDay = startDateOnly;
                    long len = haveEnd ? end - start : dur >= 0 ? dur : startDateOnly ? 1440 : 0;
                    e.LenMin = (int)Math.Max(0, len);
                    e.Day0 = (int)(start >= 0 ? start / 1440 : (start - 1439) / 1440);
                    e.StartMin = (int)(start - (long)e.Day0 * 1440);
                    if (e.Summary.Length == 0) e.Summary = "(No title)";
                    if (e.Freq == 2 && e.ByDay == 0) e.ByDay = (byte)(1 << Weekday(e.Day0));
                    evs.Add(e);
                    continue;
                }
                switch (name)
                {
                    case "SUMMARY": e.Summary = Unesc(val, false); break;
                    case "LOCATION": e.Location = Unesc(val, false); break;
                    case "DESCRIPTION": e.Desc = Unesc(val, true); break;
                    case "DTSTART": haveStart = ParseDt(val, out start, out startDateOnly); break;
                    case "DTEND": haveEnd = ParseDt(val, out end, out _); break;
                    case "DURATION": dur = ParseDuration(val); break;
                    case "RRULE": Rrule(e, val.Length > 255 ? val[..255] : val); break;
                }
            }
            return evs;
        }

        private static void Rrule(Ev e, string v)
        {
            e.Interval = 1;
            foreach (string p in v.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = p.IndexOf('=');
                if (eq < 0) continue;
                string k = p[..eq], x = p[(eq + 1)..];
                static int Atoi(string s) { int n = 0, i = 0; while (i < s.Length && char.IsAsciiDigit(s[i])) n = n * 10 + s[i++] - '0'; return n; }
                switch (k)
                {
                    case "FREQ": e.Freq = x == "DAILY" ? 1 : x == "WEEKLY" ? 2 : x == "MONTHLY" ? 3 : x == "YEARLY" ? 4 : 0; break;
                    case "INTERVAL": e.Interval = Atoi(x) > 0 ? Atoi(x) : 1; break;
                    case "COUNT": e.Count = Atoi(x); break;
                    case "UNTIL": if (ParseDt(x, out long m, out _)) e.Until = (int)(m >= 0 ? m / 1440 : (m - 1439) / 1440); break;
                    case "BYDAY":
                        string[] wd = { "MO", "TU", "WE", "TH", "FR", "SA", "SU" };
                        for (int i = 0; i < 7; i++) if (x.Contains(wd[i], StringComparison.Ordinal)) e.ByDay |= (byte)(1 << i);
                        break;
                }
            }
        }

        private static int SpanDays(Ev e)
        {
            int endm = e.StartMin + e.LenMin;
            int sp = e.AllDay ? (e.LenMin + 1439) / 1440 : (endm > 0 ? endm - 1 : 0) / 1440 + 1;
            return sp < 1 ? 1 : sp > 62 ? 62 : sp;
        }

        private static int Bits(byte m, int n) { int c = 0; for (int i = 0; i < n; i++) c += (m >> i) & 1; return c; }

        public static bool StartsOn(Ev e, int o)
        {
            if (o < e.Day0 || o > e.Until) return false;
            long idx;
            switch (e.Freq)
            {
                case 0: return o == e.Day0;
                case 1:
                    if ((o - e.Day0) % e.Interval != 0) return false;
                    idx = (o - e.Day0) / e.Interval;
                    break;
                case 2:
                {
                    if (((e.ByDay >> Weekday(o)) & 1) == 0) return false;
                    int w0 = e.Day0 - Weekday(e.Day0), w = o - Weekday(o);
                    long weeks = (w - w0) / 7;
                    if (weeks % e.Interval != 0) return false;
                    idx = weeks / e.Interval * Bits(e.ByDay, 7) + Bits(e.ByDay, Weekday(o)) - Bits(e.ByDay, Weekday(e.Day0));
                    break;
                }
                default:
                {
                    var a = Epoch.AddDays(e.Day0); var b = Epoch.AddDays(o);
                    if (a.Day != b.Day) return false;
                    if (e.Freq == 4)
                    {
                        if (a.Month != b.Month || (b.Year - a.Year) % e.Interval != 0) return false;
                        idx = (b.Year - a.Year) / e.Interval;
                    }
                    else
                    {
                        long months = (long)(b.Year - a.Year) * 12 + (b.Month - a.Month);
                        if (months % e.Interval != 0) return false;
                        idx = months / e.Interval;
                    }
                    break;
                }
            }
            return e.Count == 0 || idx < e.Count;
        }

        public static bool OccursOn(Ev e, int day)
        {
            int sp = SpanDays(e);
            for (int k = 0; k < sp; k++) if (StartsOn(e, day - k)) return true;
            return false;
        }

        /// <summary>"yyyy-MM-dd HH:mm Summary" for every start Encore shows between two dates (all-day: no time).</summary>
        public static List<string> Starts(string text, DateTime from, DateTime to)
        {
            var list = new List<string>();
            var evs = Parse(text);
            for (int d = Days(from); d <= Days(to); d++)
                foreach (var e in evs)
                    if (StartsOn(e, d))
                        list.Add(Epoch.AddDays(d).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) +
                                 (e.AllDay ? "" : Epoch.AddMinutes(e.StartMin).ToString(" HH:mm", CultureInfo.InvariantCulture)) + " " + e.Summary);
            list.Sort(StringComparer.Ordinal);
            return list;
        }
    }
}

/// <summary>A calendar Mixtape puts on an Encore iPod: a name (the file's name on the iPod) and where it comes from - a
/// .ics file on this PC, or a calendar's address (Google's "secret address in iCal format", webcal://…).</summary>
internal sealed class EncoreCalendarSource
{
    public string Name { get; set; } = "";
    public string Location { get; set; } = "";

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsLink => Location.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || Location.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                       || Location.StartsWith("webcal://", StringComparison.OrdinalIgnoreCase) || Location.StartsWith("webcals://", StringComparison.OrdinalIgnoreCase);

    /// <summary>What to show for the source: a file's path, or only a link's host - the rest of a secret address is a
    /// password and stays off the screen.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string Display => IsLink && Uri.TryCreate(Location.Replace("webcal", "https", StringComparison.OrdinalIgnoreCase), UriKind.Absolute, out var u)
        ? u.Host + "/…" : Location;
}
