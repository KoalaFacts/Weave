# Extract Static Helpers from Grains

**Date:** 2026-04-05
**Status:** Approved
**Scope:** Refactor pure-function static helpers out of `AgentGrain` (663 lines) and `ToolRegistryGrain` (398 lines) into focused, testable classes.

## Problem

`AgentGrain` and `ToolRegistryGrain` contain static helper methods that perform pure transformations (JSON parsing, string formatting, type mapping, placeholder scanning) unrelated to Orleans grain lifecycle. This makes the grains harder to read and forces `internal static` visibility hacks for testability. Additionally, `{secret:X}` placeholder parsing is duplicated between `ToolRegistryGrain.EnumerateSecretPaths` (manual scan) and `TransparentSecretProxy.SecretPlaceholderRegex` (regex).

## Extracted Classes

### 1. SecretPlaceholderParser

**Location:** `src/Security/Weave.Security/Scanning/SecretPlaceholderParser.cs`

Unifies the duplicate `{secret:X}` parsing into a single public static class.

```csharp
public static class SecretPlaceholderParser
{
    public static IEnumerable<string> EnumeratePaths(string content)
    public static string Substitute(string content, Func<string, string?> resolver)
}
```

- `EnumeratePaths` — manual forward scan (allocation-friendly). Replaces `ToolRegistryGrain.EnumerateSecretPaths`.
- `Substitute` — replaces matched `{secret:X}` placeholders using a resolver callback. Replaces the regex-based substitution in `TransparentSecretProxy.SubstitutePlaceholders`.

**Consumers:**
- `ToolRegistryGrain` calls `SecretPlaceholderParser.EnumeratePaths()`.
- `TransparentSecretProxy.SubstitutePlaceholders()` delegates to `SecretPlaceholderParser.Substitute()`, passing its `_secretMapping` lookup as the resolver.
- `TransparentSecretProxy` keeps its `RegisterSecret`/`UnregisterSecret` state management — only the substitution logic moves.

**Tests:** `SecretPlaceholderParserTests` in `Weave.Security.Tests`. Absorbs existing tests from `ToolRegistryGrainStaticMethodTests.EnumerateSecretPaths_*` and `TransparentSecretProxyTests.SubstitutePlaceholders_*`, plus new shared tests.

### 2. ToolInvocationBuilder

**Location:** `src/Tools/Weave.Tools/Builders/ToolInvocationBuilder.cs`

Parses user input into `ToolInvocation` and formats `ToolSchema` into descriptions.

```csharp
public static class ToolInvocationBuilder
{
    public static ToolInvocation FromInput(string toolName, string? input)
    public static string DescribeSchema(ToolSchema schema)
}
```

- `FromInput` — JSON object parsing with `method`/`rawInput` extraction, fallback to plain text for non-JSON input. Replaces `AgentGrain.CreateToolInvocation`.
- `DescribeSchema` — builds human-readable description with parameter types, required annotations, and semicolon separators. Replaces `AgentGrain.BuildToolDescription`.

**Consumer:** `AgentGrain` calls both methods.

**Tests:** `ToolInvocationBuilderTests` in `Weave.Tools.Tests`. Absorbs existing tests from `AgentGrainStaticMethodTests.CreateToolInvocation_*` and `BuildToolDescription_*`.

### 3. ToolSpecMapper

**Location:** `src/Tools/Weave.Tools/Mapping/ToolSpecMapper.cs`

Maps workspace manifest `ToolDefinition` to connector `ToolSpec`.

```csharp
public static class ToolSpecMapper
{
    public static ToolSpec FromDefinition(string toolName, ToolDefinition definition)
    public static string? ResolveEndpoint(ToolDefinition definition)
}
```

- `FromDefinition` — type string to `ToolType` enum switch, config object mapping, `DirectHttp` auth header construction (`"{Type} {Token}"`). Replaces `ToolRegistryGrain.MapToToolSpec`.
- `ResolveEndpoint` — extracts endpoint URL per tool type (`mcp` → Server, `openapi` → SpecUrl, `direct_http` → BaseUrl, others → null). Replaces `ToolRegistryGrain.ResolveEndpoint`.

**Consumer:** `ToolRegistryGrain` calls both methods.

**Tests:** `ToolSpecMapperTests` in `Weave.Tools.Tests`. Absorbs existing tests from `ToolRegistryGrainStaticMethodTests.MapToToolSpec_*` and `ResolveEndpoint_*`.

### 4. ChatMessageMapper

**Location:** `src/Assistants/Weave.Agents/Pipeline/ChatMessageMapper.cs`

Maps between domain `ConversationMessage` and `Microsoft.Extensions.AI.ChatMessage`.

```csharp
public static class ChatMessageMapper
{
    public static ChatMessage ToChatMessage(ConversationMessage message)
    public static IEnumerable<ConversationMessage> ToConversationMessages(ChatMessage message)
}
```

- `ToChatMessage` — case-insensitive role switch (`"assistant"` → `ChatRole.Assistant`, unknown → `ChatRole.User`), timestamp preservation. Replaces `AgentGrain.ToChatMessage`.
- `ToConversationMessages` — yields one message per text content, plus additional messages for `FunctionCallContent` and `FunctionResultContent` items. Replaces `AgentGrain.ToConversationMessages`.

**Consumer:** `AgentGrain` calls both methods.

**Tests:** `ChatMessageMapperTests` in `Weave.Agents.Tests`. Absorbs existing tests from `AgentGrainStaticMethodTests.ToChatMessage_*`, plus new tests for `ToConversationMessages`.

## Dependency Flow

No new project references required. All classes land in projects that already have the needed dependencies:

```
SecretPlaceholderParser  →  Weave.Security (no deps beyond BCL)
ToolInvocationBuilder    →  Weave.Tools (uses Weave.Tools.Models)
ToolSpecMapper           →  Weave.Tools (uses Weave.Tools.Models + Weave.Workspaces.Models)
ChatMessageMapper        →  Weave.Agents (uses Microsoft.Extensions.AI + Weave.Agents.Models)
```

## What Changes in the Grains

### AgentGrain (663 → ~580 lines)

- Remove `CreateToolInvocation`, `BuildToolDescription`, `ToChatMessage`, `ToConversationMessages`, `ToolInputJsonOptions`
- Add `using Weave.Tools.Builders;` and `using Weave.Agents.Pipeline;`
- Call sites: `ToolInvocationBuilder.FromInput(...)`, `ToolInvocationBuilder.DescribeSchema(...)`, `ChatMessageMapper.ToChatMessage(...)`, `ChatMessageMapper.ToConversationMessages(...)`

### ToolRegistryGrain (398 → ~340 lines)

- Remove `MapToToolSpec`, `ResolveEndpoint`, `EnumerateSecretPaths`
- Add `using Weave.Tools.Mapping;` and `using Weave.Security.Scanning;`
- Call sites: `ToolSpecMapper.FromDefinition(...)`, `ToolSpecMapper.ResolveEndpoint(...)`, `SecretPlaceholderParser.EnumeratePaths(...)`

### TransparentSecretProxy

- Remove `SecretPlaceholderRegex` generated regex
- `SubstitutePlaceholders` body becomes a one-liner delegating to `SecretPlaceholderParser.Substitute(content, path => _secretMapping.TryGetValue(path, out var s) ? s.DecryptToString() : null)`
- Keeps `RegisterSecret`, `UnregisterSecret`, `ScanResponseAsync`, `ScanRequestAsync` unchanged

## Test Migration

| Old Test File | Tests Moving | New Test File |
|---|---|---|
| `AgentGrainStaticMethodTests.cs` | `CreateToolInvocation_*` (10), `BuildToolDescription_*` (4) | `ToolInvocationBuilderTests.cs` |
| `AgentGrainStaticMethodTests.cs` | `ToChatMessage_*` (7) | `ChatMessageMapperTests.cs` |
| `ToolRegistryGrainStaticMethodTests.cs` | `MapToToolSpec_*` (7), `ResolveEndpoint_*` (6) | `ToolSpecMapperTests.cs` |
| `ToolRegistryGrainStaticMethodTests.cs` | `EnumerateSecretPaths_*` (7) | `SecretPlaceholderParserTests.cs` |

After migration, `AgentGrainStaticMethodTests.cs` and `ToolRegistryGrainStaticMethodTests.cs` are deleted. New test files also add tests for `ToConversationMessages` and `SecretPlaceholderParser.Substitute`.

## Out of Scope

- Changing grain public APIs or Orleans wiring
- Extracting instance methods that depend on grain state (`SendAsync`, `ResolveAsync`, etc.)
- Changing `TransparentSecretProxy`'s public API (only its internal implementation changes)
- Modifying any deployment or workspace code
