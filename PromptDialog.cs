namespace iPodCommander;

/// <summary>A small dark themed text-input dialog (used for renaming playlists). Returns null on cancel.</summary>
internal static class PromptDialog
{
    /// <summary>The window itself, built but not shown — the render harness needs it without a modal loop.</summary>
    internal static CardDialog Build(string title, string prompt, string initial, out TextBox box)
    {
        var f = new CardDialog
        {
            Text = title,
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(380, 142),
            BackColor = Theme.Bg,
            ForeColor = Theme.TextCol,
            Font = Theme.UiFont(9.5f),
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
        };

        var lbl = new GlassLabel { Text = prompt, AutoSize = true, ForeColor = Theme.Subtle, Location = new Point(22, 18) };
        var tb = new TextBox
        {
            Text = initial,
            Location = new Point(22, 44),
            Width = 336,
            Font = Theme.UiFont(10.5f),
            BackColor = Theme.RowBg,
            ForeColor = Theme.TextCol,
            BorderStyle = BorderStyle.FixedSingle,
        };
        var ok = new ThemedButton { Text = Loc.T("OK"), Primary = true, Pill = true, Width = 96, Height = 32, Location = new Point(262, 94), DialogResult = DialogResult.OK };
        var cancel = new ThemedButton { Text = Loc.T("Cancel"), Pill = true, Width = 96, Height = 32, Location = new Point(156, 94), DialogResult = DialogResult.Cancel };

        f.Controls.Add(lbl);
        f.Controls.Add(ThemedField.Wrap(tb));
        f.Controls.Add(ok);
        f.Controls.Add(cancel);
        f.AcceptButton = ok;
        f.CancelButton = cancel;
        f.Shown += (_, _) => { tb.Focus(); tb.SelectAll(); };
        f.AdoptCard();   // the app's own title strip in place of the Windows caption
        box = tb;
        return f;
    }

    public static string? Show(IWin32Window owner, string title, string prompt, string initial)
    {
        using var f = Build(title, prompt, initial, out var tb);
        return f.ShowDialog(owner) == DialogResult.OK ? tb.Text : null;
    }
}
