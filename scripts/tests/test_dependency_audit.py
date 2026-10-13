"""Execute the audit evidence gate against synthetic NuGet JSON reports."""
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / 'scripts/check_dependency_audit.py'


class DependencyAuditTests(unittest.TestCase):
    def run_report(self, report):
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / 'audit.json'
            path.write_text(json.dumps(report))
            return subprocess.run([sys.executable, str(SCRIPT), str(path)],
                                  capture_output=True, text=True, timeout=10)

    def test_clean_version_one_report_passes(self):
        result = self.run_report({'version': 1, 'projects': [{'path': 'synthetic.csproj'}]})
        self.assertEqual(0, result.returncode, result.stderr)

    def test_nested_direct_and_transitive_vulnerabilities_fail(self):
        for kind in ('topLevelPackages', 'transitivePackages'):
            with self.subTest(kind=kind):
                result = self.run_report({'version': 1, 'projects': [{'path': 'synthetic.csproj', 'frameworks': [{kind: [
                    {'id': 'synthetic-package', 'vulnerabilities': [{'severity': 'Low'}]}]}]}]})
                self.assertEqual(1, result.returncode, result.stderr)
                self.assertIn('vulnerabilities', result.stderr)

    def test_incomplete_audit_warnings_and_errors_fail(self):
        for level in ('Warning', 'Error'):
            with self.subTest(level=level):
                result = self.run_report({'version': 1, 'projects': [{'path': 'synthetic.csproj'}], 'problems': [{'level': level}]})
                self.assertEqual(1, result.returncode, result.stderr)
                self.assertIn('complete cleanly', result.stderr)

    def test_malformed_project_entries_fail(self):
        for project in (None, {}, 'bad', {'path': ''}, {'path': '   '}, {'path': 1}):
            with self.subTest(project=project):
                result = self.run_report({'version': 1, 'projects': [project]})
                self.assertEqual(1, result.returncode, result.stderr)
                self.assertIn('project identity', result.stderr)

    def test_missing_empty_or_unsupported_evidence_fails(self):
        for report in (None, [], {}, {'version': 2, 'projects': [{}]},
                       {'version': True, 'projects': [{}]}, {'version': 1, 'projects': []}):
            with self.subTest(report=report):
                result = self.run_report(report)
                self.assertEqual(1, result.returncode, result.stderr)

    def test_unreadable_and_invalid_json_fail(self):
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / 'audit.json'
            for content in (None, 'not JSON'):
                if content is not None:
                    path.write_text(content)
                result = subprocess.run([sys.executable, str(SCRIPT), str(path)],
                                        capture_output=True, text=True, timeout=10)
                self.assertEqual(1, result.returncode, result.stderr)


if __name__ == '__main__':
    unittest.main()
