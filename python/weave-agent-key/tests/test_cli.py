import base64
import hashlib
import json
import os
import subprocess
import sys
import tempfile
import threading
import unittest
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import parse_qs

import support

ACCOUNT_ID = '0f8fad5b-d9cb-469f-a165-70867728950e'
ISSUED_TOKEN = 'T' * 43
LIMITED_ACCOUNT = '7c9e6679-7425-40de-944b-e07fc1f90ae7'


def run(*args):
    env = {**os.environ, 'PYTHONPATH': str(support.PACKAGE_ROOT), 'PYTHONIOENCODING': 'utf-8'}
    done = subprocess.run([sys.executable, '-m', 'weave_agent_key', *args], capture_output=True, text=True, encoding='utf-8', env=env, timeout=30)
    return done.returncode, done.stdout, done.stderr


class Relay(BaseHTTPRequestHandler):
    """A minimal stand-in for the relay: enough shape for the CLI, no verification (the vectors test verifies with the mailbox code)."""

    def log_message(self, *args):
        pass

    def reply(self, status, value, headers=None):
        body = json.dumps(value).encode()
        self.send_response(status)
        for name, header in {'Content-Type': 'application/json', 'Content-Length': str(len(body)), **(headers or {})}.items():
            self.send_header(name, header)
        self.end_headers()
        self.wfile.write(body)

    def body(self):
        return self.rfile.read(int(self.headers.get('Content-Length') or 0)).decode()

    def do_POST(self):
        body = self.body()
        if self.path == '/v1/accounts':
            header = json.loads(base64.urlsafe_b64decode(self.headers['DPoP'].split('.')[0] + '=='))
            jwk = header['jwk']
            canonical = json.dumps({'crv': jwk['crv'], 'kty': jwk['kty'], 'x': jwk['x']}, separators=(',', ':')).encode()
            kid = base64.urlsafe_b64encode(hashlib.sha256(canonical).digest()).rstrip(b'=').decode()
            return self.reply(200, {'accountId': ACCOUNT_ID, 'displayName': None, 'description': None, 'kid': kid})
        if self.path == '/v1/token' and parse_qs(body).get('client_id') == [LIMITED_ACCOUNT]:
            return self.reply(429, {'error': 'temporarily_unavailable'}, {'Retry-After': '3600'})
        if self.path == '/v1/token':
            return self.reply(200, {'access_token': ISSUED_TOKEN, 'token_type': 'DPoP', 'expires_in': 900})
        self.reply(401, {'status': 401, 'code': 'unauthenticated'})

    def do_GET(self):
        if self.path == '/v1/keys' and self.headers.get('Authorization') == 'DPoP ' + ISSUED_TOKEN:
            return self.reply(200, {'items': [{'kid': 'k', 'label': None, 'createdAt': '2026-11-01T00:00:00Z', 'lastUsedAt': None, 'coolingUntil': None}]})
        self.reply(401, {'status': 401, 'code': 'unauthenticated'})


class CliTests(unittest.TestCase):
    def setUp(self):
        self._dir = tempfile.TemporaryDirectory(prefix='weave-agent-key-cli-')
        self.keyfile = str(Path(self._dir.name) / 'agent.jwk')
        self.server = ThreadingHTTPServer(('127.0.0.1', 0), Relay)
        self.origin = f'http://127.0.0.1:{self.server.server_address[1]}'
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        self.thread.start()

    def tearDown(self):
        self.server.shutdown()
        self.server.server_close()
        self.thread.join()
        self._dir.cleanup()

    def test_cli_token_does_not_print_token_without_flag(self):
        self.assertEqual(run('init', self.keyfile)[0], 0)
        code, stdout, stderr = run('token', self.origin, self.keyfile, ACCOUNT_ID)
        self.assertEqual(code, 0, stderr)
        self.assertNotIn(ISSUED_TOKEN, stdout + stderr)
        self.assertEqual(list(json.loads(stdout)), ['expiresAt'])
        code, stdout, stderr = run('token', self.origin, self.keyfile, ACCOUNT_ID, '--print-token')
        self.assertEqual(code, 0, stderr)
        self.assertEqual(json.loads(stdout)['token'], ISSUED_TOKEN)

    def test_cli_never_prints_private_key(self):
        runs = [run('init', self.keyfile)]
        d = json.loads(Path(self.keyfile).read_text('utf-8'))['d']
        runs.append(run('init', self.keyfile))
        runs.append(run('register', self.origin, self.keyfile, '--name', 'helper test'))
        runs.append(run('token', self.origin, self.keyfile, ACCOUNT_ID))
        runs.append(run('token', self.origin, self.keyfile, ACCOUNT_ID, '--print-token'))
        runs.append(run('whoami', self.origin, self.keyfile, ACCOUNT_ID))
        runs.append(run('whoami', self.origin, self.keyfile, ACCOUNT_ID, '--unknown'))
        runs.append(run('register', 'ftp://example.test', self.keyfile))
        self.assertEqual([code for code, _, _ in runs], [0, 1, 0, 0, 0, 0, 2, 1], runs)
        self.assertEqual(list(json.loads(runs[0][1])), ['kid'])
        self.assertEqual(json.loads(runs[2][1])['accountId'], ACCOUNT_ID)
        self.assertEqual(json.loads(runs[5][1])['accountId'], ACCOUNT_ID)
        for _, stdout, stderr in runs:
            self.assertNotIn(d, stdout + stderr)

    def test_cli_token_rate_limited_beyond_one_minute_reports_wait_without_waiting(self):
        self.assertEqual(run('init', self.keyfile)[0], 0)
        code, stdout, stderr = run('token', self.origin, self.keyfile, LIMITED_ACCOUNT)
        self.assertEqual(code, 1)
        self.assertEqual(stdout, '')
        self.assertEqual(stderr, 'weave-agent-key: Token request answered HTTP 429 temporarily_unavailable; retry after 3600 s\n')

    def test_cli_options_apply_only_to_their_command(self):
        self.assertEqual(run('init', self.keyfile)[0], 0)
        for args in (('init', self.keyfile + '2', '--name', 'x'), ('whoami', self.origin, self.keyfile, ACCOUNT_ID, '--print-token'),
                     ('register', self.origin), (), ('token', self.origin, self.keyfile, ACCOUNT_ID, '--print-token=yes')):
            with self.subTest(args):
                code, stdout, stderr = run(*args)
                self.assertEqual((code, stdout), (2, ''))
                self.assertIn('Usage:', stderr)
        self.assertFalse(Path(self.keyfile + '2').exists())


if __name__ == '__main__':
    unittest.main()
