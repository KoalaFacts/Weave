using Microsoft.Extensions.Logging;
using Weave.Security.Tokens;
using Weave.Security.Vault;
using Weave.Shared.Plugins;
using Weave.Workspaces.Models;
using Weave.Workspaces.Plugins;

namespace Weave.Silo.Plugins;

/// <summary>
/// Connects the Vault plugin — swaps in <see cref="VaultSecretProvider"/>
/// via the broker. Hot-swappable: disconnect reverts to the default provider.
/// </summary>
public sealed partial class VaultPluginConnector(
    PluginServiceBroker broker,
    IHttpClientFactory httpClientFactory,
    ICapabilityTokenService tokenService,
    ILoggerFactory loggerFactory) : IPluginConnector
{
    private readonly ILogger<VaultPluginConnector> _logger = loggerFactory.CreateLogger<VaultPluginConnector>();

    public string PluginType => "vault";

    public async Task<PluginStatus> ConnectAsync(string name, PluginDefinition definition)
    {
        var address = definition.Config.GetValueOrDefault("address");
        if (string.IsNullOrWhiteSpace(address))
        {
            return new PluginStatus
            {
                Name = name,
                Type = PluginType,
                IsConnected = false,
                Error = "Vault 'address' is required in plugin config."
            };
        }

        var token = definition.Config.GetValueOrDefault("token");

        // Use a unique client name per connect to avoid header accumulation across swaps.
        // IHttpClientFactory creates a fresh HttpClient each time (handlers are pooled, but
        // DefaultRequestHeaders are per-instance).
        var clientName = $"vault-plugin:{name}:{Guid.NewGuid():N}";
        var httpClient = httpClientFactory.CreateClient(clientName);
        httpClient.BaseAddress = new Uri(address);
        if (token is not null)
            httpClient.DefaultRequestHeaders.Add("X-Vault-Token", token);

        var provider = new VaultSecretProvider(
            httpClient,
            tokenService,
            loggerFactory.CreateLogger<VaultSecretProvider>());
        var previous = broker.Swap<ISecretProvider>(provider);
        await PluginDisposal.DisposeIfNeededAsync(previous);

        LogVaultConnected(name, address);

        return new PluginStatus
        {
            Name = name,
            Type = PluginType,
            IsConnected = true,
            Info = new Dictionary<string, string> { ["address"] = address }
        };
    }

    public async Task<PluginStatus> DisconnectAsync(string name)
    {
        var previous = broker.Swap<ISecretProvider>(null);
        await PluginDisposal.DisposeIfNeededAsync(previous);

        LogVaultDisconnected(name);
        return new PluginStatus { Name = name, Type = PluginType, IsConnected = false };
    }

    public PluginStatus GetStatus(string name)
    {
        var hasVault = broker.Get<ISecretProvider>() is VaultSecretProvider;
        return new PluginStatus { Name = name, Type = PluginType, IsConnected = hasVault };
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Vault plugin '{Name}' connected — server at {Address}")]
    private partial void LogVaultConnected(string name, string address);

    [LoggerMessage(Level = LogLevel.Information, Message = "Vault plugin '{Name}' disconnected")]
    private partial void LogVaultDisconnected(string name);
}
