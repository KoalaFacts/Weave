"""Execute the first-use product-integrity guard against real Git states."""
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

from test_release_security import ROOT, run_blocks


class FirstUseIntegrityTests(unittest.TestCase):
    def test_reviewed_product_change_can_run_acceptance(self):
        self.check_integrity(mutate=False)

    def test_acceptance_cannot_modify_tracked_product_or_locks(self):
        for path in ('hosts/Weave.Host/Product.cs', 'hosts/Weave.Host/packages.lock.json'):
            for staged in (False, True):
                with self.subTest(path=path, staged=staged):
                    self.check_integrity(mutate=True, path=path, staged=staged)

    def check_integrity(self, *, mutate, path='hosts/Weave.Host/Product.cs', staged=False):
        shell = shutil.which('sh' if os.name == 'nt' else 'bash')
        if shell is None:
            self.skipTest('A POSIX shell is unavailable')
        block = next(block for block in run_blocks(ROOT / '.github/workflows/first-use-acceptance.yml')
                     if 'git diff --exit-code' in block)
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)

            def git(*args):
                return subprocess.run(['git', *args], cwd=root, capture_output=True,
                                      text=True, check=True, timeout=10).stdout.strip()

            git('init', '-q')
            source = root / 'hosts/Weave.Host/Product.cs'
            source.parent.mkdir(parents=True)
            source.write_text('baseline product\n')
            lock = source.parent / 'packages.lock.json'
            lock.write_text('{}\n')
            git('add', '.')
            git('-c', 'user.name=Fixture', '-c', 'user.email=fixture@example.invalid',
                'commit', '-qm', 'baseline')
            base = git('rev-parse', 'HEAD')
            source.write_text('reviewed product\n')
            git('add', '.')
            git('-c', 'user.name=Fixture', '-c', 'user.email=fixture@example.invalid',
                'commit', '-qm', 'reviewed change')
            if mutate:
                (root / path).write_text('unreviewed acceptance substitution\n')
                if staged:
                    git('add', path)
            result = subprocess.run([shell, '-euo', 'pipefail', '-c', ' '.join(block.splitlines())],
                                    cwd=root, env={**os.environ, 'BASE_SHA': base},
                                    capture_output=True, text=True, timeout=10)
            self.assertEqual(result.returncode, 1 if mutate else 0, result.stdout + result.stderr)
            if mutate:
                self.assertIn('unreviewed acceptance substitution', result.stdout)
