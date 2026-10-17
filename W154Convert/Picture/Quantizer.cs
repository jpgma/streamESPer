namespace W154Convert;

sealed class Palette
{
    public Palette(ushort[] entries, int count, byte[] lut)
    {
        Entries = entries;
        Count = count;
        Lut = lut;
    }

    public ushort[] Entries { get; }

    public int Count { get; }

    public byte[] Lut { get; }
}

static class Quantizer
{
    readonly struct Bin
    {
        public Bin(ushort value, int count)
        {
            Value = value;
            Count = count;
        }

        public ushort Value { get; }

        public int Count { get; }
    }

    public static Palette Build(IReadOnlyList<ushort[]> frames, bool reserveBlack)
    {
        var hist = new int[65536];
        foreach (ushort[] frame in frames)
        {
            foreach (ushort pixel in frame)
                hist[pixel]++;
        }

        var colors = new List<Bin>();
        for (int i = 0; i < hist.Length; i++)
        {
            if (hist[i] == 0)
                continue;
            if (reserveBlack && i == 0)
                continue;
            colors.Add(new Bin((ushort)i, hist[i]));
        }

        int slots = reserveBlack ? PictureContract.PaletteColors - 1 : PictureContract.PaletteColors;
        ushort[] chosen = colors.Count <= slots
            ? colors.Select(c => c.Value).OrderBy(v => v).ToArray()
            : MedianCut(colors, slots);

        var entries = new ushort[PictureContract.PaletteColors];
        int count;
        if (reserveBlack)
        {
            entries[0] = 0;
            chosen.CopyTo(entries, 1);
            count = chosen.Length + 1;
        }
        else
        {
            chosen.CopyTo(entries, 0);
            count = chosen.Length;
        }

        if (count == 0)
        {
            entries[0] = 0;
            count = 1;
        }

        return new Palette(entries, count, BuildLut(entries, count));
    }

    static ushort[] MedianCut(List<Bin> colors, int slots)
    {
        var boxes = new List<Box> { Box.Create(colors) };
        while (boxes.Count < slots)
        {
            int best = -1;
            int bestScore = 0;
            for (int i = 0; i < boxes.Count; i++)
            {
                int score = boxes[i].SplitScore();
                if (score > bestScore)
                {
                    bestScore = score;
                    best = i;
                }
            }

            if (best < 0)
                break;

            (Box left, Box right) = boxes[best].Split();
            boxes[best] = left;
            boxes.Add(right);
        }

        var used = new HashSet<ushort>();
        var result = new List<ushort>(boxes.Count);
        foreach (Box box in boxes)
        {
            if (box.Representative(used) is not ushort color)
                continue;
            if (used.Add(color))
                result.Add(color);
        }

        result.Sort();
        return result.ToArray();
    }

    static byte[] BuildLut(ushort[] entries, int count)
    {
        var lut = new byte[65536];
        var red = new int[count];
        var green = new int[count];
        var blue = new int[count];
        for (int i = 0; i < count; i++)
        {
            Rgb565.Unpack(entries[i], out byte r, out byte g, out byte b);
            red[i] = r;
            green[i] = g;
            blue[i] = b;
        }

        for (int color = 0; color < 65536; color++)
        {
            Rgb565.Unpack((ushort)color, out byte r, out byte g, out byte b);
            int best = 0;
            int bestDist = int.MaxValue;
            for (int i = 0; i < count; i++)
            {
                int dr = r - red[i];
                int dg = g - green[i];
                int db = b - blue[i];
                int dist = (2 * dr * dr) + (4 * dg * dg) + (3 * db * db);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = i;
                    if (dist == 0)
                        break;
                }
            }

            lut[color] = (byte)best;
        }

        return lut;
    }

    sealed class Box
    {
        Box(List<Bin> colors)
        {
            Colors = colors;
            foreach (Bin color in colors)
            {
                int r = Rgb565.ChannelR(color.Value);
                int g = Rgb565.ChannelG(color.Value);
                int b = Rgb565.ChannelB(color.Value);
                RMin = Math.Min(RMin, r);
                RMax = Math.Max(RMax, r);
                GMin = Math.Min(GMin, g);
                GMax = Math.Max(GMax, g);
                BMin = Math.Min(BMin, b);
                BMax = Math.Max(BMax, b);
                Pop += color.Count;
            }
        }

        public List<Bin> Colors { get; }

        public int RMin { get; private set; } = 31;

        public int RMax { get; private set; }

        public int GMin { get; private set; } = 63;

        public int GMax { get; private set; }

        public int BMin { get; private set; } = 31;

        public int BMax { get; private set; }

        public long Pop { get; private set; }

        public static Box Create(List<Bin> colors) => new(colors);

        public int SplitScore()
        {
            if (Colors.Count < 2)
                return 0;
            int r = (RMax - RMin) * 8;
            int g = (GMax - GMin) * 4;
            int b = (BMax - BMin) * 8;
            return Math.Max(r, Math.Max(g, b));
        }

        public (Box Left, Box Right) Split()
        {
            int r = (RMax - RMin) * 8;
            int g = (GMax - GMin) * 4;
            int b = (BMax - BMin) * 8;
            int channel = 1;
            int best = g;
            if (r > best)
            {
                channel = 0;
                best = r;
            }

            if (b > best)
                channel = 2;

            List<Bin> sorted = channel switch
            {
                0 => Colors.OrderBy(c => Rgb565.ChannelR(c.Value)).ThenBy(c => c.Value).ToList(),
                2 => Colors.OrderBy(c => Rgb565.ChannelB(c.Value)).ThenBy(c => c.Value).ToList(),
                _ => Colors.OrderBy(c => Rgb565.ChannelG(c.Value)).ThenBy(c => c.Value).ToList(),
            };

            long half = Pop / 2;
            long acc = 0;
            int splitAt = 1;
            for (int i = 0; i < sorted.Count - 1; i++)
            {
                acc += sorted[i].Count;
                splitAt = i + 1;
                if (acc >= half)
                    break;
            }

            return (new Box(sorted.GetRange(0, splitAt)), new Box(sorted.GetRange(splitAt, sorted.Count - splitAt)));
        }

        public ushort? Representative(HashSet<ushort> used)
        {
            long r = 0;
            long g = 0;
            long b = 0;
            long n = 0;
            foreach (Bin color in Colors)
            {
                Rgb565.Unpack(color.Value, out byte cr, out byte cg, out byte cb);
                r += (long)cr * color.Count;
                g += (long)cg * color.Count;
                b += (long)cb * color.Count;
                n += color.Count;
            }

            ushort mean = Rgb565.Pack((byte)(r / n), (byte)(g / n), (byte)(b / n));
            ushort best = 0;
            int bestDist = int.MaxValue;
            bool found = false;
            foreach (Bin color in Colors)
            {
                if (used.Contains(color.Value))
                    continue;
                int dist = Rgb565.Distance(mean, color.Value);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = color.Value;
                    found = true;
                }
            }

            return found ? best : null;
        }
    }
}
