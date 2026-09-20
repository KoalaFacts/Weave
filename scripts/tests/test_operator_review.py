import copy
import importlib.util
import io
import json
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location("weave_operator_review", ROOT / "examples/governed-tools/review.py")
review = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(review)

ID = "14bd2b92c8a34fdd8c79cb62eab32a13"
DIGEST = "approval-v1:" + "A" * 64


def invocation():
    return {"invocationId": ID, "toolName": "files", "method": "write_file",
            "parameters": {"path": "note.txt"}, "rawInput": "line 1\n\x1b[2J\u202e\\n\t第二行"}


class OperatorReviewTests(unittest.TestCase):
    def exercise(self, answer, alter_preview=None, mutate_request=False):
        request = invocation()
        original = copy.deepcopy(request)
        output = io.StringIO()
        calls = []
        preview = {"invocationId": ID, "workspaceId": "workspace", "subject": "agent",
                   "toolName": "files", "operation": "write_file", "targetDescription": "/fixed/tools",
                   "parameters": copy.deepcopy(request["parameters"]), "rawInput": request["rawInput"],
                   "planDigest": DIGEST, "expiresAt": "2026-09-20T05:00:00Z"}
        if alter_preview:
            preview.update(alter_preview)

        def post_json(url, payload):
            calls.append((url, json.loads(payload)))
            if len(calls) == 1:
                if mutate_request:
                    request["parameters"]["path"] = "mutated.txt"
                    request["rawInput"] = "mutated"
                return preview
            self.assertIn("\\u001b", output.getvalue())
            return {"invocationId": ID, "approvalState": "Approved" if answer.startswith("approve ") else "Rejected"}

        result = review.run_review("https://weave.example", "workspace", request, post_json,
                                   lambda prompt: answer, output)
        return result, calls, output.getvalue(), original

    def test_approve_sends_the_exact_displayed_digest_and_retained_body(self):
        result, calls, text, original = self.exercise("approve " + DIGEST)
        self.assertTrue(result)
        self.assertEqual(len(calls), 2)
        self.assertTrue(calls[0][0].endswith("/approval/review"))
        self.assertTrue(calls[1][0].endswith("/approval/decision"))
        self.assertEqual(calls[1][1], {"invocation": original, "planDigest": DIGEST, "decision": "approve"})
        self.assertIn(DIGEST, text)
        self.assertIn("\\u202e", text)
        self.assertNotIn("\x1b", text)
        self.assertNotIn("\u202e", text)

    def test_reject_is_an_explicit_separate_decision(self):
        result, calls, _, _ = self.exercise("reject " + DIGEST)
        self.assertTrue(result)
        self.assertEqual(calls[-1][1]["decision"], "reject")

    def test_default_wrong_digest_and_generic_yes_do_not_send_decisions(self):
        for answer in ("", "yes", "approve", "approve approval-v1:" + "B" * 64):
            with self.subTest(answer=answer):
                result, calls, text, _ = self.exercise(answer)
                self.assertFalse(result)
                self.assertEqual(len(calls), 1)
                self.assertIn(DIGEST, text)

    def test_caller_mutation_does_not_change_the_confirmed_request(self):
        result, calls, _, original = self.exercise("approve " + DIGEST, mutate_request=True)
        self.assertTrue(result)
        self.assertEqual(calls[-1][1]["invocation"], original)

    def test_mismatched_preview_is_not_displayed_as_verified(self):
        for change in ({"invocationId": "0" * 32}, {"workspaceId": "other"},
                       {"toolName": "other"}, {"rawInput": "different"},
                       {"parameters": {"path": "other"}}, {"planDigest": "\x1b[2J"}):
            with self.subTest(change=change):
                with self.assertRaises(ValueError):
                    self.exercise("approve " + DIGEST, change)

    def test_transport_failure_never_automatically_retries(self):
        calls = []
        def broken(url, payload):
            calls.append(url)
            raise OSError("connection lost")
        with self.assertRaises(OSError):
            review.run_review("https://weave.example", "workspace", invocation(), broken,
                              lambda _: "approve " + DIGEST, io.StringIO())
        self.assertEqual(len(calls), 1)

    def test_insecure_origins_and_route_injection_are_rejected_before_transport(self):
        for base, workspace in (("http://remote.example", "workspace"),
                                ("https://user:secret@weave.example", "workspace"),
                                ("https://weave.example?next=other", "workspace"),
                                ("https://weave.example", "../admin")):
            with self.subTest(base=base, workspace=workspace):
                with self.assertRaises(ValueError):
                    review.run_review(base, workspace, invocation(),
                                      lambda *_: self.fail("transport must not run"),
                                      lambda _: "", io.StringIO())


    def test_duplicate_json_keys_are_rejected(self):
        with self.assertRaises(ValueError):
            json.loads('{"decision":"reject","decision":"approve"}', object_pairs_hook=review._unique_object)

    def test_redirect_does_not_forward_credentials_and_environment_proxy_is_ignored(self):
        import http.server
        import os
        import threading
        from unittest.mock import patch
        calls = []
        class Handler(http.server.BaseHTTPRequestHandler):
            def do_POST(self):
                calls.append((self.command, self.path, self.headers.get("X-Weave-Capability")))
                self.rfile.read(int(self.headers.get("Content-Length", "0")))
                self.send_response(302)
                self.send_header("Location", "/must-not-receive-credential")
                self.end_headers()
            def do_GET(self):
                calls.append((self.command, self.path, self.headers.get("X-Weave-Capability")))
                self.send_response(200)
                self.end_headers()
            def log_message(self, *_):
                pass
        server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        worker = threading.Thread(target=lambda: server.serve_forever(poll_interval=0.01), daemon=True)
        worker.start()
        try:
            with patch.dict(os.environ, {"http_proxy": "http://127.0.0.1:1", "no_proxy": ""}):
                transport = review.OperatorTransport("synthetic-envelope")
                with self.assertRaisesRegex(ValueError, "HTTP 302"):
                    transport.post_json(f"http://127.0.0.1:{server.server_port}/review", b"{}")
            self.assertEqual(calls, [("POST", "/review", "synthetic-envelope")])
        finally:
            server.shutdown()
            server.server_close()
            worker.join(2)
        self.assertFalse(worker.is_alive())


if __name__ == "__main__":
    unittest.main()
