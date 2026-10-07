"""Collect exact-base evidence only; this never evaluates or waives coverage gates."""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import sys
import xml.etree.ElementTree as ET

BASE_SHA = 'a7983cc73e05d9d0bb6afb147ab30396b74e6fde'
EXPECTED_SUITES = tuple(f'Weave.{name}.Tests' for name in (
    'Actions', 'Agents', 'Cli', 'Deploy', 'Security', 'Shared', 'Silo', 'Tools', 'Workspaces'))


def save(output, name, value):
    (output / name).write_text(json.dumps(value, indent=2, sort_keys=True) + '\n', encoding='utf-8')


def git_output(root, *arguments):
    return subprocess.check_output(['git', '-C', str(root), *arguments], text=True).strip()


def sha256(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def source_hashes(root):
    return {name: sha256(root / name) if (root / name).is_file() else None for name in
            git_output(root, 'ls-files', '-z').split('\0') if name}


def binary_hashes(root):
    return {path.relative_to(root).as_posix(): sha256(path)
            for path in sorted(root.glob('**/bin/Release/**/*'))
            if path.is_file()}


def test_projects(root):
    projects = sorted(path.relative_to(root).as_posix() for path in root.glob('tests/**/*.Tests.csproj')
                      if not {'bin', 'obj'}.intersection(path.relative_to(root).parts))
    if [Path(project).stem for project in projects] != sorted(EXPECTED_SUITES):
        raise ValueError('Exact-base suite inventory must contain all nine approved suites.')
    solution = ET.parse(root / 'Weave.slnx')
    included = {node.attrib['Path'] for node in solution.iter('Project')}
    if not set(projects).issubset(included):
        raise ValueError('The solution must build all nine baseline suites.')
    return projects


def run_logged(root, output, name, arguments, records):
    record = {'name': name, 'arguments': arguments, 'working_directory': str(root), 'exit_code': None}
    records.append(record)
    save(output, 'commands.json', records)
    print(f'Diagnostic: {name}', flush=True)
    with (output / f'{name}.command.log').open('w', encoding='utf-8') as log:
        result = subprocess.run(arguments, cwd=root, stdout=log, stderr=subprocess.STDOUT, text=True)
    record['exit_code'] = result.returncode
    save(output, 'commands.json', records)
    return result.returncode


def collect_suites(root, output, projects, records):
    successful = True
    for project in projects:
        suite = Path(project).stem
        report = output / f'{suite}.cobertura.xml'
        report.unlink(missing_ok=True)
        # Keep flags identical to CoverageCommand. Only the evidence destination differs.
        arguments = ['dotnet', 'dotnet-coverage', 'collect', '-f', 'cobertura', '-o', str(report),
                     '-l', str(output / f'{suite}.collector.log'), '--', 'dotnet', 'test',
                     '--project', project, '--no-build', '--no-restore', '-c', 'Release']
        if run_logged(root, output, suite, arguments, records) != 0:
            successful = False
        if not report.is_file() or report.stat().st_size == 0:
            print(f'Diagnostic: missing or empty report for {suite}', file=sys.stderr)
            successful = False
    return successful


def run(root, output):
    root, output = root.resolve(), output.resolve()
    helper = Path(__file__).resolve()
    if output.is_relative_to(root) or helper.is_relative_to(root):
        raise ValueError('The helper and evidence must remain outside the baseline checkout.')
    output.mkdir(parents=True, exist_ok=True)
    identity = {'diagnostic_only': True, 'expected_base_sha': BASE_SHA,
                'actual_base_sha': git_output(root, 'rev-parse', 'HEAD'),
                'source_root': str(root), 'helper_sha256': sha256(helper),
                'collector_version': '18.11.2', 'configuration': 'Release'}
    save(output, 'identity.json', identity)
    if identity['actual_base_sha'] != BASE_SHA:
        raise ValueError('Checkout does not match the exact approved base.')
    if git_output(root, 'status', '--porcelain'):
        raise ValueError('Baseline checkout must start clean.')
    projects = test_projects(root)
    manifest = json.loads((root / '.config/dotnet-tools.json').read_text())
    collector = manifest['tools']['dotnet-coverage']
    if collector['version'] != '18.11.2' or collector.get('rollForward') is not False:
        raise ValueError('Baseline collector must be pinned to 18.11.2 without roll-forward.')
    identity['projects'] = projects
    save(output, 'identity.json', identity)
    sources = source_hashes(root)
    save(output, 'source-hashes-before.json', sources)
    records = []
    binaries = None
    try:
        for name, arguments in (
            ('sdk', ['dotnet', '--info']),
            ('restore', ['dotnet', 'restore', 'Weave.slnx']),
            ('build', ['dotnet', 'build', 'Weave.slnx', '--no-restore', '-c', 'Release']),
            ('tool-restore', ['dotnet', 'tool', 'restore', '--tool-manifest', '.config/dotnet-tools.json']),
            ('collector-version', ['dotnet', 'dotnet-coverage', '--version']),
        ):
            if run_logged(root, output, name, arguments, records) != 0:
                raise RuntimeError(f'Baseline {name} failed; see retained command log.')
        if source_hashes(root) != sources:
            raise ValueError('Baseline tracked inputs changed during restore/build; collection refused.')
        binaries = binary_hashes(root)
        if not binaries:
            raise ValueError('No Release binary identity evidence was produced.')
        save(output, 'binary-hashes-before.json', binaries)
        successful = collect_suites(root, output, projects, records)
        identity['all_suites_collected'] = successful
        return 0 if successful else 1
    finally:
        after = binary_hashes(root)
        sources_after = source_hashes(root)
        save(output, 'binary-hashes-after.json', after)
        save(output, 'source-hashes-after.json', sources_after)
        identity['tracked_changes_after'] = git_output(root, 'status', '--porcelain', '--untracked-files=no')
        identity['sources_unchanged'] = sources == sources_after
        identity['binaries_unchanged_during_collection'] = bool(binaries) and binaries == after
        save(output, 'identity.json', identity)
        if binaries is not None and binaries != after:
            raise ValueError('Release binaries changed during collection; evidence is not comparable.')
        if sources != sources_after:
            raise ValueError('Baseline tracked inputs changed; evidence is not comparable.')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--repository', required=True, type=Path)
    parser.add_argument('--output', required=True, type=Path)
    args = parser.parse_args()
    try:
        return run(args.repository, args.output)
    except (OSError, ValueError, RuntimeError, KeyError, ET.ParseError, subprocess.SubprocessError) as error:
        print(f'Baseline diagnostic failed: {error}', file=sys.stderr)
        return 2


if __name__ == '__main__':
    sys.exit(main())
