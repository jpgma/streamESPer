namespace W154Format;

public static class BandRle
{
    public static byte[] Encode(ReadOnlySpan<byte> src)
    {
        var dst = new List<byte>(src.Length);
        int i = 0;
        while (i < src.Length)
        {
            int run = 1;
            while (i + run < src.Length && src[i + run] == src[i] && run < 128)
                run++;

            if (run >= 2)
            {
                dst.Add((byte)(run + 127));
                dst.Add(src[i]);
                i += run;
                continue;
            }

            int start = i;
            i++;
            while (i < src.Length && i - start < 128)
            {
                int ahead = 1;
                while (i + ahead < src.Length && src[i + ahead] == src[i] && ahead < 128)
                    ahead++;
                if (ahead >= 2)
                    break;
                i++;
            }

            int n = i - start;
            dst.Add((byte)(n - 1));
            for (int k = 0; k < n; k++)
                dst.Add(src[start + k]);
        }

        return dst.ToArray();
    }

    public static bool TryDecode(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        if (dst.Length != PictureContract.BandBytes)
            return false;

        int ip = 0;
        int op = 0;
        while (op < dst.Length)
        {
            if (ip >= src.Length)
                return false;
            byte ctl = src[ip++];
            if (ctl < 128)
            {
                int n = ctl + 1;
                if (ip + n > src.Length || op + n > dst.Length)
                    return false;
                src.Slice(ip, n).CopyTo(dst.Slice(op, n));
                ip += n;
                op += n;
            }
            else
            {
                int n = ctl - 127;
                if (ip >= src.Length || op + n > dst.Length)
                    return false;
                byte value = src[ip++];
                dst.Slice(op, n).Fill(value);
                op += n;
            }
        }

        return op == dst.Length && ip == src.Length;
    }
}
