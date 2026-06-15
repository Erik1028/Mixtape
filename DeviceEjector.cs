using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace iPodCommander;

/// <summary>
/// Safely ejects a removable volume by drive letter: flush pending writes, lock + dismount the volume,
/// then best-effort tell the device to eject its media. After this the iPod can be unplugged without
/// risking the database (the critical part is the flush + dismount). No admin rights needed for
/// removable media. Read/write open is required, same as the rest of our raw-volume access.
/// </summary>
internal static class DeviceEjector
{
    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr template);

    [DllImport("kernel32", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle h, uint code, IntPtr inBuf, uint inSz, IntPtr outBuf, uint outSz, out uint returned, IntPtr overlapped);

    [DllImport("kernel32", SetLastError = true)]
    private static extern bool FlushFileBuffers(SafeFileHandle h);

    private const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_RW = 0x00000003, OPEN_EXISTING = 3;
    private const uint FSCTL_LOCK_VOLUME = 0x00090018, FSCTL_DISMOUNT_VOLUME = 0x00090020;
    private const uint IOCTL_STORAGE_MEDIA_REMOVAL = 0x002D4804, IOCTL_STORAGE_EJECT_MEDIA = 0x002D4808;

    /// <summary>Eject the volume at <paramref name="driveLetter"/> (e.g. 'G'). Returns false + a reason on failure.</summary>
    public static bool TryEject(char driveLetter, out string message)
    {
        message = "";
        try
        {
            using var h = CreateFile($@"\\.\{char.ToUpperInvariant(driveLetter)}:",
                GENERIC_READ | GENERIC_WRITE, FILE_SHARE_RW, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (h.IsInvalid) { message = "Couldn't open the iPod's drive."; return false; }

            FlushFileBuffers(h); // commit any cached writes first

            // Lock the volume — retry briefly in case the OS is momentarily holding it.
            bool locked = false;
            for (int i = 0; i < 10 && !locked; i++)
            {
                locked = DeviceIoControl(h, FSCTL_LOCK_VOLUME, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
                if (!locked) System.Threading.Thread.Sleep(150);
            }
            if (!locked)
            {
                message = "Something is still using the iPod (a file may be open). Close it and try again, or use Windows' “Safely Remove Hardware”.";
                return false;
            }

            DeviceIoControl(h, FSCTL_DISMOUNT_VOLUME, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);

            // Allow removal + best-effort media eject (no-op on drives that don't support it — harmless).
            IntPtr prevent = Marshal.AllocHGlobal(1);
            try { Marshal.WriteByte(prevent, 0); DeviceIoControl(h, IOCTL_STORAGE_MEDIA_REMOVAL, prevent, 1, IntPtr.Zero, 0, out _, IntPtr.Zero); }
            finally { Marshal.FreeHGlobal(prevent); }
            DeviceIoControl(h, IOCTL_STORAGE_EJECT_MEDIA, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);

            return true; // flushed + dismounted → safe to unplug
        }
        catch (Exception ex) { message = ex.Message; return false; }
    }
}
