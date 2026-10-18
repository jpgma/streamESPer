namespace W154Convert;

enum DitherMode
{
    None,
    Ordered,
    Fs,
}

static class DitherModeParser
{
    public static bool TryParse(string text, out DitherMode mode)
    {
        switch (text.Trim().ToLowerInvariant())
        {
            case "none":
                mode = DitherMode.None;
                return true;
            case "ordered":
                mode = DitherMode.Ordered;
                return true;
            case "fs":
                mode = DitherMode.Fs;
                return true;
            default:
                mode = DitherMode.None;
                return false;
        }
    }

    public static string Name(DitherMode mode) => mode switch
    {
        DitherMode.Ordered => "ordered",
        DitherMode.Fs => "fs",
        _ => "none",
    };
}
