#!/usr/bin/env python3
"""Submit committed NuGet lock graphs; never execute the inspected revision's code.

Run only from an explicitly pinned trusted tools revision, after read-only locked
restores of BOTH source revisions. No package installation or build occurs here.
"""
import argparse
import base64
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import sys
import urllib.error
import urllib.parse
import urllib.request

MAX_BYTES = 8 * 1024 * 1024
SHA = re.compile(r'[0-9a-f]{40}')
NAME = re.compile(r'[A-Za-z0-9_.-]{1,256}')
VERSION = re.compile(r'[A-Za-z0-9_.+\-]{1,128}')


class SnapshotError(ValueError):
    pass


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise SnapshotError('duplicate-json-key')
        result[key] = value
    return result


def load_json(data):
    if len(data) > MAX_BYTES:
        raise SnapshotError('oversize-json')
    try:
        return json.loads(data, object_pairs_hook=unique_object)
    except (ValueError, UnicodeError, RecursionError):
        raise SnapshotError('invalid-json') from None


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


class Api:
    def __init__(self, repository, token):
        if (not re.fullmatch(r'[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+', repository)
                or any(p in ('.', '..') for p in repository.split('/'))
                or not token or len(token) > 16384
                or any(not 33 <= ord(c) <= 126 for c in token)):
            raise SnapshotError('invalid-api-identity')
        self.prefix = f'https://api.github.com/repos/{repository}/'
        self.opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirect())
        self.headers = {'Authorization': 'Bearer ' + token, 'Accept': 'application/vnd.github+json',
                        'X-GitHub-Api-Version': '2022-11-28', 'User-Agent': 'weave-nuget-lock-snapshots'}

    def request(self, path, payload=None):
        if not re.fullmatch(r'(git/(commits|trees|blobs)/[0-9a-f]{40}(\?recursive=1)?|dependency-graph/snapshots)', path):
            raise SnapshotError('invalid-api-path')
        data = None if payload is None else json.dumps(payload, ensure_ascii=True).encode()
        if data is not None and (path != 'dependency-graph/snapshots' or len(data) > MAX_BYTES):
            raise SnapshotError('invalid-snapshot-request')
        request = urllib.request.Request(self.prefix + path, data=data,
                                         headers={**self.headers, 'Content-Type': 'application/json'})
        try:
            with self.opener.open(request, timeout=30) as response:
                if response.status != (200 if data is None else 201):
                    raise SnapshotError('snapshot-api-status')
                return load_json(response.read(MAX_BYTES + 1))
        except urllib.error.HTTPError as error:
            status = error.code
            error.close()
            raise SnapshotError(f'snapshot-api-http-{status}') from None
        except (urllib.error.URLError, OSError, TimeoutError):
            raise SnapshotError('snapshot-api-network') from None


def lock_manifests(path, document, blob_sha):
    if not isinstance(document, dict) or document.get('version') not in (1, 2):
        raise SnapshotError('unsupported-lock-version')
    frameworks = document.get('dependencies')
    if not isinstance(frameworks, dict) or not frameworks:
        raise SnapshotError('missing-lock-frameworks')
    manifests = {}
    for framework, packages in frameworks.items():
        if not isinstance(packages, dict) or len(packages) > 10000:
            raise SnapshotError('invalid-package-graph')
        table = {name.lower(): node for name, node in packages.items()}
        if len(table) != len(packages):
            raise SnapshotError('ambiguous-package-name')
        urls = {}
        for name, node in table.items():
            if not NAME.fullmatch(name) or not isinstance(node, dict):
                raise SnapshotError('invalid-package-node')
            if node.get('type') == 'Project':
                continue
            if node.get('type') not in ('Direct', 'Transitive', 'CentralTransitive'):
                raise SnapshotError('unsupported-dependency-type')
            version = node.get('resolved')
            if not isinstance(version, str) or not VERSION.fullmatch(version):
                raise SnapshotError('unresolved-package')
            checksum = base64.b64decode(node.get('contentHash', ''), validate=True)
            if len(checksum) != 64:
                raise SnapshotError('missing-package-integrity')
            urls[name] = 'pkg:nuget/' + name + '@' + urllib.parse.quote(version, safe='.-_')

        def children(name, visiting):
            if name not in table or name in visiting:
                raise SnapshotError('incomplete-or-cyclic-project-edge')
            if name in urls:
                return {urls[name]}
            edges = table[name].get('dependencies', {})
            if not isinstance(edges, dict):
                raise SnapshotError('invalid-project-edges')
            result = set()
            for child in edges:
                result.update(children(child.lower(), visiting | {name}))
            return result

        resolved = {}
        for name, url in urls.items():
            node = table[name]
            edges = node.get('dependencies', {})
            if not isinstance(edges, dict):
                raise SnapshotError('invalid-package-edges')
            dependencies = set()
            for child in edges:
                dependencies.update(children(child.lower(), {name}))
            resolved[url] = {'package_url': url, 'dependencies': sorted(dependencies),
                             'relationship': 'direct' if node['type'] == 'Direct' else 'indirect',
                             'scope': 'runtime'}
        key = path + '#' + framework
        manifests[key] = {'name': key, 'file': {'source_location': path},
                          'metadata': {'lock_blob': blob_sha, 'framework': framework}, 'resolved': resolved}
    return manifests


def snapshot(api, sha, ref, repository, run_id, blobs):
    if not SHA.fullmatch(sha) or not re.fullmatch(r'refs/(heads|pull)/[A-Za-z0-9_./-]+', ref) or '..' in ref:
        raise SnapshotError('invalid-source-ref')
    commit = api.request('git/commits/' + sha)
    if commit.get('sha') != sha or not SHA.fullmatch(commit.get('tree', {}).get('sha', '')):
        raise SnapshotError('commit-identity-mismatch')
    tree = api.request('git/trees/' + commit['tree']['sha'] + '?recursive=1')
    if tree.get('truncated') is not False or not isinstance(tree.get('tree'), list):
        raise SnapshotError('incomplete-source-tree')
    files = {item['path']: item for item in tree['tree']}
    if len(files) != len(tree['tree']) or len(files) > 50000:
        raise SnapshotError('invalid-source-inventory')
    projects = [p for p in files if p.endswith(('.csproj', '.fsproj', '.vbproj'))]
    locks = {str(PurePosixPath(p).parent / 'packages.lock.json') for p in projects}
    if not projects or len(locks) > 500 or any(p not in files for p in locks):
        raise SnapshotError('missing-project-lock')
    manifests = {}
    for path in sorted(locks):
        entry = files[path]
        if entry.get('type') != 'blob' or entry.get('mode') != '100644' or entry.get('size', MAX_BYTES + 1) > MAX_BYTES:
            raise SnapshotError('unsafe-lock-entry')
        blob_sha = entry['sha']
        if not SHA.fullmatch(blob_sha):
            raise SnapshotError('invalid-blob-id')
        if blob_sha not in blobs:
            blob = api.request('git/blobs/' + blob_sha)
            if blob.get('encoding') != 'base64' or blob.get('sha') != blob_sha:
                raise SnapshotError('invalid-lock-encoding')
            data = base64.b64decode(''.join(blob['content'].split()), validate=True)
            actual = hashlib.sha1(b'blob ' + str(len(data)).encode() + b'\0' + data).hexdigest()
            if actual != blob_sha or len(data) != entry['size']:
                raise SnapshotError('lock-integrity-mismatch')
            blobs[blob_sha] = load_json(data)
        manifests.update(lock_manifests(path, blobs[blob_sha], blob_sha))
    package_count = sum(len(m['resolved']) for m in manifests.values())
    if not package_count:
        raise SnapshotError('empty-nuget-graph')
    return {'version': 0, 'sha': sha, 'ref': ref,
            'job': {'id': run_id + '-' + sha, 'correlator': 'weave-nuget-lock-graph'},
            'detector': {'name': 'weave-nuget-lockfiles', 'version': '1.0.0',
                         'url': 'https://github.com/' + repository},
            'scanned': datetime.now(timezone.utc).isoformat(),
            'metadata': {'project_count': len(projects), 'lock_count': len(locks), 'package_nodes': package_count},
            'manifests': manifests}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=True)
    report = {'status': 'unavailable', 'sources': []}
    try:
        repo = os.environ['GITHUB_REPOSITORY']
        api = Api(repo, os.environ.get('GH_TOKEN', ''))
        blobs = {}
        plans = [snapshot(api, os.environ[key + '_SHA'], os.environ[key + '_REF'],
                          repo, os.environ['GITHUB_RUN_ID'], blobs) for key in ('PR_BASE', 'PR_HEAD')]
        # Validate both graphs completely before making any submission.
        for index, plan in enumerate(plans):
            result = api.request('dependency-graph/snapshots', plan)
            if result.get('result') != 'SUCCESS' or not isinstance(result.get('id'), int):
                raise SnapshotError('snapshot-not-confirmed')
            report['sources'].append({'sha': plan['sha'], 'snapshot_id': result['id'], **plan['metadata']})
            (args.output / f'snapshot-{index}.json').write_text(json.dumps(plan, indent=2) + '\n', encoding='utf-8')
        report['status'] = 'submitted'
        print('Exact base/head NuGet graphs submitted; availability and policy checks still must pass.')
    except (SnapshotError, KeyError, TypeError, ValueError, RecursionError):
        report['failure'] = 'dependency-snapshot-production-failed'
        print('::error::Dependency snapshot production failed. No clean review can be claimed.')
    (args.output / 'submission.json').write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    return 0 if report['status'] == 'submitted' else 1


if __name__ == '__main__':
    sys.exit(main())
