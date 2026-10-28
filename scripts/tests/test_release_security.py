"""Regression tests execute workflow scripts with harmless local sentinel input."""
import os
from pathlib import Path
import re
import shutil
import subprocess
import tempfile
import unittest
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]


def run_blocks(path):
    """Read literal YAML run blocks without adding a CI dependency."""
    lines = path.read_text(encoding='utf-8').splitlines()
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
        shell = shutil.which('sh' if os.name == 'nt' else 'bash')
        if shell is None:
            self.skipTest('A POSIX shell is unavailable')
        with tempfile.TemporaryDirectory() as temporary:
            sentinel = Path(temporary) / 'executed'
            value = f'1.2.3$(touch "{sentinel}")'
            script = validation.replace('${{ inputs.version }}', value)
            result = subprocess.run([shell, '-euo', 'pipefail', '-c', script],
                                    cwd=ROOT, env={**os.environ, 'INPUT_VERSION': value,
                                    'GITHUB_OUTPUT': str(Path(temporary) / 'output'),
                                    'GITHUB_ENV': str(Path(temporary) / 'env')},
                                    capture_output=True, text=True, timeout=30)
            self.assertFalse(sentinel.exists(), 'Version input executed as shell code before validation')
            self.assertEqual(result.returncode, 1, result.stderr)
            self.assertIn('Release validation failed (ValueError).', result.stderr)

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

    def test_only_publish_job_can_request_oidc_credentials(self):
        content = (ROOT / '.github/workflows/publish-nuget.yml').read_text()
        before_publish, publish = content.split('  publish:', 1)
        self.assertNotIn('id-token:', before_publish)
        permissions = publish.split('    permissions:', 1)[1].split('    environment:', 1)[0]
        self.assertEqual(dict(re.findall(r'^      ([\w-]+): (\w+)$', permissions, re.MULTILINE)),
                         {'contents': 'read', 'actions': 'read', 'id-token': 'write'})
        self.assertEqual(content.count('id-token: write'), 1)
        self.assertIn('      name: nuget', publish)

    def test_trusted_publishing_follows_package_validation_without_long_lived_key(self):
        content = (ROOT / '.github/workflows/publish-nuget.yml').read_text()
        login = content.split('      - name: Authenticate to NuGet with OIDC', 1)[1].split('      - name:', 1)[0]
        self.assertIn('uses: NuGet/login@8d196754b4036150537f80ac539e15c2f1028841 # v1.2.0', login)
        self.assertIn('id: nuget-login', login)
        self.assertIn('user: ${{ vars.NUGET_USER }}', login)
        self.assertLess(content.index('guard.py validate-packages'), content.index('Authenticate to NuGet with OIDC'))
        self.assertLess(content.index('Validate NuGet profile configuration'), content.index('Authenticate to NuGet with OIDC'))
        push = content.split('      - name: Publish to NuGet.org', 1)[1].split('      - name:', 1)[0]
        self.assertIn('NUGET_PUBLISH_TOKEN: ${{ steps.nuget-login.outputs.NUGET_API_KEY }}', push)
        self.assertIn('--api-key "$NUGET_PUBLISH_TOKEN"', push)
        self.assertNotIn('secrets.', content)
        self.assertNotIn('GITHUB_ENV', content)
        self.assertNotIn('nuget setApiKey', content)
        self.assertNotIn('nuget add source', content)
        self.assertNotIn('continue-on-error', content)

    def test_nuget_profile_configuration_fails_closed_without_echoing_input(self):
        block = next(b for b in run_blocks(ROOT / '.github/workflows/publish-nuget.yml') if 'NUGET_USER' in b)
        shell = shutil.which('sh' if os.name == 'nt' else 'bash')
        if shell is None:
            self.skipTest('A POSIX shell is unavailable')
        for value, accepted in (('', False), (' \t\n', False), ('private@example.invalid', False),
                                ('public-profile', True)):
            with self.subTest(accepted=accepted):
                result = subprocess.run([shell, '-euo', 'pipefail', '-c', block],
                                        env={**os.environ, 'NUGET_USER': value},
                                        capture_output=True, text=True, timeout=10)
                self.assertEqual(result.returncode, 0 if accepted else 1, result.stderr)
                if value.strip():
                    self.assertNotIn(value, result.stdout + result.stderr)
                if not accepted:
                    self.assertIn('::error::', result.stdout + result.stderr)

    def test_publish_shell_uses_only_the_provided_ephemeral_token(self):
        block = next(b for b in run_blocks(ROOT / '.github/workflows/publish-nuget.yml') if 'dotnet nuget push' in b)
        shell = shutil.which('sh' if os.name == 'nt' else 'bash')
        if shell is None:
            self.skipTest('A POSIX shell is unavailable')
        for token in ('', 'synthetic-ephemeral-token'):
            with self.subTest(has_token=bool(token)), tempfile.TemporaryDirectory() as temporary:
                packages = Path(temporary) / 'packages'
                packages.mkdir()
                (packages / 'synthetic.nupkg').touch()
                capture = Path(temporary) / 'arguments'
                script = 'dotnet() { printf "%s\\n" "$@" > "$CAPTURE"; };\n' + block
                result = subprocess.run([shell, '-euo', 'pipefail', '-c', script], cwd=temporary,
                                        env={**os.environ, 'NUGET_PUBLISH_TOKEN': token,
                                             'NUGET_API_KEY': 'synthetic-long-lived-fallback', 'CAPTURE': str(capture)},
                                        capture_output=True, text=True, timeout=10)
                self.assertEqual(result.returncode, 0 if token else 1, result.stderr)
                self.assertEqual(capture.exists(), bool(token), 'A missing OIDC token must not fall back to another key')
                self.assertNotIn(token or 'synthetic-long-lived-fallback', result.stdout + result.stderr)
                if token:
                    arguments = capture.read_text().splitlines()
                    self.assertEqual(arguments[arguments.index('--api-key') + 1], token)
                    self.assertEqual(arguments[arguments.index('--source') + 1], 'https://api.nuget.org/v3/index.json')

    def test_release_tag_targets_built_commit(self):
        content = (ROOT / '.github/workflows/release.yml').read_text()
        self.assertIn('--target "$GITHUB_SHA"', content)
        self.assertIn("github.ref == 'refs/heads/main'", content)

    def test_prerelease_is_not_promoted_to_latest(self):
        block = next(b for b in run_blocks(ROOT / '.github/workflows/release.yml') if 'gh release create' in b)
        shell = shutil.which('sh' if os.name == 'nt' else 'bash')
        if shell is None:
            self.skipTest('A POSIX shell is unavailable')
        for version, prerelease in (('1.2.3-preview.1', True), ('1.2.3', False)):
            with self.subTest(version=version), tempfile.TemporaryDirectory() as temporary:
                capture = Path(temporary) / 'arguments'
                script = 'gh() { printf "%s\\n" "$@" > "$CAPTURE"; };\n' + block
                result = subprocess.run([shell, '-euo', 'pipefail', '-c', script], cwd=temporary,
                                        env={**os.environ, 'VERSION': version, 'GITHUB_REPOSITORY': 'example/project',
                                             'GITHUB_SHA': 'a' * 40, 'CAPTURE': str(capture)},
                                        capture_output=True, text=True, timeout=10)
                self.assertEqual(result.returncode, 0, result.stderr)
                arguments = capture.read_text().splitlines()
                self.assertEqual('--prerelease' in arguments, prerelease)
                self.assertEqual('--latest=false' in arguments, prerelease)
                self.assertIn('v' + version, arguments)
                self.assertEqual(arguments[arguments.index('--target') + 1], 'a' * 40)
                instructions = (Path(temporary) / 'release-footer.md').read_text(encoding='utf-8')
                self.assertIn("$env:WEAVE_VERSION='" + version + "'", instructions)
                self.assertIn('WEAVE_VERSION=' + version + ' sh', instructions)

    def test_build_and_pack_receive_the_same_version(self):
        for name in ('release.yml', 'ci.yml'):
            blocks = run_blocks(ROOT / '.github/workflows' / name)
            build = next(b for b in blocks if b.startswith('dotnet build Weave.slnx'))
            pack = next(b for b in blocks if 'dotnet pack hosts/Weave.Cli/Weave.Cli.csproj' in b)
            with self.subTest(workflow=name):
                self.assertIn('"-p:Version=$VERSION"', build)
                self.assertIn('"-p:Version=$VERSION"', pack)

    def test_release_installs_node_and_gates_native_distributions(self):
        content = (ROOT / '.github/workflows/release.yml').read_text()
        self.assertIn("node-version: '24'", content)
        build = content.split('  create-release:', 1)[1].split('  publish-binaries:', 1)[0]
        self.assertIn('scripts/release/smoke.py', build)
        self.assertLess(build.index('scripts/release/smoke.py'), build.index('Generate package provenance'))
        binaries = content.split('  publish-binaries:', 1)[1].split('  github-release:', 1)[0]
        for rid in ('win-x64', 'linux-x64', 'osx-arm64'):
            row = binaries.split('- rid: ' + rid, 1)[1].split('- rid:', 1)[0].split('    runs-on:', 1)[0]
            self.assertIn('smoke: true', row)
        for rid in ('win-arm64', 'linux-arm64', 'osx-x64'):
            row = binaries.split('- rid: ' + rid, 1)[1].split('- rid:', 1)[0].split('    runs-on:', 1)[0]
            self.assertIn('smoke: false', row)
        self.assertIn('if: matrix.smoke', binaries)
        self.assertIn('--distribution "publish/$RID"', binaries)
        self.assertIn('--package "packages/KoalaFacts.Weave.Cli.$VERSION.nupkg"', binaries)
        self.assertIn('digest-mismatch: error', binaries)
        self.assertLess(binaries.index('scripts/release/smoke.py'), binaries.index('Package (tar.gz)'))

    def test_public_install_identity_is_owned_and_command_remains_weave(self):
        publish = (ROOT / '.github/workflows/publish-nuget.yml').read_text()
        release = (ROOT / '.github/workflows/release.yml').read_text()
        self.assertIn('https://www.nuget.org/packages/KoalaFacts.Weave.Cli/', publish)
        self.assertIn('dotnet tool install -g KoalaFacts.Weave.Cli --version', release)
        self.assertNotIn('dotnet tool install -g Weave.Cli ', release)
        project = ET.parse(ROOT / 'hosts/Weave.Cli/Weave.Cli.csproj')
        self.assertEqual(project.find('.//ToolCommandName').text, 'weave')

    def test_embedded_symbols_do_not_request_an_empty_symbol_package(self):
        props = ET.parse(ROOT / 'Directory.Build.props')
        self.assertEqual(props.find('.//DebugType').text, 'embedded')
        for name in ('release.yml', 'ci.yml'):
            content = (ROOT / '.github/workflows' / name).read_text()
            self.assertNotIn('IncludeSymbols=true', content)
            self.assertNotIn('SymbolPackageFormat=snupkg', content)
            self.assertIn('IncludeSymbols=false', content)
        release = (ROOT / '.github/workflows/release.yml').read_text()
        self.assertNotIn('artifacts/*.snupkg', release)

    def test_ci_packages_real_tool_without_publish_credentials(self):
        content = (ROOT / '.github/workflows/ci.yml').read_text()
        build = content.split('  build-and-test:', 1)[1].split('  exact-base-coverage-diagnostic:', 1)[0]
        for required in ('dotnet pack hosts/Weave.Cli/Weave.Cli.csproj', 'IncludeSymbols=false',
                         'scripts/release/guard.py validate-packages', '0.0.0-ci-validation'):
            self.assertIn(required, build)
        for forbidden in ('nuget push', 'NUGET_API_KEY', 'id-token: write', 'attestations: write'):
            self.assertNotIn(forbidden, build)

    def test_current_checks_share_strict_locked_dependency_audit(self):
        action = ROOT / '.github/actions/strict-nuget-audit/action.yml'
        self.assertTrue(action.exists(), 'Strict audit must replace the retired branch-specific checks')
        content = action.read_text()
        for required in ('--locked-mode', 'NuGetAudit=true', 'NuGetAuditMode=all', 'NuGetAuditLevel=low',
                         '--include-transitive', '--vulnerable', '--no-restore', '--format json',
                         '--output-version 1', 'scripts/check_dependency_audit.py', 'set -euo pipefail'):
            self.assertIn(required, content)
        for filename in ('ci.yml', 'scan-security.yml'):
            self.assertIn('uses: ./.github/actions/strict-nuget-audit',
                          (ROOT / '.github/workflows' / filename).read_text())
        for forbidden in ('--force-evaluate', 'continue-on-error', '|| true', 'NuGetAudit=false'):
            self.assertNotIn(forbidden, content)

    def test_ci_reports_checks_without_requesting_pr_comment_writes(self):
        content = (ROOT / '.github/workflows/ci.yml').read_text()
        build = content.split('  build-and-test:', 1)[1].split('  code-quality:', 1)[0]
        reporter = build.split('      - name: Publish test results', 1)[1].split('      - name:', 1)[0]
        self.assertIn("comment_mode: 'off'", reporter)
        self.assertIn('checks: write', build)
        self.assertNotIn('pull-requests: write', build)
        self.assertNotIn('issues: write', build)
        self.assertNotIn('continue-on-error:', reporter)

    def test_messagepack_security_patch_is_pinned(self):
        packages = ET.parse(ROOT / 'Directory.Packages.props')
        version = packages.find('.//PackageVersion[@Include="MessagePack"]').attrib['Version']
        parts = tuple(int(v) for v in version.split('.'))
        self.assertEqual(parts[0], 2, 'Review a major-version migration explicitly')
        self.assertGreaterEqual(parts, (2, 5, 303))


if __name__ == '__main__':
    unittest.main()
