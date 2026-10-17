namespace W154Stream;

static class Session
{
    public const uint HelloMagic = 0x34353148;
    public const uint CreditMagic = 0x34353143;
    public const uint ControlMagic = 0x3435314B;
    public const int PacketBytes = 1472;
    public const uint OpReset = 1;
    public const uint OpPause = 2;
    public const uint OpResume = 3;
    public const uint OpAudio = 4;
    public const uint OpEos = 5;
    public const uint FlagUnderrun = 1u << 30;
    public const uint FlagPaused = 1u << 31;
    public const uint FrameMask = 0x3FFFFFFF;
    public const int AudioLeadUs = 80_000;
    public const int SeqWindow = 4096;
}
