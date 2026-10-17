namespace W154Convert;

readonly record struct ConvertRequest(
    string Input,
    string Output,
    int Fps,
    FitMode Fit,
    bool Dither,
    bool AllowOverBudget);

static class Converter
{
    public static int Run(ConvertRequest request, TextWriter log)
    {
        int frameUs = PictureContract.FrameUs(request.Fps);
        int gop = PictureContract.Gop(frameUs);
        string ffmpeg = FfmpegLocator.Find();
        FfmpegDecoder.VideoSize size = FfmpegDecoder.Probe(ffmpeg, request.Input);
        log.WriteLine(
            $"fonte {size.Width}x{size.Height}, {request.Fps} fps, frame_us {frameUs}, gop {gop}, {request.Fit.ToString().ToLowerInvariant()}");

        var gopFrames = new List<ushort[]>(gop);
        using var draft = new W154Draft();
        byte[]? previous = null;
        int index = 0;
        var progress = new ConvertProgress(log, EstimateFrames(size.DurationSeconds, request.Fps));

        void Flush()
        {
            if (gopFrames.Count == 0)
                return;
            Palette palette = Quantizer.Build(gopFrames, request.Fit == FitMode.Contain);
            foreach (ushort[] frame in gopFrames)
            {
                bool keyframe = index % gop == 0;
                byte[] indices = IndexMap.Map(frame, palette, request.Dither);
                draft.Add(FrameEncoder.Build(keyframe, palette, indices, previous ?? indices));
                previous = indices;
                index++;
            }

            gopFrames.Clear();
        }

        try
        {
            progress.Report(0);
            FfmpegDecoder.ReadFrames(ffmpeg, request.Input, request.Fps, size, rgb =>
            {
                gopFrames.Add(Scaler.ToRgb565(rgb, size.Width, size.Height, request.Fit));
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

        int[] lengths = draft.Complete(request.Output, frameUs);
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

        RateReport rate = RateCheck.Measure(frameUs, lengths);
        log.WriteLine($"arquivo {request.Output}");
        log.WriteLine($"quadros {lengths.Length}");
        log.WriteLine($"média {FormatMbps(rate.AverageMbps)} Mbit/s");
        log.WriteLine($"pior segundo {rate.WorstSecond}: {FormatMbps(rate.WorstMbps)} Mbit/s — {BandName(rate.Band)}");
        if (rate.WorstMbps <= 16 || request.AllowOverBudget)
            return 0;
        log.WriteLine("acima de 16 Mbit/s. O arquivo foi gravado. Use --allow-over-budget para sair com sucesso.");
        return 2;
    }

    public static int[] EncodeFrames(IReadOnlyList<ushort[]> frames, int fps, bool contain, bool dither, string output)
    {
        int frameUs = PictureContract.FrameUs(fps);
        int gop = PictureContract.Gop(frameUs);
        var bodies = new List<byte[]>();
        byte[]? previous = null;
        for (int offset = 0; offset < frames.Count; offset += gop)
        {
            int count = Math.Min(gop, frames.Count - offset);
            var slice = new ushort[count][];
            for (int i = 0; i < count; i++)
                slice[i] = frames[offset + i];
            Palette palette = Quantizer.Build(slice, contain);
            for (int i = 0; i < count; i++)
            {
                int index = offset + i;
                byte[] indices = IndexMap.Map(slice[i], palette, dither);
                bodies.Add(FrameEncoder.Build(index % gop == 0, palette, indices, previous ?? indices));
                previous = indices;
            }
        }

        int[] lengths = W154File.Write(output, frameUs, bodies);
        W154File.Verify(output);
        return lengths;
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
