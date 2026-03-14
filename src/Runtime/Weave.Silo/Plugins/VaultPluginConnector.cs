using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Weave.Security.Vault;
using Weave.Shared.Secrets;
using Weave.Workspaces.Models;
using Weave.Workspaces.Plugins;

namespace Weave.Silo.Plugins;

/// <summary>
/// Connects the Vault plugin — registers <see cref="VaultSecretProvider"/>
/// backed by the HashiCorp Vault HTTP API.
/// </summary>
public sealed partial class VaultPluginConnector(
    IServiceCollection services,
    ILogger<VaultPluginConnector> logger) : IPluginConnector
{
    public string PluginType => "vault";

    public PluginStatus Connect(string name, PluginDefinition definition)
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

        services.AddHttpClient<VaultSecretProvider>(c =>
        {
            c.BaseAddress = new Uri(address);
            if (token is not null)
                c.DefaultRequestHeaders.Add("X-Vault-Token", token);
        });
        services.AddSingleton<ISecretProvider, VaultSecretProvider>();

        LogVaultConnected(name, address);

        return new PluginStatus
        {
            Name = name,
            Type = PluginType,
            IsConnected = true,
            Info = new Dictionary<string, string> { ["address"] = address }
        };
    }

    public PluginStatus Disconnect(string name)
    {
        return new PluginStatus { Name = name, Type = PluginType, IsConnected = false };
    }

    public PluginStatus GetStatus(string name)
    {
        return new PluginStatus { Name = name, Type = PluginType, IsConnected = true };
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Vault plugin '{Name}' connected — server at {Address}")]
    private partial void LogVaultConnected(string name, string address);
}
