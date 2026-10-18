using System.Buffers.Binary;

namespace W154Stream;

static class SelfTest
{
    public static int Run()
    {
        string path = Path.Combine(Path.GetTempPath(), "w154-stream-" + Guid.NewGuid().ToString("N") + ".w154");
        try
        {
            byte[] key = RawKeyframe();
            byte[] delta = EmptyDelta();
            Check(Crc32.Compute(delta.AsSpan(0, PictureContract.PreambleBytes)) == 0x41716C2A, "crc do delta vazio");
            W154File.Write(path, PictureContract.FrameUs(24), new[] { key, delta });
            W154File.Verify(path);

            using W154Movie movie = W154Movie.Open(path);
            Check(movie.FrameCount == 2 && movie.FrameUs == 41667 && !movie.Audio.Present, "header do movie");
            Check(movie.Body(0).SequenceEqual(key) && movie.Body(1).SequenceEqual(delta), "bodies mapeados");

            uint seq = 0;
            for (int frame = 0; frame < movie.FrameCount; frame++)
            {
                ReadOnlySpan<byte> body = movie.Body(frame);
                int frags = RateCheck.Fragments(body.Length);
                int offset = 0;
                for (int frag = 0; frag < frags; frag++)
                {
                    int payload = Math.Min(PictureContract.FragPayload, body.Length - offset);
                    Check(payload is >= 1 and <= PictureContract.FragPayload, "payload no corte");
                    if (frag + 1 < frags)
                        Check(payload == PictureContract.FragPayload, "fragmento cheio");
                    Check(PictureContract.FragHeader + payload <= Session.PacketBytes, "datagrama cabe em 1472");
                    offset += payload;
                    seq++;
                }

                Check(offset == body.Length, "body coberto");
            }

            string? byName = Catalog.Resolve(Path.GetDirectoryName(path)!, Path.GetFileName(path));
            Check(string.Equals(byName, path, StringComparison.OrdinalIgnoreCase), "nome no dir");
            Check(Catalog.Resolve(Path.GetTempPath(), ".." + Path.DirectorySeparatorChar + "x.w154") is null, "recusa ..");
            Check(string.Equals(Catalog.Resolve(Path.GetTempPath(), path), path, StringComparison.OrdinalIgnoreCase), "caminho absoluto");
            Check(!string.IsNullOrEmpty(Environment.ProcessPath) && Catalog.Resolve(Path.GetTempPath(), Environment.ProcessPath) is null, "absoluto que não é w154");
            Check(Catalog.Resolve(Path.GetTempPath(), Path.Combine(Path.GetTempPath(), "ausente.w154")) is null, "absoluto ausente");
            Check(RateCheck.Fragments(key.Length) == 41, "keyframe cru em 41 frags");
            Check(seq == 42, "seq atravessa os dois quadros");

            int samples = AudioPcm.SampleCount(2, PictureContract.FrameUs(24), 48000);
            var pcm = new byte[samples * 2];
            pcm[0] = 0x34;
            pcm[1] = 0x12;
            string audioPath = path + ".audio.w154";
            using (var stream = new MemoryStream(pcm, writable: false))
                W154File.Write(audioPath, PictureContract.FrameUs(24), new[] { key, delta }, stream, 48000);
            using (W154Movie withAudio = W154Movie.Open(audioPath))
            {
                var s16 = new byte[4];
                withAudio.ReadPcm(0, s16);
                Check(s16[0] == 0x34 && s16[1] == 0x12, "leitura de áudio devolve s16");
                Check(withAudio.Pcm.Length == ImaAdpcm.PayloadBytes(withAudio.SampleCount), "arquivo guarda ADPCM");
                Check(withAudio.Pcm[2] == 0 && (s16[2] != 0 || s16[3] != 0), "o datagrama não carrega o nibble cru");
            }

            File.Delete(audioPath);
            Console.WriteLine("selftest ok");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("selftest falhou: " + ex.Message);
            return 1;
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
            string audioLeft = path + ".audio.w154";
            if (File.Exists(audioLeft))
                File.Delete(audioLeft);
        }
    }

    static byte[] RawKeyframe()
    {
        var body = new byte[PictureContract.MaxBodyBytes];
        int pos = 0;
        WriteU16(body, ref pos, 3);
        WriteU16(body, ref pos, (ushort)PictureContract.RowCount);
        for (int i = 0; i < PictureContract.RowMaskBytes; i++)
            body[pos++] = 0xFF;
        pos += PictureContract.PaletteBytes;
        for (int row = 0; row < PictureContract.RowCount; row++)
        {
            body[pos++] = RowCodec.Raw;
            body[pos++] = 0;
            WriteU16(body, ref pos, (ushort)PictureContract.RowBytes);
            pos += PictureContract.RowBytes;
        }

        uint crc = Crc32.Compute(body.AsSpan(0, pos));
        WriteU32(body, ref pos, crc);
        if (pos != body.Length)
            throw new W154Exception($"keyframe de teste com {pos} bytes.");
        return body;
    }

    static byte[] EmptyDelta()
    {
        var body = new byte[PictureContract.MinBodyBytes];
        uint crc = Crc32.Compute(body.AsSpan(0, PictureContract.PreambleBytes));
        int pos = PictureContract.PreambleBytes;
        WriteU32(body, ref pos, crc);
        return body;
    }

    static void WriteU16(byte[] dst, ref int pos, ushort value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(dst.AsSpan(pos), value);
        pos += 2;
    }

    static void WriteU32(byte[] dst, ref int pos, uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(dst.AsSpan(pos), value);
        pos += 4;
    }

    static void Check(bool ok, string name)
    {
        if (!ok)
            throw new W154Exception(name);
    }
}
