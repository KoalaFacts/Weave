"""Diagnostics must remain bounded observations, never policy evidence."""
import contextlib
from email.message import Message
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import signal
import sys
import tempfile
import time
import unittest
from unittest.mock import patch
import urllib.error

SCRIPTS = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(SCRIPTS))
spec = importlib.util.spec_from_file_location('diagnostic', SCRIPTS / 'diagnose_dependency_comparison.py')
diagnostic = importlib.util.module_from_spec(spec)
spec.loader.exec_module(diagnostic)
SECRET = 'synthetic-credential-do-not-print'
RECORD = {'manifest': 'packages.lock.json', 'change_type': 'added', 'name': 'Example'}


class Reply(io.BytesIO):
    status = 200

    def __init__(self, body=b'[]', **headers):
        super().__init__(body)
        self.headers = Message()
        for key, value in headers.items():
            self.headers[key] = value


class Opener:
    def __init__(self, *responses):
        self.responses = iter(responses)
        self.requests = []

    def open(self, request, timeout):
        self.requests.append((request, timeout))
        reply = next(self.responses)
        if isinstance(reply, Exception):
            raise reply
        return reply


class DiagnosticTests(unittest.TestCase):
    def collect(self, opener):
        return diagnostic.collect(SECRET, opener=opener)

    def test_exact_three_gets_fixed_pair_no_follow_and_hash_whole_multiset(self):
        body = json.dumps([RECORD] * 553).encode()
        link = f'<{diagnostic.ENDPOINT}?per_page=5&page=10>; rel="last"'
        opener = Opener(*(Reply(body, Link=link, **{'X-GitHub-Request-Id': 'AB12:123:DEF',
                          'X-GitHub-Api-Version-Selected': '2022-11-28'}) for _ in range(3)))
        result = self.collect(opener)
        self.assertEqual([request.full_url for request, _ in opener.requests],
                         [diagnostic.ENDPOINT + query for query in ('?per_page=5&page=1', '?per_page=5&page=2', '?per_page=100&page=1')])
        self.assertTrue(all(r.get_method() == 'GET' and 0 < timeout <= 15 for r, timeout in opener.requests))
        self.assertEqual([row['records'] for row in result], [553] * 3)
        self.assertEqual(result[0]['body_sha256'], hashlib.sha256(body).hexdigest())
        canonical = json.dumps(sorted(json.dumps(r, sort_keys=True, separators=(',', ':'), ensure_ascii=True)
                                      for r in [RECORD] * 553), separators=(',', ':')).encode()
        self.assertEqual(result[0]['canonical_multiset_sha256'], hashlib.sha256(canonical).hexdigest())
        self.assertEqual(result[0]['link'], link)
        self.assertNotIn(SECRET, json.dumps(result))
        self.assertNotIn('dependency_records', json.dumps(result))

    def test_denials_redirects_and_network_errors_stop_without_retry_or_body(self):
        for status in (301, 302, 307, 403, 429, 500):
            with self.subTest(status=status):
                opener = Opener(urllib.error.HTTPError('https://secret.invalid/' + SECRET, status, SECRET,
                                  {'Location': 'https://evil.invalid/' + SECRET}, io.BytesIO(SECRET.encode())), Reply())
                with self.assertRaises(diagnostic.DiagnosticError) as caught:
                    self.collect(opener)
                self.assertEqual(caught.exception.code, 'diagnostic-http-error')
                self.assertEqual(len(opener.requests), 1)
                self.assertNotIn(SECRET, str(caught.exception))
        opener = Opener(urllib.error.URLError(SECRET), Reply())
        with self.assertRaisesRegex(diagnostic.DiagnosticError, '^diagnostic-network-error$'):
            self.collect(opener)
        self.assertEqual(len(opener.requests), 1)

    def test_body_budget_and_strict_format_errors_are_fixed_codes(self):
        cases = [(b' ' * (2 * 1024 * 1024 + 1), 'diagnostic-response-too-large'),
                 (SECRET.encode(), 'diagnostic-invalid-response'),
                 (b'[{"manifest":"a","manifest":"b"}]', 'diagnostic-invalid-response'),
                 (b'[NaN]', 'diagnostic-invalid-response'), (b'[1]', 'diagnostic-invalid-response'),
                 (b'{}', 'diagnostic-invalid-response'), (b'\xff', 'diagnostic-invalid-response')]
        for body, code in cases:
            with self.subTest(code=code, size=len(body)):
                opener = Opener(Reply(body), Reply())
                with self.assertRaises(diagnostic.DiagnosticError) as caught:
                    self.collect(opener)
                self.assertEqual(caught.exception.code, code)
                self.assertEqual(len(opener.requests), 1)

    def test_aggregate_raw_record_budget_counts_duplicates(self):
        opener = Opener(*(Reply(json.dumps([RECORD] * 667).encode()) for _ in range(3)))
        with self.assertRaisesRegex(diagnostic.DiagnosticError, '^diagnostic-change-limit$'):
            self.collect(opener)
        self.assertEqual(len(opener.requests), 3)

    def test_unsafe_headers_never_reach_public_output(self):
        cases = [('Link', f'<https://evil.invalid/{SECRET}>; rel="next"'),
                 ('Link', f'<{diagnostic.ENDPOINT}?per_page=5&page=2&token={SECRET}>; rel="next"'),
                 ('Link', 'x' * 4097), ('X-GitHub-Request-Id', SECRET),
                 ('X-GitHub-Request-Id', 'ABCD\n::error::' + SECRET),
                 ('X-GitHub-Api-Version-Selected', SECRET), ('Date', SECRET)]
        for key, value in cases:
            with self.subTest(header=key):
                opener = Opener(Reply(**{key: value}), Reply())
                with self.assertRaisesRegex(diagnostic.DiagnosticError, '^diagnostic-invalid-header$'):
                    self.collect(opener)
                self.assertEqual(len(opener.requests), 1)

    def test_duplicate_allowlisted_header_is_rejected(self):
        response = Reply(**{'X-GitHub-Request-Id': 'ABCD:123'})
        response.headers['X-GitHub-Request-Id'] = 'ABCD:456'
        with self.assertRaisesRegex(diagnostic.DiagnosticError, '^diagnostic-invalid-header$'):
            self.collect(Opener(response))

    def test_numeric_repository_link_and_rfc_date_are_allowed(self):
        link = (f'<https://api.github.com/repositories/1178623688/dependency-graph/compare/'
                f'{diagnostic.BASE}...{diagnostic.HEAD}?page=2&per_page=5>; rel="next"')
        rows = self.collect(Opener(Reply(Link=link, Date='Tue, 20 Oct 2026 07:16:07 GMT'), Reply(), Reply()))
        self.assertEqual(rows[0]['link'], link)
        self.assertEqual(rows[0]['response_date'], 'Tue, 20 Oct 2026 07:16:07 GMT')
        self.assertIsNone(rows[1]['request_id'])

    def test_transport_disables_redirects_and_environment_proxy(self):
        with patch.object(diagnostic.urllib.request, 'build_opener', return_value=Opener(Reply(), Reply(), Reply())) as build:
            diagnostic.collect(SECRET)
        proxy, redirect = build.call_args.args
        self.assertEqual(proxy.proxies, {})
        self.assertIsNone(redirect.redirect_request(None, None, 302, '', {}, 'https://evil.invalid'))

    def test_hard_deadline_interrupts_blocking_io_and_restores_alarm(self):
        class Blocking(Opener):
            def open(self, request, timeout):
                time.sleep(5)
        started = time.monotonic()
        with patch.object(diagnostic, 'REQUEST_SECONDS', 0.05):
            with self.assertRaisesRegex(diagnostic.DiagnosticError, '^diagnostic-timeout$'):
                self.collect(Blocking())
        self.assertLess(time.monotonic() - started, 1)
        self.assertEqual(signal.getitimer(signal.ITIMER_REAL), (0.0, 0.0))
        with patch.object(diagnostic, 'TOTAL_SECONDS', 0.05):
            with self.assertRaisesRegex(diagnostic.DiagnosticError, '^diagnostic-timeout$'):
                self.collect(Blocking())

    def test_main_public_summary_has_only_fixed_error_and_separate_execution_identity(self):
        with tempfile.TemporaryDirectory() as directory:
            summary = Path(directory) / 'summary.md'
            report = Path(directory) / 'report.json'
            env = {'GH_TOKEN': SECRET, 'GITHUB_SHA': 'a' * 40, 'PR_HEAD_SHA': 'b' * 40,
                   'GITHUB_STEP_SUMMARY': str(summary), 'DIAGNOSTIC_REPORT': str(report)}
            output = io.StringIO()
            with patch.dict(os.environ, env, clear=True), contextlib.redirect_stdout(output), \
                 patch.object(diagnostic, 'collect', side_effect=diagnostic.DiagnosticError('diagnostic-invalid-response')):
                self.assertEqual(diagnostic.main(), 1)
            data = json.loads(report.read_text())
            self.assertEqual(data['execution_sha'], 'a' * 40)
            self.assertEqual(data['pr_head_sha'], 'b' * 40)
            self.assertEqual(data['comparison_head'], diagnostic.HEAD)
            self.assertEqual(data['failure_code'], 'diagnostic-invalid-response')
            self.assertNotIn(SECRET, output.getvalue() + summary.read_text() + report.read_text())
            self.assertIn('::notice title=Dependency comparison diagnostic::', output.getvalue())

    def test_invalid_identity_and_credential_make_no_request(self):
        with patch.dict(os.environ, {'GH_TOKEN': SECRET, 'GITHUB_SHA': SECRET, 'PR_HEAD_SHA': 'b' * 40}, clear=True), \
             patch.object(diagnostic, 'collect') as collect, contextlib.redirect_stdout(io.StringIO()) as output:
            self.assertEqual(diagnostic.main(), 1)
            collect.assert_not_called()
            self.assertNotIn(SECRET, output.getvalue())
        for token in ('', 'bad\nvalue'):
            opener = Opener(Reply())
            with self.assertRaisesRegex(diagnostic.DiagnosticError, '^diagnostic-invalid-credential$'):
                diagnostic.collect(token, opener=opener)
            self.assertEqual(opener.requests, [])

    def test_workflow_diagnostic_is_last_and_read_only_without_policy_override(self):
        workflow = (SCRIPTS.parent / '.github/workflows/ci.yml').read_text()
        job = workflow.split('  dependency-review:\n', 1)[1]
        self.assertIn('    permissions:\n      contents: read\n', job)
        before, step = job.split('      - name: Diagnose fixed dependency comparison\n', 1)
        self.assertIn('Require allowed dependency licenses', before)
        self.assertIn('Retain license evidence outcome', before)
        self.assertIn("if: ${{ !cancelled() && github.event.pull_request.number == 223 }}", step)
        self.assertIn('timeout-minutes: 2', step)
        self.assertNotIn('continue-on-error', step)
        self.assertNotIn('checks: write', job)
        self.assertNotIn('pull-requests: write', job)


if __name__ == '__main__':
    unittest.main()
