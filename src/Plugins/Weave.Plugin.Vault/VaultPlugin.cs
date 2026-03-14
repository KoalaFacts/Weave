using Microsoft.Extensions.DependencyInjection;
using Weave.Security.Vault;
using Weave.Shared.Plugins;

[assembly: WeavePlugin(typeof(Weave.Plugin.Vault.VaultPlugin))]

namespace Weave.Plugin.Vault;

/// <summary>
/// Provides HashiCorp Vault secret provider.
/// Activates only when Vault:Address is configured.
/// </summary>
public sealed class VaultPlugin : IWeavePlugin
{
    public PluginMetadata Metadata { get; } = new("Vault", "1.0.0", "HashiCorp Vault secret provider for production secret management");

    public bool IsEnabled(PluginContext context) =>
        context.GetValue("Vault:Address") is not null;

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<ISecretProvider>(sp =>
        {
            var config = sp.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>();
            var address = config["Vault:Address"] ?? "http://localhost:8200";
            var token = config["Vault:Token"] ?? "";
            var client = VaultSecretProvider.CreateClient(address, token);
            return new VaultSecretProvider(
                client,
                sp.GetRequiredService<Weave.Security.Tokens.ICapabilityTokenService>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<VaultSecretProvider>>());
        });
    }
}
