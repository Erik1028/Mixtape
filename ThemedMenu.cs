namespace iPodCommander;

/// <summary>A dark context menu matching the Mixtape theme (accent hover, themed separators/checks).</summary>
internal static class ThemedMenu
{
    public static ContextMenuStrip New()
    {
        var m = new ContextMenuStrip
        {
            BackColor = Theme.PanelBg,
            ForeColor = Theme.TextCol,
            Font = Theme.UiFont(9.5f),
            ShowImageMargin = true,
            Renderer = new DarkMenuRenderer(),
        };
        // A fresh menu is created per right-click and used once; dispose it after it closes (deferred
        // to the next message-loop turn so an item-click handler that opens a dialog stays valid).
        m.Closed += (_, _) => { try { if (m.IsHandleCreated) m.BeginInvoke(new Action(m.Dispose)); } catch { } };
        return m;
    }
}

internal sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
{
    public DarkMenuRenderer() : base(new DarkMenuColors()) { RoundedEdges = false; }
    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? Theme.TextCol : Theme.Faint;
        base.OnRenderItemText(e);
    }
}

internal sealed class DarkMenuColors : ProfessionalColorTable
{
    private static Color Sel => Theme.Blend(Theme.PanelBg, Theme.Accent, 0.28);
    public override Color ToolStripDropDownBackground => Theme.PanelBg;
    public override Color MenuBorder => Theme.Border;
    public override Color MenuItemBorder => Color.Transparent;
    public override Color MenuItemSelected => Sel;
    public override Color MenuItemSelectedGradientBegin => Sel;
    public override Color MenuItemSelectedGradientEnd => Sel;
    public override Color MenuItemPressedGradientBegin => Theme.PanelBg;
    public override Color MenuItemPressedGradientEnd => Theme.PanelBg;
    public override Color ImageMarginGradientBegin => Theme.PanelBg;
    public override Color ImageMarginGradientMiddle => Theme.PanelBg;
    public override Color ImageMarginGradientEnd => Theme.PanelBg;
    public override Color SeparatorDark => Theme.Border;
    public override Color SeparatorLight => Theme.Border;
    public override Color CheckBackground => Theme.AccentDim;
    public override Color CheckSelectedBackground => Theme.Accent;
    public override Color CheckPressedBackground => Theme.Accent;
}
