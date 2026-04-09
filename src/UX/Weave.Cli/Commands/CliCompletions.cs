using System.CommandLine.Completions;

namespace Weave.Cli.Commands;

internal static class CliCompletions
{
    internal static IEnumerable<CompletionItem> CompleteWorkspaceNames(CompletionContext _)
    {
        var dir = "workspaces";
        if (!Directory.Exists(dir))
            return [];

        return Directory.GetDirectories(dir)
            .Select(d => new CompletionItem(Path.GetFileName(d)));
    }

    internal static IEnumerable<CompletionItem> CompletePresetNames(CompletionContext _) =>
        WorkspacePresets.All.Keys.Select(k => new CompletionItem(k));

    internal static IEnumerable<CompletionItem> CompleteDeployTargets(CompletionContext _) =>
    [
        new("docker-compose"),
        new("kubernetes"),
        new("nomad"),
        new("fly-io"),
        new("github-actions"),
    ];

    internal static IEnumerable<CompletionItem> CompleteToolTypes(CompletionContext _) =>
    [
        new("mcp"),
        new("cli"),
        new("openapi"),
        new("direct_http"),
        new("dapr"),
        new("filesystem"),
    ];

    internal static IEnumerable<CompletionItem> CompleteRuntimeTypes(CompletionContext _) =>
    [
        new("podman"),
        new("docker"),
    ];

    internal static IEnumerable<CompletionItem> CompletePluginTypes(CompletionContext _) =>
    [
        new("dapr"),
        new("vault"),
        new("http"),
        new("custom"),
    ];
}
