# echo-mcp

A minimal but protocol-complete MCP server. Implements MCP protocol revision
**2024-11-05** over two transports — stdio (newline-delimited JSON-RPC) and
Streamable HTTP (POST + SSE per spec rev 2025-03-26).

Single tool: **echo**. Echoes the supplied `text` back. If `chunk_size > 0` and
the client speaks HTTP+SSE, the response is streamed as `notifications/progress`
events followed by the final result.

## Requirements

- Python 3.10+ (uses only the standard library — no `pip install`)

## Run

```bash
# stdio mode (what Weave's McpToolConnector connects to)
python3 server.py --transport stdio

# HTTP mode
python3 server.py --transport http --port 8765
```

## Wire examples

### stdio

```text
→ {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"curl","version":"0"}}}
← {"jsonrpc":"2.0","id":1,"result":{"protocolVersion":"2024-11-05","serverInfo":{"name":"echo-mcp","version":"0.1.0"},"capabilities":{"tools":{}}}}
→ {"jsonrpc":"2.0","method":"notifications/initialized"}
→ {"jsonrpc":"2.0","id":2,"method":"tools/list"}
← {"jsonrpc":"2.0","id":2,"result":{"tools":[{"name":"echo",...}]}}
→ {"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"echo","arguments":{"text":"hello"}}}
← {"jsonrpc":"2.0","id":3,"result":{"content":[{"type":"text","text":"hello"}],"isError":false}}
```

### HTTP — single JSON response

```bash
curl -X POST http://127.0.0.1:8765/mcp \
  -H 'Content-Type: application/json' \
  -d '{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"echo","arguments":{"text":"hello"}}}'
```

```json
{"jsonrpc": "2.0", "id": 1, "result": {"content": [{"type": "text", "text": "hello"}], "isError": false}}
```

### HTTP — SSE streaming (chunked progress + final result)

```bash
curl -N -X POST http://127.0.0.1:8765/mcp \
  -H 'Content-Type: application/json' \
  -H 'Accept: text/event-stream' \
  -d '{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"echo","arguments":{"text":"streaming demo","chunk_size":4}}}'
```

```text
data: {"jsonrpc":"2.0","method":"notifications/progress","params":{"progress":4,"total":14,"message":"stre"}}

data: {"jsonrpc":"2.0","method":"notifications/progress","params":{"progress":8,"total":14,"message":"amin"}}

data: {"jsonrpc":"2.0","method":"notifications/progress","params":{"progress":12,"total":14,"message":"g de"}}

data: {"jsonrpc":"2.0","method":"notifications/progress","params":{"progress":14,"total":14,"message":"mo"}}

data: {"jsonrpc":"2.0","id":2,"result":{"content":[{"type":"text","text":"streaming demo"}],"isError":false}}
```

### HTTP — GET /mcp (server-initiated SSE stream)

```bash
curl -N http://127.0.0.1:8765/mcp
```

The demo emits three `notifications/message` pings (one per second) and closes.
In a production server this channel carries server-pushed notifications.

## Use with Weave

The companion [`workspace.json`](./workspace.json) wires this server into a Weave
workspace. Weave supports both stdio and HTTP transports for MCP:

```jsonc
// stdio: Weave spawns the server as a subprocess
"echo-server": {
  "type": "mcp",
  "mcp": {
    "server": "python3",
    "args": ["examples/echo-mcp/server.py", "--transport", "stdio"]
  }
}

// http: server runs separately, Weave connects via Streamable HTTP
"echo-server": {
  "type": "mcp",
  "mcp": { "url": "http://127.0.0.1:8765/mcp" }
}
```

For the HTTP variant, start the server first:

```bash
python3 examples/echo-mcp/server.py --transport http --port 8765
```

Then boot the workspace:

```bash
dotnet run --project src/UX/Weave.Cli -- workspace up examples/echo-mcp/workspace.json
```

### Security defaults

The HTTP transport is hostile-peer aware:

- **SSRF defense.** URLs targeting loopback (127.x, ::1, localhost), private
  ranges (10/8, 172.16/12, 192.168/16, 100.64/10, fc00::/7), link-local
  (169.254/16, fe80::/10), or reserved ranges are **rejected by default**.
  For local development, set `allow_private_endpoints: true` in the manifest.
  URLs with userinfo (`http://user:pass@…`) or fragments are always rejected.
- **Resource caps.** `max_response_bytes` (default 16 MiB) bounds total bytes
  consumed per request. `max_frame_bytes` (default 1 MiB) bounds a single
  JSON body or SSE `data:` value. `max_queued_frames` (default 1024) bounds
  the in-memory channel for SSE streams.
- **Timeouts.** `request_timeout_seconds` (default 300) caps the entire
  request including streaming response. `idle_timeout_seconds` (default 30)
  caps the gap between SSE frames — a slow-loris dripping one byte every
  29s won't keep the connection alive indefinitely.
- **Diagnostics never include raw server bodies** — only status codes,
  truncated reasons, and counts.

Out of scope: `GET /mcp` server-initiated SSE channel, `Mcp-Session-Id`
headers, and `Authorization` header pass-through. The echo server emits
those for any spec-compliant client; Weave's connector doesn't drive them
yet.

## Files

| File              | Purpose                                                    |
| ----------------- | ---------------------------------------------------------- |
| `server.py`       | The MCP server. Stdlib only, ~180 lines.                   |
| `workspace.json`  | Weave workspace manifest that registers the server.        |
| `README.md`       | This file.                                                 |
