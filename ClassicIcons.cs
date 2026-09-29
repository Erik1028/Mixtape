namespace iPodCommander;

/// <summary>
/// The Windows 95 look's icons: 16 x 16 pixel art in the sixteen colours of the VGA palette, the way every icon
/// of 1995 was made. Each is a hand-placed character map (one letter per pixel), turned into a bitmap once and
/// cached; larger sizes are integer multiples of the same pixels, never smoothed, because a smoothed pixel icon is
/// neither one thing nor the other.
/// </summary>
internal static class ClassicIcons
{
    public enum Id
    {
        Home, IPod, Songs, Album, Artist, Chart, Playlist, SmartPlaylist, Computer, Video, Photos, Latest,
        Folder, Lyrics, Queue, Wand, Eq, Speaker, SpeakerMute, CoverFlow, AddToList, Gear, Shuffle, Repeat, RepeatOne,
        RadioOff, RadioOn,
    }

    // k black  w white  s silver  g grey  n navy  b blue  t teal  c cyan  r red  m maroon
    // y yellow o olive  e green   l lime  p purple f fuchsia   . transparent
    private static Color Pal(char c) => c switch
    {
        'k' => Color.FromArgb(0, 0, 0),
        'w' => Color.FromArgb(255, 255, 255),
        's' => Color.FromArgb(192, 192, 192),
        'g' => Color.FromArgb(128, 128, 128),
        'n' => Color.FromArgb(0, 0, 128),
        'b' => Color.FromArgb(0, 0, 255),
        't' => Color.FromArgb(0, 128, 128),
        'c' => Color.FromArgb(0, 255, 255),
        'r' => Color.FromArgb(255, 0, 0),
        'm' => Color.FromArgb(128, 0, 0),
        'y' => Color.FromArgb(255, 255, 0),
        'o' => Color.FromArgb(128, 128, 0),
        'e' => Color.FromArgb(0, 128, 0),
        'l' => Color.FromArgb(0, 255, 0),
        'p' => Color.FromArgb(128, 0, 128),
        'f' => Color.FromArgb(255, 0, 255),
        _ => Color.Transparent,
    };

    private static string[] Map(Id id) => id switch
    {
        Id.Home => new[]
        {
            "................",
            ".......kk.......",
            "......kmmk......",
            ".....kmrrmk.....",
            "....kmrrrrmk....",
            "...kmrrrrrrmk...",
            "..kmrrrrrrrrmk..",
            ".kkkkkkkkkkkkkk.",
            "..kwwwwwwwwwwk..",
            "..kwbbwwkkkkwk..",
            "..kwbbwwkmmkwk..",
            "..kwwwwwkmmkwk..",
            "..kwwwwwkmykwk..",
            "..kwwwwwkmmkwk..",
            "..kkkkkkkkkkkk..",
            "................",
        },
        Id.IPod => new[]
        {
            "................",
            "....kkkkkkkk....",
            "...kwsssssssk...",
            "...kskkkkkksk...",
            "...kskctttksk...",
            "...kskttttksk...",
            "...kskkkkkksk...",
            "...kssssssssk...",
            "...ksswwwwssk...",
            "...kswggggwsk...",
            "...kswgssgwsk...",
            "...kswgssgwsk...",
            "...kswggggwsk...",
            "...ksswwwwssk...",
            "...kgsssssssk...",
            "....kkkkkkkk....",
        },
        Id.Songs => new[]
        {
            "................",
            "......nnnnnnnn..",
            "......nnnnnnnn..",
            "......n......n..",
            "......n......n..",
            "......n......n..",
            "......n......n..",
            "......n......n..",
            "......n......n..",
            "......n......n..",
            "...nnnn...nnnn..",
            "..nnnnn..nnnnn..",
            "..nnnnn..nnnnn..",
            "...nnn....nnn...",
            "................",
            "................",
        },
        Id.Album => new[]
        {
            "................",
            ".....gggggg.....",
            "...ggwwwwsssg...",
            "..gwwwwssssssg..",
            "..gwwwssssssyg..",
            ".gwwwsssssssycg.",
            ".gwwsssggsssycg.",
            ".gssssgkkgsycfg.",
            ".gssssgkkgycfsg.",
            ".gssssggggycfsg.",
            ".gsssssssycfssg.",
            "..gsssssycfsssg.",
            "..gssssycfsssg..",
            "...gsssssssgg...",
            ".....gggggg.....",
            "................",
        },
        Id.Artist => new[]
        {
            "................",
            "......kkkk......",
            ".....kkkkkk.....",
            ".....kwwwwk.....",
            ".....kwwwwk.....",
            ".....kwwwwk.....",
            "......kwwk......",
            ".....kkwwkk.....",
            "....knnwwnnk....",
            "...knnnnnnnnk...",
            "..knnnnnnnnnnk..",
            "..knnnnnnnnnnk..",
            "..knnnnnnnnnnk..",
            "..kkkkkkkkkkkk..",
            "................",
            "................",
        },
        Id.Chart => new[]
        {
            "................",
            ".kkkkkkkkkkkkkk.",
            ".kwwwwwwwwwwwwk.",
            ".kwwwwwwwwrrwwk.",
            ".kwwwwwwwwrrwwk.",
            ".kwwwwwwwwrrwwk.",
            ".kwwwwwbbwrrwwk.",
            ".kwwwwwbbwrrwwk.",
            ".kwwwwwbbwrrwwk.",
            ".kwwllwbbwrrwwk.",
            ".kwwllwbbwrrwwk.",
            ".kwwllwbbwrrwwk.",
            ".kwkkkkkkkkkkwk.",
            ".kwwwwwwwwwwwwk.",
            ".kkkkkkkkkkkkkk.",
            "................",
        },
        Id.Playlist => new[]
        {
            "..kkkkkkkkk.....",
            "..kwwwwwwwkk....",
            "..kwwwwwwwkwk...",
            "..kwkkkkwwkkkk..",
            "..kwwwwwwwwwwk..",
            "..kwkkkkkkkwwk..",
            "..kwwwwwwwwwwk..",
            "..kwkkkkkkwwwk..",
            "..kwwwwwwwwnwk..",
            "..kwkkkkwwwnnk..",
            "..kwwwwwwwwnwk..",
            "..kwkkkkwwwnwk..",
            "..kwwwwwwnnnwk..",
            "..kwwwwwwnnwwk..",
            "..kwwwwwwwwwwk..",
            "..kkkkkkkkkkkk..",
        },
        Id.SmartPlaylist => new[]
        {
            "..kkkkkkkkk.....",
            "..kwwwwwwwkk....",
            "..kwwwwwwwkwk...",
            "..kwkkkkwwkkkk..",
            "..kwwwwwwwwwwk..",
            "..kwkkkkkkkwwk..",
            "..kwwwwwwwwwwk..",
            "..kwkkkkkwwywk..",
            "..kwwwwwwwwywk..",
            "..kwkkkkwyyyyyk.",
            "..kwwwwwwwyyyk..",
            "..kwkkkkwwyyyk..",
            "..kwwwwwwywwywk.",
            "..kwwwwwwwwwwk..",
            "..kwwwwwwwwwwk..",
            "..kkkkkkkkkkkk..",
        },
        Id.Computer => new[]
        {
            "................",
            "..kkkkkkkkkkkk..",
            "..kssssssssssk..",
            "..kskkkkkkkksk..",
            "..ksktttttcksk..",
            "..kskttttttksk..",
            "..kskttttttksk..",
            "..kskttttttksk..",
            "..kskkkkkkkksk..",
            "..kssssssssesk..",
            "..kkkkkkkkkkkk..",
            ".....kssssk.....",
            "...kkkkkkkkkk...",
            "...kssssssssk...",
            "...kkkkkkkkkk...",
            "................",
        },
        Id.Video => new[]
        {
            "................",
            "................",
            "kkkkkkkkkkkkkkkk",
            "kwkkwkkwkkwkkwkk",
            "kkkkkkkkkkkkkkkk",
            "kkcccckkcccckkkk",
            "kkcbbckkcbbckkkk",
            "kkcbbckkcbbckkkk",
            "kkcccckkcccckkkk",
            "kkkkkkkkkkkkkkkk",
            "kwkkwkkwkkwkkwkk",
            "kkkkkkkkkkkkkkkk",
            "................",
            "................",
            "................",
            "................",
        },
        Id.Photos => new[]
        {
            "................",
            "kkkkkkkkkkkkkkkk",
            "kssssssssssssssk",
            "kskkkkkkkkkkkksk",
            "kskccccccccyycsk",
            "kskccccccccyycsk",
            "kskcccccccccccsk",
            "kskcccccecccccsk",
            "kskccceeeeccccsk",
            "kskceeeeeeeeccsk",
            "kskeeeeeeeeeeesk",
            "kskeeeeeeeeeeesk",
            "kskkkkkkkkkkkksk",
            "kssssssssssssssk",
            "kkkkkkkkkkkkkkkk",
            "................",
        },
        Id.Latest => new[]
        {
            "................",
            "..y...nnnnnnnn..",
            ".yyy..nnnnnnnn..",
            "yyyyy.n......n..",
            ".yyy..n......n..",
            ".y.y..n......n..",
            "......n......n..",
            "......n......n..",
            "......n......n..",
            "......n......n..",
            "...nnnn...nnnn..",
            "..nnnnn..nnnnn..",
            "..nnnnn..nnnnn..",
            "...nnn....nnn...",
            "................",
            "................",
        },
        Id.Folder => new[]
        {
            "................",
            "................",
            "..kkkkk.........",
            ".kwyyyyk........",
            ".kyyyyyykkkkkkk.",
            ".kwwwwwwwwwwwwok",
            ".kwyyyyyyyyyyyok",
            ".kwyyyyyyyyyyyok",
            ".kwyyyyyyyyyyyok",
            ".kwyyyyyyyyyyyok",
            ".kwyyyyyyyyyyyok",
            ".kwyyyyyyyyyyyok",
            ".koooooooooooooo",
            ".kkkkkkkkkkkkkkk",
            "................",
            "................",
        },
        Id.Lyrics => new[]
        {
            "................",
            "..kkkkkkkkkkkk..",
            ".kwwwwwwwwwwwwk.",
            "kwwwwwwwwwwwwwwk",
            "kwkkkkkwkkkkwwwk",
            "kwwwwwwwwwwwwwwk",
            "kwkkkwkkkkkkkwwk",
            "kwwwwwwwwwwwwwwk",
            "kwkkkkkkwkkwwwwk",
            ".kwwwwwwwwwwwwk.",
            "..kkkwwkkkkkkk..",
            "....kwk.........",
            "....kk..........",
            "...k............",
            "................",
            "................",
        },
        Id.Queue => new[]
        {
            "................",
            "................",
            ".e..............",
            ".ee..kkkkkkkkkk.",
            ".eee............",
            ".ee..kkkkkkkkkk.",
            ".e..............",
            ".....kkkkkkkkkk.",
            "................",
            ".....kkkkkkkkkk.",
            "................",
            ".....kkkkkkk....",
            "................",
            "................",
            "................",
            "................",
        },
        Id.Wand => new[]
        {
            "..........y.....",
            "..........y.....",
            "........yyyyy...",
            "..........y.....",
            ".....y....y.....",
            "....yyy.........",
            ".....y..wk......",
            ".......wwk......",
            "......wwk.......",
            ".....kkk........",
            "....kkk.........",
            "...kkk..........",
            "..kkk...........",
            ".kkk............",
            ".kk.............",
            "................",
        },
        Id.Eq => new[]
        {
            "................",
            "..k....k....k...",
            "..k....k....k...",
            "..k..kkkkk..k...",
            "..k..kwwwk..k...",
            "kkkkkkkkkk..k...",
            "kwwwk..k....k...",
            "kkkkk..k..kkkkk.",
            "..k....k..kwwwk.",
            "..k....k..kkkkk.",
            "..k....k....k...",
            "..k....k....k...",
            "..k....k....k...",
            "..k....k....k...",
            "................",
            "................",
        },
        Id.Speaker => new[]
        {
            "................",
            "......k.........",
            ".....kk.....k...",
            "....ksk..k...k..",
            "kkkkssk...k..k..",
            "kssssk.k...k..k.",
            "kssssk..k..k..k.",
            "kssssk..k..k..k.",
            "kssssk.k...k..k.",
            "kkkkssk...k..k..",
            "....ksk..k...k..",
            ".....kk.....k...",
            "......k.........",
            "................",
            "................",
            "................",
        },
        Id.SpeakerMute => new[]
        {
            "................",
            "......k.........",
            ".....kk.........",
            "....ksk.........",
            "kkkkssk..r...r..",
            "kssssk....r.r...",
            "kssssk.....r....",
            "kssssk....r.r...",
            "kssssk...r...r..",
            "kkkkssk.........",
            "....ksk.........",
            ".....kk.........",
            "......k.........",
            "................",
            "................",
            "................",
        },
        Id.CoverFlow => new[]
        {
            "................",
            "................",
            "....kkkkkkkk....",
            "kkk.kccccccck.kk",
            "kgk.kccccyyck.kg",
            "kgk.kccccyyck.kg",
            "kgk.kccccccck.kg",
            "kgk.kcceeccck.kg",
            "kgk.keeeeeeek.kg",
            "kgk.keeeeeeek.kg",
            "kkk.keeeeeeek.kk",
            "....kkkkkkkk....",
            "................",
            "................",
            "................",
            "................",
        },
        Id.AddToList => new[]
        {
            "................",
            "................",
            ".kkkkkkkkkk.....",
            "................",
            ".kkkkkkkkkk.....",
            "................",
            ".kkkkkkkkkk.....",
            "................",
            ".kkkkkk....ee...",
            "...........ee...",
            ".........eeeeee.",
            ".........eeeeee.",
            "...........ee...",
            "...........ee...",
            "................",
            "................",
        },
        Id.Gear => Outline(new[]
        {
            "................",
            ".......xx.......",
            "...xx..xx..xx...",
            "...xxxxxxxxxx...",
            "....xxxxxxxx....",
            "...xxxxxxxxxx...",
            ".xxxxxx..xxxxxx.",
            ".xxxxx....xxxxx.",
            ".xxxxx....xxxxx.",
            ".xxxxxx..xxxxxx.",
            "...xxxxxxxxxx...",
            "....xxxxxxxx....",
            "...xxxxxxxxxx...",
            "...xx..xx..xx...",
            ".......xx.......",
            "................",
        }),
        Id.Shuffle => new[]
        {
            "................",
            "................",
            "...........k....",
            "...........kk...",
            "kkkk...kkkkkkk..",
            "....k.k....kk...",
            ".....k.....k....",
            "....k.k.........",
            "kkkk...kkkk.k...",
            "...........kk...",
            "........kkkkkk..",
            "...........kk...",
            "...........k....",
            "................",
            "................",
            "................",
        },
        Id.Repeat => new[]
        {
            "................",
            "................",
            "..........k.....",
            "..kkkkkkkkkk....",
            ".k........kkk...",
            ".k........k.....",
            ".k..............",
            ".k............k.",
            ".k............k.",
            "..............k.",
            ".....k........k.",
            "...kkk........k.",
            "....kkkkkkkkkk..",
            ".....k..........",
            "................",
            "................",
        },
        Id.RepeatOne => new[]
        {
            "................",
            "................",
            "..........k.....",
            "..kkkkkkkkkk....",
            ".k........kkk...",
            ".k........k.....",
            ".k.....kk.......",
            ".k....kkk.....k.",
            ".k.....kk.....k.",
            ".......kk.....k.",
            ".....k.kk.....k.",
            "...kkk........k.",
            "....kkkkkkkkkk..",
            ".....k..........",
            "................",
            "................",
        },
        // the radio button: a 12 px circle in the top-left of the map - grey and black arcs on the lit side, white
        // and silver on the shaded side, a white well, and (on) the four-by-four dot
        Id.RadioOff => new[]
        {
            "....gggg........",
            "..ggkkkkgg......",
            ".gkkwwwwkkw.....",
            ".gkwwwwwwsw.....",
            "gkwwwwwwwwsw....",
            "gkwwwwwwwwsw....",
            "gkwwwwwwwwsw....",
            "gkwwwwwwwwsw....",
            ".gkwwwwwwsw.....",
            ".wsswwwwssw.....",
            "..wwssssww......",
            "....wwww........",
            "................",
            "................",
            "................",
            "................",
        },
        Id.RadioOn => new[]
        {
            "....gggg........",
            "..ggkkkkgg......",
            ".gkkwwwwkkw.....",
            ".gkwwwwwwsw.....",
            "gkwwwkkwwwsw....",
            "gkwwkkkkwwsw....",
            "gkwwkkkkwwsw....",
            "gkwwwkkwwwsw....",
            ".gkwwwwwwsw.....",
            ".wsswwwwssw.....",
            "..wwssssww......",
            "....wwww........",
            "................",
            "................",
            "................",
            "................",
        },
        _ => new[] { "................" },
    };

    /// <summary>A filled silhouette ('x') turned into a drawn shape: black where it meets the outside, silver
    /// inside, a white pixel of light on each upper-left edge - how the era shaded a metal part.</summary>
    private static string[] Outline(string[] mask)
    {
        bool In(int x, int y) => y >= 0 && y < mask.Length && x >= 0 && x < mask[y].Length && mask[y][x] == 'x';
        var rows = new string[mask.Length];
        for (int y = 0; y < mask.Length; y++)
        {
            var r = new char[mask[y].Length];
            for (int x = 0; x < r.Length; x++)
            {
                if (!In(x, y)) { r[x] = '.'; continue; }
                bool edge = !In(x - 1, y) || !In(x + 1, y) || !In(x, y - 1) || !In(x, y + 1);
                r[x] = edge ? 'k' : (!In(x - 2, y) || !In(x, y - 2)) ? 'w' : (!In(x + 2, y) || !In(x, y + 2)) ? 'g' : 's';
            }
            rows[y] = new string(r);
        }
        return rows;
    }

    private static readonly Dictionary<(Id, int), Bitmap> Cache = new();

    /// <summary>The icon at <paramref name="scale"/> x its 16 px (2 = the era's "large icon" size, pixel for pixel).</summary>
    public static Bitmap Get(Id id, int scale = 1)
    {
        scale = Math.Max(1, scale);
        lock (Cache)
        {
            if (Cache.TryGetValue((id, scale), out var hit)) return hit;
            var rows = Map(id);
            var bmp = new Bitmap(16 * scale, 16 * scale, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            for (int y = 0; y < Math.Min(16, rows.Length); y++)
            {
                string row = rows[y];
                for (int x = 0; x < Math.Min(16, row.Length); x++)
                {
                    var c = Pal(row[x]);
                    if (c.A == 0) continue;
                    for (int dy = 0; dy < scale; dy++)
                        for (int dx = 0; dx < scale; dx++)
                            bmp.SetPixel(x * scale + dx, y * scale + dy, c);
                }
            }
            Cache[(id, scale)] = bmp;
            return bmp;
        }
    }

    /// <summary>Draw the icon with its top-left at (x, y), unscaled pixels (no interpolation, no smoothing).</summary>
    public static void Draw(Graphics g, Id id, int x, int y, int scale = 1)
    {
        var bmp = Get(id, scale);
        var im = g.InterpolationMode; var po = g.PixelOffsetMode;
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
        g.DrawImage(bmp, new Rectangle(x, y, bmp.Width, bmp.Height));
        g.InterpolationMode = im; g.PixelOffsetMode = po;
    }

    /// <summary>Draw the icon centred in <paramref name="r"/> at the largest whole scale that fits (at most
    /// <paramref name="maxScale"/>).</summary>
    public static void DrawCentered(Graphics g, Id id, Rectangle r, int maxScale = 1)
    {
        int scale = Math.Max(1, Math.Min(maxScale, Math.Min(r.Width, r.Height) / 16));
        int s = 16 * scale;
        Draw(g, id, r.X + (r.Width - s) / 2, r.Y + (r.Height - s) / 2, scale);
    }

    /// <summary>Row-length check for the maps (the render harness reports any that are not 16 wide).</summary>
    public static IEnumerable<string> Problems()
    {
        foreach (Id id in Enum.GetValues(typeof(Id)))
        {
            var rows = Map(id);
            if (rows.Length != 16) yield return $"{id}: {rows.Length} rows";
            for (int i = 0; i < rows.Length; i++) if (rows[i].Length != 16) yield return $"{id} row {i}: {rows[i].Length} wide";
        }
    }
}
