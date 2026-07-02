using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace iPodCommander;

/// <summary>
/// A popup window that has captured a blurred image of the app content sitting BEHIND it, so the controls
/// inside it can paint themselves as frosted glass over that blur. The frost bitmap is in the host's CLIENT
/// coordinates (0,0 = client top-left), full client resolution, so a child control slices it 1:1 at its own
/// offset. Null means "no glass" — controls then fall back to their normal opaque fill.
/// </summary>
internal interface IGlassHost
{
    Bitmap? GlassFrost { get; }
    /// <summary>The translucent tint this host lays over the blur. Modal dialogs frost HARDER than flyouts (just a
    /// whisper of content) so the app behind reads as an elegant matte sheet, not a busy reflection.</summary>
    Color GlassTint { get; }
}

/// <summary>
/// Shared frosted-glass plumbing for popups (flyouts + dialogs). WinForms can't alpha-composite overlapping
/// sibling/child controls, so instead of one translucent layer we give every glass surface its OWN slice of a
/// single captured-and-blurred backdrop: the host snaps the content behind the popup once (no per-frame work —
/// the content behind a popup is static while it's open), and each control draws the slice under itself plus a
/// translucent tint. The look matches the now-playing bar's frost and stays on-brand with the dark theme.
/// </summary>
internal static class Glass
{
    /// <summary>Master switch for the FLYOUT glass (Equalizer / Pro / Up Next): when false those popups stay plain opaque.
    /// Set from <c>AppSettings.GlassPopups</c> right before a flyout is created; the flyout reads it into its GlassEnabled.</summary>
    public static bool PopupsEnabled = true;

    private static SolidBrush? _tintBrush; private static Color _tintColor;   // reused tint brush for PaintBackground (rebuilt only when the tint colour changes — e.g. theme switch)


    /// <summary>LIVE adaptive tint alpha for the flyout glass. Apple's material adds a dimming layer over BRIGHT
    /// content and none over dark (HIG: "if the underlying content is bright, consider adding a dark dimming layer
    /// of 35% opacity") — 136 is the tuned dark-backdrop baseline, and the flyout worker raises it toward 176 over
    /// bright/busy backdrops (art-heavy views). Volatile: written by the glass worker thread, read by UI paints.</summary>
    public static volatile int LiveTintAlpha = 136;

    /// <summary>Target tint alpha for a backdrop of the given mean luma (0-255): exactly the tuned 136 for the
    /// normal dark theme (luma ≤ 60 → no change to the approved look), ramping to 176 over bright content — the
    /// extra coverage matches the HIG's 35% dimming layer.</summary>
    public static int TintTargetFor(float meanLuma) => Math.Clamp(136 + (int)(0.45f * Math.Max(0f, meanLuma - 60f)), 136, 176);

    /// <summary>The translucent tint for FLYOUTS — more see-through (the focused content behind reads nicely).
    /// Lower alpha = more of the blurred content shows through (dropped 150→136 with the Vibrance pass, so the
    /// boosted colours actually reach the eye instead of being re-buried by the tint).</summary>
    public static Color SurfaceTint => Color.FromArgb(LiveTintAlpha, Theme.SidebarBg);

    /// <summary>The translucent tint for big MODAL dialogs — frosted harder, so the busy app behind a large window
    /// is only a faint whisper (an elegant matte sheet, not a distracting reflection).</summary>
    public static Color DialogTint => Color.FromArgb(212, Theme.SidebarBg);

    /// <summary>Capture the app content behind <paramref name="popup"/> (rendered from its <paramref name="owner"/>
    /// window), blurred, sized to the popup's client area in the popup's own client coordinates. The content
    /// behind a popup doesn't move while it's open, so this one-shot capture needs no refresh. Returns null if it
    /// can't capture (the caller then shows its normal opaque background).</summary>
    public static Bitmap? Capture(Form popup, Form? owner)
    {
        try
        {
            int w = popup.ClientSize.Width, h = popup.ClientSize.Height;
            if (w < 8 || h < 8) return null;
            owner ??= popup.Owner;   // explicit owner only — no ActiveForm fallback (else an owner-less popup self-captures)
            if (owner is null || owner.IsDisposed || !owner.IsHandleCreated) return null;

            int ow = Math.Max(1, owner.ClientSize.Width), oh = Math.Max(1, owner.ClientSize.Height);
            using var ownerBmp = new Bitmap(ow, oh);
            owner.DrawToBitmap(ownerBmp, new Rectangle(0, 0, ow, oh));   // owner-drawn UI prints fine; works off-screen too

            Point off = owner.PointToClient(popup.PointToScreen(Point.Empty));   // DPI-correct popup-in-owner-client offset
            int ox = off.X, oy = off.Y;
            if (ow >= w) ox = Math.Clamp(ox, 0, ow - w);   // keep the slice inside the owner → no hard "frame" edge
            if (oh >= h) oy = Math.Clamp(oy, 0, oh - h);

            // Full-res slice of the owner under the popup (areas off the owner fall back to the theme bg).
            using var slice = new Bitmap(w, h);
            using (var g = Graphics.FromImage(slice))
            {
                using (var bg = new SolidBrush(Theme.Bg)) g.FillRectangle(bg, 0, 0, w, h);
                g.DrawImage(ownerBmp, new Rectangle(0, 0, w, h), new Rectangle(ox, oy, w, h), GraphicsUnit.Pixel);
            }

            // Downsample then upsample → a smooth blur, back at full client size so control slices stay 1:1.
            const int k = 7;
            int tw = Math.Max(8, w / k), th = Math.Max(8, h / k);
            using var tiny = new Bitmap(tw, th);
            using (var g = Graphics.FromImage(tiny))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(slice, 0, 0, tw, th);
            }
            var frost = new Bitmap(w, h);
            using (var g = Graphics.FromImage(frost))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(tiny, new Rectangle(0, 0, w, h));
            }
            return frost;
        }
        catch { return null; }
    }

    /// <summary>The SHARP (unblurred) owner slice under the popup PLUS a <paramref name="margin"/> px border on every side.
    /// The live flyout glass caches this ONCE as the static backdrop and re-composites only the now-playing bar over it each
    /// tick (cheap), instead of re-rendering the whole window. Blur it with <see cref="BlurInto"/> before <see cref="Refract"/>.</summary>
    public static Bitmap? CapturePaddedRaw(Form popup, Form? owner, int margin)
    {
        owner ??= popup.Owner;
        try { return CaptureRegionPaddedRaw(owner, new Rectangle(popup.PointToScreen(Point.Empty), popup.ClientSize), margin); }
        catch { return null; }
    }

    /// <summary>Snapshot a form's whole WINDOW. Control.DrawToBitmap on a Form prints the NON-CLIENT area too
    /// (WM_PRINT | PRF_NONCLIENT), so the bitmap's origin is the WINDOW top-left — for the borderless main window
    /// that equals the client area, but a captioned dialog's content sits (frame, caption) px in. Slice regions
    /// from this with <see cref="SliceRegionPadded"/>, which maps in window coordinates for exactly that reason.</summary>
    public static Bitmap? CaptureWindow(Form? owner)
    {
        if (owner is null || owner.IsDisposed || !owner.IsHandleCreated) return null;
        try
        {
            int ow = Math.Max(1, owner.Width), oh = Math.Max(1, owner.Height);
            var bmp = new Bitmap(ow, oh);
            owner.DrawToBitmap(bmp, new Rectangle(0, 0, ow, oh));
            return bmp;
        }
        catch { return null; }
    }

    /// <summary>Slice the padded region under <paramref name="screenRect"/> out of a <see cref="CaptureWindow"/>
    /// snapshot (window-coordinate mapping — NC-safe for captioned owners). Off-snapshot areas fall back to the
    /// theme background. The caller owns the returned bitmap; the snapshot can be re-sliced for many popups.</summary>
    public static Bitmap? SliceRegionPadded(Bitmap? snap, Form? owner, Rectangle screenRect, int margin)
    {
        if (snap is null || owner is null || owner.IsDisposed) return null;
        try
        {
            int w = screenRect.Width, h = screenRect.Height;
            if (w < 8 || h < 8) return null;
            int pw = w + 2 * margin, ph = h + 2 * margin;
            int ox = screenRect.X - owner.Left - margin, oy = screenRect.Y - owner.Top - margin;   // window coords
            var slice = new Bitmap(pw, ph);
            using (var g = Graphics.FromImage(slice))
            {
                using (var bg = new SolidBrush(Theme.Bg)) g.FillRectangle(bg, 0, 0, pw, ph);
                g.DrawImage(snap, new Rectangle(0, 0, pw, ph), new Rectangle(ox, oy, pw, ph), GraphicsUnit.Pixel);
            }
            return slice;
        }
        catch { return null; }
    }

    /// <summary>Rect-based variant of <see cref="CapturePaddedRaw"/> for popups that aren't Forms (the themed
    /// context menus are ToolStripDropDown windows): captures the owner content under <paramref name="screenRect"/>
    /// plus a margin border. Same padded layout, same fallbacks.</summary>
    public static Bitmap? CaptureRegionPaddedRaw(Form? owner, Rectangle screenRect, int margin)
    {
        try
        {
            using var snap = CaptureWindow(owner);
            return SliceRegionPadded(snap, owner, screenRect, margin);
        }
        catch { return null; }
    }

    /// <summary>Like <see cref="CapturePaddedRaw"/> but REUSES the caller-owned <paramref name="ownerBuf"/> (full owner snapshot)
    /// and <paramref name="sliceBuf"/> (the padded region) across calls — so the LIVE per-tick capture allocates no ~MB bitmaps
    /// (the per-tick LOH churn was the fps killer). Returns <paramref name="sliceBuf"/> (do NOT dispose it per call).</summary>
    public static Bitmap? CapturePaddedRawInto(Form popup, Form? owner, int margin, ref Bitmap? ownerBuf, ref Bitmap? sliceBuf)
    {
        try
        {
            int w = popup.ClientSize.Width, h = popup.ClientSize.Height;
            if (w < 8 || h < 8) return null;
            owner ??= popup.Owner;
            if (owner is null || owner.IsDisposed || !owner.IsHandleCreated) return null;
            int pw = w + 2 * margin, ph = h + 2 * margin;
            int ow = Math.Max(1, owner.ClientSize.Width), oh = Math.Max(1, owner.ClientSize.Height);
            if (ownerBuf is null || ownerBuf.Width != ow || ownerBuf.Height != oh) { ownerBuf?.Dispose(); ownerBuf = new Bitmap(ow, oh, PixelFormat.Format32bppArgb); }
            owner.DrawToBitmap(ownerBuf, new Rectangle(0, 0, ow, oh));
            Point off = owner.PointToClient(popup.PointToScreen(Point.Empty));
            int ox = off.X - margin, oy = off.Y - margin;
            if (sliceBuf is null || sliceBuf.Width != pw || sliceBuf.Height != ph) { sliceBuf?.Dispose(); sliceBuf = new Bitmap(pw, ph, PixelFormat.Format32bppArgb); }
            using (var g = Graphics.FromImage(sliceBuf))
            {
                using (var bg = new SolidBrush(Theme.Bg)) g.FillRectangle(bg, 0, 0, pw, ph);
                g.DrawImage(ownerBuf, new Rectangle(0, 0, pw, ph), new Rectangle(ox, oy, pw, ph), GraphicsUnit.Pixel);
            }
            return sliceBuf;
        }
        catch { return null; }
    }

    /// <summary>Light box blur of <paramref name="src"/> INTO <paramref name="dst"/> (same size) via a downsample/upsample
    /// through <paramref name="tiny"/> (≈src/k). All three buffers are caller-owned so the live re-blur allocates nothing.</summary>
    /// <summary>Blur src into dst via the tiny downsample, with the Vibrance pass on the tiny. Returns the backdrop's
    /// mean luma (0-255, measured PRE-vibrance, optionally ignoring a <paramref name="lumaInset"/> px border of the
    /// tiny — the flyout passes margin/3 so the 60px capture padding doesn't dilute what's actually under the panel);
    /// the flyout worker feeds it to <see cref="TintTargetFor"/> for the adaptive dimming layer.</summary>
    public static float BlurInto(Bitmap src, Bitmap dst, Bitmap tiny, int lumaInset = 0)
    {
        using (var g = Graphics.FromImage(tiny)) { g.InterpolationMode = InterpolationMode.HighQualityBilinear; g.PixelOffsetMode = PixelOffsetMode.HighQuality; g.DrawImage(src, 0, 0, tiny.Width, tiny.Height); }
        float luma = Vibrance(tiny, lumaInset: lumaInset);   // on the TINY (1/9th the pixels — sub-ms); the upsample spreads the boosted colour
        using (var g = Graphics.FromImage(dst)) { g.InterpolationMode = InterpolationMode.HighQualityBilinear; g.PixelOffsetMode = PixelOffsetMode.HighQuality; g.DrawImage(tiny, new Rectangle(0, 0, dst.Width, dst.Height)); }
        return luma;
    }

    /// <summary>Apple-style "vibrancy": boost the saturation (plus a whisper of brightness) of a small frost bitmap
    /// IN PLACE, so the colours behind the glass POP through the tint instead of greying out — a big part of what
    /// makes real liquid glass read as glass and not as fog. Per-pixel around the luma axis, clamped. Returns the
    /// mean PRE-vibrance luma (0-255) of the region inside <paramref name="lumaInset"/> (callers that don't need it
    /// just ignore the return).</summary>
    public static float Vibrance(Bitmap bmp, float sat = 1.35f, int lift = 4, int lumaInset = 0)
    {
        int bw = bmp.Width, bh = bmp.Height;
        int ix = Math.Min(lumaInset, Math.Max(0, bw / 2 - 1)), iy = Math.Min(lumaInset, Math.Max(0, bh / 2 - 1));
        long lumaSum = 0; long lumaN = 0;
        var d = bmp.LockBits(new Rectangle(0, 0, bw, bh), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        try
        {
            unsafe
            {
                for (int y = 0; y < bh; y++)
                {
                    int* row = (int*)((byte*)d.Scan0 + y * d.Stride);
                    bool yIn = y >= iy && y < bh - iy;
                    for (int x = 0; x < bw; x++)
                    {
                        int c = row[x];
                        float r = (c >> 16) & 0xFF, g = (c >> 8) & 0xFF, b = c & 0xFF;
                        float raw = 0.299f * r + 0.587f * g + 0.114f * b;
                        if (yIn && x >= ix && x < bw - ix) { lumaSum += (long)raw; lumaN++; }
                        float luma = raw + lift;
                        int rr = Math.Clamp((int)(luma + (r - luma) * sat + 0.5f), 0, 255);
                        int gg = Math.Clamp((int)(luma + (g - luma) * sat + 0.5f), 0, 255);
                        int bb = Math.Clamp((int)(luma + (b - luma) * sat + 0.5f), 0, 255);
                        row[x] = (c & unchecked((int)0xFF000000)) | (rr << 16) | (gg << 8) | bb;
                    }
                }
            }
        }
        finally { bmp.UnlockBits(d); }
        return lumaN > 0 ? lumaSum / (float)lumaN : 0f;
    }

    /// <summary>Like <see cref="Capture"/> but grabs the owner content under the popup PLUS a <paramref name="margin"/>
    /// px border on every side (so <see cref="Refract"/>'s outward edge-magnification samples real adjacent content
    /// instead of a clamped smear). Returns a (clientW+2·margin)×(clientH+2·margin) blurred bitmap, or null.</summary>
    public static Bitmap? CapturePadded(Form popup, Form? owner, int margin)
    {
        var raw = CapturePaddedRaw(popup, owner, margin);
        if (raw is null) return null;
        try
        {
            int k = 3;   // light blur only — liquid glass reads SHARP (Erik wants the content behind crisp), not frosted
            using var tiny = new Bitmap(Math.Max(8, raw.Width / k), Math.Max(8, raw.Height / k));
            var frost = new Bitmap(raw.Width, raw.Height);
            BlurInto(raw, frost, tiny);
            return frost;
        }
        finally { raw.Dispose(); }
    }

    /// <summary>Snapshot the owner window's whole client area ONCE (the expensive DrawToBitmap). For a modal dialog
    /// the owner behind it doesn't change, so the frost can be re-sliced from this cheap snapshot as the dialog is
    /// dragged — no repeated DrawToBitmap. Returns null if the owner can't be captured.</summary>
    public static Bitmap? CaptureOwner(Form? owner)
    {
        if (owner is null || owner.IsDisposed || !owner.IsHandleCreated) return null;
        try
        {
            int ow = Math.Max(1, owner.ClientSize.Width), oh = Math.Max(1, owner.ClientSize.Height);
            var bmp = new Bitmap(ow, oh);
            owner.DrawToBitmap(bmp, new Rectangle(0, 0, ow, oh));
            return bmp;
        }
        catch { return null; }
    }

    /// <summary>Build a blurred frost for <paramref name="popup"/> by slicing <paramref name="ownerSnap"/> at the
    /// popup's CURRENT screen position (so dragging the popup re-reveals the owner content behind its new spot —
    /// the glass "reacts" instead of freezing). Cheap: just a slice + downsample/upsample blur.</summary>
    public static Bitmap? FrostFrom(Bitmap? ownerSnap, Form? owner, Form popup)
    {
        if (ownerSnap is null || owner is null) return null;
        try
        {
            int w = popup.ClientSize.Width, h = popup.ClientSize.Height;
            if (w < 8 || h < 8) return null;
            // The popup's client origin IN THE OWNER'S CLIENT SPACE (same space as the snapshot) — PointToClient does
            // the screen→client + DPI conversion, so the slice lines up exactly with what's behind the popup. (A raw
            // screen-coordinate subtraction drifts at non-100% DPI and reads as a mis-aligned "weird reflection".)
            Point off = owner.PointToClient(popup.PointToScreen(Point.Empty));
            int ox = off.X, oy = off.Y;
            // Keep the slice INSIDE the owner snapshot when it fits — else the off-owner area fills with flat Theme.Bg
            // and that hard edge reads as a "weird frame" in the glass. (At the edges the blur is shifted a hair, but
            // it's blurred so it's invisible — and there's no frame.)
            if (ownerSnap.Width >= w) ox = Math.Clamp(ox, 0, ownerSnap.Width - w);
            if (ownerSnap.Height >= h) oy = Math.Clamp(oy, 0, ownerSnap.Height - h);
            using var slice = new Bitmap(w, h);
            using (var g = Graphics.FromImage(slice))
            {
                using (var bg = new SolidBrush(Theme.Bg)) g.FillRectangle(bg, 0, 0, w, h);
                g.DrawImage(ownerSnap, new Rectangle(0, 0, w, h), new Rectangle(ox, oy, w, h), GraphicsUnit.Pixel);
            }
            const int k = 7;
            int tw = Math.Max(8, w / k), th = Math.Max(8, h / k);
            using var tiny = new Bitmap(tw, th);
            using (var g = Graphics.FromImage(tiny)) { g.InterpolationMode = InterpolationMode.HighQualityBilinear; g.PixelOffsetMode = PixelOffsetMode.HighQuality; g.DrawImage(slice, 0, 0, tw, th); }
            var frost = new Bitmap(w, h);
            using (var g = Graphics.FromImage(frost)) { g.InterpolationMode = InterpolationMode.HighQualityBilinear; g.PixelOffsetMode = PixelOffsetMode.HighQuality; g.DrawImage(tiny, new Rectangle(0, 0, w, h)); }
            return frost;
        }
        catch { return null; }
    }

    /// <summary>"Liquid glass": refract a captured-with-MARGIN backdrop through a rounded-rect glass lens, the way
    /// Apple's effect does — a convex SQUIRCLE bezel profile (`y = ⁴√(1-(1-x)⁴)`) whose Snell-law refraction concentrates
    /// the displacement into a thin band at the very rim; the rim samples INWARD (a convex lens keeps rays inside the
    /// shape → magnifies the content under the rim), with CHROMATIC ABERRATION (R/G/B refract by different amounts → a
    /// subtle colour fringe) and a bottom-weighted directional rim shade. SEPARABLE per-axis so corners stay clean (no
    /// pinch). The margin stays as belt-and-suspenders for the CA offsets + settle overshoot.
    /// <paramref name="paddedFrost"/> is the backdrop captured at the popup size + <paramref name="margin"/> px each side
    /// (so the outward magnification samples real adjacent content, not a clamped smear). Returns the popup-sized result
    /// (the centre crop, in popup-client coords). One-shot on open (a few ms), never per frame. Caller disposes the input.</summary>
    public static Bitmap Refract(Bitmap paddedFrost, int margin, int supersample = 2, float magScale = 1f)
    {
        int w = paddedFrost.Width - 2 * margin, h = paddedFrost.Height - 2 * margin;   // the popup client size
        var outBmp = new Bitmap(Math.Max(1, w), Math.Max(1, h), PixelFormat.Format32bppArgb);
        RefractInto(paddedFrost, outBmp, margin, supersample, magScale);
        return outBmp;
    }

    /// <summary>Like <see cref="Refract"/> but warps INTO a caller-owned <paramref name="outBmp"/> (sized to the popup client =
    /// padded − 2·margin), so the LIVE flyout re-warp reuses one buffer every tick instead of allocating an LOH bitmap (which
    /// would churn Gen2 GC → the very stutter we're avoiding). Same pixels as <see cref="Refract"/>.</summary>
    public static void RefractInto(Bitmap paddedFrost, Bitmap outBmp, int margin, int supersample = 2, float magScale = 1f)
    {
        int pw = paddedFrost.Width, ph = paddedFrost.Height;
        int w = pw - 2 * margin, h = ph - 2 * margin;   // the popup client size
        if (w < 8 || h < 8) { using (var g = Graphics.FromImage(outBmp)) g.DrawImage(paddedFrost, new Rectangle(0, 0, outBmp.Width, outBmp.Height), margin, margin, outBmp.Width, outBmp.Height, GraphicsUnit.Pixel); return; }

        float bezel = Math.Clamp(Math.Min(w, h) * 0.15f, 18f, 44f);     // the refraction band — wider so the space CURVES into the panel (Erik), not a thin rim
        const float ca = 0.10f;                                         // chromatic aberration — measured recreations of Apple's effect converge on a ~2-3px max R-B fringe (≈ mag·2·ca); the old 0.14 at 37px displacement was a ~10px prism gimmick
        // Peak displacement ~16-18px: every measured recreation of Apple's lensing clusters at ~10-25px; the old
        // bezel*0.85 (~31-37px) read as a funhouse warp. magScale animates the "liquid settle"/press flex (overshoot →
        // damped wobble → 1); the margin cap always wins so even the strongest wobble frame samples inside the padding.
        float mag = Math.Min(Math.Min(bezel * 0.45f, 18f) * Math.Max(0.1f, magScale), (margin - 1) / (1f + ca));
        float mR = mag * (1f - ca), mG = mag, mB = mag * (1f + ca);
        var lut = RefractLut(); int LN = lut.Length;

        var sd = paddedFrost.LockBits(new Rectangle(0, 0, pw, ph), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var dd = outBmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            unsafe
            {
                byte* sp = (byte*)sd.Scan0; int ss = sd.Stride;
                byte* dp0 = (byte*)dd.Scan0; int ds = dd.Stride;
                int SS = Math.Clamp(supersample, 1, 3); float inv = 1f / (SS * SS);   // N×N supersample of the refracted rim → anti-aliased bent edges (1× for cheap live re-warps)
                for (int y = 0; y < h; y++)
                {
                    byte* drow = dp0 + y * ds;
                    float gTc = Prof(lut, LN, y + 0.5f, bezel), gBc = Prof(lut, LN, h - (y + 0.5f), bezel);   // strength at the pixel CENTRE (centre/shade test)
                    for (int x = 0; x < w; x++)
                    {
                        int* dpix = (int*)(drow + x * 4);
                        float gLc = Prof(lut, LN, x + 0.5f, bezel), gRc = Prof(lut, LN, w - (x + 0.5f), bezel);
                        if (gTc == 0f && gBc == 0f && gLc == 0f && gRc == 0f)   // flat centre → straight CRISP copy (no AA → stays sharp)
                        { *dpix = *(int*)(sp + (int)(y + 0.5f + margin) * ss + (int)(x + 0.5f + margin) * 4); continue; }

                        // 2×2 SUPERSAMPLE the refracted rim — the content COMPRESSES at the rim (grid lines bunch up), which point/bilinear
                        // sampling aliases (moiré). Evaluate the warp at 4 sub-positions and average → smooth bent edges. Centre stays crisp (above).
                        float ar = 0f, ag = 0f, ab = 0f;
                        for (int syi = 0; syi < SS; syi++)
                            for (int sxi = 0; sxi < SS; sxi++)
                            {
                                float ox = x + (sxi + 0.5f) / SS, oy = y + (syi + 0.5f) / SS;
                                float gT = Prof(lut, LN, oy, bezel), gB = Prof(lut, LN, h - oy, bezel);
                                float gL = Prof(lut, LN, ox, bezel), gR = Prof(lut, LN, w - ox, bezel);
                                float sxc = ox + margin, syc = oy + margin;
                                // INWARD displacement: near the left rim sample toward the panel INTERIOR — a convex lens keeps
                                // refracted rays inside the shape and MAGNIFIES the content under the rim (Apple's direction; our
                                // own Snell LUT derives this sign — the old code flipped it outward, the concave/peephole look).
                                float fx = gL - gR, fy = gT - gB;
                                int sR = SampleBilinear(sp, ss, pw, ph, sxc + fx * mR, syc + fy * mR);   // chromatic aberration:
                                int sG = SampleBilinear(sp, ss, pw, ph, sxc + fx * mG, syc + fy * mG);   //   each channel refracts
                                int sB = SampleBilinear(sp, ss, pw, ph, sxc + fx * mB, syc + fy * mB);   //   by a different amount
                                ar += (sR >> 16) & 0xFF; ag += (sG >> 8) & 0xFF; ab += sB & 0xFF;
                            }
                        int rr = (int)(ar * inv), gg = (int)(ag * inv), bb = (int)(ab * inv);

                        // NO specular glow — the rim REFRACTS light, it doesn't light up (Erik). DIRECTIONAL rim shade instead of
                        // the old symmetric ring (which read as a vignette): light-from-top matching the top glint — depth pools at
                        // the BOTTOM inner edge (~0.20 peak), sides ~0.10, top barely 0.05 so it doesn't fight the specular hairline.
                        float sS = Math.Max(gLc, gRc);
                        float s = gBc * gBc * gBc * 0.20f + sS * sS * sS * 0.10f + gTc * gTc * gTc * 0.05f;
                        if (s > 0.24f) s = 0.24f;
                        if (s > 0.004f) { rr = (int)(rr * (1 - s)); gg = (int)(gg * (1 - s)); bb = (int)(bb * (1 - s)); }
                        *dpix = (0xFF << 24) | (rr << 16) | (gg << 8) | bb;
                    }
                }
            }
        }
        finally { paddedFrost.UnlockBits(sd); outBmp.UnlockBits(dd); }
    }

    /// <summary>Cheap one-axis "liquid lip" for the now-playing bar's frost: bends the content at the TOP edge of a small
    /// downsampled frost slice (vertical refraction only) using the same squircle-Snell profile, so the song list appears
    /// to curve as it flows under the glass. Operates IN PLACE on the tiny slice (sub-ms, no allocation) — the bar then
    /// stretches the result, which amplifies the small bend into a soft lens lip. Sampling is strictly DOWNWARD (source row
    /// ≥ output row) so a top-to-bottom in-place pass never reads an already-rewritten row.</summary>
    public static void TopLip(Bitmap tiny, float strength = 0.55f)
    {
        int w = tiny.Width, h = tiny.Height;
        if (h < 6 || w < 2) return;
        float bezel = h * 0.42f;            // the lip occupies the top ~40% of the (short) slice — kept short so the bar's top blur isn't "long" (Erik); below it the list is flat
        var lut = RefractLut(); int LN = lut.Length;
        float mag = bezel * strength;
        var d = tiny.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        try
        {
            unsafe
            {
                byte* p = (byte*)d.Scan0; int s = d.Stride;
                for (int y = 0; y < h; y++)
                {
                    float prof = Prof(lut, LN, y + 0.5f, bezel);    // 1 at the very top → 0 at the bezel depth
                    if (prof <= 0.003f) continue;                   // below the lip → row unchanged
                    float sy = (y + 0.5f) + mag * prof - 0.5f;      // sample DOWNWARD → magnifies/bends the list at the top edge
                    int y0 = (int)Math.Floor(sy); float yf = sy - y0;
                    int y1 = Math.Clamp(y0 + 1, 0, h - 1); y0 = Math.Clamp(y0, 0, h - 1);
                    int* r0 = (int*)(p + y0 * s); int* r1 = (int*)(p + y1 * s); int* ro = (int*)(p + y * s);
                    for (int x = 0; x < w; x++)
                    {
                        int a = r0[x], b = r1[x]; int o = unchecked((int)0xFF000000);
                        for (int sh2 = 0; sh2 < 24; sh2 += 8)
                        {
                            float v = ((a >> sh2) & 0xFF) * (1 - yf) + ((b >> sh2) & 0xFF) * yf;
                            o |= ((int)(v + 0.5f) & 0xFF) << sh2;
                        }
                        ro[x] = o;
                    }
                }
            }
        }
        finally { tiny.UnlockBits(d); }
    }

    // Refraction strength 0..1 at depth `d` into the panel: the precomputed squircle profile, 0 past the bezel band.
    private static float Prof(float[] lut, int n, float d, float bezel)
    {
        if (d <= 0f) return lut[0];
        if (d >= bezel) return 0f;
        int idx = (int)(d / bezel * (n - 1));
        return lut[idx < 0 ? 0 : (idx >= n ? n - 1 : idx)];
    }

    // Convex SQUIRCLE glass-thickness profile (Apple's preferred curve): y = ⁴√(1 - (1-p)⁴). 0 at the rim → 1 inner.
    private static float SquircleH(float p)
    {
        p = Math.Clamp(p, 0f, 1f);
        float u = 1f - p, u4 = u * u * u * u;
        return (float)Math.Pow(Math.Max(0f, 1f - u4), 0.25);
    }

    // Build (once) the refraction-displacement profile of a convex squircle bezel via Snell's law (air n=1 → glass n=1.5):
    // numerically differentiate the height to get the surface slope/normal, refract a straight-down ray, and take the
    // refracted ray's horizontal component as the displacement. Normalized so the peak (at the steep rim) = 1.
    private static float[]? _refractLut;
    private static float[] RefractLut()
    {
        var cached = _refractLut;
        if (cached != null) return cached;
        const int N = 128; const float eta = 1f / 1.5f, thick = 1.7f;
        var lut = new float[N];
        float maxv = 1e-6f;
        for (int i = 0; i < N; i++)
        {
            float p = i / (float)(N - 1);                 // 0 at the rim → 1 at the inner bezel edge
            float dd2 = 0.5f / N;
            float slope = (SquircleH(p + dd2) - SquircleH(p - dd2)) / (2f * dd2) * thick;
            float nl = (float)Math.Sqrt(slope * slope + 1f);
            float nx = -slope / nl, ny = 1f / nl;          // surface normal (rotated −90° from the slope)
            float cosi = ny;                                // incident ray (0,1) · N
            float k = 1f - eta * eta * (1f - cosi * cosi);
            float rx = k < 0f ? 0f : -(eta * cosi + (float)Math.Sqrt(k)) * nx;   // refracted ray horizontal component
            lut[i] = Math.Abs(rx);
            if (lut[i] > maxv) maxv = lut[i];
        }
        for (int i = 0; i < N; i++) lut[i] /= maxv;
        _refractLut = lut;
        return lut;
    }

    // Bilinear-sample a locked 32bpp surface (B,G,R channels; alpha forced opaque), clamped to bounds.
    private static unsafe int SampleBilinear(byte* src, int stride, int w, int h, float fx, float fy)
    {
        fx -= 0.5f; fy -= 0.5f;
        int x0 = (int)Math.Floor(fx), y0 = (int)Math.Floor(fy);
        float xf = fx - x0, yf = fy - y0;
        int x1 = Math.Clamp(x0 + 1, 0, w - 1), y1 = Math.Clamp(y0 + 1, 0, h - 1);
        x0 = Math.Clamp(x0, 0, w - 1); y0 = Math.Clamp(y0, 0, h - 1);
        int* r0 = (int*)(src + y0 * stride); int* r1 = (int*)(src + y1 * stride);
        int tl = r0[x0], tr = r0[x1], bl = r1[x0], br = r1[x1];
        int o = unchecked((int)0xFF000000);
        for (int shft = 0; shft < 24; shft += 8)
        {
            float top = ((tl >> shft) & 0xFF) * (1 - xf) + ((tr >> shft) & 0xFF) * xf;
            float bot = ((bl >> shft) & 0xFF) * (1 - xf) + ((br >> shft) & 0xFF) * xf;
            int v = (int)(top * (1 - yf) + bot * yf + 0.5f);
            o |= (v & 0xFF) << shft;
        }
        return o;
    }

    /// <summary>Paint control <paramref name="c"/>'s slice of its glass host's frost plus <paramref name="tint"/> as
    /// its background. Returns false when there is no glass host above it — the caller then does its opaque fill, so
    /// every control keeps its original look on non-glass windows (Settings, etc.).</summary>
    public static bool PaintBackground(Graphics g, Control c, Color tint)
    {
        var (host, frost) = FindHost(c);
        if (host is null || frost is null) return false;
        tint = ((IGlassHost)host).GlassTint;   // the host decides the tint (dialogs frost harder than flyouts)
        try
        {
            Point onScreen = c.PointToScreen(Point.Empty);
            Point hostClient = host.PointToClient(onScreen);
            var src = new Rectangle(hostClient.X, hostClient.Y, c.Width, c.Height);
            // Blit the 1:1 slice with smoothing + interpolation OFF. The caller (e.g. CardPanel) may have set
            // SmoothingMode.AntiAlias, and an AA'd DrawImage blends the slice's EDGE column with the buffer → a bright
            // 1px fringe at every control boundary (the line down the card's left edge). NearestNeighbor + None = an
            // exact pixel copy with no edge blending; the frost is already blurred so it looks identical.
            var sm = g.SmoothingMode; var im = g.InterpolationMode; var po = g.PixelOffsetMode;
            g.SmoothingMode = SmoothingMode.None;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(frost, new Rectangle(0, 0, c.Width, c.Height), src, GraphicsUnit.Pixel);
            if (_tintBrush is null || _tintColor != tint) { _tintBrush?.Dispose(); _tintBrush = new SolidBrush(tint); _tintColor = tint; }   // cached — the live flyout repaints every glass child ~12fps
            g.FillRectangle(_tintBrush, 0, 0, c.Width, c.Height);
            g.SmoothingMode = sm; g.InterpolationMode = im; g.PixelOffsetMode = po;
            return true;
        }
        catch { return false; }
    }

    /// <summary>The nearest ancestor (or the control itself) that is a glass host with a live frost bitmap.
    /// Returns the host as a Control (for coordinate mapping) plus its frost.</summary>
    private static (Control?, Bitmap?) FindHost(Control c)
    {
        for (Control? p = c; p is not null; p = p.Parent)
            if (p is IGlassHost h && h.GlassFrost is { } f) return (p, f);
        return (null, null);
    }
}

/// <summary>A modal-dialog base that paints a frosted-glass backdrop (the parent window behind it, blurred). The
/// backdrop is captured ONCE when the dialog is shown (the content behind a modal is static), and the dialog's
/// glass-aware child controls slice their own piece of it. Falls back to the normal opaque background when there's
/// no owner to capture from. Derive a dialog from this instead of Form to make it glass.</summary>
internal class GlassDialog : Form, IGlassHost
{
    private Bitmap? _frost;
    private Bitmap? _ownerSnap;   // one-shot snapshot of the owner behind us; re-sliced on drag so the glass reacts
    public Bitmap? GlassFrost => _frost;
    public Color GlassTint => Glass.DialogTint;   // dialogs frost harder than flyouts
    // Modal dialogs stay OPAQUE: the glass there shows the app behind (not the desktop), which read as a weird
    // reflection on big windows, and true desktop-acrylic isn't possible behind opaque GDI controls. The plumbing
    // stays (dark title bar etc.) but the backdrop capture is off; controls fall back to their normal opaque fill.
    protected bool GlassEnabled = false;   // explicit: dialogs deliberately stay opaque (see above) — the initializer also silences CS0649
    private bool _hookedMove;

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try { int dark = 1; DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int)); } catch { }   // dark title bar
        try { int round = 2; DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int)); } catch { }  // DWMWCP_ROUND — rounded corners (needed once a dialog goes borderless)
        // Colour the title bar to match the dark glass body so the default "grey bar" blends into the app
        // (DWMWA_CAPTION_COLOR, Win11). The caption stays draggable + keeps its close button — it just isn't a slab.
        try { var c = Theme.Bg; int cap = (c.B << 16) | (c.G << 8) | c.R; DwmSetWindowAttribute(Handle, 35, ref cap, sizeof(int)); } catch { }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (!GlassEnabled) return;
        _ownerSnap?.Dispose();
        _ownerSnap = Glass.CaptureOwner(Owner);
        RebuildFrost();
        if (!_hookedMove) { _hookedMove = true; LocationChanged += (_, _) => RebuildFrost(); }   // drag → re-slice the backdrop
    }

    private void RebuildFrost()
    {
        if (!GlassEnabled || _ownerSnap is null) return;
        var old = _frost;
        _frost = Glass.FrostFrom(_ownerSnap, Owner, this);
        old?.Dispose();
        if (_frost is not null && !IsDisposed) Invalidate(true);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        if (_frost is null) { base.OnPaintBackground(e); return; }
        var g = e.Graphics;
        var im = g.InterpolationMode; var sm = g.SmoothingMode;
        g.InterpolationMode = InterpolationMode.NearestNeighbor; g.SmoothingMode = SmoothingMode.None;   // exact copy, no edge fringe
        g.DrawImage(_frost, new Rectangle(0, 0, ClientSize.Width, ClientSize.Height));
        using (var tint = new SolidBrush(GlassTint)) g.FillRectangle(tint, 0, 0, ClientSize.Width, ClientSize.Height);
        g.InterpolationMode = im; g.SmoothingMode = sm;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _frost?.Dispose(); _frost = null; _ownerSnap?.Dispose(); _ownerSnap = null; }
        base.Dispose(disposing);
    }
}

/// <summary>A panel that paints itself as frosted glass inside an <see cref="IGlassHost"/>, else exactly like a
/// normal opaque panel — a drop-in replacement for the plain background/viewport Panels in a glass dialog.</summary>
internal sealed class GlassPanel : Panel
{
    public GlassPanel()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnPaintBackground(PaintEventArgs e) { /* full background painted in OnPaint */ }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        if (Glass.PaintBackground(g, this, Glass.SurfaceTint)) return;
        Color bg = BackColor.A == 0 ? (Parent?.BackColor ?? Theme.Bg) : BackColor;
        using var b = new SolidBrush(bg);
        g.FillRectangle(b, ClientRectangle);
    }
}

/// <summary>A label that paints itself as frosted glass when it sits inside an <see cref="IGlassHost"/>, and
/// exactly like a normal opaque label otherwise — so it is a drop-in replacement that only changes behaviour on
/// glass popups.</summary>
internal sealed class GlassLabel : Label
{
    public GlassLabel() { DoubleBuffered = true; }

    protected override void OnPaintBackground(PaintEventArgs e) { /* the full background is painted in OnPaint */ }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        if (!Glass.PaintBackground(g, this, Glass.SurfaceTint))
        {
            Color bg = BackColor.A == 0 ? (Parent?.BackColor ?? Theme.PanelBg) : BackColor;
            using var b = new SolidBrush(bg);
            g.FillRectangle(b, ClientRectangle);
        }
        TextRenderer.DrawText(g, Text, Font, ClientRectangle, ForeColor, AlignFlags(TextAlign));
    }

    private static TextFormatFlags AlignFlags(ContentAlignment a) => a switch
    {
        ContentAlignment.MiddleLeft => TextFormatFlags.Left | TextFormatFlags.VerticalCenter,
        ContentAlignment.MiddleCenter => TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter,
        ContentAlignment.MiddleRight => TextFormatFlags.Right | TextFormatFlags.VerticalCenter,
        ContentAlignment.TopLeft => TextFormatFlags.Left | TextFormatFlags.Top,
        ContentAlignment.TopCenter => TextFormatFlags.HorizontalCenter | TextFormatFlags.Top,
        ContentAlignment.TopRight => TextFormatFlags.Right | TextFormatFlags.Top,
        ContentAlignment.BottomLeft => TextFormatFlags.Left | TextFormatFlags.Bottom,
        _ => TextFormatFlags.Left | TextFormatFlags.VerticalCenter,
    } | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix;   // wrap like a normal Label (the description rows are multi-line)
}
