using Weave.Agents.Pipeline;
using Weave.Security.Proxy;
using Weave.Security.Scanning;
using Weave.Security.Tokens;
using Weave.Security.Vault;
using Weave.Shared.Cqrs;
using Weave.Shared.Events;
using Weave.Shared.Lifecycle;
using Weave.Shared.Secrets;
using Weave.ServiceDefaults;
using Weave.Silo.Api;
using Weave.Silo.Plugins;
using Weave.Tools.Connectors;
using Weave.Tools.Discovery;
using Weave.Workspaces.Models;
using Weave.Workspaces.Plugins;
using Weave.Workspaces.Runtime;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, SiloApiJsonContext.Default);
});

var isLocalMode = builder.Configuration.GetValue<bool>("Weave:LocalMode")
    || builder.Configuration["Orleans:ClusterId"] is null;

builder.AddServiceDefaults();

if (isLocalMode)
{
    builder.Services.AddOrleans(siloBuilder =>
    {
        siloBuilder.UseLocalhostClustering();
        siloBuilder.AddMemoryGrainStorageAsDefault();
    });
}
else
{
    builder.UseOrleans();
}

// Shared kernel services
builder.Services.AddSingleton<ILifecycleManager, LifecycleManager>();

if (isLocalMode)
    builder.Services.AddSingleton<IWorkspaceRuntime, InProcessRuntime>();
else
    builder.Services.AddSingleton<IWorkspaceRuntime, PodmanRuntime>();

// Source-generated CQRS handler registration — no reflection
builder.Services.AddGeneratedCqrsHandlers();

// Security services
builder.Services.Configure<CapabilityTokenOptions>(
    builder.Configuration.GetSection(CapabilityTokenOptions.ConfigurationSectionName));
builder.Services.AddSingleton<ICapabilityTokenService, CapabilityTokenService>();
builder.Services.AddSingleton<ILeakScanner, LeakScanner>();
builder.Services.AddSingleton<TransparentSecretProxy>();
builder.Services.AddSingleton<ISecretProvider, InMemorySecretProvider>();

// Agent chat pipeline
builder.Services.AddSingleton<IAgentCostLedger, AgentCostLedger>();
builder.Services.AddSingleton<IAgentChatClientFactory, AgentChatClientFactory>();

// Tool connectors and discovery
builder.Services.AddSingleton<IToolConnector, McpToolConnector>();
builder.Services.AddSingleton<IToolConnector, CliToolConnector>();
builder.Services.AddSingleton<IToolDiscoveryService, ToolDiscoveryService>();
builder.Services.AddHttpClient<OpenApiToolConnector>();
builder.Services.AddSingleton<IToolConnector>(sp => sp.GetRequiredService<OpenApiToolConnector>());

// Default event bus — in-process, no external dependencies
builder.Services.AddSingleton<IEventBus, InProcessEventBus>();

// --- Plugin connectors (registered so PluginRegistry can find them) ---
builder.Services.AddSingleton<IPluginConnector>(sp =>
    new DaprPluginConnector(builder.Services, sp.GetRequiredService<ILogger<DaprPluginConnector>>()));
builder.Services.AddSingleton<IPluginConnector>(sp =>
    new VaultPluginConnector(builder.Services, sp.GetRequiredService<ILogger<VaultPluginConnector>>()));
builder.Services.AddSingleton<IPluginConnector>(sp =>
    new HttpPluginConnector(builder.Services, sp.GetRequiredService<ILogger<HttpPluginConnector>>()));
builder.Services.AddSingleton<IPluginRegistry, PluginRegistry>();

var app = builder.Build();

// --- Activate plugins from workspace manifest or environment ---
var pluginRegistry = app.Services.GetRequiredService<IPluginRegistry>();
var pluginsFromConfig = builder.Configuration.GetSection("Weave:Plugins");

// Environment-detected plugins (backward compat with DAPR_HTTP_PORT / Vault:Address)
var daprPort = builder.Configuration["DAPR_HTTP_PORT"]
    ?? Environment.GetEnvironmentVariable("DAPR_HTTP_PORT");
if (daprPort is not null)
{
    pluginRegistry.Connect("dapr", new PluginDefinition
    {
        Type = "dapr",
        Description = "Auto-detected Dapr sidecar",
        Config = new Dictionary<string, string> { ["port"] = daprPort }
    });
}

var vaultAddress = builder.Configuration["Vault:Address"];
if (vaultAddress is not null)
{
    var vaultConfig = new Dictionary<string, string> { ["address"] = vaultAddress };
    var vaultToken = builder.Configuration["Vault:Token"];
    if (vaultToken is not null) vaultConfig["token"] = vaultToken;

    pluginRegistry.Connect("vault", new PluginDefinition
    {
        Type = "vault",
        Description = "Auto-detected Vault server",
        Config = vaultConfig
    });
}

// Log active plugins
foreach (var status in pluginRegistry.GetAll())
{
    if (status.IsConnected)
        app.Logger.LogInformation("Plugin '{Name}' ({Type}) active", status.Name, status.Type);
    else
        app.Logger.LogWarning("Plugin '{Name}' ({Type}) failed: {Error}", status.Name, status.Type, status.Error);
}

if (isLocalMode)
{
    app.Logger.LogInformation("Weave running in local mode — no external services required");
}

app.MapDefaultEndpoints();

// Domain API
app.MapWorkspaceEndpoints();
app.MapAgentEndpoints();
app.MapToolEndpoints();
app.MapPluginEndpoints();

app.Run();
