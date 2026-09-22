#!/usr/bin/env python3
"""Black-box acceptance of the exact pinned action; fixture data never leaves loopback."""
import argparse
import base64
import http.server
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import threading
import urllib.parse
import urllib.request

ROOT = Path(__file__).resolve().parents[2]
PIN = 'a1d282b36b6f3519aa1f3fc636f609c47dddb294'
BASE, HEAD = '1' * 40, '2' * 40
TOKEN = 'fixture-token-not-a-credential'


def outputs(path):
    lines = path.read_text(encoding='utf-8').splitlines()
    result, index = {}, 0
    while index < len(lines):
        line = lines[index]
        index += 1
        if '<<' in line:
            name, delimiter = line.split('<<', 1)
            content = []
            while index < len(lines) and lines[index] != delimiter:
                content.append(lines[index])
                index += 1
            if index == len(lines):
                raise AssertionError('Unterminated action output')
            index += 1
            result[name] = '\n'.join(content)
        elif '=' in line:
            name, value = line.split('=', 1)
            result[name] = value
    return result


def change(license_value):
    return {'change_type': 'added', 'manifest': 'fixture/packages.lock.json', 'ecosystem': 'nuget',
            'name': 'fixture.component', 'version': '1.0.0',
            'package_url': 'pkg:nuget/fixture.component@1.0.0', 'scope': 'runtime',
            'license': license_value, 'source_repository_url': None, 'vulnerabilities': []}


class Peer(http.server.ThreadingHTTPServer):
    daemon_threads = True
    def __init__(self, changes, warning):
        self.changes, self.warning, self.calls, self.unexpected = changes, warning, [], []
        super().__init__(('127.0.0.1', 0), Handler)


class Handler(http.server.BaseHTTPRequestHandler):
    def do_GET(self):
        expected = f'/repos/fixture/repo/dependency-graph/compare/{BASE}...{HEAD}'
        self.server.calls.append(self.path)
        valid = urllib.parse.urlsplit(self.path).path == expected
        if not valid:
            self.server.unexpected.append(self.path)
        body = json.dumps(self.server.changes if valid else {'message': 'Unexpected fixture endpoint'}).encode()
        self.send_response(200 if valid else 404)
        self.send_header('Content-Type', 'application/json')
        self.send_header('Content-Length', str(len(body)))
        if self.server.warning:
            self.send_header('x-github-dependency-graph-snapshot-warnings',
                             base64.b64encode(b'No snapshots were found for the head SHA.').decode())
        self.end_headers()
        self.wfile.write(body)
    def do_POST(self):
        self.server.unexpected.append('POST ' + self.path)
        self.send_error(405)
    def log_message(self, *_):
        pass


class LoopbackOpener:
    """Exercise the production evidence parser over a real local HTTP response."""
    def __init__(self, port):
        self.port = port
        self.inner = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    def open(self, request, timeout):
        parsed = urllib.parse.urlsplit(request.full_url)
        assert parsed.netloc == 'api.github.com'
        local = urllib.request.Request(f'http://127.0.0.1:{self.port}{parsed.path}?{parsed.query}',
                                       headers={'Authorization': 'Bearer ' + TOKEN})
        return self.inner.open(local, timeout=timeout)


def run_case(action, node, name, changes, warning, expected_blocked):
    with tempfile.TemporaryDirectory(prefix='weave-license-fixture-') as directory:
        directory = Path(directory)
        event = directory / 'event.json'
        event.write_text(json.dumps({'number': 1, 'repository': {'full_name': 'fixture/repo'},
                                     'pull_request': {'number': 1, 'base': {'sha': BASE}, 'head': {'sha': HEAD}}}))
        output, summary = directory / 'outputs', directory / 'summary'
        output.touch()
        summary.touch()
        server = Peer(changes, warning)
        thread = threading.Thread(target=lambda: server.serve_forever(poll_interval=0.01), daemon=True)
        thread.start()
        try:
            # Allowlist: no inherited CI credentials, artifact tokens, proxies or Node hooks.
            env = {key: os.environ[key] for key in ('PATH', 'SYSTEMROOT', 'LANG') if key in os.environ}
            env.update({'HOME': str(directory), 'GITHUB_WORKSPACE': str(directory),
                        'GITHUB_EVENT_PATH': str(event), 'GITHUB_EVENT_NAME': 'pull_request',
                        'GITHUB_REPOSITORY': 'fixture/repo', 'GITHUB_OUTPUT': str(output),
                        'GITHUB_STEP_SUMMARY': str(summary), 'GITHUB_SERVER_URL': 'https://github.com',
                        'GITHUB_API_URL': f'http://127.0.0.1:{server.server_port}',
                        'INPUT_REPO-TOKEN': TOKEN, 'INPUT_BASE-REF': BASE, 'INPUT_HEAD-REF': HEAD,
                        'INPUT_FAIL-ON-SEVERITY': 'high',
                        'INPUT_DENY-LICENSES': 'GPL-2.0, GPL-3.0, AGPL-3.0',
                        'INPUT_SHOW-OPENSSF-SCORECARD': 'false', 'INPUT_COMMENT-SUMMARY-IN-PR': 'never',
                        'INPUT_RETRY-ON-SNAPSHOT-WARNINGS': 'false'})
            result = subprocess.run([node, str(action / 'dist/index.js')], env=env,
                                    cwd=directory, capture_output=True, timeout=40, check=False)
            action_output = outputs(output)
            # Keep the real existing evidence preflight in the chain.
            spec = importlib.util.spec_from_file_location('evidence', ROOT / 'scripts/check_dependency_review_evidence.py')
            evidence = importlib.util.module_from_spec(spec)
            spec.loader.exec_module(evidence)
            evidence_status = 'available'
            try:
                evidence.inspect_comparison('fixture/repo', BASE, HEAD, TOKEN, LoopbackOpener(server.server_port))
            except evidence.EvidenceUnavailable as error:
                evidence_status = error.code
            guard_exit = None
            guard = ROOT / 'scripts/check_dependency_license_evidence.py'
            if guard.exists() and evidence_status == 'available':
                guard_env = {**env, 'INVALID_LICENSE_CHANGES': action_output.get('invalid-license-changes', '')}
                checked = subprocess.run([sys.executable, str(guard), '--report', str(directory / 'license.json')],
                                         env=guard_env, capture_output=True, timeout=10, check=False)
                guard_exit = checked.returncode
            blocked = result.returncode != 0 or evidence_status != 'available' or guard_exit not in (None, 0)
            assert server.calls, 'Action never reached the controlled API'
            assert not server.unexpected, f'Unexpected API calls: {server.unexpected}'
            assert 'invalid-license-changes' in action_output, result.stdout.decode(errors='replace')[-3000:]
            invalid = json.loads(action_output['invalid-license-changes'])
            record = {'case': name, 'action_exit': result.returncode, 'guard_exit': guard_exit,
                      'evidence': evidence_status, 'blocked': blocked, 'expected_blocked': expected_blocked,
                      'invalid_counts': {key: len(value) for key, value in invalid.items()},
                      'requests': len(server.calls)}
            record['passed'] = blocked == expected_blocked
            return record
        finally:
            server.shutdown()
            server.server_close()
            thread.join(5)
            assert not thread.is_alive()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--action', type=Path, required=True)
    parser.add_argument('--node', default='node')
    parser.add_argument('--report', type=Path, required=True)
    args = parser.parse_args()
    action = args.action.resolve()
    actual = subprocess.check_output(['git', '-C', str(action), 'rev-parse', 'HEAD'], text=True).strip()
    if actual != PIN:
        raise SystemExit('The acceptance action must match the reviewed immutable pin.')
    cases = [
        ('permitted-license', [change('MIT')], False, False),
        ('prohibited-gpl', [change('GPL-3.0')], False, True),
        ('prohibited-agpl', [change('AGPL-3.0')], False, True),
        ('invalid-spdx', [change('not-an-spdx-license')], False, True),
        ('missing-license', [change(None)], False, True),
        ('noassertion-license', [change('NOASSERTION')], False, True),
        ('complete-empty-delta', [], False, False),
        ('missing-snapshot', [], True, True),
    ]
    records = []
    for name, changes, warning, blocked in cases:
        try:
            record = run_case(action, args.node, name, changes, warning, blocked)
        except Exception as error:
            record = {'case': name, 'passed': False, 'setup_error': str(error)[:3000]}
        records.append(record)
        print(json.dumps(record), flush=True)
    args.report.parent.mkdir(parents=True, exist_ok=True)
    args.report.write_text(json.dumps({'action_sha': actual, 'cases': records}, indent=2) + '\n')
    return 0 if all(item['passed'] for item in records) else 1


if __name__ == '__main__':
    sys.exit(main())
