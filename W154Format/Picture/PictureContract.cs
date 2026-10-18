namespace W154Format;

public static class PictureContract
{
    public const int Width = 240;
    public const int Height = 240;
    public const int Pixels = Width * Height;
    public const int RowBytes = Width;
    public const int RowCount = Height;
    public const int RowMaskBytes = 30;
    public const int PreambleBytes = 4 + RowMaskBytes;
    public const int BandRows = 1;
    public const int BandCountMax = 240;
    public const int PaletteColors = 256;
    public const int PaletteBytes = PaletteColors * 2;
    public const int HeaderSize = 32;
    public const int MaxBodyBytes = 59110;
    public const int MinBodyBytes = 38;
    public const int MinFrameUs = 12500;
    public const int MaxFrameUs = 1_000_000;
    public const int MaxFps = 80;
    public const int MaxShift = 16;
    public const int FragPayload = 1456;
    public const int FragHeader = 16;
    public const ushort Version = 2;

    public static int FrameUs(int fps)
    {
        if (fps < 1 || fps > MaxFps)
            throw new W154Exception("fps inteiro de 1 a 80.");
        return (1_000_000 + fps / 2) / fps;
    }

    public static int Gop(int frameUs) => Math.Max(1, (1_000_000 + frameUs / 2) / frameUs);

    public static bool FrameUsOk(int frameUs) => frameUs is >= MinFrameUs and <= MaxFrameUs;
}
