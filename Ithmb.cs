using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace iPodCommander;

/// <summary>
/// Packs/unpacks the raw pixel slots inside an iPod <c>.ithmb</c> thumbnail file. Each photo is
/// scaled to fit its format's fixed slot (keeping aspect), centred, and the border filled with
/// black; the slot is then written as little-endian RGB565, row-major top-to-bottom. This matches
/// libgpod's <c>pack_RGB_565</c> exactly (RRRRR GGGGGG BBBBB, low byte first, no row alignment and
/// no per-image padding for the 5G/Classic photo formats).
/// </summary>
internal static class Ithmb
{
    /// <summary>The result of rendering one photo into one format's slot.</summary>
    public readonly record struct Slot(byte[] Pixels, int ImageWidth, int ImageHeight, int HPad, int VPad);

    /// <summary>Render <paramref name="src"/> into <paramref name="fmt"/>'s fixed slot as RGB565-LE.</summary>
    public static Slot Encode(Image src, PhotoFormat fmt)
    {
        double scale = Math.Min((double)fmt.Width / src.Width, (double)fmt.Height / src.Height);
        int w = Math.Clamp((int)Math.Round(src.Width * scale), 1, fmt.Width);
        int h = Math.Clamp((int)Math.Round(src.Height * scale), 1, fmt.Height);
        int hPad = (fmt.Width - w) / 2;
        int vPad = (fmt.Height - h) / 2;

        using var slot = new Bitmap(fmt.Width, fmt.Height, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(slot))
        {
            g.Clear(Color.Black);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(src, new Rectangle(hPad, vPad, w, h));
        }

        byte[] px = Rgb565FromBitmap(slot);
        return new Slot(px, hPad + w, vPad + h, hPad, vPad);
    }

    private static byte[] Rgb565FromBitmap(Bitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        var outBytes = new byte[w * h * 2];
        var rect = new Rectangle(0, 0, w, h);
        BitmapData data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            unsafe
            {
                byte* basep = (byte*)data.Scan0;
                int o = 0;
                for (int y = 0; y < h; y++)
                {
                    byte* row = basep + y * data.Stride;
                    for (int x = 0; x < w; x++)
                    {
                        byte b = row[x * 3 + 0], gr = row[x * 3 + 1], r = row[x * 3 + 2]; // 24bpp = BGR
                        ushort p = (ushort)(((r >> 3) << 11) | ((gr >> 2) << 5) | (b >> 3));
                        outBytes[o++] = (byte)(p & 0xFF);        // little-endian: low byte first
                        outBytes[o++] = (byte)((p >> 8) & 0xFF);
                    }
                }
            }
        }
        finally { bmp.UnlockBits(data); }
        return outBytes;
    }

    /// <summary>Decode a stored RGB565-LE slot back into a Bitmap for the on-screen grid.</summary>
    public static Bitmap? Decode(byte[] pixels, int w, int h)
    {
        if (w <= 0 || h <= 0 || pixels.Length < w * h * 2) return null;
        var bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb);
        var rect = new Rectangle(0, 0, w, h);
        BitmapData data = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
        try
        {
            unsafe
            {
                byte* basep = (byte*)data.Scan0;
                int o = 0;
                for (int y = 0; y < h; y++)
                {
                    byte* row = basep + y * data.Stride;
                    for (int x = 0; x < w; x++)
                    {
                        ushort p = (ushort)(pixels[o] | (pixels[o + 1] << 8)); o += 2;
                        int r = (p >> 11) & 0x1F, gr = (p >> 5) & 0x3F, b = p & 0x1F;
                        row[x * 3 + 0] = (byte)((b << 3) | (b >> 2));
                        row[x * 3 + 1] = (byte)((gr << 2) | (gr >> 4));
                        row[x * 3 + 2] = (byte)((r << 3) | (r >> 2));
                    }
                }
            }
        }
        finally { bmp.UnlockBits(data); }
        return bmp;
    }
}
