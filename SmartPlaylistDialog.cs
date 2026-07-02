using System.Runtime.InteropServices;

namespace iPodCommander;

/// <summary>
/// Editor for a <see cref="SmartPlaylistDef"/>: a name, a match-all/any toggle, a scrollable list of rule rows
/// (field + operator + value), a sort order and an optional limit, plus a live "≈ N songs match" preview. The
/// field/operator choosers reuse the app's <see cref="ThemedMenu"/> so the whole dialog stays on-theme. On OK,
/// <see cref="Result"/> holds the edited definition (its PersistentId/Name are filled in by the caller).
/// </summary>
internal sealed class SmartPlaylistDialog : GlassDialog
{
    private readonly IReadOnlyList<Track> _audio;
    private readonly TextBox _name;
    private bool _matchAll;
    private readonly ThemedButton _matchBtn;
    private readonly Panel _rulesPanel;
    private readonly List<RuleRow> _rows = new();
    private string _sort;
    private readonly ThemedButton _sortBtn;
    private readonly TextBox _limit;
    private readonly Label _count;

    public SmartPlaylistDef Result { get; private set; } = new();

    public SmartPlaylistDialog(IReadOnlyList<Track> audio, SmartPlaylistDef? initial)
    {
        _audio = audio;
        _matchAll = initial?.MatchAll ?? true;
        _sort = initial?.LimitSort ?? "Added";

        Text = initial is null ? Loc.T("New Smart Playlist") : Loc.T("Edit Smart Playlist");
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
        ClientSize = new Size(560, 540);
        BackColor = Theme.Bg;
        ForeColor = Theme.TextCol;
        Font = Theme.UiFont(9.5f);

        GlassLabel Lbl(string text, int x, int y, int w) => new() { Text = text, ForeColor = Theme.Subtle, AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, Location = new Point(x, y), Size = new Size(w, 26) };

        // Name
        Controls.Add(Lbl(Loc.T("Name"), 16, 18, 64));
        _name = new TextBox { Text = initial?.Name ?? Loc.T("Smart Playlist"), Location = new Point(86, 18), Width = 458, BackColor = Theme.RowBg, ForeColor = Theme.TextCol, BorderStyle = BorderStyle.FixedSingle };
        Controls.Add(_name);

        // Match all / any
        Controls.Add(Lbl(Loc.T("Match"), 16, 56, 64));
        _matchBtn = new ThemedButton { Location = new Point(86, 54), Width = 220, Height = 28 };
        _matchBtn.Click += (_, _) => ShowMenu(_matchBtn, new[]
        {
            (Loc.T("all of the following"), (Action)(() => { _matchAll = true; SyncMatch(); Recompute(); })),
            (Loc.T("any of the following"), (Action)(() => { _matchAll = false; SyncMatch(); Recompute(); })),
        });
        Controls.Add(_matchBtn);
        SyncMatch();

        // Rules (scrollable)
        _rulesPanel = new GlassPanel { Location = new Point(16, 90), Size = new Size(528, 244), AutoScroll = true, BackColor = Theme.Bg };
        Controls.Add(_rulesPanel);

        var addBtn = new ThemedButton { Text = Loc.T("+ Add rule"), Location = new Point(16, 340), Width = 120, Height = 28 };
        addBtn.Click += (_, _) => { AddRow(new SmartRule()); Recompute(); };
        Controls.Add(addBtn);

        // Sort by
        Controls.Add(Lbl(Loc.T("Sort by"), 16, 378, 64));
        _sortBtn = new ThemedButton { Location = new Point(86, 376), Width = 220, Height = 28 };
        _sortBtn.Click += (_, _) => ShowMenu(_sortBtn, SmartPlaylist.Sorts.Select(s => (Loc.T(s.Label), (Action)(() => { _sort = s.Key; SyncSort(); Recompute(); }))));
        Controls.Add(_sortBtn);
        SyncSort();

        // Limit
        Controls.Add(Lbl(Loc.T("Limit to"), 16, 414, 64));
        _limit = new TextBox { Text = (initial?.Limit ?? 0) > 0 ? initial!.Limit.ToString() : "", Location = new Point(86, 414), Width = 56, BackColor = Theme.RowBg, ForeColor = Theme.TextCol, BorderStyle = BorderStyle.FixedSingle };
        _limit.TextChanged += (_, _) => Recompute();
        Controls.Add(_limit);
        Controls.Add(Lbl(Loc.T("songs (0 = no limit)"), 150, 414, 200));

        // Live count
        _count = new GlassLabel { ForeColor = Theme.Accent, AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, Location = new Point(16, 450), Size = new Size(320, 24), Font = Theme.UiFont(9.5f, FontStyle.Bold) };
        Controls.Add(_count);

        // Buttons
        var save = new ThemedButton { Text = Loc.T("Save"), Primary = true, Pill = true, Width = 104, Height = 32, Location = new Point(ClientSize.Width - 120, 486) };
        var cancel = new ThemedButton { Text = Loc.T("Cancel"), Pill = true, Width = 96, Height = 32, Location = new Point(ClientSize.Width - 120 - 104, 486), DialogResult = DialogResult.Cancel };
        save.Click += (_, _) => OnSave();
        Controls.Add(save); Controls.Add(cancel);
        CancelButton = cancel;

        // Seed rows
        if (initial is { Rules.Count: > 0 }) foreach (var r in initial.Rules) AddRow(new SmartRule { Field = r.Field, Op = r.Op, Value = r.Value });
        else AddRow(new SmartRule());
        _name.TextChanged += (_, _) => Recompute();
        Recompute();
    }

    private void SyncMatch() => _matchBtn.Text = (_matchAll ? Loc.T("all of the following") : Loc.T("any of the following")) + "   ▾";
    private void SyncSort() => _sortBtn.Text = Loc.T(SmartPlaylist.SortLabel(_sort)) + "   ▾";

    private void AddRow(SmartRule rule)
    {
        var row = new RuleRow(rule);
        row.Changed += Recompute;
        row.RemoveRequested += () => { _rulesPanel.Controls.Remove(row.Host); _rows.Remove(row); row.Host.Dispose(); Relayout(); Recompute(); };
        _rows.Add(row);
        _rulesPanel.Controls.Add(row.Host);
        Relayout();
    }

    private void Relayout()
    {
        for (int i = 0; i < _rows.Count; i++) _rows[i].Host.Location = new Point(0, i * 36);
    }

    private SmartPlaylistDef BuildDef()
    {
        int.TryParse(_limit.Text.Trim(), out int lim);
        return new SmartPlaylistDef
        {
            Name = _name.Text.Trim(),
            MatchAll = _matchAll,
            LimitSort = _sort,
            Limit = Math.Max(0, lim),
            Rules = _rows.Select(r => r.ToRule()).ToList(),
        };
    }

    private void Recompute()
    {
        int n = SmartPlaylist.Evaluate(BuildDef(), _audio).Count;
        _count.Text = Loc.T("≈ {0} songs match", n);
    }

    private void OnSave()
    {
        if (_name.Text.Trim().Length == 0) { _name.Focus(); System.Media.SystemSounds.Beep.Play(); return; }
        Result = BuildDef();
        DialogResult = DialogResult.OK;
    }

    /// <summary>Pop a themed menu of (label → action) choices anchored under a control.</summary>
    private static void ShowMenu(Control anchor, IEnumerable<(string label, Action pick)> items)
    {
        var m = ThemedMenu.New();
        foreach (var (label, pick) in items)
        {
            var it = new ToolStripMenuItem(label);
            it.Click += (_, _) => pick();
            m.Items.Add(it);
        }
        m.Show(anchor, new Point(0, anchor.Height));
    }

    // ---- one rule row ----
    private sealed class RuleRow
    {
        public readonly Panel Host;
        private string _field, _op;
        private readonly ThemedButton _fieldBtn, _opBtn, _remove;
        private readonly TextBox _value;
        private readonly Label _suffix;

        public event Action? Changed;
        public event Action? RemoveRequested;

        public RuleRow(SmartRule rule)
        {
            _field = rule.Field; _op = rule.Op;
            Host = new Panel { Size = new Size(500, 32), BackColor = Theme.RowBg };

            _fieldBtn = new ThemedButton { Location = new Point(6, 2), Width = 135, Height = 28 };
            _fieldBtn.Click += (_, _) => ShowMenu(_fieldBtn, SmartPlaylist.Fields.Select(f => (Loc.T(f.Label), (Action)(() => SetField(f.Key)))));

            _opBtn = new ThemedButton { Location = new Point(147, 2), Width = 128, Height = 28 };
            _opBtn.Click += (_, _) => ShowMenu(_opBtn, SmartPlaylist.OpsFor(SmartPlaylist.Field(_field).Type).Select(o => (Loc.T(o.Label), (Action)(() => SetOp(o.Key)))));

            _value = new TextBox { Text = rule.Value, Location = new Point(281, 4), Width = 138, BackColor = Theme.PanelBg, ForeColor = Theme.TextCol, BorderStyle = BorderStyle.FixedSingle };
            _value.TextChanged += (_, _) => Changed?.Invoke();

            _suffix = new Label { ForeColor = Theme.Subtle, AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, Location = new Point(423, 2), Size = new Size(40, 28) };

            _remove = new ThemedButton { Text = "✕", Location = new Point(468, 2), Width = 28, Height = 28 };
            _remove.Click += (_, _) => RemoveRequested?.Invoke();

            Host.Controls.AddRange(new Control[] { _fieldBtn, _opBtn, _value, _suffix, _remove });
            SyncField(); SyncOp(); SyncSuffix();
        }

        private void SetField(string key)
        {
            if (_field == key) return;
            var oldType = SmartPlaylist.Field(_field).Type;
            _field = key;
            var newType = SmartPlaylist.Field(_field).Type;
            if (oldType != newType) _op = SmartPlaylist.OpsFor(newType)[0].Key;   // operators differ by type → reset
            SyncField(); SyncOp(); SyncSuffix();
            Changed?.Invoke();
        }

        private void SetOp(string key) { _op = key; SyncOp(); Changed?.Invoke(); }

        private void SyncField() => _fieldBtn.Text = Loc.T(SmartPlaylist.Field(_field).Label) + "  ▾";
        private void SyncOp() => _opBtn.Text = Loc.T(SmartPlaylist.OpLabel(SmartPlaylist.Field(_field).Type, _op)) + "  ▾";
        private void SyncSuffix() => _suffix.Text = SmartPlaylist.Field(_field).Type switch
        {
            SmartPlaylist.FieldType.Days => Loc.T("days"),
            SmartPlaylist.FieldType.Rating => "★",
            _ => "",
        };

        public SmartRule ToRule() => new() { Field = _field, Op = _op, Value = _value.Text.Trim() };
    }

    // DWM dark titlebar + rounded corners (Windows 11), matching the app's other dialogs.
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try { int on = 1; DwmSetWindowAttribute(Handle, 20, ref on, sizeof(int)); } catch { }
        try { int round = 2; DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int)); } catch { }
    }
}
