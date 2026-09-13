namespace iPodCommander;

/// <summary>Where the memory goes: the managed heap, the process, and the bitmap caches (GDI+ pixel memory is
/// native — the managed heap never shows it). Written by the render harness with MIX_MEMLOG=1.</summary>
internal static class MemProbe
{
    public static string Report(CoverFlowView? cf)
    {
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var p = System.Diagnostics.Process.GetCurrentProcess(); p.Refresh();
        var (artN, artB) = ArtworkService.CacheStats();
        var (tileN, tileB) = Theme.ArtCacheStats();
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"managed heap {GC.GetTotalMemory(false) / 1048576.0:F1} MB | private {p.PrivateMemorySize64 / 1048576.0:F0} MB | working set {p.WorkingSet64 / 1048576.0:F0} MB | GDI handles {GdiCount(p.Handle)}");
        sb.AppendLine($"ArtworkService: {artN} bitmaps, {artB / 1048576.0:F1} MB | Theme.ArtCache: {tileN} tiles, {tileB / 1048576.0:F1} MB");
        if (cf is not null)
        {
            var (sN, sB, pN, pB) = cf.CacheStats();
            sb.AppendLine($"CoverFlow: {sN} sprites {sB / 1048576.0:F1} MB + {pN} cover pixel arrays {pB / 1048576.0:F1} MB");
        }
        return sb.ToString();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetGuiResources(IntPtr hProcess, uint uiFlags);
    private static uint GdiCount(IntPtr h) { try { return GetGuiResources(h, 0); } catch { return 0; } }
}
