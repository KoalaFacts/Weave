# Extract Grain Helpers Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox syntax for tracking.

**Goal:** Extract pure-function static helpers from `AgentGrain` and `ToolRegistryGrain` into four focused, public static classes — improving testability, reducing grain complexity, and unifying duplicate secret placeholder parsing.

**Architecture:** Four new static classes (`SecretPlaceholderParser`, `ToolInvocationBuilder`, `ToolSpecMapper`, `ChatMessageMapper`) absorb methods from grains and `TransparentSecretProxy`. Grains become thin consumers. No new project references needed.

**Tech Stack:** C# / .NET 10, Orleans (grain consumers only), xunit.v3 + Shouldly

---

## File Map

| Action | File | Responsibility |
|--------|------|----------------|
| Create | `src/Security/Weave.Security/Scanning/SecretPlaceholderParser.cs` | secret placeholder parsing and substitution |
| Create | `src/Security/Weave.Security.Tests/SecretPlaceholderParserTests.cs` | Tests for above |
| Create | `src/Tools/Weave.Tools/Builders/ToolInvocationBuilder.cs` | JSON to ToolInvocation, schema description |
| Create | `src/Tools/Weave.Tools.Tests/ToolInvocationBuilderTests.cs` | Tests for above |
| Create | `src/Tools/Weave.Tools/Mapping/ToolSpecMapper.cs` | ToolDefinition to ToolSpec mapping |
| Create | `src/Tools/Weave.Tools.Tests/ToolSpecMapperTests.cs` | Tests for above |
| Create | `src/Assistants/Weave.Agents/Pipeline/ChatMessageMapper.cs` | ConversationMessage and ChatMessage mapping |
| Create | `src/Assistants/Weave.Agents.Tests/ChatMessageMapperTests.cs` | Tests for above |
| Modify | `src/Assistants/Weave.Agents/Grains/AgentGrain.cs` | Remove extracted methods, call new classes |
| Modify | `src/Assistants/Weave.Agents/Grains/ToolRegistryGrain.cs` | Remove extracted methods, call new classes |
| Modify | `src/Security/Weave.Security/Proxy/TransparentSecretProxy.cs` | Delegate to SecretPlaceholderParser |
| Delete | `src/Assistants/Weave.Agents.Tests/AgentGrainStaticMethodTests.cs` | Tests migrated to new files |
| Delete | `src/Assistants/Weave.Agents.Tests/ToolRegistryGrainStaticMethodTests.cs` | Tests migrated to new files |

---

### Task 1: SecretPlaceholderParser — Implementation and Tests

**Files:**
- Create: `src/Security/Weave.Security/Scanning/SecretPlaceholderParser.cs`
- Create: `src/Security/Weave.Security.Tests/SecretPlaceholderParserTests.cs`

- [ ] **Step 1: Create SecretPlaceholderParser with EnumeratePaths and Substitute**

The `EnumeratePaths` method is the manual forward-scan algorithm currently in `ToolRegistryGrain.EnumerateSecretPaths` (lines 341-359), lifted verbatim. The `Substitute` method is new — it replaces matched placeholders using a resolver callback, scanning left-to-right and adjusting indices after each replacement. If the resolver returns null, the placeholder is left in place.

See the spec section "1. SecretPlaceholderParser" for the full class. Place it in `src/Security/Weave.Security/Scanning/SecretPlaceholderParser.cs`. Namespace: `Weave.Security.Scanning`.

- [ ] **Step 2: Write tests for SecretPlaceholderParser**

Create `src/Security/Weave.Security.Tests/SecretPlaceholderParserTests.cs`. Migrate the 7 `EnumerateSecretPaths_*` tests from `ToolRegistryGrainStaticMethodTests.cs`, retargeting calls from `ToolRegistryGrain.EnumerateSecretPaths` to `SecretPlaceholderParser.EnumeratePaths`. Add 10 new tests for `Substitute`: registered path replaces value, unregistered path leaves placeholder, multiple paths, mixed known/unknown, same placeholder twice, empty content, no placeholders, malformed placeholder, adjacent placeholders, path with slashes.

- [ ] **Step 3: Run tests to verify they pass**

Run: `dotnet test --project src/Security/Weave.Security.Tests --filter "SecretPlaceholderParser"`
Expected: All 17 tests PASS

- [ ] **Step 4: Commit**

Message: `feat: extract SecretPlaceholderParser from grain and proxy`

---

### Task 2: ToolInvocationBuilder — Implementation and Tests

**Files:**
- Create: `src/Tools/Weave.Tools/Builders/ToolInvocationBuilder.cs`
- Create: `src/Tools/Weave.Tools.Tests/ToolInvocationBuilderTests.cs`

- [ ] **Step 1: Create ToolInvocationBuilder**

Lift `CreateToolInvocation` (lines 527-581) as `FromInput` and `BuildToolDescription` (lines 583-608) as `DescribeSchema` from `AgentGrain.cs`. Both are pure static methods. Place in `src/Tools/Weave.Tools/Builders/ToolInvocationBuilder.cs`. Namespace: `Weave.Tools.Builders`. Dependencies: `System.Text.Json`, `Weave.Tools.Models`.

- [ ] **Step 2: Write tests for ToolInvocationBuilder**

Create `src/Tools/Weave.Tools.Tests/ToolInvocationBuilderTests.cs`. Migrate the 10 `CreateToolInvocation_*` tests (retarget to `ToolInvocationBuilder.FromInput`) and 4 `BuildToolDescription_*` tests (retarget to `ToolInvocationBuilder.DescribeSchema`) from `AgentGrainStaticMethodTests.cs`.

- [ ] **Step 3: Run tests to verify they pass**

Run: `dotnet test --project src/Tools/Weave.Tools.Tests --filter "ToolInvocationBuilder"`
Expected: All 14 tests PASS

- [ ] **Step 4: Commit**

Message: `feat: extract ToolInvocationBuilder from AgentGrain`

---

### Task 3: ToolSpecMapper — Implementation and Tests

**Files:**
- Create: `src/Tools/Weave.Tools/Mapping/ToolSpecMapper.cs`
- Create: `src/Tools/Weave.Tools.Tests/ToolSpecMapperTests.cs`

- [ ] **Step 1: Create ToolSpecMapper**

Lift `MapToToolSpec` (lines 304-330) as `FromDefinition` and `ResolveEndpoint` (lines 332-339) from `ToolRegistryGrain.cs`. Place in `src/Tools/Weave.Tools/Mapping/ToolSpecMapper.cs`. Namespace: `Weave.Tools.Mapping`. Dependencies: `Weave.Tools.Models`, `Weave.Workspaces.Models`.

- [ ] **Step 2: Write tests for ToolSpecMapper**

Create `src/Tools/Weave.Tools.Tests/ToolSpecMapperTests.cs`. Migrate the 7 `MapToToolSpec_*` tests (retarget to `ToolSpecMapper.FromDefinition`) and 6 `ResolveEndpoint_*` tests from `ToolRegistryGrainStaticMethodTests.cs`.

- [ ] **Step 3: Run tests to verify they pass**

Run: `dotnet test --project src/Tools/Weave.Tools.Tests --filter "ToolSpecMapper"`
Expected: All 13 tests PASS

- [ ] **Step 4: Commit**

Message: `feat: extract ToolSpecMapper from ToolRegistryGrain`

---

### Task 4: ChatMessageMapper — Implementation and Tests

**Files:**
- Create: `src/Assistants/Weave.Agents/Pipeline/ChatMessageMapper.cs`
- Create: `src/Assistants/Weave.Agents.Tests/ChatMessageMapperTests.cs`

- [ ] **Step 1: Create ChatMessageMapper**

Lift `ToChatMessage` (lines 610-624) and `ToConversationMessages` (lines 628-662) from `AgentGrain.cs`. The `ToConversationMessages` method needs the `ToolInputJsonOptions` field and AOT suppression attributes. Place in `src/Assistants/Weave.Agents/Pipeline/ChatMessageMapper.cs`. Namespace: `Weave.Agents.Pipeline`. Dependencies: `Microsoft.Extensions.AI`, `Weave.Agents.Models`.

- [ ] **Step 2: Write tests for ChatMessageMapper**

Create `src/Assistants/Weave.Agents.Tests/ChatMessageMapperTests.cs`. Migrate the 7 `ToChatMessage_*` tests from `AgentGrainStaticMethodTests.cs` (retarget to `ChatMessageMapper.ToChatMessage`). Add 6 new tests for `ToConversationMessages`: plain text yields single message, function call yields tool message, function result yields tool message, empty text skips text message, text and function call yields both, preserves timestamp.

- [ ] **Step 3: Run tests to verify they pass**

Run: `dotnet test --project src/Assistants/Weave.Agents.Tests --filter "ChatMessageMapper"`
Expected: All 13 tests PASS

- [ ] **Step 4: Commit**

Message: `feat: extract ChatMessageMapper from AgentGrain`

---

### Task 5: Rewire AgentGrain to Use Extracted Classes

**Files:**
- Modify: `src/Assistants/Weave.Agents/Grains/AgentGrain.cs`

- [ ] **Step 1: Replace static methods with calls to extracted classes**

1. Add `using Weave.Agents.Pipeline;` and `using Weave.Tools.Builders;` at the top.
2. Delete the `ToolInputJsonOptions` field (line 26).
3. Replace `ToChatMessage(historyMessage)` (line 192) with `ChatMessageMapper.ToChatMessage(historyMessage)`.
4. Replace `ToConversationMessages(responseMessage)` (line 214) with `ChatMessageMapper.ToConversationMessages(responseMessage)`.
5. Replace `BuildToolDescription(resolution.Schema)` (line 507) with `ToolInvocationBuilder.DescribeSchema(resolution.Schema)`.
6. Replace `CreateToolInvocation(toolName, input)` (line 522) with `ToolInvocationBuilder.FromInput(toolName, input)`.
7. Delete the five extracted methods: `CreateToolInvocation` (527-581), `BuildToolDescription` (583-608), `ToChatMessage` (610-624), `ToConversationMessages` (628-662), and `ToolInputJsonOptions` field.

- [ ] **Step 2: Build to verify no compile errors**

Run: `dotnet build Weave.slnx`
Expected: Build succeeded, 0 errors

- [ ] **Step 3: Run AgentGrain tests to verify no regressions**

Run: `dotnet test --project src/Assistants/Weave.Agents.Tests --filter "AgentGrain"`
Expected: All existing AgentGrainTests PASS

- [ ] **Step 4: Commit**

Message: `refactor: rewire AgentGrain to use ToolInvocationBuilder and ChatMessageMapper`

---

### Task 6: Rewire ToolRegistryGrain to Use Extracted Classes

**Files:**
- Modify: `src/Assistants/Weave.Agents/Grains/ToolRegistryGrain.cs`

- [ ] **Step 1: Replace static methods with calls to extracted classes**

1. Add `using Weave.Security.Scanning;` and `using Weave.Tools.Mapping;` at the top.
2. Replace `MapToToolSpec(toolName, ...)` (line 196) with `ToolSpecMapper.FromDefinition(toolName, ...)`.
3. Replace all `ResolveEndpoint(definition)` calls (lines 188, 214, 239) with `ToolSpecMapper.ResolveEndpoint(definition)`.
4. Replace all `EnumerateSecretPaths(...)` calls (lines 282, 370) with `SecretPlaceholderParser.EnumeratePaths(...)`.
5. Delete the three extracted methods: `MapToToolSpec` (304-330), `ResolveEndpoint` (332-339), `EnumerateSecretPaths` (341-359).

- [ ] **Step 2: Build to verify no compile errors**

Run: `dotnet build Weave.slnx`
Expected: Build succeeded, 0 errors

- [ ] **Step 3: Run ToolRegistryGrain tests to verify no regressions**

Run: `dotnet test --project src/Assistants/Weave.Agents.Tests --filter "ToolRegistryGrain"`
Expected: All existing ToolRegistryGrainTests PASS

- [ ] **Step 4: Commit**

Message: `refactor: rewire ToolRegistryGrain to use ToolSpecMapper and SecretPlaceholderParser`

---

### Task 7: Rewire TransparentSecretProxy to Use SecretPlaceholderParser

**Files:**
- Modify: `src/Security/Weave.Security/Proxy/TransparentSecretProxy.cs`

- [ ] **Step 1: Replace regex-based substitution with SecretPlaceholderParser.Substitute**

1. Remove `using System.Text.RegularExpressions;` (line 2).
2. Replace the `SubstitutePlaceholders` body (lines 39-52) with delegation to `SecretPlaceholderParser.Substitute(content, path => ...)` where the lambda tries `_secretMapping.TryGetValue(path, ...)` and calls `LogPlaceholderNotRegistered(path)` on miss, returning null.
3. Delete the `[GeneratedRegex]` method `SecretPlaceholderRegex()` (lines 84-85).
4. Keep `partial` on the class — it is still needed for the `[LoggerMessage]` attribute on `LogPlaceholderNotRegistered`.

- [ ] **Step 2: Build to verify no compile errors**

Run: `dotnet build Weave.slnx`
Expected: Build succeeded, 0 errors

- [ ] **Step 3: Run TransparentSecretProxy tests to verify no regressions**

Run: `dotnet test --project src/Security/Weave.Security.Tests --filter "TransparentSecretProxy"`
Expected: All 21 existing tests PASS (they test through the proxy's public API which still delegates correctly)

- [ ] **Step 4: Commit**

Message: `refactor: rewire TransparentSecretProxy to use SecretPlaceholderParser`

---

### Task 8: Delete Old Test Files and Run Full Suite

**Files:**
- Delete: `src/Assistants/Weave.Agents.Tests/AgentGrainStaticMethodTests.cs`
- Delete: `src/Assistants/Weave.Agents.Tests/ToolRegistryGrainStaticMethodTests.cs`

- [ ] **Step 1: Delete the migrated test files**

Delete `AgentGrainStaticMethodTests.cs` and `ToolRegistryGrainStaticMethodTests.cs`. All their tests have been migrated to the new test files in Tasks 1-4.

- [ ] **Step 2: Build the full solution**

Run: `dotnet build Weave.slnx`
Expected: Build succeeded, 0 errors

- [ ] **Step 3: Run the full test suite**

Run: `dotnet test --solution Weave.slnx`
Expected: All tests PASS. Total count should be similar to before (tests moved, not removed) plus the new Substitute and ToConversationMessages tests.

- [ ] **Step 4: Commit**

Message: `refactor: delete migrated test files, complete helper extraction`
