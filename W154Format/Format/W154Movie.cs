using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;

namespace W154Format;

public readonly struct W154FrameRef
{
    public W154FrameRef(int bodyOffset, int bodyBytes)
    {
        BodyOffset = bodyOffset;
        BodyBytes = bodyBytes;
    }

    public int BodyOffset { get; }

    public int BodyBytes { get; }
}

public readonly struct W154Audio
{
    public W154Audio(int sampleRate, int pcmBytes, int pcmOffset)
    {
        SampleRate = sampleRate;
        PcmBytes = pcmBytes;
        PcmOffset = pcmOffset;
    }

    public int SampleRate { get; }

    public int PcmBytes { get; }

    public int PcmOffset { get; }

    public bool Present => PcmBytes > 0;
}

public sealed unsafe class W154Movie : IDisposable
{
    readonly MemoryMappedFile _file;
    readonly MemoryMappedViewAccessor _view;
    readonly byte* _ptr;
    readonly W154FrameRef[] _frames;
    int _disposed;

    W154Movie(MemoryMappedFile file, MemoryMappedViewAccessor view, byte* ptr, int frameUs, W154FrameRef[] frames, W154Audio audio)
    {
        _file = file;
        _view = view;
        _ptr = ptr;
        FrameUs = frameUs;
        _frames = frames;
        Audio = audio;
    }

    public int FrameUs { get; }

    public int FrameCount => _frames.Length;

    public W154Audio Audio { get; }

    public ReadOnlySpan<W154FrameRef> Frames => _frames;

    public ReadOnlySpan<byte> Body(int index)
    {
        W154FrameRef frame = _frames[index];
        return new ReadOnlySpan<byte>(_ptr + frame.BodyOffset, frame.BodyBytes);
    }

    public ReadOnlySpan<byte> Pcm =>
        Audio.Present ? new ReadOnlySpan<byte>(_ptr + Audio.PcmOffset, Audio.PcmBytes) : ReadOnlySpan<byte>.Empty;

    public static W154Movie Open(string path)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length < PictureContract.HeaderSize || stream.Length > int.MaxValue)
        {
            stream.Dispose();
            throw new W154Exception("arquivo vazio ou maior que 2 GiB.");
        }

        int length = (int)stream.Length;
        MemoryMappedFile? file = null;
        MemoryMappedViewAccessor? view = null;
        byte* ptr = null;
        var held = false;
        try
        {
            file = MemoryMappedFile.CreateFromFile(stream, null, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, leaveOpen: false);
            stream = null;
            view = file.CreateViewAccessor(0, length, MemoryMappedFileAccess.Read);
            view.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
            held = true;
            var bytes = new ReadOnlySpan<byte>(ptr, length);
            Parse(bytes, out int frameUs, out W154FrameRef[] frames, out W154Audio audio);
            var movie = new W154Movie(file, view, ptr, frameUs, frames, audio);
            file = null;
            view = null;
            held = false;
            return movie;
        }
        finally
        {
            stream?.Dispose();
            if (held)
                view!.SafeMemoryMappedViewHandle.ReleasePointer();
            view?.Dispose();
            file?.Dispose();
        }
    }

    static void Parse(ReadOnlySpan<byte> file, out int frameUs, out W154FrameRef[] frames, out W154Audio audio)
    {
        if (file[0] != (byte)'W' || file[1] != (byte)'1' || file[2] != (byte)'5' || file[3] != (byte)'4')
            throw new W154Exception("magic diferente de W154.");
        if (BinaryPrimitives.ReadUInt16LittleEndian(file[4..]) != PictureContract.Version)
            throw new W154Exception("version diferente de 1.");
        if (BinaryPrimitives.ReadUInt16LittleEndian(file[6..]) != PictureContract.HeaderSize)
            throw new W154Exception("header_size diferente de 32.");
        if (BinaryPrimitives.ReadUInt16LittleEndian(file[8..]) != PictureContract.Width
            || BinaryPrimitives.ReadUInt16LittleEndian(file[10..]) != PictureContract.Height)
            throw new W154Exception("width ou height diferente de 240.");
        if (BinaryPrimitives.ReadUInt16LittleEndian(file[12..]) != PictureContract.BandRows
            || BinaryPrimitives.ReadUInt16LittleEndian(file[14..]) != PictureContract.BandCount)
            throw new W154Exception("band_rows ou band_count_max diferente do contrato.");
        if (BinaryPrimitives.ReadUInt32LittleEndian(file[24..]) != 0)
            throw new W154Exception("flags do header não são 0.");

        uint us = BinaryPrimitives.ReadUInt32LittleEndian(file[16..]);
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(file[20..]);
        uint audioOffset = BinaryPrimitives.ReadUInt32LittleEndian(file[28..]);
        if (count < 1)
            throw new W154Exception("frame_count menor que 1.");
        if (us is < 16667 or > 1_000_000)
            throw new W154Exception("frame_us fora de 16667..1000000.");
        if ((long)PictureContract.HeaderSize + count * 4L > file.Length)
            throw new W154Exception("tabela de offsets não cabe no arquivo.");

        frameUs = (int)us;
        frames = new W154FrameRef[count];
        uint cursor = (uint)(PictureContract.HeaderSize + count * 4);
        for (int i = 0; i < frames.Length; i++)
        {
            int at = PictureContract.HeaderSize + i * 4;
            uint offset = BinaryPrimitives.ReadUInt32LittleEndian(file[at..]);
            if (offset != cursor)
                throw new W154Exception($"offset do quadro {i} não segue o quadro anterior.");
            if (offset + 4 > file.Length)
                throw new W154Exception($"quadro {i} não cabe no arquivo.");
            int bodyBytes = (int)BinaryPrimitives.ReadUInt32LittleEndian(file[(int)offset..]);
            if (bodyBytes is < PictureContract.MinBodyBytes or > PictureContract.MaxBodyBytes)
                throw new W154Exception($"body_bytes do quadro {i} fora do limite.");
            long end = offset + 4L + bodyBytes;
            if (end > file.Length)
                throw new W154Exception($"body do quadro {i} passa do arquivo.");
            frames[i] = new W154FrameRef((int)offset + 4, bodyBytes);
            cursor = (uint)end;
        }

        if (audioOffset == 0)
        {
            if (cursor != file.Length)
                throw new W154Exception("bytes sobrando depois do último quadro.");
            audio = default;
            return;
        }

        if (audioOffset != cursor)
            throw new W154Exception("audio_offset não aponta para o fim do vídeo.");
        if (audioOffset + AudioPcm.HeaderBytes > file.Length)
            throw new W154Exception("bloco de áudio cortado.");
        int audioAt = (int)audioOffset;
        uint pcmBytes = BinaryPrimitives.ReadUInt32LittleEndian(file[audioAt..]);
        uint sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(file[(audioAt + 4)..]);
        ushort channels = BinaryPrimitives.ReadUInt16LittleEndian(file[(audioAt + 8)..]);
        ushort format = BinaryPrimitives.ReadUInt16LittleEndian(file[(audioAt + 10)..]);
        if (pcmBytes == 0 || (pcmBytes & 1) != 0)
            throw new W154Exception("pcm_bytes inválido.");
        if (!AudioPcm.IsRate((int)sampleRate))
            throw new W154Exception("sample_rate fora de 16000, 24000 e 48000.");
        if (channels != AudioPcm.Channels || format != AudioPcm.FormatS16Le)
            throw new W154Exception("PCM que não é mono s16le.");
        int pcmOffset = audioAt + AudioPcm.HeaderBytes;
        if (pcmOffset + pcmBytes != file.Length)
            throw new W154Exception("o PCM não termina no fim do arquivo.");
        audio = new W154Audio((int)sampleRate, (int)pcmBytes, pcmOffset);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _view.SafeMemoryMappedViewHandle.ReleasePointer();
        _view.Dispose();
        _file.Dispose();
    }
}
