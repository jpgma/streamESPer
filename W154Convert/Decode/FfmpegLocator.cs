namespace W154Convert;

static class FfmpegLocator
{
    public static string Find()
    {
        string beside = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
        if (File.Exists(beside))
            return beside;

        foreach (string start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var dir = new DirectoryInfo(start);
            while (dir is not null)
            {
                string candidate = Path.Combine(dir.FullName, "tools", "ffmpeg", "win-x64", "ffmpeg.exe");
                if (File.Exists(candidate))
                    return candidate;
                dir = dir.Parent;
            }
        }

        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (pathEnv is not null)
        {
            foreach (string entry in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                string candidate = Path.Combine(entry.Trim(), "ffmpeg.exe");
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        throw new W154Exception(
            "ffmpeg.exe não encontrado. Ele deve estar em tools/ffmpeg/win-x64, ao lado deste programa, ou no PATH.");
    }
}
