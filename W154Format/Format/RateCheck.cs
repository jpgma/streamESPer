namespace W154Format;

public enum RateBand
{
    Comfortable,
    Intermittent,
    Stall,
}

public readonly record struct RateReport(double AverageMbps, double WorstMbps, int WorstSecond, RateBand Band);

public static class RateCheck
{
    public static int UdpBytes(int bodyBytes)
    {
        int frags = (bodyBytes + PictureContract.FragPayload - 1) / PictureContract.FragPayload;
        return bodyBytes + (PictureContract.FragHeader * frags);
    }

    public static int Fragments(int bodyBytes) =>
        (bodyBytes + PictureContract.FragPayload - 1) / PictureContract.FragPayload;

    public static RateReport Measure(int frameUs, IReadOnlyList<int> bodyBytes, int sampleRate = 0, int sampleCount = 0)
    {
        if (bodyBytes.Count == 0)
            throw new W154Exception("sem quadros para a taxa.");
        if (sampleCount > 0)
            AudioPcm.RequireRate(sampleRate);

        long total = 0;
        int lastSecond = (int)(((long)(bodyBytes.Count - 1) * frameUs) / 1_000_000L);
        if (sampleCount > 0)
        {
            int audioLast = (sampleCount - 1) / sampleRate;
            if (audioLast > lastSecond)
                lastSecond = audioLast;
        }

        var buckets = new long[lastSecond + 1];
        for (int n = 0; n < bodyBytes.Count; n++)
        {
            int udp = UdpBytes(bodyBytes[n]);
            total += udp;
            int second = (int)(((long)n * frameUs) / 1_000_000L);
            buckets[second] += udp;
        }

        if (sampleCount > 0)
        {
            int index = 0;
            while (index < sampleCount)
            {
                int second = index / sampleRate;
                int end = Math.Min(sampleCount, (second + 1) * sampleRate);
                int udp = UdpBytes((end - index) * 2);
                total += udp;
                buckets[second] += udp;
                index = end;
            }
        }

        double worst = 0;
        int worstSecond = 0;
        for (int second = 0; second < buckets.Length; second++)
        {
            double mbps = buckets[second] * 8.0 / 1_000_000.0;
            if (mbps > worst)
            {
                worst = mbps;
                worstSecond = second;
            }
        }

        double average = total * 8.0 / ((double)bodyBytes.Count * frameUs);
        RateBand band = worst <= 16 ? RateBand.Comfortable
            : worst <= 21 ? RateBand.Intermittent
            : RateBand.Stall;
        return new RateReport(average, worst, worstSecond, band);
    }
}
