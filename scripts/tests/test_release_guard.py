"""Pure-data release-source/package tests; never invoke GitHub or publish packages."""
import copy
import importlib.util
from pathlib import Path
import tempfile
import unittest
import zipfile

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location('release_guard', ROOT / 'scripts/release/guard.py')
guard = importlib.util.module_from_spec(spec)
spec.loader.exec_module(guard)


class VersionTests(unittest.TestCase):
    def test_stable_and_prerelease(self):
        for version in ('0.1.0', '10.20.30', '1.0.0-preview.1', '1.0.0-rc-1'):
            with self.subTest(version=version):
                self.assertEqual(guard.validate_version(version), version)

    def test_rejects_shell_and_noncanonical_versions(self):
        for version in ('', 'v1.0.0', '1.2', '01.2.3', '1.0.0-01', '1.0.0-a..b',
                        '1.0.0$(true)', '1.0.0;true', '1.0.0\nVERSION=2.0.0', '1.0.0+metadata',
                        '../1.0.0', '1.0.0 ', '1.0.0-' + 'x' * 200, None):
            with self.subTest(version=version):
                with self.assertRaises(ValueError):
                    guard.validate_version(version)


class SourceTests(unittest.TestCase):
    def setUp(self):
        self.run = {'id': 123, 'status': 'completed', 'conclusion': 'success',
                    'head_branch': 'main', 'event': 'workflow_dispatch',
                    'path': '.github/workflows/release.yml', 'head_sha': 'a' * 40,
                    'head_repository': {'full_name': 'KoalaFacts/Weave'}}
        self.artifact = {'id': 456, 'name': 'nuget-packages-v1.2.3', 'expired': False}
        self.pages = [{'artifacts': [self.artifact]}]
        self.tag = {'ref': 'refs/tags/v1.2.3', 'object': {'type': 'commit', 'sha': 'a' * 40}}
        self.calls = []

    def get(self, path, paginate=False):
        self.calls.append(path)
        if path.endswith('/actions/runs/123'):
            return self.run
        if path.endswith('/actions/runs/123/artifacts?per_page=100'):
            return self.pages
        if path.endswith('/git/ref/tags/v1.2.3'):
            return self.tag
        if path.endswith('/git/tags/' + 'b' * 40):
            return {'object': {'type': 'commit', 'sha': 'a' * 40}}
        raise AssertionError('Unexpected API request: ' + path)

    def resolve(self, version=''):
        return guard.resolve_source('KoalaFacts/Weave', '123', version, self.get)

    def test_exact_successful_run(self):
        self.assertEqual(self.resolve(), {'version': '1.2.3', 'run_id': '123',
                         'artifact_id': '456', 'artifact_name': 'nuget-packages-v1.2.3',
                         'source_sha': 'a' * 40})
        self.assertFalse(any('/releases' in p for p in self.calls))

    def test_explicit_matching_republish(self):
        self.assertEqual(self.resolve('1.2.3')['run_id'], '123')

    def test_annotated_tag(self):
        self.tag['object'] = {'type': 'tag', 'sha': 'b' * 40}
        self.assertEqual(self.resolve()['source_sha'], 'a' * 40)

    def test_rejects_untrusted_or_unsuccessful_runs(self):
        for field, value in (('id', 124), ('status', 'in_progress'), ('conclusion', 'failure'),
                             ('head_branch', 'feature'), ('event', 'pull_request'),
                             ('path', '.github/workflows/evil.yml'), ('head_sha', 'bad'),
                             ('head_repository', {'full_name': 'attacker/Weave'})):
            original = copy.deepcopy(self.run)
            self.run[field] = value
            with self.subTest(field=field):
                with self.assertRaises(ValueError):
                    self.resolve()
            self.run = original

    def test_api_failure_is_not_fallback(self):
        def failed(*args, **kwargs):
            raise RuntimeError('Network failure')
        with self.assertRaises(RuntimeError):
            guard.resolve_source('KoalaFacts/Weave', '123', '', failed)

    def test_bad_repository_and_run_id_rejected_before_api(self):
        for repo, run_id in (('org/repo/../evil', '123'), ('KoalaFacts/Weave', '-1'),
                             ('KoalaFacts/Weave', '123?evil=1'), ('KoalaFacts/Weave', '00123')):
            with self.subTest(repo=repo, run_id=run_id):
                with self.assertRaises(ValueError):
                    guard.resolve_source(repo, run_id, '', self.get)
        self.assertEqual(self.calls, [])

    def test_missing_or_duplicate_artifact(self):
        for artifacts in ([], [self.artifact, copy.deepcopy(self.artifact)]):
            self.pages = [{'artifacts': artifacts}]
            with self.subTest(artifacts=len(artifacts)):
                with self.assertRaises(ValueError):
                    self.resolve()

    def test_expired_and_invalid_artifact(self):
        for field, value in (('expired', True), ('expired', None), ('id', 0),
                             ('name', 'nuget-packages-v1.2.3$(true)')):
            original = copy.deepcopy(self.artifact)
            self.artifact[field] = value
            with self.subTest(field=field):
                with self.assertRaises(ValueError):
                    self.resolve()
            self.artifact.clear()
            self.artifact.update(original)

    def test_pagination_and_unrelated_binary(self):
        self.pages = [{'artifacts': [{'name': 'weave-win-x64'}]}, {'artifacts': [self.artifact]}]
        self.assertEqual(self.resolve()['artifact_id'], '456')

    def test_manual_version_mismatch(self):
        with self.assertRaises(ValueError):
            self.resolve('9.9.9')

    def test_tag_must_match_source_and_name(self):
        for tag in ({'ref': 'refs/tags/v1.2.3', 'object': {'type': 'commit', 'sha': 'c' * 40}},
                    {'ref': 'refs/tags/v9.9.9', 'object': {'type': 'commit', 'sha': 'a' * 40}},
                    {'ref': 'refs/tags/v1.2.3', 'object': {'type': 'blob', 'sha': 'a' * 40}}):
            self.tag = tag
            with self.subTest(tag=tag):
                with self.assertRaises(ValueError):
                    self.resolve()

    def test_cyclic_annotated_tag_is_bounded(self):
        self.tag['object'] = {'type': 'tag', 'sha': 'b' * 40}
        original = self.get
        def cyclic(path, paginate=False):
            if '/git/tags/' in path:
                return {'object': self.tag['object']}
            return original(path, paginate)
        with self.assertRaises(ValueError):
            guard.resolve_source('KoalaFacts/Weave', '123', '', cyclic)


class PackageTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.directory = Path(self.temporary.name)
        self.package = self.directory / 'Weave.Cli.1.2.3.nupkg'
        self.xml = '<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd"><metadata><id>Weave.Cli</id><version>1.2.3</version></metadata></package>'
        self.write(self.package, self.xml)

    def write(self, path, xml):
        with zipfile.ZipFile(path, 'w') as archive:
            archive.writestr('Weave.Cli.nuspec', xml)

    def test_valid_package_and_optional_symbols(self):
        guard.validate_packages(self.directory, '1.2.3')
        self.write(self.directory / 'Weave.Cli.1.2.3.snupkg', self.xml)
        guard.validate_packages(self.directory, '1.2.3')

    def test_missing_main_package(self):
        self.package.unlink()
        with self.assertRaises(ValueError):
            guard.validate_packages(self.directory, '1.2.3')

    def test_wrong_id_version_dtd_and_large_manifest(self):
        for xml in (self.xml.replace('Weave.Cli', 'Other'), self.xml.replace('1.2.3', '9.9.9'),
                    '<!DOCTYPE package []>' + self.xml, self.xml + ' ' * 131073):
            self.write(self.package, xml)
            with self.subTest(kind=xml[:40]):
                with self.assertRaises(ValueError):
                    guard.validate_packages(self.directory, '1.2.3')

    def test_extra_file_rejected(self):
        (self.directory / 'unexpected.nupkg').write_bytes(b'anything')
        with self.assertRaises(ValueError):
            guard.validate_packages(self.directory, '1.2.3')

    def test_symlink_rejected(self):
        self.package.rename(self.directory / 'target')
        try:
            self.package.symlink_to(self.directory / 'target')
        except OSError as error:
            if getattr(error, 'winerror', None) == 1314:
                self.skipTest('Windows symlink privilege is unavailable')
            raise
        with self.assertRaises(ValueError):
            guard.validate_packages(self.directory, '1.2.3')

    def test_duplicate_manifest_rejected(self):
        with zipfile.ZipFile(self.package, 'a') as archive:
            archive.writestr('Other.nuspec', self.xml)
        with self.assertRaises(ValueError):
            guard.validate_packages(self.directory, '1.2.3')

    def test_invalid_zip_rejected(self):
        self.package.write_bytes(b'not a package')
        with self.assertRaises(ValueError):
            guard.validate_packages(self.directory, '1.2.3')


if __name__ == '__main__':
    unittest.main()
