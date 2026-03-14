using Weave.Agents.Pipeline;
using Weave.Security.Proxy;
using Weave.Security.Scanning;
using Weave.Security.Tokens;
using Weave.Security.Vault;
using Weave.ServiceDefaults;
using Weave.Shared.Cqrs;
using Weave.Shared.Events;
using Weave.Shared.Lifecycle;
using Weave.Silo.Api;
using Weave.Silo.Events;
using Weave.Tools.Connectors;
using Weave.Tools.Discovery;
using Weave.Workspaces.Runtime;

var builder = WebApplication.CreateBuilder(args);

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

// --- JSON-configured plugins (environment / config driven) ---
// Dapr: activated when DAPR_HTTP_PORT is set (sidecar present)
var daprPort = builder.Configuration["DAPR_HTTP_PORT"]
    ?? Environment.GetEnvironmentVariable("DAPR_HTTP_PORT");

if (daprPort is not null)
{
    var daprBaseUrl = $"http://localhost:{daprPort}";
    builder.Services.AddHttpClient<DaprEventBus>(c => c.BaseAddress = new Uri(daprBaseUrl));
    builder.Services.AddSingleton<IEventBus, DaprEventBus>();
    builder.Services.AddHttpClient<DaprToolConnector>(c => c.BaseAddress = new Uri(daprBaseUrl));
    builder.Services.AddSingleton<IToolConnector>(sp => sp.GetRequiredService<DaprToolConnector>());
}

// Vault: activated when Vault:Address is configured
var vaultAddress = builder.Configuration["Vault:Address"];
if (vaultAddress is not null)
{
    builder.Services.AddHttpClient<VaultSecretProvider>(c =>
    {
        c.BaseAddress = new Uri(vaultAddress);
        var token = builder.Configuration["Vault:Token"];
        if (token is not null)
            c.DefaultRequestHeaders.Add("X-Vault-Token", token);
    });
    builder.Services.AddSingleton<ISecretProvider, VaultSecretProvider>();
}

var app = builder.Build();

if (daprPort is not null)
    app.Logger.LogInformation("Dapr plugin active — sidecar at localhost:{DaprPort}", daprPort);
if (vaultAddress is not null)
    app.Logger.LogInformation("Vault plugin active — server at {VaultAddress}", vaultAddress);

if (isLocalMode)
{
    app.Logger.LogInformation("Weave running in local mode — no external services required");
}

app.MapDefaultEndpoints();

// Domain API
app.MapWorkspaceEndpoints();
app.MapAgentEndpoints();
app.MapToolEndpoints();

app.Run();
