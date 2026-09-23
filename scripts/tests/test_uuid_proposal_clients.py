"""Real terminal HTTP plus adapter contract checks, not live-model/human evidence."""
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
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
ID = '89f0b55c9a314192a40751bb85da3c43'
DIGEST = 'approval-v1:' + 'A' * 64
SPEC = importlib.util.spec_from_file_location('uuid_bridge', ROOT / 'examples/governed-tools/codex_bridge.py')
bridge = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(bridge)


class UuidProposalClientTests(unittest.TestCase):
    def test_mcp_status_needs_only_uuid_and_new_empty_client_directory(self):
        with tempfile.TemporaryDirectory() as directory:
            agent = bridge.Bridge('http://127.0.0.1:9401', 'pilot', 'files', Path(directory), 'synthetic')
            with patch.object(agent, 'send', side_effect=[{'http_status': 404, 'result': None},
                              {'http_status': 200, 'result': {'approvalState': 'Pending'}}]) as calls:
                state = agent.call_tool('get_status', {'invocation_id': ID})
            self.assertEqual(state['invocation_id'], ID)
            self.assertEqual(state['approval']['result']['approvalState'], 'Pending')
            self.assertEqual([c.args for c in calls.call_args_list], [('GET', '/' + ID), ('GET', '/' + ID + '/approval')])
            self.assertFalse((Path(directory) / (ID + '.json')).exists())

    def test_mcp_resume_posts_uuid_without_body_after_explicit_approved_query(self):
        with tempfile.TemporaryDirectory() as directory:
            agent = bridge.Bridge('http://127.0.0.1:9401', 'pilot', 'files', Path(directory), 'fresh_capability')
            with patch.object(agent, 'send', side_effect=[{'http_status': 404, 'result': None},
                              {'http_status': 200, 'result': {'approvalState': 'Approved'}},
                              {'http_status': 200, 'result': {'success': True}}]) as calls:
                result = agent.call_tool('resume_write', {'invocation_id': ID})
            self.assertEqual(result['http_status'], 200)
            self.assertEqual(calls.call_args_list[-1].args, ('POST', '/' + ID + '/resume'))
            self.assertEqual(calls.call_args_list[-1].kwargs, {})

    def test_terminal_uuid_mode_approves_verified_server_body_without_request_file(self):
        self.terminal('approve')

    def test_terminal_uuid_mode_rejects_verified_server_body_without_request_file(self):
        self.terminal('reject')

    def test_terminal_uuid_mode_does_not_decide_when_confirmation_is_missing(self):
        self.terminal('leave')

    def terminal(self, decision):
        observed = []
        preview = {'invocationId': ID, 'workspaceId': 'pilot', 'toolName': 'files',
                   'subject': 'agent', 'operation': 'write_file', 'targetDescription': 'controlled test target',
                   'parameters': {'path': 'summary.md'}, 'rawInput': 'unique-server-proposal-汉🙂',
                   'planDigest': DIGEST, 'expiresAt': '2030-01-01T00:00:00Z'}

        class Peer(BaseHTTPRequestHandler):
            def do_GET(self):
                observed.append(('GET', self.path, b''))
                self.respond(preview)

            def do_POST(self):
                data = self.rfile.read(int(self.headers.get('Content-Length', '0')))
                observed.append(('POST', self.path, data))
                self.respond({'invocationId': ID, 'approvalState': 'Approved' if decision == 'approve' else 'Rejected'})

            def respond(self, value):
                data = json.dumps(value).encode()
                self.send_response(200)
                self.send_header('Content-Type', 'application/json')
                self.send_header('Content-Length', str(len(data)))
                self.end_headers()
                self.wfile.write(data)

            def log_message(self, *args):
                pass

        with tempfile.TemporaryDirectory() as directory:
            server = ThreadingHTTPServer(('127.0.0.1', 0), Peer)
            worker = threading.Thread(target=server.serve_forever, daemon=True)
            worker.start()
            env = {k: os.environ[k] for k in ('PATH', 'LANG', 'LC_ALL', 'SystemRoot') if k in os.environ}
            env['WEAVE_REVIEW_CAPABILITY'] = 'synthetic_reviewer'
            env['WEAVE_OPERATOR_KEY'] = 'synthetic-operator-key-for-test-only-123456789'
            try:
                result = subprocess.run([sys.executable, str(ROOT / 'examples/governed-tools/review.py'),
                    '--url', f'http://127.0.0.1:{server.server_port}', '--workspace', 'pilot',
                    '--tool', 'files', '--invocation-id', ID],
                    cwd=directory, env=env, input=decision + ' ' + DIGEST + '\n',
                    text=True, capture_output=True, timeout=10)
            finally:
                server.shutdown()
                server.server_close()
                worker.join(timeout=2)
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertIn('unique-server-proposal-', result.stdout)
            route = '/api/workspaces/pilot/tools/files/invocations/' + ID
            self.assertEqual(observed[0], ('GET', route + '/approval/review', b''))
            if decision == 'leave':
                self.assertEqual(len(observed), 1)
                self.assertIn('No decision sent.', result.stdout)
            else:
                self.assertEqual(len(observed), 2)
                self.assertEqual(observed[1][1], route + '/decision')
                self.assertEqual(json.loads(observed[1][2]), {'decision': decision, 'planDigest': DIGEST})
            self.assertEqual(list(Path(directory).iterdir()), [])
            self.assertNotIn('synthetic_reviewer', result.stdout + result.stderr)
            self.assertNotIn('synthetic-operator-key', result.stdout + result.stderr)


if __name__ == '__main__':
    unittest.main()
