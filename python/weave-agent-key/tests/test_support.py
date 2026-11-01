"""The missing-dependency rule: a local run skips without `cryptography`; CI sets WEAVE_REQUIRE_CRYPTOGRAPHY=1 so the run fails instead."""
import os
import subprocess
import sys
import unittest
from pathlib import Path

TESTS = Path(__file__).resolve().parent
# Imports support.py with `cryptography` made unimportable and reports how the import ended.
PROBE = '''
import sys, unittest
sys.modules["cryptography"] = None
try:
    import support
except unittest.SkipTest:
    print("skipped")
except ImportError:
    print("failed")
else:
    print("imported")
'''


def probe(require: str | None) -> str:
    env = {name: value for name, value in os.environ.items() if name != 'WEAVE_REQUIRE_CRYPTOGRAPHY'}
    if require is not None:
        env['WEAVE_REQUIRE_CRYPTOGRAPHY'] = require
    result = subprocess.run([sys.executable, '-c', PROBE], cwd=TESTS, env=env, capture_output=True, text=True, timeout=60, check=True)
    return result.stdout.strip()


class SupportTests(unittest.TestCase):
    def test_support_cryptography_missing_skips_by_default(self):
        self.assertEqual(probe(None), 'skipped')
        self.assertEqual(probe('0'), 'skipped')

    def test_support_cryptography_missing_and_required_fails(self):
        self.assertEqual(probe('1'), 'failed')


if __name__ == '__main__':
    unittest.main()
