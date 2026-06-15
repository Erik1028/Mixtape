using System.Xml;

namespace iPodCommander;

/// <summary>
/// Reads the handful of values we need from iPod_Control/Device/SysInfoExtended, an Apple
/// plist. On click-wheel iPods this is XML, which we parse with System.Xml (no NuGet needed
/// for Milestone 1). If the file is the binary plist variant, parsing fails gracefully and
/// we fall back to SysInfo + the model table.
///
/// Keys of interest: FireWireGUID (note plist casing, usually no "0x"), SerialNumber,
/// FamilyID, DBVersion (0/1/2→none, 3→hash58, 4→hash72, 5→hashAB).
/// </summary>
internal sealed class SysInfoExtended
{
    public string? FirewireGuid;
    public string? SerialNumber;
    public int? FamilyId;
    public int? UpdaterFamilyId; // splits the FamilyID collisions (mini1/2, 5G/5.5G, classic1/2/3)
    public int? DbVersion;

    public static SysInfoExtended? TryParse(string path)
    {
        if (!File.Exists(path)) return null;
        try { return TryParse(File.ReadAllBytes(path)); }
        catch { return null; }
    }

    /// <summary>Parse SysInfoExtended directly from bytes (also used to validate a SCSI-read doc before persisting it).</summary>
    public static SysInfoExtended? TryParse(byte[] data)
    {
        try
        {
            // Newer iPods (nano 3G/4G, classic, …) store SysInfoExtended as a BINARY plist; older
            // click-wheel ones use XML. Parse whichever it is into the same key→value map. Getting the
            // binary form right is what lets a hash58 device's FireWireGUID be found → writes enabled.
            bool isBinary = data.Length >= 6 && System.Text.Encoding.ASCII.GetString(data, 0, 6) == "bplist";

            Dictionary<string, string>? map;
            if (isBinary)
            {
                map = BinaryPlist.Flatten(data);
            }
            else
            {
                var doc = new XmlDocument();
                using var ms = new MemoryStream(data);
                doc.Load(ms);
                map = FlattenTopDict(doc);
            }
            if (map is null) return null;

            var result = new SysInfoExtended();
            if (map.TryGetValue("FireWireGUID", out string? g)) result.FirewireGuid = SysInfoParser.NormalizeGuid(g);
            if (map.TryGetValue("SerialNumber", out string? s)) result.SerialNumber = s;
            if (map.TryGetValue("FamilyID", out string? f) && int.TryParse(f, out int fi)) result.FamilyId = fi;
            if (map.TryGetValue("UpdaterFamilyID", out string? uf) && int.TryParse(uf, out int ufi)) result.UpdaterFamilyId = ufi;
            if (map.TryGetValue("DBVersion", out string? d) && int.TryParse(d, out int di)) result.DbVersion = di;
            return result;
        }
        catch
        {
            return null; // malformed/unsupported — caller falls back to model table
        }
    }

    /// <summary>
    /// Flattens the top-level &lt;dict&gt; of a plist into key→string. plist dicts alternate
    /// &lt;key&gt; and value elements; we only need scalar string/integer values at the top level.
    /// </summary>
    private static Dictionary<string, string> FlattenTopDict(XmlDocument doc)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        XmlNode? dict = doc.SelectSingleNode("/plist/dict");
        if (dict is null) return map;

        XmlNode? node = dict.FirstChild;
        while (node is not null)
        {
            if (node.Name == "key")
            {
                string key = node.InnerText.Trim();
                XmlNode? val = node.NextSibling;
                if (val is not null)
                {
                    // Capture scalars; skip nested dict/array (not needed for our keys).
                    if (val.Name is "string" or "integer" or "real" or "data")
                        map[key] = val.InnerText.Trim();
                    else if (val.Name is "true") map[key] = "1";
                    else if (val.Name is "false") map[key] = "0";
                    node = val.NextSibling;
                    continue;
                }
            }
            node = node.NextSibling;
        }
        return map;
    }
}
