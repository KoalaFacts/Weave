"""Dependency-free architecture gates for the flat source migration."""
import os
from pathlib import Path
import unittest
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]


class SourceLayoutTests(unittest.TestCase):
    def test_product_project_is_directly_under_src(self):
        self.assertTrue((ROOT / 'src/Weave.csproj').is_file())
        self.assertEqual([ROOT / 'src/Weave.csproj'], sorted((ROOT / 'src').rglob('*.csproj')))

    def test_source_has_features_not_product_or_layer_wrappers(self):
        forbidden = {'Weave', 'Features', 'Foundation', 'Assistants', 'Runtime', 'UX',
                     'Security', 'Core', 'Contracts', 'Kernel', 'Infrastructure'}
        actual = {p.name for p in (ROOT / 'src').iterdir() if p.is_dir()}
        source_wrappers = set()
        for name in actual & forbidden:
            for _, directories, files in os.walk(ROOT / 'src' / name):
                directories[:] = [directory for directory in directories
                                  if directory not in {'bin', 'obj'}]
                if any(not file.endswith('.user') for file in files):
                    source_wrappers.add(name)
                    break
        self.assertFalse(source_wrappers, sorted(source_wrappers))
        self.assertTrue({'Agents', 'Workspaces', 'Authority', 'Invocations', 'Plugins',
                         'Credentials', 'Audit', 'Composition'} <= actual)

    def test_hosts_extensions_and_tests_are_outside_src(self):
        for path in ('hosts/Weave.Host/Weave.Host.csproj',
                     'extensions/Weave.Mcp/Weave.Mcp.csproj',
                     'extensions/Weave.CliTools/Weave.CliTools.csproj',
                     'extensions/Weave.AgentRuntime/Weave.AgentRuntime.csproj',
                     'tests/Weave.Tools.Tests/Weave.Tools.Tests.csproj'):
            self.assertTrue((ROOT / path).is_file(), path)

    def test_project_references_resolve_and_are_unique(self):
        for project in sorted(ROOT.rglob('*.csproj')):
            if any(part in {'obj', 'bin', '.git'} for part in project.parts):
                continue
            seen = set()
            for ref in ET.parse(project).getroot().iter('ProjectReference'):
                path = ref.get('Include', '').replace('\\', '/')
                self.assertNotIn('$(', path, (project, path))
                target = (project.parent / path).resolve()
                self.assertTrue(target.is_file(), f'{project.relative_to(ROOT)} -> {path}')
                self.assertNotIn(target, seen, f'duplicate reference in {project}')
                seen.add(target)

    def test_solution_includes_each_deployable_and_test_project(self):
        solution = ET.parse(ROOT / 'Weave.slnx').getroot()
        entries = [(ROOT / p.attrib['Path']).resolve() for p in solution.iter('Project')]
        self.assertEqual(len(entries), len(set(entries)))
        for path in entries:
            self.assertTrue(path.is_file(), str(path))
        expected = {p.resolve() for area in ('src', 'hosts', 'extensions', 'tests', 'tools')
                    for p in (ROOT / area).rglob('*.csproj')
                    if 'obj' not in p.parts and 'bin' not in p.parts}
        self.assertEqual(expected, set(entries))

    def test_product_does_not_reference_concrete_protocol_or_model_extensions(self):
        product = ROOT / 'src/Weave.csproj'
        self.assertTrue(product.is_file())
        tree = ET.parse(product).getroot()
        refs = [r.attrib['Include'] for r in tree.iter('ProjectReference')]
        self.assertFalse(any('/extensions/' in p.replace('\\', '/') for p in refs))
        packages = {p.attrib['Include'] for p in tree.iter('PackageReference')}
        self.assertNotIn('Microsoft.Extensions.AI.OpenAI', packages)
        self.assertFalse(any(p.startswith('Microsoft.Orleans') for p in packages))
        self.assertEqual('false', tree.findtext('.//GenerateCqrsRegistration'))
        self.assertTrue((ROOT / 'src/Invocations/IToolConnector.cs').is_file())


if __name__ == '__main__':
    unittest.main()
