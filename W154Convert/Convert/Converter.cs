namespace W154Convert;

readonly record struct ConvertRequest(
    string Input,
    string Output,
    int Fps,
    FitMode Fit,
    DitherMode Dither,
    bool AllowOverBudget,
    int SampleRate,
    bool IncludeAudio);

readonly record struct RatePass(int Colors, float HoldScale, bool HoldFs);

static class Converter
{
    static readonly RatePass[] Ladder =
    [
        new(256, 1f, false),
        new(256, 4f, true),
        new(256, 16f, true),
        new(128, 16f, true),
        new(64, 16f, true),
        new(32, 16f, true),
    ];

    public static int Run(ConvertRequest request, TextWriter log)
    {
        int frameUs = PictureContract.FrameUs(request.Fps);
        int gop = PictureContract.Gop(frameUs);
        string ffmpeg = FfmpegLocator.Find();
        FfmpegDecoder.VideoSize size = FfmpegDecoder.Probe(ffmpeg, request.Input);
        log.WriteLine(
            $"fonte {size.Width}x{size.Height}, {request.Fps} fps, frame_us {frameUs}, gop {gop}, {request.Fit.ToString().ToLowerInvariant()}");

        int sampleRate = 0;
        string? pcmPath = null;
        FileStream? pcm = null;
        try
        {
            if (request.IncludeAudio && size.HasAudio)
            {
                sampleRate = request.SampleRate;
                pcmPath = Path.Combine(Path.GetTempPath(), "w154-pcm-" + Guid.NewGuid().ToString("N") + ".s16le");
                FfmpegDecoder.ReadPcm(ffmpeg, request.Input, sampleRate, pcmPath);
                pcm = new FileStream(pcmPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            }
            else if (request.IncludeAudio)
            {
                log.WriteLine("a fonte não tem áudio. O arquivo fica só com vídeo.");
            }
            else
            {
                log.WriteLine("sem áudio");
            }

            float step = Quantizer.StepDistance();
            RateReport rate = default;
            int[] lengths = [];
            int sampleCount = 0;
            for (int pass = 0; pass < Ladder.Length; pass++)
            {
                RatePass tune = Ladder[pass];
                log.WriteLine($"passe {pass + 1}: {tune.Colors} cores, hold {tune.HoldScale.ToString("0", System.Globalization.CultureInfo.InvariantCulture)}");
                var progress = new ConvertProgress(log, EstimateFrames(size.DurationSeconds, request.Fps));
                using var draft = new W154Draft();
                byte[]? previous = null;
                Palette? carried = null;
                int index = 0;
                var gopFrames = new List<byte[]>(gop);
                void Flush()
                {
                    if (gopFrames.Count == 0)
                        return;
                    EncodeGop(gopFrames, index, gop, request.Fit == FitMode.Contain, request.Dither, tune, step, ref previous, ref carried, draft.Add);
                    index += gopFrames.Count;
                    gopFrames.Clear();
                }

                try
                {
                    progress.Report(0);
                    FfmpegDecoder.ReadFrames(ffmpeg, request.Input, request.Fps, size, rgb =>
                    {
                        gopFrames.Add(Scaler.ToSquareRgb24(rgb, size.Width, size.Height, request.Fit));
                        if (gopFrames.Count == gop)
                            Flush();
                        progress.Report(index + gopFrames.Count);
                    });
                    Flush();
                    progress.Report(index);
                    progress.Finish();
                }
                catch
                {
                    progress.BreakLine();
                    throw;
                }

                if (draft.Count == 0)
                    throw new W154Exception("o vídeo não tem quadros.");

                sampleCount = sampleRate == 0 ? 0 : AudioPcm.SampleCount(draft.Count, frameUs, sampleRate);
                if (pass == 0 && pcm is not null && sampleCount > 0)
                    log.WriteLine($"áudio {sampleRate} Hz mono, {sampleCount} amostras, IMA ADPCM no arquivo");
                if (pcm is not null && pcm.CanSeek)
                    pcm.Position = 0;
                lengths = draft.Complete(request.Output, frameUs, pcm, sampleRate);
                try
                {
                    W154File.Verify(request.Output);
                }
                catch
                {
                    if (File.Exists(request.Output))
                        File.Delete(request.Output);
                    throw;
                }

                rate = RateCheck.Measure(frameUs, lengths, sampleRate, sampleCount);
                if (rate.WorstMbps <= 16 || pass == Ladder.Length - 1)
                    break;
                log.WriteLine($"pior segundo {FormatMbps(rate.WorstMbps)} Mbit/s. Seguinte passagem com menos detalhe.");
            }

            log.WriteLine($"arquivo {request.Output}");
            log.WriteLine($"quadros {lengths.Length}");
            log.WriteLine($"média {FormatMbps(rate.AverageMbps)} Mbit/s");
            log.WriteLine($"pior segundo {rate.WorstSecond}: {FormatMbps(rate.WorstMbps)} Mbit/s — {BandName(rate.Band)}");
            if (rate.WorstMbps <= 16 || request.AllowOverBudget)
                return 0;
            log.WriteLine("acima de 16 Mbit/s. O arquivo foi gravado. Use --allow-over-budget para sair com sucesso.");
            return 2;
        }
        finally
        {
            pcm?.Dispose();
            if (pcmPath is not null && File.Exists(pcmPath))
                File.Delete(pcmPath);
        }
    }

    public static int[] EncodeFrames(
        IReadOnlyList<ushort[]> frames,
        int fps,
        bool contain,
        DitherMode dither,
        string output,
        Stream? pcm = null,
        int sampleRate = 0,
        float holdScale = 1,
        int maxColors = 256,
        bool holdFs = false)
    {
        int frameUs = PictureContract.FrameUs(fps);
        int gop = PictureContract.Gop(frameUs);
        var bodies = new List<byte[]>();
        byte[]? previous = null;
        Palette? carried = null;
        float step = Quantizer.StepDistance();
        var tune = new RatePass(maxColors, holdScale, holdFs);
        for (int offset = 0; offset < frames.Count; offset += gop)
        {
            int count = Math.Min(gop, frames.Count - offset);
            var slice = new byte[count][];
            for (int i = 0; i < count; i++)
                slice[i] = Expand(frames[offset + i]);
            EncodeGop(slice, offset, gop, contain, dither, tune, step, ref previous, ref carried, bodies.Add);
        }

        int[] lengths = W154File.Write(output, frameUs, bodies, pcm, sampleRate);
        W154File.Verify(output);
        return lengths;
    }

    static void EncodeGop(
        IReadOnlyList<byte[]> frames,
        int index,
        int gop,
        bool contain,
        DitherMode dither,
        RatePass tune,
        float step,
        ref byte[]? previous,
        ref Palette? carried,
        Action<byte[]> add)
    {
        Palette palette = Quantizer.Build(frames, contain, carried, tune.Colors);
        float margin = tune.HoldScale * step;
        bool holdThis = tune.HoldFs || dither != DitherMode.Fs;
        for (int i = 0; i < frames.Count; i++)
        {
            ReadOnlySpan<ushort> colors = i == 0 && carried is not null ? carried.Entries : palette.Entries;
            float hold = previous is null || !holdThis ? 0 : margin;
            byte[] indices = IndexMap.Map(frames[i], palette, dither, previous ?? [], colors, hold);
            add(FrameEncoder.Build(index % gop == 0, palette.Entries, indices, previous ?? indices));
            previous = indices;
            index++;
        }

        carried = palette;
    }

    static byte[] Expand(ushort[] frame)
    {
        var rgb = new byte[PictureContract.Pixels * 3];
        int n = Math.Min(frame.Length, PictureContract.Pixels);
        for (int i = 0; i < n; i++)
        {
            ColorSpace.Expand565(frame[i], out byte r, out byte g, out byte b);
            int p = i * 3;
            rgb[p] = r;
            rgb[p + 1] = g;
            rgb[p + 2] = b;
        }

        return rgb;
    }

    static int? EstimateFrames(double? durationSeconds, int fps)
    {
        if (durationSeconds is not double seconds || seconds <= 0)
            return null;
        return Math.Max(1, (int)Math.Round(seconds * fps, MidpointRounding.AwayFromZero));
    }

    public static string FormatMbps(double value)
    {
        string text = value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        return text.Replace('.', ',');
    }

    public static string BandName(RateBand band) => band switch
    {
        RateBand.Comfortable => "confortável",
        RateBand.Intermittent => "intermitente",
        _ => "estoura",
    };
}
