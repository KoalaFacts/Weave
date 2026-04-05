# API Contracts, Errors, and Validation — DX Improvement (Phase 1)

**Date:** 2026-04-06
**Status:** Approved
**Scope:** Standardize error responses (ProblemDetails), replace string status/type fields with enums, add request validation, and validate plugin config against catalog schema.

## Problem

The HTTP API has poor developer experience in four areas:

1. **No standardized error response** — endpoints return bare `Results.NotFound()` or `Results.UnprocessableEntity()` with no detail. Clients can't programmatically distinguish error types or extract messages.
2. **Status fields are strings** — `AgentResponse.Status`, `TaskResponse.Status`, `ToolConnectionResponse.Status`, `ProofItemResponse.Type` are all `string` typed via `.ToString()`. No IDE autocomplete, no schema validation, no contract guarantee.
3. **No request validation** — `SubmitTaskRequest.Description` accepts any string including empty. `ProofItemRequest.Type` is a string that gets `Enum.Parse` at the endpoint with no error handling. Malformed requests hit the grain layer and throw opaque exceptions.
4. **Plugin config is unvalidated** — `ConnectPluginRequest.Config` is `Dictionary<string, string>` with no schema enforcement, even though `PluginRegistry.GetCatalog()` returns `PluginSchema` with required fields.

## Design

### 1. ProblemDetails Error Responses

All error responses use RFC 9457 `ProblemDetails`. ASP.NET provides `Results.Problem()` and `Results.ValidationProblem()` built-in.

**Status code mapping:**

| Code | Meaning | Source |
|------|---------|--------|
| 400 | Request validation failure | `RequestValidator` checks before dispatch |
| 404 | Resource not found | `KeyNotFoundException` from grain or null result |
| 409 | State conflict | `InvalidOperationException` from grain (agent not active, task not awaiting review, max tasks reached) |
| 422 | Semantic failure | Plugin connection failed |
| 500 | Unhandled exception | Global exception handler (no stack trace) |

**Wire format examples:**

404:
```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.5",
  "title": "Not Found",
  "status": 404,
  "detail": "Agent 'researcher' not found in workspace 'ws-1'."
}
```

400 (validation):
```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.1",
  "title": "Validation Failed",
  "status": 400,
  "errors": {
    "description": ["Description is required."],
    "proof[0].label": ["Label is required."]
  }
}
```

409 (conflict):
```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.10",
  "title": "Conflict",
  "status": 409,
  "detail": "Agent 'researcher' is not active (status: idle)."
}
```

**Cross-cutting plumbing** — a thin `ResultExtensions.cs` (~25 lines) with helper methods:

```csharp
// Api/ResultExtensions.cs
internal static class ResultExtensions
{
    internal static IResult NotFound(string detail) =>
        Results.Problem(statusCode: 404, title: "Not Found", detail: detail);

    internal static IResult Conflict(string detail) =>
        Results.Problem(statusCode: 409, title: "Conflict", detail: detail);

    internal static IResult ValidationFailed(Dictionary<string, string[]> errors) =>
        Results.ValidationProblem(errors, title: "Validation Failed");

    internal static IResult ServerError(string detail) =>
        Results.Problem(statusCode: 500, title: "Internal Server Error", detail: detail);
}
```

**Vertical error handling** — each endpoint file wraps grain calls with exception mapping specific to its domain:

```csharp
// In AgentEndpoints.cs
try
{
    var state = await dispatcher.DispatchAsync<...>(command, ct);
    return Results.Ok(AgentResponse.FromState(state));
}
catch (InvalidOperationException ex)
{
    return ResultExtensions.Conflict(ex.Message);
}
catch (KeyNotFoundException ex)
{
    return ResultExtensions.NotFound(ex.Message);
}
```

No centralized error code registry. Each endpoint decides what exceptions mean in its context.

### 2. Enum Types in Responses

Replace `string` status/type fields with typed enums. Serialization uses `JsonStringEnumConverter<T>` with `JsonNamingPolicy.CamelCase` — the wire format changes from PascalCase to camelCase (breaking change, acceptable since API is in development).

**Response DTO changes:**

| DTO | Property | Before | After |
|-----|----------|--------|-------|
| `AgentResponse` | `Status` | `string` | `AgentStatus` |
| `TaskResponse` | `Status` | `string` | `AgentTaskStatus` |
| `ToolConnectionResponse` | `Status` | `string` | `ToolConnectionStatus` |
| `ToolConnectionResponse` | `ToolType` | `string` | `ToolType` |
| `ProofItemResponse` | `Type` | `string` | `ProofType` |

**Request DTO changes:**

| DTO | Property | Before | After |
|-----|----------|--------|-------|
| `ProofItemRequest` | `Type` | `string` | `ProofType` |

**Factory method changes:**

```csharp
// Before
Status = state.Status.ToString(),
// After
Status = state.Status,
```

**Serialization registration:**

Add `[JsonConverter(typeof(JsonStringEnumConverter<EnumType>))]` attribute on each enum property in the response DTOs. This is explicit, AOT-friendly, and works with the source-generated `SiloApiJsonContext`. The `JsonKnownNamingPolicy.CamelCase` from `SiloApiJsonContext` applies to enum member names automatically.

**Wire format change:**

```json
// Before
{ "status": "AwaitingReview", "type": "CiStatus" }
// After
{ "status": "awaitingReview", "type": "ciStatus" }
```

**SiloApiJsonContext** — add `[JsonSerializable]` entries for any new enum types that the source generator needs to see.

### 3. Request Validation

Validation is **vertical** — each endpoint file has private static validation methods for its own requests. No centralized validator class.

**Validation rules by endpoint group:**

**AgentEndpoints:**

| Request | Field | Rule |
|---------|-------|------|
| `SubmitTaskRequest` | `Description` | Not empty, max 1000 chars |
| `SendMessageRequest` | `Content` | Not empty, max 50000 chars |
| `CompleteTaskRequest` | `Proof` | Not empty (at least 1 item) |
| `ProofItemRequest` | `Label` | Not empty |
| `ProofItemRequest` | `Value` | Not empty |
| `ProofItemRequest` | `Type` | Enum deserialization handles this — invalid value → ASP.NET 400 |
| `ActivateAgentRequest` | `Definition.Model` | Not empty |

**WorkspaceEndpoints:**

| Request | Field | Rule |
|---------|-------|------|
| `StartWorkspaceRequest` | `Manifest.Name` | Not empty |
| `StartWorkspaceRequest` | `Manifest.Version` | Not empty |

**PluginEndpoints:**

| Request | Field | Rule |
|---------|-------|------|
| `ConnectPluginRequest` | `Name` | Not empty |
| `ConnectPluginRequest` | `Type` | Not empty |
| `ConnectPluginRequest` | `Config` | Validated against catalog schema (see Section 4) |

**Validation pattern:**

Each endpoint file has a private static method per request type:

```csharp
// In AgentEndpoints.cs
private static Dictionary<string, string[]>? ValidateSubmitTask(SubmitTaskRequest request)
{
    Dictionary<string, string[]>? errors = null;

    if (string.IsNullOrWhiteSpace(request.Description))
        (errors ??= [])["description"] = ["Description is required."];
    else if (request.Description.Length > 1000)
        (errors ??= [])["description"] = ["Description must be 1000 characters or fewer."];

    return errors;
}
```

Endpoint calls validator before dispatch:

```csharp
var errors = ValidateSubmitTask(request);
if (errors is not null)
    return ResultExtensions.ValidationFailed(errors);
```

### 4. Plugin Config Validation Against Catalog

When `ConnectPlugin` is called, validate `request.Config` against the plugin schema from the catalog:

1. Call `registry.GetCatalog()` to get available plugin schemas.
2. Find the schema matching `request.Type`.
3. If no schema found → **skip config validation** (plugin type may not have a catalog entry yet).
4. If schema found:
   - Check all `Required` fields from `PluginConfigField` are present and non-empty in `request.Config`.
   - **Unknown keys** (keys not in schema) → **warn but allow** — include a `warnings` list in the successful response rather than rejecting. This allows forward-compatible config keys.

**Validation method** lives in `PluginEndpoints.cs`:

```csharp
private static (Dictionary<string, string[]>? Errors, List<string> Warnings) ValidatePluginConfig(
    ConnectPluginRequest request,
    IReadOnlyList<PluginSchema> catalog)
{
    // Find matching schema, validate required fields, collect unknown key warnings
}
```

**Success response with warnings:**

When connection succeeds but unknown config keys were present, wrap the `PluginStatus` in a new response:

```csharp
public sealed record ConnectPluginResponse
{
    public required PluginStatus Status { get; init; }
    public List<string> Warnings { get; init; } = [];
}
```

If no warnings, `Warnings` is an empty list (not null — `WhenWritingNull` hides it, but empty list is more honest).

### 5. Global Exception Handler

Add `app.UseExceptionHandler()` that catches unhandled exceptions and returns a `ProblemDetails` with status 500. No stack trace in production.

```csharp
app.UseExceptionHandler(error => error.Run(async context =>
{
    context.Response.StatusCode = 500;
    context.Response.ContentType = "application/problem+json";
    await context.Response.WriteAsJsonAsync(new ProblemDetails
    {
        Status = 500,
        Title = "Internal Server Error",
        Detail = "An unexpected error occurred."
    }, SiloApiJsonContext.Default.ProblemDetails);
}));
```

This ensures every failure returns `ProblemDetails`, even unhandled ones.

## File Map

| Action | File | Responsibility |
|--------|------|----------------|
| Create | `Api/ResultExtensions.cs` | Thin ProblemDetails helper methods (~25 lines) |
| Create | `Api/ConnectPluginResponse.cs` | Plugin connect response with warnings |
| Modify | `Api/Contracts.cs` | String → enum types in DTOs, remove `.ToString()` in factories |
| Modify | `Api/AgentEndpoints.cs` | Add validation methods, try/catch error mapping |
| Modify | `Api/WorkspaceEndpoints.cs` | Add validation, try/catch error mapping |
| Modify | `Api/ToolEndpoints.cs` | Add try/catch error mapping |
| Modify | `Api/PluginEndpoints.cs` | Add config validation against catalog, try/catch, warnings |
| Modify | `Api/SiloApiJsonContext.cs` | Register `ConnectPluginResponse`, `ProblemDetails`, `HttpValidationProblemDetails` for source gen |
| Modify | `Program.cs` | Add `UseExceptionHandler` for global 500 handler |

## What Does NOT Change

- No grain interface changes
- No domain model changes (enums already exist in domain)
- No CQRS command/query changes
- No new project references
- Plugin config `Dictionary<string, string>` stays as-is in the request — we validate it, not restructure it

## Test Strategy

Tests for this work are HTTP integration tests. Each endpoint group gets validation tests:

**AgentEndpoints validation tests** — `Weave.Silo.Tests/Api/AgentEndpointTests.cs` (or nearest test project):
- `SubmitTask_EmptyDescription_Returns400WithErrors`
- `SubmitTask_TooLongDescription_Returns400WithErrors`
- `SendMessage_EmptyContent_Returns400`
- `CompleteTask_EmptyProof_Returns400`
- `CompleteTask_InvalidProofType_Returns400`
- `ActivateAgent_EmptyModel_Returns400`
- `GetAgent_NotFound_Returns404ProblemDetails`
- `SubmitTask_AgentNotActive_Returns409`

**WorkspaceEndpoints validation tests:**
- `StartWorkspace_EmptyName_Returns400`
- `StartWorkspace_EmptyVersion_Returns400`

**PluginEndpoints validation tests:**
- `ConnectPlugin_EmptyName_Returns400`
- `ConnectPlugin_MissingRequiredConfig_Returns400`
- `ConnectPlugin_UnknownConfigKeys_ReturnsWarnings`

**Enum serialization tests:**
- `AgentResponse_Status_SerializesAsCamelCase`
- `TaskResponse_Status_SerializesAsCamelCase`
- `ProofItemResponse_Type_SerializesAsCamelCase`

If no Silo integration test project exists, these tests can verify serialization via the source-generated JSON context directly — no need for a running HTTP server.

## Out of Scope (Phase 2 and 3)

- Missing task list/get endpoints (Phase 2)
- Pagination on list endpoints (Phase 2)
- OpenAPI metadata / Swagger UI (Phase 3)
- Manifest schema discovery endpoint (Phase 3)
