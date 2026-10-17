using System.Diagnostics;
using System.Text;

namespace W154Convert;

static class FfmpegDecoder
{
    public readonly record struct VideoSize(int Width, int Height, double? DurationSeconds);

    public static VideoSize Probe(string ffmpeg, string input)
    {
        var start = new ProcessStartInfo
        {
            FileName = ffmpeg,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("-hide_banner");
        start.ArgumentList.Add("-i");
        start.ArgumentList.Add(input);

        using var process = Process.Start(start) ?? throw new W154Exception("não foi possível iniciar o ffmpeg.");
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        int width = 0;
        int height = 0;
        double? duration = null;
        foreach (string line in stderr.Split('\n'))
        {
            if (width == 0 && TryParseSize(line, out int w, out int h))
            {
                width = w;
                height = h;
            }

            if (duration is null && TryParseDuration(line, out double seconds))
                duration = seconds;
        }

        if (width > 0 && height > 0)
        {
            if (width > 16384 || height > 16384)
                throw new W154Exception($"quadro {width}x{height} passa do limite de 16384.");
            return new VideoSize(width, height, duration);
        }

        throw new W154Exception("o ffmpeg não achou um stream de vídeo nesse arquivo.");
    }

    public static void ReadFrames(string ffmpeg, string input, int fps, VideoSize size, Action<ReadOnlySpan<byte>> onFrame)
    {
        var start = new ProcessStartInfo
        {
            FileName = ffmpeg,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("-hide_banner");
        start.ArgumentList.Add("-loglevel");
        start.ArgumentList.Add("error");
        start.ArgumentList.Add("-i");
        start.ArgumentList.Add(input);
        start.ArgumentList.Add("-map");
        start.ArgumentList.Add("0:v:0");
        start.ArgumentList.Add("-vf");
        start.ArgumentList.Add($"fps={fps}");
        start.ArgumentList.Add("-f");
        start.ArgumentList.Add("rawvideo");
        start.ArgumentList.Add("-pix_fmt");
        start.ArgumentList.Add("rgb24");
        start.ArgumentList.Add("pipe:1");

        using var process = Process.Start(start) ?? throw new W154Exception("não foi possível iniciar o ffmpeg.");
        var stderr = new StringBuilder();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();
        Stream stdout = process.StandardOutput.BaseStream;
        int frameBytes = checked(size.Width * size.Height * 3);
        var buffer = new byte[frameBytes];
        try
        {
            while (true)
            {
                int read = 0;
                while (read < frameBytes)
                {
                    int n = stdout.Read(buffer, read, frameBytes - read);
                    if (n == 0)
                        break;
                    read += n;
                }

                if (read == 0)
                    break;
                if (read != frameBytes)
                    throw new W154Exception("o ffmpeg terminou no meio de um quadro.");
                onFrame(buffer);
            }
        }
        finally
        {
            if (!process.HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }
            }

            process.WaitForExit();
            stderr.Append(stderrTask.GetAwaiter().GetResult());
        }

        if (process.ExitCode != 0)
        {
            string detail = stderr.ToString().Trim();
            throw new W154Exception(string.IsNullOrEmpty(detail)
                ? $"ffmpeg saiu com código {process.ExitCode}."
                : $"ffmpeg saiu com código {process.ExitCode}: {detail}");
        }
    }

    static bool TryParseSize(string line, out int width, out int height)
    {
        width = 0;
        height = 0;
        int video = line.IndexOf("Video:", StringComparison.Ordinal);
        if (video < 0)
            return false;

        for (int i = video; i < line.Length; i++)
        {
            if (!char.IsAsciiDigit(line[i]))
                continue;
            int j = i;
            while (j < line.Length && char.IsAsciiDigit(line[j]))
                j++;
            if (j >= line.Length || line[j] != 'x')
            {
                i = Math.Max(j, i);
                continue;
            }

            int k = j + 1;
            if (k >= line.Length || !char.IsAsciiDigit(line[k]))
                continue;
            int end = k;
            while (end < line.Length && char.IsAsciiDigit(line[end]))
                end++;
            width = ParsePositive(line.AsSpan(i, j - i));
            height = ParsePositive(line.AsSpan(k, end - k));
            if (width > 0 && height > 0)
                return true;
            i = end;
        }

        return false;
    }

    static bool TryParseDuration(string line, out double seconds)
    {
        seconds = 0;
        const string marker = "Duration:";
        int at = line.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0)
            return false;
        int i = at + marker.Length;
        while (i < line.Length && line[i] == ' ')
            i++;
        if (i >= line.Length || line.AsSpan(i).StartsWith("N/A", StringComparison.Ordinal))
            return false;
        int end = i;
        while (end < line.Length && line[end] != ',')
            end++;
        return TryParseClock(line.AsSpan(i, end - i), out seconds);
    }

    static bool TryParseClock(ReadOnlySpan<char> token, out double seconds)
    {
        seconds = 0;
        int first = token.IndexOf(':');
        if (first <= 0 || first + 1 >= token.Length)
            return false;
        int secondRel = token[(first + 1)..].IndexOf(':');
        if (secondRel < 0)
            return false;
        int second = first + 1 + secondRel;
        if (!int.TryParse(token[..first], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int hours))
            return false;
        if (!int.TryParse(token[(first + 1)..second], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int minutes))
            return false;
        if (!double.TryParse(token[(second + 1)..], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double secs))
            return false;
        if (hours < 0 || minutes is < 0 or > 59 || secs is < 0 or >= 60)
            return false;
        seconds = (hours * 3600d) + (minutes * 60d) + secs;
        return true;
    }

    static int ParsePositive(ReadOnlySpan<char> text)
    {
        int value = 0;
        foreach (char c in text)
        {
            value = (value * 10) + (c - '0');
            if (value < 0)
                return 0;
        }

        return value;
    }
}
