namespace W154Stream;

static class Program
{
    public static int Main(string[] args)
    {
        if (Has(args, "--selftest"))
            return SelfTest.Run();

        int http = OptionalInt(args, "--http") ?? 5090;
        int hello = OptionalInt(args, "--hello") ?? 5091;
        string dir = Optional(args, "--dir") ?? Environment.CurrentDirectory;
        dir = Path.GetFullPath(dir);

        var builder = WebApplication.CreateSlimBuilder(args);
        builder.WebHost.UseUrls($"http://0.0.0.0:{http}");
        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonContext.Default);
        });

        var pump = new Pump(dir, hello);
        builder.Services.AddSingleton(pump);
        builder.Services.AddHostedService(sp => sp.GetRequiredService<Pump>());

        var app = builder.Build();
        app.MapGet("/files", () => Results.Json(pump.Files(), AppJsonContext.Default.FileItemArray));
        app.MapPost("/play", (PlayRequest request) =>
        {
            string? error = pump.Play(request.Name);
            return error is null ? Results.Ok() : Results.Text(error, statusCode: StatusCodes.Status400BadRequest);
        });
        app.MapPost("/pause", () =>
        {
            pump.Pause();
            return Results.Ok();
        });
        app.MapPost("/resume", () =>
        {
            pump.Resume();
            return Results.Ok();
        });
        app.MapGet("/status", () => Results.Json(pump.Status(), AppJsonContext.Default.StatusDto));

        Console.WriteLine($"http :{http}  dir {dir}");
        app.Run();
        return 0;
    }

    static bool Has(string[] args, string name)
    {
        foreach (string arg in args)
        {
            if (arg == name)
                return true;
        }

        return false;
    }

    static string? Optional(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == name)
                return args[i + 1];
        }

        return null;
    }

    static int? OptionalInt(string[] args, string name)
    {
        string? text = Optional(args, name);
        if (text is null)
            return null;
        return int.TryParse(text, out int value) ? value : null;
    }
}
