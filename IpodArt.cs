using System.Drawing.Drawing2D;

namespace iPodCommander;

/// <summary>
/// Draws a per-generation iPod illustration for the device page: distinct silhouettes, proportions and
/// body colours for the click-wheel models (1G–5G, Photo, Classic, Mini), the various nanos (tall-thin
/// 1G/4G/5G, squat 3G, square touch 6G, tall touch 7G) and the clip-on shuffle. Recognizable at ~150px.
/// </summary>
internal static class IpodArt
{
    private enum Shape { ClickWheel, TouchSquare, TouchTall, Shuffle }
    // Hmm/Wmm = the model's REAL body height/width in millimetres, so proportions AND relative sizes are
    // accurate (a Mini renders smaller than a Classic, a nano thinner, the nano6/shuffle tiny). ScreenFrac
    // = the fraction of body height the display occupies (a Mini has a small screen + a big wheel).
    private readonly record struct Spec(float Hmm, float Wmm, Shape Shape, float ScreenFrac);

    private const float MaxMm = 104f; // the tallest models (full-size iPod / Classic) fill the tile

    // Shape + real dimensions per generation; the BODY COLOUR comes from IpodColors (per model number).
    private static Spec For(IPodGeneration g) => g switch
    {
        // full-size click-wheel iPods (~104×62 mm); colour models have bigger screens
        IPodGeneration.First or IPodGeneration.Second => new(104f, 62f, Shape.ClickWheel, 0.34f),
        IPodGeneration.Third => new(104f, 62f, Shape.ClickWheel, 0.36f),
        IPodGeneration.Fourth => new(104f, 61f, Shape.ClickWheel, 0.40f),
        IPodGeneration.Photo => new(104f, 61f, Shape.ClickWheel, 0.46f),
        IPodGeneration.Video => new(103.5f, 61.8f, Shape.ClickWheel, 0.50f),            // the user's 5G — big 2.5" screen
        IPodGeneration.Classic1 or IPodGeneration.Classic2 or IPodGeneration.Classic3
            => new(103.5f, 61.8f, Shape.ClickWheel, 0.50f),
        IPodGeneration.Mini1 or IPodGeneration.Mini2 => new(91.4f, 50.8f, Shape.ClickWheel, 0.30f), // small body, small screen, big wheel
        IPodGeneration.Nano1 => new(90f, 40f, Shape.ClickWheel, 0.42f),                 // tall + thin
        IPodGeneration.Nano2 => new(90f, 40f, Shape.ClickWheel, 0.44f),                 // coloured aluminium
        IPodGeneration.Nano3 => new(70f, 52f, Shape.ClickWheel, 0.50f),                 // "fat"/squat, wide screen
        IPodGeneration.Nano4 => new(90.7f, 38.7f, Shape.ClickWheel, 0.52f),            // very tall + thin
        IPodGeneration.Nano5 => new(90.7f, 38.7f, Shape.ClickWheel, 0.56f),            // tall, bigger screen
        IPodGeneration.Nano6 => new(40.9f, 37.5f, Shape.TouchSquare, 0f),               // tiny square touch + clip
        IPodGeneration.Nano7 => new(76.5f, 39.6f, Shape.TouchTall, 0f),                 // tall touch + home button
        IPodGeneration.Shuffle => new(41f, 27f, Shape.Shuffle, 0f),                     // tiny clip-on, no screen
        _ => new(103.5f, 61.8f, Shape.ClickWheel, 0.46f),                                // Unknown → generic full-size
    };

    public static Bitmap Render(IPodGeneration gen, int size, string? modelNumber = null)
    {
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);

        // rounded tile background: subtle dark gradient with a faint accent tint
        using (var tp = Theme.RoundedRect(new RectangleF(0.5f, 0.5f, size - 1, size - 1), size * Theme.TileFrac))
        using (var bg = new LinearGradientBrush(new RectangleF(0, 0, size, size),
            Theme.Blend(Theme.PanelBg, Theme.Accent, 0.14), Theme.Blend(Theme.PanelBg, Color.Black, 0.22), Theme.ArtAngle))
            g.FillPath(bg, tp);

        var spec = For(gen);
        Color bodyColor = IpodColors.Resolve(modelNumber, gen); // real Apple colour for this exact model
        // Scale by real mm so the tallest model fills ~0.84 of the tile and every other model is sized
        // proportionally to it — accurate aspect ratios AND accurate relative sizes.
        float unit = 0.84f * size / MaxMm;
        float bw = spec.Wmm * unit, bh = spec.Hmm * unit, bx = (size - bw) / 2f, by = (size - bh) / 2f;
        var body = new RectangleF(bx, by, bw, bh);
        float radius = (spec.Shape == Shape.Shuffle ? 0.22f : 0.16f) * Math.Min(bw, bh);

        // drop shadow + metal body
        using (var sp = Theme.RoundedRect(new RectangleF(bx + size * 0.015f, by + size * 0.03f, bw, bh), radius))
        using (var sh = new SolidBrush(Color.FromArgb(85, 0, 0, 0))) g.FillPath(sh, sp);
        using (var bp = Theme.RoundedRect(body, radius))
        {
            using (var bb = new LinearGradientBrush(body, Theme.Blend(bodyColor, Color.White, 0.18), Theme.Blend(bodyColor, Color.Black, 0.16), 90f))
                g.FillPath(bb, bp);
            // a soft glossy sheen across the top third (plastic/anodised highlight)
            var saved = g.Clip; g.SetClip(bp);
            using (var sheen = new LinearGradientBrush(new RectangleF(body.X, body.Y, body.Width, body.Height * 0.38f), Color.FromArgb(46, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 90f))
                g.FillRectangle(sheen, body.X, body.Y, body.Width, body.Height * 0.38f);
            g.Clip = saved;
            using var pen = new Pen(Color.FromArgb(70, 0, 0, 0), 1f);
            g.DrawPath(pen, bp);
        }

        switch (spec.Shape)
        {
            case Shape.ClickWheel: DrawClickWheel(g, body, spec.ScreenFrac, bodyColor); break;
            case Shape.TouchSquare: DrawTouchSquare(g, body); break;
            case Shape.TouchTall: DrawTouchTall(g, body); break;
            case Shape.Shuffle: DrawShuffle(g, body, bodyColor); break;
        }
        return bmp;
    }

    private static double Lum(Color c) => (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;

    private static void DrawScreen(Graphics g, RectangleF screen, float radius)
    {
        using var scp = Theme.RoundedRect(screen, radius);
        // dark reflective glass (not a coloured glow — reads as a real iPod screen at any size)
        using (var scb = new LinearGradientBrush(screen, Color.FromArgb(22, 26, 32), Color.FromArgb(8, 10, 14), 90f)) g.FillPath(scb, scp);
        var saved = g.Clip;
        g.SetClip(scp);
        // a soft diagonal glass reflection across the top
        using (var refl = new LinearGradientBrush(new RectangleF(screen.X, screen.Y - 1, screen.Width, screen.Height * 0.6f + 1), Color.FromArgb(34, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 60f))
            g.FillRectangle(refl, screen.X, screen.Y - 1, screen.Width, screen.Height * 0.6f + 1);
        g.Clip = saved;
        using var rim = new Pen(Color.FromArgb(110, 0, 0, 0), 1f);   // crisp inner bezel
        g.DrawPath(rim, scp);
    }

    private static void DrawClickWheel(Graphics g, RectangleF body, float screenFrac, Color bodyColor)
    {
        float pad = body.Width * 0.1f;
        DrawScreen(g, new RectangleF(body.X + pad, body.Y + pad, body.Width - 2 * pad, body.Height * screenFrac), body.Width * 0.05f);

        // The wheel fills the space below the screen — so a small-screen model (Mini) gets a big wheel
        // and a big-screen model (5G/Classic) a proportionally smaller one, like the real devices.
        float lowerTop = body.Y + body.Height * (screenFrac + pad / body.Height + 0.04f);
        float lowerH = body.Bottom - body.Height * 0.06f - lowerTop;
        float wd = Math.Min(body.Width * 0.78f, lowerH);
        var wheel = new RectangleF(body.X + (body.Width - wd) / 2, lowerTop + (lowerH - wd) / 2, wd, wd);

        // A real click wheel is the SAME colour family as the body (white wheel on a white iPod, grey on metal),
        // not a black hole. Tint it slightly for contrast, with a faint rim.
        bool dark = Lum(bodyColor) < 0.5;
        Color wheelFill = dark ? Theme.Blend(bodyColor, Color.White, 0.12) : Theme.Blend(bodyColor, Color.Black, 0.07);
        using (var wb = new SolidBrush(wheelFill)) g.FillEllipse(wb, wheel);
        using (var wp = new Pen(Color.FromArgb(dark ? 70 : 45, 0, 0, 0), 1f)) g.DrawEllipse(wp, wheel.X + 0.5f, wheel.Y + 0.5f, wheel.Width - 1, wheel.Height - 1);

        float cx = wheel.X + wd / 2f, cy = wheel.Y + wd / 2f, r = wd * 0.37f;   // ring radius the labels sit on
        Color label = dark ? Color.FromArgb(150, 235, 235, 235) : Color.FromArgb(135, 60, 60, 60);
        if (wd >= 42f)   // only label the wheel when it's big enough to read (skip on the tiny header/sidebar icon)
        {
            float gs = wd * 0.05f;   // glyph size unit
            using (var f = Theme.UiFont(Math.Max(5.5f, wd * 0.082f), FontStyle.Bold))
                TextRenderer.DrawText(g, "MENU", f, Rectangle.Round(new RectangleF(cx - wd * 0.28f, cy - r - wd * 0.085f, wd * 0.56f, wd * 0.2f)), label,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            DoubleTri(g, label, cx - r, cy, gs, -1);   // ◀◀
            DoubleTri(g, label, cx + r, cy, gs, +1);   // ▶▶
            PlayPause(g, label, cx, cy + r, gs);       // ▶❙❙
        }

        // raised centre button
        float cc = wd * 0.34f;
        Color centre = dark ? Theme.Blend(bodyColor, Color.White, 0.05) : Theme.Blend(bodyColor, Color.Black, 0.04);
        using (var cb = new SolidBrush(centre)) g.FillEllipse(cb, cx - cc / 2, cy - cc / 2, cc, cc);
        using (var cp = new Pen(Color.FromArgb(dark ? 80 : 50, 0, 0, 0), 1f)) g.DrawEllipse(cp, cx - cc / 2, cy - cc / 2, cc, cc);
    }

    // ◀◀ / ▶▶ : two stacked triangles. dir = -1 points left, +1 points right. (cx,cy) is the pair's centre.
    private static void DoubleTri(Graphics g, Color c, float cx, float cy, float s, int dir)
    {
        using var b = new SolidBrush(c);
        for (int k = -1; k <= 0; k++)
        {
            float ox = cx + dir * (k + 0.5f) * s * 1.15f;
            g.FillPolygon(b, new[] { new PointF(ox - dir * s * 0.55f, cy - s * 0.62f), new PointF(ox - dir * s * 0.55f, cy + s * 0.62f), new PointF(ox + dir * s * 0.55f, cy) });
        }
    }

    // ▶❙❙ : a play triangle followed by two pause bars. (cx,cy) is the glyph centre.
    private static void PlayPause(Graphics g, Color c, float cx, float cy, float s)
    {
        using var b = new SolidBrush(c);
        g.FillPolygon(b, new[] { new PointF(cx - s * 1.25f, cy - s * 0.62f), new PointF(cx - s * 1.25f, cy + s * 0.62f), new PointF(cx - s * 0.25f, cy) });
        g.FillRectangle(b, cx + s * 0.25f, cy - s * 0.62f, s * 0.34f, s * 1.24f);
        g.FillRectangle(b, cx + s * 0.78f, cy - s * 0.62f, s * 0.34f, s * 1.24f);
    }

    private static void DrawTouchSquare(Graphics g, RectangleF body)
    {
        float pad = body.Width * 0.1f;
        var screen = new RectangleF(body.X + pad, body.Y + pad, body.Width - 2 * pad, body.Height - 2 * pad);
        DrawScreen(g, screen, body.Width * 0.06f);
        // a little clip tab peeking from the top edge
        float cw = body.Width * 0.3f;
        using var cb = new SolidBrush(Theme.Blend(Theme.PanelBg, Color.White, 0.10));
        using var cp = Theme.RoundedRect(new RectangleF(body.X + (body.Width - cw) / 2, body.Y - body.Height * 0.06f, cw, body.Height * 0.1f), body.Width * 0.04f);
        g.FillPath(cb, cp);
    }

    private static void DrawTouchTall(Graphics g, RectangleF body)
    {
        float pad = body.Width * 0.12f;
        DrawScreen(g, new RectangleF(body.X + pad, body.Y + pad, body.Width - 2 * pad, body.Height * 0.74f), body.Width * 0.08f);
        float d = body.Width * 0.22f; // home button
        using var hb = new SolidBrush(Color.FromArgb(70, 0, 0, 0));
        g.FillEllipse(hb, body.X + (body.Width - d) / 2, body.Y + body.Height * 0.86f - d / 2, d, d);
    }

    private static void DrawShuffle(Graphics g, RectangleF body, Color bodyColor)
    {
        bool dark = Lum(bodyColor) < 0.5;
        float d = body.Width * 0.62f;
        var pad = new RectangleF(body.X + (body.Width - d) / 2, body.Y + (body.Height - d) / 2, d, d);
        using (var wb = new SolidBrush(dark ? Theme.Blend(bodyColor, Color.White, 0.10) : Theme.Blend(bodyColor, Color.Black, 0.06))) g.FillEllipse(wb, pad);
        using (var wp = new Pen(Color.FromArgb(dark ? 65 : 45, 0, 0, 0), 1f)) g.DrawEllipse(wp, pad);
        // ▶❙❙ + ◀◀ / ▶▶ around the tiny control pad, like the clip-on shuffle's ring
        Color label = dark ? Color.FromArgb(150, 235, 235, 235) : Color.FromArgb(135, 60, 60, 60);
        if (d >= 30f)
        {
            float cx = pad.X + d / 2f, cy = pad.Y + d / 2f, r = d * 0.34f, gs = d * 0.05f;
            DoubleTri(g, label, cx - r, cy, gs, -1);
            DoubleTri(g, label, cx + r, cy, gs, +1);
            PlayPause(g, label, cx, cy - r, gs);
        }
        float c0 = d * 0.34f;
        using (var cb = new SolidBrush(dark ? Theme.Blend(bodyColor, Color.White, 0.04) : Theme.Blend(bodyColor, Color.Black, 0.04))) g.FillEllipse(cb, pad.X + (d - c0) / 2, pad.Y + (d - c0) / 2, c0, c0);
        using (var cp = new Pen(Color.FromArgb(dark ? 75 : 50, 0, 0, 0), 1f)) g.DrawEllipse(cp, pad.X + (d - c0) / 2, pad.Y + (d - c0) / 2, c0, c0);
    }
}
