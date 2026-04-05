# UX — CLI & Dashboard

The UX layer provides two user interfaces for managing Weave workspaces: a terminal CLI and a Blazor web dashboard.

## Projects

| Project | Purpose |
|---------|---------|
| `Weave.Cli` | System.CommandLine + Spectre.Console CLI |
| `Weave.Dashboard` | Blazor Web + FluentUI dashboard |

## CLI

### Command Tree

```
weave
├── workspace
│   ├── new <name> [--preset]          Create workspace from preset
│   ├── list                           List all workspaces
│   ├── remove <name> [--purge]        Remove workspace
│   ├── up <name> [--target]           Start workspace
│   ├── down <name>                    Stop workspace
│   ├── status <name>                  Show live/manifest status
│   ├── show <name>                    Display workspace.json
│   ├── validate <name>               Validate manifest
│   ├── publish <name> --target        Generate deployment configs
│   ├── presets                        List available presets
│   ├── add
│   │   ├── agent <ws> --name --model  Add agent
│   │   ├── tool <ws> --name --type    Add tool
│   │   ├── target <ws> --name --runtime  Add target
│   │   └── plugin <ws> --name --type  Add plugin
│   └── plugin
│       ├── list <ws>                  List plugins
│       ├── add <ws> --name --type     Add plugin
│       └── remove <ws> --name         Remove plugin
└── serve [--port] [--background]      Start local silo
```

### Key Commands

#### `workspace new`
Creates workspace directory structure with `workspace.json` and `prompts/` directory. Interactive preset selection: starter, coding-assistant, research, multi-agent.

#### `workspace up`
Reads manifest → validates → resolves prompt paths → POSTs to `/api/workspaces` → stores workspace ID in `.weave/workspace-id`.

#### `workspace status`
Tries live API first; falls back to manifest-based display. Shows agents, tools, and connection status in Spectre.Console tables.

#### `workspace publish`
Delegates to `IPublisher` implementations. Targets: docker-compose, kubernetes, nomad, fly-io, github-actions.

#### `serve`
Starts the Weave Silo locally. Default port 9401. Can run in foreground or background (detached process).

### Presets

| Preset | Description |
|--------|-------------|
| `starter` | One assistant, no tools |
| `coding-assistant` | Git + file tools |
| `research` | Web + document tools |
| `multi-agent` | Supervisor + worker agents with git, file, web tools |

### API Client

`WorkspaceApiClient` communicates with the Silo API:
- Base URL: `WEAVE_API_URL` env var or `http://localhost:9401`
- Methods: `StartWorkspaceAsync`, `GetWorkspaceAsync`, `GetAgentsAsync`, `GetToolsAsync`, `StopWorkspaceAsync`, `IsReachableAsync`

### Theme System

`CliTheme.cs` provides centralized terminal styling:
- Brand colors: Teal (primary), Soft Violet (accent), Dark Slate (surface)
- Semantic colors: Green (success), Coral-Red (error), Amber (warning)
- Icons: checkmark, cross, warning, bullet, brand diamond
- Helpers: `WriteBanner()`, `WriteSuccess()`, `WriteError()`, `CreateTable()`, `CreatePanel()`

### Tab Completions

Auto-complete for workspace names, presets, deployment targets, tool types, runtimes, and plugin types.

## Dashboard

### Architecture

- **Framework**: Blazor Web with Interactive Server rendering (SignalR)
- **UI Library**: Microsoft FluentUI AspNetCore Components
- **Service Discovery**: Aspire-compatible (`https+http://silo`)

### Navigation

| Route | Page | Description |
|-------|------|-------------|
| `/` | Home | Dashboard metrics (workspaces, agents, tools, costs) |
| `/workspaces` | Workspaces | DataGrid of all workspaces |
| `/workspaces/{id}` | WorkspaceDetail | Agents and tools for a workspace |
| `/agents` | Agents | Agent list + chat interface |
| `/tools` | Tools | DataGrid of connected tools |
| `/secrets` | Secrets | Secret management info |
| `/monitoring` | Monitoring | Observability features |
| `/logs` | Logs | Structured logging info |
| `/setup` | Setup | Multi-step wizard (runtime, secrets, workspace) |

### API Client

`WeaveApiClient` mirrors CLI communication with typed DTOs:
- `GetWorkspacesAsync()` → `List<WorkspaceDto>`
- `GetAgentsAsync(id)` → `List<AgentDto>`
- `SendMessageAsync(workspaceId, agentName, content)` → `AgentChatResponseDto?`
- `GetToolsAsync(id)` → `List<ToolConnectionDto>`

### Key Pages

#### Home
Metric cards showing workspace count, agent count, tool count, and LLM costs. Quick action links.

#### Workspaces
DataGrid with columns: Name (linked), Workspace ID, Status badge, Container count, timestamps.

#### Agents
Two-panel layout — agent list (left) and details + chat interface (right). Chat supports message history, tool usage display, and real-time interaction.

#### WorkspaceDetail
Workspace info card + agent DataGrid + tool DataGrid with status badges.

#### Setup
FluentWizard with steps: Runtime selection → Secrets provider → Workspace name/path → Review.

### JSON Serialization

Source-generated `DashboardJsonContext` with camelCase naming, case-insensitive matching, and null-value skipping.

### FluentUI Components Used

`FluentCard`, `FluentStack`, `FluentBadge`, `FluentDataGrid`, `FluentButton`, `FluentTextField`, `FluentProgressRing`, `FluentMessageBar`, `FluentAnchor`, `FluentRadioGroup`, `FluentWizard`, `FluentNavMenu`, layout components.

## Backend Integration

Both CLI and Dashboard follow the same pattern:

1. Read/create workspace manifest
2. Send HTTP request to Silo API
3. Silo dispatches CQRS commands/queries
4. Orleans grains handle state and coordination
5. Response mapped to API contracts → UI display
