namespace W154Convert;

static class SelfTest
{
    public static int Run()
    {
        Check(Crc32.Compute("123456789"u8) == 0xCBF43926, "crc32 de 123456789");
        Check(PictureContract.FrameUs(24) == 41667 && PictureContract.Gop(41667) == 24, "frame_us e gop de 24 fps");
        Check(PictureContract.FrameUs(30) == 33333 && PictureContract.Gop(33333) == 30, "frame_us e gop de 30 fps");
        CheckRle();
        CheckLz4();
        CheckRate();
        CheckFit();
        CheckPalette();
        CheckProgress();
        CheckFile();
        CheckAudio();
        Console.WriteLine("selftest ok");
        return 0;
    }

    static void CheckRle()
    {
        var band = new byte[PictureContract.BandBytes];
        Array.Fill(band, (byte)7);
        byte[] encoded = BandRle.Encode(band);
        Check(encoded.Length == 30, "RLE de faixa constante tem 30 bytes");
        for (int i = 0; i < 15; i++)
        {
            Check(encoded[i * 2] == 0xFF && encoded[(i * 2) + 1] == 0x07, "par RLE FF 07");
        }

        var decoded = new byte[PictureContract.BandBytes];
        Check(BandRle.TryDecode(encoded, decoded) && decoded.AsSpan().SequenceEqual(band), "roundtrip RLE constante");

        var mixed = new byte[PictureContract.BandBytes];
        for (int i = 0; i < mixed.Length; i++)
            mixed[i] = (byte)(i < 100 ? 3 : i);
        byte[] mixedEncoded = BandRle.Encode(mixed);
        Check(BandRle.TryDecode(mixedEncoded, decoded) && decoded.AsSpan().SequenceEqual(mixed), "roundtrip RLE misto");
    }

    static void CheckLz4()
    {
        var flat = new byte[PictureContract.BandBytes];
        Array.Fill(flat, (byte)7);
        byte[]? compressed = Lz4Block.TryCompress(flat, PictureContract.BandBytes - 1);
        Check(compressed is not null, "LZ4 comprime faixa plana");
        var decoded = new byte[PictureContract.BandBytes];
        Check(compressed is not null && Lz4Block.TryDecode(compressed, decoded) && decoded.AsSpan().SequenceEqual(flat), "roundtrip LZ4 plano");

        var noise = new byte[PictureContract.BandBytes];
        uint state = 0x12345678;
        for (int i = 0; i < noise.Length; i++)
        {
            state = (state * 1664525) + 1013904223;
            noise[i] = (byte)(state >> 24);
        }

        byte[]? noiseCompressed = Lz4Block.TryCompress(noise, PictureContract.BandBytes - 1);
        if (noiseCompressed is not null)
            Check(Lz4Block.TryDecode(noiseCompressed, decoded) && decoded.AsSpan().SequenceEqual(noise), "roundtrip LZ4 de ruído");

        bool rejected = false;
        for (int seed = 1; seed <= 32; seed++)
        {
            FillIncompressible(noise, (uint)seed);
            if (Lz4Block.TryCompress(noise, PictureContract.BandBytes - 1) is null)
            {
                rejected = true;
                break;
            }

            byte[]? again = Lz4Block.TryCompress(noise, PictureContract.BandBytes - 1);
            Check(again is not null && Lz4Block.TryDecode(again, decoded) && decoded.AsSpan().SequenceEqual(noise), "roundtrip LZ4");
        }

        Check(rejected, "LZ4 recusa bloco que não comprime");
    }

    static void FillIncompressible(byte[] data, uint seed)
    {
        uint state = seed * 747796405u + 2891336453u;
        for (int i = 0; i < data.Length; i++)
        {
            state = (state * 747796405u) + 2891336453u;
            data[i] = (byte)(state >> 24);
        }
    }

    static void CheckRate()
    {
        Check(RateCheck.Fragments(58244) == 41 && RateCheck.UdpBytes(58244) == 58900, "keyframe raw 58244");
        Check(RateCheck.Fragments(57732) == 40 && RateCheck.UdpBytes(57732) == 58372, "delta raw 57732");
    }

    static void CheckFit()
    {
        var wide = new byte[240 * 120 * 3];
        for (int i = 0; i < wide.Length; i += 3)
        {
            wide[i] = 255;
            wide[i + 1] = 0;
            wide[i + 2] = 0;
        }

        ushort[] contained = Scaler.ToRgb565(wide, 240, 120, FitMode.Contain);
        Check(contained[0] == 0 && contained[239 * 240] == 0, "contain preenche as barras com preto");
        Check(contained[100 * 240] == Rgb565.Pack(255, 0, 0), "contain mantém a imagem no centro");

        var strip = new byte[4 * 2 * 3];
        Put(strip, 4, 0, 0, 0, 0, 255);
        Put(strip, 4, 1, 0, 255, 0, 0);
        Put(strip, 4, 2, 0, 0, 255, 0);
        Put(strip, 4, 3, 0, 0, 0, 255);
        Put(strip, 4, 0, 1, 0, 0, 255);
        Put(strip, 4, 1, 1, 255, 0, 0);
        Put(strip, 4, 2, 1, 0, 255, 0);
        Put(strip, 4, 3, 1, 0, 0, 255);
        ushort[] covered = Scaler.ToRgb565(strip, 4, 2, FitMode.Cover);
        Check(covered[0] == Rgb565.Pack(255, 0, 0), "cover recorta a esquerda do quadrado");
        Check(covered[239] == Rgb565.Pack(0, 255, 0), "cover recorta a direita do quadrado");
    }

    static void CheckPalette()
    {
        ColorSpace.Expand565(0xFFFF, out byte whiteR, out byte whiteG, out byte whiteB);
        Check(whiteR == 255 && whiteG == 255 && whiteB == 255, "branco RGB565 expande para 255");
        ColorSpace.Oklab(0, out float blackL, out float blackA, out float blackB);
        Check(MathF.Abs(blackL) < 1e-5f && MathF.Abs(blackA) < 1e-5f && MathF.Abs(blackB) < 1e-5f, "preto em OKLab é 0");
        ColorSpace.Oklab(0xFFFF, out float whiteL, out _, out _);
        Check(whiteL > 0.99f && whiteL < 1.01f, "branco em OKLab tem L perto de 1");

        var few = new byte[PictureContract.Pixels * 3];
        for (int i = 0; i < PictureContract.Pixels; i++)
        {
            ColorSpace.Expand565((ushort)(i % 200), out byte r, out byte g, out byte b);
            int p = i * 3;
            few[p] = r;
            few[p + 1] = g;
            few[p + 2] = b;
        }

        Palette exact = Quantizer.Build([few], reserveBlack: false, previous: null);
        byte[] exactIndex = IndexMap.Map(few, exact, DitherMode.None);
        bool lossless = exact.Count == 200;
        for (int i = 0; lossless && i < PictureContract.Pixels; i++)
            lossless = exact.Entries[exactIndex[i]] == (ushort)(i % 200);
        Check(lossless, "até 256 cores mapeiam o RGB565 exato");

        byte[] gradient = Gradient();
        Palette reduced = Quantizer.Build([gradient], reserveBlack: false, previous: null);
        var used = new HashSet<ushort>();
        for (int i = 0; i < reduced.Count; i++)
            used.Add(reduced.Entries[i]);
        byte[] mapped = IndexMap.Map(gradient, reduced, DitherMode.None);
        double sse = 0;
        for (int i = 0; i < PictureContract.Pixels; i++)
        {
            int p = i * 3;
            ushort src = Rgb565.Pack(gradient[p], gradient[p + 1], gradient[p + 2]);
            sse += ColorSpace.Distance(src, reduced.Entries[mapped[i]]);
        }

        double average = sse / PictureContract.Pixels;
        string averageText = average.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture);
        Check(used.Count == 256, "degradê usa 256 entradas");
        Check(average < 0.01, "erro médio OKLab do degradê (" + averageText + ") abaixo de 0,01");

        ColorSpace.Expand565(0x1234, out byte flatR, out byte flatG, out byte flatB);
        var flat = new byte[PictureContract.Pixels * 3];
        for (int i = 0; i < PictureContract.Pixels; i++)
        {
            int p = i * 3;
            flat[p] = flatR;
            flat[p + 1] = flatG;
            flat[p + 2] = flatB;
        }

        Palette flatPalette = Quantizer.Build([flat], reserveBlack: false, previous: null);
        byte[] diffused = IndexMap.Map(flat, flatPalette, DitherMode.Fs);
        bool quiet = true;
        for (int i = 1; quiet && i < diffused.Length; i++)
            quiet = diffused[i] == diffused[0];
        Check(quiet, "Floyd-Steinberg em cor chapada não espalha ruído");

        ushort[] gradient565 = PackRgb(gradient);
        string path = Path.Combine(Path.GetTempPath(), "w154-palette-" + Guid.NewGuid().ToString("N") + ".w154");
        try
        {
            int[] ordered = Converter.EncodeFrames([gradient565, gradient565], 2, contain: false, DitherMode.Ordered, path);
            Check(ordered.Length == 2 && ordered[1] == PictureContract.MinBodyBytes, "dither ordenado repete o quadro parado");

            Converter.EncodeFrames([gradient565, gradient565, gradient565, gradient565], 2, contain: false, DitherMode.None, path);
            byte[] file = File.ReadAllBytes(path);
            Check(ReadPalette(file, 0).AsSpan().SequenceEqual(ReadPalette(file, 2)), "GOPs iguais repetem a paleta");
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    static byte[] Gradient()
    {
        var rgb = new byte[PictureContract.Pixels * 3];
        for (int y = 0; y < PictureContract.Height; y++)
        {
            for (int x = 0; x < PictureContract.Width; x++)
            {
                int p = ((y * PictureContract.Width) + x) * 3;
                rgb[p] = (byte)(x * 255 / 239);
                rgb[p + 1] = (byte)(y * 255 / 239);
                rgb[p + 2] = (byte)((x + y) * 255 / 478);
            }
        }

        return rgb;
    }

    static ushort[] PackRgb(byte[] rgb)
    {
        var frame = new ushort[PictureContract.Pixels];
        for (int i = 0; i < frame.Length; i++)
        {
            int p = i * 3;
            frame[i] = Rgb565.Pack(rgb[p], rgb[p + 1], rgb[p + 2]);
        }

        return frame;
    }

    static ushort[] ReadPalette(byte[] file, int frame)
    {
        int at = PictureContract.HeaderSize + (frame * 4);
        int palette = (int)ReadU32(file, at) + 4 + 8;
        var entries = new ushort[PictureContract.PaletteColors];
        for (int i = 0; i < entries.Length; i++)
            entries[i] = (ushort)(file[palette + (i * 2)] | (file[palette + (i * 2) + 1] << 8));
        return entries;
    }

    static void CheckProgress()
    {
        string start = ConvertProgress.FormatLine(0, 100, TimeSpan.Zero, 80, final: false);
        Check(start.Contains("  0%", StringComparison.Ordinal) && start.Contains("resta --:--", StringComparison.Ordinal), "barra no início");
        string middle = ConvertProgress.FormatLine(50, 100, TimeSpan.FromSeconds(10), 80, final: false);
        Check(middle.Contains(" 50%", StringComparison.Ordinal)
            && middle.Contains("50/100", StringComparison.Ordinal)
            && middle.Contains("resta 00:10", StringComparison.Ordinal), "estimativa de tempo");
        string end = ConvertProgress.FormatLine(24, 30, TimeSpan.FromSeconds(3), 80, final: true);
        Check(end.Contains("100%", StringComparison.Ordinal) && end.Contains("resta 00:00", StringComparison.Ordinal), "barra no fim");
        string unknown = ConvertProgress.FormatLine(4, null, TimeSpan.FromSeconds(65), 80, final: false);
        Check(unknown.Contains("quadros 4", StringComparison.Ordinal) && unknown.Contains("passou 01:05", StringComparison.Ordinal), "barra sem duração");
    }

    static void CheckFile()
    {
        ushort red = Rgb565.Pack(255, 0, 0);
        ushort green = Rgb565.Pack(0, 255, 0);
        ushort blue = Rgb565.Pack(0, 0, 255);
        ushort[] Solid(ushort color)
        {
            var frame = new ushort[PictureContract.Pixels];
            Array.Fill(frame, color);
            return frame;
        }

        ushort[] first = Solid(red);
        ushort[] dirty = (ushort[])first.Clone();
        for (int i = 3 * PictureContract.BandBytes; i < 4 * PictureContract.BandBytes; i++)
            dirty[i] = green;
        ushort[] third = Solid(blue);
        ushort[] still = (ushort[])third.Clone();
        string path = Path.Combine(Path.GetTempPath(), "w154-selftest-" + Guid.NewGuid().ToString("N") + ".w154");
        try
        {
            int[] lengths = Converter.EncodeFrames([first, dirty, third, still], 2, contain: false, DitherMode.None, path);
            Check(lengths.Length == 4, "arquivo sintético tem 4 quadros");
            int[] verified = W154File.Verify(path);
            Check(verified.AsSpan().SequenceEqual(lengths), "verify relê os mesmos bodies");

            byte[] bytes = File.ReadAllBytes(path);
            Check(bytes[28] == 0 && bytes[29] == 0 && bytes[30] == 0 && bytes[31] == 0, "vídeo sem áudio grava audio_offset 0");
            bytes[80] ^= 0xFF;
            string broken = path + ".bad";
            File.WriteAllBytes(broken, bytes);
            bool failed = false;
            try
            {
                W154File.Verify(broken);
            }
            catch (W154Exception)
            {
                failed = true;
            }

            Check(failed, "crc32 alterado é recusado");
            File.Delete(broken);

            string contained = path + ".contain.w154";
            Converter.EncodeFrames([first], 24, contain: true, DitherMode.None, contained);
            byte[] containBytes = File.ReadAllBytes(contained);
            int palette = 32 + 4 + 4 + 8;
            ushort entry0 = (ushort)(containBytes[palette] | (containBytes[palette + 1] << 8));
            Check(entry0 == 0, "contain grava palette[0] = 0");
            File.Delete(contained);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    static void CheckAudio()
    {
        Check(AudioPcm.SampleCount(24, 41667, 48000) == 48000, "24 quadros a 48000 Hz");
        Check(AudioPcm.SampleCount(24, 41667, 16000) == 16000, "24 quadros a 16000 Hz");
        Check(RateCheck.Fragments(96000) == 66 && RateCheck.UdpBytes(96000) == 97056, "um segundo de 48000 Hz mono");

        int videoUdp = RateCheck.UdpBytes(58244);
        RateReport mixed = RateCheck.Measure(1_000_000, new[] { 58244 }, 48000, 48000);
        double expected = (videoUdp + 97056) * 8.0 / 1_000_000.0;
        Check(mixed.WorstSecond == 0 && mixed.WorstMbps == expected && mixed.AverageMbps == expected, "áudio soma no balde do vídeo");

        var frame = new ushort[PictureContract.Pixels];
        Array.Fill(frame, Rgb565.Pack(255, 0, 0));
        int samples = AudioPcm.SampleCount(1, PictureContract.FrameUs(24), 48000);
        var pcm = new byte[samples * 2];
        for (int i = 0; i < pcm.Length; i++)
            pcm[i] = (byte)i;

        string path = Path.Combine(Path.GetTempPath(), "w154-audio-" + Guid.NewGuid().ToString("N") + ".w154");
        try
        {
            using (var stream = new MemoryStream(pcm, writable: false))
                Converter.EncodeFrames([frame], 24, contain: false, DitherMode.None, path, stream, 48000);

            byte[] file = File.ReadAllBytes(path);
            uint audioOffset = ReadU32(file, 28);
            Check(audioOffset + AudioPcm.HeaderBytes + (uint)pcm.Length == (uint)file.Length, "audio_offset no fim do vídeo");
            Check(file.AsSpan((int)audioOffset + AudioPcm.HeaderBytes).SequenceEqual(pcm), "PCM roundtrip");
            Check(ReadU32(file, (int)audioOffset + 12) == Crc32.Compute(pcm), "crc32 do PCM");

            string padded = path + ".pad.w154";
            var head = new byte[4];
            pcm.AsSpan(0, 4).CopyTo(head);
            using (var shortPcm = new MemoryStream(head))
                Converter.EncodeFrames([frame], 24, contain: false, DitherMode.None, padded, shortPcm, 48000);
            byte[] padFile = File.ReadAllBytes(padded);
            int padAt = (int)ReadU32(padFile, 28) + AudioPcm.HeaderBytes;
            Check(padFile.AsSpan(padAt, 4).SequenceEqual(head), "PCM curto preserva o início");
            Check(padFile[padAt + 4] == 0, "PCM curto completa com silêncio");
            File.Delete(padded);

            string trimmed = path + ".trim.w154";
            var longPcm = new byte[pcm.Length + 4];
            pcm.CopyTo(longPcm, 0);
            longPcm[^1] = 0xFF;
            using (var longStream = new MemoryStream(longPcm))
                Converter.EncodeFrames([frame], 24, contain: false, DitherMode.None, trimmed, longStream, 48000);
            byte[] trimFile = File.ReadAllBytes(trimmed);
            Check(trimFile.Length == file.Length && trimFile[^1] == pcm[^1], "PCM longo é cortado");
            File.Delete(trimmed);

            ExpectReject(WithU32(file, (int)audioOffset + 4, 22050), "sample_rate 22050 é recusado");
            ExpectReject(WithU16(file, (int)audioOffset + 8, 2), "channels 2 é recusado");
            ExpectReject(WithU32(file, (int)audioOffset, 1), "pcm_bytes ímpar é recusado");
            byte[] badCrc = (byte[])file.Clone();
            badCrc[(int)audioOffset + AudioPcm.HeaderBytes] ^= 0xFF;
            ExpectReject(badCrc, "crc32 do PCM alterado é recusado");
            ExpectReject(WithU32(file, 28, audioOffset + 4), "folga antes do PCM é recusada");
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    static uint ReadU32(byte[] data, int offset) =>
        (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24));

    static byte[] WithU32(byte[] source, int offset, uint value)
    {
        byte[] copy = (byte[])source.Clone();
        copy[offset] = (byte)value;
        copy[offset + 1] = (byte)(value >> 8);
        copy[offset + 2] = (byte)(value >> 16);
        copy[offset + 3] = (byte)(value >> 24);
        return copy;
    }

    static byte[] WithU16(byte[] source, int offset, ushort value)
    {
        byte[] copy = (byte[])source.Clone();
        copy[offset] = (byte)value;
        copy[offset + 1] = (byte)(value >> 8);
        return copy;
    }

    static void ExpectReject(byte[] bytes, string name)
    {
        string path = Path.Combine(Path.GetTempPath(), "w154-bad-" + Guid.NewGuid().ToString("N") + ".w154");
        try
        {
            File.WriteAllBytes(path, bytes);
            bool failed = false;
            try
            {
                W154File.Verify(path);
            }
            catch (W154Exception)
            {
                failed = true;
            }

            Check(failed, name);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    static void Put(byte[] rgb, int width, int x, int y, byte r, byte g, byte b)
    {
        int p = ((y * width) + x) * 3;
        rgb[p] = r;
        rgb[p + 1] = g;
        rgb[p + 2] = b;
    }

    static void Check(bool condition, string name)
    {
        if (!condition)
            throw new W154Exception("selftest falhou: " + name);
    }
}
