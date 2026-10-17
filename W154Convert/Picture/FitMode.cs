namespace W154Convert;

enum FitMode
{
    Cover,
    Contain,
}

static class FitModeParser
{
    public static bool TryParse(string text, out FitMode fit)
    {
        switch (text.Trim().ToLowerInvariant())
        {
            case "cover":
            case "crop":
            case "recorte":
                fit = FitMode.Cover;
                return true;
            case "contain":
            case "pad":
            case "barras":
                fit = FitMode.Contain;
                return true;
            default:
                fit = FitMode.Cover;
                return false;
        }
    }
}
