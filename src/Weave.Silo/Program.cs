using System.Diagnostics.CodeAnalysis;
using Weave.ServiceDefaults;
using Weave.Shared.Cqrs;
using Weave.Shared.Events;
using Weave.Shared.Lifecycle;
using Weave.Silo.Api;
using Weave.Silo.Events;
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
    typeof(Weave.Agents.Commands.ActivateAgentCommand).Assembly);

// Dapr integration — use Dapr pub/sub event bus when available, fall back to in-process
if (builder.Configuration["DAPR_HTTP_PORT"] is not null)
{
    builder.Services.AddDaprClient();
    builder.Services.AddSingleton<IEventBus, DaprEventBus>();
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
