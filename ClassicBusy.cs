using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace iPodCommander;

/// <summary>
/// The Windows 95 look's busy pointer: 1995's hourglass instead of Windows 11's spinning ring. WinForms' wait cursor is
/// the system one and cannot be swapped, so while the app is busy a hook sees every WM_SETCURSOR the main window's
/// controls answer and puts the hourglass up after them - the last SetCursor is the one shown. The hook lives only
/// while the app is busy; the rest of the time nothing runs.
/// </summary>
internal static class ClassicBusy
{
    private const int WH_CALLWNDPROCRET = 12, WM_SETCURSOR = 0x0020, GA_ROOT = 2;
    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
    private static HookProc? _proc;
    private static IntPtr _hook, _root;
    private static Cursor? _hourglass;

    /// <summary>The pixel hourglass (a real cursor, its hotspot in the middle of the glass).</summary>
    public static Cursor Hourglass => _hourglass ??= MakeHourglass();

    /// <summary>Busy or not, for the window <paramref name="root"/> and everything in it.</summary>
    public static void Set(bool busy, IntPtr root)
    {
        if (busy)
        {
            _root = root;
            if (_hook == IntPtr.Zero)
            {
                _proc ??= Hook;
                _hook = SetWindowsHookEx(WH_CALLWNDPROCRET, _proc, IntPtr.Zero, GetCurrentThreadId());
            }
            Cursor.Current = Hourglass;   // at once, not on the next move of the mouse
        }
        else if (_hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
            Cursor.Current = Cursors.Default;   // the control under the pointer sets its own again on the next move
        }
    }

    private static IntPtr Hook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            // CWPRETSTRUCT { LRESULT lResult; LPARAM lParam; WPARAM wParam; UINT message; HWND hwnd; }
            int msg = Marshal.ReadInt32(lParam, IntPtr.Size * 3);
            if (msg == WM_SETCURSOR)
            {
                IntPtr hwnd = Marshal.ReadIntPtr(lParam, IntPtr.Size == 8 ? 32 : 16);
                if (GetAncestor(hwnd, GA_ROOT) == _root) SetCursor(Hourglass.Handle);
            }
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    // The Windows 95 hourglass: black frame, white glass, the sand running from the top bulb into a pile.
    private static readonly string[] Glass =
    {
        "KKKKKKKKKKKKK",
        "KWWWWWWWWWWWK",
        "KKKKKKKKKKKKK",
        ".KWWWWWWWWWK.",
        ".KWKKKKKKKWK.",
        ".KWWKKKKKWWK.",
        "..KWWKKKWWK..",
        "...KWWKWWK...",
        "....KWKWK....",
        ".....KWK.....",
        "....KWWWK....",
        "...KWWKWWK...",
        "..KWWWKWWWK..",
        ".KWWWWKWWWWK.",
        ".KWWWKKKWWWK.",
        ".KWWKKKKKWWK.",
        ".KWKKKKKKKWK.",
        "KKKKKKKKKKKKK",
        "KWWWWWWWWWWWK",
        "KKKKKKKKKKKKK",
    };

    private static Cursor MakeHourglass()
    {
        const int S = 32;
        using var bmp = new Bitmap(S, S, PixelFormat.Format32bppArgb);
        int ox = (S - Glass[0].Length) / 2, oy = (S - Glass.Length) / 2;
        for (int y = 0; y < Glass.Length; y++)
            for (int x = 0; x < Glass[y].Length; x++)
            {
                char c = Glass[y][x];
                if (c == 'K') bmp.SetPixel(ox + x, oy + y, Color.Black);
                else if (c == 'W') bmp.SetPixel(ox + x, oy + y, Color.White);
            }
        IntPtr hIcon = bmp.GetHicon();
        try
        {
            if (!GetIconInfo(hIcon, out var ii)) return Cursors.WaitCursor;
            ii.fIcon = false;
            ii.xHotspot = ox + Glass[0].Length / 2;
            ii.yHotspot = oy + Glass.Length / 2;
            IntPtr cur = CreateIconIndirect(ref ii);
            DeleteObject(ii.hbmColor); DeleteObject(ii.hbmMask);
            return cur != IntPtr.Zero ? new Cursor(cur) : Cursors.WaitCursor;
        }
        catch { return Cursors.WaitCursor; }
        finally { DestroyIcon(hIcon); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO { [MarshalAs(UnmanagedType.Bool)] public bool fIcon; public int xHotspot, yHotspot; public IntPtr hbmMask, hbmColor; }

    [DllImport("user32.dll")] private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, int flags);
    [DllImport("user32.dll")] private static extern IntPtr SetCursor(IntPtr hCursor);
    [DllImport("user32.dll")] private static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO info);
    [DllImport("user32.dll")] private static extern IntPtr CreateIconIndirect(ref ICONINFO info);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr hIcon);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
}
