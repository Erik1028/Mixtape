using System.Drawing.Drawing2D;
using System.Text;

namespace iPodCommander;

/// <summary>
/// The text notes a click-wheel iPod shows under Extras > Notes. They are plain files in a Notes folder at the
/// drive's root, nothing to do with the music database - which is why this works even on an iPod Mixtape refuses
/// to write music to. Lists what is there, adds a file or a typed note, removes one.
/// </summary>
internal sealed class NotesDialog : CardDialog
{
    private const int MaxNote = 4096;        // what the iPod itself will display of a note
    private readonly string _dir;
    private readonly NoteList _list = new();
    private readonly GlassLabel _hint = new();

    public NotesDialog(string mountRoot)
    {
        _dir = Path.Combine(mountRoot, "Notes");
        Text = Loc.T("Notes on the iPod");
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(460, 330);
        BackColor = Theme.Bg;
        ForeColor = Theme.TextCol;
        Font = Theme.UiFont(9.5f);

        _list.SetBounds(20, 16, ClientSize.Width - 40, 218);
        _list.Removed += LoadNotes;
        Controls.Add(_list);

        _hint.Text = Loc.T("They appear on the iPod under Extras → Notes. It shows about the first 4 KB of each one.");
        _hint.ForeColor = Theme.Faint;
        _hint.AutoSize = false;
        _hint.SetBounds(22, 240, ClientSize.Width - 44, 32);
        Controls.Add(_hint);

        var add = new ThemedButton { Text = Loc.T("Add file…"), Pill = true, Width = 110, Height = 32, Location = new Point(20, 280) };
        add.Click += (_, _) => AddFile();
        var make = new ThemedButton { Text = Loc.T("New note…"), Pill = true, Width = 120, Height = 32, Location = new Point(138, 280) };
        make.Click += (_, _) => NewNote();
        var done = new ThemedButton { Text = Loc.T("Done"), Primary = true, Pill = true, Width = 96, Height = 32, Location = new Point(ClientSize.Width - 116, 280), DialogResult = DialogResult.OK };
        Controls.Add(add); Controls.Add(make); Controls.Add(done);
        AcceptButton = done;

        LoadNotes();
        AdoptCard();
    }

    private void LoadNotes()
    {
        var items = new List<(string Path, string Name, long Size)>();
        try
        {
            if (Directory.Exists(_dir))
                foreach (var f in Directory.EnumerateFiles(_dir, "*.txt").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                    items.Add((f, Path.GetFileNameWithoutExtension(f), new FileInfo(f).Length));
        }
        catch { /* an unplugged iPod just shows an empty list */ }
        _list.SetItems(items, Loc.T("No notes on this iPod yet."));
    }

    private void AddFile()
    {
        using var dlg = new OpenFileDialog { Filter = Loc.T("Text files") + " (*.txt)|*.txt", Multiselect = true, Title = Loc.T("Add file…") };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        foreach (string src in dlg.FileNames)
        {
            try
            {
                string text = File.ReadAllText(src);
                Write(Path.GetFileNameWithoutExtension(src), text);
            }
            catch (Exception ex) { Fail(ex); return; }
        }
        LoadNotes();
    }

    private void NewNote()
    {
        using var f = new NoteEditor();
        if (f.ShowDialog(this) != DialogResult.OK) return;
        try { Write(f.NoteName, f.NoteText); } catch (Exception ex) { Fail(ex); }
        LoadNotes();
    }

    /// <summary>Writes one note, as the iPod wants it: a .txt file in the Notes folder, plain text, safe name.</summary>
    private void Write(string name, string text)
    {
        Directory.CreateDirectory(_dir);
        var clean = new StringBuilder();
        foreach (char c in name.Trim())
            if (!Path.GetInvalidFileNameChars().Contains(c)) clean.Append(c);
        string file = clean.Length == 0 ? Loc.T("Note") : clean.ToString();
        if (file.Length > 40) file = file[..40];
        File.WriteAllText(Path.Combine(_dir, file + ".txt"), text.ReplaceLineEndings("\r\n"), Encoding.UTF8);
    }

    private void Fail(Exception ex) =>
        MessageDialog.Show(this, Loc.T("Couldn't write the note:\n\n{0}", ex.Message), "Mixtape", MessageBoxButtons.OK, MessageBoxIcon.Error);

    /// <summary>Render harness: the note editor on its own.</summary>
    internal static Form PreviewEditor() => new NoteEditor();

    /// <summary>The notes already on the device: a name, its size, and a × that removes it.</summary>
    private sealed class NoteList : Control
    {
        public event Action? Removed;
        private readonly List<(string Path, string Name, long Size)> _items = new();
        private string _empty = "";
        private int _hover = -1, _scroll;
        private const int RowH = 34;
        private readonly Font _fName = Theme.UiFont(Theme.SzTitle, FontStyle.Bold), _fSize = Theme.UiFont(Theme.SzCaption);

        public NoteList()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.PanelBg;
            MouseMove += (_, e) => { int h = RowAt(e.Y); if (h != _hover) { _hover = h; Invalidate(); } };
            MouseLeave += (_, _) => { if (_hover >= 0) { _hover = -1; Invalidate(); } };
            MouseWheel += (_, e) => { int max = Math.Max(0, _items.Count * RowH - Height + 12); int v = Math.Clamp(_scroll - Math.Sign(e.Delta) * RowH, 0, max); if (v != _scroll) { _scroll = v; Invalidate(); } };
            MouseClick += (_, e) =>
            {
                int i = RowAt(e.Y);
                if (i < 0 || e.X < Width - 44) return;
                var it = _items[i];
                if (MessageDialog.Show(this, Loc.T("Remove the note “{0}” from the iPod?", it.Name), "Mixtape", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
                try { File.Delete(it.Path); } catch { }
                Removed?.Invoke();
            };
        }

        public void SetItems(List<(string Path, string Name, long Size)> items, string empty)
        {
            _items.Clear(); _items.AddRange(items);
            _empty = empty; _scroll = 0; _hover = -1;
            Invalidate();
        }

        private int RowAt(int y)
        {
            int i = (y - 6 + _scroll) / RowH;
            return i >= 0 && i < _items.Count ? i : -1;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent?.BackColor ?? Theme.Bg);
            using (var b = new SolidBrush(Theme.PanelBg))
            using (var p = Theme.RoundedRect(new Rectangle(0, 0, Width, Height), Theme.RadCard)) g.FillPath(b, p);

            if (_items.Count == 0)
            {
                TextRenderer.DrawText(g, _empty, _fSize, new Rectangle(16, 0, Width - 32, Height), Theme.Faint,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                return;
            }

            var clip = g.Clip;
            g.SetClip(new Rectangle(0, 4, Width, Height - 8));
            for (int i = 0; i < _items.Count; i++)
            {
                int y = 6 + i * RowH - _scroll;
                if (y + RowH < 0 || y > Height) continue;
                var row = new Rectangle(6, y, Width - 12, RowH - 2);
                if (i == _hover)
                {
                    using var hb = new SolidBrush(Theme.RowHover);
                    using var hp = Theme.RoundedRect(row, Theme.RadControl);
                    g.FillPath(hb, hp);
                }
                TextRenderer.DrawText(g, _items[i].Name, _fName, new Rectangle(row.X + 12, row.Y, row.Width - 120, row.Height), Theme.TextCol,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
                TextRenderer.DrawText(g, SizeStr(_items[i].Size), _fSize, new Rectangle(row.Right - 96, row.Y, 50, row.Height), Theme.Faint,
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                if (i == _hover)   // the × only on the row under the pointer, so the list stays calm
                {
                    float cx = row.Right - 22, cy = row.Y + row.Height / 2f, k = 4f;
                    using var pen = new Pen(Theme.Subtle, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                    g.DrawLine(pen, cx - k, cy - k, cx + k, cy + k);
                    g.DrawLine(pen, cx + k, cy - k, cx - k, cy + k);
                }
            }
            g.Clip = clip;
        }

        private static string SizeStr(long n) => n >= 1024 ? $"{n / 1024.0:0.#} KB" : $"{n} B";

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _fName.Dispose(); _fSize.Dispose(); }
            base.Dispose(disposing);
        }
    }

    /// <summary>Type a note here instead of making a text file first.</summary>
    private sealed class NoteEditor : CardDialog
    {
        private readonly TextBox _name = new() { Text = Loc.T("Note"), BackColor = Theme.RowBg, ForeColor = Theme.TextCol, BorderStyle = BorderStyle.FixedSingle };
        private readonly TextBox _body = new()
        {
            // No native scrollbar: a note is 4 KB at most, the wheel and the caret still scroll it, and the grey
            // Windows bar was the one foreign thing in the dialog.
            Multiline = true, ScrollBars = ScrollBars.None, BorderStyle = BorderStyle.None,
            BackColor = Theme.RowBg, ForeColor = Theme.TextCol, Font = Theme.UiFont(10f), MaxLength = MaxNote,
        };

        public string NoteName => _name.Text;
        public string NoteText => _body.Text;

        public NoteEditor()
        {
            Text = Loc.T("New note…");
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(440, 320);
            BackColor = Theme.Bg;
            ForeColor = Theme.TextCol;
            Font = Theme.UiFont(9.5f);

            Controls.Add(new GlassLabel { Text = Loc.T("Name"), ForeColor = Theme.Subtle, AutoSize = false, Location = new Point(22, 20), Size = new Size(60, 24), TextAlign = ContentAlignment.MiddleLeft });
            _name.Location = new Point(86, 16);
            _name.Width = ClientSize.Width - 108;
            Controls.Add(ThemedField.Wrap(_name));

            var frame = new RoundPanel { BackColor = Theme.RowBg, Location = new Point(22, 58), Size = new Size(ClientSize.Width - 44, 190) };
            _body.SetBounds(8, 8, frame.Width - 16, frame.Height - 16);
            frame.Controls.Add(_body);
            Controls.Add(frame);

            var ok = new ThemedButton { Text = Loc.T("Save"), Primary = true, Pill = true, Width = 100, Height = 32, Location = new Point(ClientSize.Width - 120, 262), DialogResult = DialogResult.OK };
            var cancel = new ThemedButton { Text = Loc.T("Cancel"), Pill = true, Width = 96, Height = 32, Location = new Point(ClientSize.Width - 120 - 106, 262), DialogResult = DialogResult.Cancel };
            Controls.Add(ok); Controls.Add(cancel);
            AcceptButton = null;          // Enter belongs to the text, not to Save
            CancelButton = cancel;
            AdoptCard();
        }
    }
}
