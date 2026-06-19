using System.Drawing;
using System.Windows.Forms;

namespace iPodCommander;

/// <summary>
/// Shows a <see cref="DoctorReport"/> and lets the user opt into the safe fixes (remove dead entries,
/// delete stray files, drop duplicate copies). Purely presentational: it computes a <see cref="DoctorPlan"/>
/// on "Apply" and returns DialogResult.OK; the host applies it through the normal write+save path.
/// Tag/album-completeness problems are shown read-only (they're for the future tag editor).
/// </summary>
internal sealed class LibraryDoctorDialog : Form
{
    private readonly DoctorReport _r;
    public DoctorPlan? Plan { get; private set; }

    private ToggleSwitch? _tMissing, _tOrphan, _tDupes;
    private ThemedButton _apply = null!;

    private const int W = 620, Pad = 24;
    private const int CardW = W - Pad * 2;

    public LibraryDoctorDialog(DoctorReport report)
    {
        _r = report;
        Text = "Library Doctor";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
        BackColor = Theme.Bg;
        ForeColor = Theme.TextCol;
        Font = Theme.UiFont(9.5f);
        ClientSize = new Size(W, 240);
        Build();
        if (Anim.MotionEnabled) Opacity = 0;   // fade up in OnShown (matches Settings)
    }

    private void Build()
    {
        int y = 18;

        Controls.Add(new Label
        {
            Text = "Library Doctor", Font = Theme.DisplayFont(17f, FontStyle.Bold), ForeColor = Theme.TextCol,
            AutoSize = false, Left = Pad + 2, Top = y, Width = CardW, Height = 30, TextAlign = ContentAlignment.MiddleLeft,
        });
        y += 34;

        string summary = _r.Clean
            ? $"Scanned {_r.TotalTracks} songs — everything looks healthy."
            : $"Scanned {_r.TotalTracks} songs. Tick what you'd like Mixtape to tidy up:";
        Controls.Add(new Label
        {
            Text = summary, Font = Theme.UiFont(9.5f), ForeColor = Theme.Subtle,
            AutoSize = false, Left = Pad + 2, Top = y, Width = CardW, Height = 20, TextAlign = ContentAlignment.MiddleLeft,
        });
        y += 30;

        if (_r.Clean)
        {
            var card = new CardPanel(CardW) { Left = Pad, Top = y };
            const string cleanDesc = "Your library and the files on the iPod are consistent — no missing files, no duplicates, no stray files.";
            card.AddRow("✓  No problems found", cleanDesc, null, RowHFor(cleanDesc, null));
            card.Finish(); Controls.Add(card); y += card.Height + 12;
        }
        else
        {
            // ---- fixable problems (opt-in toggles) ----
            var fixes = new CardPanel(CardW) { Left = Pad, Top = y };
            bool any = false;
            if (_r.MissingFiles.Count > 0)
            {
                _tMissing = MakeToggle(true);
                string d = $"{_r.MissingFiles.Count} song(s) point to a file that's no longer on the iPod. Remove these dead entries.";
                fixes.AddRow("Missing files", d, _tMissing, RowHFor(d, _tMissing));
                any = true;
            }
            if (_r.DuplicateExtras > 0)
            {
                _tDupes = MakeToggle(false);
                string d = $"{_r.DuplicateExtras} duplicate copy(ies) across {_r.DuplicateGroups.Count} song(s). Delete the extras, keeping the best copy of each.";
                fixes.AddRow("Duplicates", d, _tDupes, RowHFor(d, _tDupes));
                any = true;
            }
            if (_r.OrphanFiles.Count > 0)
            {
                _tOrphan = MakeToggle(false);
                string d = $"{_r.OrphanFiles.Count} file(s) on the iPod ({HumanSize(_r.OrphanBytes)}) aren't in your library. Delete them to reclaim space.";
                fixes.AddRow("Stray files", d, _tOrphan, RowHFor(d, _tOrphan));
                any = true;
            }
            if (any) { fixes.Finish(); Controls.Add(fixes); y += fixes.Height + 12; }

            // ---- report-only problems (need the tag editor; shown for awareness) ----
            if (_r.IncompleteTags > 0 || _r.AlbumGaps > 0)
            {
                var info = new CardPanel(CardW) { Left = Pad, Top = y };
                if (_r.IncompleteTags > 0) info.AddInfoRow("Incomplete tags", $"{_r.IncompleteTags} song(s) missing title/artist/album");
                if (_r.AlbumGaps > 0) info.AddInfoRow("Possibly incomplete albums", $"{_r.AlbumGaps} album(s) with a track-number gap");
                info.Finish(); Controls.Add(info); y += info.Height + 6;

                Controls.Add(new Label
                {
                    Text = "These are shown for awareness — fix them with the upcoming tag editor.",
                    Font = Theme.UiFont(8.5f), ForeColor = Theme.Faint,
                    AutoSize = false, Left = Pad + 2, Top = y, Width = CardW, Height = 18, TextAlign = ContentAlignment.MiddleLeft,
                });
                y += 26;
            }
        }

        // ---- buttons ----
        y += 6;
        var close = new ThemedButton { Text = _r.Clean ? "Close" : "Cancel", Pill = true, Width = 100, Height = 32 };
        close.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        close.Left = W - Pad - close.Width; close.Top = y;
        Controls.Add(close);

        if (!_r.Clean)
        {
            _apply = new ThemedButton { Text = "Apply fixes", Pill = true, Primary = true, Width = 130, Height = 32, Enabled = false };
            _apply.Click += OnApply;
            _apply.Left = close.Left - 10 - _apply.Width; _apply.Top = y;
            Controls.Add(_apply);
        }
        y += close.Height;

        ClientSize = new Size(W, y + Pad);
        UpdateApply();   // reflect the default-on toggles (CheckedChanged isn't raised for the initial value)
    }

    // Row height sized to the ACTUAL wrapped description (mirrors CardPanel.AddRow geometry). A fixed height
    // left the toggle centred over empty space below one-line text (it sagged) and clipped two-line text.
    private static int RowHFor(string desc, Control? ctrl)
    {
        const int labelLeft = 18, gap = 16, rightPad = 18;
        int ctrlLeft = ctrl is not null ? CardW - rightPad - ctrl.Width : CardW - rightPad;
        int labelW = Math.Max(80, ctrlLeft - gap - labelLeft);
        using var f = Theme.UiFont(9f);
        int descH = TextRenderer.MeasureText(desc, f, new Size(labelW, 0), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height;
        return Math.Max(58, 28 + descH + 16);
    }

    private ToggleSwitch MakeToggle(bool initial)
    {
        var t = new ToggleSwitch { Checked = initial };
        t.CheckedChanged += UpdateApply;
        return t;
    }

    private void UpdateApply()
    {
        if (_apply is null) return;
        _apply.Enabled = (_tMissing?.Checked ?? false) || (_tDupes?.Checked ?? false) || (_tOrphan?.Checked ?? false);
    }

    private void OnApply(object? sender, EventArgs e)
    {
        var plan = new DoctorPlan();
        if (_tMissing?.Checked == true)
            plan.RemoveRows.AddRange(_r.MissingFiles.Select(t => t.UniqueId));
        if (_tDupes?.Checked == true)
            foreach (var grp in _r.DuplicateGroups)
                plan.DeleteDupTracks.AddRange(grp.Skip(1).Select(t => t.UniqueId));   // keep grp[0] (the best)
        if (_tOrphan?.Checked == true)
            plan.DeleteFiles.AddRange(_r.OrphanFiles.Select(o => o.Path));
        Plan = plan;
        DialogResult = DialogResult.OK;
        Close();
    }

    private static string HumanSize(long bytes)
    {
        if (bytes >= 1L << 30) return $"{bytes / (double)(1L << 30):0.0} GB";
        if (bytes >= 1L << 20) return $"{bytes / (double)(1L << 20):0.0} MB";
        if (bytes >= 1L << 10) return $"{bytes / (double)(1L << 10):0.0} KB";
        return $"{bytes} B";
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (!Anim.MotionEnabled) { Opacity = 1; return; }
        int home = Top;
        Top = home + 16;
        Anim.Run(190, v =>
        {
            if (IsDisposed) return;
            Opacity = v;
            Top = home + (int)Math.Round(16 * (1 - v));
        }, () => { if (!IsDisposed) { Opacity = 1; Top = home; } }, Easings.OutCubic);
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try { int on = 1; DwmSetWindowAttribute(Handle, 20, ref on, sizeof(int)); } catch { }
        try { int caption = 0x001A1716; DwmSetWindowAttribute(Handle, 35, ref caption, sizeof(int)); } catch { }
    }
}
