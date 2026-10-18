namespace W154Convert;

static class IndexMap
{
    const int OrderedAmplitude = 12;

    static readonly byte[] Bayer8 =
    [
        0, 32, 8, 40, 2, 34, 10, 42,
        48, 16, 56, 24, 50, 18, 58, 26,
        12, 44, 4, 36, 14, 46, 6, 38,
        60, 28, 52, 20, 62, 30, 54, 22,
        3, 35, 11, 43, 1, 33, 9, 41,
        51, 19, 59, 27, 49, 17, 57, 25,
        15, 47, 7, 39, 13, 45, 5, 37,
        63, 31, 55, 23, 61, 29, 53, 21,
    ];

    public static byte[] Map(ReadOnlySpan<byte> rgb, Palette palette, DitherMode dither)
    {
        if (rgb.Length < PictureContract.Pixels * 3)
            throw new W154Exception("quadro RGB24 inválido.");
        if (dither == DitherMode.Fs)
            return FloydSteinberg(rgb, palette);
        if (dither == DitherMode.Ordered)
            return Ordered(rgb, palette);
        return Nearest(rgb, palette);
    }

    static byte[] Nearest(ReadOnlySpan<byte> rgb, Palette palette)
    {
        var dst = new byte[PictureContract.Pixels];
        for (int i = 0; i < PictureContract.Pixels; i++)
        {
            int p = i * 3;
            dst[i] = palette.Lut[Rgb565.Pack(rgb[p], rgb[p + 1], rgb[p + 2])];
        }

        return dst;
    }

    static byte[] Ordered(ReadOnlySpan<byte> rgb, Palette palette)
    {
        int width = PictureContract.Width;
        var dst = new byte[PictureContract.Pixels];
        for (int y = 0; y < PictureContract.Height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = (y * width) + x;
                int p = i * 3;
                int offset = (Bayer8[((y & 7) * 8) + (x & 7)] * (OrderedAmplitude * 2) / 63) - OrderedAmplitude;
                byte r = (byte)Clamp(rgb[p] + offset);
                byte g = (byte)Clamp(rgb[p + 1] + offset);
                byte b = (byte)Clamp(rgb[p + 2] + offset);
                dst[i] = palette.Lut[Rgb565.Pack(r, g, b)];
            }
        }

        return dst;
    }

    static byte[] FloydSteinberg(ReadOnlySpan<byte> rgb, Palette palette)
    {
        int width = PictureContract.Width;
        int height = PictureContract.Height;
        var dst = new byte[PictureContract.Pixels];
        var errR = new int[width * 2];
        var errG = new int[width * 2];
        var errB = new int[width * 2];
        var palR = new int[palette.Count];
        var palG = new int[palette.Count];
        var palB = new int[palette.Count];
        for (int i = 0; i < palette.Count; i++)
        {
            ColorSpace.Expand565(palette.Entries[i], out byte r, out byte g, out byte b);
            palR[i] = r;
            palG[i] = g;
            palB[i] = b;
        }

        for (int y = 0; y < height; y++)
        {
            int row = y & 1;
            int next = row ^ 1;
            Array.Clear(errR, next * width, width);
            Array.Clear(errG, next * width, width);
            Array.Clear(errB, next * width, width);
            bool forward = (y & 1) == 0;
            int x = forward ? 0 : width - 1;
            int step = forward ? 1 : -1;
            for (int n = 0; n < width; n++, x += step)
            {
                int i = (y * width) + x;
                int p = i * 3;
                int at = (row * width) + x;
                int r = Clamp(rgb[p] + errR[at]);
                int g = Clamp(rgb[p + 1] + errG[at]);
                int b = Clamp(rgb[p + 2] + errB[at]);
                byte idx = palette.Lut[Rgb565.Pack((byte)r, (byte)g, (byte)b)];
                dst[i] = idx;
                int er = r - palR[idx];
                int eg = g - palG[idx];
                int eb = b - palB[idx];
                int ahead = forward ? 1 : -1;
                Diffuse(errR, errG, errB, width, row, next, x + ahead, y, y, er, eg, eb, 7);
                Diffuse(errR, errG, errB, width, row, next, x - ahead, y + 1, y, er, eg, eb, 3);
                Diffuse(errR, errG, errB, width, row, next, x, y + 1, y, er, eg, eb, 5);
                Diffuse(errR, errG, errB, width, row, next, x + ahead, y + 1, y, er, eg, eb, 1);
            }
        }

        return dst;
    }

    static void Diffuse(
        int[] errR,
        int[] errG,
        int[] errB,
        int width,
        int row,
        int next,
        int x,
        int y,
        int currentY,
        int er,
        int eg,
        int eb,
        int weight)
    {
        if ((uint)x >= (uint)width || y != currentY && y != currentY + 1)
            return;
        int line = y == currentY ? row : next;
        int at = (line * width) + x;
        errR[at] = ClampError(errR[at] + (er * weight / 16));
        errG[at] = ClampError(errG[at] + (eg * weight / 16));
        errB[at] = ClampError(errB[at] + (eb * weight / 16));
    }

    static int Clamp(int value)
    {
        if (value < 0)
            return 0;
        if (value > 255)
            return 255;
        return value;
    }

    static int ClampError(int value)
    {
        if (value < -255)
            return -255;
        if (value > 255)
            return 255;
        return value;
    }
}
