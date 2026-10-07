"""Exercise the workflow's actual script-file transport without extra packages."""
import errno
import importlib.util
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile
import textwrap
import unittest

ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location('license_evidence', ROOT / 'scripts/check_dependency_license_evidence.py')
guard = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(guard)
NODE = shutil.which('node')
ENTRY = {'change_type': 'added', 'name': 'fixture-' + 'x' * 750, 'version': '1.0.0',
         'manifest': 'fixture.lock', 'package_url': 'pkg:nuget/fixture@1.0.0', 'license': 'MIT'}
LARGE_CHANGES = json.dumps([{**ENTRY, 'name': ENTRY['name'] + str(index)} for index in range(199)])
LARGE_INVALID = json.dumps({'unlicensed': [], 'unresolved': [],
                            'forbidden': [{**ENTRY, 'license': 'GPL-3.0'} for _ in range(199)]})


class DependencyPolicyTransportTests(unittest.TestCase):
    def test_acceptance_runs_the_real_file_policy_regressions(self):
        workflow = (ROOT / '.github/workflows/dependency-policy-acceptance.yml').read_text()
        self.assertIn('npm test --prefix tools/dependency-license-policy', workflow)
        acceptance = (ROOT / 'scripts/acceptance/dependency_policy.py').read_text()
        self.assertNotIn("'DEPENDENCY_CHANGES':", acceptance)
        self.assertNotIn("'INVALID_LICENSE_CHANGES':", acceptance)
        for name in ('large-permitted-delta', 'prohibited-large-final-change', 'unknown-large-final-change'):
            self.assertIn(name, acceptance)

    def transport_script(self):
        review = (ROOT / '.github/workflows/ci.yml').read_text().split('  dependency-review:\n', 1)[1]
        match = re.search(r'      - name: Materialize dependency policy outputs\n(.*?)(?=\n      - name:|\Z)',
                          review, re.DOTALL)
        self.assertIsNotNone(match, 'Large action outputs need a script-file transport step, not environment entries.')
        step = match.group(1)
        self.assertIn('shell: node {0}', step)
        self.assertIn('        run: |\n', step)
        self.assertNotIn('DEPENDENCY_CHANGES:', review)
        self.assertNotIn('INVALID_LICENSE_CHANGES:', review)
        self.assertLess(review.index('Set up Node for license policy'), match.start())
        self.assertLess(match.start(), review.index('Require complete license evidence'))
        return textwrap.dedent(step.split('        run: |\n', 1)[1])

    def materialize(self, directory, changes, invalid):
        script = self.transport_script()
        for name, payload in (('dependency-changes', changes), ('invalid-license-changes', invalid)):
            expression = "${{ toJSON(steps.dependency-policy.outputs['" + name + "']) }}"
            self.assertIn(expression, script)
            # JSON string literals model GitHub's toJSON output, including quotes and line escapes.
            script = script.replace(expression, json.dumps(payload, ensure_ascii=True))
        self.assertNotIn('${{', script)
        script_path = directory / 'runner-script'
        script_path.write_text(script, encoding='utf-8')
        env = {key: os.environ[key] for key in ('PATH', 'SYSTEMROOT', 'SystemDrive', 'LANG')
               if key in os.environ}
        env['RUNNER_TEMP'] = str(directory)
        result = subprocess.run([NODE, str(script_path)], cwd=directory, env=env, capture_output=True, timeout=10)
        self.assertEqual(result.returncode, 0, result.stderr.decode())
        self.assertEqual(result.stdout, b'')
        return directory / 'dependency-changes.json', directory / 'invalid-license-changes.json'

    @unittest.skipUnless(NODE, 'Node is required for the real workflow transport boundary')
    def test_large_outputs_are_preserved_byte_for_byte_in_files(self):
        self.assertGreater(len(LARGE_CHANGES.encode()), 128 * 1024)
        self.assertGreater(len(LARGE_INVALID.encode()), 128 * 1024)
        with tempfile.TemporaryDirectory() as root:
            changes, invalid = self.materialize(Path(root), LARGE_CHANGES, LARGE_INVALID)
            self.assertEqual(changes.read_bytes(), LARGE_CHANGES.encode())
            self.assertEqual(invalid.read_bytes(), LARGE_INVALID.encode())
            self.assertEqual(len(json.loads(changes.read_bytes())), 199)
            report = Path(root) / 'report.json'
            result = subprocess.run([sys.executable, str(ROOT / 'scripts/check_dependency_license_evidence.py'),
                                     '--changes', str(invalid), '--report', str(report)],
                                    capture_output=True, timeout=10)
            self.assertEqual(result.returncode, 1, result.stderr.decode())
            self.assertEqual(json.loads(report.read_bytes()), {
                'status': 'blocked', 'failure_code': 'license-policy-denied',
                'unlicensed': 0, 'unresolved': 0, 'forbidden': 199})

    @unittest.skipUnless(NODE and sys.platform.startswith('linux'), 'Linux execve boundary regression')
    def test_old_large_environment_transport_cannot_launch_the_process(self):
        with self.assertRaises(OSError) as caught:
            subprocess.run([NODE, '-e', 'process.exit(0)'],
                           env={'DEPENDENCY_CHANGES': LARGE_CHANGES}, capture_output=True, timeout=10)
        self.assertEqual(caught.exception.errno, errno.E2BIG)

    @unittest.skipUnless(NODE, 'Node is required for the real workflow transport boundary')
    def test_untrusted_quotes_commands_and_unicode_remain_data(self):
        hostile = '"; require("node:fs").writeFileSync("injected", "bad"); //\n$(touch injected)\n`touch injected`\nEOF\n\\\r\t\u2028\u2029雪'
        changes_payload = json.dumps([{**ENTRY, 'name': hostile}], ensure_ascii=False)
        invalid_payload = json.dumps({'unlicensed': [{**ENTRY, 'name': hostile}],
                                      'unresolved': [], 'forbidden': []}, ensure_ascii=False)
        with tempfile.TemporaryDirectory() as root:
            directory = Path(root)
            changes, invalid = self.materialize(directory, changes_payload, invalid_payload)
            self.assertEqual(changes.read_bytes(), changes_payload.encode())
            self.assertEqual(invalid.read_bytes(), invalid_payload.encode())
            self.assertFalse((directory / 'injected').exists())

    @unittest.skipUnless(NODE, 'Node is required for the real workflow transport boundary')
    def test_missing_action_outputs_remain_empty_and_fail_closed(self):
        with tempfile.TemporaryDirectory() as root:
            changes, invalid = self.materialize(Path(root), '', '')
            self.assertEqual(changes.read_bytes(), b'')
            self.assertEqual(invalid.read_bytes(), b'')
            self.assertEqual(guard.inspect_licenses(invalid.read_text())['failure_code'],
                             'invalid-license-evidence')


if __name__ == '__main__':
    unittest.main()
