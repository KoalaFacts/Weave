namespace Weave.Cli.Commands;

internal sealed record PresetDefinition(string Name, string Description, string Model, IReadOnlyList<string> Tools);

internal static class WorkspacePresets
{
    public static readonly IReadOnlyDictionary<string, PresetDefinition> All =
        new Dictionary<string, PresetDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            ["starter"] = new("starter", "One assistant, no tools — the simplest possible workspace.", "claude-sonnet-4-20250514", []),
            ["coding-assistant"] = new("coding-assistant", "An assistant with git and file tools, ready for code tasks.", "claude-sonnet-4-20250514", ["git", "file"]),
            ["research"] = new("research", "An assistant with web and document tools for gathering information.", "claude-sonnet-4-20250514", ["web", "document"]),
            ["multi-agent"] = new("multi-agent", "A supervisor and worker assistants for more complex workflows.", "claude-sonnet-4-20250514", ["git", "file", "web"]),
        };
}
