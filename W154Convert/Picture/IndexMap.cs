namespace W154Convert;

static class IndexMap
{
    public static byte[] Map(ReadOnlySpan<ushort> pixels, Palette palette, bool dither)
    {
        if (!dither)
        {
            var direct = new byte[PictureContract.Pixels];
            for (int i = 0; i < pixels.Length; i++)
                direct[i] = palette.Lut[pixels[i]];
            return direct;
        }

        return FloydSteinberg(pixels, palette);
    }

    static byte[] FloydSteinberg(ReadOnlySpan<ushort> pixels, Palette palette)
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
            Rgb565.Unpack(palette.Entries[i], out byte r, out byte g, out byte b);
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

            for (int x = 0; x < width; x++)
            {
                Rgb565.Unpack(pixels[(y * width) + x], out byte sr, out byte sg, out byte sb);
                int r = Clamp(sr + errR[(row * width) + x]);
                int g = Clamp(sg + errG[(row * width) + x]);
                int b = Clamp(sb + errB[(row * width) + x]);
                byte idx = palette.Lut[Rgb565.Pack((byte)r, (byte)g, (byte)b)];
                dst[(y * width) + x] = idx;
                int er = r - palR[idx];
                int eg = g - palG[idx];
                int eb = b - palB[idx];
                Diffuse(errR, errG, errB, width, row, next, x + 1, y, y, er, eg, eb, 7);
                Diffuse(errR, errG, errB, width, row, next, x - 1, y + 1, y, er, eg, eb, 3);
                Diffuse(errR, errG, errB, width, row, next, x, y + 1, y, er, eg, eb, 5);
                Diffuse(errR, errG, errB, width, row, next, x + 1, y + 1, y, er, eg, eb, 1);
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
        errR[at] += er * weight / 16;
        errG[at] += eg * weight / 16;
        errB[at] += eb * weight / 16;
    }

    static int Clamp(int value)
    {
        if (value < 0)
            return 0;
        if (value > 255)
            return 255;
        return value;
    }
}
