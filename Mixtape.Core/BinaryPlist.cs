using System.Globalization;
using System.Text;

namespace iPodCommander;

/// <summary>
/// A minimal, HARDENED reader for Apple binary property lists ("bplist00"), enough to flatten the
/// top-level dictionary of an iPod's <c>SysInfoExtended</c> into key→string. Newer click-wheel iPods
/// (nano 3G/4G, classic, …) store SysInfoExtended as a binary plist, and that's where the
/// <c>FireWireGUID</c> needed to sign a hash58 database lives — without this, those devices open
/// read-only. The file comes off a user's device so it is treated as UNTRUSTED: every read is
/// bounds-checked and never walks off the buffer (it returns null/skips instead). Only scalar values
/// (string/int/bool/data/real) are surfaced; nested containers are skipped.
/// </summary>
internal static class BinaryPlist
{
    public static Dictionary<string, string>? TryFlattenTopDict(string path)
    {
        try { return Flatten(File.ReadAllBytes(path)); }
        catch { return null; }
    }

    public static Dictionary<string, string>? Flatten(byte[] d)
    {
        if (d.Length < 40 || Encoding.ASCII.GetString(d, 0, 8) != "bplist00") return null;

        int tr = d.Length - 32;                       // 32-byte trailer
        int offsetSize = d[tr + 6];
        int refSize = d[tr + 7];
        long numObjects = ReadBE(d, tr + 8, 8);
        long topObject = ReadBE(d, tr + 16, 8);
        long offTableOff = ReadBE(d, tr + 24, 8);
        if (numObjects <= 0 || numObjects > 1_000_000 || offsetSize is < 1 or > 8 || refSize is < 1 or > 8) return null;
        if (topObject < 0 || topObject >= numObjects) return null;
        if (offTableOff < 0 || offTableOff > tr || offTableOff + numObjects * (long)offsetSize > tr) return null;

        var offsets = new long[numObjects];
        for (long i = 0; i < numObjects; i++)
            offsets[i] = ReadBE(d, (int)(offTableOff + i * offsetSize), offsetSize);

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        long top = offsets[topObject];
        if (top < 0 || top >= tr) return map;
        int p = (int)top;
        byte marker = d[p];
        if ((marker & 0xF0) != 0xD0) return map;       // top object isn't a dict

        int count = marker & 0x0F;
        p++;
        if (count == 0x0F) (count, p) = ReadCount(d, p);
        if (count < 0 || count > numObjects) return map;
        // keys + values: 2*count refs must fit before the offset table.
        if ((long)p + 2L * count * refSize > tr) return map;

        var keyRefs = new int[count];
        var valRefs = new int[count];
        for (int i = 0; i < count; i++) { keyRefs[i] = (int)ReadBE(d, p, refSize); p += refSize; }
        for (int i = 0; i < count; i++) { valRefs[i] = (int)ReadBE(d, p, refSize); p += refSize; }

        for (int i = 0; i < count; i++)
        {
            if (keyRefs[i] < 0 || keyRefs[i] >= numObjects || valRefs[i] < 0 || valRefs[i] >= numObjects) continue;
            string? k = ReadScalar(d, (int)offsets[keyRefs[i]]);
            if (k is null) continue;
            string? v = ReadScalar(d, (int)offsets[valRefs[i]]);
            if (v is not null) map[k] = v;
        }
        return map;
    }

    /// <summary>Read a scalar object to a string (string/int/bool/data-as-hex/real); null for containers or OOB.</summary>
    private static string? ReadScalar(byte[] d, int p)
    {
        if (p < 0 || p >= d.Length) return null;
        byte m = d[p];
        int hi = m & 0xF0, lo = m & 0x0F;
        switch (hi)
        {
            case 0x50: { var (len, q) = LenAt(d, p, lo); return Span(q, len, d.Length) ? Encoding.ASCII.GetString(d, q, len) : null; }   // ASCII
            case 0x60: { var (len, q) = LenAt(d, p, lo); long b = (long)len * 2; return len >= 0 && q >= 0 && b <= d.Length - q ? Encoding.BigEndianUnicode.GetString(d, q, (int)b) : null; } // UTF-16BE
            case 0x10: { if (lo > 3) return null; int n = 1 << lo; return ReadBE(d, p + 1, n).ToString(CultureInfo.InvariantCulture); } // int
            case 0x40: { var (len, q) = LenAt(d, p, lo); return Span(q, len, d.Length) ? Convert.ToHexString(d, q, len) : null; }        // data → hex
            case 0x00: return m == 0x09 ? "1" : m == 0x08 ? "0" : null;                                                                  // bool
            case 0x20: { if (lo != 3) return null; return BitConverter.Int64BitsToDouble(ReadBE(d, p + 1, 8)).ToString(CultureInfo.InvariantCulture); } // real(8)
            default: return null;
        }
    }

    private static bool Span(int start, int len, int total) => len >= 0 && start >= 0 && start <= total - len;

    /// <summary>Length for string/data markers: the low nibble, or (when 0xF) a following int object.</summary>
    private static (int Len, int DataStart) LenAt(byte[] d, int p, int lo)
    {
        if (lo != 0x0F) return (lo, p + 1);
        return ReadCount(d, p + 1);
    }

    /// <summary>Read an int OBJECT (marker 0x1k + 2^k bytes, k≤3) used as a count/length; returns (value, posAfter).</summary>
    private static (int Value, int PosAfter) ReadCount(byte[] d, int p)
    {
        if (p < 0 || p >= d.Length) return (-1, p);
        int k = d[p] & 0x0F;
        if (k > 3) return (-1, p + 1);          // a legal count/length int is ≤ 8 bytes
        int n = 1 << k;
        long v = ReadBE(d, p + 1, n);           // bounds-checked
        return (v < 0 || v > int.MaxValue ? -1 : (int)v, p + 1 + n);
    }

    private static long ReadBE(byte[] d, int off, int size)
    {
        if (size < 1 || size > 8 || off < 0 || off > d.Length - size)
            throw new InvalidDataException("bplist read out of range");
        long v = 0;
        for (int i = 0; i < size; i++) v = (v << 8) | d[off + i];
        return v;
    }
}
