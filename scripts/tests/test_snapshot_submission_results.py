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


if __name__ == '__main__':
    unittest.main()
