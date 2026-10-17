namespace W154Format;

public static class Rgb565
{
    public static ushort Pack(byte r, byte g, byte b) =>
        (ushort)(((r >> 3) << 11) | ((g >> 2) << 5) | (b >> 3));

    public static void Unpack(ushort value, out byte r, out byte g, out byte b)
    {
        int r5 = (value >> 11) & 31;
        int g6 = (value >> 5) & 63;
        int b5 = value & 31;
        r = (byte)(r5 << 3);
        g = (byte)(g6 << 2);
        b = (byte)(b5 << 3);
    }

    public static int ChannelR(ushort value) => (value >> 11) & 31;

    public static int ChannelG(ushort value) => (value >> 5) & 63;

    public static int ChannelB(ushort value) => value & 31;

    public static int Distance(ushort a, ushort b)
    {
        Unpack(a, out byte ar, out byte ag, out byte ab);
        Unpack(b, out byte br, out byte bg, out byte bb);
        int dr = ar - br;
        int dg = ag - bg;
        int db = ab - bb;
        return (2 * dr * dr) + (4 * dg * dg) + (3 * db * db);
    }
}
