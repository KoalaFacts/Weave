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
        self.assertIn("deny-licenses: GPL-2.0, GPL-3.0, AGPL-3.0", job)


if __name__ == "__main__":
    unittest.main()
