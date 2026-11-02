using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace iPodCommander;

/// <summary>
/// Encore OS's own video format (.evf), written the way Encore's own converter writes it (tools/evf/evfenc.c, which
/// matches the browser converter evfcore.js bit for bit - and so does this). Everything little-endian:
///   header (32 bytes): "EVF1", u16 width, u16 height, u32 fps×1000, u32 frames, u32 sample rate (0: silent),
///                      u8 channels, 3 zero bytes, u32 index offset, u32 index entries;
///   chunks: u8 'A'/'V', u8 flags (V: 1 = keyframe), u16 0, u32 size, then the payload -
///     'A': u32 sample frames, per channel (i16 predictor, u8 step index, u8 0), then IMA ADPCM (stereo: one byte per
///          sample pair, left in the low nibble; mono: two samples a byte);
///     'V': u32 frame number, then 4×4 blocks row by row in runs: 0x00-0x3F n+1 unchanged, 0x40-0x7F n+1 flat (RGB565
///          each), 0x80-0xBF n+1 two-colour (2+2 bytes colour, 2 bytes mask), 0xC0-0xFF n+1 four-colour (4×2 bytes,
///          4 bytes of 2-bit indices);
///   index: per keyframe u32 frame, u32 offset of its 'V' chunk, u32 offset of the 'A' chunk before it.
/// At most 320×240 with sides a multiple of 4, 44.1 kHz. A keyframe every 2 s is what the player seeks to, resumes at,
/// and takes the list's thumbnail from (the one nearest 10 %).
/// </summary>
internal sealed class EvfAudio
{
    internal static readonly short[] Step =
    {
        7, 8, 9, 10, 11, 12, 13, 14, 16, 17, 19, 21, 23, 25, 28, 31, 34, 37, 41, 45, 50, 55, 60, 66,
        73, 80, 88, 97, 107, 118, 130, 143, 157, 173, 190, 209, 230, 253, 279, 307, 337, 371, 408,
        449, 494, 544, 598, 658, 724, 796, 876, 963, 1060, 1166, 1282, 1411, 1552, 1707, 1878, 2066,
        2272, 2499, 2749, 3024, 3327, 3660, 4026, 4428, 4871, 5358, 5894, 6484, 7132, 7845, 8630,
        9493, 10442, 11487, 12635, 13899, 15289, 16818, 18500, 20350, 22385, 24623, 27086, 29794, 32767,
    };
    internal static readonly sbyte[] Idx = { -1, -1, -1, -1, 2, 4, 6, 8, -1, -1, -1, -1, 2, 4, 6, 8 };

    public readonly int Channels;
    private readonly double _fps;
    private readonly int[] _pred = new int[2], _index = new int[2];
    private const int ChunkBits = 20;                         // the codes in 1 MB pieces: an hour of stereo is ~160 MB
    private readonly List<byte[]> _store = new();
    private long _len;
    private readonly List<int> _marks = new();               // per frame: the coder's state where its chunk starts (4 ints)
    private long _nextMark;

    public EvfAudio(int channels, double fps) { Channels = channels == 1 ? 1 : 2; _fps = fps; }

    public long Samples => _len;
    private int MarkCount => _marks.Count / 4;
    private long Boundary(long f) => (long)Math.Floor(f * 44100.0 / _fps + 0.5);

    private int Code(int c, int s)
    {
        int step = Step[_index[c]], diff = s - _pred[c], code = 0;
        if (diff < 0) { code = 8; diff = -diff; }
        int delta = step >> 3;
        if (diff >= step) { code |= 4; diff -= step; delta += step; }
        step >>= 1;
        if (diff >= step) { code |= 2; diff -= step; delta += step; }
        step >>= 1;
        if (diff >= step) { code |= 1; delta += step; }
        int p = (code & 8) != 0 ? _pred[c] - delta : _pred[c] + delta;
        _pred[c] = p > 32767 ? 32767 : p < -32768 ? -32768 : p;
        int i = _index[c] + Idx[code];
        _index[c] = i < 0 ? 0 : i > 88 ? 88 : i;
        return code;
    }

    private void Put(byte b)
    {
        int at = (int)(_len & ((1 << ChunkBits) - 1));
        if (at == 0) _store.Add(new byte[1 << ChunkBits]);
        _store[^1][at] = b;
        _len++;
    }

    private byte At(long i) => _store[(int)(i >> ChunkBits)][i & ((1 << ChunkBits) - 1)];

    /// <summary>44.1 kHz samples, interleaved by channel (L,R,L,R… or L only).</summary>
    public void Write(ReadOnlySpan<short> pcm, int frames)
    {
        for (int i = 0; i < frames; i++)
        {
            while (_len == _nextMark)
            {
                _marks.Add(_pred[0]); _marks.Add(_index[0]); _marks.Add(_pred[1]); _marks.Add(_index[1]);
                _nextMark = Boundary(MarkCount);
            }
            if (Channels == 2)
            {
                int l = Code(0, pcm[2 * i]), r = Code(1, pcm[2 * i + 1]);
                Put((byte)(l | (r << 4)));
            }
            else Put((byte)Code(0, pcm[i]));
        }
    }

    /// <summary>Frame <paramref name="f"/>'s 'A' payload; null when it has no sound.</summary>
    public byte[]? Chunk(long f)
    {
        if (f >= MarkCount) return null;
        long s0 = Boundary(f), s1 = Math.Min(Boundary(f + 1), _len);
        if (s1 <= s0) return null;
        int n = (int)(s1 - s0), hdr = 4 + 4 * Channels;
        int body = Channels == 2 ? n : (n + 1) / 2;
        var o = new byte[hdr + body];
        int m = (int)f * 4;
        BinaryPrimitives.WriteInt32LittleEndian(o, n);
        o[4] = (byte)_marks[m]; o[5] = (byte)((uint)_marks[m] >> 8); o[6] = (byte)_marks[m + 1];
        if (Channels == 2) { o[8] = (byte)_marks[m + 2]; o[9] = (byte)((uint)_marks[m + 2] >> 8); o[10] = (byte)_marks[m + 3]; }
        if (Channels == 2) for (int i = 0; i < n; i++) o[hdr + i] = At(s0 + i);
        else for (int i = 0; i < n; i++) o[hdr + i / 2] |= (byte)((i & 1) != 0 ? At(s0 + i) << 4 : At(s0 + i));
        return o;
    }
}

/// <summary>One picture at a time into 4×4 blocks: for each block the cheapest of unchanged / flat / two / four colours
/// by error + λ·bytes, against what the iPod shows now (<see cref="Shown"/>). Blocks don't depend on each other, so the
/// rows are coded in parallel; the file is the same whichever way.</summary>
internal sealed class EvfFrameEncoder
{
    private static readonly byte[] Q5 = new byte[256], Q6 = new byte[256], E5 = new byte[32], E6 = new byte[64];
    static EvfFrameEncoder()
    {
        for (int v = 0; v < 256; v++) { Q5[v] = (byte)((2 * v * 31 + 255) / 510); Q6[v] = (byte)((2 * v * 63 + 255) / 510); }
        for (int v = 0; v < 32; v++) E5[v] = (byte)(v * 255 / 31);
        for (int v = 0; v < 64; v++) E6[v] = (byte)(v * 255 / 63);
    }

    public readonly int W, H;
    private readonly int _lambda;
    private readonly byte[] _rec;          // what the iPod shows now (RGB)
    private readonly byte[] _kind;
    private readonly ushort[] _data;
    private readonly byte[] _out;
    private readonly int _threads;

    private sealed class Scratch
    {
        public readonly int[] R = new int[16], G = new int[16], B = new int[16], Pal = new int[12], Proj = new int[16];
        public readonly byte[] Asg = new byte[16], Best = new byte[16], Order = new byte[16];
        public readonly ushort[] Pq = new ushort[4];
    }

    public EvfFrameEncoder(int w, int h, int lambda, int threads = 1)
    {
        W = w; H = h; _lambda = lambda;
        int nb = (w / 4) * (h / 4);
        _rec = new byte[w * h * 3];
        _kind = new byte[nb];
        _data = new ushort[nb * 6];
        _out = new byte[nb * 14 + 64];
        _threads = Math.Max(1, threads);
    }

    /// <summary>The picture the iPod shows after the last frame (RGB, W×H×3).</summary>
    public ReadOnlySpan<byte> Shown => _rec;

    private static int RoundDiv(int s, int n) => (2 * s + n) / (2 * n);

    /// <summary>k-means for k = 2 or 4 colours; returns the error. Leaves Pq, Pal and Best.</summary>
    private static int Cluster(Scratch x, int k)
    {
        int[] R = x.R, G = x.G, B = x.B, pal = x.Pal;
        ushort[] pq = x.Pq;
        byte[] asg = x.Asg, order = x.Order;
        int rmin = 255, rmax = 0, gmin = 255, gmax = 0, bmin = 255, bmax = 0;
        for (int p = 0; p < 16; p++)
        {
            if (R[p] < rmin) rmin = R[p];
            if (R[p] > rmax) rmax = R[p];
            if (G[p] < gmin) gmin = G[p];
            if (G[p] > gmax) gmax = G[p];
            if (B[p] < bmin) bmin = B[p];
            if (B[p] > bmax) bmax = B[p];
        }
        int rr = rmax - rmin, gr = gmax - gmin, br = bmax - bmin;
        int[] C = rr >= gr && rr >= br ? R : gr >= br ? G : B;
        for (int p = 0; p < 16; p++) { x.Proj[p] = C[p] * 1024 + R[p] * 2 + G[p] * 4 + B[p]; order[p] = (byte)p; }
        for (int i = 1; i < 16; i++)
        {
            byte o = order[i];
            int v = x.Proj[o], j = i - 1;
            while (j >= 0 && x.Proj[order[j]] > v) { order[j + 1] = order[j]; j--; }
            order[j + 1] = o;
        }
        int per = 16 / k;
        for (int i = 0; i < 16; i++) asg[order[i]] = (byte)(i / per);
        int err = 0;
        for (int it = 0; it < 3; it++)
        {
            for (int c = 0; c < k; c++)
            {
                int sr = 0, sg = 0, sb = 0, n = 0;
                for (int p = 0; p < 16; p++) if (asg[p] == c) { sr += R[p]; sg += G[p]; sb += B[p]; n++; }
                if (n != 0)
                {
                    int q5r = Q5[RoundDiv(sr, n)], q6g = Q6[RoundDiv(sg, n)], q5b = Q5[RoundDiv(sb, n)];
                    pq[c] = (ushort)((q5r << 11) | (q6g << 5) | q5b);
                    pal[c * 3] = E5[q5r]; pal[c * 3 + 1] = E6[q6g]; pal[c * 3 + 2] = E5[q5b];
                }
                else if (it == 0)
                {
                    pq[c] = pq[0]; pal[c * 3] = pal[0]; pal[c * 3 + 1] = pal[1]; pal[c * 3 + 2] = pal[2];
                }
            }
            err = 0;
            bool changed = false;
            for (int p = 0; p < 16; p++)
            {
                int bd = 1000000000, bc = 0;
                for (int c = 0; c < k; c++)
                {
                    int dr = R[p] - pal[c * 3], dg = G[p] - pal[c * 3 + 1], db = B[p] - pal[c * 3 + 2];
                    int d = dr * dr + dg * dg + db * db;
                    if (d < bd) { bd = d; bc = c; }
                }
                if (asg[p] != bc) changed = true;
                asg[p] = (byte)bc;
                err += bd;
            }
            if (!changed && it > 0) break;
        }
        Array.Copy(asg, x.Best, 16);
        return err;
    }

    private void Row(byte[] rgba, bool key, int by, Scratch x)
    {
        int W = this.W, bw = W / 4, L = _lambda;
        int[] R = x.R, G = x.G, B = x.B, pal = x.Pal;
        byte[] rec = _rec;
        ushort[] data = _data, pq = x.Pq;
        for (int bx = 0; bx < bw; bx++)
        {
            int blk = by * bw + bx;
            int Pix(int p) => (by * 4 + (p >> 2)) * W + bx * 4 + (p & 3);
            int dskip = 0;
            for (int p = 0; p < 16; p++)
            {
                int i = Pix(p);
                int r = rgba[i * 4], g = rgba[i * 4 + 1], b = rgba[i * 4 + 2];
                R[p] = r; G[p] = g; B[p] = b;
                int dr = r - rec[i * 3], dg = g - rec[i * 3 + 1], db = b - rec[i * 3 + 2];
                dskip += dr * dr + dg * dg + db * db;
            }
            if (!key && dskip < 2 * L) { _kind[blk] = 0; continue; }
            double bestJ = key ? 1e300 : dskip;
            int bestK = 0;
            int sr = 0, sg = 0, sb = 0;
            for (int p = 0; p < 16; p++) { sr += R[p]; sg += G[p]; sb += B[p]; }
            int q5r = Q5[(sr + 8) >> 4], q6g = Q6[(sg + 8) >> 4], q5b = Q5[(sb + 8) >> 4];
            int er = E5[q5r], eg = E6[q6g], eb = E5[q5b];
            int d1 = 0;
            for (int p = 0; p < 16; p++)
            {
                int dr = R[p] - er, dg = G[p] - eg, db = B[p] - eb;
                d1 += dr * dr + dg * dg + db * db;
            }
            if (d1 + 2 * L < bestJ) { bestJ = d1 + 2 * L; bestK = 1; }
            if (d1 >= 4 * L)
            {
                int d2 = Cluster(x, 2);
                if (d2 + 6 * L < bestJ)
                {
                    bestJ = d2 + 6 * L; bestK = 2;
                    uint m2 = 0;
                    for (int p = 0; p < 16; p++) if (x.Best[p] != 0) m2 |= 1u << p;
                    data[blk * 6] = pq[0]; data[blk * 6 + 1] = pq[1]; data[blk * 6 + 2] = (ushort)m2;
                    for (int p = 0; p < 16; p++)
                    {
                        int i = Pix(p), c = x.Best[p];
                        rec[i * 3] = (byte)pal[c * 3]; rec[i * 3 + 1] = (byte)pal[c * 3 + 1]; rec[i * 3 + 2] = (byte)pal[c * 3 + 2];
                    }
                }
                if (d2 >= 6 * L)
                {
                    int d4 = Cluster(x, 4);
                    if (d4 + 12 * L < bestJ)
                    {
                        bestJ = d4 + 12 * L; bestK = 3;
                        uint bits = 0;
                        for (int p = 0; p < 16; p++) bits |= (uint)x.Best[p] << (2 * p);
                        data[blk * 6] = pq[0]; data[blk * 6 + 1] = pq[1]; data[blk * 6 + 2] = pq[2]; data[blk * 6 + 3] = pq[3];
                        data[blk * 6 + 4] = (ushort)(bits & 0xFFFF); data[blk * 6 + 5] = (ushort)(bits >> 16);
                        for (int p = 0; p < 16; p++)
                        {
                            int i = Pix(p), c = x.Best[p];
                            rec[i * 3] = (byte)pal[c * 3]; rec[i * 3 + 1] = (byte)pal[c * 3 + 1]; rec[i * 3 + 2] = (byte)pal[c * 3 + 2];
                        }
                    }
                }
            }
            _kind[blk] = (byte)bestK;
            if (bestK == 1)
            {
                data[blk * 6] = (ushort)((q5r << 11) | (q6g << 5) | q5b);
                for (int p = 0; p < 16; p++)
                {
                    int i = Pix(p);
                    rec[i * 3] = (byte)er; rec[i * 3 + 1] = (byte)eg; rec[i * 3 + 2] = (byte)eb;
                }
            }
        }
    }

    [ThreadStatic] private static Scratch? _scratch;

    /// <summary>A frame (RGBA, W×H×4) as block data; valid until the next call.</summary>
    public ReadOnlySpan<byte> Encode(byte[] rgba, bool key)
    {
        int bh = H / 4, bw = W / 4;
        if (_threads > 1)
            Parallel.For(0, bh, new ParallelOptions { MaxDegreeOfParallelism = _threads }, by => Row(rgba, key, by, _scratch ??= new Scratch()));
        else
        {
            var x = _scratch ??= new Scratch();
            for (int by = 0; by < bh; by++) Row(rgba, key, by, x);
        }
        int o = 0, total = bw * bh, i = 0;
        while (i < total)
        {
            int k = _kind[i], j = i;
            while (j < total && _kind[j] == k && j - i < 64) j++;
            _out[o++] = (byte)((k << 6) | (j - i - 1));
            int nw = k == 0 ? 0 : k == 1 ? 1 : k == 2 ? 3 : 6;
            for (int b = i; b < j; b++)
                for (int w = 0; w < nw; w++) { ushort v = _data[b * 6 + w]; _out[o++] = (byte)v; _out[o++] = (byte)(v >> 8); }
            i = j;
        }
        return _out.AsSpan(0, o);
    }
}

/// <summary>Header, chunks, index - onto a seekable stream (the header is filled in at the end).</summary>
internal sealed class EvfWriter
{
    private readonly Stream _s;
    private readonly int _w, _h;
    private readonly double _fps;
    private readonly EvfAudio? _audio;
    private readonly List<(uint Frame, uint VOff, uint AOff)> _index = new();
    public long Size { get; private set; }
    public uint Frames { get; private set; }
    public const long Limit = 0xF0000000L;     // 4 GB: the format's (and FAT32's) ceiling

    public EvfWriter(Stream s, int w, int h, double fps, EvfAudio? audio)
    {
        _s = s; _w = w; _h = h; _fps = fps; _audio = audio;
        Bytes(new byte[32]);
    }

    private void Bytes(ReadOnlySpan<byte> b) { _s.Write(b); Size += b.Length; }

    private void Chunk(char type, byte flags, ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        Span<byte> h = stackalloc byte[8];
        h[0] = (byte)type; h[1] = flags; h[2] = h[3] = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(h[4..], (uint)(a.Length + b.Length));
        Bytes(h); Bytes(a); Bytes(b);
    }

    public void Frame(ReadOnlySpan<byte> blocks, bool key)
    {
        uint f = Frames++;
        long aOff = Size;
        if (_audio?.Chunk(f) is { } ab) Chunk('A', 0, ab, default);
        if (key) _index.Add((f, (uint)Size, (uint)aOff));
        Span<byte> num = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(num, f);
        Chunk('V', key ? (byte)1 : (byte)0, num, blocks);
        if (Size >= Limit) throw new IOException("The video is over 4 GB, the most a video on the iPod can be.");
    }

    public void Close()
    {
        uint idxOff = (uint)Size;
        var e = new byte[12];
        foreach (var (fr, v, a) in _index)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(e, fr);
            BinaryPrimitives.WriteUInt32LittleEndian(e.AsSpan(4), v);
            BinaryPrimitives.WriteUInt32LittleEndian(e.AsSpan(8), a);
            Bytes(e);
        }
        var h = new byte[32];
        "EVF1"u8.CopyTo(h);
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(4), (ushort)_w);
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(6), (ushort)_h);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(8), (uint)Math.Floor(_fps * 1000 + 0.5));
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(12), Frames);
        bool sound = _audio is not null && _audio.Samples > 0;
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(16), sound ? 44100u : 0u);
        h[20] = sound ? (byte)_audio!.Channels : (byte)0;
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(24), idxOff);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(28), (uint)_index.Count);
        _s.Flush();
        _s.Position = 0;
        _s.Write(h);
        _s.Flush();
    }
}

/// <summary>An .evf on disk, read the way Encore's player reads it (src/media/video.c): the header, the index, and - for
/// the self-test - every frame decoded into the RGB565 picture the iPod would show. A stored (uncompressed) zip whose
/// first entry is the video counts too, like on the iPod.</summary>
internal sealed class EvfFile
{
    public int Width, Height, Channels;
    public uint FpsMilli, Frames, Rate, IndexOffset, IndexCount;
    public long Base;                       // where the video starts in the file (a zip's entry: > 0)
    public readonly List<(uint Frame, uint VOff, uint AOff)> Index = new();
    public TimeSpan Duration => FpsMilli == 0 ? TimeSpan.Zero : TimeSpan.FromMilliseconds(Frames * 1_000_000.0 / FpsMilli);

    /// <summary>The header and index, or null when Encore wouldn't play it (wrong magic, size, sample rate).</summary>
    public static EvfFile? Read(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var z = new byte[32];
            if (fs.Length < 32 || fs.Read(z, 0, 30) != 30) return null;
            var e = new EvfFile();
            long len = fs.Length;
            if (z[0] == 'P' && z[1] == 'K' && z[2] == 3 && z[3] == 4)
            {
                uint csize = BinaryPrimitives.ReadUInt32LittleEndian(z.AsSpan(18)), usize = BinaryPrimitives.ReadUInt32LittleEndian(z.AsSpan(22));
                if (BinaryPrimitives.ReadUInt16LittleEndian(z.AsSpan(8)) != 0 || (BinaryPrimitives.ReadUInt16LittleEndian(z.AsSpan(6)) & 8) != 0 || csize != usize) return null;
                e.Base = 30 + BinaryPrimitives.ReadUInt16LittleEndian(z.AsSpan(26)) + BinaryPrimitives.ReadUInt16LittleEndian(z.AsSpan(28));
                len = csize;
            }
            fs.Position = e.Base;
            if (fs.Read(z, 0, 32) != 32 || z[0] != 'E' || z[1] != 'V' || z[2] != 'F' || z[3] != '1') return null;
            e.Width = BinaryPrimitives.ReadUInt16LittleEndian(z.AsSpan(4)); e.Height = BinaryPrimitives.ReadUInt16LittleEndian(z.AsSpan(6));
            e.FpsMilli = BinaryPrimitives.ReadUInt32LittleEndian(z.AsSpan(8)); e.Frames = BinaryPrimitives.ReadUInt32LittleEndian(z.AsSpan(12));
            e.Rate = BinaryPrimitives.ReadUInt32LittleEndian(z.AsSpan(16)); e.Channels = z[20];
            e.IndexOffset = BinaryPrimitives.ReadUInt32LittleEndian(z.AsSpan(24)); e.IndexCount = BinaryPrimitives.ReadUInt32LittleEndian(z.AsSpan(28));
            if (e.Width < 4 || e.Height < 4 || e.Width > 320 || e.Height > 240 || (e.Width & 3) != 0 || (e.Height & 3) != 0 || e.FpsMilli == 0 ||
                (e.Rate != 0 && e.Rate != 44100) || e.Channels > 2 || (e.Rate != 0 && e.Channels == 0)) return null;
            if (e.IndexCount > 0 && e.IndexCount < 1_000_000 && e.IndexOffset >= 32 && e.IndexOffset + (long)e.IndexCount * 12 <= len)
            {
                var raw = new byte[e.IndexCount * 12];
                fs.Position = e.Base + e.IndexOffset;
                fs.ReadExactly(raw);
                for (int i = 0; i < e.IndexCount; i++)
                    e.Index.Add((BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(i * 12)), BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(i * 12 + 4)), BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(i * 12 + 8))));
            }
            return e;
        }
        catch { return null; }
    }

    /// <summary>video.c decode_frame: one 'V' payload (after its frame number) onto the RGB565 picture.</summary>
    public static void DecodeFrame(ushort[] fb, int w, int h, ReadOnlySpan<byte> d)
    {
        int bw = w / 4, total = bw * (h / 4), blk = 0, at = 0;
        while (blk < total && at < d.Length)
        {
            int op = d[at++], n = (op & 0x3F) + 1, kind = op >> 6;
            if (n > total - blk) n = total - blk;
            if (kind == 0) { blk += n; continue; }
            for (int i = 0; i < n; i++, blk++)
            {
                int row = blk / bw * 4 * w, p = row + blk % bw * 4;
                if (kind == 1)
                {
                    if (at + 2 > d.Length) return;
                    ushort c = BinaryPrimitives.ReadUInt16LittleEndian(d[at..]); at += 2;
                    for (int y = 0; y < 4; y++, p += w) fb[p] = fb[p + 1] = fb[p + 2] = fb[p + 3] = c;
                }
                else if (kind == 2)
                {
                    if (at + 6 > d.Length) return;
                    ushort c0 = BinaryPrimitives.ReadUInt16LittleEndian(d[at..]), c1 = BinaryPrimitives.ReadUInt16LittleEndian(d[(at + 2)..]);
                    uint m = BinaryPrimitives.ReadUInt16LittleEndian(d[(at + 4)..]); at += 6;
                    for (int y = 0; y < 4; y++, p += w, m >>= 4)
                        for (int x = 0; x < 4; x++) fb[p + x] = ((m >> x) & 1) != 0 ? c1 : c0;
                }
                else
                {
                    if (at + 12 > d.Length) return;
                    Span<ushort> c = stackalloc ushort[4];
                    for (int k = 0; k < 4; k++) c[k] = BinaryPrimitives.ReadUInt16LittleEndian(d[(at + 2 * k)..]);
                    uint m = BinaryPrimitives.ReadUInt32LittleEndian(d[(at + 8)..]); at += 12;
                    for (int y = 0; y < 4; y++, p += w, m >>= 8)
                        for (int x = 0; x < 4; x++) fb[p + x] = c[(int)((m >> (2 * x)) & 3)];
                }
            }
        }
    }

    /// <summary>Walk every chunk like the player: each frame's picture after decoding it, and the sample frames of
    /// sound the 'A' chunks carry. False when a chunk is damaged.</summary>
    public bool Play(string path, Action<uint, ushort[]> frame, out long samples)
    {
        samples = 0;
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var fb = new ushort[Width * Height];
        long end = IndexCount > 0 ? IndexOffset : fs.Length - Base, pos = 32;
        var h = new byte[8];
        while (pos + 8 <= end)
        {
            fs.Position = Base + pos;
            fs.ReadExactly(h);
            uint sz = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(4));
            if (sz > end - pos - 8 || sz > 512 * 1024 || (h[0] != 'A' && h[0] != 'V')) return false;
            var body = new byte[sz];
            fs.ReadExactly(body);
            if (h[0] == 'A') samples += BinaryPrimitives.ReadUInt32LittleEndian(body);
            else
            {
                DecodeFrame(fb, Width, Height, body.AsSpan(4));
                frame(BinaryPrimitives.ReadUInt32LittleEndian(body), fb);
            }
            pos += 8 + sz;
        }
        return pos == end;
    }
}

/// <summary>Picture and sound settings for <see cref="EvfConverter"/> - the Encore converter's own choices.</summary>
internal sealed record EvfOptions(int Fps = 15, int Lambda = 300, bool Fill = false, bool Mono = false)
{
    public static readonly int[] FpsChoices = { 10, 15, 20, 24 };
    public static readonly int[] Lambdas = { 800, 300, 120 };   // smaller file / balanced / better picture
}

/// <summary>
/// Any video ffmpeg can open → an .evf (Encore's converter, tools/evf/win/atalakito.c, step for step): the sound first
/// (44.1 kHz, IMA ADPCM), then the picture at the chosen frame rate, scaled into 320×240 with sides a multiple of 4,
/// a keyframe every 2 seconds. The file is written as "&lt;name&gt;.evf.part" and renamed when it is complete, so the
/// iPod never lists half a video.
/// </summary>
internal static class EvfConverter
{
    internal sealed class Probe
    {
        public int W, H;
        public double Duration;
        public bool Audio, Video, Drm;
        public string Title = "";
    }

    /// <summary>What `ffmpeg -i` says about a file: picture size (display aspect, turned as ffmpeg turns it), length,
    /// whether it has sound, and copy protection. Null with a reason when it isn't a video ffmpeg can read.</summary>
    public static Probe? ProbeFile(string ffmpeg, string src, out string? error)
    {
        error = null;
        string txt;
        try
        {
            var psi = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true, StandardErrorEncoding = Encoding.UTF8 };
            psi.ArgumentList.Add("-hide_banner"); psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(src);
            using var p = Process.Start(psi)!;
            var err = p.StandardError.ReadToEndAsync();
            p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(30_000)) { try { p.Kill(true); } catch { } }
            txt = err.Result;
        }
        catch (Exception ex) { error = ex.Message; return null; }
        var pr = new Probe();
        var dm = System.Text.RegularExpressions.Regex.Match(txt, @"Duration: (\d+):(\d+):(\d+(?:\.\d+)?)");
        if (dm.Success) pr.Duration = int.Parse(dm.Groups[1].Value) * 3600 + int.Parse(dm.Groups[2].Value) * 60 + double.Parse(dm.Groups[3].Value, CultureInfo.InvariantCulture);
        pr.Drm = txt.Contains("drmi") || txt.Contains("drms") || txt.Contains("(encrypted)");
        int rot = 0;
        bool streams = false;
        foreach (string line in txt.Split('\n'))
        {
            if (line.Contains("Stream #")) streams = true;
            int tl = line.IndexOf("    title ", StringComparison.Ordinal);
            if (!streams && tl >= 0 && pr.Title.Length == 0 && line.IndexOf(": ", tl, StringComparison.Ordinal) is int v and > 0) pr.Title = line[(v + 2)..].Trim();
            if (line.Contains("Stream #") && line.Contains(": Video: ") && !pr.Video && !line.Contains("attached pic"))
            {
                var m = System.Text.RegularExpressions.Regex.Match(line[line.IndexOf(": Video: ", StringComparison.Ordinal)..], @"[, ] (\d+)x(\d+)(?=[ ,\r]|$)");
                if (m.Success && int.TryParse(m.Groups[1].Value, out int w) && int.TryParse(m.Groups[2].Value, out int h) && w > 0 && h > 0)
                {
                    pr.W = w; pr.H = h; pr.Video = true;
                    var dar = System.Text.RegularExpressions.Regex.Match(line, @"DAR (\d+):(\d+)");
                    if (dar.Success && int.Parse(dar.Groups[1].Value) is int a and > 0 && int.Parse(dar.Groups[2].Value) is int b and > 0)
                        pr.W = (int)Math.Floor((double)h * a / b + 0.5);
                }
            }
            else if (line.Contains("Stream #") && line.Contains(": Audio: ")) pr.Audio = true;
            var r1 = System.Text.RegularExpressions.Regex.Match(line, @"rotation of (-?\d+(?:\.\d+)?)");
            if (r1.Success && pr.Video) rot = (int)Math.Floor(Math.Abs(double.Parse(r1.Groups[1].Value, CultureInfo.InvariantCulture)) + 0.5);
            var r2 = System.Text.RegularExpressions.Regex.Match(line, @"rotate\s+:\s*(-?\d+)");
            if (r2.Success && pr.Video) rot = Math.Abs(int.Parse(r2.Groups[1].Value));
        }
        if (rot % 180 == 90) (pr.W, pr.H) = (pr.H, pr.W);   // ffmpeg turns the picture upright itself
        if (pr.Drm) { error = "copy-protected (DRM)"; return null; }
        if (!pr.Video) { error = "no picture in it, or not a video"; return null; }
        return pr;
    }

    /// <summary>The picture's size on the iPod and ffmpeg's filter for it: the whole picture inside 320×240 (never
    /// enlarged), or filling a 4:3 screen and cropping the sides.</summary>
    public static (int W, int H, string Vf) Geometry(Probe p, EvfOptions o)
    {
        int W, H;
        string vf;
        if (o.Fill)
        {
            W = p.W < 320 ? p.W / 4 * 4 : 320;
            if (W < 4) W = 4;
            H = (int)Math.Floor(W * 3.0 / 4 / 4 + 0.5) * 4;
            if (H > 240) H = 240;
            vf = $"fps={o.Fps},scale={W}:{H}:force_original_aspect_ratio=increase:flags=lanczos,crop={W}:{H},setsar=1";
        }
        else
        {
            double s = Math.Min(Math.Min(320.0 / p.W, 240.0 / p.H), 1.0);
            W = Math.Max(4, (int)Math.Floor(p.W * s / 4) * 4);
            H = Math.Max(4, (int)Math.Floor(p.H * s / 4) * 4);
            vf = $"fps={o.Fps},scale={W}:{H}:flags=lanczos,setsar=1";
        }
        return (W, H, vf);
    }

    /// <summary>A converted copy of <paramref name="baseName"/> already in the folder (Film.evf, Film.evf.zip, Film.zip).</summary>
    public static string? Existing(string dir, string baseName)
    {
        foreach (string ext in new[] { ".evf", ".evf.zip", ".zip" })
        {
            string p = Path.Combine(dir, baseName + ext);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    private static Process Start(string ffmpeg, params string[] args)
    {
        var psi = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (string a in args) psi.ArgumentList.Add(a);
        var p = Process.Start(psi)!;
        p.ErrorDataReceived += (_, _) => { };   // drained, so a chatty ffmpeg never blocks on a full pipe
        p.BeginErrorReadLine();
        return p;
    }

    private static void Stop(Process p)
    {
        try { if (!p.HasExited) p.Kill(true); } catch { }
        try { p.WaitForExit(5000); } catch { }
    }

    /// <summary>
    /// Convert <paramref name="src"/> into <paramref name="dst"/> (an .evf path; a number is added when it is taken).
    /// <paramref name="progress"/> gets 0..1 and the time converted so far. Returns the file written; throws
    /// <see cref="OperationCanceledException"/> when cancelled (nothing is left behind) and an exception with a reason
    /// when the video can't be converted.
    /// </summary>
    public static string Convert(string ffmpeg, string src, string dst, EvfOptions o, Action<double, TimeSpan> progress, Func<bool> cancelled, int threads = 1)
    {
        var pr = ProbeFile(ffmpeg, src, out string? why) ?? throw new InvalidDataException(why ?? "can't be read");
        var (W, H, vf) = Geometry(pr, o);
        int ch = o.Mono ? 1 : 2;
        double expect = pr.Duration > 0 ? pr.Duration * o.Fps : 0;

        // 1. the sound, all of it (the 'A' chunk of frame f needs the coder's state at its start)
        EvfAudio? audio = null;
        if (pr.Audio)
        {
            audio = new EvfAudio(ch, o.Fps);
            var p = Start(ffmpeg, "-v", "error", "-i", src, "-vn", "-ac", ch.ToString(CultureInfo.InvariantCulture), "-ar", "44100", "-f", "s16le", "-");
            try
            {
                var s = p.StandardOutput.BaseStream;
                var buf = new byte[65536];
                int have = 0, n;
                while ((n = s.Read(buf, have, buf.Length - have)) > 0)
                {
                    if (cancelled()) throw new OperationCanceledException();
                    have += n;
                    int got = have / (2 * ch);
                    audio.Write(System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(buf.AsSpan(0, got * 2 * ch)), got);
                    int rest = have - got * 2 * ch;
                    Buffer.BlockCopy(buf, got * 2 * ch, buf, 0, rest);
                    have = rest;
                    if (pr.Duration > 0) progress(Math.Min(1, audio.Samples / 44100.0 / pr.Duration) * 0.05, TimeSpan.Zero);
                }
            }
            finally { Stop(p); }
            if (audio.Samples == 0) audio = null;
        }

        // 2. the picture, frame by frame, into "<name>.evf.part"
        string dir = Path.GetDirectoryName(dst)!, stem = Path.GetFileNameWithoutExtension(dst);
        string part = dst + ".part";
        var enc = new EvfFrameEncoder(W, H, o.Lambda, threads);
        var vp = Start(ffmpeg, "-v", "error", "-i", src, "-an", "-sn", "-vf", vf, "-f", "rawvideo", "-pix_fmt", "rgba", "-");
        bool done = false;
        uint frames;
        try
        {
            using (var fs = new FileStream(part, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 20))
            {
                var wr = new EvfWriter(fs, W, H, o.Fps, audio);
                var frame = new byte[W * H * 4];
                var s = vp.StandardOutput.BaseStream;
                int keyEvery = o.Fps * 2;
                var last = Stopwatch.StartNew();
                while (ReadFull(s, frame))
                {
                    if (cancelled()) throw new OperationCanceledException();
                    bool key = wr.Frames % keyEvery == 0;
                    wr.Frame(enc.Encode(frame, key), key);
                    if (last.ElapsedMilliseconds > 150)
                    {
                        last.Restart();
                        double f = expect > 0 ? Math.Min(1, wr.Frames / expect) : 0;
                        progress(0.05 + 0.95 * f, TimeSpan.FromSeconds(wr.Frames / (double)o.Fps));
                    }
                }
                frames = wr.Frames;
                if (frames == 0) throw new InvalidDataException("not a single frame could be read");
                wr.Close();
            }
            // the finished file takes the name, or the next free "(2)", "(3)"… - never one being written meanwhile
            string outPath = dst;
            for (int k = 2; ; k++)
            {
                try { File.Move(part, outPath); break; }
                catch (IOException) when (File.Exists(outPath) && k < 1000) { outPath = Path.Combine(dir, $"{stem} ({k}).evf"); }
            }
            done = true;
            progress(1, TimeSpan.FromSeconds(frames / (double)o.Fps));
            return outPath;
        }
        finally
        {
            Stop(vp);
            if (!done) try { File.Delete(part); } catch { }
        }
    }

    private static bool ReadFull(Stream s, byte[] buf)
    {
        int got = 0;
        while (got < buf.Length)
        {
            int n = s.Read(buf, got, buf.Length - got);
            if (n <= 0) return false;
            got += n;
        }
        return true;
    }
}
