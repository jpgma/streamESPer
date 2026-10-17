namespace W154Convert;

static class PictureContract
{
    public const int Width = 240;
    public const int Height = 240;
    public const int Pixels = Width * Height;
    public const int BandRows = 8;
    public const int BandCount = 30;
    public const int BandBytes = Width * BandRows;
    public const int PaletteColors = 256;
    public const int PaletteBytes = PaletteColors * 2;
    public const int HeaderSize = 32;
    public const int MaxBodyBytes = 58244;
    public const int MinBodyBytes = 12;
    public const int FragPayload = 1456;
    public const int FragHeader = 16;
    public const ushort Version = 1;
    public const uint KeyframeMask = 0x3FFFFFFF;

    public static int FrameUs(int fps)
    {
        if (fps < 1 || fps > 60)
            throw new W154Exception("fps inteiro de 1 a 60.");
        return (1_000_000 + fps / 2) / fps;
    }

    public static int Gop(int frameUs) => Math.Max(1, (1_000_000 + frameUs / 2) / frameUs);
}
