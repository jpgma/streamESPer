namespace W154Stream;

public readonly record struct FileItem(string Name, int FrameCount, int FrameUs, bool HasAudio);

static class Catalog
{
    public static FileItem[] List(string dir)
    {
        if (!Directory.Exists(dir))
            return [];

        var found = new List<FileItem>();
        foreach (string path in Directory.EnumerateFiles(dir, "*.w154"))
        {
            try
            {
                using W154Movie movie = W154Movie.Open(path);
                found.Add(new FileItem(Path.GetFileName(path), movie.FrameCount, movie.FrameUs, movie.Audio.Present));
            }
            catch (W154Exception)
            {
            }
        }

        return found.ToArray();
    }

    public static string? Resolve(string dir, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        if (Path.IsPathFullyQualified(name))
        {
            string full = Path.GetFullPath(name);
            if (!full.EndsWith(".w154", StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
                return null;
            return full;
        }
        if (name != Path.GetFileName(name))
            return null;
        string path = Path.GetFullPath(Path.Combine(dir, name));
        string root = Path.GetFullPath(dir);
        if (!root.EndsWith(Path.DirectorySeparatorChar))
            root += Path.DirectorySeparatorChar;
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            return null;
        if (!File.Exists(path))
            return null;
        return path;
    }
}
