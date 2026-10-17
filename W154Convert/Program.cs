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
        bool dither = AskYes("Dither (s/N)", HasFlag(args, "--dither"));
        return Build(input, output, fps, fit, dither, HasFlag(args, "--allow-over-budget"));
    }

    static ConvertRequest Parse(string[] args)
    {
        string input = RequiredFlag(args, "--input");
        string output = OptionalFlag(args, "--output") ?? Path.ChangeExtension(input, ".w154");
        int fps = OptionalInt(args, "--fps") ?? 24;
        FitMode fit = ParseFit(OptionalFlag(args, "--fit") ?? "cover");
        return Build(input, output, fps, fit, HasFlag(args, "--dither"), HasFlag(args, "--allow-over-budget"));
    }

    static void RejectUnknown(string[] args)
    {
        var known = new HashSet<string>(StringComparer.Ordinal)
        {
            "--input", "--output", "--fps", "--fit", "--dither", "--allow-over-budget",
        };
        for (int i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
                throw new W154Exception($"argumento desconhecido: {args[i]}");
            string name = FlagName(args[i]);
            if (!known.Contains(name))
                throw new W154Exception($"argumento desconhecido: {args[i]}");
            if (name is "--dither" or "--allow-over-budget" || args[i].Contains('='))
                continue;
            if (FlagValue(args, i) is null)
                throw new W154Exception($"falta o valor de {name}.");
            i++;
        }
    }

    static ConvertRequest Build(string input, string output, int fps, FitMode fit, bool dither, bool allowOverBudget)
    {
        if (!File.Exists(input))
            throw new W154Exception($"arquivo não encontrado: {input}");
        _ = PictureContract.FrameUs(fps);
        return new ConvertRequest(Path.GetFullPath(input), Path.GetFullPath(output), fps, fit, dither, allowOverBudget);
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
        return line.Trim();
    }

    static int AskInt(string label, int current)
    {
        while (true)
        {
            string text = Ask(label, current.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (int.TryParse(text, out int value) && value is >= 1 and <= 60)
                return value;
            Console.Error.WriteLine("use um fps inteiro de 1 a 60.");
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
            w154 --input video.mp4 --fit contain --dither --allow-over-budget
            w154 selftest

            --fps                  inteiro de 1 a 60 (padrão 24)
            --dither               Floyd-Steinberg (padrão desligado)
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
        if (name is "--dither" or "--allow-over-budget")
            return "true";
        if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            return null;
        return args[index + 1];
    }
}
