using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace iPodCommander;

/// <summary>
/// Photos onto an Encore OS iPod: plain files in the folder Encore reads (<see cref="EncoreFolders.PhotoRoot"/>), one
/// subfolder per album. Encore lists them newest first by the date in the file name (IMG_20261023_142530, PXL_…,
/// Screenshot_2026-10-23-14-25-30…), else by the file's FAT time - so names stay as they are, and the copy's time is
/// the moment the photo was taken (EXIF DateTimeOriginal), else the original's own time.
///
/// What the iPod can't read well is made smaller on the way (Encore's src/ui/image.c with build-ipod.sh's limits):
/// a file over 8 MB isn't read at all, a PNG or progressive JPEG is decoded whole and only up to 3 megapixels, and a
/// progressive JPEG takes a blurry shortcut. So a photo longer than 2048 px, a progressive JPEG, a too-big PNG or
/// another format (BMP, GIF, TIFF) is drawn again as a baseline JPEG (PNG stays PNG) - upright, as the EXIF said.
/// Anything else is copied byte for byte, EXIF and all (Encore turns photos by their EXIF itself).
/// (Apple's own photo library - PhotoLibrary.cs - is untouched: that is the original iPod software's.)
/// </summary>
internal static class EncorePhotos
{
    public const int MaxSide = 2048;
    public const long MaxFile = 8 * 1024 * 1024;       // build-ipod.sh MAX_FILE
    public const long MaxWholePixels = 3_000_000;      // build-ipod.sh IMAGE_MAX_PIXELS: PNG / progressive JPEG, decoded whole
    public static readonly string[] Readable = { ".jpg", ".jpeg", ".png" };
    public static readonly string[] Images = { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff", ".webp" };

    public enum Outcome { Copied, Redrawn, AlreadyThere }

    public sealed class Report
    {
        public int Copied, Redrawn, AlreadyThere;
        public readonly List<string> Errors = new();
        public int Added => Copied + Redrawn;
    }

    /// <summary>A JPEG whose frame is progressive (SOF2/6/10/14) - read off the markers, without decoding.</summary>
    public static bool IsProgressive(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            Span<byte> b = stackalloc byte[4];
            if (fs.Read(b[..2]) != 2 || b[0] != 0xFF || b[1] != 0xD8) return false;
            while (fs.Read(b[..2]) == 2)
            {
                if (b[0] != 0xFF) return false;
                byte m = b[1];
                if (m == 0xFF) { fs.Position--; continue; }                     // fill byte
                if (m is >= 0xD0 and <= 0xD9 or 0x01) continue;                 // markers without a length
                if (m is 0xC2 or 0xC6 or 0xCA or 0xCE) return true;
                if (m is >= 0xC0 and <= 0xCF and not (0xC4 or 0xC8 or 0xCC)) return false;   // the frame is baseline/extended
                if (m == 0xDA) return false;                                    // scan data: no frame header met
                if (fs.Read(b[..2]) != 2) return false;
                fs.Position += ((b[0] << 8) | b[1]) - 2;
            }
        }
        catch { }
        return false;
    }

    private static string? Ascii(Image img, int id)
    {
        try
        {
            if (Array.IndexOf(img.PropertyIdList, id) < 0) return null;
            var v = img.GetPropertyItem(id)?.Value;
            return v is null ? null : System.Text.Encoding.ASCII.GetString(v).TrimEnd('\0', ' ');
        }
        catch { return null; }
    }

    /// <summary>When the photo was taken: EXIF DateTimeOriginal ("2026:10:23 14:25:30"), else null.</summary>
    public static DateTime? Taken(Image img)
    {
        foreach (int id in new[] { 0x9003, 0x9004 })
            if (Ascii(img, id) is { Length: >= 19 } s && DateTime.TryParseExact(s[..19], "yyyy:MM:dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d)
                && d.Year > 1980) return d;
        return null;
    }

    private static int Orientation(Image img)
    {
        try { return Array.IndexOf(img.PropertyIdList, 0x0112) >= 0 && img.GetPropertyItem(0x0112)?.Value is { Length: >= 2 } v ? BitConverter.ToUInt16(v, 0) : 1; }
        catch { return 1; }
    }

    // ---- the file's time, as Encore reads it ----------------------------------------------------------------

    /// <summary>A FAT volume (the iPod's) keeps local time, and Windows turns the UTC it is given into local time with
    /// the zone's offset of TODAY - a summer photo dated in winter would land an hour off. So on FAT the UTC handed over
    /// is the wanted local time less today's offset; anywhere else the usual conversion is already right.</summary>
    private static bool IsFat(string path)
    {
        try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).DriveFormat.StartsWith("FAT", StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    public static void SetTime(string path, DateTime local)
    {
        if (IsFat(path)) File.SetLastWriteTimeUtc(path, DateTime.SpecifyKind(local - TimeZoneInfo.Local.GetUtcOffset(DateTime.Now), DateTimeKind.Utc));
        else File.SetLastWriteTime(path, DateTime.SpecifyKind(local, DateTimeKind.Local));
    }

    public static DateTime GetTime(string path) =>
        IsFat(path) ? File.GetLastWriteTimeUtc(path) + TimeZoneInfo.Local.GetUtcOffset(DateTime.Now) : File.GetLastWriteTime(path);

    // ---- adding ---------------------------------------------------------------------------------------------

    /// <summary>Put one photo into <paramref name="album"/> ("" = straight into the photo folder, Encore's "Photos";
    /// "Summer/Balaton" = a nested album). The same photo added again is recognised by name and time (or size) and left.</summary>
    public static Outcome Add(string mountRoot, string src, string album)
    {
        string ext = Path.GetExtension(src).ToLowerInvariant();
        if (Array.IndexOf(Images, ext) < 0) throw new InvalidDataException(Loc.T("not a picture Mixtape can read"));
        string root = EncoreFolders.PhotoRoot(mountRoot);
        string dir = root;
        foreach (string part in album.Split('/', '\\', StringSplitOptions.RemoveEmptyEntries)) dir = Path.Combine(dir, EncoreFolders.SafeName(part, "Album"));

        DateTime when;
        int w, h, orient;
        bool redraw;
        using (var fs = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var img = Open(fs))   // header only: no pixels yet
        {
            when = Taken(img) ?? File.GetLastWriteTime(src);
            w = img.Width; h = img.Height; orient = Orientation(img);
            bool png = ext == ".png";
            redraw = Array.IndexOf(Readable, ext) < 0 || new FileInfo(src).Length > MaxFile || Math.Max(w, h) > MaxSide
                  || (png && (long)w * h > MaxWholePixels) || (!png && IsProgressive(src));
        }

        string name = Path.GetFileNameWithoutExtension(src);
        string outExt = !redraw ? Path.GetExtension(src) : ext == ".png" ? ".png" : ext is ".jpg" or ".jpeg" ? Path.GetExtension(src) : ".jpg";
        Directory.CreateDirectory(dir);
        string dst = Path.Combine(dir, name + outExt);
        for (int k = 2; File.Exists(dst); k++)
        {
            if (Same(dst, src, when, redraw)) return Outcome.AlreadyThere;
            dst = Path.Combine(dir, $"{name} ({k}){outExt}");
        }

        string part2 = dst + ".part";   // not a picture's name: Encore never lists a half-written one
        try
        {
            if (redraw) Redraw(src, part2, outExt == ".png", orient, w, h);
            else File.Copy(src, part2, overwrite: true);
            File.Move(part2, dst);
        }
        catch { try { File.Delete(part2); } catch { } throw; }
        SetTime(dst, when);
        return redraw ? Outcome.Redrawn : Outcome.Copied;
    }

    /// <summary>GDI+ says "Parameter is not valid" for a file it can't decode (a WebP without its codec, a damaged one).</summary>
    private static Image Open(Stream s)
    {
        try { return Image.FromStream(s, useEmbeddedColorManagement: false, validateImageData: false); }
        catch (ArgumentException) { throw new InvalidDataException(Loc.T("not a picture Mixtape can read")); }
    }

    /// <summary>The file already in the album is this photo: the same time (FAT keeps it to 2 s; a whole hour off is
    /// a clock change between two adds), or - copied byte for byte - the same size.</summary>
    private static bool Same(string existing, string src, DateTime when, bool redrawn)
    {
        try
        {
            double dt = Math.Abs((GetTime(existing) - when).TotalSeconds);
            if (dt <= 2 || Math.Abs(dt - 3600) <= 2) return true;
            return !redrawn && new FileInfo(existing).Length == new FileInfo(src).Length;
        }
        catch { return false; }
    }

    /// <summary>Draw the photo again: upright (its EXIF turn applied), at most <see cref="MaxSide"/> long (a PNG also at
    /// most <see cref="MaxWholePixels"/>), as a baseline JPEG (quality 90) or a PNG. The taking date goes along.</summary>
    private static void Redraw(string src, string dst, bool png, int orient, int w, int h)
    {
        using var fs = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var img = Image.FromStream(fs, useEmbeddedColorManagement: true, validateImageData: false);
        double s = Math.Min(1.0, MaxSide / (double)Math.Max(w, h));
        if (png && (long)w * h * s * s > MaxWholePixels) s = Math.Sqrt(MaxWholePixels / ((double)w * h)) * 0.999;
        int nw = Math.Max(1, (int)Math.Round(w * s)), nh = Math.Max(1, (int)Math.Round(h * s));
        using var bmp = new Bitmap(nw, nh, png ? PixelFormat.Format32bppArgb : PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(bmp))
        using (var ia = new ImageAttributes())
        {
            if (!png) g.Clear(Color.White);   // a transparent GIF/PNG drawn into a JPEG: on white, not black
            g.CompositingMode = png ? CompositingMode.SourceCopy : CompositingMode.SourceOver;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.SmoothingMode = SmoothingMode.HighQuality;
            ia.SetWrapMode(WrapMode.TileFlipXY);   // no dark fringe at the edges
            g.DrawImage(img, new Rectangle(0, 0, nw, nh), 0, 0, w, h, GraphicsUnit.Pixel, ia);
        }
        var turn = orient switch
        {
            2 => RotateFlipType.RotateNoneFlipX, 3 => RotateFlipType.Rotate180FlipNone, 4 => RotateFlipType.RotateNoneFlipY,
            5 => RotateFlipType.Rotate90FlipX, 6 => RotateFlipType.Rotate90FlipNone, 7 => RotateFlipType.Rotate270FlipX,
            8 => RotateFlipType.Rotate270FlipNone, _ => RotateFlipType.RotateNoneFlipNone,
        };
        if (turn != RotateFlipType.RotateNoneFlipNone) bmp.RotateFlip(turn);
        foreach (int id in new[] { 0x9003, 0x9004, 0x0132, 0x010F, 0x0110 })   // taken, digitised, changed, camera make and model
            try { if (Array.IndexOf(img.PropertyIdList, id) >= 0 && img.GetPropertyItem(id) is { } pi) bmp.SetPropertyItem(pi); } catch { }
        if (png) { bmp.Save(dst, ImageFormat.Png); return; }
        var jpeg = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        using var ep = new EncoderParameters(1);
        ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 90L);
        bmp.Save(dst, jpeg, ep);
    }

    /// <summary>How many photos and albums Encore will list (its rule: .jpg/.jpeg/.png, every folder an album, the
    /// photo folder's own files "Photos"). Up to 16 folders deep, like Encore.</summary>
    public static (int Photos, int Albums) Count(string mountRoot)
    {
        string root = EncoreFolders.PhotoRoot(mountRoot);
        int photos = 0;
        var albums = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!Directory.Exists(root)) return (0, 0);
            var opts = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 16, IgnoreInaccessible = true, AttributesToSkip = 0 };
            foreach (string f in Directory.EnumerateFiles(root, "*", opts))
            {
                if (Array.IndexOf(Readable, Path.GetExtension(f).ToLowerInvariant()) < 0) continue;
                photos++;
                albums.Add(Path.GetDirectoryName(f) ?? "");
            }
        }
        catch { }
        return (photos, albums.Count);
    }
}
