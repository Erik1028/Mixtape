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
    private readonly record struct Spec(float Hmm, float Wmm, Color Body, Shape Shape, float ScreenFrac);

    private const float MaxMm = 104f; // the tallest models (full-size iPod / Classic) fill the tile

    private static readonly Color White = Color.FromArgb(240, 242, 245);
    private static readonly Color Black = Color.FromArgb(56, 59, 65);
    private static readonly Color Silver = Color.FromArgb(206, 210, 216);
    private static Color Anod(double hue) => Theme.HsvToColor(hue, 0.32, 0.78); // muted anodized-aluminium tone

    private static Spec For(IPodGeneration g) => g switch
    {
        // full-size click-wheel iPods (~104×62 mm); colour models have bigger screens
        IPodGeneration.First or IPodGeneration.Second => new(104f, 62f, White, Shape.ClickWheel, 0.34f),
        IPodGeneration.Third => new(104f, 62f, White, Shape.ClickWheel, 0.36f),
        IPodGeneration.Fourth => new(104f, 61f, White, Shape.ClickWheel, 0.40f),
        IPodGeneration.Photo => new(104f, 61f, White, Shape.ClickWheel, 0.46f),
        IPodGeneration.Video => new(103.5f, 61.8f, White, Shape.ClickWheel, 0.50f),   // the user's 5G — big 2.5" screen
        IPodGeneration.Classic1 or IPodGeneration.Classic2 or IPodGeneration.Classic3
            => new(103.5f, 61.8f, Black, Shape.ClickWheel, 0.50f),                     // black/space-grey
        IPodGeneration.Mini1 or IPodGeneration.Mini2 => new(91.4f, 50.8f, Silver, Shape.ClickWheel, 0.30f), // small body, small screen, big wheel
        IPodGeneration.Nano1 => new(90f, 40f, White, Shape.ClickWheel, 0.42f),         // tall + thin
        IPodGeneration.Nano2 => new(90f, 40f, Anod(140), Shape.ClickWheel, 0.44f),     // coloured aluminium
        IPodGeneration.Nano3 => new(70f, 52f, Silver, Shape.ClickWheel, 0.50f),        // "fat"/squat, wide screen
        IPodGeneration.Nano4 => new(90.7f, 38.7f, Anod(215), Shape.ClickWheel, 0.52f), // very tall + thin
        IPodGeneration.Nano5 => new(90.7f, 38.7f, Anod(15), Shape.ClickWheel, 0.56f),  // tall, bigger screen
        IPodGeneration.Nano6 => new(40.9f, 37.5f, Anod(195), Shape.TouchSquare, 0f),   // tiny square touch + clip
        IPodGeneration.Nano7 => new(76.5f, 39.6f, Anod(225), Shape.TouchTall, 0f),     // tall touch + home button
        IPodGeneration.Shuffle => new(41f, 27f, Anod(150), Shape.Shuffle, 0f),         // tiny clip-on, no screen
        _ => new(103.5f, 61.8f, White, Shape.ClickWheel, 0.46f),                        // Unknown → generic full-size
    };

    public static Bitmap Render(IPodGeneration gen, int size)
    {
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        // rounded tile background: subtle dark gradient with a faint accent tint
        using (var tp = Theme.RoundedRect(new RectangleF(0.5f, 0.5f, size - 1, size - 1), size * 0.14f))
        using (var bg = new LinearGradientBrush(new RectangleF(0, 0, size, size),
            Theme.Blend(Theme.PanelBg, Theme.Accent, 0.18), Theme.Blend(Theme.PanelBg, Color.Black, 0.22), 55f))
            g.FillPath(bg, tp);

        var spec = For(gen);
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
            using (var bb = new LinearGradientBrush(body, Theme.Blend(spec.Body, Color.White, 0.18), Theme.Blend(spec.Body, Color.Black, 0.16), 90f))
                g.FillPath(bb, bp);
            using var pen = new Pen(Color.FromArgb(70, 0, 0, 0), 1f);
            g.DrawPath(pen, bp);
        }

        switch (spec.Shape)
        {
            case Shape.ClickWheel: DrawClickWheel(g, body, spec.ScreenFrac); break;
            case Shape.TouchSquare: DrawTouchSquare(g, body); break;
            case Shape.TouchTall: DrawTouchTall(g, body); break;
            case Shape.Shuffle: DrawShuffle(g, body); break;
        }
        return bmp;
    }

    private static void DrawScreen(Graphics g, RectangleF screen, float radius)
    {
        using var scp = Theme.RoundedRect(screen, radius);
        using (var scb = new LinearGradientBrush(screen, Color.FromArgb(20, 28, 34), Color.FromArgb(8, 11, 15), 90f)) g.FillPath(scb, scp);
        var saved = g.Clip;
        g.SetClip(scp);
        using (var glow = new SolidBrush(Color.FromArgb(46, Theme.Accent)))
            g.FillEllipse(glow, screen.X + screen.Width * 0.08f, screen.Y + screen.Height * 0.25f, screen.Width * 0.84f, screen.Height * 0.95f);
        g.Clip = saved;
    }

    private static void DrawClickWheel(Graphics g, RectangleF body, float screenFrac)
    {
        float pad = body.Width * 0.1f;
        DrawScreen(g, new RectangleF(body.X + pad, body.Y + pad, body.Width - 2 * pad, body.Height * screenFrac), body.Width * 0.05f);

        // The wheel fills the space below the screen — so a small-screen model (Mini) gets a big wheel
        // and a big-screen model (5G/Classic) a proportionally smaller one, like the real devices.
        float lowerTop = body.Y + body.Height * (screenFrac + pad / body.Height + 0.04f);
        float lowerH = body.Bottom - body.Height * 0.06f - lowerTop;
        float wd = Math.Min(body.Width * 0.76f, lowerH);
        var wheel = new RectangleF(body.X + (body.Width - wd) / 2, lowerTop + (lowerH - wd) / 2, wd, wd);
        using (var wb = new SolidBrush(Color.FromArgb(60, 0, 0, 0))) g.FillEllipse(wb, wheel); // recessed wheel reads on any body colour
        using (var wp = new Pen(Color.FromArgb(45, 255, 255, 255), 1f)) g.DrawEllipse(wp, wheel);
        float cc = wd * 0.36f;
        using (var cb = new SolidBrush(Color.FromArgb(70, 0, 0, 0))) g.FillEllipse(cb, wheel.X + (wd - cc) / 2, wheel.Y + (wd - cc) / 2, cc, cc);
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

    private static void DrawShuffle(Graphics g, RectangleF body)
    {
        float d = body.Width * 0.62f;
        var pad = new RectangleF(body.X + (body.Width - d) / 2, body.Y + (body.Height - d) / 2, d, d);
        using (var wb = new SolidBrush(Color.FromArgb(55, 0, 0, 0))) g.FillEllipse(wb, pad);
        using (var wp = new Pen(Color.FromArgb(55, 255, 255, 255), 1f)) g.DrawEllipse(wp, pad);
        float c0 = d * 0.34f;
        using (var cb = new SolidBrush(Color.FromArgb(70, 0, 0, 0))) g.FillEllipse(cb, pad.X + (d - c0) / 2, pad.Y + (d - c0) / 2, c0, c0);
    }
}
