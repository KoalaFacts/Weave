"""One Echo tool over MCP stdio; stdout is protocol-only."""
import json
import sys

TOOL = {
    "name": "echo", "description": "Return the supplied text unchanged.",
    "inputSchema": {"type": "object", "properties": {"text": {"type": "string"}},
                    "required": ["text"], "additionalProperties": False},
}
initialized = False
ready = False


def error(identifier, code, message):
    return {"jsonrpc": "2.0", "id": identifier, "error": {"code": code, "message": message}}


def handle(message):
    global initialized, ready
    if (not isinstance(message, dict) or message.get("jsonrpc") != "2.0"
            or not isinstance(message.get("method"), str)):
        return error(None, -32600, "Invalid request.")
    method = message["method"]
    if "id" not in message:
        if method == "notifications/initialized" and initialized:
            ready = True
        return None
    identifier = message["id"]
    if type(identifier) not in (int, str):
        return error(None, -32600, "Invalid request ID.")
    params = message.get("params", {})
    if not isinstance(params, dict):
        return error(identifier, -32602, "Expected object parameters.")
    if method == "initialize":
        initialized = True
        result = {"protocolVersion": "2024-11-05", "capabilities": {"tools": {}},
                  "serverInfo": {"name": "echo-plugin", "version": "0.1.0"}}
    elif method == "ping":
        result = {}
    elif not ready:
        return error(identifier, -32002, "Server not initialized.")
    elif method == "tools/list":
        result = {"tools": [TOOL]}
    elif method == "tools/call":
        if params.get("name") != "echo":
            return error(identifier, -32602, "Unknown tool.")
        args = params.get("arguments", {})
        valid = isinstance(args, dict) and set(args) == {"text"} and isinstance(args["text"], str)
        # Replace this expression with your own tool's behavior.
        text = args["text"] if valid else "Expected exactly one string argument: text."
        result = {"content": [{"type": "text", "text": text}], "isError": not valid}
    else:
        return error(identifier, -32601, "Unknown method.")
    return {"jsonrpc": "2.0", "id": identifier, "result": result}


if __name__ == "__main__":
    sys.stdin.reconfigure(encoding="utf-8")
    sys.stdout.reconfigure(encoding="utf-8")
    for line in sys.stdin:
        try:
            response = handle(json.loads(line))
        except json.JSONDecodeError:
            response = error(None, -32700, "Invalid JSON.")
        if response is not None:
            print(json.dumps(response, ensure_ascii=True), flush=True)
