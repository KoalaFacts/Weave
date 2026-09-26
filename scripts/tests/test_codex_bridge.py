"""Adapter contract checks; the HTTP peer is NOT a live model or human approval."""
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import unittest

ROOT = Path(__file__).resolve().parents[2]
BRIDGE = ROOT / 'examples/governed-tools/codex_bridge.py'
ID = '89f0b55c9a314192a40751bb85da3c43'


class BridgeTests(unittest.TestCase):
    def setUp(self):
        self.assertTrue(BRIDGE.exists(), 'The approved narrow Agent bridge is not implemented.')
        spec = importlib.util.spec_from_file_location('codex_bridge', BRIDGE)
        self.module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(self.module)
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.calls = []
        self.replies = []
        test = self

        class Peer(BaseHTTPRequestHandler):
            def handle_request(self):
                size = int(self.headers.get('Content-Length', '0'))
                body = self.rfile.read(size)
                test.calls.append((self.command, self.path, dict(self.headers), body))
                status, payload = test.replies.pop(0) if test.replies else (500, {})
                data = json.dumps(payload).encode()
                self.send_response(status)
                self.send_header('Content-Type', 'application/json')
                self.send_header('Content-Length', str(len(data)))
                self.end_headers()
                self.wfile.write(data)
            do_GET = do_POST = handle_request
            def log_message(self, *args):
                pass

        self.server = ThreadingHTTPServer(('127.0.0.1', 0), Peer)
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        self.thread.start()
        self.addCleanup(self.stop_server)
        self.url = f'http://127.0.0.1:{self.server.server_port}'
        self.agent = self.module.Bridge(self.url, 'pilot', 'files', Path(self.temp.name), 'test_capability')

    def stop_server(self):
        self.server.shutdown()
        self.server.server_close()
        self.thread.join(timeout=2)

    def pending(self):
        self.replies.extend([(404, {}), (404, {}), (202, {'errorCode': 'approval-pending'})])
        return self.agent.call_tool('submit_write', {'invocation_id': ID, 'path': 'summary.md', 'content': '汉🙂\n'})

    def test_pending_is_not_success_and_receipt_retains_uuid_hash_not_body(self):
        result = self.pending()
        self.assertEqual(result['http_status'], 202)
        saved = json.loads((Path(self.temp.name) / (ID + '.json')).read_text())
        self.assertEqual(set(saved), {'invocationId', 'inputSha256'})
        self.assertEqual(result['invocation_id'], ID)
        self.assertEqual(saved['invocationId'], ID)
        self.assertEqual(saved['inputSha256'], hashlib.sha256(self.calls[-1][3]).hexdigest())
        self.assertEqual(json.loads(self.calls[-1][3])['rawInput'], '汉🙂\n')
        self.assertEqual([call[0] for call in self.calls], ['GET', 'GET', 'POST'])
        for call in self.calls:
            self.assertNotIn('X-Weave-Operator-Key', call[2])

    def test_duplicate_submit_is_query_only_and_changed_body_cannot_reuse_uuid(self):
        first = self.pending()
        self.replies.extend([(404, {}), (200, {'approvalState': 'Pending'})])
        again = self.agent.call_tool('submit_write', {'invocation_id': ID, 'path': 'summary.md', 'content': '汉🙂\n'})
        self.assertEqual(again['invocation_id'], first['invocation_id'])
        self.assertEqual(sum(c[0] == 'POST' for c in self.calls), 1)
        with self.assertRaises(self.module.BridgeError):
            self.agent.call_tool('submit_write', {'invocation_id': ID, 'path': 'summary.md', 'content': 'changed'})
        self.assertEqual(len(self.calls), 5)

    def test_resume_after_bridge_restart_and_local_receipt_loss_uses_uuid_with_current_token(self):
        self.pending()
        (Path(self.temp.name) / (ID + '.json')).unlink()
        replacement = self.module.Bridge(self.url, 'pilot', 'files', Path(self.temp.name), 'fresh_capability')
        self.replies.extend([(404, {}), (200, {'approvalState': 'Approved'}), (200, {'success': True, 'outcomeRecorded': True})])
        result = replacement.call_tool('resume_write', {'invocation_id': ID})
        self.assertEqual(result['http_status'], 200)
        self.assertEqual(self.calls[-1][1], '/api/workspaces/pilot/tools/files/invocations/' + ID + '/resume')
        self.assertEqual(self.calls[-1][3], b'')
        self.assertEqual(self.calls[-1][2]['X-Weave-Capability'], 'fresh_capability')
        self.assertFalse((Path(self.temp.name) / (ID + '.json')).exists())

    def test_pending_rejected_expired_unknown_and_recorded_outcomes_never_trigger_resume_post(self):
        self.pending()
        for state in ('Pending', 'Rejected', 'Expired', 'Cancelled'):
            self.replies.extend([(404, {}), (200, {'approvalState': state})])
            self.agent.call_tool('resume_write', {'invocation_id': ID})
        for outcome in ('OutcomeUnknown', 'Succeeded', 'Failed'):
            self.replies.append((200, {'outcome': outcome}))
            self.agent.call_tool('resume_write', {'invocation_id': ID})
        self.replies.append((401, {}))
        self.agent.call_tool('resume_write', {'invocation_id': ID})
        self.assertEqual(sum(c[0] == 'POST' for c in self.calls), 1)

    def test_only_declared_tools_and_arguments_can_be_used(self):
        for name in ('approve', 'issue', 'connect', 'shell', 'http'):
            with self.assertRaises(self.module.BridgeError):
                self.agent.call_tool(name, {})
        with self.assertRaises(self.module.BridgeError):
            self.agent.call_tool('submit_write', {'invocation_id': ID, 'path': 'a', 'content': 'x', 'grants': ['*']})
        for invalid in ('../x', 'summary-v1', '0' * 32):
            with self.assertRaises(self.module.BridgeError):
                self.agent.call_tool('submit_write', {'invocation_id': invalid, 'path': 'a', 'content': 'x'})
        with self.assertRaises(self.module.BridgeError):
            self.agent.call_tool('get_status', {'request_key': 'summary-v1'})
        self.assertEqual(self.calls, [])

    def test_state_cannot_be_reused_for_another_target_or_workspace(self):
        with self.assertRaises(self.module.BridgeError):
            self.module.Bridge(self.url, 'different', 'files', Path(self.temp.name), 'test_capability')

    def test_read_response_comes_from_peer_not_a_canned_document(self):
        self.replies.append((200, {'success': True, 'output': 'unique-input-汉'}))
        result = self.agent.call_tool('read_document', {'path': 'meeting.txt'})
        self.assertEqual(result['result']['output'], 'unique-input-汉')
        self.assertEqual(json.loads(self.calls[0][3])['method'], 'read_file')

    def test_real_stdio_process_discovers_only_four_tools_and_uuid_contract(self):
        env = {k: os.environ[k] for k in ('PATH', 'LANG', 'SystemRoot', 'SystemDrive') if k in os.environ}
        env['WEAVE_AGENT_CAPABILITY'] = 'test_capability'
        messages = [{'jsonrpc': '2.0', 'id': 1, 'method': 'initialize', 'params': {'protocolVersion': '2024-11-05'}},
                    {'jsonrpc': '2.0', 'method': 'notifications/initialized'},
                    {'jsonrpc': '2.0', 'id': 2, 'method': 'tools/list'}]
        result = subprocess.run([sys.executable, str(BRIDGE), '--url', self.url, '--workspace', 'pilot',
                                 '--tool', 'files', '--requests', self.temp.name],
                                input='\n'.join(json.dumps(m) for m in messages)+'\n',
                                text=True, capture_output=True, timeout=5, env=env, cwd=self.temp.name)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertFalse((Path(self.temp.name) / '%SystemDrive%').exists())
        replies = [json.loads(line) for line in result.stdout.splitlines()]
        self.assertEqual(len(replies), 2)
        tools = {t['name']: t for t in replies[1]['result']['tools']}
        self.assertEqual(set(tools), {'read_document', 'submit_write', 'get_status', 'resume_write'})
        for name in ('get_status', 'resume_write'):
            self.assertEqual(tools[name]['inputSchema']['required'], ['invocation_id'])
        self.assertNotIn('request_key', tools['submit_write']['inputSchema']['properties'])
        self.assertEqual(self.calls, [])

    def test_failed_initial_status_lookup_never_submits_or_saves_a_write(self):
        for status in (401, 403, 500):
            self.replies.append((status, {}))
            result = self.agent.call_tool('submit_write', {'invocation_id': ID, 'path': 'summary.md', 'content': 'not submitted'})
            self.assertEqual(result['invocation']['http_status'], status)
        self.assertTrue(all(call[0] == 'GET' for call in self.calls))
        self.assertFalse((Path(self.temp.name) / (ID + '.json')).exists())


if __name__ == '__main__':
    unittest.main()
