namespace W154Convert;

static class Crc32
{
    static readonly uint[] Table = BuildTable();

    public static uint Compute(ReadOnlySpan<byte> data) => Finish(Update(0xFFFFFFFF, data));

    public static uint Update(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (byte b in data)
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    public static uint Finish(uint crc) => crc ^ 0xFFFFFFFF;

    static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint crc = i;
            for (int bit = 0; bit < 8; bit++)
            {
                if ((crc & 1) != 0)
                    crc = (crc >> 1) ^ 0xEDB88320;
                else
                    crc >>= 1;
            }

            table[i] = crc;
        }

        return table;
    }
}
