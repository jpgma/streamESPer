using System.Text;

namespace W154Convert;

static class Program
{
    static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        try
        {
            if (args.Length == 1 && args[0] == "selftest")
                return SelfTest.Run();
            if (args.Any(static arg => arg is "--help" or "-h" or "help"))
            {
                PrintHelp();
                return 0;
            }

            RejectUnknown(args);
            ConvertRequest request = HasFlag(args, "--input") ? Parse(args) : Prompt(args);
            return Converter.Run(request, Console.Out);
        }
        catch (W154Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    static ConvertRequest Prompt(string[] args)
    {
        string? inputDefault = OptionalFlag(args, "--input");
        string input = AskRequired("Arquivo de vídeo", inputDefault);
        string outputDefault = OptionalFlag(args, "--output") ?? Path.ChangeExtension(input, ".w154");
        string output = Ask("Saída (.w154)", outputDefault);
        int fps = AskInt("fps", OptionalInt(args, "--fps") ?? 24);
        FitMode fit = AskFit(OptionalFlag(args, "--fit"));
        DitherMode dither = AskDither(OptionalDither(args) ?? DitherMode.None);
        bool includeAudio = AskYes("Incluir áudio (S/n)", !HasFlag(args, "--no-audio"));
        int sampleRate = OptionalInt(args, "--sample-rate") ?? AudioPcm.DefaultSampleRate;
        if (includeAudio)
            sampleRate = AskSampleRate(sampleRate);
        return Build(input, output, fps, fit, dither, HasFlag(args, "--allow-over-budget"), sampleRate, includeAudio);
    }

    static ConvertRequest Parse(string[] args)
    {
        string input = RequiredFlag(args, "--input");
        string output = OptionalFlag(args, "--output") ?? Path.ChangeExtension(input, ".w154");
        int fps = OptionalInt(args, "--fps") ?? 24;
        FitMode fit = ParseFit(OptionalFlag(args, "--fit") ?? "cover");
        int sampleRate = OptionalInt(args, "--sample-rate") ?? AudioPcm.DefaultSampleRate;
        return Build(input, output, fps, fit, OptionalDither(args) ?? DitherMode.None, HasFlag(args, "--allow-over-budget"), sampleRate, !HasFlag(args, "--no-audio"));
    }

    static void RejectUnknown(string[] args)
    {
        var known = new HashSet<string>(StringComparer.Ordinal)
        {
            "--input", "--output", "--fps", "--fit", "--dither", "--allow-over-budget", "--sample-rate", "--no-audio",
        };
        for (int i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
                throw new W154Exception($"argumento desconhecido: {args[i]}");
            string name = FlagName(args[i]);
            if (!known.Contains(name))
                throw new W154Exception($"argumento desconhecido: {args[i]}");
            if (name is "--allow-over-budget" or "--no-audio" || args[i].Contains('='))
                continue;
            if (name == "--dither")
            {
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    i++;
                continue;
            }
            if (FlagValue(args, i) is null)
                throw new W154Exception($"falta o valor de {name}.");
            i++;
        }
    }

    static ConvertRequest Build(string input, string output, int fps, FitMode fit, DitherMode dither, bool allowOverBudget, int sampleRate, bool includeAudio)
    {
        input = Unquote(input);
        output = Unquote(output);
        if (!File.Exists(input))
            throw new W154Exception($"arquivo não encontrado: {input}");
        _ = PictureContract.FrameUs(fps);
        AudioPcm.RequireRate(sampleRate);
        return new ConvertRequest(Path.GetFullPath(input), Path.GetFullPath(output), fps, fit, dither, allowOverBudget, sampleRate, includeAudio);
    }

    static string AskRequired(string label, string? current)
    {
        while (true)
        {
            string value = Ask(label, current);
            if (!string.IsNullOrWhiteSpace(value))
                return value;
            Console.Error.WriteLine("informe um caminho.");
        }
    }

    static string Ask(string label, string? current)
    {
        if (string.IsNullOrWhiteSpace(current))
            Console.Write(label + ": ");
        else
            Console.Write($"{label} [{current}]: ");
        string? line = Console.ReadLine();
        if (string.IsNullOrWhiteSpace(line))
            return current ?? "";
        return Unquote(line);
    }

    static string Unquote(string value)
    {
        value = value.Trim();
        if (value.Length >= 2 && value[0] == value[^1] && value[0] is '"' or '\'')
            return value[1..^1].Trim();
        return value;
    }

    static int AskInt(string label, int current)
    {
        while (true)
        {
            string text = Ask(label, current.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (int.TryParse(text, out int value) && value is >= 1 and <= PictureContract.MaxFps)
                return value;
            Console.Error.WriteLine("use um fps inteiro de 1 a 80.");
        }
    }

    static int AskSampleRate(int current)
    {
        while (true)
        {
            string text = Ask("Taxa de áudio", current.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int value)
                && AudioPcm.IsRate(value))
                return value;
            Console.Error.WriteLine("use 16000, 24000 ou 48000.");
        }
    }

    static FitMode AskFit(string? current)
    {
        FitMode fallback = current is null ? FitMode.Cover : ParseFit(current);
        Console.WriteLine("Proporção 1:1:");
        Console.WriteLine("  cover   — recorta o centro");
        Console.WriteLine("  contain — encaixa e completa com barras pretas");
        while (true)
        {
            string text = Ask("Proporção", fallback == FitMode.Cover ? "cover" : "contain");
            if (FitModeParser.TryParse(text, out FitMode fit))
                return fit;
            Console.Error.WriteLine("use cover ou contain.");
        }
    }

    static DitherMode AskDither(DitherMode current)
    {
        while (true)
        {
            string text = Ask("Dither (none/ordered/fs)", DitherModeParser.Name(current));
            if (DitherModeParser.TryParse(text, out DitherMode mode))
                return mode;
            Console.Error.WriteLine("use none, ordered ou fs.");
        }
    }

    static DitherMode? OptionalDither(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (FlagName(args[i]) != "--dither")
                continue;
            string? text = FlagValue(args, i);
            if (text is null)
                return DitherMode.Fs;
            if (!DitherModeParser.TryParse(text, out DitherMode mode))
                throw new W154Exception("dither deve ser none, ordered ou fs.");
            return mode;
        }

        return null;
    }

    static bool AskYes(string label, bool current)
    {
        string text = Ask(label, current ? "s" : "n");
        return text.ToLowerInvariant() is "s" or "sim" or "y" or "yes";
    }

    static FitMode ParseFit(string text)
    {
        if (!FitModeParser.TryParse(text, out FitMode fit))
            throw new W154Exception("fit deve ser cover ou contain.");
        return fit;
    }

    static void PrintHelp()
    {
        Console.WriteLine("""
            w154 converte um vídeo para .w154 (240x240, paleta, bandas de 8 linhas).

            Sem --input, o programa pergunta o resto.
            Proporção 1:1:
              cover    recorta o centro (padrão)
              contain  encaixa e completa com barras pretas

            w154
            w154 --input video.mp4 --output video.w154 --fps 24 --fit cover
            w154 --input video.mp4 --dither ordered
            w154 --input video.mp4 --fit contain --dither fs --allow-over-budget
            w154 --input video.mp4 --sample-rate 16000 --no-audio
            w154 selftest

            --fps                  inteiro de 1 a 80 (padrão 24)
            --dither               none, ordered ou fs (padrão none). Sozinho, fs
            --sample-rate          16000, 24000 ou 48000 (padrão 48000), mono s16le
            --no-audio             não grava PCM, mesmo se a origem tiver áudio
            --allow-over-budget    sai 0 mesmo se o pior segundo passar de 16 Mbit/s
            """);
    }

    static bool HasFlag(string[] args, string name)
    {
        foreach (string arg in args)
        {
            if (FlagName(arg) == name)
                return true;
        }

        return false;
    }

    static string RequiredFlag(string[] args, string name)
    {
        return OptionalFlag(args, name) ?? throw new W154Exception($"falta {name}.");
    }

    static string? OptionalFlag(string[] args, string name)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (FlagName(args[i]) != name)
                continue;
            return FlagValue(args, i);
        }

        return null;
    }

    static int? OptionalInt(string[] args, string name)
    {
        string? text = OptionalFlag(args, name);
        if (text is null)
            return null;
        if (!int.TryParse(text, out int value))
            throw new W154Exception($"{name} precisa ser um inteiro.");
        return value;
    }

    static string FlagName(string arg)
    {
        int eq = arg.IndexOf('=');
        return eq < 0 ? arg : arg[..eq];
    }

    static string? FlagValue(string[] args, int index)
    {
        string arg = args[index];
        int eq = arg.IndexOf('=');
        if (eq >= 0)
            return arg[(eq + 1)..];
        string name = FlagName(arg);
        if (name is "--allow-over-budget" or "--no-audio")
            return "true";
        if (name == "--dither")
        {
            if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
                return null;
            return args[index + 1];
        }
        if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            return null;
        return args[index + 1];
    }
}
