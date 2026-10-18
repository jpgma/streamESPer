namespace W154Format;

public static class ImaAdpcm
{
    public const int SamplesPerBlock = 505;
    public const int FullBlockBytes = 256;

    static readonly int[] IndexTable =
    [
        -1, -1, -1, -1, 2, 4, 6, 8,
        -1, -1, -1, -1, 2, 4, 6, 8,
    ];

    static readonly int[] StepTable =
    [
        7, 8, 9, 10, 11, 12, 13, 14, 16, 17,
        19, 21, 23, 25, 28, 31, 34, 37, 41, 45,
        50, 55, 60, 66, 73, 80, 88, 97, 107, 118,
        130, 143, 157, 173, 190, 209, 230, 253, 279, 307,
        337, 371, 408, 449, 494, 544, 598, 658, 724, 796,
        876, 963, 1060, 1166, 1282, 1411, 1552, 1707, 1878, 2066,
        2272, 2499, 2749, 3024, 3327, 3660, 4026, 4428, 4871, 5358,
        5894, 6484, 7132, 7845, 8630, 9493, 10442, 11487, 12635, 13899,
        15289, 16818, 18500, 20350, 22385, 24623, 27086, 29794, 32767,
    ];

    public static int PayloadBytes(int sampleCount)
    {
        if (sampleCount < 1)
            throw new W154Exception("o ADPCM não tem amostras.");
        int full = sampleCount / SamplesPerBlock;
        int rem = sampleCount % SamplesPerBlock;
        return (full * FullBlockBytes) + (rem == 0 ? 0 : BlockBytes(rem));
    }

    public static byte[] Encode(ReadOnlySpan<byte> s16le)
    {
        if ((s16le.Length & 1) != 0)
            throw new W154Exception("PCM s16le com tamanho ímpar.");
        int sampleCount = s16le.Length / 2;
        var dst = new byte[PayloadBytes(sampleCount)];
        int stepIndex = 0;
        int at = 0;
        int op = 0;
        while (at < sampleCount)
        {
            int count = Math.Min(SamplesPerBlock, sampleCount - at);
            int predictor = Sample(s16le, at);
            dst[op] = (byte)predictor;
            dst[op + 1] = (byte)(predictor >> 8);
            dst[op + 2] = (byte)stepIndex;
            dst[op + 3] = 0;
            int nibbleAt = op + 4;
            bool high = false;
            for (int i = 1; i < count; i++)
            {
                byte nibble = EncodeNibble(ref predictor, ref stepIndex, Sample(s16le, at + i));
                if (!high)
                {
                    dst[nibbleAt] = nibble;
                    high = true;
                }
                else
                {
                    dst[nibbleAt] |= (byte)(nibble << 4);
                    nibbleAt++;
                    high = false;
                }
            }

            op += BlockBytes(count);
            at += count;
        }

        return dst;
    }

    public static bool Accepts(ReadOnlySpan<byte> payload, int sampleCount)
    {
        if (sampleCount < 1 || payload.Length != PayloadBytes(sampleCount))
            return false;
        int at = 0;
        int op = 0;
        while (at < sampleCount)
        {
            int count = Math.Min(SamplesPerBlock, sampleCount - at);
            int size = BlockBytes(count);
            if (op + size > payload.Length)
                return false;
            if (payload[op + 2] > 88 || payload[op + 3] != 0)
                return false;
            op += size;
            at += count;
        }

        return op == payload.Length;
    }

    public static void DecodeAt(ReadOnlySpan<byte> payload, int sampleCount, int sampleIndex, Span<byte> s16le)
    {
        if ((s16le.Length & 1) != 0 || sampleIndex < 0)
            throw new W154Exception("faixa de PCM inválida.");
        int want = s16le.Length / 2;
        if (want == 0 || sampleIndex + want > sampleCount)
            throw new W154Exception("faixa de PCM passa das amostras.");
        if (!Accepts(payload, sampleCount))
            throw new W154Exception("bloco ADPCM inválido.");

        Span<short> block = stackalloc short[SamplesPerBlock];
        int filled = 0;
        while (filled < want)
        {
            int abs = sampleIndex + filled;
            int blockIndex = abs / SamplesPerBlock;
            int local = abs % SamplesPerBlock;
            int count = Math.Min(SamplesPerBlock, sampleCount - (blockIndex * SamplesPerBlock));
            DecodeBlock(payload.Slice(blockIndex * FullBlockBytes, BlockBytes(count)), count, block);
            int take = Math.Min(count - local, want - filled);
            for (int i = 0; i < take; i++)
            {
                short sample = block[local + i];
                int o = (filled + i) * 2;
                s16le[o] = (byte)sample;
                s16le[o + 1] = (byte)(sample >> 8);
            }

            filled += take;
        }
    }

    public static byte[] DecodeAll(ReadOnlySpan<byte> payload, int sampleCount)
    {
        var dst = new byte[sampleCount * 2];
        DecodeAt(payload, sampleCount, 0, dst);
        return dst;
    }

    static int BlockBytes(int samples)
    {
        int raw = 4 + (samples / 2);
        return (raw + 3) & ~3;
    }

    static void DecodeBlock(ReadOnlySpan<byte> block, int samples, Span<short> dst)
    {
        int predictor = (short)(block[0] | (block[1] << 8));
        int stepIndex = block[2];
        dst[0] = (short)predictor;
        int ip = 4;
        bool high = false;
        for (int i = 1; i < samples; i++)
        {
            int nibble = high ? block[ip++] >> 4 : block[ip] & 0x0F;
            high = !high;
            predictor = DecodeNibble(predictor, ref stepIndex, nibble);
            dst[i] = (short)predictor;
        }
    }

    static int Sample(ReadOnlySpan<byte> pcm, int index)
    {
        int o = index * 2;
        return (short)(pcm[o] | (pcm[o + 1] << 8));
    }

    static byte EncodeNibble(ref int predictor, ref int stepIndex, int sample)
    {
        int step = StepTable[stepIndex];
        int diff = sample - predictor;
        int nibble = 0;
        if (diff < 0)
        {
            nibble = 8;
            diff = -diff;
        }

        int vpdiff = step >> 3;
        if (diff >= step)
        {
            nibble |= 4;
            diff -= step;
            vpdiff += step;
        }

        step >>= 1;
        if (diff >= step)
        {
            nibble |= 2;
            diff -= step;
            vpdiff += step;
        }

        step >>= 1;
        if (diff >= step)
        {
            nibble |= 1;
            vpdiff += step;
        }

        if ((nibble & 8) != 0)
            predictor -= vpdiff;
        else
            predictor += vpdiff;
        predictor = Clamp(predictor);
        stepIndex += IndexTable[nibble];
        if (stepIndex < 0)
            stepIndex = 0;
        else if (stepIndex > 88)
            stepIndex = 88;
        return (byte)nibble;
    }

    static int DecodeNibble(int predictor, ref int stepIndex, int nibble)
    {
        int step = StepTable[stepIndex];
        int diff = step >> 3;
        if ((nibble & 4) != 0)
            diff += step;
        if ((nibble & 2) != 0)
            diff += step >> 1;
        if ((nibble & 1) != 0)
            diff += step >> 2;
        if ((nibble & 8) != 0)
            predictor -= diff;
        else
            predictor += diff;
        predictor = Clamp(predictor);
        stepIndex += IndexTable[nibble & 15];
        if (stepIndex < 0)
            stepIndex = 0;
        else if (stepIndex > 88)
            stepIndex = 88;
        return predictor;
    }

    static int Clamp(int value)
    {
        if (value > 32767)
            return 32767;
        if (value < -32768)
            return -32768;
        return value;
    }
}
