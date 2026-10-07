"""Bounded baseline diagnostics; these tests never execute a coverage collector."""
import importlib.util
import json
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / 'scripts/collect_base_coverage.py'
BASE = 'a7983cc73e05d9d0bb6afb147ab30396b74e6fde'
BEFORE = 'c8bd6e0df89160e9b74826edca9ac258d6e467f4'


class BaselineWorkflowTests(unittest.TestCase):
    def test_job_is_bounded_to_one_pr_transition_and_read_only(self):
        workflow = (ROOT / '.github/workflows/ci.yml').read_text()
        self.assertIn('  exact-base-coverage-diagnostic:\n', workflow)
        job = workflow.split('  exact-base-coverage-diagnostic:\n')[1].split('\n  code-quality:')[0]
        for required in ("github.event_name == 'pull_request'", "github.event.action == 'synchronize'",
                         'github.event.number == 190', f"github.event.before == '{BEFORE}'",
                         f"github.event.pull_request.base.sha == '{BASE}'", 'contents: read',
                         'timeout-minutes: 30', 'if: always()', 'persist-credentials: false',
                         'path: baseline', 'path: diagnostic-tools', 'collect_base_coverage.py',
                         'uses: ./diagnostic-tools/.github/actions/setup-dotnet', "node-version: '24'"):
            self.assertIn(required, job)
        for forbidden in ('write', 'secrets.', 'GH_TOKEN', 'continue-on-error', '--threshold', 'pull_request_target'):
            self.assertNotIn(forbidden, job)
        self.assertIn(f'ref: {BASE}', job)
        self.assertIn('github.event.pull_request.head.sha', job)


class BaselineCollectorTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        if not SCRIPT.exists():
            raise AssertionError('The isolated baseline collector is missing.')
        spec = importlib.util.spec_from_file_location('base_coverage', SCRIPT)
        cls.module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(cls.module)

    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name) / 'base'
        self.root.mkdir()
        self.output = Path(temporary.name) / 'evidence'
        self.output.mkdir()
        self.projects = []
        for suite in self.module.EXPECTED_SUITES:
            project = f'tests/{suite}/{suite}.csproj'
            path = self.root / project
            path.parent.mkdir(parents=True)
            path.write_text('<Project />')
            self.projects.append(project)
        (self.root / 'Weave.slnx').write_text('<Solution>' + ''.join(
            f'<Project Path="{project}" />' for project in self.projects) + '</Solution>')
        (self.root / '.config').mkdir()
        (self.root / '.config/dotnet-tools.json').write_text(json.dumps({
            'tools': {'dotnet-coverage': {'version': '18.11.2', 'rollForward': False}}}))

    def test_discovery_requires_all_nine_base_suites(self):
        self.assertEqual(sorted(self.projects), self.module.test_projects(self.root))
        Path(self.root / self.projects[0]).unlink()
        with self.assertRaisesRegex(ValueError, 'suite inventory'):
            self.module.test_projects(self.root)

    def test_solution_cannot_silently_omit_a_base_suite(self):
        solution = self.root / 'Weave.slnx'
        solution.write_text(solution.read_text().replace(f'<Project Path="{self.projects[0]}" />', ''))
        with self.assertRaisesRegex(ValueError, 'solution'):
            self.module.test_projects(self.root)

    def test_collection_matches_gate_flags_and_continues_after_failure(self):
        records = []
        observed = []

        def simulated_run(root, output, name, arguments, evidence):
            observed.append((root, name, arguments))
            return 7 if len(observed) == 1 else 0

        with patch.object(self.module, 'run_logged', side_effect=simulated_run):
            result = self.module.collect_suites(self.root, self.output, self.projects, records)
        self.assertFalse(result)
        self.assertEqual(9, len(observed))
        for root, suite, arguments in observed:
            project = f'tests/{suite}/{suite}.csproj'
            self.assertEqual(self.root, root)
            self.assertEqual(['dotnet', 'dotnet-coverage', 'collect', '-f', 'cobertura', '-o',
                              str(self.output / f'{suite}.cobertura.xml'), '-l',
                              str(self.output / f'{suite}.collector.log'), '--', 'dotnet', 'test',
                              '--project', project, '--no-build', '--no-restore', '-c', 'Release'], arguments)

    def test_logged_failure_preserves_exit_code_arguments_and_output(self):
        def simulated_process(arguments, **kwargs):
            kwargs['stdout'].write('Synthetic test failure\n')
            return subprocess.CompletedProcess(arguments, 8)

        records = []
        with patch.object(self.module.subprocess, 'run', side_effect=simulated_process):
            code = self.module.run_logged(self.root, self.output, 'failing-suite', ['fixture-only'], records)
        self.assertEqual(8, code)
        self.assertEqual(8, records[0]['exit_code'])
        self.assertEqual(['fixture-only'], records[0]['arguments'])
        self.assertIn('Synthetic test failure', (self.output / 'failing-suite.command.log').read_text())
        self.assertEqual(records, json.loads((self.output / 'commands.json').read_text()))

    def test_binary_snapshot_detects_changes_and_excludes_debug(self):
        release = self.root / 'tests/Weave.Cli.Tests/bin/Release/net10.0'
        release.mkdir(parents=True)
        binary = release / 'weave.dll'
        binary.write_bytes(b'base-binary')
        debug = self.root / 'tests/Weave.Cli.Tests/bin/Debug'
        debug.mkdir(parents=True)
        (debug / 'weave.dll').write_bytes(b'debug')
        before = self.module.binary_hashes(self.root)
        self.assertEqual([binary.relative_to(self.root).as_posix()], list(before))
        binary.write_bytes(b'contaminated-binary')
        self.assertNotEqual(before, self.module.binary_hashes(self.root))

    def test_deleted_tracked_source_is_retained_as_missing_evidence(self):
        with patch.object(self.module, 'git_output', return_value='deleted.cs\0Weave.slnx\0'):
            hashes = self.module.source_hashes(self.root)
        self.assertIsNone(hashes['deleted.cs'])
        self.assertEqual(self.module.sha256(self.root / 'Weave.slnx'), hashes['Weave.slnx'])

    def test_apphost_and_native_library_mutation_invalidates_binary_identity(self):
        release = self.root / 'tests/Weave.Cli.Tests/bin/Release/net10.0'
        release.mkdir(parents=True)
        for filename in ('Weave.Cli.Tests', 'libe_sqlite3.so'):
            with self.subTest(filename=filename):
                executable = release / filename
                executable.write_bytes(b'baseline-executable')
                before = self.module.binary_hashes(self.root)
                self.assertIn(executable.relative_to(self.root).as_posix(), before)
                executable.write_bytes(b'changed-executable')
                self.assertNotEqual(before, self.module.binary_hashes(self.root))

    def test_wrong_revision_stops_before_any_dotnet_command_and_retains_identity(self):
        with patch.object(self.module, 'git_output', return_value='0' * 40), \
                patch.object(self.module, 'run_logged') as runner:
            with self.assertRaisesRegex(ValueError, 'exact approved base'):
                self.module.run(self.root, self.output)
        runner.assert_not_called()
        evidence = json.loads((self.output / 'identity.json').read_text())
        self.assertEqual(BASE, evidence['expected_base_sha'])
        self.assertEqual('0' * 40, evidence['actual_base_sha'])
        self.assertTrue(evidence['diagnostic_only'])

    def test_evidence_cannot_be_written_into_base_checkout(self):
        with self.assertRaisesRegex(ValueError, 'outside'):
            self.module.run(self.root, self.root / 'TestResults')

    def test_zero_exit_without_raw_report_is_not_successful_collection(self):
        with patch.object(self.module, 'run_logged', return_value=0):
            self.assertFalse(self.module.collect_suites(self.root, self.output, self.projects, []))

    def test_success_requires_all_nine_fresh_reports(self):
        def simulated_run(root, output, name, arguments, records):
            (output / f'{name}.cobertura.xml').write_text('<coverage />')
            return 0

        with patch.object(self.module, 'run_logged', side_effect=simulated_run):
            self.assertTrue(self.module.collect_suites(self.root, self.output, self.projects, []))

    def test_restore_failure_retains_source_and_partial_binary_identity(self):
        with patch.object(self.module, 'git_output', side_effect=[BASE, '', '']), \
                patch.object(self.module, 'source_hashes', return_value={'src/Fixture.cs': 'source-hash'}), \
                patch.object(self.module, 'run_logged', side_effect=[0, 3]):
            with self.assertRaisesRegex(RuntimeError, 'restore failed'):
                self.module.run(self.root, self.output)
        self.assertEqual({'src/Fixture.cs': 'source-hash'},
                         json.loads((self.output / 'source-hashes-after.json').read_text()))
        self.assertTrue((self.output / 'binary-hashes-after.json').exists())

    def test_changed_sources_during_collection_are_rejected(self):
        with patch.object(self.module, 'git_output', side_effect=[BASE, '', ' M source.cs']), \
                patch.object(self.module, 'source_hashes', side_effect=[{'source': 'old'}, {'source': 'old'}, {'source': 'new'}]), \
                patch.object(self.module, 'binary_hashes', return_value={'base.dll': 'unchanged'}), \
                patch.object(self.module, 'run_logged', return_value=0), \
                patch.object(self.module, 'collect_suites', return_value=True):
            with self.assertRaisesRegex(ValueError, 'tracked inputs changed'):
                self.module.run(self.root, self.output)
        identity = json.loads((self.output / 'identity.json').read_text())
        self.assertFalse(identity['sources_unchanged'])


if __name__ == '__main__':
    unittest.main()
