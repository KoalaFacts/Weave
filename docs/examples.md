# Code Examples

> Practical examples drawn from the actual Weave codebase. Each example shows real patterns you can follow.
> **See also**: [index](index.md)

## Full-Featured Workspace

A workspace with agents, tools, channels, and self-improving skill memory — the complete picture of what Weave can do:

```jsonc
{
  "version": "1.0",
  "name": "support-team",
  "agents": {
    "support-bot": {
      "model": "claude-sonnet-4-20250514",
      "system_prompt_file": "./prompts/support.md",
      "tools": ["docs-search", "ticket-api"],
      "max_concurrent_tasks": 5
    },
    "monitor": {
      "model": "claude-haiku-4-5-20251001",
      "tools": ["health-check"],
      "heartbeat": {
        "cron": "*/5 * * * *",
        "tasks": ["Check service health"]
      }
    }
  },
  "tools": {
    "docs-search": {
      "type": "mcp",
      "mcp": {
        "server": "npx",
        "args": ["-y", "@anthropic/mcp-server-web-search"]
      }
    },
    "ticket-api": {
      "type": "direct_http",
      "direct_http": {
        "base_url": "https://tickets.example.com/api",
        "auth": { "type": "bearer", "token": "${secrets.ticket_token}" }
      }
    },
    "health-check": {
      "type": "cli",
      "cli": {
        "shell": "/bin/bash",
        "allowed_commands": ["curl *", "ping *"]
      }
    }
  },
  "channels": {
    "slack-support": {
      "type": "slack",
      "target_agent": "support-bot",
      "config": {
        "webhook_url": "${secrets.slack_webhook}"
      }
    },
    "telegram-alerts": {
      "type": "telegram",
      "target_agent": "monitor",
      "config": {
        "bot_token": "${secrets.telegram_bot_token}",
        "chat_id": "-100123456789"
      }
    }
  },
  "workspace": {
    "secrets": { "provider": "env" }
  }
}
```

What happens when you `weave workspace up support-team`:
1. Both agents activate with their tools and security tokens
2. The monitor agent starts checking health every 5 minutes
3. Slack messages from users route to `support-bot`, which searches docs and creates tickets
4. Each Slack user gets a `UserModelActor` that tracks their preferences and frequent topics
5. When the support-bot successfully resolves a multi-step issue, a skill document is auto-extracted
6. Next time a similar issue arrives, the agent retrieves the skill and resolves it faster

## Workspace Manifest

A complete `workspace.json` with agents, tools, targets, and secrets:

```jsonc
{
  "version": "1.0",
  "name": "test-workspace",
  "workspace": {
    "isolation": "full",
    "network": {
      "name": "weave-test",
      "subnet": "10.42.0.0/16"
    },
    "filesystem": {
      "root": "/var/weave/workspaces/test",
      "mounts": [
        { "source": "./data", "target": "/workspace/data", "readonly": false }
      ]
    },
    "secrets": {
      "provider": "vault",
      "vault": {
        "address": "https://vault.example.com",
        "mount": "weave/test"
      }
    }
  },
  "agents": {
    "researcher": {
      "model": "claude-sonnet-4-20250514",
      "system_prompt_file": "./prompts/researcher.md",
      "max_concurrent_tasks": 5,
      "memory": { "provider": "redis", "ttl": "24h" },
      "tools": ["web-search", "terminal"],
      "capabilities": ["net:outbound"],
      "heartbeat": {
        "cron": "*/30 * * * *",
        "tasks": ["Check for updates"]
      }
    }
  },
  "tools": {
    "web-search": {
      "type": "mcp",
      "mcp": {
        "server": "npx",
        "args": ["-y", "@anthropic/mcp-server-web-search"],
        "env": { "ANTHROPIC_API_KEY": "${secrets.anthropic_api_key}" }
      }
    },
    "terminal": {
      "type": "cli",
      "cli": {
        "shell": "/bin/bash",
        "allowed_commands": ["git *"],
        "denied_commands": ["rm -rf /"]
      }
    }
  },
  "targets": {
    "local": { "runtime": "podman" },
    "staging": { "runtime": "k3s", "replicas": 2 }
  }
}
```

## CLI Usage

### Create a workspace from a preset

```bash
weave workspace new my-app --preset coding-assistant
```

Available presets:

| Preset | Description | Tools |
|--------|-------------|-------|
| `starter` | One assistant, no tools | (none) |
| `coding-assistant` | Code tasks | git, file |
| `research` | Information gathering | web, document |
| `multi-agent` | Supervisor + workers | git, file, web |

### Start and manage a workspace

```bash
weave workspace up my-app                         # Start workspace
weave workspace status my-app                     # Check status
weave workspace down my-app                       # Stop workspace
weave workspace publish my-app --target kubernetes # Generate K8s manifests
```

### Add components

```bash
weave workspace add agent my-app --name coder --model claude-sonnet-4-20250514
weave workspace add tool my-app --name web --type mcp
weave workspace add target my-app --name prod --runtime docker
weave workspace plugin add my-app --name dapr --type dapr
```

## Adding a Branded ID

Declare in `Weave.Shared/Ids/BrandedIds.cs`:

```csharp
[BrandedId]
public readonly partial record struct WorkspaceId;
```

The source generator produces the full implementation. Usage:

```csharp
var id = WorkspaceId.New();           // UUID v7
var id2 = WorkspaceId.From("abc");    // From string
string s = id;                         // Implicit to string
bool empty = id.IsEmpty;              // Check empty
```

## Implementing a CQRS Command Handler

Define the command (in the domain project):

```csharp
public sealed record StartWorkspaceCommand(WorkspaceId WorkspaceId, WorkspaceManifest Manifest);
```

Implement the handler (in the Silo or domain project):

```csharp
// Source: src/Runtime/Weave.Silo/Api/WorkspaceCommandHandlers.cs
public sealed class StartWorkspaceHandler(IActorFactory actorFactory)
    : ICommandHandler<StartWorkspaceCommand, WorkspaceState>
{
    public async Task<WorkspaceState> HandleAsync(StartWorkspaceCommand command, CancellationToken ct)
    {
        var workspace = actorFactory.GetActor<IWorkspaceActor>(command.WorkspaceId.ToString());
        var state = await workspace.StartAsync(command.Manifest);

        var registry = actorFactory.GetActor<IWorkspaceRegistryActor>("active");
        await registry.RegisterAsync(command.WorkspaceId.ToString());

        var toolRegistry = actorFactory.GetActor<IToolRegistryActor>(command.WorkspaceId.ToString());
        await toolRegistry.ConnectToolsAsync(command.Manifest.Tools);
        await toolRegistry.ConfigureAccessAsync(command.Manifest.Agents.ToDictionary(
            static kvp => kvp.Key,
            static kvp => kvp.Value.Tools.ToList(),
            StringComparer.Ordinal));

        var supervisor = actorFactory.GetActor<IAgentSupervisorActor>(command.WorkspaceId.ToString());
        await supervisor.ActivateAllAsync(command.Manifest);

        foreach (var (agentName, definition) in command.Manifest.Agents)
        {
            if (definition.Heartbeat is null) continue;
            var heartbeat = actorFactory.GetActor<IHeartbeatActor>($"{command.WorkspaceId}/{agentName}");
            await heartbeat.StartAsync(new HeartbeatConfig
            {
                Cron = definition.Heartbeat.Cron,
                Tasks = definition.Heartbeat.Tasks,
                Enabled = true
            });
        }

        return await workspace.GetStateAsync();
    }
}
```

Handlers are registered automatically by the source-generated `AddGeneratedCqrsHandlers()`. Dispatch from an API endpoint:

```csharp
var result = await commandDispatcher.DispatchAsync<StartWorkspaceCommand, WorkspaceState>(
    new StartWorkspaceCommand(workspaceId, manifest), ct);
```

## Working with Orleans Actors

### Actor key conventions

```csharp
// Workspace actor — keyed by workspaceId
var workspace = actorFactory.GetActor<IWorkspaceActor>(workspaceId.ToString());

// Agent actor — keyed by workspaceId/agentName
var agent = actorFactory.GetActor<IAgentActor>($"{workspaceId}/{agentName}");

// Tool actor — keyed by workspaceId/toolName
var tool = actorFactory.GetActor<IToolActor>($"{workspaceId}/{toolName}");

// Singleton — fixed key
var registry = actorFactory.GetActor<IWorkspaceRegistryActor>("active");
```

### Implementing a actor with primary constructor DI

```csharp
// Source: src/Tools/Weave.Tools/Actors/ToolActor.cs
public sealed partial class ToolActor(
    IActorFactory actorFactory,
    IToolDiscoveryService discovery,
    ILeakScanner leakScanner,
    ICapabilityTokenService tokenService,
    ILifecycleManager lifecycleManager,
    IEventBus eventBus,
    ILogger<ToolActor> logger) : VirtualActor, IToolActor
{
    private ToolHandle? _handle;
    private ToolSpec? _definition;
    private string _workspaceId = string.Empty;
    private string _toolName = string.Empty;

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        // Parse actor key: "workspaceId/toolName"
        EnsureIdentity();
        return Task.CompletedTask;
    }

    public async Task<ToolHandle> ConnectAsync(ToolSpec definition, CapabilityToken token)
    {
        if (!tokenService.Validate(token))
            throw new UnauthorizedAccessException("Invalid or expired capability token");

        if (!token.HasGrant($"tool:{_toolName}") && !token.HasGrant("tool:*"))
            throw new UnauthorizedAccessException($"Token does not grant access to tool '{_toolName}'");

        var context = new LifecycleContext
        {
            WorkspaceId = WorkspaceId.From(_workspaceId),
            Phase = LifecyclePhase.ToolConnecting
        };
        await lifecycleManager.RunHooksAsync(LifecyclePhase.ToolConnecting, context, CancellationToken.None);

        var connector = discovery.GetConnector(definition.Type);
        _handle = await connector.ConnectAsync(definition, token);
        _definition = definition;

        await lifecycleManager.RunHooksAsync(LifecyclePhase.ToolConnected,
            context with { Phase = LifecyclePhase.ToolConnected }, CancellationToken.None);

        return _handle;
    }
}
```

## Minting and Validating Capability Tokens

```csharp
// Source: src/Security/Weave.Security/Tokens/CapabilityTokenService.cs

// Mint a token
var token = tokenService.Mint(new CapabilityTokenRequest
{
    WorkspaceId = "my-workspace",
    IssuedTo = "researcher-agent",
    Grants = ["tool:web-search", "secret:api-key"],
    Lifetime = TimeSpan.FromHours(1)
});

// Validate before use
if (!tokenService.Validate(token))
    throw new UnauthorizedAccessException("Invalid or expired token");

// Check specific grants
if (token.HasGrant("tool:web-search"))
    // agent can use web-search tool

// Revoke when done
tokenService.Revoke(token.TokenId);
```

## Publishing Domain Events

```csharp
// Source: src/Assistants/Weave.Agents/Actors/AgentActor.cs

// Publishing
await eventBus.PublishAsync(new AgentActivatedEvent
{
    SourceId = agentState.AgentId,
    AgentName = agentState.AgentName,
    WorkspaceId = workspaceId,
    Model = definition.Model,
    Tools = definition.Tools
}, CancellationToken.None);

// Subscribing
var subscription = eventBus.Subscribe<AgentActivatedEvent>(async (evt, ct) =>
{
    logger.LogInformation("Agent {Name} activated with model {Model}", evt.AgentName, evt.Model);
});

// Unsubscribe when done
subscription.Dispose();
```

## Registering Lifecycle Hooks

```csharp
// Define a hook
public sealed class MyToolHook : ILifecycleHook
{
    public LifecyclePhase Phase => LifecyclePhase.ToolConnected;
    public int Order => 10;

    public Task ExecuteAsync(LifecycleContext context, CancellationToken ct)
    {
        // Runs after every tool connection in the workspace
        Console.WriteLine($"Tool {context.ToolName} connected in {context.WorkspaceId}");
        return Task.CompletedTask;
    }
}

// Register
using var registration = lifecycleManager.Register(new MyToolHook());

// Hooks execute in Order within each phase
await lifecycleManager.RunHooksAsync(LifecyclePhase.ToolConnected, context, ct);
```

## Writing Tests

Pattern: NSubstitute for mocking + Shouldly for assertions.

```csharp
// Source: src/Tools/Weave.Tools.Tests/ToolActorTests.cs

public sealed class ToolActorTests
{
    // Factory method to create actor with mocked dependencies
    private static (ToolActor Actor, IToolConnector Connector, ICapabilityTokenService TokenService) CreateActor()
    {
        var actorFactory = Substitute.For<IActorFactory>();
        var connector = Substitute.For<IToolConnector>();
        connector.ToolType.Returns(ToolType.Cli);

        var discovery = Substitute.For<IToolDiscoveryService>();
        discovery.GetConnector(ToolType.Cli).Returns(connector);

        var leakScanner = new LeakScanner(Substitute.For<ILogger<LeakScanner>>());
        var tokenService = new CapabilityTokenService();
        var lifecycleManager = Substitute.For<ILifecycleManager>();
        var eventBus = Substitute.For<IEventBus>();
        var secretProxy = Substitute.For<ISecretProxyActor>();
        secretProxy.SubstituteAsync(Arg.Any<string>()).Returns(callInfo => callInfo.Arg<string>());
        actorFactory.GetActor<ISecretProxyActor>(Arg.Any<string>(), null).Returns(secretProxy);

        var actor = new ToolActor(actorFactory, discovery, leakScanner, tokenService,
            lifecycleManager, eventBus, Substitute.For<ILogger<ToolActor>>());
        return (actor, connector, tokenService);
    }

    private static CapabilityToken CreateToken(ICapabilityTokenService svc) =>
        svc.Mint(new CapabilityTokenRequest
        {
            WorkspaceId = "test",
            IssuedTo = "agent",
            Grants = ["tool:*", "secret:*"],
            Lifetime = TimeSpan.FromHours(1)
        });

    [Fact]
    public async Task ConnectAsync_WithValidToken_ReturnsHandle()
    {
        var (actor, connector, tokenSvc) = CreateActor();
        var token = CreateToken(tokenSvc);

        connector.ConnectAsync(Arg.Any<ToolSpec>(), Arg.Any<CapabilityToken>(), Arg.Any<CancellationToken>())
            .Returns(new ToolHandle { ToolName = "test-tool", Type = ToolType.Cli, IsConnected = true });

        var spec = new ToolSpec { Name = "test-tool", Type = ToolType.Cli, Cli = new CliConfig() };
        var handle = await actor.ConnectAsync(spec, token);

        handle.ShouldNotBeNull();
        handle.ToolName.ShouldBe("test-tool");
        handle.IsConnected.ShouldBeTrue();
    }

    [Fact]
    public async Task ConnectAsync_WithExpiredToken_Throws()
    {
        var (actor, _, tokenSvc) = CreateActor();
        var token = tokenSvc.Mint(new CapabilityTokenRequest
        {
            WorkspaceId = "test",
            IssuedTo = "agent",
            Grants = ["*"],
            Lifetime = TimeSpan.FromMilliseconds(-1) // already expired
        });

        var spec = new ToolSpec { Name = "tool", Type = ToolType.Cli };
        await Should.ThrowAsync<UnauthorizedAccessException>(() => actor.ConnectAsync(spec, token));
    }

    [Fact]
    public async Task InvokeAsync_WithSecretInPayload_BlocksInvocation()
    {
        var (actor, connector, tokenSvc) = CreateActor();
        var token = CreateToken(tokenSvc);

        connector.ConnectAsync(Arg.Any<ToolSpec>(), Arg.Any<CapabilityToken>(), Arg.Any<CancellationToken>())
            .Returns(new ToolHandle { ToolName = "tool", Type = ToolType.Cli, IsConnected = true });

        await actor.ConnectAsync(new ToolSpec { Name = "tool", Type = ToolType.Cli, Cli = new CliConfig() }, token);

        var result = await actor.InvokeAsync(new ToolInvocation
        {
            ToolName = "tool",
            Method = "exec",
            RawInput = "AKIAIOSFODNN7EXAMPLE" // AWS key triggers leak scanner
        }, token);

        result.Success.ShouldBeFalse();
        result.Error!.ShouldContain("secret leak");
    }
}
```

### Test naming convention

```
MethodName_Condition_ExpectedResult
```

Examples:
- `ConnectAsync_WithValidToken_ReturnsHandle`
- `Validate_WithRevokedToken_ReturnsFalse`
- `ScanStringAsync_WithKnownPatterns_DetectsLeak`

### Common test gotchas

- Use `ShouldNotBeNull()` or `!` before asserting on nullable properties (warnings are errors)
- `SecretValue.ToString()` returns `"***REDACTED***"` — use `.DecryptToString()` for actual values
- Use `NullLogger<T>.Instance` when NSubstitute can't mock `ILogger<T>` for internal types
- `AITool` cannot be mocked — create a concrete stub that overrides `Name`
- HTTP connectors: use `StubHandler : HttpMessageHandler` for test isolation
- Actors: make `private static` helpers `internal static` for direct testing

## Implementing a Tool Connector

```csharp
// 1. Implement IToolConnector
public sealed class MyCustomConnector : IToolConnector
{
    public ToolType ToolType => ToolType.DirectHttp; // or a new ToolType

    public Task<ToolHandle> ConnectAsync(ToolSpec spec, CapabilityToken token, CancellationToken ct = default)
    {
        // Validate config, establish connection
        return Task.FromResult(new ToolHandle
        {
            ToolName = spec.Name,
            Type = ToolType,
            ConnectionId = Guid.NewGuid().ToString("N"),
            IsConnected = true
        });
    }

    public Task DisconnectAsync(ToolHandle handle, CancellationToken ct = default)
    {
        // Clean up resources
        return Task.CompletedTask;
    }

    public Task<ToolResult> InvokeAsync(ToolHandle handle, ToolInvocation invocation, CancellationToken ct = default)
    {
        // Execute tool logic
        return Task.FromResult(new ToolResult
        {
            Success = true,
            Output = "result",
            ToolName = handle.ToolName,
            Duration = TimeSpan.FromMilliseconds(42)
        });
    }

    public Task<ToolSchema> DiscoverSchemaAsync(ToolHandle handle, CancellationToken ct = default)
    {
        return Task.FromResult(new ToolSchema
        {
            ToolName = handle.ToolName,
            Description = "My custom tool",
            Parameters = [new ToolParameter { Name = "input", Type = "string", Required = true }]
        });
    }
}

// 2. Register in Program.cs
builder.Services.AddSingleton<IToolConnector, MyCustomConnector>();
```

## Implementing a Deploy Publisher

```csharp
// 1. Implement IPublisher
public sealed class MyCloudPublisher : IPublisher
{
    public string TargetName => "my-cloud";

    public async Task<PublishResult> PublishAsync(
        WorkspaceManifest manifest, PublishOptions options, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var sb = new StringBuilder();
        sb.AppendLine($"# Config for {manifest.Name}");
        // ... generate platform-specific config ...

        var outputPath = Path.Combine(options.OutputPath, $"{manifest.Name}.yaml");
        Directory.CreateDirectory(options.OutputPath);
        await File.WriteAllTextAsync(outputPath, sb.ToString(), ct);

        return new PublishResult
        {
            Success = true,
            TargetName = TargetName,
            OutputPath = options.OutputPath,
            GeneratedFiles = [outputPath]
        };
    }
}

// 2. Wire into CLI publish command
```

## REST API — Skills, Channels, Users, Marketplace, Templates

All new features are accessible via the Silo's REST API (default port 5000).

### Skills API

```bash
# List all skills in a workspace
GET /api/workspaces/{workspaceId}/skills

# Search skills by keyword
GET /api/workspaces/{workspaceId}/skills/search?q=deploy&max=5

# Get a specific skill
GET /api/workspaces/{workspaceId}/skills/{skillId}

# Store a skill manually
POST /api/workspaces/{workspaceId}/skills
{
  "title": "Database migration",
  "description": "Steps to run a safe database migration",
  "tags": ["database", "migration"],
  "steps": [
    { "action": "Create backup", "tool_name": "cli" },
    { "action": "Run migration script", "tool_name": "cli" },
    { "action": "Verify schema", "tool_name": "cli" }
  ],
  "tools_used": ["cli"],
  "created_by_agent": "dba-agent"
}

# Remove a skill
DELETE /api/workspaces/{workspaceId}/skills/{skillId}
```

### Channels API

```bash
# List registered channels
GET /api/workspaces/{workspaceId}/channels

# Register a channel
POST /api/workspaces/{workspaceId}/channels
{ "type": "slack", "name": "general", "target_agent": "support-bot",
  "config": { "webhook_url": "https://hooks.slack.com/..." } }

# Webhook entry point for inbound messages
POST /api/workspaces/{workspaceId}/channels/inbound
{ "channel_id": "ch-1", "source_channel": "slack",
  "sender_id": "U12345", "sender_name": "Alice",
  "content": "How do I reset my password?" }
# -> Returns: { "channel_id": "ch-1", "content": "Here's how to reset..." }

# Unregister a channel
DELETE /api/workspaces/{workspaceId}/channels/{channelId}
```

### Users API

```bash
# Get user profile
GET /api/workspaces/{workspaceId}/users/{userId}/profile

# Set a preference
PUT /api/workspaces/{workspaceId}/users/{userId}/preferences
{ "key": "language", "value": "python" }

# Set domain context
PUT /api/workspaces/{workspaceId}/users/{userId}/context
{ "key": "team", "value": "backend" }

# Clear user profile
DELETE /api/workspaces/{workspaceId}/users/{userId}
```

### Marketplace API

```bash
# Browse published items
GET /api/marketplace
GET /api/marketplace/search?q=github&category=ToolConnector

# Get item details
GET /api/marketplace/{itemId}

# Submit a new item (starts as Draft)
POST /api/marketplace
{ "name": "GitHub MCP", "description": "...", "category": "ToolConnector",
  "version": "1.0.0", "author": "platform-team", "tags": ["github"] }

# Publish with security review
POST /api/marketplace/{itemId}/publish
{ "reviewer_id": "sec-lead", "approved": true, "notes": "Reviewed" }

# Rate an item
POST /api/marketplace/{itemId}/rate
{ "rating": 4.5 }

# Deprecate
POST /api/marketplace/{itemId}/deprecate
```

### Templates API

```bash
# Browse published templates
GET /api/templates
GET /api/templates/search?q=code+review

# Get template details
GET /api/templates/{templateId}

# Register a template (starts as Draft)
POST /api/templates
{ "name": "Code Reviewer", "description": "...", "version": "1.0.0",
  "author": "platform-team",
  "agent_definition": { "model": "claude-sonnet-4-20250514", "tools": ["git"] },
  "required_tools": { "git": { "type": "cli", "cli": { "allowed_commands": ["git *"] } } } }

# Validate and publish
POST /api/templates/{templateId}/publish

# Deprecate
POST /api/templates/{templateId}/deprecate
```
