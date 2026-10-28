"""Release smoke boundaries; synthetic packages/processes, no SDK build or live model."""
import contextlib
import http.server
import importlib.util
import io
import os
from pathlib import Path
import sys
import tempfile
import threading
import time
import unittest
from unittest.mock import patch
import xml.etree.ElementTree as ET
import zipfile

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location('release_smoke', ROOT / 'scripts/release/smoke.py')
smoke = importlib.util.module_from_spec(spec)
spec.loader.exec_module(smoke)
VERSION = '0.1.0-preview.1'


class InstallationTests(unittest.TestCase):
    def package(self, directory, package_id=smoke.PACKAGE_ID, version=VERSION):
        package = directory / f'{smoke.PACKAGE_ID}.{VERSION}.nupkg'
        with zipfile.ZipFile(package, 'w') as archive:
            archive.writestr('tool.nuspec', f'<package><metadata><id>{package_id}</id>'
                              f'<version>{version}</version></metadata></package>')
        return package

    def test_install_uses_only_copied_artifact_and_private_caches(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            package = self.package(root)
            with patch.dict(os.environ, {'WEAVE_SILO_PATH': 'untrusted-source-host',
                                         'NUGET_PACKAGES': 'existing-cache', 'NUGET_PLUGIN_PATHS': 'existing-plugin',
                                         'WEAVE_OPERATOR_KEY': 'sensitive', 'HTTP_PROXY': 'http://proxy.invalid',
                                         'ProgramFiles(x86)': 'machine-program-files'}):
                env = smoke.isolated_environment(root)
            calls = []

            def runner(command, cwd, environment, **_):
                calls.append(command)
                self.assertEqual(cwd, root)
                self.assertEqual(environment, env)
                if 'install' in command:
                    tools = Path(command[command.index('--tool-path') + 1])
                    tools.mkdir()
                    (tools / ('weave.exe' if os.name == 'nt' else 'weave')).touch()
                    return b''
                return (VERSION + '+abcdef\n').encode()

            with patch.object(smoke.shutil, 'which', return_value=str(root / 'dotnet')):
                smoke.install_tool(package, VERSION, root, env, runner)
            self.assertEqual(calls[0][1:4], ['tool', 'install', smoke.PACKAGE_ID])
            self.assertIn('--no-cache', calls[0])
            self.assertNotIn('--add-source', calls[0])
            self.assertEqual(calls[0][calls[0].index('--version') + 1], VERSION)
            self.assertEqual(list((root / 'feed').iterdir()), [root / 'feed' / package.name])
            self.assertEqual((root / 'feed' / package.name).read_bytes(), package.read_bytes())
            config = ET.parse(root / 'NuGet.Config').getroot()
            for section in ('packageSources', 'auditSources'):
                self.assertEqual([node.tag for node in config.find(section)], ['clear', 'add'])
                self.assertEqual(config.find(section + '/add').attrib['value'], str(root / 'feed'))
            self.assertIsNotNone(config.find('fallbackPackageFolders/clear'))
            for name in ('WEAVE_SILO_PATH', 'WEAVE_OPERATOR_KEY', 'HTTP_PROXY', 'NUGET_PLUGIN_PATHS'):
                self.assertNotIn(name, env)
            for name in ('HOME', 'USERPROFILE', 'DOTNET_CLI_HOME', 'NUGET_PACKAGES', 'NUGET_HTTP_CACHE_PATH'):
                self.assertTrue(Path(env[name]).is_relative_to(root))
            self.assertIn('machine-program-files', env.values())

    def test_wrong_package_identity_or_manifest_version_fails_before_install(self):
        for package_id, version in (('Weave.Cli', VERSION), (smoke.PACKAGE_ID, '0.0.1')):
            with self.subTest(package_id=package_id, version=version), tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary)
                package = self.package(root, package_id, version)
                with self.assertRaises(ValueError):
                    smoke.install_tool(package, VERSION, root, {}, lambda *_: self.fail('install must not run'))

    def test_version_requires_entire_exact_value(self):
        smoke.check_version(VERSION.encode(), VERSION)
        smoke.check_version((VERSION + '+build.123\n').encode(), VERSION)
        for value in ('1.0.0', VERSION + '.1', 'version ' + VERSION, VERSION + '\nextra'):
            with self.subTest(value=value), self.assertRaises(smoke.SmokeError):
                smoke.check_version(value.encode(), VERSION)

    def test_distribution_requires_native_host_without_source_fallback(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            suffix = '.exe' if os.name == 'nt' else ''
            (root / ('weave' + suffix)).touch()
            (root / 'host').mkdir()
            (root / 'host/Weave.Silo.dll').touch()
            with patch.dict(os.environ, {'WEAVE_SILO_PATH': 'source-host'}), self.assertRaises(smoke.SmokeError):
                smoke.distribution_paths(root)
            host = root / 'host' / ('Weave.Silo' + suffix)
            host.touch()
            self.assertEqual(smoke.distribution_paths(root), (root.resolve() / ('weave' + suffix), host.resolve()))


class ProtocolReplay:
    """Replay real endpoint field shapes, recording exact request identity and authority."""
    def __init__(self, documents, mutation=None):
        self.documents = documents
        self.mutation = mutation
        self.write = None
        self.calls = []
        self.digest = 'approval-v1:' + 'A' * 64

    def call(self, method, path, status, **options):
        self.calls.append((method, path, status, options))
        if path.endswith('/issue'):
            return None if status == 401 else ('review-capability' if '/reviewer/' in path else 'agent-capability')
        if path.endswith('/approval/review'):
            if status == 403:
                return {'errorCode': 'forbidden'}
            self.assert_authority(options, 'review-capability')
            result = {'invocationId': self.write['invocationId'], 'workspaceId': 'release-smoke', 'toolName': 'files',
                      'operation': 'write_file', 'rawInput': self.write['rawInput'], 'parameters': self.write['parameters'],
                      'planDigest': self.digest}
            stage = 'preview'
        elif path.endswith('/decision'):
            assert not path.endswith('/approval/decision'), 'Stored proposal decisions use the UUID /decision endpoint.'
            self.assert_authority(options, 'review-capability')
            assert options['body'] == {'decision': 'approve', 'planDigest': self.digest}
            result = {'invocationId': self.write['invocationId'], 'approvalState': 'Approved'}
            stage = 'approval'
        elif path.endswith('/resume'):
            assert options['capability'] == 'agent-capability' and path.split('/')[-2] == self.write['invocationId']
            (self.documents / 'result.md').write_bytes(b'\xef\xbb\xbf' + self.write['rawInput'].encode('utf-8'))
            result = self.success()
            stage = 'executed'
        elif path.endswith('/approval'):
            result = {'invocationId': self.write['invocationId'], 'approvalState': 'Consumed'}
            stage = 'consumed'
        elif method == 'GET':
            result = self.success()
            stage = 'retained'
        elif options['body']['method'] == 'read_file':
            assert options['capability'] == 'agent-capability'
            return {'success': True, 'output': (self.documents / options['body']['parameters']['path']).read_text(encoding='utf-8-sig')}
        elif self.write is None:
            self.write = options['body']
            result = {'invocationId': self.write['invocationId'], 'success': False, 'errorCode': 'approval-pending',
                      'approvalState': 'Pending', 'outcome': 'NotDispatched', 'outcomeRecorded': False,
                      'isReplay': False, 'approvalPlanDigest': self.digest}
            stage = 'pending'
        else:
            assert options['body'] == self.write
            result = {**self.success(), 'isReplay': True}
            stage = 'replay'
        if self.mutation:
            self.mutation(stage, result)
        return result

    def success(self):
        return {'invocationId': self.write['invocationId'], 'success': True, 'outcome': 'Succeeded',
                'outcomeRecorded': True, 'attemptId': 'fixed-attempt-id'}

    def assert_authority(self, options, capability):
        assert options['capability'] == capability and options['operator'] == 'test-operator'


class ProtocolTests(unittest.TestCase):
    def setup_documents(self, root):
        (root / 'source.txt').write_bytes(b'Release artifact smoke input.\n')
        (root / 'result.md').write_bytes(b'Original.\n')

    def test_round_trip_uses_independent_review_and_same_uuid_for_execution_and_replay(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            self.setup_documents(root)
            api = ProtocolReplay(root)
            smoke.governed_round_trip(api, 'test-operator', root)
            resumes = [call for call in api.calls if call[1].endswith('/resume')]
            self.assertEqual(len(resumes), 1)
            writes = [call[3]['body'] for call in api.calls if call[3].get('body', {}).get('method') == 'write_file']
            self.assertEqual(len(writes), 2)
            self.assertEqual(writes[0], writes[1])
            decisions = [call for call in api.calls if call[1].endswith('/decision')]
            self.assertEqual(decisions[0][1], '/api/workspaces/release-smoke/tools/files/invocations/'
                             + writes[0]['invocationId'] + '/decision')

    def test_unconfirmed_evidence_fails_without_blind_resume_or_new_uuid(self):
        cases = [('pending', 'outcome', 'OutcomeUnknown'), ('pending', 'outcomeRecorded', True),
                 ('pending', 'approvalState', 'Approved'), ('pending', 'invocationId', 'other'),
                 ('preview', 'rawInput', 'changed'), ('preview', 'planDigest', 'approval-v1:' + 'B' * 64),
                 ('preview', 'workspaceId', 'other'), ('approval', 'approvalState', 'Rejected'),
                 ('executed', 'outcomeRecorded', False), ('executed', 'invocationId', 'other'),
                 ('retained', 'attemptId', 'other-attempt'), ('consumed', 'approvalState', 'Approved'),
                 ('replay', 'isReplay', False)]
        for stage, key, value in cases:
            with self.subTest(stage=stage, key=key), tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary)
                self.setup_documents(root)
                def mutate(current, result):
                    if current == stage:
                        result[key] = value
                api = ProtocolReplay(root, mutate)
                with self.assertRaises(smoke.SmokeError):
                    smoke.governed_round_trip(api, 'test-operator', root)
                resumes = [call for call in api.calls if call[1].endswith('/resume')]
                self.assertLessEqual(len(resumes), 1)
                if stage in ('pending', 'preview', 'approval'):
                    self.assertEqual(resumes, [])
                write_ids = {call[3]['body']['invocationId'] for call in api.calls
                             if call[3].get('body', {}).get('method') == 'write_file'}
                self.assertEqual(len(write_ids), 1)

    def test_http_redirect_is_never_followed_and_body_is_not_reported(self):
        paths = []
        class Handler(http.server.BaseHTTPRequestHandler):
            def do_POST(self):
                paths.append(self.path)
                self.send_response(302)
                self.send_header('Location', '/api/other')
                self.end_headers()
                self.wfile.write(b'private-token-in-response')
            def log_message(self, *_):
                pass
        with http.server.HTTPServer(('127.0.0.1', 0), Handler) as server:
            thread = threading.Thread(target=server.serve_forever, daemon=True)
            thread.start()
            try:
                with self.assertRaises(smoke.SmokeError) as error:
                    smoke.LocalApi(server.server_port).call('POST', '/api/write', 202, capability='private-token')
                self.assertNotIn('private-token', str(error.exception))
                self.assertEqual(paths, ['/api/write'])
            finally:
                server.shutdown()
                thread.join()

    def test_workspace_readiness_accepts_array_without_weakening_invocation_object_contract(self):
        class Handler(http.server.BaseHTTPRequestHandler):
            def do_GET(self):
                self.send_response(200)
                self.send_header('Content-Type', 'application/json')
                self.end_headers()
                self.wfile.write(b'[]')
            def log_message(self, *_):
                pass
        with http.server.HTTPServer(('127.0.0.1', 0), Handler) as server:
            thread = threading.Thread(target=server.serve_forever, daemon=True)
            thread.start()
            try:
                api = smoke.LocalApi(server.server_port)
                self.assertIsNone(api.call('GET', '/api/workspaces/', 200, status_only=True))
                with self.assertRaises(smoke.SmokeError):
                    api.call('GET', '/api/invocation', 200)
            finally:
                server.shutdown()
                thread.join()


class ProcessTests(unittest.TestCase):
    def test_process_output_and_timeout_are_bounded_and_children_stop(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            env = smoke.isolated_environment(root)
            marker = root / 'escaped.txt'
            ready = root / 'child-started.txt'
            if os.name == 'nt':
                powershell = Path(os.environ['SystemRoot']) / 'System32/WindowsPowerShell/v1.0/powershell.exe'
                child_script = root / 'child.ps1'
                child_script.write_text('param($ready, $escaped)\n[IO.File]::WriteAllText($ready, "started")\n'
                                        'Start-Sleep -Milliseconds 1200\n[IO.File]::WriteAllText($escaped, "escaped")', encoding='utf-8')
                parent_script = root / 'parent.ps1'
                parent_script.write_text('param($child, $ready, $escaped)\n'
                                         '& "$PSHOME/powershell.exe" -NoProfile -File $child $ready $escaped', encoding='utf-8')
                command = [str(powershell), '-NoProfile', '-File', str(parent_script), str(child_script), str(ready), str(marker)]
                sleeper = [str(powershell), '-NoProfile', '-Command', 'Start-Sleep -Seconds 30']
                noisy = [str(powershell), '-NoProfile', '-Command', '[Console]::WriteLine(("x" * 10000))']
            else:
                code = ('import pathlib,sys,time; pathlib.Path(sys.argv[2]).touch(); '
                        'time.sleep(1.2); pathlib.Path(sys.argv[1]).touch()')
                parent = ('import subprocess,sys,time; '
                          'subprocess.Popen([sys.executable,"-c",sys.argv[1],sys.argv[2],sys.argv[3]]); time.sleep(30)')
                command = [sys.executable, '-c', parent, code, str(marker), str(ready)]
                sleeper = [sys.executable, '-c', 'import time; time.sleep(30)']
                noisy = [sys.executable, '-c', 'print("x"*10000)']
            with smoke.OwnedProcess(command, root, env, timeout=15) as child:
                deadline = time.monotonic() + 10
                while not ready.exists() and time.monotonic() < deadline:
                    child.check()
                    time.sleep(0.02)
                self.assertTrue(ready.exists(), 'Descendant must actually start before cleanup is tested.')
            time.sleep(1.3)
            self.assertFalse(marker.exists())
            with self.assertRaises(smoke.SmokeError):
                smoke.run(sleeper, root, env, timeout=0.2)
            with patch.object(smoke, 'MAX_OUTPUT', 128), self.assertRaises(smoke.SmokeError):
                smoke.run(noisy, root, env)

    @unittest.skipUnless(os.name == 'nt', 'Windows suspended native process boundary')
    def test_failed_job_assignment_never_runs_suspended_native_command(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            env = smoke.isolated_environment(root)
            original = smoke.windows_job
            def rejected_job():
                kernel, job = original()
                kernel.AssignProcessToJobObject = lambda *_: False
                return kernel, job
            command = [os.environ['COMSPEC'], '/d', '/c', 'echo escaped>escaped.txt']
            with patch.object(smoke, 'windows_job', rejected_job), self.assertRaises(smoke.SmokeError):
                smoke.run(command, root, env)
            self.assertFalse((root / 'escaped.txt').exists())

    def test_main_returns_failure_without_private_diagnostics(self):
        output = io.StringIO()
        with patch.object(smoke, 'install_tool', side_effect=OSError('private-token')), contextlib.redirect_stderr(output):
            result = smoke.main(['--package', 'synthetic.nupkg', '--version', VERSION])
        self.assertEqual(result, 1)
        self.assertNotIn('private-token', output.getvalue())

    def test_child_failure_names_the_stage_without_output(self):
        def fail(*args, **kwargs):
            raise smoke.SmokeError('Release command failed; child diagnostics were withheld.')
        with self.assertRaisesRegex(smoke.SmokeError, '^NuGet tool installation:'):
            smoke.run_stage('NuGet tool installation', [], None, {}, fail)


if __name__ == '__main__':
    unittest.main()
