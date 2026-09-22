"""Small post-action guard tests; black-box pinned-action acceptance is separate."""
import copy
import importlib.util
import io
import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location('license_evidence', ROOT / 'scripts/check_dependency_license_evidence.py')
guard = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(guard)
EMPTY = {'unlicensed': [], 'unresolved': [], 'forbidden': []}
ENTRY = {'change_type': 'added', 'name': 'fixture', 'version': '1.0.0',
         'manifest': 'fixture.lock', 'package_url': 'pkg:nuget/fixture@1.0.0'}


class LicenseEvidenceTests(unittest.TestCase):
    def test_complete_empty_invalid_lists_are_available_not_full_audit(self):
        result = guard.inspect_licenses(json.dumps(EMPTY))
        self.assertEqual(result, {'status': 'available', 'unlicensed': 0, 'unresolved': 0, 'forbidden': 0})

    def test_prohibited_and_unknown_evidence_fail_with_distinct_categories(self):
        for category in EMPTY:
            with self.subTest(category=category):
                value = copy.deepcopy(EMPTY)
                value[category] = [ENTRY]
                result = guard.inspect_licenses(json.dumps(value))
                self.assertEqual(result['status'], 'blocked' if category == 'forbidden' else 'unavailable')
                self.assertEqual(result[category], 1)

    def test_missing_malformed_duplicate_and_unknown_outputs_do_not_pass(self):
        for payload in ('', 'null', '[]', '{}', '{',
                        '{"unlicensed":[],"unresolved":[],"forbidden":[],"forbidden":[]}',
                        json.dumps({**EMPTY, 'futureCategory': []}),
                        json.dumps({**EMPTY, 'unlicensed': None})):
            with self.subTest(payload=payload):
                self.assertEqual(guard.inspect_licenses(payload)['status'], 'unavailable')

    def test_invalid_entries_or_removed_rows_are_not_silently_ignored(self):
        for entry in (None, {}, {**ENTRY, 'change_type': 'removed'}, {**ENTRY, 'name': 7},
                      {**ENTRY, 'package_url': ''}):
            with self.subTest(entry=entry):
                result = guard.inspect_licenses(json.dumps({**EMPTY, 'unlicensed': [entry]}))
                self.assertEqual(result['failure_code'], 'invalid-license-evidence')

    def test_total_and_raw_byte_limits_still_block(self):
        value = {key: [ENTRY] * 1000 for key in EMPTY}
        self.assertEqual(guard.inspect_licenses(json.dumps(value))['failure_code'], 'invalid-license-evidence')
        self.assertEqual(guard.inspect_licenses(' ' * (guard.MAX_BYTES + 1))['status'], 'unavailable')

    def test_cli_failure_report_contains_counts_not_untrusted_content(self):
        private = {**ENTRY, 'name': 'private-credential-like-text', 'license': 'private-peer-data'}
        with tempfile.TemporaryDirectory() as root:
            report = Path(root) / 'report.json'
            output = io.StringIO()
            with patch.dict(os.environ, {'INVALID_LICENSE_CHANGES': json.dumps({**EMPTY, 'unlicensed': [private]})}), \
                 patch('sys.stdout', output):
                self.assertEqual(guard.main(['--report', str(report)]), 1)
            text = report.read_text() + output.getvalue()
            self.assertNotIn('private-', text)
            self.assertEqual(json.loads(report.read_text())['unlicensed'], 1)

    def test_ci_runs_evidence_guard_after_the_same_pinned_policy_action(self):
        workflow = (ROOT / '.github/workflows/ci.yml').read_text()
        review = workflow.split('  dependency-review:\n', 1)[1]
        self.assertIn('id: dependency-policy', review)
        self.assertIn('a1d282b36b6f3519aa1f3fc636f609c47dddb294', review)
        self.assertIn('deny-licenses: GPL-2.0, GPL-3.0, AGPL-3.0', review)
        self.assertIn('check_dependency_license_evidence.py', review)
        self.assertIn("steps.dependency-policy.outputs['invalid-license-changes']", review)
        self.assertLess(review.index('check_dependency_review_evidence.py'), review.index('id: dependency-policy'))
        self.assertLess(review.index('id: dependency-policy'), review.index('check_dependency_license_evidence.py'))
        self.assertNotIn('continue-on-error', review)
        self.assertNotIn('contents: write', review)

    def test_fork_equivalent_permission_acceptance_keeps_writer_immutable_and_readonly(self):
        workflow = (ROOT / '.github/workflows/dependency-policy-acceptance.yml').read_text()
        denial = workflow.split('  read-only-submission:\n', 1)[1]
        self.assertIn('contents: read', denial)
        self.assertNotIn('contents: write', denial)
        self.assertIn('ref: c36ab57b5c2c46e00b7cc71fdab19cac4e344b3e', denial)
        self.assertIn('snapshot-api-http-403', denial)
        self.assertIn("report.get('sources') == []", denial)
        self.assertNotIn('pull_request_target:', workflow)
        for operation in ('dotnet restore', 'npm install', 'cargo build', 'mvn '):
            self.assertNotIn(operation, denial)


if __name__ == '__main__':
    unittest.main()
