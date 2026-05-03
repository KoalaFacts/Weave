using Weave.Security.Tokens;
using Weave.Silo.Configuration;
using Weave.Silo.Plugins;
using Weave.Workspaces.Models;

namespace Weave.Silo.Startup;

internal sealed class SiloPluginActivator
{
    private readonly IServiceProvider _services;
    private readonly ILogger _logger;
    private readonly WeaveSettings _weaveSettings;

    public SiloPluginActivator(IServiceProvider services, ILogger logger, WeaveSettings weaveSettings)
    {
        _services = services;
        _logger = logger;
        _weaveSettings = weaveSettings;
    }

    public async Task ActivateAsync()
    {
        var pluginRegistry = _services.GetRequiredService<IPluginRegistry>();
        var tokenService = _services.GetRequiredService<ICapabilityTokenService>();

        if (_weaveSettings.Dapr.HttpPort is not null)
        {
            using var source = MintActivatorToken(tokenService, "dapr");
            await pluginRegistry.ConnectAsync("dapr", new PluginDefinition
            {
                Type = "dapr",
                Description = "Auto-detected Dapr sidecar",
                Config = new Dictionary<string, string> { ["port"] = _weaveSettings.Dapr.HttpPort }
            }, source.Token);
        }

        if (_weaveSettings.Vault.Address is not null)
        {
            var vaultConfig = new Dictionary<string, string> { ["address"] = _weaveSettings.Vault.Address };
            if (_weaveSettings.Vault.Token is not null)
                vaultConfig["token"] = _weaveSettings.Vault.Token;

            using var source = MintActivatorToken(tokenService, "vault");
            await pluginRegistry.ConnectAsync("vault", new PluginDefinition
            {
                Type = "vault",
                Description = "Auto-detected Vault server",
                Config = vaultConfig
            }, source.Token);
        }

        foreach (var status in pluginRegistry.GetAll())
        {
            if (status.IsConnected)
                _logger.LogInformation("Plugin '{Name}' ({Type}) active", status.Name, status.Type);
            else
                _logger.LogWarning("Plugin '{Name}' ({Type}) failed: {Error}", status.Name, status.Type, status.Error);
        }
    }

    private static CapabilityTokenSource MintActivatorToken(ICapabilityTokenService tokenService, string pluginName) =>
        tokenService.MintLinked(
            new CapabilityTokenRequest
            {
                WorkspaceId = "silo",
                IssuedTo = "silo/plugin-activator",
                Grants = [$"plugin:invoke:{pluginName}"],
                Lifetime = TimeSpan.FromMinutes(1)
            },
            CancellationToken.None);
}
