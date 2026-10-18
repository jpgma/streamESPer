namespace W154Convert;

static class ColorSpace
{
    static readonly float[] Lab = BuildLab();

    public static void Expand565(ushort value, out byte r, out byte g, out byte b)
    {
        int r5 = (value >> 11) & 31;
        int g6 = (value >> 5) & 63;
        int b5 = value & 31;
        r = (byte)((r5 << 3) | (r5 >> 2));
        g = (byte)((g6 << 2) | (g6 >> 4));
        b = (byte)((b5 << 3) | (b5 >> 2));
    }

    public static void Oklab(ushort rgb565, out float l, out float a, out float b)
    {
        int i = rgb565 * 3;
        l = Lab[i];
        a = Lab[i + 1];
        b = Lab[i + 2];
    }

    public static float Distance(int left, int right)
    {
        int i = left * 3;
        int j = right * 3;
        float dl = Lab[i] - Lab[j];
        float da = Lab[i + 1] - Lab[j + 1];
        float db = Lab[i + 2] - Lab[j + 2];
        return (dl * dl) + (da * da) + (db * db);
    }

    public static float DistanceTo(float l, float a, float b, int rgb565)
    {
        int i = rgb565 * 3;
        float dl = l - Lab[i];
        float da = a - Lab[i + 1];
        float db = b - Lab[i + 2];
        return (dl * dl) + (da * da) + (db * db);
    }

    static float[] BuildLab()
    {
        var lab = new float[65536 * 3];
        for (int color = 0; color < 65536; color++)
        {
            Expand565((ushort)color, out byte r8, out byte g8, out byte b8);
            float r = Linear(r8 / 255f);
            float g = Linear(g8 / 255f);
            float b = Linear(b8 / 255f);
            float lmsL = (0.4122214708f * r) + (0.5363325363f * g) + (0.0514459929f * b);
            float lmsM = (0.2119034982f * r) + (0.6806995451f * g) + (0.1073969566f * b);
            float lmsS = (0.0883024619f * r) + (0.2817188376f * g) + (0.6299787005f * b);
            if (lmsL < 0)
                lmsL = 0;
            if (lmsM < 0)
                lmsM = 0;
            if (lmsS < 0)
                lmsS = 0;
            float lC = MathF.Cbrt(lmsL);
            float mC = MathF.Cbrt(lmsM);
            float sC = MathF.Cbrt(lmsS);
            int i = color * 3;
            lab[i] = (0.2104542553f * lC) + (0.7936177850f * mC) - (0.0040720468f * sC);
            lab[i + 1] = (1.9779984951f * lC) - (2.4285922050f * mC) + (0.4505937099f * sC);
            lab[i + 2] = (0.0259040371f * lC) + (0.7827717662f * mC) - (0.8086757660f * sC);
        }

        return lab;
    }

    static float Linear(float channel)
    {
        if (channel <= 0.04045f)
            return channel / 12.92f;
        return MathF.Pow((channel + 0.055f) / 1.055f, 2.4f);
    }
}
