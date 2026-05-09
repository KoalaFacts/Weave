using System.Diagnostics.CodeAnalysis;
using Weave.Agents.Channels;
using Weave.Agents.Pipeline;
using Weave.Agents.Pipeline.Providers;
using Weave.Agents.Verification;
using Weave.Security.Audit;
using Weave.Security.Plugins;
using Weave.Security.Postgres;
using Weave.Security.Proxy;
using Weave.Security.Scanning;
using Weave.Security.Sqlite;
using Weave.Security.Tokens;
using Weave.Security.Vault;
using Weave.Shared.Cqrs;
using Weave.Shared.Events;
using Weave.Shared.Lifecycle;
using Weave.Shared.Plugins;
using Weave.Silo.Audit;
using Weave.Silo.Channels;
using Weave.Silo.Configuration;
using Weave.Silo.Plugins;
using Weave.Silo.Security;
using Weave.Silo.Templates;
using Weave.Silo.VirtualActors;
using Weave.Tools.Connectors;
using Weave.Tools.Discovery;
using Weave.Workspaces.Manifest;
using Weave.Workspaces.Runtime;

namespace Weave.Silo.Startup;

internal sealed class SiloServiceRegistrar
{
    private readonly IServiceCollection _services;
    private readonly IConfiguration _configuration;
    private readonly WeaveSettings _weaveSettings;

    public SiloServiceRegistrar(
        IServiceCollection services,
        IConfiguration configuration,
        WeaveSettings weaveSettings)
    {
        _services = services;
        _configuration = configuration;
        _weaveSettings = weaveSettings;
    }

    public void Register()
    {
        RegisterKernel();
        RegisterRuntime();
        RegisterSecurity();
        RegisterPluginBroker();
        RegisterAgentPipeline();
        RegisterChannelAdapters();
        RegisterToolConnectors();
        RegisterPluginConnectors();
        RegisterApiOptions();

        _services.AddOpenApi();
    }

    private void RegisterKernel()
    {
        _services.AddSingleton(TimeProvider.System);
        _services.AddSingleton<ILifecycleManager, LifecycleManager>();
        _services.AddSingleton(_weaveSettings);
        _services.AddSingleton<Weave.Shared.VirtualActors.IVirtualActorProvider, OrleansVirtualActorProvider>();
        _services.AddSingleton<ICommandRunner, ProcessCommandRunner>();
        _services.AddSingleton<IManifestParser, ManifestParser>();
        _services.AddGeneratedCqrsHandlers();
    }

    private void RegisterRuntime()
    {
        if (_weaveSettings.IsLocalMode)
        {
            _services.AddSingleton<IWorkspaceRuntime, InProcessRuntime>();
            return;
        }

        _services.AddSingleton<IWorkspaceRuntime>(sp =>
            new ContainerRuntime(
                sp.GetRequiredService<ICommandRunner>(),
                new ContainerRuntimeOptions { Engine = _weaveSettings.ContainerRuntime.Engine },
                sp.GetRequiredService<ILogger<ContainerRuntime>>()));
    }

    private void RegisterSecurity()
    {
        _services.Configure<CapabilityTokenOptions>(
            _configuration.GetSection(CapabilityTokenOptions.ConfigurationSectionName));
        _services.Configure<CapabilityAuditOptions>(
            _configuration.GetSection(CapabilityAuditOptions.ConfigurationSectionName));
        _services.AddSingleton<ICapabilityTokenService, CapabilityTokenService>();
        _services.AddSingleton<ICapabilityAuthorizer, CapabilityAuthorizer>();
        RegisterCapabilityAuditStore();
        _services.AddSingleton<ILeakScanner, LeakScanner>();
        _services.AddSingleton<TransparentSecretProxy>();
    }

    private void RegisterCapabilityAuditStore()
    {
        var backend = _configuration[$"{CapabilityAuditOptions.ConfigurationSectionName}:{nameof(CapabilityAuditOptions.Backend)}"]
            ?? CapabilityAuditOptions.MemoryBackend;
        switch (backend.ToLowerInvariant())
        {
            case CapabilityAuditOptions.SqliteBackend:
                _services.AddSingleton<ICapabilityAuditStore, SqliteCapabilityAuditStore>();
                break;
            case CapabilityAuditOptions.PostgreSqlBackend:
                _services.AddSingleton<ICapabilityAuditStore, PostgresCapabilityAuditStore>();
                break;
            default:
                _services.AddSingleton<ICapabilityAuditStore, InMemoryCapabilityAuditStore>();
                break;
        }
    }

    private void RegisterPluginBroker()
    {
        _services.AddSingleton<PluginServiceBroker>();
        _services.AddSingleton<InProcessEventBus>();
        _services.AddSingleton<IEventBus, EventBusProxy>();
        _services.AddSingleton<InMemorySecretProvider>();
        _services.AddSingleton<ISecretProvider>(sp =>
            new SecretProviderProxy(
                sp.GetRequiredService<PluginServiceBroker>(),
                sp.GetRequiredService<InMemorySecretProvider>()));
    }

    private void RegisterAgentPipeline()
    {
        _services.AddSingleton<IAgentCostLedger, AgentCostLedger>();
        _services.AddSingleton<IAgentCredentialStore, EnvironmentAgentCredentialStore>();
        _services.AddScoped<IProviderResolver, ProviderResolver>();
        _services.AddScoped<IAgentChatClientFactory, AgentChatClientFactory>();
        _services.AddTransient<IAgentChatPipeline, AgentChatPipeline>();
        _services.AddSingleton<AgentVerificationDispatcher>();
        _services.AddSingleton<IAgentVerificationDispatcher>(sp =>
            sp.GetRequiredService<AgentVerificationDispatcher>());
        _services.AddHostedService<AgentVerificationHostedService>();
        _services.AddHostedService<CapabilityAuditSubscriberHostedService>();
        _services.AddHostedService<BuiltInTemplateSeeder>();
    }

    private void RegisterChannelAdapters()
    {
        AddChannelAdapter<SlackChannelAdapter>();
        AddChannelAdapter<DiscordChannelAdapter>();
        AddChannelAdapter<TelegramChannelAdapter>();
        AddChannelAdapter<TeamsChannelAdapter>();
        AddChannelAdapter<EmailChannelAdapter>();
    }

    private void AddChannelAdapter<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TAdapter>()
        where TAdapter : class, IChannelAdapter
    {
        _services.AddHttpClient<TAdapter>();
        _services.AddSingleton<IChannelAdapter>(sp => sp.GetRequiredService<TAdapter>());
    }

    private void RegisterToolConnectors()
    {
        _services.AddSingleton<IToolConnector, McpToolConnector>();
        _services.AddSingleton<IToolConnector, CliToolConnector>();
        _services.AddSingleton<IToolConnector, FileSystemToolConnector>();
        _services.AddSingleton<IToolDiscoveryService, ToolDiscoveryService>();
        _services.AddHttpClient<OpenApiToolConnector>();
        _services.AddSingleton<IToolConnector>(sp => sp.GetRequiredService<OpenApiToolConnector>());
        _services.AddHttpClient<DirectHttpToolConnector>();
        _services.AddSingleton<IToolConnector>(sp => sp.GetRequiredService<DirectHttpToolConnector>());
    }

    private void RegisterPluginConnectors()
    {
        _services.AddSingleton<IPluginConnector>(sp =>
            new DaprPluginConnector(
                sp.GetRequiredService<PluginServiceBroker>(),
                sp.GetRequiredService<IToolDiscoveryService>(),
                sp.GetRequiredService<IHttpClientFactory>(),
                sp.GetRequiredService<ILoggerFactory>()));
        _services.AddSingleton<IPluginConnector>(sp =>
            new VaultPluginConnector(
                sp.GetRequiredService<PluginServiceBroker>(),
                sp.GetRequiredService<IHttpClientFactory>(),
                sp.GetRequiredService<ICapabilityAuthorizer>(),
                sp.GetRequiredService<ILoggerFactory>()));
        _services.AddSingleton<IPluginConnector>(sp =>
            new HttpPluginConnector(
                sp.GetRequiredService<PluginServiceBroker>(),
                sp.GetRequiredService<IHttpClientFactory>(),
                sp.GetRequiredService<ILoggerFactory>()));
        _services.AddSingleton<IPluginConnector>(sp =>
            new AuthPluginConnector(
                sp.GetRequiredService<PluginServiceBroker>(),
                sp.GetRequiredService<ILoggerFactory>()));
        _services.AddSingleton<IPluginConnector>(sp =>
            new WebhookPluginConnector(
                sp.GetRequiredService<PluginServiceBroker>(),
                sp.GetRequiredService<IHttpClientFactory>(),
                sp.GetRequiredService<ILoggerFactory>()));
        _services.AddSingleton<IPluginRegistry, PluginRegistry>();
    }

    private void RegisterApiOptions()
    {
        _services.AddSingleton(ApiAuthOptions.FromConfiguration(_configuration));
        _services.AddSingleton(AuditOptions.FromConfiguration(_configuration));
    }
}
