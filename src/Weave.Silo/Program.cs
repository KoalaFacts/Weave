using System.Diagnostics.CodeAnalysis;
using Weave.ServiceDefaults;
using Weave.Shared.Cqrs;
using Weave.Shared.Events;
using Weave.Shared.Lifecycle;
using Weave.Workspaces.Runtime;

[assembly: UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Orleans and CQRS require reflection.")]

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.UseOrleans();

// Shared kernel services
builder.Services.AddSingleton<IEventBus, InProcessEventBus>();
builder.Services.AddSingleton<ILifecycleManager, LifecycleManager>();
builder.Services.AddSingleton<IWorkspaceRuntime, PodmanRuntime>();
builder.Services.AddCqrs(
    typeof(Weave.Workspaces.Commands.StartWorkspaceCommand).Assembly,
    typeof(Weave.Agents.Commands.ActivateAgentCommand).Assembly);

var app = builder.Build();

app.MapDefaultEndpoints();

app.Run();
