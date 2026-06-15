using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace iPodCommander;

/// <summary>
/// Reads an iPod's FireWire GUID straight from the device over USB — the same data iTunes and
/// libgpod's <c>ipod-read-sysinfo-extended</c> obtain — so a Windows/drag-drop-managed iPod that has
/// NO SysInfo/SysInfoExtended on disk can still be signed (hash58 needs the GUID).
///
/// Two independent routes, both strictly READ-ONLY and (for the iPod's removable volume) un-elevated:
///   1. SCSI INQUIRY EVPD vendor page 0xC0 → a directory of further VPD pages → concatenate them into
///      the SysInfoExtended plist (XML on click-wheel iPods, sometimes bplist on later ones) → read the
///      <c>FireWireGUID</c> key. This is authoritative and the assembled bytes can be persisted to
///      <c>iPod_Control/Device/SysInfoExtended</c> so the device looks normal afterwards.
///   2. Fallback: the USB serial number (== the GUID for click-wheel iPods) via
///      IOCTL_STORAGE_QUERY_PROPERTY (some USB stacks byte-swap it in nibble pairs, so we also report a
///      de-swapped variant for cross-checking).
///
/// Protocol + Windows mechanics verified against libgpod tools/ipod-scsi.c, sg3_utils, a real nano-3G
/// SysInfoExtended dump, and Microsoft "ACLs and the Device Stack" / IOCTL_SCSI_PASS_THROUGH docs.
/// </summary>
internal static class IpodGuidReader
{
    const uint IOCTL_SCSI_PASS_THROUGH = 0x4D004;          // buffered; payload is tiny (<256B)
    const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x2D1400;
    const byte SCSI_IOCTL_DATA_IN = 1;
    const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
    const uint FILE_SHARE_RW = 0x3, OPEN_EXISTING = 3;

    [StructLayout(LayoutKind.Sequential)]
    struct SCSI_PASS_THROUGH
    {
        public ushort Length; public byte ScsiStatus; public byte PathId; public byte TargetId; public byte Lun;
        public byte CdbLength; public byte SenseInfoLength; public byte DataIn;
        public uint DataTransferLength; public uint TimeOutValue;
        public IntPtr DataBufferOffset; public uint SenseInfoOffset;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] Cdb;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SPT_WITH_BUFFERS
    {
        public SCSI_PASS_THROUGH spt; public uint Filler;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] Sense;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)] public byte[] Data;
    }

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sa, uint disp, uint flags, IntPtr tmpl);
    [DllImport("kernel32", SetLastError = true)]
    static extern bool DeviceIoControl(SafeFileHandle h, uint code, ref SPT_WITH_BUFFERS inb, int inLen, ref SPT_WITH_BUFFERS outb, int outLen, out int ret, IntPtr ovl);
    [DllImport("kernel32", SetLastError = true)]
    static extern bool DeviceIoControl(SafeFileHandle h, uint code, byte[] inb, int inLen, byte[] outb, int outLen, out int ret, IntPtr ovl);

    static SafeFileHandle Open(char drive)
    {
        // R+W is MANDATORY for SCSI pass-through (the CDB opcode isn't inspected) — read-only → ACCESS_DENIED.
        var h = CreateFile($@"\\.\{char.ToUpperInvariant(drive)}:", GENERIC_READ | GENERIC_WRITE, FILE_SHARE_RW, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (h.IsInvalid) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), $@"open \\.\{drive}:");
        return h;
    }

    /// <summary>One INQUIRY EVPD page; returns the page payload (bytes 4 .. 4+length).</summary>
    static byte[] InquiryPage(SafeFileHandle h, byte page, byte targetId)
    {
        var w = new SPT_WITH_BUFFERS
        {
            Sense = new byte[32],
            Data = new byte[256],
            spt = new SCSI_PASS_THROUGH { Cdb = new byte[16] },
        };
        w.spt.Length = (ushort)Marshal.SizeOf<SCSI_PASS_THROUGH>();
        w.spt.PathId = 0; w.spt.TargetId = targetId; w.spt.Lun = 0;
        w.spt.CdbLength = 6; w.spt.SenseInfoLength = 32; w.spt.DataIn = SCSI_IOCTL_DATA_IN;
        w.spt.DataTransferLength = 252; w.spt.TimeOutValue = 10;
        w.spt.SenseInfoOffset = (uint)Marshal.OffsetOf<SPT_WITH_BUFFERS>(nameof(SPT_WITH_BUFFERS.Sense));
        w.spt.DataBufferOffset = (IntPtr)(long)(uint)Marshal.OffsetOf<SPT_WITH_BUFFERS>(nameof(SPT_WITH_BUFFERS.Data));
        // INQUIRY, EVPD=1, page, alloc length 0x00FC (252, big-endian), control 0.
        w.spt.Cdb[0] = 0x12; w.spt.Cdb[1] = 0x01; w.spt.Cdb[2] = page; w.spt.Cdb[3] = 0x00; w.spt.Cdb[4] = 0xFC; w.spt.Cdb[5] = 0x00;

        int sz = Marshal.SizeOf<SPT_WITH_BUFFERS>();
        if (!DeviceIoControl(h, IOCTL_SCSI_PASS_THROUGH, ref w, sz, ref w, sz, out _, IntPtr.Zero))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), $"INQUIRY 0x{page:X2}");
        if (w.spt.ScsiStatus != 0)
            throw new InvalidOperationException($"INQUIRY 0x{page:X2}: SCSI status 0x{w.spt.ScsiStatus:X2}, sense=" + Convert.ToHexString(w.Sense ?? Array.Empty<byte>()));
        // VPD page length (byte 3). The device reports the page's TRUE length, which can exceed the
        // bytes that fit in our 256-byte buffer — clamp to the payload region so a large/garbage page
        // can never read out of bounds (Array.Copy from [4 .. 4+len) must stay within Data).
        int len = Math.Min(w.Data[3], w.Data.Length - 4);
        var payload = new byte[len];
        Array.Copy(w.Data, 4, payload, 0, len);
        return payload;
    }

    /// <summary>
    /// SCSI route: assemble the SysInfoExtended document. Page 0xC0 returns an ordered list of further
    /// VPD page codes (stop at the first 0x00); each is read and its payload concatenated.
    /// </summary>
    public static byte[] ReadSysInfoExtendedScsi(char drive, byte targetId = 0)
    {
        using var h = Open(drive);
        byte[] pageCodes = InquiryPage(h, 0xC0, targetId);
        var doc = new List<byte>(4096);
        foreach (byte code in pageCodes)
        {
            if (code == 0) break;
            doc.AddRange(InquiryPage(h, code, targetId));
        }
        return doc.ToArray();
    }

    /// <summary>Pull the FireWireGUID out of an assembled SysInfoExtended blob (XML or bplist). Null if absent.</summary>
    public static string? ExtractFireWireGuid(byte[] doc)
    {
        if (doc.Length == 0) return null;
        // Binary plist?
        if (doc.Length >= 8 && Encoding.ASCII.GetString(doc, 0, 8) == "bplist00")
        {
            var map = BinaryPlist.Flatten(doc);
            if (map != null && map.TryGetValue("FireWireGUID", out var bv)) return SysInfoParser.NormalizeGuid(bv);
            return null;
        }
        // XML / text plist.
        string xml = Encoding.UTF8.GetString(doc);
        var m = System.Text.RegularExpressions.Regex.Match(xml,
            @"<key>\s*FireWireGUID\s*</key>\s*<string>\s*([0-9A-Fa-f]{16})\s*</string>",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return m.Success ? SysInfoParser.NormalizeGuid(m.Groups[1].Value) : null;
    }

    /// <summary>
    /// Fallback: the storage device serial via IOCTL_STORAGE_QUERY_PROPERTY (== the GUID for click-wheel
    /// iPods). Returns the raw serial string, or null. Some USB stacks byte-swap it in nibble pairs.
    /// </summary>
    public static string? ReadStorageSerial(char drive)
    {
        using var h = CreateFile($@"\\.\{char.ToUpperInvariant(drive)}:", GENERIC_READ, FILE_SHARE_RW, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (h.IsInvalid) return null;
        var query = new byte[12];            // STORAGE_PROPERTY_QUERY: PropertyId=StorageDeviceProperty(0), QueryType=Standard(0)
        var outBuf = new byte[1024];
        if (!DeviceIoControl(h, IOCTL_STORAGE_QUERY_PROPERTY, query, query.Length, outBuf, outBuf.Length, out int ret, IntPtr.Zero) || ret < 28)
            return null;
        uint serialOff = BitConverter.ToUInt32(outBuf, 24);   // STORAGE_DEVICE_DESCRIPTOR.SerialNumberOffset
        if (serialOff == 0 || serialOff >= outBuf.Length) return null;
        int end = (int)serialOff;
        while (end < outBuf.Length && outBuf[end] != 0) end++;
        string s = Encoding.ASCII.GetString(outBuf, (int)serialOff, end - (int)serialOff).Trim();
        return s.Length == 0 ? null : s;
    }

    /// <summary>Nibble-pair byte-swap (some Windows USB stacks return the serial this way): "0123" → "1032".</summary>
    public static string SwapPairs(string s)
    {
        var c = s.ToCharArray();
        for (int i = 0; i + 1 < c.Length; i += 2) (c[i], c[i + 1]) = (c[i + 1], c[i]);
        return new string(c);
    }
}
