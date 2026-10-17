namespace W154Convert;

static class BandCodec
{
    public const byte Raw = 0;
    public const byte Rle = 1;
    public const byte Lz4 = 2;

    public static void Append(List<byte> body, ReadOnlySpan<byte> band)
    {
        byte[]? rle = null;
        byte[] encoded = BandRle.Encode(band);
        if (encoded.Length < PictureContract.BandBytes)
            rle = encoded;

        byte[]? lz4 = Lz4Block.TryCompress(band, PictureContract.BandBytes - 1);
        byte codec;
        byte[] data;
        if (rle is null && lz4 is null)
        {
            codec = Raw;
            data = band.ToArray();
        }
        else if (lz4 is null || rle is not null && rle.Length <= lz4.Length)
        {
            codec = Rle;
            data = rle!;
        }
        else
        {
            codec = Lz4;
            data = lz4;
        }

        body.Add(codec);
        body.Add(0);
        body.Add((byte)data.Length);
        body.Add((byte)(data.Length >> 8));
        body.AddRange(data);
        int pad = (4 - (data.Length & 3)) & 3;
        for (int i = 0; i < pad; i++)
            body.Add(0);
    }
}
