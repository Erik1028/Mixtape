namespace iPodCommander;

/// <summary>Outcome of an attempt to recover a hash58 iPod's FireWire GUID off the device.</summary>
internal enum GuidRecoveryStatus
{
    /// <summary>A read GUID provably reproduced the iPod's existing signature → persisted, writing safe.</summary>
    EnabledVerified,
    /// <summary>No stored signature to cross-check; a directly-read GUID was trusted → persisted (caller should warn).</summary>
    EnabledTrusted,
    /// <summary>An id was read but couldn't be verified, and trusting it wasn't permitted → nothing persisted.</summary>
    Unverified,
    /// <summary>The iPod has a signature and no read id reproduced it → wrong id; nothing persisted.</summary>
    Mismatch,
    /// <summary>No usable hardware id could be read from the device at all.</summary>
    NoIdFound,
    /// <summary>An id was chosen but couldn't be written back to the iPod.</summary>
    SaveFailed,
}

internal sealed class GuidRecoveryResult
{
    public GuidRecoveryStatus Status;
    public string? Guid;         // the chosen/observed GUID(s)
    public string? SwapVariant;  // byte-swapped alternative worth suggesting on the trusted path
    public string? Message;      // read/save error detail, when relevant
}

/// <summary>
/// Non-interactive engine behind both the manual "Read device ID" button and the automatic
/// recovery on device load. Reads a hash58 iPod's FireWire GUID over USB (SCSI INQUIRY and/or the
/// unprivileged USB storage serial), picks the candidate that reproduces the device's existing
/// signature, and persists it so re-detection enables writing. Shows no UI and does not re-detect —
/// the caller owns both.
///
/// Safety knobs:
///   <paramref name="allowScsi"/> — try the SCSI route (needs a read+write volume handle, so it can
///     require elevation). The automatic path passes false to stay unprivileged and silent.
///   <paramref name="allowTrustUnverified"/> — when the iPod has no stored signature to verify
///     against, persist a directly-read GUID anyway (true for the manual button; false for auto,
///     which never guesses on its own).
/// </summary>
internal static class GuidRecovery
{
    public static GuidRecoveryResult Recover(IPodDevice dev, bool allowScsi, bool allowTrustUnverified, bool dryRun = false)
    {
        string root = dev.MountRoot;
        if (root.Length < 2 || root[1] != ':')
            return new GuidRecoveryResult { Status = GuidRecoveryStatus.NoIdFound, Message = "Not a drive-letter mount." };
        char drive = root[0];

        // The iPod's current database: used to verify a recovered GUID against the signature already
        // on the device, and to know whether there even is a signature to cross-check against.
        byte[]? existingDb = TryReadAllBytesOrNull(dev.ITunesDbPath);
        bool hasStoredSignature = HasStoredHash58Signature(existingDb);

        byte[]? scsiDoc = null; string? serial = null; string? readErr = null;

        // Route A — SCSI INQUIRY: authoritative (a full SysInfoExtended document) but needs a read+write
        // volume handle, so it can fail with "access denied" unless elevated. Skipped on the auto path.
        if (allowScsi)
        {
            foreach (byte tid in new byte[] { 0, 1 })
            {
                try { scsiDoc = IpodGuidReader.ReadSysInfoExtendedScsi(drive, tid); break; }
                catch (Exception ex) { readErr = ex.Message; }
            }
        }
        // Route B — USB storage serial (== the FireWire GUID for click-wheel iPods), read with an
        // unprivileged read-only handle, so it succeeds in the common case where Route A is denied.
        try { serial = IpodGuidReader.ReadStorageSerial(drive); }
        catch (Exception ex) { readErr ??= ex.Message; }

        string? scsiGuid = scsiDoc is { Length: > 0 } ? IpodGuidReader.ExtractFireWireGuid(scsiDoc) : null;
        string? serialGuid = SysInfoParser.NormalizeGuid(serial);
        string? serialSwapGuid = serial is null ? null : SysInfoParser.NormalizeGuid(IpodGuidReader.SwapPairs(serial));

        if (scsiGuid is null && serialGuid is null && serialSwapGuid is null)
            return new GuidRecoveryResult { Status = GuidRecoveryStatus.NoIdFound, Message = readErr };

        // Whether the SCSI document is something DeviceDetector can re-read (same parser, so we never
        // persist a doc the firmware-read happened to regex-match but re-detection can't use).
        bool scsiDocPersistable = scsiDoc is { Length: > 0 } && SysInfoExtended.TryParse(scsiDoc!)?.FirewireGuid is not null;

        // Prefer a GUID that PROVABLY reproduces the iPod's existing signature — the safe choice.
        string? verified = existingDb is null ? null
            : ChecksumWriter.FirstGuidMatchingSignature(existingDb, new[] { scsiGuid, serialGuid, serialSwapGuid });

        string? chosen;
        bool persistScsiDoc;
        GuidRecoveryStatus status;

        if (verified is not null)
        {
            chosen = verified;
            persistScsiDoc = scsiDocPersistable && string.Equals(verified, scsiGuid, StringComparison.OrdinalIgnoreCase);
            status = GuidRecoveryStatus.EnabledVerified;
        }
        else if (hasStoredSignature)
        {
            // The device HAS a signature and none of our candidates reproduced it → the id we read is
            // wrong (or our signing is). Never persist here — it would corrupt the database.
            string ids = string.Join(", ", new[] { scsiGuid, serialGuid, serialSwapGuid }.Where(g => g is not null).Distinct());
            return new GuidRecoveryResult { Status = GuidRecoveryStatus.Mismatch, Guid = ids, SwapVariant = serialSwapGuid };
        }
        else
        {
            // No signature on the device to cross-check against.
            if (!allowTrustUnverified)
                return new GuidRecoveryResult
                {
                    Status = GuidRecoveryStatus.Unverified,
                    Guid = serialGuid ?? scsiGuid ?? serialSwapGuid,
                    SwapVariant = serialSwapGuid,
                };

            // Trust the directly-read GUID: prefer the authoritative SCSI document, else the USB serial.
            if (scsiDocPersistable) { chosen = scsiGuid; persistScsiDoc = true; }
            else if (serialGuid is not null) { chosen = serialGuid; persistScsiDoc = false; }
            else if (serialSwapGuid is not null) { chosen = serialSwapGuid; persistScsiDoc = false; }
            else { chosen = scsiGuid; persistScsiDoc = false; } // SCSI GUID but its doc isn't re-readable → persist bare GUID
            status = GuidRecoveryStatus.EnabledTrusted;
        }

        // Persist the chosen identity so re-detection enables writing (skipped in a read-only dry run).
        if (dryRun)
            return new GuidRecoveryResult { Status = status, Guid = chosen, SwapVariant = serialSwapGuid };
        try
        {
            if (persistScsiDoc)
            {
                string devDir = Path.Combine(root, "iPod_Control", "Device");
                Directory.CreateDirectory(devDir);
                File.WriteAllBytes(Path.Combine(devDir, "SysInfoExtended"), scsiDoc!);
            }
            else
            {
                DeviceInfoStore.WriteFirewireGuid(root, chosen!);
            }
        }
        catch (Exception ex)
        {
            return new GuidRecoveryResult { Status = GuidRecoveryStatus.SaveFailed, Guid = chosen, Message = ex.Message };
        }

        return new GuidRecoveryResult { Status = status, Guid = chosen, SwapVariant = serialSwapGuid };
    }

    /// <summary>Read a file's bytes, or null if it doesn't exist / can't be read.</summary>
    private static byte[]? TryReadAllBytesOrNull(string path)
    {
        try { return File.Exists(path) ? File.ReadAllBytes(path) : null; }
        catch { return null; }
    }

    /// <summary>True if the iTunesDB carries a non-empty hash58 signature at 0x58 (i.e. it was signed).</summary>
    private static bool HasStoredHash58Signature(byte[]? db)
        => db is { Length: >= 0x6C } && db.AsSpan(0x58, 20).ToArray().Any(b => b != 0);
}
