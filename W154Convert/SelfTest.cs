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
        CheckProgress();
        CheckFile();
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
            int[] lengths = Converter.EncodeFrames([first, dirty, third, still], 2, contain: false, dither: false, path);
            Check(lengths.Length == 4, "arquivo sintético tem 4 quadros");
            int[] verified = W154File.Verify(path);
            Check(verified.AsSpan().SequenceEqual(lengths), "verify relê os mesmos bodies");

            byte[] bytes = File.ReadAllBytes(path);
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
            Converter.EncodeFrames([first], 24, contain: true, dither: false, contained);
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
