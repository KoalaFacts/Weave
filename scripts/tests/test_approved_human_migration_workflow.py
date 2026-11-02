"""Guard the one-time, source-pinned D1 rollout against accidental scope expansion.

No Cloudflare/GitHub credentials or network calls. The workflow itself must still
execute real remote read-only D1 preflights before any writes.
"""
from pathlib import Path
import re
import unittest

ROOT = Path(__file__).resolve().parents[2]
WORKFLOW = ROOT / '.github/workflows/approved-human-schema-migration.yml'
APPROVED_SOURCE = '150ec32e5f2415ed3c64db7e1c0d36655f8dabea'


class ApprovedMigrationWorkflowTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.workflow = WORKFLOW.read_text(encoding='utf-8')

    def test_manual_main_only_with_exact_approved_source_confirmation(self):
        self.assertIn('  workflow_dispatch:', self.workflow)
        self.assertNotIn('\n  push:', self.workflow)
        self.assertNotIn('\n  pull_request:', self.workflow)
        self.assertIn("github.ref == 'refs/heads/main'", self.workflow)
        self.assertIn('confirm_approved_source:', self.workflow)
        self.assertIn('WEAVE_MIGRATION_CONFIRM: ${{ inputs.confirm_approved_source }}', self.workflow)
        self.assertIn('test "$WEAVE_MIGRATION_CONFIRM" = "$WEAVE_MIGRATION_PIN"', self.workflow)

    def test_pinned_source_is_checked_out_and_ancestor_verified_in_both_jobs(self):
        self.assertEqual(self.workflow.count('ref: 150ec32e5f2415ed3c64db7e1c0d36655f8dabea'), 2)
        self.assertEqual(self.workflow.count('git merge-base --is-ancestor "$WEAVE_MIGRATION_PIN" origin/main'), 2)
        self.assertEqual(self.workflow.count('test "$(git rev-parse HEAD)" = "$WEAVE_MIGRATION_PIN"'), 2)
        self.assertEqual(self.workflow.count('test "$(git rev-parse origin/main)" = "$GITHUB_SHA"'), 2)
        self.assertNotIn('ref: ${{ github.sha }}', self.workflow)

    def test_production_only_one_time_human_migration_no_oauth_or_access_activation(self):
        self.assertIn('    environment: production', self.workflow)
        self.assertIn('    needs: verify', self.workflow)
        self.assertIn('  contents: read', self.workflow)
        self.assertNotIn('contents: write', self.workflow)
        self.assertIn('WEAVE_RELEASE_CONFIGURATION_SCOPE: production', self.workflow)
        self.assertIn('WEAVE_RELEASE_DATABASE_MODE: maintain', self.workflow)
        self.assertIn("WEAVE_RELEASE_HUMAN_ACCOUNT_MIGRATION: 'true'", self.workflow)
        self.assertIn("WEAVE_RELEASE_FIRST_PARTY_OAUTH: 'false'", self.workflow)
        self.assertIn("WEAVE_RELEASE_ACCESS_TRIAL_APPLICATION: ''", self.workflow)
        self.assertIn("WEAVE_APPROVED_HUMAN_ONBOARDING_SCHEMA: 'true'", self.workflow)
        self.assertIn('WEAVE_DEPLOY_DATABASE_MODE: maintain', self.workflow)
        self.assertNotIn("WEAVE_APPROVED_FIRST_PARTY_OAUTH_BINDING: 'true'", self.workflow)
        self.assertNotIn('WEAVE_APPROVED_ACCESS_TRIAL_APPLICATION:', self.workflow)

    def test_existing_shared_secret_recovery_and_public_acceptance_gates_retained(self):
        self.assertIn('CLOUDFLARE_API_TOKEN: ${{ secrets.CLOUDFLARE_API_TOKEN }}', self.workflow)
        self.assertIn('CLOUDFLARE_ACCOUNT_ID: ${{ secrets.CLOUDFLARE_ACCOUNT_ID }}', self.workflow)
        self.assertNotIn('CLOUDFLARE_ACCESS_TRIAL_TOKEN', self.workflow)
        self.assertIn('cancel-in-progress: false', self.workflow)
        self.assertIn('group: deploy-weave-worker-demo', self.workflow)
        self.assertIn('node scripts/deploy.ts', self.workflow)
        self.assertIn('node scripts/verify-agent-entry.ts', self.workflow)
        self.assertIn('node scripts/verify-public-mcp.ts', self.workflow)
        self.assertIn('actions/upload-artifact@043fb46d1a93c77aae656e7c1c64a875d1fc6a0a', self.workflow)
        self.assertIn('retention-days: 7', self.workflow)
        self.assertIn('if: ${{ always() }}', self.workflow)
        self.assertTrue(re.search(r'path: typescript/mailbox-worker/test-results/deployment/result\.json', self.workflow))
        self.assertNotIn('continue-on-error:', self.workflow)


if __name__ == '__main__':
    unittest.main()
