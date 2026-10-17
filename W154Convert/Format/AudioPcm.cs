namespace W154Convert;

static class AudioPcm
{
    public const int HeaderBytes = 16;
    public const int Channels = 1;
    public const int FormatS16Le = 0;
    public const int DefaultSampleRate = 48000;

    public static bool IsRate(int sampleRate) => sampleRate is 16000 or 24000 or 48000;

    public static void RequireRate(int sampleRate)
    {
        if (!IsRate(sampleRate))
            throw new W154Exception("sample_rate deve ser 16000, 24000 ou 48000.");
    }

    public static int SampleCount(int frameCount, int frameUs, int sampleRate)
    {
        RequireRate(sampleRate);
        if (frameCount < 1)
            throw new W154Exception("o vídeo não tem quadros.");
        long timelineUs = (long)frameCount * frameUs;
        long count = (timelineUs * sampleRate + 500_000L) / 1_000_000L;
        if (count < 1 || count > int.MaxValue / 2)
            throw new W154Exception("o PCM não cabe num u32.");
        return (int)count;
    }

    public static uint CrcFitted(Stream source, int pcmBytes)
    {
        long start = source.Position;
        uint crc = 0xFFFFFFFF;
        var buffer = new byte[65536];
        int left = pcmBytes;
        while (left > 0)
        {
            int want = Math.Min(buffer.Length, left);
            int got = ReadOrSilence(source, buffer, want);
            crc = Crc32.Update(crc, buffer.AsSpan(0, got));
            left -= got;
        }

        source.Position = start;
        return Crc32.Finish(crc);
    }

    public static void CopyFitted(Stream source, Stream dest, int pcmBytes)
    {
        var buffer = new byte[65536];
        int left = pcmBytes;
        while (left > 0)
        {
            int want = Math.Min(buffer.Length, left);
            int got = ReadOrSilence(source, buffer, want);
            dest.Write(buffer, 0, got);
            left -= got;
        }
    }

    static int ReadOrSilence(Stream source, byte[] buffer, int want)
    {
        int filled = 0;
        while (filled < want)
        {
            int n = source.Read(buffer, filled, want - filled);
            if (n == 0)
            {
                Array.Clear(buffer, filled, want - filled);
                return want;
            }

            filled += n;
        }

        return want;
    }
}
