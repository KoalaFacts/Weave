"""Keep explicit coverage owners/contributors tied to actual runtime project metadata."""
import json
from pathlib import Path
import unittest
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]


def references(project):
    targets = {(project.parent / ref.attrib['Include'].replace('\\', '/')).resolve()
               for ref in ET.parse(project).getroot().iter('ProjectReference')
               if ref.get('ReferenceOutputAssembly', 'true').lower() != 'false'
               and ref.get('OutputItemType') != 'Analyzer'}
    manifest = json.loads((ROOT / 'scripts/coverage-ownership.json').read_text())
    targets.update((ROOT / path).resolve() for path, entry in manifest.items()
                   if any(owner['suite'] == project.stem for owner in entry.get('dynamicOwners', [])))
    return targets


def closure(project, seen=None):
    seen = set() if seen is None else seen
    for target in references(project):
        if target not in seen:
            seen.add(target)
            closure(target, seen)
    return seen


class CoverageInventoryTests(unittest.TestCase):
    def test_manifest_matches_every_runtime_project_and_actual_assembly(self):
        manifest = json.loads((ROOT / 'scripts/coverage-ownership.json').read_text())
        projects = {p.relative_to(ROOT).as_posix(): p for area in ('src', 'hosts', 'extensions')
                    for p in (ROOT / area).rglob('*.csproj')
                    if not {'bin', 'obj'} & set(p.parts)}
        self.assertEqual(set(projects), set(manifest))
        assemblies = []
        for path, project in projects.items():
            actual = ET.parse(project).findtext('.//AssemblyName') or project.stem
            self.assertEqual(actual, manifest[path]['assembly'], path)
            assemblies.append(actual)
        self.assertEqual(len(assemblies), len(set(assemblies)))

    def test_owners_are_direct_or_explicit_dynamic_and_contributors_are_runtime_closure(self):
        manifest = json.loads((ROOT / 'scripts/coverage-ownership.json').read_text())
        suites = sorted((ROOT / 'tests').rglob('*.Tests.csproj'))
        for path, entry in manifest.items():
            target = (ROOT / path).resolve()
            self.assertEqual(sorted(p.stem for p in suites if target in references(p)), entry['owners'], path)
            self.assertEqual(sorted(p.stem for p in suites if target in closure(p)), entry['contributors'], path)
        for suite in suites:
            self.assertTrue(any(suite.stem in entry['owners'] for entry in manifest.values()), suite)

    def test_mailbox_requires_full_product_provider_and_host_and_gaps_stay_visible(self):
        manifest = json.loads((ROOT / 'scripts/coverage-ownership.json').read_text())
        required = {p['assembly'] for p in manifest.values() if 'Weave.Mailboxes.Tests' in p['owners']}
        self.assertEqual({'Weave.Product', 'Weave.Mailboxes.Sqlite', 'Weave.Mailbox.Host'}, required)
        no_contributor = {p['assembly'] for p in manifest.values() if not p['contributors']}
        self.assertEqual({'Weave.AppHost'}, no_contributor)
        indirect = {p['assembly'] for p in manifest.values() if p['contributors'] and not p['owners']}
        self.assertEqual({'Weave.ServiceDefaults', 'Weave.Silo.Clustering.Postgres',
                          'Weave.Silo.Clustering.Redis', 'Weave.Silo.Clustering.SqlServer',
                          'Weave.Silo.Clustering.Sqlite'}, indirect)

    def test_dashboard_dynamic_ownership_is_narrow_and_source_backed(self):
        manifest = json.loads((ROOT / 'scripts/coverage-ownership.json').read_text())
        dynamic = {path: entry['dynamicOwners'] for path, entry in manifest.items() if entry.get('dynamicOwners')}
        self.assertEqual({'hosts/Weave.Dashboard/Weave.Dashboard.csproj': [{
            'suite': 'Weave.Silo.Tests',
            'source': 'tests/Weave.Silo.Tests/Invocations/DashboardReviewRenderingTests.cs',
            'method': 'DashboardReviewRenderingTests.DashboardType',
        }]}, dynamic)
        evidence = (ROOT / dynamic['hosts/Weave.Dashboard/Weave.Dashboard.csproj'][0]['source']).read_text()
        self.assertIn('internal static Type DashboardType(string name)', evidence)
        self.assertIn('Assembly.LoadFrom(assemblyPath)', evidence)
        self.assertIn('"hosts", "Weave.Dashboard", "bin", configuration', evidence)
        self.assertIn('"net10.0", "Weave.Dashboard.dll"', evidence)
        self.assertEqual(['Weave.Silo.Tests'], manifest['hosts/Weave.Dashboard/Weave.Dashboard.csproj']['owners'])

    def test_ci_restores_pinned_tool_runs_executable_regressions_and_keeps_reports(self):
        workflow = (ROOT / '.github/workflows/ci.yml').read_text().split('  code-quality:')[0]
        ordered = ['- name: Test\n', '- name: Restore coverage tool and analyzer\n',
                   '- name: Coverage analyzer regressions\n', '- name: Collect and enforce tested-owner coverage\n']
        for step in ordered:
            self.assertIn(step, workflow)
        positions = [workflow.index(step) for step in ordered]
        self.assertEqual(sorted(positions), positions)
        for command in ('dotnet tool restore --tool-manifest .config/dotnet-tools.json',
                        'dotnet restore scripts/DevTool/DevTool.csproj --locked-mode',
                        'python3 -m unittest discover -s scripts/coverage-tests -v',
                        'coverage --threshold 90', 'path: TestResults/'):
            self.assertIn(command, workflow)
        upload = workflow.split('- name: Upload coverage evidence\n')[1]
        self.assertTrue(upload.lstrip().startswith('if: always()'))
        tools = json.loads((ROOT / '.config/dotnet-tools.json').read_text())['tools']
        self.assertEqual('18.11.2', tools['dotnet-coverage']['version'])
        self.assertFalse(tools['dotnet-coverage']['rollForward'])


if __name__ == '__main__':
    unittest.main()
