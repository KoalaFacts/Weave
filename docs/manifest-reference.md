# Manifest Reference

A workspace manifest is a JSONC file that defines everything about your Weave workspace — the assistants, the tools they can use, the security boundaries, and where it all runs. JSONC means you can use comments and trailing commas.

## Minimal Example

```jsonc
{
  "version": "1.0",
  "name": "my-workspace",
  "agents": {
    "assistant": {
      "model": "claude-sonnet-4-20250514",
      "tools": ["search"]
    }
  },
  "tools": {
    "search": {
      "type": "mcp",
      "mcp": {
        "server": "npx",
        "args": ["-y", "@anthropic/mcp-server-web-search"]
      }
    }
  }
}
```

## Root Fields

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `version` | string | Yes | Must be `"1.0"` |
| `name` | string | Yes | Workspace identifier |
| `workspace` | object | No | Workspace-level configuration |
| `agents` | object | No | Dictionary of agent definitions |
| `tools` | object | No | Dictionary of tool definitions |
| `targets` | object | No | Dictionary of deployment targets |
| `hooks` | object | No | Lifecycle hooks |
| `plugins` | object | No | Plugin configurations |

---

## workspace

Controls isolation, networking, filesystem mounts, and secret management.

```jsonc
"workspace": {
  "isolation": "full",
  "network": {
    "name": "weave-net",
    "subnet": "10.42.0.0/16"
  },
  "filesystem": {
    "root": "/var/weave/workspaces",
    "mounts": [
      {
        "source": "./data",
        "target": "/workspace/data",
        "readonly": false
      }
    ]
  },
  "secrets": {
    "provider": "env",
    "vault": {
      "address": "https://vault.example.com",
      "mount": "weave/prod"
    }
  }
}
```

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `isolation` | string | `"full"` | `full`, `shared`, or `none` |
| `network.name` | string | — | Network name for the workspace |
| `network.subnet` | string | — | Subnet CIDR range |
| `filesystem.root` | string | — | Root mount point |
| `filesystem.mounts` | array | `[]` | List of mount configurations |
| `secrets.provider` | string | `"env"` | `env` or `vault` |
| `secrets.vault.address` | string | — | Vault server address |
| `secrets.vault.mount` | string | — | Vault mount path |

---

## agents

Each key is the agent name. Agents are the AI assistants that do work on your behalf.

```jsonc
"agents": {
  "reviewer": {
    "model": "claude-sonnet-4-20250514",
    "system_prompt_file": "./prompts/reviewer.md",
    "max_concurrent_tasks": 3,
    "memory": {
      "provider": "in-memory",
      "ttl": "24h"
    },
    "tools": ["git", "file-reader"],
    "capabilities": ["net:outbound", "file:read"],
    "heartbeat": {
      "cron": "*/30 * * * *",
      "tasks": ["Check for new PRs"]
    },
    "target": {
      "labels": ["region:us-east"]
    }
  }
}
```

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `model` | string | **Required** | AI model identifier |
| `system_prompt_file` | string | — | Path to a system prompt file |
| `max_concurrent_tasks` | int | `1` | Max parallel tasks |
| `memory.provider` | string | `"in-memory"` | `in-memory` or `redis` |
| `memory.ttl` | string | — | Memory time-to-live (e.g. `"24h"`) |
| `tools` | array | `[]` | Names of tools this agent can use (must match keys in `tools`) |
| `capabilities` | array | `[]` | Capability tokens the agent holds |
| `heartbeat.cron` | string | — | Cron expression for recurring tasks |
| `heartbeat.tasks` | array | `[]` | Task descriptions to run on heartbeat |
| `target.labels` | array | `[]` | Scheduling labels for target selection |

---

## tools

Each key is the tool name. The `type` field determines which configuration block is required.

### Tool types

| Type | Description | Config block |
|------|-------------|--------------|
| `mcp` | Model Context Protocol server | `mcp` |
| `openapi` | OpenAPI specification | `openapi` |
| `cli` | Command-line tool | `cli` |
| `direct_http` | Direct HTTP endpoint | `direct_http` |
| `dapr` | Dapr service invocation | — |
| `library` | Internal library | — |

### mcp

```jsonc
"web-search": {
  "type": "mcp",
  "mcp": {
    "server": "npx",
    "args": ["-y", "@anthropic/mcp-server-web-search"],
    "env": {
      "ANTHROPIC_API_KEY": "${secrets.anthropic_api_key}"
    }
  }
}
```

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `mcp.server` | string | **Required** | Server command |
| `mcp.args` | array | `[]` | Command arguments |
| `mcp.env` | object | `{}` | Environment variables (supports `${secrets.*}` references) |

### openapi

```jsonc
"docs-api": {
  "type": "openapi",
  "openapi": {
    "spec_url": "https://api.example.com/openapi.json",
    "auth": {
      "type": "bearer",
      "token": "${secrets.docs_token}"
    }
  }
}
```

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `openapi.spec_url` | string | **Required** | URL to the OpenAPI spec |
| `openapi.auth.type` | string | — | `bearer`, `basic`, or `oauth2` |
| `openapi.auth.token` | string | — | Auth token or credentials |

### cli

```jsonc
"git": {
  "type": "cli",
  "cli": {
    "shell": "/bin/bash",
    "allowed_commands": ["git *", "npm test"],
    "denied_commands": ["git push --force", "rm -rf /"]
  }
}
```

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `cli.shell` | string | `"/bin/bash"` | Shell to execute commands |
| `cli.allowed_commands` | array | `[]` | Allowed command patterns (supports `*` wildcards) |
| `cli.denied_commands` | array | `[]` | Denied command patterns (checked first) |

### direct_http

```jsonc
"api-service": {
  "type": "direct_http",
  "direct_http": {
    "base_url": "http://my-service:8080",
    "auth": {
      "type": "bearer",
      "token": "${secrets.api_key}"
    }
  }
}
```

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `direct_http.base_url` | string | **Required** | Base URL of the HTTP service |
| `direct_http.auth.type` | string | — | `bearer` or `basic` |
| `direct_http.auth.token` | string | — | Auth token or credentials |

---

## targets

Each key is the target name. Targets define where and how a workspace runs.

```jsonc
"targets": {
  "local": {
    "runtime": "podman",
    "replicas": 1
  },
  "staging": {
    "runtime": "k3s",
    "replicas": 2,
    "region": "us-east",
    "trigger": "schedule",
    "scaling": {
      "min": 2,
      "max": 10
    }
  }
}
```

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `runtime` | string | **Required** | `podman`, `k3s`, or `docker` |
| `replicas` | int | `1` | Number of replicas |
| `region` | string | — | Geographic region |
| `trigger` | string | — | `manual` or `schedule` |
| `scaling.min` | int | `1` | Minimum replicas |
| `scaling.max` | int | `1` | Maximum replicas |

---

## hooks

Lifecycle hooks run scripts or commands at key moments.

```jsonc
"hooks": {
  "workspace": {
    "pre_start": ["./scripts/setup.sh"],
    "post_start": [],
    "pre_stop": [],
    "post_stop": ["./scripts/cleanup.sh"]
  },
  "agents": {
    "reviewer": {
      "on_activated": ["initialize"],
      "on_deactivated": ["save-state"],
      "on_error": ["alert-admin"]
    }
  },
  "tools": {
    "web-search": {
      "on_connected": ["log-connection"],
      "on_disconnected": [],
      "on_error": []
    }
  }
}
```

### Workspace hooks

| Hook | When it runs |
|------|-------------|
| `pre_start` | Before the workspace starts |
| `post_start` | After the workspace is running |
| `pre_stop` | Before the workspace shuts down |
| `post_stop` | After the workspace has stopped |

### Agent hooks

| Hook | When it runs |
|------|-------------|
| `on_activated` | When the agent grain is activated |
| `on_deactivated` | When the agent grain is deactivated |
| `on_error` | When the agent encounters an error |

### Tool hooks

| Hook | When it runs |
|------|-------------|
| `on_connected` | When the tool connector establishes a connection |
| `on_disconnected` | When the tool connector disconnects |
| `on_error` | When the tool encounters an error |

---

## plugins

Plugins add optional integrations that are activated based on runtime environment.

```jsonc
"plugins": {
  "dapr": {
    "type": "dapr",
    "description": "Dapr sidecar for service invocation",
    "config": {
      "port": "3500"
    }
  },
  "vault": {
    "type": "vault",
    "description": "HashiCorp Vault for secrets",
    "enabled_when": "env:VAULT_ADDR",
    "config": {
      "address": "http://localhost:8200",
      "token": "${secrets.vault_token}"
    }
  }
}
```

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `type` | string | **Required** | `dapr`, `vault`, `http`, `webhook`, or `custom` |
| `description` | string | — | Human-readable description |
| `enabled_when` | string | — | Conditional activation (e.g. `"env:VAULT_ADDR"`) |
| `config` | object | `{}` | Plugin-specific configuration |

---

## Secret References

Use `${secrets.*}` to reference secrets anywhere in the manifest. The actual values are resolved at runtime from the configured secrets provider.

```jsonc
"env": {
  "API_KEY": "${secrets.my_api_key}"
}
```

With the `env` provider, secrets are read from environment variables. With `vault`, they are fetched from HashiCorp Vault at the configured mount path.

---

## Validation Rules

The manifest is validated when you run `weave workspace validate`:

- `version` must be `"1.0"`
- `name` must be present and non-empty
- Each agent must have a `model`
- Agent `tools` lists must reference tool names that exist in the `tools` section
- Each tool must have a valid `type`
- If an agent has a `heartbeat`, the `cron` field is required
- Each target must have a `runtime`
- Each plugin must have a `type`
