using System.Drawing.Drawing2D;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace iPodCommander;

/// <summary>
/// A modern dark context menu matching the Mixtape theme: the popup window itself has rounded
/// corners, a softly-elevated surface, a hairline border, accent-tinted rounded hover pills, a clean
/// chevron for submenus, and the app's Segoe UI Variable font. One <see cref="RoundMenuRenderer"/> is
/// shared by the top menu and every submenu; each dropdown styles itself (font/colours/padding) and
/// the renderer rounds the popup region as it paints.
/// </summary>
internal static class ThemedMenu
{
    public static ContextMenuStrip New()
    {
        var m = new RoundContextMenu();
        // A fresh menu is created per right-click and used once; dispose it after it closes (deferred
        // to the next message-loop turn so an item-click handler that opens a dialog stays valid).
        m.Closed += (_, _) => { try { if (m.IsHandleCreated) m.BeginInvoke(new Action(m.Dispose)); } catch { } };
        return m;
    }
}

/// <summary>A <see cref="ContextMenuStrip"/> that styles itself and every submenu (font, colours,
/// padding, renderer) so the whole menu tree gets the rounded modern look — not just the top level.</summary>
internal sealed class RoundContextMenu : ContextMenuStrip
{
    public RoundContextMenu() => MenuStyle.Apply(this);

    // Style each item the moment it's added — BEFORE the menu lays out or shows. Doing this late (e.g.
    // in OnOpening) grows the row heights after layout is computed, leaving the text riding high in each
    // row. By now the caller has already populated this item's submenu, so we can recurse into it too.
    protected override void OnItemAdded(ToolStripItemEventArgs e)
    {
        base.OnItemAdded(e);
        MenuStyle.StyleItem(e.Item);
    }
}

/// <summary>Shared colours, fonts and the styling pass for the themed menu tree.</summary>
internal static class MenuStyle
{
    public const int Radius = 10;          // popup-window corner radius
    public const int PillRadius = 6;       // hover/selection pill radius
    public const int PillInsetX = 4;       // pill inset from the item's left/right edges

    public static readonly RoundMenuRenderer Renderer = new();

    public static Color Surface => Theme.Blend(Theme.PanelBg, Color.White, 0.05);   // a touch elevated off the content
    public static Color BorderCol => Theme.Blend(Surface, Color.White, 0.10);
    public static Color Hover => Theme.Blend(Surface, Theme.Accent, 0.20);
    public static Color SeparatorCol => Theme.Blend(Surface, Color.White, 0.09);

    public static Font Font() => Theme.UiFont(9.75f);

    /// <summary>Apply the surface look + shared renderer to a dropdown (top menu or any submenu).</summary>
    public static void Apply(ToolStripDropDownMenu d)
    {
        d.BackColor = Surface;
        d.ForeColor = Theme.TextCol;
        d.Font = Font();
        d.ShowImageMargin = false;     // no item uses an icon or check → drop the gutter for a tight, clean look
        d.ShowCheckMargin = false;
        d.Renderer = Renderer;     // assigning a renderer implicitly switches RenderMode to Custom
        // Only TOP/BOTTOM breathing room inside the rounded corners — NO left/right window padding, so a
        // submenu sits flush against its parent (horizontal text/pill insets come from the item itself).
        d.Padding = new Padding(0, 6, 0, 6);
        // Close the gap WinForms leaves between a submenu and its parent. Subscribed on every dropdown;
        // the handler no-ops for the top menu (no OwnerItem) and only repositions actual submenus.
        d.Opened -= FlushToParent;
        d.Opened += FlushToParent;
    }

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    private const uint SWP_NOSIZE = 0x1, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10;

    /// <summary>Slide a freshly-opened submenu so its edge meets the parent menu's edge (no gap). WinForms
    /// ignores the managed Location setter on a shown ToolStripDropDown, so move the native window directly.
    /// Handles the screen-edge case where WinForms flips the submenu to the parent's left.</summary>
    private static void FlushToParent(object? sender, EventArgs e)
    {
        if (sender is not ToolStripDropDown d || d.OwnerItem?.Owner is not ToolStripDropDown parent) return;
        if (!d.IsHandleCreated || !parent.IsHandleCreated) return;
        if (!GetWindowRect(parent.Handle, out var pr) || !GetWindowRect(d.Handle, out var dr)) return;
        int w = dr.Right - dr.Left;
        bool rightSide = (dr.Left + dr.Right) / 2 >= (pr.Left + pr.Right) / 2;
        int targetX = rightSide ? pr.Right - 1 : pr.Left - w + 1; // 1px overlap hides the seam
        if (dr.Left != targetX) SetWindowPos(d.Handle, IntPtr.Zero, targetX, dr.Top, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    /// <summary>Give an item a taller row + left text inset, and recursively style its submenu (if any).</summary>
    public static void StyleItem(ToolStripItem it)
    {
        if (it is not ToolStripMenuItem mi) return;
        mi.Padding = new Padding(10, 5, 12, 5);   // taller rows + comfortable left text inset
        if (mi.HasDropDownItems && mi.DropDown is ToolStripDropDownMenu sub)
        {
            Apply(sub);
            foreach (ToolStripItem child in mi.DropDownItems) StyleItem(child);
        }
    }
}

/// <summary>Owner-draws the menu: rounded popup window (region + fill + hairline border), accent
/// rounded hover pills, inset separators, and a crisp submenu chevron.</summary>
internal sealed class RoundMenuRenderer : ToolStripProfessionalRenderer
{
    // Remembers the last region size per dropdown so we don't re-assign the region every paint
    // (which would invalidate → repaint → loop). Weak keys: entries vanish when the menu is disposed.
    private static readonly ConditionalWeakTable<ToolStrip, object> _sized = new();

    public RoundMenuRenderer() : base(new MenuColors()) { RoundedEdges = false; }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        var g = e.Graphics;
        var ts = e.ToolStrip;

        if (ts is ToolStripDropDown dd)
        {
            // Round the popup window itself. Guarded so the region is set only when the size changes.
            if (!_sized.TryGetValue(dd, out var prev) || prev is not Size s || s != dd.Size)
            {
                using var rp = Theme.RoundedRect(new RectangleF(0, 0, dd.Width, dd.Height), Radius());
                dd.Region = new Region(rp);
                _sized.AddOrUpdate(dd, dd.Size);
            }
        }

        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Theme.RoundedRect(new RectangleF(0, 0, ts.Width, ts.Height), Radius());
        using var b = new SolidBrush(MenuStyle.Surface);
        g.FillPath(b, path);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Theme.RoundedRect(new RectangleF(0.5f, 0.5f, e.ToolStrip.Width - 1.5f, e.ToolStrip.Height - 1.5f), Radius() - 0.5f);
        using var pen = new Pen(MenuStyle.BorderCol, 1f);
        g.DrawPath(pen, path);
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected || !e.Item.Enabled) return;   // disabled items never light up
        var g = e.Graphics;                                 // graphics origin is the item's top-left
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var rc = new RectangleF(MenuStyle.PillInsetX, 1, e.Item.Width - MenuStyle.PillInsetX * 2, e.Item.Height - 2);
        using var path = Theme.RoundedRect(rc, MenuStyle.PillRadius);
        using var b = new SolidBrush(MenuStyle.Hover);
        g.FillPath(b, path);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? Theme.TextCol : Theme.Faint;
        // Re-center the text across the full row height (keeping the laid-out left inset + width) so it
        // can never ride high in a tall row, whatever the layout timing. e.TextRectangle is item-relative.
        var tr = e.TextRectangle;
        e.TextRectangle = new Rectangle(tr.X, 0, tr.Width, e.Item.Height);
        e.TextFormat = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        if (e.Vertical) { base.OnRenderSeparator(e); return; }
        var g = e.Graphics;                                 // graphics origin is the item's top-left
        g.SmoothingMode = SmoothingMode.None;
        int y = e.Item.Height / 2;
        using var pen = new Pen(MenuStyle.SeparatorCol);
        g.DrawLine(pen, 12, y, e.Item.Width - 12, y);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = e.ArrowRectangle;
        float cx = r.Left + r.Width * 0.40f;
        float cy = r.Top + r.Height / 2f;
        float h = Math.Min(r.Height, 11) * 0.42f;           // half chevron height
        float w = h * 0.78f;
        var col = e.Item?.Selected == true ? Theme.TextCol : Theme.Subtle;
        using var pen = new Pen(col, 1.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        g.DrawLines(pen, new[] { new PointF(cx - w / 2, cy - h), new PointF(cx + w / 2, cy), new PointF(cx - w / 2, cy + h) });
    }

    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { /* no gutter */ }

    private static float Radius() => MenuStyle.Radius;
}

/// <summary>Dark colour table — a fallback for any rendering not fully owner-drawn above.</summary>
internal sealed class MenuColors : ProfessionalColorTable
{
    public override Color ToolStripDropDownBackground => MenuStyle.Surface;
    public override Color MenuBorder => MenuStyle.BorderCol;
    public override Color MenuItemBorder => Color.Transparent;
    public override Color MenuItemSelected => MenuStyle.Hover;
    public override Color MenuItemSelectedGradientBegin => MenuStyle.Hover;
    public override Color MenuItemSelectedGradientEnd => MenuStyle.Hover;
    public override Color MenuItemPressedGradientBegin => MenuStyle.Surface;
    public override Color MenuItemPressedGradientEnd => MenuStyle.Surface;
    public override Color ImageMarginGradientBegin => MenuStyle.Surface;
    public override Color ImageMarginGradientMiddle => MenuStyle.Surface;
    public override Color ImageMarginGradientEnd => MenuStyle.Surface;
    public override Color SeparatorDark => MenuStyle.SeparatorCol;
    public override Color SeparatorLight => MenuStyle.SeparatorCol;
}
