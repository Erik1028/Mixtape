using System.Buffers.Binary;

namespace iPodCommander;

/// <summary>
/// Applies the right iTunesDB signature to a serialized database buffer just before it is
/// written. NONE devices need nothing. Hash58 devices (Classic 1–3, Nano 3G/4G) get a real
/// HMAC-SHA1 signature computed from their FireWire GUID. Hash72/HashAB are not generatable
/// here and must be gated off upstream (this throws if reached).
/// </summary>
internal static class ChecksumWriter
{
    public static void Apply(byte[] buffer, ChecksumScheme scheme, string? firewireGuidHex)
    {
        switch (scheme)
        {
            case ChecksumScheme.None:
                return; // unsigned db; nothing to do

            case ChecksumScheme.Hash58:
                ApplyHash58(buffer, firewireGuidHex);
                return;

            default:
                throw new NotSupportedException($"Writing a {scheme} iTunesDB is not supported.");
        }
    }

    /// <summary>
    /// Known-answer self-test: recompute the hash58 signature over the device's EXISTING (unmodified)
    /// iTunesDB and compare it to the signature already stored at 0x58. Returns true if they match
    /// (our signing reproduces what the firmware wrote → writes are safe), false if they differ (our
    /// signing is wrong → writing would corrupt the DB), or null if it can't be determined (no GUID,
    /// no stored signature to compare against, or too small). Never mutates the input.
    /// </summary>
    public static bool? VerifyHash58(byte[] originalDb, string? firewireGuidHex)
    {
        if (originalDb.Length < 0x6C) return null;
        if (ParseGuid(firewireGuidHex) is null) return null;
        byte[] stored = originalDb.AsSpan(0x58, 20).ToArray();
        if (stored.All(b => b == 0)) return null; // device has no signature yet — nothing to compare
        try
        {
            byte[] clone = (byte[])originalDb.Clone();
            ApplyHash58(clone, firewireGuidHex);
            return clone.AsSpan(0x58, 20).SequenceEqual(stored);
        }
        catch { return null; }
    }

    /// <summary>
    /// From an ordered list of candidate GUIDs (e.g. a SCSI-read GUID, a USB serial, and its
    /// byte-swapped variant), return the first one whose hash58 signing reproduces the device's
    /// EXISTING stored signature — i.e. <see cref="VerifyHash58"/> returns true. This is the safe way
    /// to choose a recovered GUID: a wrong or byte-swapped candidate can never be accepted because it
    /// would not reproduce the firmware's signature. Returns null if none verifies (including when the
    /// DB has no stored signature to compare against — the caller must decide what to do then).
    /// Candidates are normalised and de-duplicated; null/blank entries are skipped.
    /// </summary>
    public static string? FirstGuidMatchingSignature(byte[] originalDb, IEnumerable<string?> candidates)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string? raw in candidates)
        {
            byte[]? bytes = ParseGuid(raw);
            if (bytes is null) continue;
            string norm = Convert.ToHexString(bytes); // 16 uppercase hex, no "0x"
            if (!seen.Add(norm)) continue;
            if (VerifyHash58(originalDb, norm) == true) return norm;
        }
        return null;
    }

    private static void ApplyHash58(byte[] buf, string? guidHex)
    {
        byte[]? fwid = ParseGuid(guidHex);
        if (fwid is null)
            throw new InvalidOperationException("This iPod needs a hash58 signature but no FireWire GUID was available.");
        if (buf.Length < 0x6C)
            throw new InvalidDataException("iTunesDB too small to sign.");

        // Back up the regions that are excluded from the hash, then zero them. hashing_scheme
        // (@0x30) is set to 1 and IS part of the hashed data.
        byte[] dbId = buf.AsSpan(0x18, 8).ToArray();
        byte[] unk = buf.AsSpan(0x32, 20).ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(0x30, 2), 1); // ITDB_CHECKSUM_HASH58
        buf.AsSpan(0x18, 8).Clear();
        buf.AsSpan(0x32, 20).Clear();
        buf.AsSpan(0x58, 20).Clear();

        byte[] hash = Hash58.Compute(fwid, buf); // 20 bytes
        hash.AsSpan(0, 20).CopyTo(buf.AsSpan(0x58, 20));

        // Restore the excluded fields (they live in the file, just not in the hash).
        dbId.CopyTo(buf.AsSpan(0x18, 8));
        unk.CopyTo(buf.AsSpan(0x32, 20));
    }

    /// <summary>16 hex chars (optionally "0x"-prefixed) → 8 bytes, or null if not a usable GUID.</summary>
    private static byte[]? ParseGuid(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        string s = hex.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        if (s.Length != 16) return null; // exactly 8 bytes — never silently truncate a longer string
        var b = new byte[8];
        for (int i = 0; i < 8; i++)
            if (!byte.TryParse(s.AsSpan(i * 2, 2), System.Globalization.NumberStyles.HexNumber, null, out b[i]))
                return null;
        return b;
    }
}
