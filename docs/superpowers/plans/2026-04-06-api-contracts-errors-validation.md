# API Contracts, Errors, and Validation — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Standardize all API error responses as ProblemDetails, replace string status/type fields with typed enums (camelCase wire format), add request validation, and validate plugin config against catalog schema.

**Architecture:** A thin `ResultExtensions.cs` provides ProblemDetails plumbing. Each endpoint file handles its own validation and error mapping vertically. Response DTOs switch from `string` to typed enums with `JsonStringEnumConverter<T>`. No domain or grain changes.

**Tech Stack:** C# / .NET 10, ASP.NET Minimal APIs, `System.Text.Json` source generation, xunit.v3 + Shouldly

---

## File Map

| Action | File | Responsibility |
|--------|------|----------------|
| Create | `src/Runtime/Weave.Silo/Api/ResultExtensions.cs` | ProblemDetails helper methods |
| Modify | `src/Runtime/Weave.Silo/Api/Contracts.cs` | String → enum types in DTOs |
| Modify | `src/Runtime/Weave.Silo/Api/AgentEndpoints.cs` | Validation + error mapping |
| Modify | `src/Runtime/Weave.Silo/Api/WorkspaceEndpoints.cs` | Validation + error mapping |
| Modify | `src/Runtime/Weave.Silo/Api/ToolEndpoints.cs` | Error mapping |
| Modify | `src/Runtime/Weave.Silo/Api/PluginEndpoints.cs` | Config validation + error mapping + ConnectPluginResponse |
| Modify | `src/Runtime/Weave.Silo/Api/SiloApiJsonContext.cs` | Register new types for source gen |
| Modify | `src/Runtime/Weave.Silo/Program.cs` | Global exception handler |

---

### Task 1: ResultExtensions — ProblemDetails Plumbing

**Files:**
- Create: `src/Runtime/Weave.Silo/Api/ResultExtensions.cs`

- [ ] **Step 1: Create ResultExtensions**

Create `src/Runtime/Weave.Silo/Api/ResultExtensions.cs`:

```csharp
namespace Weave.Silo.Api;

internal static class ResultExtensions
{
    internal static IResult NotFound(string detail) =>
        Results.Problem(statusCode: 404, title: "Not Found", detail: detail);

    internal static IResult Conflict(string detail) =>
        Results.Problem(statusCode: 409, title: "Conflict", detail: detail);

    internal static IResult ValidationFailed(Dictionary<string, string[]> errors) =>
        Results.ValidationProblem(errors, title: "Validation Failed");

    internal static IResult ServerError() =>
        Results.Problem(statusCode: 500, title: "Internal Server Error", detail: "An unexpected error occurred.");
}
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build src/Runtime/Weave.Silo/Weave.Silo.csproj`
Expected: Build succeeded, 0 errors

- [ ] **Step 3: Commit**

```bash
git add src/Runtime/Weave.Silo/Api/ResultExtensions.cs
git commit -m "feat: add ResultExtensions for ProblemDetails responses"
```

---

### Task 2: Enum Types in Response Contracts

**Files:**
- Modify: `src/Runtime/Weave.Silo/Api/Contracts.cs`
- Modify: `src/Runtime/Weave.Silo/Api/SiloApiJsonContext.cs`

- [ ] **Step 1: Update response DTOs to use typed enums**

In `src/Runtime/Weave.Silo/Api/Contracts.cs`, make these changes:

Add usings at the top:
```csharp
using System.Text.Json.Serialization;
using Weave.Agents.Models;
using Weave.Tools.Models;
using Weave.Workspaces.Models;
```

In `WorkspaceResponse`, change `Status` from `string` to `WorkspaceStatus`:
```csharp
// Before
public required string Status { get; init; }
// After
[JsonConverter(typeof(JsonStringEnumConverter<WorkspaceStatus>))]
public required WorkspaceStatus Status { get; init; }
```

In `WorkspaceResponse.FromState`:
```csharp
// Before
Status = state.Status.ToString(),
// After
Status = state.Status,
```

In `AgentResponse`, change `Status` from `string` to `AgentStatus`:
```csharp
// Before
public required string Status { get; init; }
// After
[JsonConverter(typeof(JsonStringEnumConverter<AgentStatus>))]
public required AgentStatus Status { get; init; }
```

In `AgentResponse.FromState`, change the mapping:
```csharp
// Before
Status = state.Status.ToString(),
// After
Status = state.Status,
```

In `TaskResponse`, change `Status` from `string` to `AgentTaskStatus`:
```csharp
// Before
public required string Status { get; init; }
// After
[JsonConverter(typeof(JsonStringEnumConverter<AgentTaskStatus>))]
public required AgentTaskStatus Status { get; init; }
```

In `TaskResponse.FromInfo`:
```csharp
// Before
Status = info.Status.ToString(),
// After
Status = info.Status,
```

In `ToolConnectionResponse`, change `Status` from `string` to `ToolConnectionStatus` and `ToolType` from `string` to `ToolType`:
```csharp
// Before
public required string ToolType { get; init; }
public required string Status { get; init; }
// After
[JsonConverter(typeof(JsonStringEnumConverter<ToolType>))]
public required ToolType ToolType { get; init; }
[JsonConverter(typeof(JsonStringEnumConverter<ToolConnectionStatus>))]
public required ToolConnectionStatus Status { get; init; }
```

In `ToolConnectionResponse.FromConnection`:
```csharp
// Before
ToolType = conn.ToolType,
Status = conn.Status.ToString(),
// After
ToolType = Enum.Parse<ToolType>(conn.ToolType, ignoreCase: true),
Status = conn.Status,
```

Note: `conn.ToolType` is a `string` on the domain model `ToolConnection`, so we parse it at the API boundary.

In `ProofItemResponse`, change `Type` from `string` to `ProofType`:
```csharp
// Before
public required string Type { get; init; }
// After
[JsonConverter(typeof(JsonStringEnumConverter<ProofType>))]
public required ProofType Type { get; init; }
```

In `ProofItemResponse.FromItem`:
```csharp
// Before
Type = item.Type.ToString(),
// After
Type = item.Type,
```

In `ProofItemRequest`, change `Type` from `string` to `ProofType`:
```csharp
// Before
public required string Type { get; init; }
// After
[JsonConverter(typeof(JsonStringEnumConverter<ProofType>))]
public required ProofType Type { get; init; }
```

- [ ] **Step 2: Update SiloApiJsonContext**

In `src/Runtime/Weave.Silo/Api/SiloApiJsonContext.cs`, add the `using` for `System.Text.Json.Serialization` if not already present, and add entries for `ConnectPluginResponse` and `ProblemDetails`:

```csharp
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Weave.Workspaces.Plugins;
```

Add these `[JsonSerializable]` attributes:
```csharp
[JsonSerializable(typeof(ConnectPluginResponse))]
[JsonSerializable(typeof(ProblemDetails))]
[JsonSerializable(typeof(HttpValidationProblemDetails))]
```

- [ ] **Step 3: Update AgentEndpoints CompleteTask to use typed ProofType**

In `src/Runtime/Weave.Silo/Api/AgentEndpoints.cs`, the `CompleteTask` method currently parses `ProofItemRequest.Type` as a string with `Enum.Parse`. Since `ProofItemRequest.Type` is now `ProofType`, simplify:

```csharp
// Before
var proof = new ProofOfWork
{
    Items = request.Proof.Select(p => new ProofItem
    {
        Type = Enum.Parse<ProofType>(p.Type, ignoreCase: true),
        Label = p.Label,
        Value = p.Value,
        Uri = p.Uri
    }).ToList()
};

// After
var proof = new ProofOfWork
{
    Items = request.Proof.Select(p => new ProofItem
    {
        Type = p.Type,
        Label = p.Label,
        Value = p.Value,
        Uri = p.Uri
    }).ToList()
};
```

- [ ] **Step 4: Build to verify**

Run: `dotnet build Weave.slnx`
Expected: Build succeeded, 0 errors

- [ ] **Step 5: Commit**

```bash
git add src/Runtime/Weave.Silo/Api/Contracts.cs src/Runtime/Weave.Silo/Api/SiloApiJsonContext.cs src/Runtime/Weave.Silo/Api/AgentEndpoints.cs
git commit -m "feat: replace string status/type fields with typed enums in API contracts"
```

---

### Task 3: AgentEndpoints — Validation and Error Mapping

**Files:**
- Modify: `src/Runtime/Weave.Silo/Api/AgentEndpoints.cs`

- [ ] **Step 1: Add validation methods and error mapping**

In `src/Runtime/Weave.Silo/Api/AgentEndpoints.cs`, add these private static validation methods at the bottom of the class:

```csharp
private static Dictionary<string, string[]>? ValidateSubmitTask(SubmitTaskRequest request)
{
    Dictionary<string, string[]>? errors = null;

    if (string.IsNullOrWhiteSpace(request.Description))
        (errors ??= [])["description"] = ["Description is required."];
    else if (request.Description.Length > 1000)
        (errors ??= [])["description"] = ["Description must be 1000 characters or fewer."];

    return errors;
}

private static Dictionary<string, string[]>? ValidateSendMessage(SendMessageRequest request)
{
    Dictionary<string, string[]>? errors = null;

    if (string.IsNullOrWhiteSpace(request.Content))
        (errors ??= [])["content"] = ["Content is required."];
    else if (request.Content.Length > 50_000)
        (errors ??= [])["content"] = ["Content must be 50000 characters or fewer."];

    return errors;
}

private static Dictionary<string, string[]>? ValidateCompleteTask(CompleteTaskRequest request)
{
    Dictionary<string, string[]>? errors = null;

    if (request.Proof is not { Count: > 0 })
        (errors ??= [])["proof"] = ["At least one proof item is required."];
    else
    {
        for (var i = 0; i < request.Proof.Count; i++)
        {
            var item = request.Proof[i];
            if (string.IsNullOrWhiteSpace(item.Label))
                (errors ??= [])[$"proof[{i}].label"] = ["Label is required."];
            if (string.IsNullOrWhiteSpace(item.Value))
                (errors ??= [])[$"proof[{i}].value"] = ["Value is required."];
        }
    }

    return errors;
}

private static Dictionary<string, string[]>? ValidateActivateAgent(ActivateAgentRequest request)
{
    Dictionary<string, string[]>? errors = null;

    if (string.IsNullOrWhiteSpace(request.Definition.Model))
        (errors ??= [])["definition.model"] = ["Model is required."];

    return errors;
}
```

Now wrap each endpoint that takes a request body with validation + try/catch. Update these methods:

**SubmitTask:**
```csharp
private static async Task<IResult> SubmitTask(
    string workspaceId,
    string agentName,
    SubmitTaskRequest request,
    ICommandDispatcher dispatcher,
    CancellationToken ct)
{
    var errors = ValidateSubmitTask(request);
    if (errors is not null)
        return ResultExtensions.ValidationFailed(errors);

    try
    {
        var command = new SubmitAgentTaskCommand(WorkspaceId.From(workspaceId), agentName, request.Description);
        var info = await dispatcher.DispatchAsync<SubmitAgentTaskCommand, AgentTaskInfo>(command, ct);
        return Results.Created(
            $"/api/workspaces/{workspaceId}/agents/{agentName}/tasks/{info.TaskId}",
            TaskResponse.FromInfo(info));
    }
    catch (InvalidOperationException ex)
    {
        return ResultExtensions.Conflict(ex.Message);
    }
}
```

**SendMessage:**
```csharp
private static async Task<IResult> SendMessage(
    string workspaceId,
    string agentName,
    SendMessageRequest request,
    ICommandDispatcher dispatcher,
    CancellationToken ct)
{
    var errors = ValidateSendMessage(request);
    if (errors is not null)
        return ResultExtensions.ValidationFailed(errors);

    try
    {
        var command = new SendAgentMessageCommand(
            WorkspaceId.From(workspaceId),
            agentName,
            new AgentMessage
            {
                Role = request.Role,
                Content = request.Content
            });
        var response = await dispatcher.DispatchAsync<SendAgentMessageCommand, Weave.Agents.Models.AgentChatResponse>(command, ct);
        return Results.Ok(Api.AgentChatResponse.FromResponse(response));
    }
    catch (InvalidOperationException ex)
    {
        return ResultExtensions.Conflict(ex.Message);
    }
}
```

**CompleteTask:**
```csharp
private static async Task<IResult> CompleteTask(
    string workspaceId,
    string agentName,
    string taskId,
    CompleteTaskRequest request,
    ICommandDispatcher dispatcher,
    CancellationToken ct)
{
    var errors = ValidateCompleteTask(request);
    if (errors is not null)
        return ResultExtensions.ValidationFailed(errors);

    try
    {
        var proof = new ProofOfWork
        {
            Items = request.Proof.Select(p => new ProofItem
            {
                Type = p.Type,
                Label = p.Label,
                Value = p.Value,
                Uri = p.Uri
            }).ToList()
        };

        var command = new CompleteAgentTaskCommand(
            WorkspaceId.From(workspaceId), agentName, AgentTaskId.From(taskId), request.Success, proof);
        var info = await dispatcher.DispatchAsync<CompleteAgentTaskCommand, AgentTaskInfo>(command, ct);
        return Results.Ok(TaskResponse.FromInfo(info));
    }
    catch (InvalidOperationException ex)
    {
        return ResultExtensions.Conflict(ex.Message);
    }
}
```

**ActivateAgent:**
```csharp
private static async Task<IResult> ActivateAgent(
    string workspaceId,
    string agentName,
    ActivateAgentRequest request,
    ICommandDispatcher dispatcher,
    CancellationToken ct)
{
    var errors = ValidateActivateAgent(request);
    if (errors is not null)
        return ResultExtensions.ValidationFailed(errors);

    try
    {
        var command = new ActivateAgentCommand(WorkspaceId.From(workspaceId), agentName, request.Definition);
        var state = await dispatcher.DispatchAsync<ActivateAgentCommand, AgentState>(command, ct);
        return Results.Ok(AgentResponse.FromState(state));
    }
    catch (InvalidOperationException ex)
    {
        return ResultExtensions.Conflict(ex.Message);
    }
}
```

**ReviewTask:**
```csharp
private static async Task<IResult> ReviewTask(
    string workspaceId,
    string agentName,
    string taskId,
    ReviewTaskRequest request,
    ICommandDispatcher dispatcher,
    CancellationToken ct)
{
    try
    {
        var command = new ReviewAgentTaskCommand(
            WorkspaceId.From(workspaceId), agentName, AgentTaskId.From(taskId), request.Accepted, request.Feedback);
        var info = await dispatcher.DispatchAsync<ReviewAgentTaskCommand, AgentTaskInfo>(command, ct);
        return Results.Ok(TaskResponse.FromInfo(info));
    }
    catch (InvalidOperationException ex)
    {
        return ResultExtensions.Conflict(ex.Message);
    }
}
```

**DeactivateAgent:**
```csharp
private static async Task<IResult> DeactivateAgent(
    string workspaceId,
    string agentName,
    ICommandDispatcher dispatcher,
    CancellationToken ct)
{
    try
    {
        var command = new DeactivateAgentCommand(WorkspaceId.From(workspaceId), agentName);
        await dispatcher.DispatchAsync<DeactivateAgentCommand, bool>(command, ct);
        return Results.NoContent();
    }
    catch (InvalidOperationException ex)
    {
        return ResultExtensions.Conflict(ex.Message);
    }
}
```

`GetAllAgents` and `GetAgent` are read-only queries — no validation needed. Add try/catch for not-found:

```csharp
private static async Task<IResult> GetAgent(
    string workspaceId,
    string agentName,
    IQueryDispatcher dispatcher,
    CancellationToken ct)
{
    try
    {
        var query = new GetAgentStateQuery(WorkspaceId.From(workspaceId), agentName);
        var state = await dispatcher.DispatchAsync<GetAgentStateQuery, AgentState>(query, ct);
        return Results.Ok(AgentResponse.FromState(state));
    }
    catch (KeyNotFoundException ex)
    {
        return ResultExtensions.NotFound(ex.Message);
    }
}
```

`GetAllAgents` stays as-is (returns empty list, not 404).

- [ ] **Step 2: Build to verify**

Run: `dotnet build Weave.slnx`
Expected: Build succeeded, 0 errors

- [ ] **Step 3: Commit**

```bash
git add src/Runtime/Weave.Silo/Api/AgentEndpoints.cs
git commit -m "feat: add validation and ProblemDetails error mapping to AgentEndpoints"
```

---

### Task 4: WorkspaceEndpoints — Validation and Error Mapping

**Files:**
- Modify: `src/Runtime/Weave.Silo/Api/WorkspaceEndpoints.cs`

- [ ] **Step 1: Add validation and error mapping**

In `src/Runtime/Weave.Silo/Api/WorkspaceEndpoints.cs`, add a validation method and wrap endpoints:

```csharp
private static Dictionary<string, string[]>? ValidateStartWorkspace(StartWorkspaceRequest request)
{
    Dictionary<string, string[]>? errors = null;

    if (string.IsNullOrWhiteSpace(request.Manifest.Name))
        (errors ??= [])["manifest.name"] = ["Name is required."];
    if (string.IsNullOrWhiteSpace(request.Manifest.Version))
        (errors ??= [])["manifest.version"] = ["Version is required."];

    return errors;
}
```

Update `StartWorkspace`:
```csharp
private static async Task<IResult> StartWorkspace(
    StartWorkspaceRequest request,
    ICommandDispatcher dispatcher,
    CancellationToken ct)
{
    var errors = ValidateStartWorkspace(request);
    if (errors is not null)
        return ResultExtensions.ValidationFailed(errors);

    try
    {
        var workspaceId = WorkspaceId.New();
        var command = new StartWorkspaceCommand(workspaceId, request.Manifest);
        var state = await dispatcher.DispatchAsync<StartWorkspaceCommand, Workspaces.Models.WorkspaceState>(command, ct);
        return Results.Created($"/api/workspaces/{workspaceId}", WorkspaceResponse.FromState(state));
    }
    catch (InvalidOperationException ex)
    {
        return ResultExtensions.Conflict(ex.Message);
    }
}
```

Update `GetWorkspaceState` with not-found handling:
```csharp
private static async Task<IResult> GetWorkspaceState(
    string workspaceId,
    IQueryDispatcher dispatcher,
    CancellationToken ct)
{
    try
    {
        var query = new GetWorkspaceStateQuery(WorkspaceId.From(workspaceId));
        var state = await dispatcher.DispatchAsync<GetWorkspaceStateQuery, Workspaces.Models.WorkspaceState>(query, ct);
        return Results.Ok(WorkspaceResponse.FromState(state));
    }
    catch (KeyNotFoundException ex)
    {
        return ResultExtensions.NotFound(ex.Message);
    }
}
```

Update `StopWorkspace` with conflict handling:
```csharp
private static async Task<IResult> StopWorkspace(
    string workspaceId,
    ICommandDispatcher dispatcher,
    CancellationToken ct)
{
    try
    {
        var command = new StopWorkspaceCommand(WorkspaceId.From(workspaceId));
        await dispatcher.DispatchAsync<StopWorkspaceCommand, bool>(command, ct);
        return Results.NoContent();
    }
    catch (InvalidOperationException ex)
    {
        return ResultExtensions.Conflict(ex.Message);
    }
    catch (KeyNotFoundException ex)
    {
        return ResultExtensions.NotFound(ex.Message);
    }
}
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build Weave.slnx`
Expected: Build succeeded, 0 errors

- [ ] **Step 3: Commit**

```bash
git add src/Runtime/Weave.Silo/Api/WorkspaceEndpoints.cs
git commit -m "feat: add validation and ProblemDetails error mapping to WorkspaceEndpoints"
```

---

### Task 5: ToolEndpoints and PluginEndpoints — Error Mapping and Config Validation

**Files:**
- Modify: `src/Runtime/Weave.Silo/Api/ToolEndpoints.cs`
- Modify: `src/Runtime/Weave.Silo/Api/PluginEndpoints.cs`

- [ ] **Step 1: Add error mapping to ToolEndpoints**

In `src/Runtime/Weave.Silo/Api/ToolEndpoints.cs`, update `GetTool` to return ProblemDetails for not-found:

```csharp
private static async Task<IResult> GetTool(
    string workspaceId,
    string toolName,
    IGrainFactory grainFactory,
    CancellationToken ct)
{
    var grain = grainFactory.GetGrain<IToolRegistryGrain>(workspaceId);
    var connection = await grain.GetConnectionAsync(toolName);
    return connection is null
        ? ResultExtensions.NotFound($"Tool '{toolName}' not found in workspace '{workspaceId}'.")
        : Results.Ok(ToolConnectionResponse.FromConnection(connection));
}
```

- [ ] **Step 2: Add ConnectPluginResponse and validation to PluginEndpoints**

In `src/Runtime/Weave.Silo/Api/PluginEndpoints.cs`, add the `ConnectPluginResponse` record and validation:

Replace the file content with:

```csharp
using Weave.Workspaces.Models;
using Weave.Workspaces.Plugins;

namespace Weave.Silo.Api;

public static class PluginEndpoints
{
    public static RouteGroupBuilder MapPluginEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/plugins")
            .WithTags("Plugins");

        group.MapGet("/", GetAllPlugins);
        group.MapGet("/catalog", GetCatalog);
        group.MapPost("/", ConnectPlugin);
        group.MapDelete("/{name}", DisconnectPlugin);

        return group;
    }

    private static IResult GetAllPlugins(IPluginRegistry registry)
    {
        return Results.Ok(registry.GetAll());
    }

    private static IResult GetCatalog(IPluginRegistry registry)
    {
        return Results.Ok(registry.GetCatalog());
    }

    private static async Task<IResult> ConnectPlugin(IPluginRegistry registry, ConnectPluginRequest request)
    {
        var errors = ValidateConnectPlugin(request);
        if (errors is not null)
            return ResultExtensions.ValidationFailed(errors);

        var (configErrors, warnings) = ValidatePluginConfig(request, registry.GetCatalog());
        if (configErrors is not null)
            return ResultExtensions.ValidationFailed(configErrors);

        try
        {
            var definition = new PluginDefinition
            {
                Type = request.Type,
                Description = request.Description,
                Config = request.Config is not null ? new(request.Config) : []
            };

            var status = await registry.ConnectAsync(request.Name, definition);
            if (!status.IsConnected)
                return Results.Problem(statusCode: 422, title: "Plugin Connection Failed", detail: status.ErrorMessage);

            return Results.Ok(new ConnectPluginResponse { Status = status, Warnings = warnings });
        }
        catch (InvalidOperationException ex)
        {
            return ResultExtensions.Conflict(ex.Message);
        }
    }

    private static async Task<IResult> DisconnectPlugin(IPluginRegistry registry, string name)
    {
        try
        {
            var status = await registry.DisconnectAsync(name);
            return Results.Ok(status);
        }
        catch (KeyNotFoundException ex)
        {
            return ResultExtensions.NotFound(ex.Message);
        }
    }

    private static Dictionary<string, string[]>? ValidateConnectPlugin(ConnectPluginRequest request)
    {
        Dictionary<string, string[]>? errors = null;

        if (string.IsNullOrWhiteSpace(request.Name))
            (errors ??= [])["name"] = ["Name is required."];
        if (string.IsNullOrWhiteSpace(request.Type))
            (errors ??= [])["type"] = ["Type is required."];

        return errors;
    }

    private static (Dictionary<string, string[]>? Errors, List<string> Warnings) ValidatePluginConfig(
        ConnectPluginRequest request,
        IReadOnlyList<PluginSchema> catalog)
    {
        var warnings = new List<string>();
        var schema = catalog.FirstOrDefault(s => string.Equals(s.Type, request.Type, StringComparison.OrdinalIgnoreCase));
        if (schema is null)
            return (null, warnings);

        Dictionary<string, string[]>? errors = null;
        var config = request.Config ?? new Dictionary<string, string>();
        var knownKeys = new HashSet<string>(schema.Config.Select(f => f.Name), StringComparer.OrdinalIgnoreCase);

        foreach (var field in schema.Config)
        {
            if (field.Required && !config.ContainsKey(field.Name))
                (errors ??= [])[$"config.{field.Name}"] = [$"Required config field '{field.Name}' is missing."];
        }

        foreach (var key in config.Keys)
        {
            if (!knownKeys.Contains(key))
                warnings.Add($"Unknown config key '{key}' for plugin type '{request.Type}'.");
        }

        return (errors, warnings);
    }
}

public sealed record ConnectPluginRequest
{
    public required string Name { get; init; }
    public required string Type { get; init; }
    public string? Description { get; init; }
    public Dictionary<string, string>? Config { get; init; }
}

public sealed record ConnectPluginResponse
{
    public required PluginStatus Status { get; init; }
    public List<string> Warnings { get; init; } = [];
}
```

- [ ] **Step 3: Build to verify**

Run: `dotnet build Weave.slnx`
Expected: Build succeeded, 0 errors

- [ ] **Step 4: Commit**

```bash
git add src/Runtime/Weave.Silo/Api/ToolEndpoints.cs src/Runtime/Weave.Silo/Api/PluginEndpoints.cs
git commit -m "feat: add validation and error mapping to ToolEndpoints and PluginEndpoints"
```

---

### Task 6: Global Exception Handler

**Files:**
- Modify: `src/Runtime/Weave.Silo/Program.cs`

- [ ] **Step 1: Add global exception handler**

In `src/Runtime/Weave.Silo/Program.cs`, after `var app = builder.Build();` (or wherever the app pipeline is configured), add:

```csharp
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
```

Add the using if not present:
```csharp
using Microsoft.AspNetCore.Mvc;
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build Weave.slnx`
Expected: Build succeeded, 0 errors

- [ ] **Step 3: Run full test suite**

Run: `dotnet test --solution Weave.slnx`
Expected: All existing tests PASS. No behavioral changes to grain layer — this is all API boundary work.

- [ ] **Step 4: Commit**

```bash
git add src/Runtime/Weave.Silo/Program.cs
git commit -m "feat: add global ProblemDetails exception handler"
```
