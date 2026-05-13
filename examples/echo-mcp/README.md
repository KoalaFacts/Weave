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

Weave's HTTP transport today consumes only `application/json` responses — the
SSE-streaming response variant and the GET /mcp server-initiated SSE channel
are protocol-complete in this server but not yet wired into Weave's connector.

## Files

| File              | Purpose                                                    |
| ----------------- | ---------------------------------------------------------- |
| `server.py`       | The MCP server. Stdlib only, ~180 lines.                   |
| `workspace.json`  | Weave workspace manifest that registers the server.        |
| `README.md`       | This file.                                                 |
