using System.Collections.Concurrent;
using Weave.Tools.Connectors;
using Weave.Tools.Discovery;
using Weave.Tools.Tool;
using Weave.Workspaces.Manifest;

namespace Weave.Silo.Plugins;

/// <summary>Owns one verified, installation-scoped HTTP MCP connector per workspace plugin.</summary>
public sealed class McpToolsPluginConnector(
    IToolDiscoveryService discovery,
    ILoggerFactory loggerFactory) : IPluginConnector, IMcpInstallationDispatchGate
{
    private readonly ConcurrentDictionary<string, (string Id, McpToolConnector Connector)> _active =
        new(StringComparer.OrdinalIgnoreCase);

    public string PluginType => "mcp_tools";
    public IReadOnlyList<string> RegistrationKeys(string name) => [$"tool:mcp:{name}"];
    public PluginSchema Schema { get; } = new()
    {
        Type = "mcp_tools",
        Description = "One pinned operation on an external loopback HTTP MCP server",
        Provides = ["tools"],
        Config =
        [
            new PluginConfigField { Name = "url", Description = "Loopback MCP endpoint", Required = true },
            new PluginConfigField { Name = "server_name", Description = "Expected server name", Required = true },
            new PluginConfigField { Name = "server_version", Description = "Expected server version", Required = true },
            new PluginConfigField { Name = "operation", Description = "Declared MCP operation", Required = true },
            new PluginConfigField { Name = "contract_digest", Description = "Pinned operation schema digest" }
        ]
    };

    public async Task<PluginStatus> ConnectAsync(string name, PluginDefinition definition)
    {
        var config = definition.Config;
        var url = config.GetValueOrDefault("url");
        var serverName = config.GetValueOrDefault("server_name");
        var version = config.GetValueOrDefault("server_version");
        var operation = config.GetValueOrDefault("operation");
        var digest = config.GetValueOrDefault("contract_digest");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var endpoint)
            || endpoint.Scheme != Uri.UriSchemeHttp || endpoint.Host != "127.0.0.1"
            || endpoint.Port is < 1 or > 65535 || endpoint.AbsolutePath != "/mcp"
            || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0 || endpoint.UserInfo.Length != 0
            || string.IsNullOrWhiteSpace(serverName) || string.IsNullOrWhiteSpace(version)
            || string.IsNullOrWhiteSpace(operation)
            || digest is not null && (digest.Length != 64 || !digest.All(Uri.IsHexDigit)))
            return new PluginStatus { Name = name, Type = PluginType, Error = "Invalid installed MCP contract." };

        var contract = new McpInstallationContract(url!, serverName!, version!, operation!, digest);
        var connector = new McpToolConnector(
            contract,
            loggerFactory.CreateLogger<McpToolConnector>());
        try
        {
            await connector.ProbeAsync(contract.ProbeConfig());
        }
        catch (Exception error) when (error is IOException or HttpRequestException or InvalidOperationException
            or System.Text.Json.JsonException or TaskCanceledException or UnauthorizedAccessException)
        {
            await connector.DeactivateAsync();
            return new PluginStatus { Name = name, Type = PluginType, Error = error.Message };
        }

        _active.TryGetValue(name, out var previous);
        discovery.Register(name, connector);
        _active[name] = (name, connector);
        if (previous.Connector is not null)
        {
            discovery.UnregisterIfCurrent(previous.Id, ToolType.Mcp, previous.Connector);
            await previous.Connector.DeactivateAsync();
        }
        return new PluginStatus
        {
            Name = name,
            Type = PluginType,
            IsConnected = true,
            Info = new Dictionary<string, string> { ["contract_digest"] = connector.ContractDigest! }
        };
    }

    public async Task<PluginStatus> DisconnectAsync(string name)
    {
        if (_active.TryGetValue(name, out var active))
        {
            active.Connector.BeginDeactivate();
            _active.TryRemove(name, out _);
            discovery.UnregisterIfCurrent(active.Id, ToolType.Mcp, active.Connector);
            await active.Connector.DeactivateAsync();
        }
        return new PluginStatus { Name = name, Type = PluginType };
    }

    public void BeginDisable(string installationId)
    {
        if (_active.TryGetValue(installationId, out var active))
            active.Connector.BeginDeactivate();
    }

    public PluginStatus GetStatus(string name) => new()
    {
        Name = name,
        Type = PluginType,
        IsConnected = _active.ContainsKey(name)
    };
}
