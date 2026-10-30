internal sealed class CoverageSourcePaths(string projectDirectory)
{
    private readonly string[] _files = Directory.GetFiles(projectDirectory, "*", SearchOption.AllDirectories)
        .Select(path => CoverageInventory.Normalize(Path.GetRelativePath(Environment.CurrentDirectory, path)))
        .Where(path => !path.Split('/').Contains("bin", StringComparer.Ordinal))
        .ToArray();

    public string Resolve(string filename, string[] sources)
    {
        if (string.IsNullOrWhiteSpace(filename))
            throw new InvalidDataException("Owned coverage class has no source filename.");
        filename = CoverageInventory.Normalize(filename);
        var representations = new[] { filename }.Concat(sources.Select(source => $"{source.TrimEnd('/')}/{filename}"));
        var matches = new HashSet<string>(StringComparer.Ordinal);
        foreach (var representation in representations)
        {
            var normalized = CoverageInventory.Normalize(Path.GetFullPath(representation));
            foreach (var file in _files)
                if (normalized.EndsWith('/' + file, StringComparison.Ordinal))
                    matches.Add(file);
        }
        if (matches.Count != 1)
            throw new InvalidDataException($"Unresolved or ambiguous owned source '{filename}' in {projectDirectory} ({matches.Count} matches).");
        return matches.Single();
    }

    public static bool IsExcluded(string filename)
    {
        filename = '/' + CoverageInventory.Normalize(filename).TrimStart('/');
        return filename.Contains("/obj/", StringComparison.Ordinal)
            || filename.EndsWith(".g.cs", StringComparison.Ordinal)
            || filename.Contains("/Models/", StringComparison.Ordinal)
            || filename.Contains("Surrogate", StringComparison.Ordinal)
            || filename.EndsWith("Contracts.cs", StringComparison.Ordinal)
            || filename.EndsWith("/Program.cs", StringComparison.Ordinal);
    }
}
