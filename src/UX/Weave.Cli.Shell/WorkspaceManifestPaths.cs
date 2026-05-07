using Weave.Workspaces.Manifest;

namespace Weave.Cli.Shell;

/// <summary>
/// Frontend-orchestration helpers for relating a manifest on disk to the
/// silo's view of it. <see cref="PrepareForSilo"/> rewrites relative
/// <c>SystemPromptFile</c> paths to absolute (the silo runs in a separate
/// process and sees no relative paths); <see cref="GetStatePath"/> locates
/// the per-workspace <c>.weave/workspace-id</c> sidecar that records which
/// silo workspace ID maps to a given manifest.
/// </summary>
internal static class WorkspaceManifestPaths
{
    public static WorkspaceManifest PrepareForSilo(WorkspaceManifest manifest, string manifestDirectory)
    {
        return manifest with
        {
            Agents = manifest.Agents.ToDictionary(
                static kvp => kvp.Key,
                kvp => kvp.Value with
                {
                    SystemPromptFile = ResolvePath(manifestDirectory, kvp.Value.SystemPromptFile)
                },
                StringComparer.Ordinal)
        };
    }

    public static string GetStatePath(string manifestPath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(manifestPath))
            ?? throw new InvalidOperationException("Unable to determine the workspace directory.");
        return Path.Combine(directory, ".weave", "workspace-id");
    }

    private static string? ResolvePath(string manifestDirectory, string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path))
            return path;

        return Path.GetFullPath(Path.Combine(manifestDirectory, path));
    }
}
