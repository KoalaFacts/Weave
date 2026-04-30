namespace Weave.Cli.Commands;

internal static class ManifestResolver
{
    public static string? Resolve(string? workspace)
    {
        if (workspace is not null)
        {
            var registeredPath = WorkspaceRegistry.Resolve(workspace);
            if (registeredPath is not null)
            {
                var manifestPath = Path.Combine(registeredPath, "workspace.json");
                return File.Exists(manifestPath) ? manifestPath : null;
            }

            return null;
        }

        if (File.Exists("workspace.json"))
            return Path.GetFullPath("workspace.json");

        var dir = Directory.GetCurrentDirectory();
        while (dir is not null)
        {
            var candidate = Path.Combine(dir, "workspace.json");
            if (File.Exists(candidate))
                return candidate;
            dir = Path.GetDirectoryName(dir);
        }

        return null;
    }
}
