using System.Drawing.Drawing2D;

namespace iPodCommander;

/// <summary>
/// A playlist as a picture worth sending to someone: the covers, the name, the songs, on the app's own dark
/// card, tinted by the first cover the way the deck tints its card. Pure drawing - it reads a few bitmaps and
/// returns one, touching neither the library nor the device.
/// </summary>
internal static class Poster
{
    public sealed class Song { public string Title = "", Artist = ""; }

    public const int W = 1080;
    private const int Pad = 64, Art = 420, RowH = 46, MaxRows = 40;

    /// <summary>Renders the poster. The caller owns the bitmap and the covers it passed in.</summary>
    public static Bitmap Render(string title, string subtitle, IReadOnlyList<Song> songs, IReadOnlyList<Bitmap> covers, string footer)
    {
        int shown = Math.Min(songs.Count, MaxRows);
        bool twoCol = shown > 16;
        int rows = twoCol ? (shown + 1) / 2 : shown;
        int listTop = Pad + Art + 54;
        int more = songs.Count - shown;
        int h = listTop + rows * RowH + (more > 0 ? 44 : 10) + 96;

        var bmp = new Bitmap(W, h);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        // ---- the ground: the app's background, lifted toward the first cover's colour at the top ----
        Color tint = NowPlayingBar.AccentTintFor(covers.Count > 0 ? covers[0] : null);
        using (var bg = new LinearGradientBrush(new Rectangle(0, 0, W, h),
                   Theme.Blend(Theme.Bg, tint, 0.20), Theme.Blend(Theme.Bg, Color.Black, 0.35), LinearGradientMode.Vertical))
            g.FillRectangle(bg, 0, 0, W, h);
        using (var halo = new GraphicsPath())   // a soft light behind the art: radial, so it has no edge of its own
        {
            halo.AddEllipse(-320, -520, 1240, 1060);
            using var glow = new PathGradientBrush(halo)
            {
                CenterPoint = new PointF(300, 180),
                CenterColor = Color.FromArgb(46, tint),
                SurroundColors = new[] { Color.FromArgb(0, tint) },
            };
            g.FillPath(glow, halo);
        }

        // ---- the art: a 2x2 of covers, or one big cover, or the cassette ----
        DrawArt(g, new Rectangle(Pad, Pad, Art, Art), covers, title);

        // ---- the name, the rule and the count, as ONE block centred against the art ----
        int tx = Pad + Art + 46, tw = W - tx - Pad;
        using (var fTitle = Theme.DisplayFont(Fits(title, tw) ? 50f : 38f, FontStyle.Bold))
        using (var fSub = Theme.UiFont(17f))
        {
            int titleH = Math.Min(150, TextRenderer.MeasureText(title, fTitle, new Size(tw, 200), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height);
            int subH = TextRenderer.MeasureText(subtitle, fSub, new Size(tw, 60), TextFormatFlags.NoPrefix).Height;
            int blockH = titleH + 26 + 5 + 22 + subH;
            int top = Pad + Math.Max(0, (Art - blockH) / 2);
            TextRenderer.DrawText(g, title, fTitle, new Rectangle(tx, top, tw, titleH), Theme.TextCol,
                TextFormatFlags.Left | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            using (var rule = new SolidBrush(Theme.Blend(tint, Color.White, 0.10)))
            using (var rp = Theme.RoundedRect(new Rectangle(tx, top + titleH + 26, 96, 5), 2.5f))
                g.FillPath(rule, rp);
            TextRenderer.DrawText(g, subtitle, fSub, new Rectangle(tx, top + titleH + 26 + 5 + 22, tw, subH + 6), Theme.Subtle,
                TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        }

        // ---- the songs ----
        using (var fNum = Theme.UiFont(13f, FontStyle.Bold))
        using (var fName = Theme.UiFont(16f, FontStyle.Bold))
        using (var fArtist = Theme.UiFont(14f))
        {
            int colW = twoCol ? (W - 2 * Pad - 44) / 2 : W - 2 * Pad;
            for (int i = 0; i < shown; i++)
            {
                int col = twoCol && i >= rows ? 1 : 0;
                int row = twoCol ? i - col * rows : i;
                int x = Pad + col * (colW + 44), y = listTop + row * RowH;
                TextRenderer.DrawText(g, (i + 1).ToString(), fNum, new Rectangle(x, y, 34, RowH - 6), Theme.Faint,
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                int nameW = TextRenderer.MeasureText(songs[i].Title, fName, new Size(int.MaxValue, RowH), TextFormatFlags.NoPrefix).Width;
                int room = colW - 48;
                int useName = Math.Min(nameW, songs[i].Artist.Length > 0 ? (int)(room * 0.62) : room);
                TextRenderer.DrawText(g, songs[i].Title, fName, new Rectangle(x + 48, y, useName, RowH - 6), Theme.TextCol,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
                if (songs[i].Artist.Length > 0)
                    TextRenderer.DrawText(g, songs[i].Artist, fArtist, new Rectangle(x + 48 + useName + 14, y, room - useName - 14, RowH - 6), Theme.Faint,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            }
            if (more > 0)
                TextRenderer.DrawText(g, Loc.T("+ {0} more", more), fArtist, new Rectangle(Pad + 48, listTop + rows * RowH + 6, W - 2 * Pad, 32), Theme.Faint,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }

        // ---- the mark at the foot ----
        using (var logo = CoverArt.AppLogo())   // the app's own tile, already in the accent's hue
            g.DrawImage(logo, Pad, h - 76, 40, 40);
        using (var fFoot = Theme.UiFont(14f, FontStyle.Bold))
            TextRenderer.DrawText(g, "Mixtape", fFoot, new Rectangle(Pad + 52, h - 76, 300, 40), Theme.Subtle,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        using (var fFoot2 = Theme.UiFont(13f))
            TextRenderer.DrawText(g, footer, fFoot2, new Rectangle(W - Pad - 500, h - 76, 500, 40), Theme.Faint,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        return bmp;
    }

    private static bool Fits(string s, int w)
    {
        using var f = Theme.DisplayFont(50f, FontStyle.Bold);
        return TextRenderer.MeasureText(s, f, new Size(w, 200), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height <= 130;
    }

    private static void DrawArt(Graphics g, Rectangle r, IReadOnlyList<Bitmap> covers, string title)
    {
        float rad = r.Width * Theme.TileFrac * 0.55f;
        using var clip = Theme.RoundedRect(r, rad);
        var saved = g.Clip;
        g.SetClip(clip);
        if (covers.Count >= 4)
        {
            int half = r.Width / 2;
            for (int i = 0; i < 4; i++)
                g.DrawImage(covers[i], new Rectangle(r.X + i % 2 * half, r.Y + i / 2 * half, half, half));
        }
        else if (covers.Count > 0)
        {
            g.DrawImage(covers[0], r);
        }
        else
        {
            using var cassette = CoverArt.GenerateTitled(CoverArt.CassetteId, r.Width, title);
            g.DrawImage(cassette, r);
        }
        g.Clip = saved;
        using (var frame = new Pen(Color.FromArgb(40, 255, 255, 255), 1.6f))
        using (var fp = Theme.RoundedRect(new RectangleF(r.X + 0.8f, r.Y + 0.8f, r.Width - 1.6f, r.Height - 1.6f), rad))
            g.DrawPath(frame, fp);
    }
}
