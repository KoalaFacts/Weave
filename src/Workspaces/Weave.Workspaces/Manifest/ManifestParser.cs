using System.Collections.Frozen;
using System.Text.Json;
using Weave.Workspaces.Models;

namespace Weave.Workspaces.Manifest;

public interface IManifestParser
{
    WorkspaceManifest Parse(string json);
    WorkspaceManifest ParseFile(string path);
    string Serialize(WorkspaceManifest manifest);
    IReadOnlyList<string> Validate(WorkspaceManifest manifest);
}

public sealed class ManifestParser : IManifestParser
{
    private static readonly FrozenSet<string> ValidToolTypes =
        FrozenSet.ToFrozenSet(["mcp", "dapr", "openapi", "cli", "library"]);

    private static readonly FrozenSet<string> ValidPluginTypes =
        FrozenSet.ToFrozenSet(["dapr", "vault", "http", "custom"]);

    public WorkspaceManifest Parse(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        var manifest = JsonSerializer.Deserialize(json, ManifestJsonContext.Default.WorkspaceManifest)
            ?? throw new JsonException("Failed to deserialize workspace manifest.");

        // STJ source gen may leave collection properties null when keys are absent from JSON.
        return manifest with
        {
            Agents = manifest.Agents ?? [],
            Tools = manifest.Tools ?? [],
            Targets = manifest.Targets ?? [],
            Plugins = manifest.Plugins ?? []
        };
    }

    public WorkspaceManifest ParseFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var json = File.ReadAllText(path);
        return Parse(json);
    }

    public string Serialize(WorkspaceManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return JsonSerializer.Serialize(manifest, ManifestJsonContext.Default.WorkspaceManifest);
    }

    public IReadOnlyList<string> Validate(WorkspaceManifest manifest)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(manifest.Version))
            errors.Add("'version' is required.");

        if (string.IsNullOrWhiteSpace(manifest.Name))
            errors.Add("'name' is required.");

        if (manifest.Version is not "1.0")
            errors.Add($"Unsupported manifest version '{manifest.Version}'. Expected '1.0'.");

        foreach (var (agentName, agent) in manifest.Agents)
        {
            if (string.IsNullOrWhiteSpace(agent.Model))
                errors.Add($"Agent '{agentName}': 'model' is required.");

            foreach (var toolRef in agent.Tools)
            {
                if (!manifest.Tools.ContainsKey(toolRef))
                    errors.Add($"Agent '{agentName}' references undefined tool '{toolRef}'.");
            }

            if (agent.Heartbeat is { } hb && string.IsNullOrWhiteSpace(hb.Cron))
                errors.Add($"Agent '{agentName}': heartbeat 'cron' is required when heartbeat is configured.");
        }

        foreach (var (toolName, tool) in manifest.Tools)
        {
            if (string.IsNullOrWhiteSpace(tool.Type))
                errors.Add($"Tool '{toolName}': 'type' is required.");

            if (!ValidToolTypes.Contains(tool.Type))
                errors.Add($"Tool '{toolName}': invalid type '{tool.Type}'. Must be one of: {string.Join(", ", ValidToolTypes)}.");
        }

        foreach (var (targetName, target) in manifest.Targets)
        {
            if (string.IsNullOrWhiteSpace(target.Runtime))
                errors.Add($"Target '{targetName}': 'runtime' is required.");
        }

        foreach (var (pluginName, plugin) in manifest.Plugins)
        {
            if (string.IsNullOrWhiteSpace(plugin.Type))
                errors.Add($"Plugin '{pluginName}': 'type' is required.");

            if (!ValidPluginTypes.Contains(plugin.Type))
                errors.Add($"Plugin '{pluginName}': invalid type '{plugin.Type}'. Must be one of: {string.Join(", ", ValidPluginTypes)}.");
        }

        return errors;
    }
}
