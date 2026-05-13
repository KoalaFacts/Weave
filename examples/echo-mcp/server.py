#!/usr/bin/env python3
"""Echo MCP server: a minimal but protocol-complete demonstration.

Implements MCP protocol revision 2024-11-05 over two transports:

  stdio              newline-delimited JSON-RPC 2.0 over stdin/stdout.
                     This is what Weave's McpToolConnector connects to.

  http (Streamable)  Single POST endpoint that returns either:
                       application/json   — for a single response
                       text/event-stream  — for streaming tool calls
                     Plus a GET endpoint that opens an SSE channel for
                     server-initiated notifications. Matches the
                     Streamable HTTP transport (MCP rev 2025-03-26).

Single tool: echo
  Arguments:
    text       (required, string)  — the text to echo back
    chunk_size (optional, int)     — if > 0, split the echo into N-char
                                     chunks and stream them as progress
                                     notifications before the final result.
                                     Only takes effect over HTTP transport.

Usage:
  python server.py --transport stdio
  python server.py --transport http --port 8765
"""

from __future__ import annotations

import argparse
import http.server
import json
import sys
import threading
import time
import uuid

PROTOCOL_VERSION = "2024-11-05"
SERVER_INFO = {"name": "echo-mcp", "version": "0.1.0"}

TOOLS = [
    {
        "name": "echo",
        "description": "Echoes the provided text. If chunk_size > 0, streams chunks as progress notifications (HTTP transport only).",
        "inputSchema": {
            "type": "object",
            "properties": {
                "text": {"type": "string", "description": "Text to echo back"},
                "chunk_size": {"type": "integer", "description": "If > 0, chunk the response over SSE"},
            },
            "required": ["text"],
        },
    }
]


def handle_initialize(req_id, params):
    return {
        "jsonrpc": "2.0",
        "id": req_id,
        "result": {
            "protocolVersion": PROTOCOL_VERSION,
            "serverInfo": SERVER_INFO,
            "capabilities": {"tools": {}},
        },
    }


def handle_tools_list(req_id):
    return {"jsonrpc": "2.0", "id": req_id, "result": {"tools": TOOLS}}


def handle_tools_call(req_id, params):
    name = params.get("name")
    args = params.get("arguments", {})
    if name != "echo":
        return {"jsonrpc": "2.0", "id": req_id, "error": {"code": -32601, "message": f"unknown tool: {name}"}}
    text = args.get("text", "")
    return {
        "jsonrpc": "2.0",
        "id": req_id,
        "result": {"content": [{"type": "text", "text": text}], "isError": False},
    }


def handle_request(req):
    """Single-response dispatch. Used by stdio and HTTP non-streaming paths."""
    method = req.get("method")
    req_id = req.get("id")
    params = req.get("params") or {}
    if method == "initialize":
        return handle_initialize(req_id, params)
    if method == "notifications/initialized":
        return None  # notifications produce no response
    if method == "tools/list":
        return handle_tools_list(req_id)
    if method == "tools/call":
        return handle_tools_call(req_id, params)
    if req_id is None:
        return None  # unknown notification — drop
    return {"jsonrpc": "2.0", "id": req_id, "error": {"code": -32601, "message": f"unknown method: {method}"}}


def stream_tools_call(req_id, params):
    """Generator of SSE events for a streaming tools/call. Yields dicts
    that will be encoded as `data: <json>\\n\\n` SSE frames."""
    name = params.get("name")
    args = params.get("arguments", {})
    if name != "echo":
        yield {"jsonrpc": "2.0", "id": req_id, "error": {"code": -32601, "message": f"unknown tool: {name}"}}
        return
    text = args.get("text", "")
    chunk_size = int(args.get("chunk_size", 0) or 0)

    if chunk_size <= 0:
        yield {
            "jsonrpc": "2.0",
            "id": req_id,
            "result": {"content": [{"type": "text", "text": text}], "isError": False},
        }
        return

    for i in range(0, len(text), chunk_size):
        chunk = text[i : i + chunk_size]
        yield {
            "jsonrpc": "2.0",
            "method": "notifications/progress",
            "params": {"progress": i + len(chunk), "total": len(text), "message": chunk},
        }
        time.sleep(0.05)  # make the streaming visible
    yield {
        "jsonrpc": "2.0",
        "id": req_id,
        "result": {"content": [{"type": "text", "text": text}], "isError": False},
    }


def run_stdio():
    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        try:
            req = json.loads(line)
        except json.JSONDecodeError:
            continue
        resp = handle_request(req)
        if resp is not None:
            sys.stdout.write(json.dumps(resp) + "\n")
            sys.stdout.flush()


def _write_sse_event(handler: http.server.BaseHTTPRequestHandler, payload: dict) -> None:
    line = f"data: {json.dumps(payload)}\n\n".encode("utf-8")
    handler.wfile.write(line)
    handler.wfile.flush()


class McpHttpHandler(http.server.BaseHTTPRequestHandler):
    server_version = "echo-mcp/0.1"

    def log_message(self, fmt, *args):  # noqa: D401, N802 — silence default access log
        pass

    def do_POST(self):  # noqa: N802 — required by BaseHTTPRequestHandler
        if self.path != "/mcp":
            self.send_error(404, "use POST /mcp")
            return
        length = int(self.headers.get("Content-Length", "0") or "0")
        body = self.rfile.read(length).decode("utf-8") if length else ""
        try:
            req = json.loads(body)
        except json.JSONDecodeError:
            self.send_error(400, "invalid JSON")
            return

        accept = self.headers.get("Accept", "")
        params = req.get("params") or {}
        wants_streaming = (
            req.get("method") == "tools/call"
            and isinstance(params, dict)
            and int((params.get("arguments") or {}).get("chunk_size", 0) or 0) > 0
        )

        if "text/event-stream" in accept and wants_streaming:
            self.send_response(200)
            self.send_header("Content-Type", "text/event-stream")
            self.send_header("Cache-Control", "no-cache")
            self.end_headers()
            for event in stream_tools_call(req.get("id"), params):
                _write_sse_event(self, event)
            return

        resp = handle_request(req)
        if resp is None:
            self.send_response(204)
            self.end_headers()
            return

        payload = json.dumps(resp).encode("utf-8")
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(payload)))
        self.end_headers()
        self.wfile.write(payload)

    def do_GET(self):  # noqa: N802
        if self.path != "/mcp":
            self.send_error(404, "use GET /mcp for server-initiated SSE stream")
            return
        self.send_response(200)
        self.send_header("Content-Type", "text/event-stream")
        self.send_header("Cache-Control", "no-cache")
        self.end_headers()
        try:
            for _ in range(3):  # send a few demonstration pings then close
                _write_sse_event(self, {
                    "jsonrpc": "2.0",
                    "method": "notifications/message",
                    "params": {"level": "info", "data": f"ping {uuid.uuid4().hex[:8]}"},
                })
                time.sleep(1.0)
        except (BrokenPipeError, ConnectionResetError):
            pass  # client disconnected


def run_http(port: int):
    server = http.server.ThreadingHTTPServer(("127.0.0.1", port), McpHttpHandler)
    print(f"echo-mcp listening on http://127.0.0.1:{port}/mcp", flush=True)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        server.shutdown()


def main():
    parser = argparse.ArgumentParser(description="Echo MCP server")
    parser.add_argument("--transport", choices=["stdio", "http"], default="stdio")
    parser.add_argument("--port", type=int, default=8765)
    args = parser.parse_args()

    if args.transport == "stdio":
        run_stdio()
    else:
        run_http(args.port)


if __name__ == "__main__":
    main()
