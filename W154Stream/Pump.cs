using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace W154Stream;

sealed class Pump : IHostedService
{
    readonly object _gate = new();
    readonly string _dir;
    readonly int _helloPort;
    readonly List<W154Movie> _dead = new();
    readonly long[] _bodyBefore = new long[Session.SeqWindow];
    readonly byte[] _rx = new byte[64];
    readonly byte[] _tx = new byte[Session.PacketBytes];

    Socket? _socket;
    CancellationTokenSource? _cts;
    Task? _loop;

    W154Movie? _movie;
    string? _name;
    uint _token;
    bool _armed;
    bool _paused;
    bool _needReset;
    bool _needPause;
    bool _needResume;
    bool _joined;
    IPEndPoint? _videoEp;
    IPEndPoint? _audioEp;
    IPEndPoint? _controlEp;
    uint _ringBytes;
    uint _freeBytes;
    uint _expectSeq;
    uint _playFrame;
    bool _devUnderrun;
    int _frameSent;

    long _sentBody;
    uint _videoSeq;
    int _videoFrame;
    int _videoOffset;
    uint _audioSeq;
    long _audioSample;
    long _logAt;

    public Pump(string dir, int helloPort)
    {
        _dir = dir;
        _helloPort = helloPort;
    }

    public FileItem[] Files() => Catalog.List(_dir);

    public string? Play(string name)
    {
        string? path = Catalog.Resolve(_dir, name);
        if (path is null)
            return "arquivo inválido.";

        W154Movie movie;
        try
        {
            movie = W154Movie.Open(path);
        }
        catch (W154Exception ex)
        {
            return ex.Message;
        }

        lock (_gate)
        {
            if (_movie is not null)
                _dead.Add(_movie);
            _movie = movie;
            _name = Path.IsPathFullyQualified(name) ? path : Path.GetFileName(path);
            _token++;
            _armed = true;
            _paused = false;
            _needPause = false;
            _needResume = false;
            _needReset = _joined;
            _frameSent = 0;
        }

        return null;
    }

    public void Pause()
    {
        lock (_gate)
        {
            _paused = true;
            _needPause = true;
            _needResume = false;
        }
    }

    public void Resume()
    {
        lock (_gate)
        {
            _paused = false;
            _needResume = true;
            _needPause = false;
        }
    }

    public StatusDto Status()
    {
        lock (_gate)
        {
            return new StatusDto(_name, _joined, _frameSent, _freeBytes, _paused, _devUnderrun, _token);
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        socket.ReceiveBufferSize = 256 * 1024;
        socket.SendBufferSize = 1024 * 1024;
        socket.Bind(new IPEndPoint(IPAddress.Any, _helloPort));
        _socket = socket;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => Loop(_cts.Token));
        Console.WriteLine($"hello UDP :{_helloPort}");
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_cts is not null)
            await _cts.CancelAsync();
        if (_loop is not null)
        {
            try
            {
                await _loop.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _socket?.Dispose();
        lock (_gate)
        {
            foreach (W154Movie movie in _dead)
                movie.Dispose();
            _dead.Clear();
            _movie?.Dispose();
            _movie = null;
        }
    }

    void Loop(CancellationToken cancel)
    {
        while (!cancel.IsCancellationRequested)
        {
            Drain();
            W154Movie? movie;
            IPEndPoint? videoEp;
            IPEndPoint? audioEp;
            IPEndPoint? controlEp;
            bool doReset;
            bool doPause;
            bool doResume;
            bool paused;
            uint token;
            uint free;
            uint expect;
            uint playFrame;
            lock (_gate)
            {
                for (int i = 0; i < _dead.Count; i++)
                    _dead[i].Dispose();
                _dead.Clear();
                movie = _movie;
                videoEp = _videoEp;
                audioEp = _audioEp;
                controlEp = _controlEp;
                doReset = _needReset && _joined && movie is not null;
                doPause = _needPause && _joined;
                doResume = _needResume && _joined;
                if (doReset)
                    _needReset = false;
                if (doPause)
                    _needPause = false;
                if (doResume)
                    _needResume = false;
                paused = _paused;
                token = _token;
                free = _freeBytes;
                expect = _expectSeq;
                playFrame = _playFrame;
            }

            if (controlEp is null || movie is null)
            {
                Thread.Sleep(5);
                continue;
            }

            bool sent = false;
            if (doReset)
            {
                SendControl(controlEp, Session.OpReset, token, (uint)movie.FrameUs);
                uint rate = movie.Audio.Present ? (uint)movie.Audio.SampleRate : 0;
                SendControl(controlEp, Session.OpAudio, token, rate);
                ZeroCursors();
                free = _ringBytes;
                expect = 0;
                playFrame = 0;
                sent = true;
            }

            if (doPause)
            {
                SendControl(controlEp, Session.OpPause, token, 0);
                sent = true;
            }

            if (doResume)
            {
                SendControl(controlEp, Session.OpResume, token, 0);
                sent = true;
            }

            if (_joined && audioEp is not null && !paused && movie.Audio.Present)
                sent |= SendAudio(movie, audioEp, playFrame);

            if (_joined && videoEp is not null)
                sent |= SendVideo(movie, videoEp, ref free, ref expect);

            if (Environment.TickCount64 - _logAt >= 1000)
            {
                _logAt = Environment.TickCount64;
                Console.WriteLine($"play {playFrame} sent {_videoFrame} free {free} aud {_audioSample} underrun {(_devUnderrun ? 1 : 0)}");
            }

            lock (_gate)
            {
                _frameSent = _videoFrame;
            }

            if (!sent)
                Thread.Sleep(1);
        }
    }

    void Drain()
    {
        Socket? socket = _socket;
        if (socket is null)
            return;
        while (socket.Available > 0)
        {
            EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
            int n;
            try
            {
                n = socket.ReceiveFrom(_rx, SocketFlags.None, ref remote);
            }
            catch (SocketException)
            {
                break;
            }

            if (n < 16 || remote is not IPEndPoint from)
                continue;
            uint magic = BinaryPrimitives.ReadUInt32LittleEndian(_rx);
            if (magic == Session.HelloMagic)
                OnHello(from, _rx);
            else if (magic == Session.CreditMagic)
                OnCredit(_rx);
        }
    }

    void OnHello(IPEndPoint from, byte[] packet)
    {
        ushort video = BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(4));
        ushort audio = BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(6));
        ushort control = BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(8));
        uint ring = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(12));
        if (video == 0 || audio == 0 || control == 0 || ring < PictureContract.MaxBodyBytes)
            return;

        lock (_gate)
        {
            bool same = _joined
                && _videoEp is not null
                && _audioEp is not null
                && _controlEp is not null
                && _videoEp.Port == video
                && _audioEp.Port == audio
                && _controlEp.Port == control
                && _videoEp.Address.Equals(from.Address);
            _joined = true;
            _videoEp = new IPEndPoint(from.Address, video);
            _audioEp = new IPEndPoint(from.Address, audio);
            _controlEp = new IPEndPoint(from.Address, control);
            _ringBytes = ring;
            if (!same)
            {
                _freeBytes = ring;
                _expectSeq = 0;
                _playFrame = 0;
                _devUnderrun = false;
                if (_armed)
                    _needReset = true;
                Console.WriteLine($"hello {from.Address} video {video} audio {audio} control {control} ring {ring}");
            }
        }
    }

    void OnCredit(byte[] packet)
    {
        uint free = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(4));
        uint expect = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(8));
        uint packed = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(12));
        lock (_gate)
        {
            if (!_joined || free > _ringBytes)
                return;
            _freeBytes = free;
            _expectSeq = expect;
            _playFrame = packed & Session.FrameMask;
            _devUnderrun = (packed & Session.FlagUnderrun) != 0;
        }
    }

    bool SendVideo(W154Movie movie, IPEndPoint ep, ref uint free, ref uint expect)
    {
        bool sent = false;
        int count = movie.FrameCount;
        while (count > 0)
        {
            ReadOnlySpan<byte> body = movie.Body(_videoFrame % count);
            int fragCount = RateCheck.Fragments(body.Length);
            int frag = _videoOffset / PictureContract.FragPayload;
            int payload = Math.Min(PictureContract.FragPayload, body.Length - _videoOffset);
            if (payload < 1)
                break;
            // O device reserva frag_count * 1456 ao abrir o body. Começar sem isso vira salto até o próximo keyframe.
            if (_videoOffset == 0 && !RingFits(fragCount * PictureContract.FragPayload, free, expect))
                break;
            if (!CanSend(payload, free, expect))
                break;

            _bodyBefore[_videoSeq & (Session.SeqWindow - 1)] = _sentBody;
            WriteVideo(_tx, _videoSeq, (uint)_videoFrame, (ushort)frag, (ushort)fragCount, body.Slice(_videoOffset, payload));
            if (!Send(_tx, PictureContract.FragHeader + payload, ep))
                break;

            _sentBody += payload;
            _videoOffset += payload;
            _videoSeq++;
            sent = true;
            if (_videoOffset < body.Length)
                continue;
            _videoOffset = 0;
            _videoFrame++;
        }

        return sent;
    }

    bool CanSend(int payload, uint free, uint expect)
    {
        if (!RingFits(payload, free, expect))
            return false;
        long outstanding = Outstanding(expect);
        return outstanding + payload <= Session.FlightBytes;
    }

    bool RingFits(int bytes, uint free, uint expect)
    {
        if (expect > _videoSeq || _videoSeq - expect >= Session.SeqWindow)
            return false;
        long outstanding = Outstanding(expect);
        if (outstanding < 0)
            return false;
        return outstanding + bytes <= free;
    }

    long Outstanding(uint expect)
    {
        long acked = expect == _videoSeq ? _sentBody : _bodyBefore[expect & (Session.SeqWindow - 1)];
        return _sentBody - acked;
    }

    bool SendAudio(W154Movie movie, IPEndPoint ep, uint playFrame)
    {
        int rate = movie.Audio.SampleRate;
        int samples = movie.Audio.PcmBytes / 2;
        if (samples < 1)
            return false;
        long targetUs = (long)playFrame * movie.FrameUs + Session.AudioLeadUs;
        long target = (targetUs * rate) / 1_000_000L;
        bool sent = false;
        ReadOnlySpan<byte> pcm = movie.Pcm;
        int packets = 0;
        while (_audioSample < target && packets < Session.AudioPacketsPerTurn)
        {
            int at = (int)(_audioSample % samples);
            int room = (samples - at) * 2;
            int bytes = (int)Math.Min(PictureContract.FragPayload, Math.Min((long)room, (target - _audioSample) * 2));
            bytes &= ~1;
            if (bytes < 2)
                break;
            ulong timeUs = (ulong)_audioSample * 1_000_000UL / (ulong)rate;
            WriteAudio(_tx, _audioSeq, (uint)timeUs, pcm.Slice(at * 2, bytes));
            if (!Send(_tx, PictureContract.FragHeader + bytes, ep))
                break;
            _audioSample += bytes / 2;
            _audioSeq++;
            packets++;
            sent = true;
        }

        return sent;
    }

    void ZeroCursors()
    {
        _sentBody = 0;
        _videoSeq = 0;
        _videoFrame = 0;
        _videoOffset = 0;
        _audioSeq = 0;
        _audioSample = 0;
        Array.Clear(_bodyBefore);
        lock (_gate)
        {
            _frameSent = 0;
            _freeBytes = _ringBytes;
            _expectSeq = 0;
            _playFrame = 0;
            _devUnderrun = false;
        }
    }

    void SendControl(IPEndPoint ep, uint opcode, uint token, uint arg)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(_tx, Session.ControlMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(_tx.AsSpan(4), opcode);
        BinaryPrimitives.WriteUInt32LittleEndian(_tx.AsSpan(8), token);
        BinaryPrimitives.WriteUInt32LittleEndian(_tx.AsSpan(12), arg);
        Send(_tx, 16, ep);
    }

    static void WriteVideo(byte[] packet, uint seq, uint frame, ushort frag, ushort fragCount, ReadOnlySpan<byte> payload)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(packet, seq);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), frame);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(8), frag);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(10), fragCount);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(12), (ushort)payload.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(14), 0);
        payload.CopyTo(packet.AsSpan(16));
    }

    static void WriteAudio(byte[] packet, uint seq, uint timeUs, ReadOnlySpan<byte> payload)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(packet, seq);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), timeUs);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(8), (ushort)payload.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(10), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(12), 0);
        payload.CopyTo(packet.AsSpan(16));
    }

    bool Send(byte[] packet, int length, IPEndPoint ep)
    {
        try
        {
            _socket!.SendTo(packet, 0, length, SocketFlags.None, ep);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }
}
