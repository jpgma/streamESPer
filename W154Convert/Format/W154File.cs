using System.Buffers.Binary;

namespace W154Convert;

static class FrameEncoder
{
    public static byte[] Build(bool keyframe, Palette palette, ReadOnlySpan<byte> indices, ReadOnlySpan<byte> previous)
    {
        var body = new List<byte>(keyframe ? PictureContract.MaxBodyBytes : 4096);
        body.Add((byte)(keyframe ? 3 : 0));
        body.Add(0);

        if (keyframe)
        {
            WriteU16(body, PictureContract.BandCount);
            WriteU32(body, PictureContract.KeyframeMask);
            for (int i = 0; i < PictureContract.PaletteColors; i++)
                WriteU16(body, palette.Entries[i]);
            for (int band = 0; band < PictureContract.BandCount; band++)
            {
                int off = band * PictureContract.BandBytes;
                BandCodec.Append(body, indices.Slice(off, PictureContract.BandBytes));
            }
        }
        else
        {
            uint mask = 0;
            ushort count = 0;
            var records = new List<byte>();
            for (int band = 0; band < PictureContract.BandCount; band++)
            {
                int off = band * PictureContract.BandBytes;
                ReadOnlySpan<byte> current = indices.Slice(off, PictureContract.BandBytes);
                ReadOnlySpan<byte> prior = previous.Slice(off, PictureContract.BandBytes);
                if (current.SequenceEqual(prior))
                    continue;
                mask |= 1u << band;
                count++;
                BandCodec.Append(records, current);
            }

            WriteU16(body, count);
            WriteU32(body, mask);
            body.AddRange(records);
        }

        uint crc = Crc32.Compute(body.ToArray());
        WriteU32(body, crc);
        if (body.Count is < PictureContract.MinBodyBytes or > PictureContract.MaxBodyBytes)
            throw new W154Exception($"body com {body.Count} bytes fora de {PictureContract.MinBodyBytes}..{PictureContract.MaxBodyBytes}.");
        return body.ToArray();
    }

    static void WriteU16(List<byte> dst, ushort value)
    {
        dst.Add((byte)value);
        dst.Add((byte)(value >> 8));
    }

    static void WriteU32(List<byte> dst, uint value)
    {
        dst.Add((byte)value);
        dst.Add((byte)(value >> 8));
        dst.Add((byte)(value >> 16));
        dst.Add((byte)(value >> 24));
    }
}

sealed class W154Draft : IDisposable
{
    readonly string _tempPath;
    readonly FileStream _temp;
    readonly List<int> _lengths = new();

    public W154Draft()
    {
        _tempPath = Path.Combine(Path.GetTempPath(), "w154-" + Guid.NewGuid().ToString("N") + ".bodies");
        _temp = new FileStream(_tempPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
    }

    public int Count => _lengths.Count;

    public void Add(byte[] body)
    {
        if (body.Length is < PictureContract.MinBodyBytes or > PictureContract.MaxBodyBytes)
            throw new W154Exception($"body com {body.Length} bytes fora de {PictureContract.MinBodyBytes}..{PictureContract.MaxBodyBytes}.");
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(length, (uint)body.Length);
        _temp.Write(length);
        _temp.Write(body);
        _lengths.Add(body.Length);
    }

    public int[] Complete(string path, int frameUs, Stream? pcm = null, int sampleRate = 0)
    {
        int[] lengths = _lengths.ToArray();
        W154File.WritePrefixed(path, frameUs, lengths, _temp, pcm, sampleRate);
        return lengths;
    }

    public void Dispose()
    {
        _temp.Dispose();
        if (File.Exists(_tempPath))
            File.Delete(_tempPath);
    }
}

static class W154File
{
    public static int[] Write(string path, int frameUs, IReadOnlyList<byte[]> bodies, Stream? pcm = null, int sampleRate = 0)
    {
        using var draft = new W154Draft();
        foreach (byte[] body in bodies)
            draft.Add(body);
        return draft.Complete(path, frameUs, pcm, sampleRate);
    }

    public static void WritePrefixed(string path, int frameUs, int[] lengths, Stream prefixed, Stream? pcm = null, int sampleRate = 0)
    {
        if (lengths.Length < 1)
            throw new W154Exception("o vídeo não tem quadros.");
        if (frameUs is < 16667 or > 1_000_000)
            throw new W154Exception("frame_us fora de 16667..1000000.");

        long videoEnd = PictureContract.HeaderSize + (lengths.Length * 4L);
        foreach (int length in lengths)
        {
            videoEnd += 4L + length;
            if (videoEnd > uint.MaxValue)
                throw new W154Exception("o arquivo passa de 4 GiB e o offset u32 não cabe.");
        }

        uint audioOffset = 0;
        int pcmBytes = 0;
        uint pcmCrc = 0;
        if (pcm is not null)
        {
            AudioPcm.RequireRate(sampleRate);
            pcmBytes = AudioPcm.SampleCount(lengths.Length, frameUs, sampleRate) * 2;
            pcmCrc = AudioPcm.CrcFitted(pcm, pcmBytes);
            audioOffset = (uint)videoEnd;
        }

        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        string partial = path + ".tmp";
        try
        {
            using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                Span<byte> header = stackalloc byte[PictureContract.HeaderSize];
                header[0] = (byte)'W';
                header[1] = (byte)'1';
                header[2] = (byte)'5';
                header[3] = (byte)'4';
                BinaryPrimitives.WriteUInt16LittleEndian(header[4..], PictureContract.Version);
                BinaryPrimitives.WriteUInt16LittleEndian(header[6..], PictureContract.HeaderSize);
                BinaryPrimitives.WriteUInt16LittleEndian(header[8..], PictureContract.Width);
                BinaryPrimitives.WriteUInt16LittleEndian(header[10..], PictureContract.Height);
                BinaryPrimitives.WriteUInt16LittleEndian(header[12..], PictureContract.BandRows);
                BinaryPrimitives.WriteUInt16LittleEndian(header[14..], PictureContract.BandCount);
                BinaryPrimitives.WriteUInt32LittleEndian(header[16..], (uint)frameUs);
                BinaryPrimitives.WriteUInt32LittleEndian(header[20..], (uint)lengths.Length);
                BinaryPrimitives.WriteUInt32LittleEndian(header[28..], audioOffset);
                output.Write(header);

                long cursor = PictureContract.HeaderSize + (lengths.Length * 4L);
                Span<byte> offset = stackalloc byte[4];
                foreach (int length in lengths)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(offset, (uint)cursor);
                    output.Write(offset);
                    cursor += 4L + length;
                }

                prefixed.Position = 0;
                prefixed.CopyTo(output);

                if (pcm is not null)
                {
                    Span<byte> audio = stackalloc byte[AudioPcm.HeaderBytes];
                    BinaryPrimitives.WriteUInt32LittleEndian(audio, (uint)pcmBytes);
                    BinaryPrimitives.WriteUInt32LittleEndian(audio[4..], (uint)sampleRate);
                    BinaryPrimitives.WriteUInt16LittleEndian(audio[8..], AudioPcm.Channels);
                    BinaryPrimitives.WriteUInt16LittleEndian(audio[10..], AudioPcm.FormatS16Le);
                    BinaryPrimitives.WriteUInt32LittleEndian(audio[12..], pcmCrc);
                    output.Write(audio);
                    pcm.Position = 0;
                    AudioPcm.CopyFitted(pcm, output, pcmBytes);
                }
            }

            File.Move(partial, path, overwrite: true);
        }
        catch
        {
            if (File.Exists(partial))
                File.Delete(partial);
            throw;
        }
    }

    public static int[] Verify(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> header = stackalloc byte[PictureContract.HeaderSize];
        ReadExact(stream, header);
        if (header[0] != (byte)'W' || header[1] != (byte)'1' || header[2] != (byte)'5' || header[3] != (byte)'4')
            throw new W154Exception("magic diferente de W154.");
        if (BinaryPrimitives.ReadUInt16LittleEndian(header[4..]) != PictureContract.Version)
            throw new W154Exception("version diferente de 1.");
        if (BinaryPrimitives.ReadUInt16LittleEndian(header[6..]) != PictureContract.HeaderSize)
            throw new W154Exception("header_size diferente de 32.");
        if (BinaryPrimitives.ReadUInt16LittleEndian(header[8..]) != PictureContract.Width
            || BinaryPrimitives.ReadUInt16LittleEndian(header[10..]) != PictureContract.Height)
            throw new W154Exception("width ou height diferente de 240.");
        if (BinaryPrimitives.ReadUInt16LittleEndian(header[12..]) != PictureContract.BandRows
            || BinaryPrimitives.ReadUInt16LittleEndian(header[14..]) != PictureContract.BandCount)
            throw new W154Exception("band_rows ou band_count_max diferente do contrato.");

        uint frameUs = BinaryPrimitives.ReadUInt32LittleEndian(header[16..]);
        uint frameCount = BinaryPrimitives.ReadUInt32LittleEndian(header[20..]);
        uint audioOffset = BinaryPrimitives.ReadUInt32LittleEndian(header[28..]);
        if (BinaryPrimitives.ReadUInt32LittleEndian(header[24..]) != 0)
            throw new W154Exception("flags do header não são 0.");
        if (frameCount < 1)
            throw new W154Exception("frame_count menor que 1.");
        if (frameUs is < 16667 or > 1_000_000)
            throw new W154Exception("frame_us fora de 16667..1000000.");

        int gop = PictureContract.Gop((int)frameUs);
        var offsets = new uint[frameCount];
        Span<byte> word = stackalloc byte[4];
        uint first = (uint)(PictureContract.HeaderSize + (frameCount * 4));
        uint previousOffset = 0;
        for (int i = 0; i < offsets.Length; i++)
        {
            ReadExact(stream, word);
            uint offset = BinaryPrimitives.ReadUInt32LittleEndian(word);
            if (i == 0 && offset != first)
                throw new W154Exception("o primeiro offset não é header_size + frame_count * 4.");
            if (i > 0 && offset <= previousOffset)
                throw new W154Exception($"offset do quadro {i} não cresce.");
            offsets[i] = offset;
            previousOffset = offset;
        }

        var lengths = new int[frameCount];
        byte[]? previous = null;
        for (int i = 0; i < frameCount; i++)
        {
            if (stream.Position != offsets[i])
                throw new W154Exception($"quadro {i} não está no offset declarado.");
            ReadExact(stream, word);
            int bodyBytes = (int)BinaryPrimitives.ReadUInt32LittleEndian(word);
            if (bodyBytes is < PictureContract.MinBodyBytes or > PictureContract.MaxBodyBytes)
                throw new W154Exception($"body_bytes do quadro {i} fora do limite.");
            var body = new byte[bodyBytes];
            ReadExact(stream, body);
            previous = DecodeFrame(i, gop, body, previous);
            lengths[i] = bodyBytes;
            uint expected = offsets[i] + 4u + (uint)bodyBytes;
            if (i + 1 < frameCount && expected != offsets[i + 1])
                throw new W154Exception($"offset do quadro {i + 1} não vem depois do quadro {i}.");
        }

        if (audioOffset == 0)
        {
            if (stream.Position != stream.Length)
                throw new W154Exception("bytes sobrando depois do último quadro.");
            return lengths;
        }

        if (stream.Position != audioOffset)
            throw new W154Exception("audio_offset não aponta para o fim do vídeo.");
        VerifyAudio(stream, (int)frameCount, (int)frameUs);
        return lengths;
    }

    static void VerifyAudio(Stream stream, int frameCount, int frameUs)
    {
        Span<byte> audio = stackalloc byte[AudioPcm.HeaderBytes];
        ReadExact(stream, audio);
        uint pcmBytes = BinaryPrimitives.ReadUInt32LittleEndian(audio);
        uint sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(audio[4..]);
        ushort channels = BinaryPrimitives.ReadUInt16LittleEndian(audio[8..]);
        ushort format = BinaryPrimitives.ReadUInt16LittleEndian(audio[10..]);
        uint stored = BinaryPrimitives.ReadUInt32LittleEndian(audio[12..]);
        if (pcmBytes == 0 || (pcmBytes & 1) != 0)
            throw new W154Exception("pcm_bytes inválido.");
        if (!AudioPcm.IsRate((int)sampleRate))
            throw new W154Exception("sample_rate fora de 16000, 24000 e 48000.");
        if (channels != AudioPcm.Channels)
            throw new W154Exception("channels diferente de 1.");
        if (format != AudioPcm.FormatS16Le)
            throw new W154Exception("sample_format diferente de 0.");

        int expected = AudioPcm.SampleCount(frameCount, frameUs, (int)sampleRate) * 2;
        if (pcmBytes != (uint)expected)
            throw new W154Exception("pcm_bytes não cobre a duração do vídeo.");
        if ((long)pcmBytes > stream.Length - stream.Position)
            throw new W154Exception("arquivo acabou antes do PCM.");

        uint actual = CrcFitted(stream, pcmBytes);
        if (stored != actual)
            throw new W154Exception("crc32 do PCM diferente.");
        if (stream.Position != stream.Length)
            throw new W154Exception("bytes sobrando depois do PCM.");
    }

    static uint CrcFitted(Stream stream, uint length)
    {
        uint crc = 0xFFFFFFFF;
        var buffer = new byte[65536];
        long left = length;
        while (left > 0)
        {
            int want = (int)Math.Min(buffer.Length, left);
            int got = stream.Read(buffer, 0, want);
            if (got == 0)
                throw new W154Exception("arquivo acabou antes do esperado.");
            crc = Crc32.Update(crc, buffer.AsSpan(0, got));
            left -= got;
        }

        return Crc32.Finish(crc);
    }

    static byte[] DecodeFrame(int index, int gop, byte[] body, byte[]? previous)
    {
        int pos = 0;
        ushort flags = ReadU16(body, ref pos);
        ushort bandCount = ReadU16(body, ref pos);
        uint mask = ReadU32(body, ref pos);
        if ((flags & ~0x3) != 0)
            throw new W154Exception($"quadro {index} tem flag além dos bits 0 e 1.");
        if ((mask & 0xC0000000u) != 0)
            throw new W154Exception($"quadro {index} tem bit 30 ou 31 em band_mask.");
        if (PopCount(mask) != bandCount)
            throw new W154Exception($"quadro {index} tem band_count diferente do band_mask.");

        bool keyframe = index % gop == 0;
        if (keyframe)
        {
            if (flags != 3 || bandCount != PictureContract.BandCount || mask != PictureContract.KeyframeMask)
                throw new W154Exception($"quadro {index} deveria ser keyframe completo.");
            if (pos + PictureContract.PaletteBytes > body.Length)
                throw new W154Exception($"quadro {index} sem paleta.");
            pos += PictureContract.PaletteBytes;
        }
        else if ((flags & 0x1) != 0 || (flags & 0x2) != 0)
        {
            throw new W154Exception($"quadro {index} é delta com paleta ou bit de keyframe.");
        }
        else if (previous is null)
        {
            throw new W154Exception($"quadro {index} é delta sem quadro anterior.");
        }

        var decoded = keyframe ? new byte[PictureContract.Pixels] : (byte[])previous!.Clone();
        var prior = previous;
        int seen = 0;
        for (int band = 0; band < PictureContract.BandCount; band++)
        {
            if ((mask & (1u << band)) == 0)
                continue;
            if (pos + 4 > body.Length - 4)
                throw new W154Exception($"quadro {index} acabou no meio de uma banda.");
            byte codec = body[pos++];
            if (body[pos++] != 0)
                throw new W154Exception($"quadro {index} banda {band} com byte zero diferente de 0.");
            ushort comp = ReadU16(body, ref pos);
            if (codec == BandCodec.Raw && comp != PictureContract.BandBytes)
                throw new W154Exception($"quadro {index} banda {band} raw sem 1920 bytes.");
            if (codec is BandCodec.Rle or BandCodec.Lz4 && (comp == 0 || comp >= PictureContract.BandBytes))
                throw new W154Exception($"quadro {index} banda {band} comprimida não é menor que 1920.");
            if (codec > BandCodec.Lz4)
                throw new W154Exception($"quadro {index} banda {band} com codec desconhecido.");
            if (pos + comp > body.Length - 4)
                throw new W154Exception($"quadro {index} banda {band} passa do body.");

            var data = body.AsSpan(pos, comp);
            pos += comp;
            int pad = (4 - (comp & 3)) & 3;
            for (int p = 0; p < pad; p++)
            {
                if (pos >= body.Length - 4 || body[pos++] != 0)
                    throw new W154Exception($"quadro {index} banda {band} com pad diferente de zero.");
            }

            var target = decoded.AsSpan(band * PictureContract.BandBytes, PictureContract.BandBytes);
            bool ok = codec switch
            {
                BandCodec.Raw => CopyRaw(data, target),
                BandCodec.Rle => BandRle.TryDecode(data, target),
                _ => Lz4Block.TryDecode(data, target),
            };
            if (!ok)
                throw new W154Exception($"quadro {index} banda {band} não decodifica para 1920 bytes.");
            seen++;
        }

        if (seen != bandCount)
            throw new W154Exception($"quadro {index} não tem os records do band_mask.");
        if (pos != body.Length - 4)
            throw new W154Exception($"quadro {index} tem lixo antes do crc32.");

        uint stored = ReadU32(body, ref pos);
        uint actual = Crc32.Compute(body.AsSpan(0, body.Length - 4));
        if (stored != actual)
            throw new W154Exception($"quadro {index} com crc32 diferente.");

        if (!keyframe)
        {
            for (int band = 0; band < PictureContract.BandCount; band++)
            {
                int off = band * PictureContract.BandBytes;
                bool differs = !decoded.AsSpan(off, PictureContract.BandBytes)
                    .SequenceEqual(prior!.AsSpan(off, PictureContract.BandBytes));
                bool listed = (mask & (1u << band)) != 0;
                if (differs != listed)
                    throw new W154Exception($"quadro {index} banda {band} não bate com a diferença real.");
            }
        }

        return decoded;
    }

    static bool CopyRaw(ReadOnlySpan<byte> data, Span<byte> target)
    {
        if (data.Length != target.Length)
            return false;
        data.CopyTo(target);
        return true;
    }

    static int PopCount(uint value)
    {
        int count = 0;
        while (value != 0)
        {
            count += (int)(value & 1);
            value >>= 1;
        }

        return count;
    }

    static ushort ReadU16(byte[] data, ref int pos)
    {
        ushort value = (ushort)(data[pos] | (data[pos + 1] << 8));
        pos += 2;
        return value;
    }

    static uint ReadU32(byte[] data, ref int pos)
    {
        uint value = (uint)(data[pos] | (data[pos + 1] << 8) | (data[pos + 2] << 16) | (data[pos + 3] << 24));
        pos += 4;
        return value;
    }

    static void ReadExact(Stream stream, Span<byte> dst)
    {
        int read = 0;
        while (read < dst.Length)
        {
            int n = stream.Read(dst[read..]);
            if (n == 0)
                throw new W154Exception("arquivo acabou antes do esperado.");
            read += n;
        }
    }
}
