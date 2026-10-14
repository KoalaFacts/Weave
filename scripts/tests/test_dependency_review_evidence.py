"""The review job must not report success when the comparison lacks evidence."""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]


class DependencyReviewWorkflowTests(unittest.TestCase):
    def job(self):
        workflow = (ROOT / ".github/workflows/ci.yml").read_text(encoding="utf-8")
        return workflow.split("  dependency-review:\n", 1)[1]

    def test_evidence_gate_precedes_dependency_review(self):
        job = self.job()
        gate = "python3 scripts/check_dependency_review_evidence.py"
        self.assertIn(gate, job,
                      "Snapshot warnings must fail the job before the action can report a clean review.")
        self.assertLess(job.index(gate), job.index("uses: actions/dependency-review-action@"))
        self.assertNotIn("continue-on-error:", job)
        self.assertNotIn("warn-only: true", job)

    def test_existing_security_policy_is_not_relaxed(self):
        job = self.job()
        self.assertIn("contents: read", job)
        self.assertNotIn("contents: write", job)
        self.assertIn("fail-on-severity: high", job)
        self.assertNotIn("deny-licenses:", job)
        self.assertIn("tools/dependency-license-policy/check-license-policy.ts", job)

    def test_snapshot_wait_has_an_explicit_budget_and_enclosing_timeouts(self):
        job = self.job()
        self.assertIn('--snapshot-wait-seconds 600', job)
        self.assertIn('    timeout-minutes: 20\n', job.split('    steps:', 1)[0])
        gate = job.split('      - name: Require dependency comparison evidence\n', 1)[1]
        self.assertIn('        timeout-minutes: 12\n', gate.split('\n      - name:', 1)[0])


if __name__ == "__main__":
    unittest.main()
