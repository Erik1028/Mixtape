using System.Drawing.Imaging;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace iPodCommander;

/// <summary>
/// The Windows 95 look's pictures: a cover the way a 256-colour display of 1995 showed it. Windows drew
/// photographs through its halftone palette - a 6 x 6 x 6 colour cube - and dithered the colours it did not have
/// with an ordered pattern; that crosshatch in every gradient IS the look. The twenty colours Windows kept for
/// itself (the VGA sixteen and four more) pass through untouched, so a grey face or a white page inside a picture
/// never turns speckled.
/// <para>It works at DRAW time on a copy scaled to the exact size the picture is shown at - so the pattern stays
/// pixel-true at any grid width, and nothing dithered ever reaches the cover caches, the iPod or a file.</para>
/// </summary>
internal static class Halftone
{
    // The 8 x 8 Bayer matrix: the ordered-dither pattern of the era's halftone blits.
    private static readonly int[] Bayer =
    {
         0, 32,  8, 40,  2, 34, 10, 42,
        48, 16, 56, 24, 50, 18, 58, 26,
        12, 44,  4, 36, 14, 46,  6, 38,
        60, 28, 52, 20, 62, 30, 54, 22,
         3, 35, 11, 43,  1, 33,  9, 41,
        51, 19, 59, 27, 49, 17, 57, 25,
        15, 47,  7, 39, 13, 45,  5, 37,
        63, 31, 55, 23, 61, 29, 53, 21,
    };

    // The static colours Windows reserved in a 256-colour palette: the VGA sixteen, and money green, sky blue,
    // cream and medium grey.
    private static readonly HashSet<int> Reserved = new()
    {
        0x000000, 0x800000, 0x008000, 0x808000, 0x000080, 0x800080, 0x008080, 0xC0C0C0,
        0x808080, 0xFF0000, 0x00FF00, 0xFFFF00, 0x0000FF, 0xFF00FF, 0x00FFFF, 0xFFFFFF,
        0xC0DCC0, 0xA6CAF0, 0xFFFBF0, 0xA0A0A4,
    };

    /// <summary>Dither a 32 bpp bitmap in place onto the halftone palette.</summary>
    public static void Apply(Bitmap bmp)
    {
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        try
        {
            int n = data.Stride / 4 * bmp.Height;
            var px = new int[n];
            Marshal.Copy(data.Scan0, px, 0, n);
            int stride = data.Stride / 4;
            for (int y = 0; y < bmp.Height; y++)
            {
                int row = y * stride, by = (y & 7) * 8;
                for (int x = 0; x < bmp.Width; x++)
                {
                    int c = px[row + x];
                    int a = (c >> 24) & 0xFF;
                    if (a == 0 || Reserved.Contains(c & 0xFFFFFF)) continue;
                    // the threshold: -0.5 .. +0.5 of one cube step (51), per the pattern
                    float t = ((Bayer[by + (x & 7)] + 0.5f) / 64f - 0.5f) * 51f;
                    int r = Level(((c >> 16) & 0xFF) + t), gg = Level(((c >> 8) & 0xFF) + t), b = Level((c & 0xFF) + t);
                    px[row + x] = (a << 24) | (r << 16) | (gg << 8) | b;
                }
            }
            Marshal.Copy(px, 0, data.Scan0, n);
        }
        finally { bmp.UnlockBits(data); }
    }

    private static int Level(float v) => Math.Clamp((int)Math.Round(v / 51f), 0, 5) * 51;

    // One halftoned copy per picture and size, dropped with the picture (weak keys) and trimmed when a window
    // resize leaves a picture with several sizes it will not be shown at again.
    private static readonly ConditionalWeakTable<Image, Dictionary<Size, Bitmap>> Cache = new();

    /// <summary><paramref name="art"/> as a 1995 display showed it at <paramref name="size"/>: scaled to exactly that
    /// size, then dithered. Cached per picture and size; the caller draws it unscaled.</summary>
    public static Bitmap For(Image art, Size size)
    {
        var bySize = Cache.GetOrCreateValue(art);
        lock (bySize)
        {
            if (bySize.TryGetValue(size, out var hit)) return hit;
            if (bySize.Count >= 3) { foreach (var b in bySize.Values) b.Dispose(); bySize.Clear(); }
            var bmp = new Bitmap(Math.Max(1, size.Width), Math.Max(1, size.Height), PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                using var ia = new ImageAttributes();
                ia.SetWrapMode(System.Drawing.Drawing2D.WrapMode.TileFlipXY);   // no half-transparent fringe at the edges
                g.DrawImage(art, new Rectangle(0, 0, bmp.Width, bmp.Height), 0, 0, art.Width, art.Height, GraphicsUnit.Pixel, ia);
            }
            Apply(bmp);
            bySize[size] = bmp;
            return bmp;
        }
    }
}
