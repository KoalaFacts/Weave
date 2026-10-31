"""Shared test set-up: imports the package from this checkout and skips the module when `cryptography` is absent."""
import sys
import unittest
from pathlib import Path

PACKAGE_ROOT = Path(__file__).resolve().parents[1]
if str(PACKAGE_ROOT) not in sys.path:
    sys.path.insert(0, str(PACKAGE_ROOT))

try:
    import cryptography  # noqa: F401
except ImportError:
    raise unittest.SkipTest(
        'cryptography is not installed; run `pip install -r python/weave-agent-key/requirements.txt` to test the Python agent key helper')

POSIX_ONLY = 'POSIX file modes are not enforced on Windows; this runs on Linux/macOS CI'
