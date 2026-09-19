"""Regression tests execute workflow scripts with harmless local sentinel input."""
import os
from pathlib import Path
import re
import subprocess
import tempfile
import unittest
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]


def run_blocks(path):
    """Read literal YAML run blocks without adding a CI dependency."""
    lines = path.read_text().splitlines()
    blocks = []
    for index, line in enumerate(lines):
        match = re.match(r'^(\s*)run: (.*)$', line)
        if not match:
            continue
        if match[2] not in ('|', '|-', '>', '>-'):
            blocks.append(match[2])
            continue
        indent = len(match[1])
        body = []
        for following in lines[index + 1:]:
            if following.strip() and len(following) - len(following.lstrip()) <= indent:
                break
            body.append(following[indent + 2:])
        blocks.append('\n'.join(body))
    return blocks


class ReleaseWorkflowTests(unittest.TestCase):
    def test_version_validation_does_not_execute_input(self):
        workflow = ROOT / '.github/workflows/release.yml'
        blocks = run_blocks(workflow)
        validation = next(b for b in blocks if 'validate-version' in b or 'VERSION="${{ inputs.version }}"' in b)
        with tempfile.TemporaryDirectory() as temporary:
            sentinel = Path(temporary) / 'executed'
            value = f'1.2.3$(touch "{sentinel}")'
            script = validation.replace('${{ inputs.version }}', value)
            result = subprocess.run(['bash', '-euo', 'pipefail', '-c', script],
                                    cwd=ROOT, env={**os.environ, 'INPUT_VERSION': value,
                                    'GITHUB_OUTPUT': str(Path(temporary) / 'output'),
                                    'GITHUB_ENV': str(Path(temporary) / 'env')},
                                    capture_output=True, text=True, timeout=10)
            self.assertFalse(sentinel.exists(), 'Version input executed as shell code before validation')
            self.assertNotEqual(result.returncode, 0, 'Invalid version was accepted')

    def test_valid_version_produces_only_canonical_output(self):
        with tempfile.TemporaryDirectory() as temporary:
            target = Path(temporary) / 'output'
            result = subprocess.run(['python3', 'scripts/release/guard.py', 'validate-version'],
                                    cwd=ROOT, env={**os.environ, 'INPUT_VERSION': '1.2.3-preview.1',
                                                  'GITHUB_OUTPUT': str(target)},
                                    capture_output=True, text=True, timeout=10)
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertEqual(target.read_text(), 'version=1.2.3-preview.1\n')

    def test_no_expression_interpolation_in_release_shells(self):
        for filename in ('release.yml', 'publish-nuget.yml'):
            content = (ROOT / '.github/workflows' / filename).read_text()
            for block in run_blocks(ROOT / '.github/workflows' / filename):
                self.assertNotIn('${{', block, filename)
            self.assertNotIn('run: >-', content, 'Use literal blocks for auditable shell quoting')

    def test_publish_uses_exact_run_and_never_rebuilds(self):
        content = (ROOT / '.github/workflows/publish-nuget.yml').read_text()
        self.assertIn('run-id:', content)
        self.assertIn('release_run_id:', content)
        for unsafe in ('gh release download', '.[0].tag_name', 'dotnet pack', 'dotnet build', '|| true'):
            self.assertNotIn(unsafe, content)

    def test_release_tag_targets_built_commit(self):
        content = (ROOT / '.github/workflows/release.yml').read_text()
        self.assertIn('--target "$GITHUB_SHA"', content)
        self.assertIn("github.ref == 'refs/heads/main'", content)

    def test_messagepack_security_patch_is_pinned(self):
        packages = ET.parse(ROOT / 'Directory.Packages.props')
        version = packages.find('.//PackageVersion[@Include="MessagePack"]').attrib['Version']
        parts = tuple(int(v) for v in version.split('.'))
        self.assertEqual(parts[0], 2, 'Review a major-version migration explicitly')
        self.assertGreaterEqual(parts, (2, 5, 303))


if __name__ == '__main__':
    unittest.main()
