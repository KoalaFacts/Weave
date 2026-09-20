"""Run the same small stdio contract against one real Echo plugin process."""
import json
from pathlib import Path
import subprocess
import sys
import unittest

ROOT = Path(__file__).resolve().parents[4]
SPEC = json.loads(Path(sys.argv.pop(1)).read_text(encoding="utf-8"))
COMMAND = [SPEC["mcp"]["server"], *SPEC["mcp"].get("args", [])]
SCHEMA = {
    "type": "object", "properties": {"text": {"type": "string"}},
    "required": ["text"], "additionalProperties": False,
}


def request(identifier, method, params=None):
    return {"jsonrpc": "2.0", "id": identifier, "method": method, "params": params or {}}


class EchoStdioTests(unittest.TestCase):
    def exchange(self, messages, initialize=True):
        if initialize:
            messages = [request(0, "initialize", {
                "protocolVersion": "2024-11-05", "capabilities": {},
                "clientInfo": {"name": "echo-example-check", "version": "1.0.0"},
            }), {"jsonrpc": "2.0", "method": "notifications/initialized"}, *messages]
        wire = "\n".join(m if isinstance(m, str) else json.dumps(m, ensure_ascii=True)
                         for m in messages) + "\n"
        result = subprocess.run(COMMAND, input=wire.encode("utf-8"), capture_output=True,
                                cwd=ROOT, timeout=15, check=False)
        self.assertEqual(result.returncode, 0, result.stderr.decode("utf-8", errors="replace"))
        # EOF must terminate the process; stdout is exclusively protocol JSON.
        replies = [json.loads(line) for line in result.stdout.decode("utf-8").splitlines()]
        for reply in replies:
            self.assertEqual(reply["jsonrpc"], "2.0")
        if initialize:
            self.assertEqual(replies[0]["id"], 0)
            self.assertEqual(replies[0]["result"]["protocolVersion"], "2024-11-05")
            self.assertEqual(replies[0]["result"]["capabilities"], {"tools": {}})
            return replies[1:]
        return replies

    def test_discovers_only_echo_with_the_shared_schema(self):
        replies = self.exchange([request("list", "tools/list")])
        self.assertEqual(len(replies), 1)
        self.assertEqual(replies[0]["id"], "list")
        tools = replies[0]["result"]["tools"]
        self.assertEqual(len(tools), 1)
        self.assertEqual(tools[0]["name"], "echo")
        self.assertEqual(tools[0]["inputSchema"], SCHEMA)

    def test_echo_preserves_empty_unicode_and_newlines(self):
        texts = ["", "hello", "你好，Weave 🙂 e\u0301", "line1\r\nline2\n\t\"quoted\"\\"]
        replies = self.exchange([request(i + 1, "tools/call", {
            "name": "echo", "arguments": {"text": text},
        }) for i, text in enumerate(texts)])
        self.assertEqual(len(replies), len(texts))
        for i, (text, reply) in enumerate(zip(texts, replies)):
            self.assertEqual(reply["id"], i + 1)
            self.assertEqual(reply["result"], {
                "content": [{"type": "text", "text": text}], "isError": False,
            })

    def test_invalid_arguments_are_tool_errors_not_success(self):
        inputs = [{}, {"text": None}, {"text": 42}, {"text": True},
                  {"text": []}, {"text": "ok", "extra": "not accepted"}]
        replies = self.exchange([request(i + 1, "tools/call", {
            "name": "echo", "arguments": args,
        }) for i, args in enumerate(inputs)])
        self.assertEqual(len(replies), len(inputs))
        for reply in replies:
            self.assertTrue(reply["result"]["isError"])
            self.assertEqual(reply["result"]["content"], [{
                "type": "text", "text": "Expected exactly one string argument: text.",
            }])

    def test_unknown_tool_is_a_protocol_error(self):
        reply, = self.exchange([request(1, "tools/call", {"name": "not-echo"})])
        self.assertEqual(reply["error"]["code"], -32602)

    def test_notifications_are_silent_and_ping_works(self):
        replies = self.exchange([
            {"jsonrpc": "2.0", "method": "notifications/unknown"},
            request(7, "ping"), request("unknown", "unknown/method"),
        ])
        self.assertEqual([r["id"] for r in replies], [7, "unknown"])
        self.assertEqual(replies[0]["result"], {})
        self.assertEqual(replies[1]["error"]["code"], -32601)

    def test_bad_json_and_invalid_request_do_not_break_the_next_call(self):
        replies = self.exchange(["{", "[]", request(3, "ping")])
        self.assertEqual(len(replies), 3)
        self.assertEqual([r["error"]["code"] for r in replies[:2]], [-32700, -32600])
        self.assertEqual(replies[2]["result"], {})

    def test_tool_calls_require_initialization(self):
        reply, = self.exchange([request(1, "tools/call", {
            "name": "echo", "arguments": {"text": "not yet"},
        })], initialize=False)
        self.assertEqual(reply["error"]["code"], -32002)


if __name__ == "__main__":
    unittest.main(verbosity=2)
