using Microsoft.AspNetCore.Mvc;
using Scalar.AspNetCore;
using Weave.Agents.Pipeline;
using Weave.Security.Plugins;
using Weave.Security.Proxy;
using Weave.Security.Scanning;
using Weave.Security.Tokens;
using Weave.Security.Vault;
using Weave.ServiceDefaults;
using Weave.Shared.Cqrs;
using Weave.Shared.Events;
using Weave.Shared.Lifecycle;
using Weave.Shared.Plugins;
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

builder.Services.AddSingleton<ICommandRunner, ProcessCommandRunner>();

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

// --- Plugin hot-swap broker and proxy layer ---
// The broker holds mutable service slots; proxies delegate to the broker's current
// backing instance or fall back to defaults. This lets plugins swap implementations
// at runtime without rebuilding the DI container.
builder.Services.AddSingleton<PluginServiceBroker>();

// Default event bus (fallback when no plugin overrides it)
builder.Services.AddSingleton<InProcessEventBus>();
builder.Services.AddSingleton<IEventBus, EventBusProxy>();

// Default secret provider (fallback when no plugin overrides it)
builder.Services.AddSingleton<InMemorySecretProvider>();
builder.Services.AddSingleton<ISecretProvider>(sp =>
    new SecretProviderProxy(
        sp.GetRequiredService<PluginServiceBroker>(),
        sp.GetRequiredService<InMemorySecretProvider>()));

// Agent chat pipeline
builder.Services.AddSingleton<IAgentCostLedger, AgentCostLedger>();
builder.Services.AddSingleton<IAgentChatClientFactory, AgentChatClientFactory>();
builder.Services.AddTransient<IAgentChatPipeline, AgentChatPipeline>();

// Tool connectors and discovery
builder.Services.AddSingleton<IToolConnector, McpToolConnector>();
builder.Services.AddSingleton<IToolConnector, CliToolConnector>();
builder.Services.AddSingleton<IToolDiscoveryService, ToolDiscoveryService>();
builder.Services.AddHttpClient<OpenApiToolConnector>();
builder.Services.AddSingleton<IToolConnector>(sp => sp.GetRequiredService<OpenApiToolConnector>());
builder.Services.AddHttpClient<DirectHttpToolConnector>();
builder.Services.AddSingleton<IToolConnector>(sp => sp.GetRequiredService<DirectHttpToolConnector>());

// --- Plugin connectors (use broker + factories, not IServiceCollection) ---
builder.Services.AddSingleton<IPluginConnector>(sp =>
    new DaprPluginConnector(
        sp.GetRequiredService<PluginServiceBroker>(),
        sp.GetRequiredService<IToolDiscoveryService>(),
        sp.GetRequiredService<IHttpClientFactory>(),
        sp.GetRequiredService<ILoggerFactory>()));
builder.Services.AddSingleton<IPluginConnector>(sp =>
    new VaultPluginConnector(
        sp.GetRequiredService<PluginServiceBroker>(),
        sp.GetRequiredService<IHttpClientFactory>(),
        sp.GetRequiredService<ICapabilityTokenService>(),
        sp.GetRequiredService<ILoggerFactory>()));
builder.Services.AddSingleton<IPluginConnector>(sp =>
    new HttpPluginConnector(
        sp.GetRequiredService<PluginServiceBroker>(),
        sp.GetRequiredService<IHttpClientFactory>(),
        sp.GetRequiredService<ILoggerFactory>()));
builder.Services.AddSingleton<IPluginConnector>(sp =>
    new WebhookPluginConnector(
        sp.GetRequiredService<PluginServiceBroker>(),
        sp.GetRequiredService<IHttpClientFactory>(),
        sp.GetRequiredService<ILoggerFactory>()));
builder.Services.AddSingleton<IPluginRegistry, PluginRegistry>();
builder.Services.AddOpenApi();

var app = builder.Build();

app.UseExceptionHandler(error => error.Run(async context =>
{
    context.Response.StatusCode = 500;
    context.Response.ContentType = "application/problem+json";
    var problem = new ProblemDetails
    {
        Status = 500,
        Title = "Internal Server Error",
        Detail = "An unexpected error occurred."
    };
    await context.Response.WriteAsJsonAsync(problem, SiloApiJsonContext.Default.ProblemDetails);
}));

// --- Activate plugins from workspace manifest or environment ---
var pluginRegistry = app.Services.GetRequiredService<IPluginRegistry>();

// Environment-detected plugins (backward compat with DAPR_HTTP_PORT / Vault:Address)
var daprPort = builder.Configuration["DAPR_HTTP_PORT"]
    ?? Environment.GetEnvironmentVariable("DAPR_HTTP_PORT");
if (daprPort is not null)
{
    await pluginRegistry.ConnectAsync("dapr", new PluginDefinition
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
    if (vaultToken is not null)
        vaultConfig["token"] = vaultToken;

    await pluginRegistry.ConnectAsync("vault", new PluginDefinition
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

// OpenAPI + Scalar
app.MapOpenApi();
app.MapScalarApiReference();

// Domain API
app.MapWorkspaceEndpoints();
app.MapAgentEndpoints();
app.MapToolEndpoints();
app.MapPluginEndpoints();

app.Run();
