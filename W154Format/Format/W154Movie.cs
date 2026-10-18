using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;

namespace W154Format;

public readonly struct W154FrameRef
{
    public W154FrameRef(long bodyOffset, int bodyBytes)
    {
        BodyOffset = bodyOffset;
        BodyBytes = bodyBytes;
    }

    public long BodyOffset { get; }

    public int BodyBytes { get; }
}

public readonly struct W154Audio
{
    public W154Audio(int sampleRate, int pcmBytes, long pcmOffset)
    {
        SampleRate = sampleRate;
        PcmBytes = pcmBytes;
        PcmOffset = pcmOffset;
    }

    public int SampleRate { get; }

    public int PcmBytes { get; }

    public long PcmOffset { get; }

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
        return new ReadOnlySpan<byte>(_ptr + (nint)frame.BodyOffset, frame.BodyBytes);
    }

    public ReadOnlySpan<byte> Pcm =>
        Audio.Present ? new ReadOnlySpan<byte>(_ptr + (nint)Audio.PcmOffset, Audio.PcmBytes) : ReadOnlySpan<byte>.Empty;

    public static W154Movie Open(string path)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        long length = stream.Length;
        if (length < PictureContract.HeaderSize || length > uint.MaxValue)
        {
            stream.Dispose();
            throw new W154Exception("arquivo vazio ou maior que 4 GiB.");
        }
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
            Parse(ptr, length, out int frameUs, out W154FrameRef[] frames, out W154Audio audio);
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

    static ReadOnlySpan<byte> At(byte* file, long offset, int count) => new(file + (nint)offset, count);

    static uint U32(byte* file, long offset) => BinaryPrimitives.ReadUInt32LittleEndian(At(file, offset, 4));

    static ushort U16(byte* file, long offset) => BinaryPrimitives.ReadUInt16LittleEndian(At(file, offset, 2));

    static void Parse(byte* file, long length, out int frameUs, out W154FrameRef[] frames, out W154Audio audio)
    {
        if (file[0] != (byte)'W' || file[1] != (byte)'1' || file[2] != (byte)'5' || file[3] != (byte)'4')
            throw new W154Exception("magic diferente de W154.");
        if (U16(file, 4) != PictureContract.Version)
            throw new W154Exception("version diferente de 1.");
        if (U16(file, 6) != PictureContract.HeaderSize)
            throw new W154Exception("header_size diferente de 32.");
        if (U16(file, 8) != PictureContract.Width || U16(file, 10) != PictureContract.Height)
            throw new W154Exception("width ou height diferente de 240.");
        if (U16(file, 12) != PictureContract.BandRows || U16(file, 14) != PictureContract.BandCount)
            throw new W154Exception("band_rows ou band_count_max diferente do contrato.");
        if (U32(file, 24) != 0)
            throw new W154Exception("flags do header não são 0.");

        uint us = U32(file, 16);
        uint count = U32(file, 20);
        uint audioOffset = U32(file, 28);
        if (count < 1)
            throw new W154Exception("frame_count menor que 1.");
        if (us is < 16667 or > 1_000_000)
            throw new W154Exception("frame_us fora de 16667..1000000.");
        if ((long)PictureContract.HeaderSize + count * 4L > length)
            throw new W154Exception("tabela de offsets não cabe no arquivo.");

        frameUs = (int)us;
        frames = new W154FrameRef[count];
        uint cursor = (uint)(PictureContract.HeaderSize + count * 4);
        for (int i = 0; i < frames.Length; i++)
        {
            long at = PictureContract.HeaderSize + i * 4L;
            uint offset = U32(file, at);
            if (offset != cursor)
                throw new W154Exception($"offset do quadro {i} não segue o quadro anterior.");
            if (offset + 4L > length)
                throw new W154Exception($"quadro {i} não cabe no arquivo.");
            uint bodyRaw = U32(file, offset);
            if (bodyRaw < PictureContract.MinBodyBytes || bodyRaw > PictureContract.MaxBodyBytes)
                throw new W154Exception($"body_bytes do quadro {i} fora do limite.");
            long end = offset + 4L + bodyRaw;
            if (end > length)
                throw new W154Exception($"body do quadro {i} passa do arquivo.");
            frames[i] = new W154FrameRef(offset + 4L, (int)bodyRaw);
            cursor = (uint)end;
        }

        if (audioOffset == 0)
        {
            if (cursor != length)
                throw new W154Exception("bytes sobrando depois do último quadro.");
            audio = default;
            return;
        }

        if (audioOffset != cursor)
            throw new W154Exception("audio_offset não aponta para o fim do vídeo.");
        if (audioOffset + (long)AudioPcm.HeaderBytes > length)
            throw new W154Exception("bloco de áudio cortado.");
        uint pcmBytes = U32(file, audioOffset);
        uint sampleRate = U32(file, audioOffset + 4);
        ushort channels = U16(file, audioOffset + 8);
        ushort format = U16(file, audioOffset + 10);
        if (pcmBytes == 0 || (pcmBytes & 1) != 0 || pcmBytes > int.MaxValue)
            throw new W154Exception("pcm_bytes inválido.");
        if (!AudioPcm.IsRate((int)sampleRate))
            throw new W154Exception("sample_rate fora de 16000, 24000 e 48000.");
        if (channels != AudioPcm.Channels || format != AudioPcm.FormatS16Le)
            throw new W154Exception("PCM que não é mono s16le.");
        long pcmOffset = audioOffset + (long)AudioPcm.HeaderBytes;
        if (pcmOffset + pcmBytes != length)
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
