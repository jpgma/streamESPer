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
            Check(Crc32.Compute(delta.AsSpan(0, 8)) == 0x6522DF69, "crc do delta vazio");
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

            Check(RateCheck.Fragments(key.Length) == 41, "keyframe cru em 41 frags");
            Check(seq == 42, "seq atravessa os dois quadros");
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
        }
    }

    static byte[] RawKeyframe()
    {
        var body = new byte[PictureContract.MaxBodyBytes];
        int pos = 0;
        WriteU16(body, ref pos, 3);
        WriteU16(body, ref pos, PictureContract.BandCount);
        WriteU32(body, ref pos, PictureContract.KeyframeMask);
        pos += PictureContract.PaletteBytes;
        for (int band = 0; band < PictureContract.BandCount; band++)
        {
            body[pos++] = BandCodec.Raw;
            body[pos++] = 0;
            WriteU16(body, ref pos, (ushort)PictureContract.BandBytes);
            pos += PictureContract.BandBytes;
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
        uint crc = Crc32.Compute(body.AsSpan(0, 8));
        int pos = 8;
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
