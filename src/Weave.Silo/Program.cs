using System.Diagnostics.CodeAnalysis;
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
using Weave.Agents.Pipeline;
using Weave.Tools.Connectors;
using Weave.Tools.Discovery;
using Weave.Workspaces.Runtime;

[assembly: UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Orleans and CQRS require reflection.")]

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.UseOrleans();

// Shared kernel services
builder.Services.AddSingleton<ILifecycleManager, LifecycleManager>();
builder.Services.AddSingleton<IWorkspaceRuntime, PodmanRuntime>();
builder.Services.AddCqrs(
    typeof(Weave.Workspaces.Commands.StartWorkspaceCommand).Assembly,
    typeof(Weave.Agents.Commands.ActivateAgentCommand).Assembly,
    typeof(Weave.Silo.Api.StartWorkspaceHandler).Assembly);

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

// Dapr integration — use Dapr pub/sub event bus when available, fall back to in-process
if (builder.Configuration["DAPR_HTTP_PORT"] is not null)
{
    builder.Services.AddDaprClient();
    builder.Services.AddSingleton<IEventBus, DaprEventBus>();
    builder.Services.AddSingleton<IToolConnector>(sp =>
        new DaprToolConnector(
            sp.GetRequiredService<Dapr.Client.DaprClient>(),
            sp.GetRequiredService<ILogger<DaprToolConnector>>()));
}
else
{
    builder.Services.AddSingleton<IEventBus, InProcessEventBus>();
}

var app = builder.Build();

app.MapDefaultEndpoints();

// Domain API
app.MapWorkspaceEndpoints();
app.MapAgentEndpoints();
app.MapToolEndpoints();

app.Run();
