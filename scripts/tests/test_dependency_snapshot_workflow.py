"""Snapshot production must be separate from read-only dependency policy review."""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]


class SnapshotWorkflowTests(unittest.TestCase):
    def workflow(self):
        return (ROOT / '.github/workflows/ci.yml').read_text(encoding='utf-8')

    def test_snapshot_producer_exists_and_precedes_review(self):
        text = self.workflow()
        self.assertIn('  dependency-snapshots:\n', text)
        review = text.split('  dependency-review:\n', 1)[1]
        self.assertIn('needs: dependency-snapshots', review)
        self.assertIn('check_dependency_review_evidence.py', review)
        self.assertNotIn('contents: write', review)

    def test_writer_executes_only_pinned_tools_not_pr_builds(self):
        text = self.workflow()
        self.assertIn('  dependency-snapshots:\n', text)
        writer = text.split('  dependency-snapshots:\n', 1)[1].split('  dependency-review:\n', 1)[0]
        self.assertIn('needs: dependency-lock-validation', writer)
        self.assertRegex(writer, r'ref: [0-9a-f]{40}')
        self.assertIn('persist-credentials: false', writer)
        self.assertIn('contents: write', writer)
        self.assertIn('submit_nuget_snapshots.py', writer)
        self.assertIn('github.event.pull_request.base.sha', writer)
        self.assertIn('github.event.pull_request.head.sha', writer)
        for unsafe in ('dotnet restore', 'npm ', 'cargo ', 'mvn ', 'pull_request_target', 'continue-on-error'):
            self.assertNotIn(unsafe, writer)

    def test_exact_base_and_head_locks_are_verified_without_write_permission(self):
        text = self.workflow()
        self.assertIn('  dependency-lock-validation:\n', text)
        job = text.split('  dependency-lock-validation:\n', 1)[1].split('  dependency-snapshots:\n', 1)[0]
        self.assertIn('github.event.pull_request.base.sha', job)
        self.assertIn('github.event.pull_request.head.sha', job)
        self.assertIn('dotnet restore Weave.slnx --locked-mode', job)
        self.assertIn('contents: read', job)
        self.assertNotIn('contents: write', job)
        self.assertNotIn('GH_TOKEN:', job)


if __name__ == '__main__':
    unittest.main()
