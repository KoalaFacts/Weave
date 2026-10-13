"""The official toolkit accepts SUCCESS and ACCEPTED; neither proves policy passed."""
import importlib.util
import io
import json
import os
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch, MagicMock

from test_nuget_snapshot import FakeApi, lock

ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location('receipt_snapshots', ROOT / 'scripts/submit_nuget_snapshots.py')
producer = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(producer)


class SnapshotReceiptTests(unittest.TestCase):
    def run_receipts(self, status):
        with tempfile.TemporaryDirectory() as temporary:
            env = {'GITHUB_REPOSITORY': 'owner/repo', 'GITHUB_RUN_ID': '7', 'GH_TOKEN': 'synthetic',
                   'PR_BASE_SHA': '1'*40, 'PR_HEAD_SHA': '2'*40,
                   'PR_BASE_REF': 'refs/heads/main', 'PR_HEAD_REF': 'refs/pull/1/head'}
            api = MagicMock()
            api.request.side_effect = [{'id': 1, 'result': 'SUCCESS'}, {'id': 2, 'result': status}]
            plans = [{'sha': '1'*40, 'metadata': {}}, {'sha': '2'*40, 'metadata': {}}]
            with patch.dict(os.environ, env), patch.object(sys, 'argv', ['test', '--output', temporary]), \
                 patch.object(producer, 'Api', return_value=api), patch.object(producer, 'snapshot', side_effect=plans), \
                 patch('sys.stdout', io.StringIO()):
                code = producer.main()
            return code, json.loads((Path(temporary)/'submission.json').read_text())

    def test_nondefault_branch_accepted_receipt_is_preserved_as_submission_only(self):
        code, report = self.run_receipts('ACCEPTED')
        self.assertEqual(code, 0)
        self.assertEqual(report['status'], 'submitted')
        self.assertEqual(len(report['sources']), 2)
        self.assertNotEqual(report['status'], 'review-passed')

    def test_unknown_receipt_is_not_success(self):
        code, report = self.run_receipts('UNKNOWN')
        self.assertEqual(code, 1)
        self.assertEqual(report['status'], 'unavailable')

    def test_incomplete_project_graph_on_either_source_blocks_all_submissions(self):
        for bad_source in ('1' * 40, '2' * 40):
            with self.subTest(bad_source=bad_source), tempfile.TemporaryDirectory() as temporary:
                class PairApi:
                    def __init__(self):
                        self.current = None
                        self.writes = []

                    def request(self, path, payload=None):
                        if payload is not None:
                            self.writes.append(payload)
                            return {'id': len(self.writes), 'result': 'SUCCESS'}
                        if path.startswith('git/commits/'):
                            sha = path.rsplit('/', 1)[1]
                            document = lock()
                            if sha == bad_source:
                                document['dependencies']['net10.0']['weave.local']['dependencies'] = {'Absent': '1.0.0'}
                            self.current = FakeApi(document)
                            return {**self.current.request(path), 'sha': sha}
                        return self.current.request(path)

                api = PairApi()
                env = {'GITHUB_REPOSITORY': 'owner/repo', 'GITHUB_RUN_ID': '7', 'GH_TOKEN': 'synthetic',
                       'PR_BASE_SHA': '1' * 40, 'PR_HEAD_SHA': '2' * 40,
                       'PR_BASE_REF': 'refs/heads/main', 'PR_HEAD_REF': 'refs/pull/1/head'}
                with patch.dict(os.environ, env), patch.object(sys, 'argv', ['test', '--output', temporary]), \
                        patch.object(producer, 'Api', return_value=api), patch('sys.stdout', io.StringIO()):
                    code = producer.main()
                report = json.loads((Path(temporary) / 'submission.json').read_text())
                self.assertEqual(code, 1)
                self.assertEqual(report['failure'], 'incomplete-or-cyclic-project-edge')
                self.assertEqual(report['sources'], [])
                self.assertEqual(api.writes, [])


if __name__ == '__main__':
    unittest.main()
