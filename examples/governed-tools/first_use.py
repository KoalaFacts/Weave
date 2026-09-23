#!/usr/bin/env python3
"""Exercise the documented single-Host public HTTP path in a disposable directory.

No in-process fixtures, direct token minting or database edits. Summary wording is
ChatGPT-authored test content, not a live model call. Approval is a scripted demo
through the existing reviewer CLI, not evidence of independent human approval.
"""
import argparse
import codecs
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import secrets
import shutil
import subprocess
import sys
import tempfile
import time
from urllib import error, request
import uuid

SOURCE = """Project meeting — synthetic acceptance data
Date: 2026-09-23
Decision: Run a single-Host Weave pilot; do not start a multi-Host rollout.
Action: Alex prepares two non-sensitive sample documents by 2026-09-25.
Action: Sam reviews every proposed file write before it is executed.
Constraint: Agent access is limited to the designated test directory.
Constraint: Retain invocation IDs and check outcomes before retrying.
Open question: Confirm the pilot users after the first walkthrough.
"""
INTRO = "Not written. Awaiting an approved request.\n"
BASE = 'http://127.0.0.1:9401'
ROUTE = '/api/workspaces/first-use/tools/files/invocations'


class AcceptanceFailure(RuntimeError):
    pass


class NoRedirect(request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


def check(condition, message):
    if not condition:
        raise AcceptanceFailure(message)


def sha(text):
    return hashlib.sha256(text.encode('utf-8')).hexdigest()


def organize(source):
    """Deterministic formatting of the actual HTTP-read document; no model claim."""
    groups = [('Decision', 'Decision'), ('Action', 'Actions'),
              ('Constraint', 'Constraints'), ('Open question', 'Open question')]
    parts = ['# Meeting summary — isolated Weave walkthrough', '', 'Date: 2026-09-23', '']
    for prefix, heading in groups:
        lines = [line.split(': ', 1)[1] for line in source.splitlines() if line.startswith(prefix + ': ')]
        check(bool(lines), 'The input document is missing an expected section.')
        parts.extend(['## ' + heading, '', *('- ' + line for line in lines), ''])
    return '\n'.join(parts)


class Walkthrough:
    def __init__(self, repo, dll, evidence, root):
        self.repo, self.dll, self.evidence, self.root = repo, dll, evidence, root
        self.process = None
        self.log_handle = None
        self.logs = []
        self.private_values = []
        self.operator = secrets.token_urlsafe(48)
        self.signing = secrets.token_urlsafe(48)
        self.private_values.extend([self.operator, self.signing])
        self.http = request.build_opener(request.ProxyHandler({}), NoRedirect())
        self.report = {'status': 'running', 'started_at': datetime.now(timezone.utc).isoformat(),
                       'source_commit': subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=repo, text=True).strip(),
                       'product_baseline': '3ded2a1162bfb14f314f39cb04a574e85edda469',
                       'scope': 'Fresh standalone Host, loopback HTTP and temporary test data.',
                       'limits': ['Scripted external-Agent client; no live LLM call.',
                                  'Scripted reviewer CLI confirmation; not independent human approval.',
                                  'Loopback HTTP only; no remote TLS/deployment validation.',
                                  'Normal restart, not a new crash/power-loss experiment.'],
                       'steps': []}
        for name in ('tools', 'state', 'home'):
            (root / name).mkdir()
        self.target = root / 'tools' / 'summary.md'
        self.target.write_text(INTRO, encoding='utf-8')
        (root / 'tools' / 'meeting.txt').write_text(SOURCE, encoding='utf-8')
        profile = lambda subject, grants, lifetime='00:05:00': {
            'WorkspaceId': 'first-use', 'IssuedTo': subject, 'Lifetime': lifetime, 'Grants': grants}
        self.config = {
            'Logging': {'LogLevel': {'Default': 'Warning', 'Microsoft.AspNetCore': 'Warning', 'Orleans': 'Warning'}},
            'CapabilityTokens': {'RevocationDirectory': str(root / 'state' / 'revocations'), 'RequireExistingStorage': False},
            'Weave': {'LocalMode': True, 'Auth': {'Mode': 'none'},
                      'Invocations': {'DatabasePath': str(root / 'state' / 'invocations.db'), 'RequireExistingStorage': False,
                                      'ApprovalRequiredGrants': ['tool:files:invoke:write_file'],
                                      'Http': {'Enabled': True, 'AgentOnly': False, 'DecisionsEnabled': True}},
                      'Operator': {'Enabled': True,
                                   'Tools': {'files': {'WorkspaceId': 'first-use', 'Tool': {
                                       'Name': 'files', 'Type': 'FileSystem', 'FileSystem': {'Root': str(root / 'tools')}}}},
                                   'Credentials': {
                                       'reader': profile('agent', ['tool:files:invoke:read_file', 'invocation:read']),
                                       'writer': profile('agent', ['tool:files:invoke:write_file', 'invocation:read']),
                                       'reviewer': profile('reviewer', ['invocation:read', 'approval:decide', 'tool:files:approve:write_file']),
                                       'expiring': profile('agent', ['tool:files:invoke:read_file'], '00:00:01')}}}}

    def record(self, name, **fields):
        self.report['steps'].append({'step': name, 'passed': True, **fields})
        print('PASS ' + name, flush=True)

    def call(self, method, path, expected, body=None, capability=None, operator=False, secret_response=False):
        check(path.startswith('/api/'), 'Unexpected endpoint in this walkthrough.')
        headers = {'Accept': 'application/json'}
        if operator:
            headers['X-Weave-Operator-Key'] = self.operator
        if capability is not None:
            headers['X-Weave-Capability'] = capability
        data = None if body is None else json.dumps(body, ensure_ascii=False).encode('utf-8')
        if data is not None:
            headers['Content-Type'] = 'application/json'
        req = request.Request(BASE + path, data=data, headers=headers, method=method)
        try:
            response = self.http.open(req, timeout=15)
        except error.HTTPError as failure:
            response = failure
        with response:
            status = response.code
            payload = response.read(1_048_577)
            no_store = 'no-store' in response.headers.get('Cache-Control', '')
        check(len(payload) <= 1_048_576, 'Response exceeded the walkthrough limit.')
        check(status == expected, f'{method} {path}: expected HTTP {expected}, got {status}.')
        if secret_response:
            check(no_store, 'Issued credential was not marked no-store.')
            token = payload.decode('ascii')
            check(0 < len(token) <= 16384 and all(c.isalnum() or c in '_-' for c in token), 'Invalid credential envelope.')
            self.private_values.append(token)
            return token
        return json.loads(payload) if payload else None

    def start(self, recovery=False):
        self.config['CapabilityTokens']['RequireExistingStorage'] = recovery
        self.config['Weave']['Invocations']['RequireExistingStorage'] = recovery
        (self.root / 'appsettings.json').write_text(json.dumps(self.config, indent=2), encoding='utf-8')
        env = {k: os.environ[k] for k in ('PATH', 'DOTNET_ROOT', 'LD_LIBRARY_PATH', 'LANG', 'LC_ALL', 'TMPDIR') if k in os.environ}
        env.update({'HOME': str(self.root / 'home'), 'DOTNET_ENVIRONMENT': 'Production',
                    'ASPNETCORE_ENVIRONMENT': 'Production', 'DOTNET_NOLOGO': 'true',
                    'DOTNET_CLI_TELEMETRY_OPTOUT': 'true', 'CapabilityTokens__SigningKey': self.signing,
                    'Weave__Operator__Key': self.operator})
        log = self.root / ('host-restart.log' if recovery else 'host-first.log')
        self.logs.append(log)
        self.log_handle = log.open('wb')
        self.process = subprocess.Popen([shutil.which('dotnet'), str(self.dll), '--urls', BASE],
                                        cwd=self.root, env=env, stdin=subprocess.DEVNULL,
                                        stdout=self.log_handle, stderr=subprocess.STDOUT)
        until = time.monotonic() + 60
        while time.monotonic() < until:
            check(self.process.poll() is None, 'Standalone Host exited before readiness; see sanitized log.')
            try:
                self.call('GET', '/api/workspaces/', 200, operator=True)
                self.record('restart ready' if recovery else 'fresh Host ready', pid=self.process.pid,
                            required_existing_storage=recovery)
                return
            except (error.URLError, TimeoutError):
                time.sleep(.25)  # Read-only readiness probe only; operations never auto-retry.
        raise AcceptanceFailure('Host readiness deadline exceeded.')

    def stop(self):
        if self.process is None:
            return
        process, self.process = self.process, None
        if process.poll() is None:
            process.terminate()
        try:
            code = process.wait(timeout=20)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait(timeout=5)
            raise AcceptanceFailure('Host did not stop gracefully; forced cleanup is not a successful restart.') from None
        finally:
            if self.log_handle is not None:
                self.log_handle.close()
                self.log_handle = None
        check(code == 0, f'Host shutdown exit code {code}; normal shutdown was required.')
        self.record('Host stopped normally', pid=process.pid, exit_code=code)

    def issue(self, profile):
        token = self.call('POST', '/api/operator/credentials/' + profile + '/issue', 200,
                          operator=True, secret_response=True)
        self.record('issue ' + profile, status=200, no_store=True)
        return token

    @staticmethod
    def invocation(method, path, content=None):
        value = {'invocationId': uuid.uuid4().hex, 'toolName': 'files', 'method': method, 'parameters': {'path': path}}
        if content is not None:
            value['rawInput'] = content
        return value

    def run(self):
        self.start()
        self.call('POST', '/api/operator/credentials/writer/issue', 401)
        self.record('anonymous credential issuance denied', status=401)
        self.call('POST', '/api/operator/tools/files/connect', 204, operator=True)
        self.record('connect configured file tool', status=204)
        reader, writer, reviewer = (self.issue(name) for name in ('reader', 'writer', 'reviewer'))
        read = self.invocation('read_file', 'meeting.txt')
        result = self.call('POST', ROUTE, 200, read, reader)
        check(result.get('output') == SOURCE, 'Source returned by public API differs from the test document.')
        self.record('Agent reads actual document through HTTP', status=200, sha256=sha(result['output']))
        summary = organize(result['output'])
        self.report['source_sha256'], self.report['summary_sha256'] = sha(SOURCE), sha(summary)
        (self.evidence / 'source.txt').write_text(SOURCE, encoding='utf-8')
        (self.evidence / 'summary.md').write_text(summary, encoding='utf-8')
        self.record('organize returned document', mode='deterministic test client, not a live model')
        write = self.invocation('write_file', 'summary.md', summary)
        self.report['invocation_id'] = write['invocationId']
        retained = self.root / 'retained-request.json'
        retained.write_text(json.dumps(write, ensure_ascii=False), encoding='utf-8')
        self.call('POST', ROUTE, 403, write, reader)
        check(self.target.read_text(encoding='utf-8') == INTRO, 'Read-only denial still changed the target.')
        self.record('read-only Agent cannot write', status=403)
        pending = self.call('POST', ROUTE, 202, write, writer)
        check(pending.get('errorCode') == 'approval-pending', 'Write did not become approval-pending.')
        check(self.target.read_text(encoding='utf-8') == INTRO, 'Pending write changed the target.')
        approval = ROUTE + '/' + write['invocationId'] + '/approval'
        state = self.call('GET', approval, 200, capability=writer)
        check(state.get('approvalState') == 'Pending', 'Approval state was not Pending.')
        self.record('write awaits approval without file effect', status=202, approval_state='Pending')
        self.call('POST', approval + '/review', 401, write, writer)
        self.record('Agent credential cannot enter operator review', status=401)
        preview = self.call('POST', approval + '/review', 200, write, reviewer, operator=True)
        check(preview.get('rawInput') == summary and preview.get('parameters') == write['parameters'], 'Review changed the requested content.')
        self.report['plan_digest'] = preview['planDigest']
        env = {k: os.environ[k] for k in ('PATH', 'LANG', 'LC_ALL') if k in os.environ}
        env.update({'WEAVE_REVIEW_CAPABILITY': reviewer, 'WEAVE_OPERATOR_KEY': self.operator})
        decision = subprocess.run([sys.executable, str(self.repo / 'examples/governed-tools/review.py'),
                                   '--url', BASE, '--workspace', 'first-use', '--request', str(retained)],
                                  input='approve ' + preview['planDigest'] + '\n', text=True, encoding='utf-8',
                                  capture_output=True, env=env, cwd=self.root, timeout=35, check=False)
        check(decision.returncode == 0 and 'Approved.' in decision.stdout, 'Existing reviewer CLI did not confirm approval.')
        (self.evidence / 'review-transcript.txt').write_text(self.redact(decision.stdout), encoding='utf-8')
        check(self.target.read_text(encoding='utf-8') == INTRO, 'Approval itself executed the write.')
        self.record('existing reviewer CLI explicitly approves without execution', status=200, simulated_confirmation=True)
        self.call('POST', ROUTE, 403, write, reader)
        self.record('approval does not grant execution authority', status=403)
        executed = self.call('POST', ROUTE, 200, write, writer)
        check(executed.get('success') is True, 'Approved invocation did not succeed.')
        # The existing writer explicitly uses Encoding.UTF8, including its preamble.
        # Compare exact bytes rather than trimming arbitrary content to obtain a pass.
        actual = self.target.read_bytes()
        self.report['written_file'] = {'sha256': hashlib.sha256(actual).hexdigest(),
                                       'bytes': len(actual), 'utf8_preamble': actual.startswith(codecs.BOM_UTF8)}
        (self.evidence / 'actual-written-summary.md').write_bytes(actual)
        check(actual == codecs.BOM_UTF8 + summary.encode('utf-8'), 'Written bytes differ from the approved UTF-8 content and preamble.')
        self.record('original Agent executes same ID and approved content', status=200, sha256=sha(summary))
        reread = self.call('POST', ROUTE, 200, self.invocation('read_file', 'summary.md'), reader)
        check(reread.get('output') == summary, 'Public API did not read back the exact approved text.')
        self.record('public API reads back exact approved text', status=200, sha256=sha(summary))
        status_path = ROUTE + '/' + write['invocationId']
        outcome = self.call('GET', status_path, 200, capability=writer)
        check(outcome.get('outcome') == 'Succeeded', 'Recorded result is not Succeeded.')
        self.report['recorded_outcome'] = outcome
        self.record('query durable successful result', status=200, outcome='Succeeded')
        expiring = self.issue('expiring')
        time.sleep(2)
        self.call('POST', ROUTE, 401, self.invocation('read_file', 'meeting.txt'), expiring)
        self.record('expired credential denied', status=401)
        first_pid = self.process.pid
        self.stop()
        self.start(recovery=True)
        check(self.process.pid != first_pid, 'Restart did not start a new OS process.')
        old = self.call('GET', status_path, 200, capability=writer)
        check(old.get('outcome') == 'Succeeded', 'Retained result was lost after restart.')
        self.record('query retained result after real process restart', status=200, outcome='Succeeded')
        self.call('POST', '/api/operator/tools/files/connect', 204, operator=True)
        fresh = self.issue('writer')
        self.call('POST', ROUTE, 401, self.invocation('read_file', 'meeting.txt'), expiring)
        self.record('expired credential stays denied after restart', status=401)
        # External edit is a deliberate non-replay sentinel, NOT another Agent write.
        sentinel = summary + '\nLocal verification marker: duplicate submission must not overwrite this line.\n'
        self.target.write_text(sentinel, encoding='utf-8')
        duplicate = self.call('POST', ROUTE, 200, write, fresh)
        check(duplicate.get('isReplay') is True, 'Duplicate did not return recorded replay metadata.')
        check(self.target.read_text(encoding='utf-8') == sentinel, 'Duplicate replayed the file effect after restart.')
        self.record('same ID after restart does not repeat file effect', status=200, is_replay=True,
                    sentinel_preserved=True)
        (self.evidence / 'final-target-with-sentinel.md').write_text(sentinel, encoding='utf-8')
        self.stop()
        self.report['status'] = 'passed'

    def redact(self, text):
        for value in sorted(self.private_values, key=len, reverse=True):
            text = text.replace(value, '[REDACTED]')
        return text.replace(str(self.root), '<temporary-root>')

    def finish(self):
        try:
            self.stop()
        except Exception as exc:
            self.report['status'] = 'failed'
            self.report['cleanup_failure'] = type(exc).__name__
        for log in self.logs:
            if log.exists():
                # No raw credentials, configuration, journal or unbounded logs leave the job.
                with log.open('rb') as source:
                    source.seek(max(0, log.stat().st_size - 32768))
                    raw = source.read(32768).decode('utf-8', errors='replace')
                (self.evidence / log.name).write_text(self.redact(raw), encoding='utf-8')
        self.report['finished_at'] = datetime.now(timezone.utc).isoformat()
        data = self.redact(json.dumps(self.report, ensure_ascii=False, indent=2)) + '\n'
        (self.evidence / 'report.json').write_text(data, encoding='utf-8')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--host', type=Path, required=True, help='Already-built Weave.Silo.dll')
    parser.add_argument('--evidence', type=Path, required=True, help='New output directory for sanitized results')
    args = parser.parse_args()
    check(shutil.which('dotnet') is not None and args.host.is_file(), 'Build the Host using the repository SDK first.')
    repo = Path(__file__).resolve().parents[2]
    evidence = args.evidence.resolve()
    evidence.mkdir(parents=True, exist_ok=False)
    os.umask(0o077)
    with tempfile.TemporaryDirectory(prefix='weave-first-use-') as temp:
        walkthrough = Walkthrough(repo, args.host.resolve(), evidence, Path(temp))
        try:
            walkthrough.run()
        except Exception as exc:
            walkthrough.report['status'] = 'failed'
            walkthrough.report['failure'] = walkthrough.redact(str(exc)) if isinstance(exc, AcceptanceFailure) else type(exc).__name__
            print('FAIL walkthrough: see the sanitized report and Host log.', file=sys.stderr)
        finally:
            walkthrough.finish()
        return 0 if walkthrough.report['status'] == 'passed' else 1


if __name__ == '__main__':
    raise SystemExit(main())
