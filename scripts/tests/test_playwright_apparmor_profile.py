"""Check the path boundary of the CI-only managed Chromium AppArmor profile."""
import importlib.util
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest
from unittest.mock import patch

from test_release_security import run_blocks


ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / 'scripts/playwright_apparmor_profile.py'
SPEC = importlib.util.spec_from_file_location('playwright_apparmor_profile', SCRIPT)
PROFILE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(PROFILE)


class PlaywrightAppArmorProfileTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.home = Path(self.temporary.name)
        self.browser = self.home / '.cache/ms-playwright/chromium-1234/chrome-linux64/chrome'
        self.browser.parent.mkdir(parents=True)
        self.browser.write_text('synthetic executable for profile generation only\n')
        self.browser.chmod(0o755)

    def render(self, executable):
        with patch.object(PROFILE.Path, 'home', return_value=self.home):
            return PROFILE.chromium_profile(str(executable))

    def test_installed_managed_executable_gets_one_exact_userns_attachment(self):
        result = self.render(self.browser)
        self.assertEqual(result, 'abi <abi/4.0>,\n'
                         f'profile weave-playwright-chromium "{self.browser}" flags=(unconfined) {{\n'
                         '  userns,\n}\n')

    def test_policy_metacharacters_and_unmanaged_paths_are_rejected(self):
        for executable in (str(self.browser).replace('1234', '*'),
                           str(self.browser).replace('1234', '{1234,5678}'),
                           str(self.browser) + '\nuserns,',
                           str(self.browser).replace('1234', '"1234'),
                           '/opt/google/chrome/chrome',
                           str(self.browser).replace('/.cache/', '/other-cache/')):
            with self.subTest(executable=executable):
                with self.assertRaisesRegex(ValueError, 'Expected the exact managed Chromium executable'):
                    self.render(executable)

    def test_missing_or_nonexecutable_browser_does_not_generate_policy(self):
        for missing in (False, True):
            with self.subTest(missing=missing):
                if missing:
                    self.browser.unlink()
                else:
                    self.browser.chmod(0o644)
                with self.assertRaisesRegex(ValueError, 'Install the pinned Playwright Chromium first'):
                    self.render(self.browser)

    def test_symlink_to_another_executable_does_not_generate_policy(self):
        target = self.home / 'other-browser'
        self.browser.rename(target)
        self.browser.symlink_to(target)
        with self.assertRaisesRegex(ValueError, 'must not contain symlinks'):
            self.render(self.browser)

    def test_runner_policy_check_rejects_changed_global_restrictions(self):
        shell = shutil.which('bash')
        if shell is None:
            self.skipTest('Bash is unavailable')
        action = ROOT / '.github/actions/setup-playwright-sandbox/action.yml'
        block = next(block for block in run_blocks(action) if 'playwright-policy-after.txt' in block)
        sysctl = self.home / 'sysctl'
        sysctl.write_text('#!/bin/sh\nprintf "kernel.apparmor_restrict_unprivileged_userns = %s\\n'
                          'user.max_user_namespaces = 1000\\n" "$TEST_USERNS"\n')
        sysctl.chmod(0o755)
        for before, after in (('1', '1'), ('1', '0'), ('0', '0'), ('0', '1')):
            with self.subTest(before=before, after=after):
                (self.home / 'playwright-policy-before.txt').write_text(
                    f'kernel.apparmor_restrict_unprivileged_userns = {before}\n'
                    'user.max_user_namespaces = 1000\n')
                result = subprocess.run([shell, '-euo', 'pipefail', '-c', block],
                                        env={**os.environ, 'PATH': str(self.home) + os.pathsep + os.environ['PATH'],
                                             'RUNNER_TEMP': str(self.home), 'TEST_USERNS': after},
                                        capture_output=True, text=True, timeout=10)
                self.assertEqual(result.returncode, 0 if before == after else 1, result.stderr)
                self.assertEqual('::notice' in result.stdout, before == after)
                if before != after:
                    self.assertIn(f'+kernel.apparmor_restrict_unprivileged_userns = {after}', result.stdout)
