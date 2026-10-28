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
                reply = test.replies.pop(0) if test.replies else (500, {})
                status, payload = reply[:2]
                headers = reply[2] if len(reply) == 3 else {}
                data = payload if isinstance(payload, bytes) else json.dumps(payload).encode()
                self.send_response(status)
                self.send_header('Content-Type', headers.get('Content-Type', 'application/json'))
                for name, value in headers.items():
                    if name != 'Content-Type':
                        self.send_header(name, value)
                if 'Transfer-Encoding' not in headers:
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
        self.replies.extend([(404, {'errorCode': 'invocation-not-found'}),
                             (404, {'errorCode': 'approval-not-found'}),
                             (202, {'errorCode': 'approval-pending'})])
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
        self.replies.extend([(404, {'errorCode': 'invocation-not-found'}), (200, {'approvalState': 'Pending'})])
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
        self.replies.extend([(404, {'errorCode': 'invocation-not-found'}),
                             (200, {'approvalState': 'Approved'}), (200, {'success': True, 'outcomeRecorded': True})])
        result = replacement.call_tool('resume_write', {'invocation_id': ID})
        self.assertEqual(result['http_status'], 200)
        self.assertEqual(self.calls[-1][1], '/api/workspaces/pilot/tools/files/invocations/' + ID + '/resume')
        self.assertEqual(self.calls[-1][3], b'')
        self.assertEqual(self.calls[-1][2]['X-Weave-Capability'], 'fresh_capability')
        self.assertFalse((Path(self.temp.name) / (ID + '.json')).exists())

    def test_pending_rejected_expired_unknown_and_recorded_outcomes_never_trigger_resume_post(self):
        self.pending()
        for state in ('Pending', 'Rejected', 'Expired', 'Cancelled'):
            self.replies.extend([(404, {'errorCode': 'invocation-not-found'}), (200, {'approvalState': state})])
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

    def test_rejected_write_exposes_safe_code_and_never_reposts_retained_uuid(self):
        self.replies.extend([(404, {'errorCode': 'invocation-not-found'}),
                             (404, {'errorCode': 'approval-not-found'}),
                             (422, {'errorCode': 'outbound-secret-detected',
                                    'error': 'synthetic-private-secret', 'output': 'operator-only-content',
                                    'success': True, 'outcome': 'Succeeded'})])
        args = {'invocation_id': ID, 'path': 'summary.md', 'content': 'proposed content'}
        result = self.agent.call_tool('submit_write', args)
        self.assertEqual(result['http_status'], 422)
        self.assertEqual(result['result'], {'errorCode': 'outbound-secret-detected'})
        self.assertIn('Query the retained UUID', result['next'])
        self.assertNotIn('synthetic-private-secret', json.dumps(result))
        self.assertNotIn('operator-only-content', json.dumps(result))
        self.assertEqual([call[0] for call in self.calls], ['GET', 'GET', 'POST'])
        receipt = json.loads((Path(self.temp.name) / (ID + '.json')).read_text())
        self.assertEqual(receipt['invocationId'], ID)
        self.replies.extend([(404, {'errorCode': 'invocation-not-found'}),
                             (404, {'errorCode': 'approval-not-found'})])
        self.agent.call_tool('submit_write', args)
        self.assertEqual(sum(call[0] == 'POST' for call in self.calls), 1)
        self.assertEqual(json.loads((Path(self.temp.name) / (ID + '.json')).read_text()), receipt)
        with self.assertRaises(self.module.BridgeError):
            self.agent.call_tool('submit_write', {**args, 'content': 'revised content'})

    def test_known_non_success_codes_do_not_expose_other_response_fields(self):
        for status, code in ((400, 'invalid-invocation'), (401, 'invalid-capability'),
                             (403, 'forbidden'), (404, 'invocation-not-found'),
                             (404, 'approval-not-found'), (409, 'outcome-unknown'),
                             (413, 'request-too-large'), (415, 'unsupported-content-type'),
                             (503, 'journal-write-failed')):
            with self.subTest(status=status, code=code):
                self.replies.append((status, {'errorCode': code, 'error': 'private-details',
                                             'output': 'private-output', 'nested': {'secret': 'hidden'}}))
                result = self.agent.send('GET', '/' + ID)
                self.assertEqual(result['http_status'], status)
                self.assertEqual(result['result'], {'errorCode': code})
                self.assertIn('Query the retained UUID', result['next'])
                self.assertNotIn('private-', json.dumps(result))
                self.assertNotIn('hidden', json.dumps(result))

    def test_invalid_error_responses_preserve_http_status_without_exposing_body(self):
        cases = [
            (422, {'errorCode': 'synthetic-private-secret'}),
            (422, {'errorCode': ['outbound-secret-detected']}),
            (422, b'{"errorCode":"outbound-secret-detected","errorCode":"private-value"}'),
            (422, b'{"errorCode":"outbound-secret-detected","extra":NaN}'),
            (422, b'{"errorCode":"outbound-secret-detected",'),
            (422, b'["private-details"]'),
            (422, b'\xff'),
            (422, {'errorCode': 'outbound-secret-detected', 'output': 'x' * self.module.MAX_BYTES}),
            (422, {'errorCode': 'outbound-secret-detected'}, {'Content-Type': 'text/html'}),
            (404, {'errorCode': 'outbound-secret-detected'}),
        ]
        for reply in cases:
            with self.subTest(reply_type=type(reply[1]).__name__, status=reply[0]):
                self.replies.append(reply)
                result = self.agent.send('GET', '/' + ID)
                self.assertEqual(result['http_status'], reply[0])
                self.assertIsNone(result['result'])
                self.assertIn('Query the retained UUID', result['next'])
                self.assertNotIn('private-', json.dumps(result))

    def test_redirect_is_not_followed_or_treated_as_a_diagnostic(self):
        self.replies.append((307, {'errorCode': 'outbound-secret-detected'},
                             {'Location': self.url + '/must-not-follow'}))
        result = self.agent.send('GET', '/' + ID)
        self.assertEqual(result['http_status'], 307)
        self.assertIsNone(result['result'])
        self.assertEqual(len(self.calls), 1)
        self.assertNotIn('/must-not-follow', self.calls[0][1])

    def test_malformed_chunked_error_body_keeps_http_status_and_safe_guidance(self):
        for payload in (b'zz\r\nprivate-proxy-details\r\n', b'20\r\nprivate-proxy-details\r\n'):
            with self.subTest(payload=payload):
                self.replies.append((422, payload, {'Transfer-Encoding': 'chunked'}))
                result = self.agent.send('GET', '/' + ID)
                self.assertEqual(result['http_status'], 422)
                self.assertIsNone(result['result'])
                self.assertIn('Query the retained UUID', result['next'])
                self.assertNotIn('private-proxy-details', json.dumps(result))

    def test_malformed_chunked_error_does_not_exit_stdio_bridge(self):
        self.replies.append((422, b'zz\r\nprivate-proxy-details\r\n', {'Transfer-Encoding': 'chunked'}))
        env = {k: os.environ[k] for k in ('PATH', 'LANG', 'SystemRoot', 'SystemDrive') if k in os.environ}
        env['WEAVE_AGENT_CAPABILITY'] = 'test_capability'
        messages = [{'jsonrpc': '2.0', 'id': 1, 'method': 'initialize'},
                    {'jsonrpc': '2.0', 'method': 'notifications/initialized'},
                    {'jsonrpc': '2.0', 'id': 2, 'method': 'tools/call',
                     'params': {'name': 'read_document', 'arguments': {'path': 'summary.md'}}},
                    {'jsonrpc': '2.0', 'id': 3, 'method': 'ping'}]
        process = subprocess.run([sys.executable, str(BRIDGE), '--url', self.url, '--workspace', 'pilot',
                                  '--tool', 'files', '--requests', self.temp.name],
                                 input='\n'.join(json.dumps(m) for m in messages) + '\n',
                                 text=True, capture_output=True, timeout=5, env=env, cwd=self.temp.name)
        self.assertEqual(process.returncode, 0, process.stderr)
        self.assertEqual(process.stderr, '')
        replies = [json.loads(line) for line in process.stdout.splitlines()]
        self.assertEqual([reply['id'] for reply in replies], [1, 2, 3])
        result = json.loads(replies[1]['result']['content'][0]['text'])
        self.assertEqual(result['http_status'], 422)
        self.assertIsNone(result['result'])
        self.assertIn('Query the retained UUID', result['next'])
        self.assertNotIn('private-proxy-details', process.stdout)
        self.assertEqual(replies[2]['result'], {})
        self.assertEqual(len(self.calls), 1)

    def test_malformed_chunked_success_body_does_not_confirm_success(self):
        self.replies.append((200, b'20\r\nprivate-proxy-details\r\n', {'Transfer-Encoding': 'chunked'}))
        result = self.agent.send('GET', '/' + ID)
        self.assertIsNone(result['http_status'])
        self.assertIsNone(result['result'])
        self.assertIn('Outcome unconfirmed', result['next'])
        self.assertNotIn('private-proxy-details', json.dumps(result))
        self.assertEqual(len(self.calls), 1)

    def test_unconfirmed_invocation_404_does_not_begin_submission(self):
        for payload in ({}, b'not-json', {'errorCode': 'approval-not-found'},
                        {'errorCode': 'unknown-private-code'}):
            with self.subTest(payload=payload):
                self.calls.clear()
                self.replies.append((404, payload))
                self.agent.call_tool('submit_write', {'invocation_id': ID, 'path': 'summary.md', 'content': 'not submitted'})
                self.assertEqual([call[0] for call in self.calls], ['GET'])
                self.assertFalse((Path(self.temp.name) / (ID + '.json')).exists())

    def test_unconfirmed_approval_404_does_not_begin_submission(self):
        for payload in ({}, b'not-json', {'errorCode': 'invocation-not-found'},
                        {'errorCode': 'unknown-private-code'}):
            with self.subTest(payload=payload):
                self.calls.clear()
                self.replies.extend([(404, {'errorCode': 'invocation-not-found'}), (404, payload)])
                self.agent.call_tool('submit_write', {'invocation_id': ID, 'path': 'summary.md', 'content': 'not submitted'})
                self.assertEqual([call[0] for call in self.calls], ['GET', 'GET'])
                self.assertFalse((Path(self.temp.name) / (ID + '.json')).exists())


if __name__ == '__main__':
    unittest.main()
