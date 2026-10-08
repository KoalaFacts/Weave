"""Executable regressions: build DevTool first; no collector or test-runner IPC needed."""
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import tempfile
import unittest
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
DOTNET = os.environ.get('DOTNET_HOST_PATH', 'dotnet')
DEVTOOL = Path(os.environ.get('WEAVE_DEVTOOL', ROOT / 'scripts/DevTool/bin/Release/net10.0/DevTool.dll')).resolve()
MAILBOX = 'Weave.Mailboxes.Tests'
SHARED = 'Weave.Shared.Tests'
TARGETS = {
    'Weave.Product': 'src/Weave.csproj',
    'Weave.Mailboxes.Sqlite': 'extensions/Weave.Mailboxes.Sqlite/Weave.Mailboxes.Sqlite.csproj',
    'Weave.Mailbox.Host': 'hosts/Weave.Mailbox.Host/Weave.Mailbox.Host.csproj',
}


class CoverageAnalyzerTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='weave-coverage-')
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.projects = {path: {'assembly': assembly, 'contributors': [MAILBOX], 'owners': [MAILBOX]}
                         for assembly, path in TARGETS.items()}
        self.suites = [MAILBOX]
        self.materialize()

    def materialize(self):
        (self.root / 'scripts').mkdir(exist_ok=True)
        (self.root / 'scripts/coverage-ownership.json').write_text(json.dumps(self.projects))
        for path, target in self.projects.items():
            project = self.root / path
            project.parent.mkdir(parents=True, exist_ok=True)
            project.write_text(f'<Project><PropertyGroup><AssemblyName>{target["assembly"]}</AssemblyName></PropertyGroup></Project>')
            (project.parent / 'Source.cs').write_text('// fixture\n' * 20)
        for suite in self.suites:
            folder = self.root / 'tests' / suite
            folder.mkdir(parents=True, exist_ok=True)
            (folder / f'{suite}.csproj').write_text('<Project />')

    def filename(self, assembly):
        return str(Path(TARGETS[assembly]).parent / 'Source.cs')

    def package(self, assembly, hits=None, filename=None):
        return (assembly, [(filename or self.filename(assembly), hits if hits is not None else [1] * 9 + [0])])

    def report(self, packages=None, suite=MAILBOX, sources=()):
        root = ET.Element('coverage')
        source_nodes = ET.SubElement(root, 'sources')
        for source in sources:
            ET.SubElement(source_nodes, 'source').text = str(source)
        package_nodes = ET.SubElement(root, 'packages')
        for name, classes in packages if packages is not None else [self.package(a) for a in TARGETS]:
            package = ET.SubElement(package_nodes, 'package', name=name)
            classes_node = ET.SubElement(package, 'classes')
            for filename, hits in classes:
                cls = ET.SubElement(classes_node, 'class', name='Duplicated.ClassName', filename=filename)
                lines = ET.SubElement(cls, 'lines')
                for number, hit in enumerate(hits, 1):
                    ET.SubElement(lines, 'line', number=str(number), hits=str(hit))
        output = self.root / 'TestResults'
        output.mkdir(exist_ok=True)
        path = output / f'{suite}.cobertura.xml'
        ET.ElementTree(root).write(path, encoding='utf-8')
        return path

    def run_gate(self, expected, *args):
        result = subprocess.run([DOTNET, str(DEVTOOL), 'coverage', '--skip-collect', *args],
                                cwd=self.root, text=True, capture_output=True, timeout=20)
        output = result.stdout + result.stderr
        self.assertEqual(expected, result.returncode, output)
        return output

    def row(self, output, assembly, covered, valid):
        self.assertRegex(output, rf'(?m)^\s*{re.escape(assembly)}\s+{covered}\s+{valid}\s+')

    def test_actual_mailbox_assembly_names_pass_at_exactly_ninety(self):
        self.report()
        output = self.run_gate(0)
        for assembly in TARGETS:
            self.row(output, assembly, 9, 10)

    def test_each_assembly_below_ninety_fails_independently(self):
        for failing in TARGETS:
            with self.subTest(assembly=failing):
                self.report([self.package(a, [1] * (8 if a == failing else 10) + [0] * (2 if a == failing else 0)) for a in TARGETS]
                            + [('ThirdParty', [('vendor.cs', [1] * 1000)])])
                output = self.run_gate(1)
                self.assertIn(f'{failing}: 80.0000%', output)

    def test_below_threshold_that_rounds_to_ninety_reports_precise_failure(self):
        (self.root / 'src/Source.cs').write_text('// fixture\n' * 229)
        self.report([self.package('Weave.Product', [1] * 206 + [0] * 23)]
                    + [self.package(a) for a in TARGETS if a != 'Weave.Product'])
        self.assertIn('Weave.Product: 89.9563% (206/229 lines)', self.run_gate(1))

    def test_whole_product_keeps_uncovered_legacy_feature(self):
        packages = [self.package(a, [1] * 10) for a in TARGETS]
        legacy = self.root / 'src/LegacyFeature.cs'
        legacy.write_text('// uncovered\n' * 10)
        packages[0][1].append(('src/LegacyFeature.cs', [0] * 10))
        self.report(packages)
        self.row(self.run_gate(1), 'Weave.Product', 10, 20)

    def test_each_required_assembly_missing_is_evidence_failure(self):
        for missing in TARGETS:
            with self.subTest(assembly=missing):
                self.report([self.package(a) for a in TARGETS if a != missing])
                self.assertIn(missing, self.run_gate(2))

    def test_empty_and_unmatched_reports_fail_closed(self):
        for packages in ([], [('Weave.Mailboxes.Tests', [('test.cs', [1])])], [('Vendor', [('vendor.cs', [1])])]):
            with self.subTest(packages=packages):
                self.report(packages)
                self.assertIn(MAILBOX, self.run_gate(2))

    def test_missing_report_directory_and_file_fail_with_target(self):
        self.assertIn(MAILBOX, self.run_gate(2))
        (self.root / 'TestResults').mkdir()
        self.assertIn(MAILBOX, self.run_gate(2))

    def test_empty_and_excluded_required_assembly_fail(self):
        for classes in ([], [('src/Models/Row.cs', [1]), ('src/Program.cs', [1])]):
            with self.subTest(classes=classes):
                packages = [self.package(a) for a in TARGETS if a != 'Weave.Product']
                self.report(packages + [('Weave.Product', classes)])
                self.assertIn('Weave.Product', self.run_gate(2))

    def test_missing_selected_report_cannot_be_rescued_by_stale_extra(self):
        self.suites.append(SHARED)
        self.projects['src/Weave.csproj']['contributors'].append(SHARED)
        self.materialize()
        self.report()
        self.assertIn(SHARED, self.run_gate(2))
        self.assertIn(SHARED, self.run_gate(2, '--root', f'tests/{SHARED}'))

    def test_unknown_suite_and_undeclared_first_party_owner_fail(self):
        self.suites.append('Weave.New.Tests')
        self.materialize()
        self.report()
        self.assertIn('Weave.New.Tests', self.run_gate(2))
        self.suites.remove('Weave.New.Tests')
        shutil.rmtree(self.root / 'tests/Weave.New.Tests')
        self.suites.append(SHARED)
        self.projects['src/Weave.csproj']['contributors'].append(SHARED)
        self.materialize()
        self.report([self.package(a) for a in TARGETS], suite=SHARED)
        self.assertIn('contributor', self.run_gate(2))

    def test_union_across_reports_classes_lines_and_order_is_deterministic(self):
        self.suites.append(SHARED)
        self.projects['src/Weave.csproj']['contributors'].append(SHARED)
        self.materialize()
        packages = [self.package(a) for a in TARGETS]
        packages[0] = self.package('Weave.Product', [1] * 5 + [0] * 5)
        packages[0][1].append(('src/Source.cs', [1] * 5 + [0] * 5))
        self.report(packages)
        self.report([self.package('Weave.Product', [0] * 5 + [1] * 4 + [0])], suite=SHARED)
        first = self.run_gate(0)
        self.row(first, 'Weave.Product', 9, 10)
        self.report(list(reversed(packages)))
        self.assertEqual(first, self.run_gate(0))

    def test_complementary_halves_cover_same_denominator_in_either_report_order(self):
        self.suites.append(SHARED)
        self.projects['src/Weave.csproj']['contributors'].append(SHARED)
        self.materialize()
        outputs = []
        for first, second in [([1] * 5 + [0] * 5, [0] * 5 + [1] * 5),
                              ([0] * 5 + [1] * 5, [1] * 5 + [0] * 5)]:
            self.report([self.package('Weave.Product', first)] + [self.package(a) for a in TARGETS if a != 'Weave.Product'])
            self.report([self.package('Weave.Product', second)], suite=SHARED)
            outputs.append(self.run_gate(0))
            self.row(outputs[-1], 'Weave.Product', 10, 10)
        self.assertEqual(outputs[0], outputs[1])

    def test_duplicate_line_entries_do_not_inflate_denominator(self):
        path = self.report()
        report = ET.parse(path)
        lines = report.find('.//package[@name="Weave.Product"]/classes/class/lines')
        ET.SubElement(lines, 'line', number='10', hits='0')
        ET.SubElement(lines, 'line', number='1', hits='0')
        report.write(path)
        self.row(self.run_gate(0), 'Weave.Product', 9, 10)

    def test_skip_collect_ignores_corrupt_unselected_report(self):
        self.suites.append(SHARED)
        self.projects['src/Weave.csproj']['contributors'].append(SHARED)
        self.materialize()
        self.report()
        (self.root / 'TestResults' / f'{SHARED}.cobertura.xml').write_text('<broken>')
        self.row(self.run_gate(0, '--root', f'tests/{MAILBOX}'), 'Weave.Product', 9, 10)

    def test_cli_assembly_identity_is_exactly_lowercase_weave(self):
        suite = 'Weave.Cli.Tests'
        self.suites.append(suite)
        self.projects['hosts/Weave.Cli/Weave.Cli.csproj'] = {
            'assembly': 'weave', 'contributors': [suite], 'owners': [suite]}
        self.materialize()
        self.report([('weave', [('hosts/Weave.Cli/Source.cs', [1] * 9 + [0])])], suite=suite)
        self.row(self.run_gate(0, '--root', f'tests/{suite}'), 'weave', 9, 10)
        self.report([('Weave', [('hosts/Weave.Cli/Source.cs', [1] * 10)])], suite=suite)
        self.assertIn('weave: missing non-excluded valid lines', self.run_gate(2, '--root', f'tests/{suite}'))

    def test_path_sources_absolute_relative_and_separators_union(self):
        classes = [('src/Source.cs', [1] * 5 + [0] * 5),
                   (str(self.root / 'src/Source.cs'), [0] * 5 + [1] * 5),
                   (str(self.root / 'src/Source.cs').replace('/', '\\'), [0] * 10),
                   ('C:\\build\\repo\\src\\Source.cs', [0] * 10),
                   ('Source.cs', [0] * 10)]
        self.report([('Weave.Product', classes)] + [self.package(a) for a in TARGETS if a != 'Weave.Product'],
                    sources=[self.root / 'src'])
        self.row(self.run_gate(0), 'Weave.Product', 10, 10)

    def test_identical_basenames_in_different_directories_stay_distinct(self):
        (self.root / 'src/Feature').mkdir()
        (self.root / 'src/Feature/Source.cs').write_text('// second file\n' * 10)
        classes = [('src/Source.cs', [1] * 10), ('src/Feature/Source.cs', [0] * 10)]
        self.report([('Weave.Product', classes)] + [self.package(a) for a in TARGETS if a != 'Weave.Product'])
        self.row(self.run_gate(1), 'Weave.Product', 10, 20)

    def test_unresolved_ambiguous_and_outside_project_sources_fail(self):
        for filename, sources in [('missing.cs', []), ('../outside.cs', []), ('Source.cs', [self.root / 'src', self.root / 'src/Feature'])]:
            with self.subTest(filename=filename, sources=sources):
                (self.root / 'src/Feature').mkdir(exist_ok=True)
                (self.root / 'src/Feature/Source.cs').write_text('// ambiguous\n')
                self.report([self.package('Weave.Product', filename=filename)] + [self.package(a) for a in TARGETS if a != 'Weave.Product'], sources=sources)
                self.assertIn(filename, self.run_gate(2))

    def test_existing_exclusions_preserved_for_both_separators(self):
        classes = [('src/Mailboxes/Row.cs', [1] * 9 + [0])]
        (self.root / 'src/Mailboxes').mkdir()
        (self.root / 'src/Mailboxes/Row.cs').write_text('// counted\n' * 10)
        excluded = ['src/obj/Generated.cs', 'src/Generated.g.cs', 'src/Models/Row.cs',
                    'src/ThingSurrogate.cs', 'src/FeatureContracts.cs', 'src/Program.cs']
        classes += [(f.replace('/', separator), [0] * 100) for f in excluded for separator in ('/', '\\')]
        self.report([('Weave.Product', classes)] + [self.package(a) for a in TARGETS if a != 'Weave.Product'])
        self.row(self.run_gate(0), 'Weave.Product', 9, 10)

    def test_bad_xml_and_invalid_line_evidence_fail(self):
        path = self.report()
        original = path.read_text()
        for content in ('<coverage>', original.replace('number="1"', 'number="0"'),
                        original.replace('hits="1"', 'hits="-1"'), original.replace('hits="1"', 'hits="oops"')):
            with self.subTest(content=content[:60]):
                path.write_text(content)
                self.assertIn(MAILBOX, self.run_gate(2))

    def test_inventory_addition_or_assembly_identity_change_fails(self):
        self.report()
        new_project = self.root / 'src/New.csproj'
        new_project.write_text('<Project />')
        self.assertIn('src/New.csproj', self.run_gate(2))
        new_project.unlink()
        (self.root / 'src/Weave.csproj').write_text('<Project />')
        self.assertIn('Weave.Product', self.run_gate(2))

    def test_unowned_apphost_dashboard_are_explicit_gaps_not_invented_requirements(self):
        for name in ('Weave.AppHost', 'Weave.Dashboard'):
            self.projects[f'hosts/{name}/{name}.csproj'] = {'assembly': name, 'contributors': [], 'owners': []}
        self.materialize()
        self.report()
        output = self.run_gate(0)
        for name in ('Weave.AppHost', 'Weave.Dashboard'):
            self.assertIn(f'Coverage gap outside tested-owner gate: {name}', output)
        # A scoped mailbox run also cannot claim the whole runtime inventory.
        self.suites.append(SHARED)
        self.projects['src/Weave.csproj']['contributors'].append(SHARED)
        self.materialize()
        self.row(self.run_gate(0, '--root', f'tests/{MAILBOX}'), 'Weave.Product', 9, 10)

    def test_transitive_only_assembly_is_reported_as_scope_gap(self):
        self.projects['hosts/Indirect/Indirect.csproj'] = {
            'assembly': 'Indirect', 'contributors': [MAILBOX], 'owners': []}
        self.materialize()
        self.report([self.package(a) for a in TARGETS] + [('Indirect', [('hosts/Indirect/Source.cs', [0] * 10)])])
        self.assertIn('Coverage gap outside tested-owner gate: Indirect', self.run_gate(0))

    def test_exclusion_from_sources_root_is_preserved(self):
        (self.root / 'src/Models').mkdir()
        (self.root / 'src/Models/OnlyModel.cs').write_text('// excluded\n')
        packages = [self.package(a) for a in TARGETS]
        packages[0][1].append(('OnlyModel.cs', [0] * 100))
        self.report(packages, sources=[self.root / 'src/Models'])
        self.row(self.run_gate(0), 'Weave.Product', 9, 10)

    def test_real_manifest_accepts_direct_dynamic_dashboard_owner_and_enforces_threshold(self):
        self.projects = json.loads((ROOT / 'scripts/coverage-ownership.json').read_text())
        self.suites = [p.stem for p in (ROOT / 'tests').rglob('*.Tests.csproj')]
        self.materialize()
        suite = 'Weave.Silo.Tests'
        packages = [(entry['assembly'], [(str(Path(path).parent / 'Source.cs'), [1] * 9 + [0])])
                    for path, entry in self.projects.items()
                    if suite in entry['contributors'] or entry['assembly'] in {'Weave.Dashboard', 'Weave.Deploy'}]
        self.report(packages, suite=suite)
        self.row(self.run_gate(0, '--root', f'tests/{suite}'), 'Weave.Dashboard', 9, 10)
        dashboard = next(package for package in packages if package[0] == 'Weave.Dashboard')
        dashboard[1][0] = (dashboard[1][0][0], [1] * 8 + [0] * 2)
        self.report(packages, suite=suite)
        self.assertIn('Weave.Dashboard: 80.0000%', self.run_gate(1, '--root', f'tests/{suite}'))
        self.report([package for package in packages if package[0] != 'Weave.Dashboard'], suite=suite)
        self.assertIn('Weave.Dashboard: missing non-excluded valid lines',
                      self.run_gate(2, '--root', f'tests/{suite}'))

    def test_unknown_options_and_nonfinite_or_out_of_range_threshold_fail(self):
        self.report()
        for args in [('--typo',), ('--threshold',), ('--threshold', 'NaN'), ('--threshold', '101'), ('--threshold', '-1')]:
            with self.subTest(args=args):
                self.run_gate(2, *args)


if __name__ == '__main__':
    unittest.main()
