namespace W154Convert;

static class Lz4Block
{
    const int HashLog = 12;
    const int LastLiterals = 5;
    const int MinMatch = 4;

    public static byte[]? TryCompress(ReadOnlySpan<byte> src, int maxBytes)
    {
        var dst = new byte[maxBytes];
        int written = Encode(src, dst);
        if (written <= 0 || written > maxBytes)
            return null;
        return dst[..written];
    }

    public static bool TryDecode(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        int ip = 0;
        int op = 0;
        while (ip < src.Length)
        {
            byte token = src[ip++];
            if (!ReadLength(src, ref ip, token >> 4, out int litLen))
                return false;
            if (litLen < 0 || op + litLen > dst.Length || ip + litLen > src.Length)
                return false;
            src.Slice(ip, litLen).CopyTo(dst.Slice(op, litLen));
            ip += litLen;
            op += litLen;
            if (ip >= src.Length)
                break;
            if (ip + 2 > src.Length)
                return false;

            int offset = src[ip] | (src[ip + 1] << 8);
            ip += 2;
            if (offset <= 0 || offset > op)
                return false;
            if (!ReadLength(src, ref ip, token & 0x0F, out int matchExtra))
                return false;
            int matchLen = matchExtra + MinMatch;
            if (op + matchLen > dst.Length)
                return false;

            int match = op - offset;
            for (int i = 0; i < matchLen; i++)
                dst[op++] = dst[match++];
        }

        return op == dst.Length;
    }

    static int Encode(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        if (src.Length == 0)
            return -1;

        var hashTable = new int[1 << HashLog];
        Array.Fill(hashTable, -1);
        int ip = 0;
        int anchor = 0;
        int opIndex = 0;
        int matchLimit = src.Length - LastLiterals;

        while (ip < matchLimit)
        {
            int candidate = -1;
            int search = ip;
            while (search <= matchLimit - MinMatch)
            {
                uint hash = Hash(src, search);
                int found = hashTable[hash];
                hashTable[hash] = search;
                if (found >= 0
                    && search - found <= 65535
                    && found < search
                    && Match4(src, found, search))
                {
                    candidate = found;
                    ip = search;
                    break;
                }

                search++;
            }

            if (candidate < 0)
                break;

            int maxLen = src.Length - LastLiterals - ip;
            if (maxLen < MinMatch)
                break;

            int matchLen = MinMatch;
            while (matchLen < maxLen && src[candidate + matchLen] == src[ip + matchLen])
                matchLen++;

            int offset = ip - candidate;
            if (!WriteSequence(src, anchor, ip - anchor, offset, matchLen, dst, ref opIndex))
                return -1;

            int matchEnd = ip + matchLen;
            for (int p = ip + 1; p < matchEnd && p <= matchLimit - MinMatch; p++)
                hashTable[Hash(src, p)] = p;

            ip = matchEnd;
            anchor = ip;
        }

        if (!WriteLiterals(src, anchor, src.Length - anchor, dst, ref opIndex))
            return -1;
        return opIndex;
    }

    static bool Match4(ReadOnlySpan<byte> src, int a, int b) =>
        src[a] == src[b]
        && src[a + 1] == src[b + 1]
        && src[a + 2] == src[b + 2]
        && src[a + 3] == src[b + 3];

    static uint Hash(ReadOnlySpan<byte> src, int i)
    {
        uint value = (uint)(src[i] | (src[i + 1] << 8) | (src[i + 2] << 16) | (src[i + 3] << 24));
        return (value * 2654435761u) >> (32 - HashLog);
    }

    static bool WriteSequence(
        ReadOnlySpan<byte> src,
        int literalAt,
        int literalLen,
        int offset,
        int matchLen,
        Span<byte> dst,
        ref int op)
    {
        if (!WriteToken(dst, ref op, literalLen, matchLen - MinMatch))
            return false;
        if (!WriteExtra(dst, ref op, literalLen))
            return false;
        if (op + literalLen > dst.Length)
            return false;
        src.Slice(literalAt, literalLen).CopyTo(dst.Slice(op, literalLen));
        op += literalLen;
        if (op + 2 > dst.Length)
            return false;
        dst[op++] = (byte)offset;
        dst[op++] = (byte)(offset >> 8);
        if (!WriteExtra(dst, ref op, matchLen - MinMatch))
            return false;
        return true;
    }

    static bool WriteLiterals(ReadOnlySpan<byte> src, int at, int length, Span<byte> dst, ref int op)
    {
        if (!WriteToken(dst, ref op, length, 0))
            return false;
        if (!WriteExtra(dst, ref op, length))
            return false;
        if (op + length > dst.Length)
            return false;
        src.Slice(at, length).CopyTo(dst.Slice(op, length));
        op += length;
        return true;
    }

    static bool WriteToken(Span<byte> dst, ref int op, int literalLen, int matchCode)
    {
        if (op >= dst.Length)
            return false;
        int litNibble = Math.Min(literalLen, 15);
        int matchNibble = Math.Min(matchCode, 15);
        dst[op++] = (byte)((litNibble << 4) | matchNibble);
        return true;
    }

    static bool WriteExtra(Span<byte> dst, ref int op, int length)
    {
        if (length < 15)
            return true;
        int extra = length - 15;
        while (extra >= 255)
        {
            if (op >= dst.Length)
                return false;
            dst[op++] = 255;
            extra -= 255;
        }

        if (op >= dst.Length)
            return false;
        dst[op++] = (byte)extra;
        return true;
    }

    static bool ReadLength(ReadOnlySpan<byte> src, ref int ip, int nibble, out int length)
    {
        length = nibble;
        if (nibble != 15)
            return true;
        while (true)
        {
            if (ip >= src.Length)
                return false;
            byte extra = src[ip++];
            length += extra;
            if (extra != 255)
                return true;
        }
    }
}
