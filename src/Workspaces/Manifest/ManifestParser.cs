using System.Collections.Frozen;
using System.Text.Json;
namespace Weave.Workspaces.Manifest;

public sealed class ManifestParser : IManifestParser
{
    private static readonly FrozenSet<string> ValidToolTypes =
        FrozenSet.ToFrozenSet(["mcp", "dapr", "openapi", "cli", "library", "direct_http", "filesystem"]);

    private static readonly FrozenSet<string> ValidPluginTypes =
        FrozenSet.ToFrozenSet(["dapr", "dapr_tools", "mcp_tools", "vault", "http", "webhook", "custom"]);

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

        errors.AddRange(ValidateDaprToolDependencies(manifest));
        errors.AddRange(ValidateMcpToolDependencies(manifest));

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

    public static IReadOnlyList<string> ValidateDaprToolDependencies(WorkspaceManifest manifest)
    {
        var errors = new List<string>();
        foreach (var (toolName, tool) in manifest.Tools)
        {
            if (!string.Equals(tool.Type, "dapr", StringComparison.OrdinalIgnoreCase))
                continue;
            if (string.IsNullOrWhiteSpace(tool.Dapr?.AppId))
                errors.Add($"Tool '{toolName}': Dapr appId is required.");
            if (string.IsNullOrWhiteSpace(tool.RequiresPlugin)
                || !manifest.Plugins.TryGetValue(tool.RequiresPlugin, out var requiredPlugin)
                || !string.Equals(requiredPlugin.Type, "dapr_tools", StringComparison.OrdinalIgnoreCase))
                errors.Add($"Tool '{toolName}': requiresPlugin must name a Dapr tools plugin in this manifest.");
            else if (!requiredPlugin.Config.TryGetValue("port", out var portText)
                || !int.TryParse(portText, out var port) || port is < 1 or > 65535)
                errors.Add($"Tool '{toolName}': the Dapr tools installation requires an explicit sidecar port between 1 and 65535.");
        }
        return errors;
    }

    public static IReadOnlyList<string> ValidateMcpToolDependencies(WorkspaceManifest manifest)
    {
        var errors = new List<string>();
        var installations = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (toolName, tool) in manifest.Tools)
        {
            if (!string.Equals(tool.Type, "mcp", StringComparison.OrdinalIgnoreCase)
                || tool.RequiresPlugin is null)
                continue;
            if (string.IsNullOrWhiteSpace(tool.RequiresPlugin)
                || !manifest.Plugins.TryGetValue(tool.RequiresPlugin, out var plugin)
                || !string.Equals(plugin.Type, "mcp_tools", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"Tool '{toolName}': requiresPlugin must name an MCP tools plugin in this manifest.");
                continue;
            }
            if (!installations.Add(tool.RequiresPlugin))
                errors.Add($"MCP tools plugin '{tool.RequiresPlugin}' supports one declared tool in this installation.");
            if (tool.Mcp is not { Server: null, Url: not null } mcp || mcp.Args.Count != 0 || mcp.Env.Count != 0
                || !Uri.TryCreate(mcp.Url, UriKind.Absolute, out var uri)
                || uri.Scheme != Uri.UriSchemeHttp || uri.Host != "127.0.0.1" || uri.Port is < 1 or > 65535
                || uri.AbsolutePath != "/mcp" || uri.Query.Length != 0 || uri.Fragment.Length != 0
                || uri.UserInfo.Length != 0 || !mcp.AllowPrivateEndpoints
                || mcp.RequestTimeoutSeconds != 300 || mcp.IdleTimeoutSeconds != 30
                || mcp.MaxResponseBytes != 16 * 1024 * 1024 || mcp.MaxFrameBytes != 1024 * 1024
                || mcp.MaxQueuedFrames != 1024)
                errors.Add($"Tool '{toolName}': this MCP installation requires an explicit HTTP endpoint at http://127.0.0.1:<port>/mcp.");
            foreach (var key in new[] { "server_name", "server_version", "operation" })
                if (string.IsNullOrWhiteSpace(plugin.Config.GetValueOrDefault(key)))
                    errors.Add($"MCP tools plugin '{tool.RequiresPlugin}': '{key}' is required.");
            if (plugin.Config.Keys.Except(["server_name", "server_version", "operation"], StringComparer.Ordinal).Any())
                errors.Add($"MCP tools plugin '{tool.RequiresPlugin}': unsupported configuration field.");
        }
        return errors;
    }
}
