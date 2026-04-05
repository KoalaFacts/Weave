# Deployment

> **Source**: `src/Deployment/` | **Depends on**: [Workspaces](workspaces.md) | **Depended on by**: [UX](ux.md)
> **See also**: [index](index.md)

The Deployment subsystem translates workspace manifests into platform-specific deployment configurations. It is a stateless translation layer with no CQRS, events, or grains.

## Projects

| Project | Purpose |
|---------|---------|
| `Weave.Deploy` | `IPublisher` interface and 5 publisher implementations |
| `Weave.Deploy.Tests` | Tests for all publishers |

## IPublisher Interface

```csharp
public interface IPublisher
{
    string TargetName { get; }
    Task<PublishResult> PublishAsync(WorkspaceManifest manifest, PublishOptions options, CancellationToken ct = default);
}

public sealed record PublishOptions
{
    public string OutputPath { get; init; } = "./output";
    public string? Registry { get; init; }
    public Dictionary<string, string> Variables { get; init; } = [];
}

public sealed record PublishResult
{
    public bool Success { get; init; }
    public string TargetName { get; init; } = string.Empty;
    public string OutputPath { get; init; } = string.Empty;
    public List<string> GeneratedFiles { get; init; } = [];
    public string? Error { get; init; }
}
```

## Publishers

### Docker Compose (`docker-compose`)

**Generates**: `docker-compose.yml`

- `weave-silo` service on ports 5000, 11111, 30000
- `redis` service (state store) on port 6379
- Tool containers with `read_only: true` and `cap_drop: [ALL]`
- Custom network from manifest (default: `weave-network`)
- `WEAVE_WORKSPACE` environment variable

### Kubernetes (`kubernetes`)

**Generates**: 4 YAML files — `namespace.yml`, `silo-deployment.yml`, `redis.yml`, `silo-service.yml`

- Namespace: `weave-{manifest.Name}`
- Replica count from `manifest.Targets["staging"].Replicas` (default: 1)
- Custom container registry via `options.Registry` (default: `ghcr.io/weave`)
- Redis via StatefulSet + ClusterIP Service
- Silo Deployment with 3 ports: HTTP (5000), Orleans Silo (11111), Orleans Gateway (30000)

### Fly.io (`fly-io`)

**Generates**: `fly.toml`

- App name: `weave-{manifest.Name}`
- Region from `manifest.Targets["production"].Region` (default: `iad`)
- Scaling from `manifest.Targets["production"].Scaling` (default: 1–10)
- Health check: `GET /health` every 10s
- VM: `shared-cpu-2x`
- Force HTTPS enabled

### HashiCorp Nomad (`nomad`)

**Generates**: `weave-{name}.nomad.hcl`

- Two job groups: **silo** (main app + Dapr sidecar) and **redis**
- Silo: 500 CPU, 512 MB memory, 3 port mappings
- Redis: 100 CPU, 128 MB memory
- Dapr sidecar runs as prestart hook

### GitHub Actions (`github-actions`)

**Generates**: `.github/workflows/weave-{name}.yml`

- Trigger from `manifest.Targets["ci"].Trigger` (default: `pull_request`)
- .NET 10.0.x setup with `actions/setup-dotnet@v4`
- Redis service container
- Starts Weave workspace via `dotnet run`
- Per-agent steps: `weave agent send {agentName} "Execute CI tasks"`

## Pipeline Flow

```
WorkspaceManifest
    ↓
Select publisher by target name
    ↓
Publisher.PublishAsync(manifest, options, ct)
    ├── Extract target-specific config from manifest
    ├── Build configuration text (StringBuilder)
    ├── Create output directory
    └── Write file(s) to disk
    ↓
PublishResult { Success, GeneratedFiles[], OutputPath }
```

## Design Principles

- **Stateless**: same input always produces same output
- **Async I/O**: all file operations support `CancellationToken`
- **Manifest-driven**: all config comes from `WorkspaceManifest`
- **Single responsibility**: one publisher per platform
- **Text-based output**: YAML, HCL, TOML — no binary artifacts

## Testing

17 test cases in `PublisherTests.cs`:
- Content validation for each publisher
- Target name correctness (parameterized)
- Edge cases: no tools, custom registry, cancellation, workspace name in filenames
- Path/structure validation for GitHub Actions and Fly.io
