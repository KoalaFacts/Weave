#!/usr/bin/env python3
"""Fail-closed release provenance and package checks. Uses only the standard library."""
import argparse
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import xml.etree.ElementTree as ET
import zipfile

SHA = re.compile(r'[0-9a-f]{40}')
VERSION = re.compile(r'(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?')


def require(condition, message):
    if not condition:
        raise ValueError(message)


def validate_version(value):
    require(isinstance(value, str) and 0 < len(value) <= 100, 'Invalid version length.')
    match = VERSION.fullmatch(value)
    require(match is not None, 'Expected canonical X.Y.Z or X.Y.Z-prerelease.')
    for identifier in (match.group(4) or '').split('.'):
        require(not (identifier.isdigit() and len(identifier) > 1 and identifier.startswith('0')),
                'Numeric prerelease identifiers must not have leading zeros.')
    return value


def gh_get(path, paginate=False):
    command = ['gh', 'api', path]
    if paginate:
        command += ['--paginate', '--slurp']
    # No shell, credential arguments, secret logging, or fallback on API failures.
    result = subprocess.run(command, check=True, capture_output=True, text=True, timeout=60)
    return json.loads(result.stdout)


def resolve_source(repository, run_id, requested_version='', get=gh_get):
    require(isinstance(repository, str) and re.fullmatch(r'[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+', repository),
            'Invalid repository.')
    require(isinstance(run_id, str) and re.fullmatch(r'[1-9][0-9]{0,19}', run_id), 'Invalid release run ID.')
    if requested_version:
        validate_version(requested_version)
    prefix = f'repos/{repository}'
    run = get(f'{prefix}/actions/runs/{run_id}')
    require(isinstance(run, dict) and type(run.get('id')) is int and run['id'] == int(run_id),
            'Run identity mismatch.')
    require(run.get('status') == 'completed' and run.get('conclusion') == 'success',
            'Release run did not complete successfully.')
    require(run.get('head_branch') == 'main' and run.get('event') == 'workflow_dispatch'
            and run.get('path') == '.github/workflows/release.yml', 'Untrusted release workflow or branch.')
    require(isinstance(run.get('head_repository'), dict)
            and run['head_repository'].get('full_name') == repository, 'Release came from another repository.')
    sha = run.get('head_sha')
    require(isinstance(sha, str) and SHA.fullmatch(sha), 'Invalid source commit.')
    pages = get(f'{prefix}/actions/runs/{run_id}/artifacts?per_page=100', paginate=True)
    require(isinstance(pages, list) and pages, 'Missing artifact pages.')
    candidates = []
    for page in pages:
        require(isinstance(page, dict) and isinstance(page.get('artifacts'), list), 'Invalid artifact response.')
        for artifact in page['artifacts']:
            require(isinstance(artifact, dict) and isinstance(artifact.get('name'), str), 'Invalid artifact.')
            if artifact['name'].startswith('nuget-packages-v'):
                candidates.append(artifact)
    require(len(candidates) == 1, 'Expected exactly one package artifact from this run.')
    artifact = candidates[0]
    require(artifact.get('expired') is False and type(artifact.get('id')) is int and artifact['id'] > 0,
            'Package artifact expired or has no valid ID.')
    version = validate_version(artifact['name'][len('nuget-packages-v'):])
    require(not requested_version or requested_version == version, 'Manual version differs from built artifact.')
    tag = get(f'{prefix}/git/ref/tags/v{version}')
    require(isinstance(tag, dict) and tag.get('ref') == f'refs/tags/v{version}', 'Release tag mismatch.')
    target = tag.get('object')
    for _ in range(8):
        require(isinstance(target, dict) and isinstance(target.get('sha'), str)
                and SHA.fullmatch(target['sha']), 'Invalid tag target.')
        if target.get('type') == 'commit':
            break
        require(target.get('type') == 'tag', 'Tag does not reference a commit.')
        target = get(f"{prefix}/git/tags/{target['sha']}").get('object')
    require(target.get('type') == 'commit' and target['sha'] == sha,
            'Release tag does not point to the built commit.')
    return {'version': version, 'run_id': run_id, 'artifact_id': str(artifact['id']),
            'artifact_name': artifact['name'], 'source_sha': sha}


def validate_packages(directory, version):
    validate_version(version)
    require(directory.is_dir() and not directory.is_symlink(), 'Package directory is missing or linked.')
    expected = f'Weave.Cli.{version}.nupkg'
    allowed = {expected, f'Weave.Cli.{version}.snupkg'}
    files = list(directory.iterdir())
    require(any(p.name == expected for p in files), 'Required tool package is missing.')
    for path in files:
        require(path.name in allowed and path.is_file() and not path.is_symlink(), 'Unexpected package artifact entry.')
        require(path.stat().st_size <= 512 * 1024 * 1024, 'Package is too large.')
        try:
            with zipfile.ZipFile(path) as archive:
                manifests = [entry for entry in archive.infolist() if entry.filename.lower().endswith('.nuspec')]
                require(len(manifests) == 1 and 0 < manifests[0].file_size <= 128 * 1024,
                        'Missing, duplicate, or oversized package manifest.')
                with archive.open(manifests[0]) as stream:
                    data = stream.read(128 * 1024 + 1)
                require(len(data) <= 128 * 1024 and b'<!DOCTYPE' not in data.upper()
                        and b'<!ENTITY' not in data.upper(), 'Unsafe package manifest.')
                # Require UTF-8, preventing alternate encodings from hiding a DTD.
                root = ET.fromstring(data.decode('utf-8-sig'))
                require(root.tag.rsplit('}', 1)[-1] == 'package', 'Invalid package manifest root.')
                metadata = [e for e in root if e.tag.rsplit('}', 1)[-1] == 'metadata']
                require(len(metadata) == 1, 'Invalid package metadata.')
                ids = [e.text for e in metadata[0] if e.tag.rsplit('}', 1)[-1] == 'id']
                versions = [e.text for e in metadata[0] if e.tag.rsplit('}', 1)[-1] == 'version']
                require(ids == ['Weave.Cli'] and versions == [version], 'Package identity or version differs from release.')
        except (zipfile.BadZipFile, ET.ParseError, UnicodeError) as error:
            raise ValueError('Invalid package or manifest.') from error


def output(values):
    # Values originate only from the validated, bounded canonical formats above.
    with open(os.environ['GITHUB_OUTPUT'], 'a', encoding='utf-8') as stream:
        for key, value in values.items():
            stream.write(f'{key}={value}\n')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('command', choices=('validate-version', 'resolve', 'validate-packages'))
    arguments = parser.parse_args()
    if arguments.command == 'validate-version':
        output({'version': validate_version(os.environ.get('INPUT_VERSION', ''))})
    elif arguments.command == 'resolve':
        event = os.environ.get('GITHUB_EVENT_NAME')
        require(event in ('workflow_run', 'workflow_dispatch'), 'Unsupported publishing trigger.')
        version = os.environ.get('INPUT_VERSION', '')
        if event == 'workflow_dispatch':
            validate_version(version)
        output(resolve_source(os.environ['GITHUB_REPOSITORY'], os.environ.get('RELEASE_RUN_ID', ''), version))
    else:
        validate_packages(Path('packages'), os.environ.get('VERSION', ''))


if __name__ == '__main__':
    try:
        main()
    except (ValueError, KeyError, OSError, subprocess.SubprocessError) as error:
        # Do not print upstream responses, package payloads, command stderr, or secrets.
        print(f'Release validation failed ({type(error).__name__}).', file=sys.stderr)
        raise SystemExit(1)
