namespace W154Convert;

sealed class Palette
{
    public Palette(ushort[] entries, int count, byte[] lut, float error)
    {
        Entries = entries;
        Count = count;
        Lut = lut;
        Error = error;
    }

    public ushort[] Entries { get; }

    public int Count { get; }

    public byte[] Lut { get; }

    public float Error { get; }
}

static class Quantizer
{
    const int Passes = 4;
    const float SeedMargin = 1.10f;

    readonly struct Bin
    {
        public Bin(ushort value, int count, float l, float a, float b)
        {
            Value = value;
            Count = count;
            L = l;
            A = a;
            B = b;
        }

        public ushort Value { get; }

        public int Count { get; }

        public float L { get; }

        public float A { get; }

        public float B { get; }
    }

    public static Palette Build(IReadOnlyList<byte[]> frames, bool reserveBlack, Palette? previous)
    {
        var hist = new int[65536];
        int pixels = PictureContract.Pixels;
        foreach (byte[] frame in frames)
        {
            if (frame.Length < pixels * 3)
                throw new W154Exception("quadro RGB24 inválido.");
            for (int i = 0; i < pixels; i++)
            {
                int p = i * 3;
                hist[Rgb565.Pack(frame[p], frame[p + 1], frame[p + 2])]++;
            }
        }

        int free = reserveBlack ? PictureContract.PaletteColors - 1 : PictureContract.PaletteColors;
        var colors = new List<Bin>(1024);
        for (int i = 0; i < hist.Length; i++)
        {
            if (hist[i] == 0)
                continue;
            if (reserveBlack && i == 0)
                continue;
            ColorSpace.Oklab((ushort)i, out float l, out float a, out float b);
            colors.Add(new Bin((ushort)i, hist[i], l, a, b));
        }

        if (colors.Count == 0)
            return Finish(Array.Empty<ushort>(), reserveBlack, colors);

        if (colors.Count <= free)
        {
            ushort[] exact = colors.Select(c => c.Value).ToArray();
            Array.Sort(exact);
            return Finish(exact, reserveBlack, colors);
        }

        Centroids start = MedianCut(colors, free, reserveBlack);
        Palette fresh = Refine(colors, start, reserveBlack);
        if (previous is null)
            return fresh;

        float kept = ErrorOf(previous.Entries, previous.Count, colors);
        if (kept <= fresh.Error * SeedMargin)
            return Adopt(previous, kept);

        Centroids seeded = Seed(previous, start, reserveBlack);
        Palette fromSeed = Refine(colors, seeded, reserveBlack);
        if (fromSeed.Error <= fresh.Error * SeedMargin)
            return fromSeed;
        return fresh;
    }

    static Palette Adopt(Palette previous, float error)
    {
        var entries = new ushort[PictureContract.PaletteColors];
        previous.Entries.CopyTo(entries, 0);
        var lut = new byte[previous.Lut.Length];
        previous.Lut.CopyTo(lut, 0);
        return new Palette(entries, previous.Count, lut, error);
    }

    static Palette Finish(ushort[] chosen, bool reserveBlack, List<Bin> colors)
    {
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

        return new Palette(entries, count, BuildLut(entries, count), ErrorOf(entries, count, colors));
    }

    static Centroids MedianCut(List<Bin> colors, int free, bool reserveBlack)
    {
        var boxes = new List<Box> { Box.Create(colors) };
        while (boxes.Count < free)
        {
            int best = -1;
            float bestError = 0;
            for (int i = 0; i < boxes.Count; i++)
            {
                float error = boxes[i].Error;
                if (error > bestError)
                {
                    bestError = error;
                    best = i;
                }
            }

            if (best < 0)
                break;
            if (!boxes[best].TrySplit(out Box? left, out Box? right) || left is null || right is null)
            {
                boxes[best].Freeze();
                continue;
            }

            boxes[best] = left;
            boxes.Add(right);
        }

        int offset = reserveBlack ? 1 : 0;
        int n = offset + boxes.Count;
        var l = new float[n];
        var a = new float[n];
        var b = new float[n];
        var pinned = new bool[n];
        if (reserveBlack)
        {
            ColorSpace.Oklab(0, out l[0], out a[0], out b[0]);
            pinned[0] = true;
        }

        for (int i = 0; i < boxes.Count; i++)
        {
            boxes[i].Mean(out l[offset + i], out a[offset + i], out b[offset + i]);
        }

        return new Centroids(l, a, b, pinned);
    }

    static Centroids Seed(Palette previous, Centroids start, bool reserveBlack)
    {
        int cap = PictureContract.PaletteColors;
        var l = new List<float>(cap);
        var a = new List<float>(cap);
        var b = new List<float>(cap);
        var pinned = new List<bool>(cap);
        if (reserveBlack)
        {
            ColorSpace.Oklab(0, out float bl, out float ba, out float bb);
            l.Add(bl);
            a.Add(ba);
            b.Add(bb);
            pinned.Add(true);
        }

        for (int i = 0; i < previous.Count && l.Count < cap; i++)
        {
            ushort color = previous.Entries[i];
            if (reserveBlack && color == 0)
                continue;
            ColorSpace.Oklab(color, out float cl, out float ca, out float cb);
            l.Add(cl);
            a.Add(ca);
            b.Add(cb);
            pinned.Add(false);
        }

        for (int i = 0; i < start.L.Length && l.Count < cap; i++)
        {
            if (start.Pinned[i])
                continue;
            l.Add(start.L[i]);
            a.Add(start.A[i]);
            b.Add(start.B[i]);
            pinned.Add(false);
        }

        return new Centroids(l.ToArray(), a.ToArray(), b.ToArray(), pinned.ToArray());
    }

    static Palette Refine(List<Bin> colors, Centroids seed, bool reserveBlack)
    {
        float[] l = (float[])seed.L.Clone();
        float[] a = (float[])seed.A.Clone();
        float[] b = (float[])seed.B.Clone();
        bool[] pinned = seed.Pinned;
        int n = l.Length;
        var assign = new int[colors.Count];
        var binError = new float[colors.Count];
        var sumL = new double[n];
        var sumA = new double[n];
        var sumB = new double[n];
        var weight = new long[n];

        for (int pass = 0; pass < Passes; pass++)
        {
            Assign(colors, l, a, b, assign, binError);
            Array.Clear(sumL);
            Array.Clear(sumA);
            Array.Clear(sumB);
            Array.Clear(weight);
            for (int i = 0; i < colors.Count; i++)
            {
                int slot = assign[i];
                Bin color = colors[i];
                sumL[slot] += color.L * color.Count;
                sumA[slot] += color.A * color.Count;
                sumB[slot] += color.B * color.Count;
                weight[slot] += color.Count;
            }

            var taken = new bool[colors.Count];
            for (int slot = 0; slot < n; slot++)
            {
                if (pinned[slot])
                    continue;
                if (weight[slot] > 0)
                {
                    l[slot] = (float)(sumL[slot] / weight[slot]);
                    a[slot] = (float)(sumA[slot] / weight[slot]);
                    b[slot] = (float)(sumB[slot] / weight[slot]);
                    continue;
                }

                int worst = WorstBin(binError, taken);
                if (worst < 0)
                    continue;
                taken[worst] = true;
                l[slot] = colors[worst].L;
                a[slot] = colors[worst].A;
                b[slot] = colors[worst].B;
            }
        }

        Assign(colors, l, a, b, assign, binError);
        ushort[] chosen = Snap(colors, l, a, b, pinned, assign);
        return Finish(chosen, reserveBlack, colors);
    }

    static void Assign(List<Bin> colors, float[] l, float[] a, float[] b, int[] assign, float[] binError)
    {
        int n = l.Length;
        for (int i = 0; i < colors.Count; i++)
        {
            Bin color = colors[i];
            int best = 0;
            float bestDist = float.MaxValue;
            for (int slot = 0; slot < n; slot++)
            {
                float dist = ColorSpace.DistanceTo(l[slot], a[slot], b[slot], color.Value);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = slot;
                    if (dist == 0)
                        break;
                }
            }

            assign[i] = best;
            binError[i] = bestDist * color.Count;
        }
    }

    static int WorstBin(float[] binError, bool[] taken)
    {
        int worst = -1;
        float worstScore = -1;
        for (int i = 0; i < binError.Length; i++)
        {
            if (taken[i] || binError[i] <= worstScore)
                continue;
            worstScore = binError[i];
            worst = i;
        }

        return worst;
    }

    static ushort[] Snap(List<Bin> colors, float[] l, float[] a, float[] b, bool[] pinned, int[] assign)
    {
        var used = new HashSet<ushort>();
        var picked = new List<ushort>();
        for (int slot = 0; slot < l.Length; slot++)
        {
            if (pinned[slot])
                continue;
            int best = -1;
            float bestDist = float.MaxValue;
            for (int i = 0; i < colors.Count; i++)
            {
                if (assign[i] != slot)
                    continue;
                float dist = ColorSpace.DistanceTo(l[slot], a[slot], b[slot], colors[i].Value);
                if (dist < bestDist || (dist == bestDist && (best < 0 || colors[i].Value < colors[best].Value)))
                {
                    bestDist = dist;
                    best = i;
                }
            }

            if (best < 0)
                continue;
            if (used.Add(colors[best].Value))
                picked.Add(colors[best].Value);
        }

        picked.Sort();
        return picked.ToArray();
    }

    static float ErrorOf(ushort[] entries, int count, List<Bin> colors)
    {
        double sse = 0;
        foreach (Bin color in colors)
        {
            float best = float.MaxValue;
            for (int i = 0; i < count; i++)
            {
                float dist = ColorSpace.Distance(color.Value, entries[i]);
                if (dist < best)
                {
                    best = dist;
                    if (dist == 0)
                        break;
                }
            }

            if (best < float.MaxValue)
                sse += best * color.Count;
        }

        return sse <= 0 ? 0 : (float)sse;
    }

    static byte[] BuildLut(ushort[] entries, int count)
    {
        var lut = new byte[65536];
        var l = new float[count];
        var a = new float[count];
        var b = new float[count];
        for (int i = 0; i < count; i++)
            ColorSpace.Oklab(entries[i], out l[i], out a[i], out b[i]);

        for (int color = 0; color < 65536; color++)
        {
            int best = 0;
            float bestDist = float.MaxValue;
            for (int i = 0; i < count; i++)
            {
                float dist = ColorSpace.DistanceTo(l[i], a[i], b[i], color);
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

    readonly struct Centroids
    {
        public Centroids(float[] l, float[] a, float[] b, bool[] pinned)
        {
            L = l;
            A = a;
            B = b;
            Pinned = pinned;
        }

        public float[] L { get; }

        public float[] A { get; }

        public float[] B { get; }

        public bool[] Pinned { get; }
    }

    sealed class Box
    {
        Box(List<Bin> colors)
        {
            Colors = colors;
            foreach (Bin color in colors)
            {
                double n = color.Count;
                SumL += color.L * n;
                SumA += color.A * n;
                SumB += color.B * n;
                SumLL += color.L * color.L * n;
                SumAA += color.A * color.A * n;
                SumBB += color.B * color.B * n;
                Pop += color.Count;
            }
        }

        public List<Bin> Colors { get; }

        public double SumL { get; }

        public double SumA { get; }

        public double SumB { get; }

        public double SumLL { get; }

        public double SumAA { get; }

        public double SumBB { get; }

        public long Pop { get; }

        public float Error { get; private set; }

        public static Box Create(List<Bin> colors)
        {
            var box = new Box(colors);
            box.Error = box.Spread();
            return box;
        }

        public void Freeze() => Error = 0;

        public void Mean(out float l, out float a, out float b)
        {
            if (Pop <= 0)
            {
                l = 0;
                a = 0;
                b = 0;
                return;
            }

            l = (float)(SumL / Pop);
            a = (float)(SumA / Pop);
            b = (float)(SumB / Pop);
        }

        public bool TrySplit(out Box? left, out Box? right)
        {
            left = null;
            right = null;
            if (Colors.Count < 2 || Pop < 2)
                return false;

            double varL = Variance(SumL, SumLL);
            double varA = Variance(SumA, SumAA);
            double varB = Variance(SumB, SumBB);
            int channel = 1;
            double best = varA;
            if (varL > best)
            {
                channel = 0;
                best = varL;
            }

            if (varB > best)
                channel = 2;
            if (best <= 0)
                return false;

            List<Bin> sorted = channel switch
            {
                0 => Colors.OrderBy(c => c.L).ThenBy(c => c.Value).ToList(),
                2 => Colors.OrderBy(c => c.B).ThenBy(c => c.Value).ToList(),
                _ => Colors.OrderBy(c => c.A).ThenBy(c => c.Value).ToList(),
            };

            double accL = 0;
            double accA = 0;
            double accB = 0;
            double accLL = 0;
            double accAA = 0;
            double accBB = 0;
            long accN = 0;
            double bestSplit = double.MaxValue;
            int splitAt = -1;
            for (int i = 0; i < sorted.Count - 1; i++)
            {
                Bin color = sorted[i];
                double n = color.Count;
                accL += color.L * n;
                accA += color.A * n;
                accB += color.B * n;
                accLL += color.L * color.L * n;
                accAA += color.A * color.A * n;
                accBB += color.B * color.B * n;
                accN += color.Count;
                long rightN = Pop - accN;
                if (accN == 0 || rightN == 0)
                    continue;
                double split = Spread(accL, accA, accB, accLL, accAA, accBB, accN)
                    + Spread(SumL - accL, SumA - accA, SumB - accB, SumLL - accLL, SumAA - accAA, SumBB - accBB, rightN);
                if (split < bestSplit)
                {
                    bestSplit = split;
                    splitAt = i + 1;
                }
            }

            if (splitAt <= 0 || splitAt >= sorted.Count)
                return false;

            left = Create(sorted.GetRange(0, splitAt));
            right = Create(sorted.GetRange(splitAt, sorted.Count - splitAt));
            return true;
        }

        float Spread() => (float)Spread(SumL, SumA, SumB, SumLL, SumAA, SumBB, Pop);

        double Variance(double sum, double sumSq) => Spread(sum, 0, 0, sumSq, 0, 0, Pop);

        static double Spread(double sumL, double sumA, double sumB, double sumLL, double sumAA, double sumBB, long pop)
        {
            if (pop <= 0)
                return 0;
            double inv = 1.0 / pop;
            double error = (sumLL - (sumL * sumL * inv))
                + (sumAA - (sumA * sumA * inv))
                + (sumBB - (sumB * sumB * inv));
            return error <= 1e-12 ? 0 : error;
        }
    }
}
