using System.Buffers.Binary;

namespace W154Format;

public static class FrameEncoder
{
    public static byte[] Build(bool keyframe, ReadOnlySpan<ushort> palette, ReadOnlySpan<byte> indices, ReadOnlySpan<byte> previous)
    {
        var body = new List<byte>(keyframe ? PictureContract.MaxBodyBytes : 4096);
        body.Add((byte)(keyframe ? 3 : 0));
        body.Add(0);

        if (keyframe)
        {
            if (palette.Length < PictureContract.PaletteColors)
                throw new W154Exception("palette com menos de 256 entradas.");
            WriteU16(body, PictureContract.RowCount);
            for (int i = 0; i < PictureContract.RowMaskBytes; i++)
                body.Add(0xFF);
            for (int i = 0; i < PictureContract.PaletteColors; i++)
                WriteU16(body, palette[i]);
            for (int row = 0; row < PictureContract.RowCount; row++)
            {
                int off = row * PictureContract.RowBytes;
                RowCodec.Append(body, indices.Slice(off, PictureContract.RowBytes), ReadOnlySpan<byte>.Empty, keyframe: true);
            }
        }
        else
        {
            ushort count = 0;
            var mask = new byte[PictureContract.RowMaskBytes];
            var records = new List<byte>();
            for (int row = 0; row < PictureContract.RowCount; row++)
            {
                int off = row * PictureContract.RowBytes;
                ReadOnlySpan<byte> current = indices.Slice(off, PictureContract.RowBytes);
                ReadOnlySpan<byte> prior = previous.Slice(off, PictureContract.RowBytes);
                if (current.SequenceEqual(prior))
                    continue;
                mask[row >> 3] |= (byte)(1 << (row & 7));
                count++;
                RowCodec.Append(records, current, prior, keyframe: false);
            }

            WriteU16(body, count);
            body.AddRange(mask);
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

public sealed class W154Draft : IDisposable
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

public static class W154File
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
        if (!PictureContract.FrameUsOk(frameUs))
            throw new W154Exception("frame_us fora de 12500..1000000.");

        long videoEnd = PictureContract.HeaderSize + (lengths.Length * 4L);
        foreach (int length in lengths)
        {
            videoEnd += 4L + length;
            if (videoEnd > uint.MaxValue)
                throw new W154Exception("o arquivo passa de 4 GiB e o offset u32 não cabe.");
        }

        uint audioOffset = 0;
        byte[]? adpcm = null;
        uint pcmCrc = 0;
        if (pcm is not null)
        {
            AudioPcm.RequireRate(sampleRate);
            int sampleCount = AudioPcm.SampleCount(lengths.Length, frameUs, sampleRate);
            var fitted = new byte[sampleCount * 2];
            if (pcm.CanSeek)
                pcm.Position = 0;
            AudioPcm.ReadFitted(pcm, fitted);
            adpcm = ImaAdpcm.Encode(fitted);
            pcmCrc = Crc32.Compute(adpcm);
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
                BinaryPrimitives.WriteUInt16LittleEndian(header[14..], PictureContract.BandCountMax);
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

                if (adpcm is not null)
                {
                    Span<byte> audio = stackalloc byte[AudioPcm.HeaderBytes];
                    BinaryPrimitives.WriteUInt32LittleEndian(audio, (uint)adpcm.Length);
                    BinaryPrimitives.WriteUInt32LittleEndian(audio[4..], (uint)sampleRate);
                    BinaryPrimitives.WriteUInt16LittleEndian(audio[8..], AudioPcm.Channels);
                    BinaryPrimitives.WriteUInt16LittleEndian(audio[10..], AudioPcm.FormatIma);
                    BinaryPrimitives.WriteUInt32LittleEndian(audio[12..], pcmCrc);
                    output.Write(audio);
                    output.Write(adpcm);
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
            throw new W154Exception("version diferente de 2.");
        if (BinaryPrimitives.ReadUInt16LittleEndian(header[6..]) != PictureContract.HeaderSize)
            throw new W154Exception("header_size diferente de 32.");
        if (BinaryPrimitives.ReadUInt16LittleEndian(header[8..]) != PictureContract.Width
            || BinaryPrimitives.ReadUInt16LittleEndian(header[10..]) != PictureContract.Height)
            throw new W154Exception("width ou height diferente de 240.");
        if (BinaryPrimitives.ReadUInt16LittleEndian(header[12..]) != PictureContract.BandRows
            || BinaryPrimitives.ReadUInt16LittleEndian(header[14..]) != PictureContract.BandCountMax)
            throw new W154Exception("band_rows ou band_count_max diferente do contrato.");

        uint frameUs = BinaryPrimitives.ReadUInt32LittleEndian(header[16..]);
        uint frameCount = BinaryPrimitives.ReadUInt32LittleEndian(header[20..]);
        uint audioOffset = BinaryPrimitives.ReadUInt32LittleEndian(header[28..]);
        if (BinaryPrimitives.ReadUInt32LittleEndian(header[24..]) != 0)
            throw new W154Exception("flags do header não são 0.");
        if (frameCount < 1)
            throw new W154Exception("frame_count menor que 1.");
        if (!PictureContract.FrameUsOk((int)frameUs))
            throw new W154Exception("frame_us fora de 12500..1000000.");

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
        if (pcmBytes == 0 || (pcmBytes & 3) != 0)
            throw new W154Exception("pcm_bytes inválido.");
        if (!AudioPcm.IsRate((int)sampleRate))
            throw new W154Exception("sample_rate fora de 16000, 24000 e 48000.");
        if (channels != AudioPcm.Channels)
            throw new W154Exception("channels diferente de 1.");
        if (format != AudioPcm.FormatIma)
            throw new W154Exception("sample_format diferente de 1.");

        int sampleCount = AudioPcm.SampleCount(frameCount, frameUs, (int)sampleRate);
        if (pcmBytes != (uint)ImaAdpcm.PayloadBytes(sampleCount))
            throw new W154Exception("pcm_bytes não cobre a duração do vídeo.");
        if ((long)pcmBytes > stream.Length - stream.Position)
            throw new W154Exception("arquivo acabou antes do PCM.");

        var payload = new byte[pcmBytes];
        ReadExact(stream, payload);
        if (!ImaAdpcm.Accepts(payload, sampleCount))
            throw new W154Exception("bloco ADPCM inválido.");
        uint actual = Crc32.Compute(payload);
        if (stored != actual)
            throw new W154Exception("crc32 do PCM diferente.");
        if (stream.Position != stream.Length)
            throw new W154Exception("bytes sobrando depois do PCM.");
    }

    static byte[] DecodeFrame(int index, int gop, byte[] body, byte[]? previous)
    {
        if (body.Length < PictureContract.PreambleBytes + 4)
            throw new W154Exception($"quadro {index} menor que o preâmbulo.");
        int pos = 0;
        ushort flags = ReadU16(body, ref pos);
        ushort rowCount = ReadU16(body, ref pos);
        var mask = body.AsSpan(pos, PictureContract.RowMaskBytes);
        pos += PictureContract.RowMaskBytes;
        if ((flags & ~0x3) != 0)
            throw new W154Exception($"quadro {index} tem flag além dos bits 0 e 1.");
        if (PopCount(mask) != rowCount)
            throw new W154Exception($"quadro {index} tem row_count diferente da row_mask.");

        bool keyframe = index % gop == 0;
        if (keyframe)
        {
            if (flags != 3 || rowCount != PictureContract.RowCount || !MaskFull(mask))
                throw new W154Exception($"quadro {index} deveria ser keyframe completo.");
            if (pos + PictureContract.PaletteBytes > body.Length)
                throw new W154Exception($"quadro {index} sem paleta.");
            pos += PictureContract.PaletteBytes;
        }
        else if (flags != 0)
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
        for (int row = 0; row < PictureContract.RowCount; row++)
        {
            if ((mask[row >> 3] & (1 << (row & 7))) == 0)
                continue;
            if (pos + 4 > body.Length - 4)
                throw new W154Exception($"quadro {index} acabou no meio de uma linha.");
            byte codec = body[pos++];
            sbyte dx = (sbyte)body[pos++];
            ushort comp = ReadU16(body, ref pos);
            if (codec > RowCodec.Left)
                throw new W154Exception($"quadro {index} linha {row} com codec desconhecido.");
            if (keyframe && codec == RowCodec.Residual)
                throw new W154Exception($"quadro {index} linha {row} residual num keyframe.");
            if (!keyframe && codec == RowCodec.Left)
                throw new W154Exception($"quadro {index} linha {row} delta à esquerda num delta.");
            if (codec == RowCodec.Raw && (comp != PictureContract.RowBytes || dx != 0))
                throw new W154Exception($"quadro {index} linha {row} raw sem 240 bytes.");
            if (codec != RowCodec.Raw && (comp == 0 || comp >= PictureContract.RowBytes))
                throw new W154Exception($"quadro {index} linha {row} comprimida não é menor que 240.");
            if (pos + comp > body.Length - 4)
                throw new W154Exception($"quadro {index} linha {row} passa do body.");

            var data = body.AsSpan(pos, comp);
            pos += comp;
            int pad = (4 - (comp & 3)) & 3;
            for (int p = 0; p < pad; p++)
            {
                if (pos >= body.Length - 4 || body[pos++] != 0)
                    throw new W154Exception($"quadro {index} linha {row} com pad diferente de zero.");
            }

            int off = row * PictureContract.RowBytes;
            var target = decoded.AsSpan(off, PictureContract.RowBytes);
            ReadOnlySpan<byte> pred = prior is null ? ReadOnlySpan<byte>.Empty : prior.AsSpan(off, PictureContract.RowBytes);
            if (!RowCodec.TryApply(codec, dx, data, target, pred))
                throw new W154Exception($"quadro {index} linha {row} não decodifica para 240 bytes.");
            seen++;
        }

        if (seen != rowCount)
            throw new W154Exception($"quadro {index} não tem os records da row_mask.");
        if (pos != body.Length - 4)
            throw new W154Exception($"quadro {index} tem lixo antes do crc32.");

        uint stored = ReadU32(body, ref pos);
        uint actual = Crc32.Compute(body.AsSpan(0, body.Length - 4));
        if (stored != actual)
            throw new W154Exception($"quadro {index} com crc32 diferente.");

        if (!keyframe)
        {
            for (int row = 0; row < PictureContract.RowCount; row++)
            {
                int off = row * PictureContract.RowBytes;
                bool differs = !decoded.AsSpan(off, PictureContract.RowBytes)
                    .SequenceEqual(prior!.AsSpan(off, PictureContract.RowBytes));
                bool listed = (mask[row >> 3] & (1 << (row & 7))) != 0;
                if (differs != listed)
                    throw new W154Exception($"quadro {index} linha {row} não bate com a diferença real.");
            }
        }

        return decoded;
    }

    static bool MaskFull(ReadOnlySpan<byte> mask)
    {
        for (int i = 0; i < mask.Length; i++)
        {
            if (mask[i] != 0xFF)
                return false;
        }

        return true;
    }

    static int PopCount(ReadOnlySpan<byte> mask)
    {
        int count = 0;
        for (int i = 0; i < mask.Length; i++)
        {
            int bits = mask[i];
            while (bits != 0)
            {
                count += bits & 1;
                bits >>= 1;
            }
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
