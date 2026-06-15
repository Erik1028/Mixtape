namespace iPodCommander;

/// <summary>A small dark themed text-input dialog (used for renaming playlists). Returns null on cancel.</summary>
internal static class PromptDialog
{
    public static string? Show(IWin32Window owner, string title, string prompt, string initial)
    {
        using var f = new Form
        {
            Text = title,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(380, 138),
            BackColor = Theme.Bg,
            ForeColor = Theme.TextCol,
            Font = Theme.UiFont(9.5f),
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
        };

        var lbl = new Label { Text = prompt, AutoSize = true, ForeColor = Theme.Subtle, Location = new Point(16, 16) };
        var tb = new TextBox
        {
            Text = initial,
            Location = new Point(16, 42),
            Width = 348,
            BackColor = Theme.RowBg,
            ForeColor = Theme.TextCol,
            BorderStyle = BorderStyle.FixedSingle,
        };
        var ok = new ThemedButton { Text = "OK", Primary = true, Pill = true, Width = 96, Height = 32, Location = new Point(268, 88), DialogResult = DialogResult.OK };
        var cancel = new ThemedButton { Text = "Cancel", Pill = true, Width = 96, Height = 32, Location = new Point(162, 88), DialogResult = DialogResult.Cancel };

        f.Controls.Add(lbl);
        f.Controls.Add(tb);
        f.Controls.Add(ok);
        f.Controls.Add(cancel);
        f.AcceptButton = ok;
        f.CancelButton = cancel;
        f.Shown += (_, _) => { tb.Focus(); tb.SelectAll(); };

        return f.ShowDialog(owner) == DialogResult.OK ? tb.Text : null;
    }
}
