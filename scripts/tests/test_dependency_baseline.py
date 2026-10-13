"""Execute the CI baseline step with real translation and a synthetic HTTP boundary."""
import base64
import copy
import hashlib
import io
import json
import os
from pathlib import Path
import tempfile
import textwrap
import unittest
from unittest.mock import patch

from test_nuget_snapshot import FakeApi, SHA, lock

ROOT = Path(__file__).resolve().parents[2]
STEP = '      - name: Validate the exact committed historical baseline\n'


class ReadOnlyResponse(io.BytesIO):
    status = 200


class ReadOnlyTransport:
    """Only Git object GETs are available; any submission is a test failure."""

    def __init__(self, api):
        self.api = api
        self.paths = []

    def open(self, request, timeout):
        prefix = 'https://api.github.com/repos/owner/repo/'
        if request.data is not None or not request.full_url.startswith(prefix + 'git/'):
            raise AssertionError('Baseline validation must only read source Git objects')
        path = request.full_url.removeprefix(prefix)
        self.paths.append(path)
        return ReadOnlyResponse(json.dumps(self.api.request(path)).encode())


class HistoricalBaselineTests(unittest.TestCase):
    def run_step(self, api=None, source_sha=SHA, source_ref='refs/heads/main'):
        workflow = (ROOT / '.github/workflows/ci.yml').read_text()
        self.assertIn(STEP, workflow, 'Historical baseline needs its own explicit validation step')
        step = workflow.split(STEP, 1)[1].split('\n      - ', 1)[0]
        code = textwrap.dedent(step.split('        run: |\n', 1)[1])
        transport = ReadOnlyTransport(api or FakeApi())
        with tempfile.TemporaryDirectory() as temporary:
            env = {'GITHUB_REPOSITORY': 'owner/repo', 'GITHUB_RUN_ID': '17',
                   'GH_TOKEN': 'synthetic-read-only', 'PR_BASE_SHA': source_sha,
                   'PR_BASE_REF': source_ref, 'RUNNER_TEMP': temporary,
                   'GITHUB_STEP_SUMMARY': str(Path(temporary) / 'summary.md')}
            with patch.dict(os.environ, env), patch('urllib.request.build_opener', return_value=transport), \
                    patch('sys.stdout', io.StringIO()), self.assertRaises(SystemExit) as result:
                exec(compile(code, 'ci-baseline-step', 'exec'), {'__name__': '__main__'})
            output = Path(temporary) / 'dependency-baseline'
            report = json.loads((output / 'validation.json').read_text())
            snapshot_path = output / 'baseline.json'
            plan = json.loads(snapshot_path.read_text()) if snapshot_path.exists() else None
            summary = (Path(temporary) / 'summary.md').read_text()
        return result.exception.code, report, plan, transport.paths, summary

    def test_authentic_historical_lock_is_retained_without_claiming_restore(self):
        code, report, plan, paths, summary = self.run_step()
        self.assertEqual(code, 0)
        self.assertEqual(report['status'], 'available')
        self.assertEqual(report['validation'], 'committed-lock-graph')
        self.assertIs(report['restore_performed'], False)
        self.assertEqual(report['source_sha'], SHA)
        self.assertEqual(plan['sha'], SHA)
        self.assertEqual(plan['ref'], 'refs/heads/main')
        self.assertEqual(report['package_nodes'], 2)
        self.assertIn('pkg:nuget/example.leaf@1.0.0', plan['manifests']['src/packages.lock.json#net10.0']['resolved'])
        self.assertIn('git/commits/' + SHA, paths)
        self.assertIn('does not establish a fresh locked restore', summary)

    def test_changed_lock_version_remains_visible_in_comparison_data(self):
        old = self.run_step()[2]
        updated = copy.deepcopy(lock())
        updated['dependencies']['net10.0']['Example.Leaf']['resolved'] = '2.0.0'
        new = self.run_step(FakeApi(updated))[2]
        manifest = 'src/packages.lock.json#net10.0'
        self.assertEqual(set(new['manifests'][manifest]['resolved']) - set(old['manifests'][manifest]['resolved']),
                         {'pkg:nuget/example.leaf@2.0.0'})
        self.assertNotEqual(new['manifests'][manifest]['metadata']['lock_blob'],
                            old['manifests'][manifest]['metadata']['lock_blob'])

    def test_missing_incomplete_symlink_and_tampered_baselines_fail_closed(self):
        for condition, reason in [('missing', 'missing-project-lock'),
                                  ('incomplete', 'incomplete-source-tree'),
                                  ('symlink', 'unsafe-lock-entry'),
                                  ('tampered', 'lock-integrity-mismatch')]:
            with self.subTest(condition=condition):
                api = FakeApi()
                if condition == 'missing':
                    api.tree['tree'].pop()
                elif condition == 'incomplete':
                    api.tree['truncated'] = True
                elif condition == 'symlink':
                    api.tree['tree'][1]['mode'] = '120000'
                else:
                    api.blob['content'] = base64.b64encode(b'{}').decode()
                code, report, plan, _, summary = self.run_step(api)
                self.assertEqual(code, 1)
                self.assertEqual(report['status'], 'unavailable')
                self.assertEqual(report['failure'], reason)
                self.assertIsNone(plan)
                self.assertIn('Unavailable', summary)

    def test_authentic_malformed_json_and_missing_edges_are_not_valid_baselines(self):
        api = FakeApi()
        data = b'{"version": 2, "dependencies": '
        blob = hashlib.sha1(b'blob ' + str(len(data)).encode() + b'\0' + data).hexdigest()
        api.blob.update(sha=blob, content=base64.b64encode(data).decode())
        api.tree['tree'][1].update(sha=blob, size=len(data))
        bad_edge = lock()
        bad_edge['dependencies']['net10.0']['Example.Top']['dependencies'] = {'Absent': '1.0.0'}
        for source, reason in [(api, 'invalid-json'),
                               (FakeApi(bad_edge), 'incomplete-or-cyclic-project-edge')]:
            with self.subTest(reason=reason):
                code, report, plan, _, _ = self.run_step(source)
                self.assertEqual(code, 1)
                self.assertEqual(report['failure'], reason)
                self.assertIsNone(plan)

    def test_wrong_commit_and_missing_or_invalid_source_identity_do_not_fall_back(self):
        for source_sha, source_ref, reason in [('9' * 40, 'refs/heads/main', 'commit-identity-mismatch'),
                                               ('', 'refs/heads/main', 'invalid-source-ref'),
                                               (SHA, 'refs/heads/../head', 'invalid-source-ref')]:
            with self.subTest(source_sha=source_sha, source_ref=source_ref):
                code, report, plan, _, _ = self.run_step(source_sha=source_sha, source_ref=source_ref)
                self.assertEqual(code, 1)
                self.assertEqual(report['source_sha'], source_sha)
                self.assertEqual(report['failure'], reason)
                self.assertIsNone(plan)


if __name__ == '__main__':
    unittest.main()
