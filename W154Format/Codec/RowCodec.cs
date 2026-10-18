namespace W154Format;

public static class RowCodec
{
    public const byte Raw = 0;
    public const byte Rle = 1;
    public const byte Residual = 2;
    public const byte Left = 3;

    public static void Append(List<byte> body, ReadOnlySpan<byte> row, ReadOnlySpan<byte> previous, bool keyframe)
    {
        byte codec = Raw;
        int dx = 0;
        byte[]? best = null;
        Consider(ref best, ref codec, ref dx, Rle, 0, BandRle.Encode(row));
        if (keyframe)
        {
            Consider(ref best, ref codec, ref dx, Left, 0, BandRle.Encode(LeftDelta(row)));
        }
        else if (previous.Length == row.Length && (best is null || best.Length > 4))
        {
            Span<byte> residual = stackalloc byte[PictureContract.RowBytes];
            for (int shift = -PictureContract.MaxShift; shift <= PictureContract.MaxShift; shift++)
            {
                FillResidual(row, previous, shift, residual);
                Consider(ref best, ref codec, ref dx, Residual, shift, BandRle.Encode(residual));
                if (best is not null && best.Length <= 4)
                    break;
            }
        }

        if (best is null)
            WriteRecord(body, Raw, 0, row);
        else
            WriteRecord(body, codec, dx, best);
    }

    public static bool TryApply(byte codec, sbyte dx, ReadOnlySpan<byte> data, Span<byte> target, ReadOnlySpan<byte> previous)
    {
        if (target.Length != PictureContract.RowBytes)
            return false;
        if (codec == Raw)
        {
            if (dx != 0 || data.Length != target.Length)
                return false;
            data.CopyTo(target);
            return true;
        }

        if (data.Length == 0 || data.Length >= target.Length)
            return false;
        if (codec == Rle)
            return dx == 0 && BandRle.TryDecode(data, target);
        if (codec == Left)
        {
            if (dx != 0 || !BandRle.TryDecode(data, target))
                return false;
            byte acc = target[0];
            for (int x = 1; x < target.Length; x++)
            {
                acc = (byte)(acc + target[x]);
                target[x] = acc;
            }

            return true;
        }

        if (codec != Residual || previous.Length != target.Length)
            return false;
        if (dx < -PictureContract.MaxShift || dx > PictureContract.MaxShift)
            return false;
        Span<byte> residual = stackalloc byte[PictureContract.RowBytes];
        if (!BandRle.TryDecode(data, residual))
            return false;
        for (int x = 0; x < target.Length; x++)
        {
            int src = x - dx;
            if (src < 0)
                src = 0;
            else if (src >= target.Length)
                src = target.Length - 1;
            target[x] = (byte)(previous[src] + residual[x]);
        }

        return true;
    }

    static void Consider(ref byte[]? best, ref byte codec, ref int dx, byte nextCodec, int nextDx, byte[] encoded)
    {
        if (encoded.Length == 0 || encoded.Length >= PictureContract.RowBytes)
            return;
        if (best is not null && encoded.Length >= best.Length)
            return;
        best = encoded;
        codec = nextCodec;
        dx = nextDx;
    }

    static byte[] LeftDelta(ReadOnlySpan<byte> row)
    {
        var dst = new byte[row.Length];
        dst[0] = row[0];
        for (int x = 1; x < row.Length; x++)
            dst[x] = (byte)(row[x] - row[x - 1]);
        return dst;
    }

    static void FillResidual(ReadOnlySpan<byte> row, ReadOnlySpan<byte> previous, int dx, Span<byte> residual)
    {
        for (int x = 0; x < row.Length; x++)
        {
            int src = x - dx;
            if (src < 0)
                src = 0;
            else if (src >= previous.Length)
                src = previous.Length - 1;
            residual[x] = (byte)(row[x] - previous[src]);
        }
    }

    static void WriteRecord(List<byte> body, byte codec, int dx, ReadOnlySpan<byte> data)
    {
        body.Add(codec);
        body.Add((byte)dx);
        body.Add((byte)data.Length);
        body.Add((byte)(data.Length >> 8));
        for (int i = 0; i < data.Length; i++)
            body.Add(data[i]);
        int pad = (4 - (data.Length & 3)) & 3;
        for (int i = 0; i < pad; i++)
            body.Add(0);
    }
}
