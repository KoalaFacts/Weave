"""No network or API credentials: verify the trusted lockfile translator."""
import base64
import copy
import hashlib
import importlib.util
import json
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location('snapshots', ROOT / 'scripts/submit_nuget_snapshots.py')
snapshots = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(snapshots)
SHA, TREE = '1' * 40, '2' * 40
HASH = base64.b64encode(b'x' * 64).decode()


def package(kind='Transitive', version='1.0.0', **extra):
    return {'type': kind, 'resolved': version, 'contentHash': HASH, **extra}


def lock():
    return {'version': 2, 'dependencies': {'net10.0': {
        'Example.Top': package('Direct', dependencies={'Example.Leaf': '1.0.0'}),
        'Example.Leaf': package(),
        'weave.local': {'type': 'Project', 'dependencies': {'Example.Leaf': '1.0.0'}}
    }}}


def manifests(document):
    return snapshots.lock_manifests('src/packages.lock.json', document, 'a' * 40)


class FakeApi:
    def __init__(self, document=None):
        data = json.dumps(lock() if document is None else document).encode()
        blob = hashlib.sha1(b'blob ' + str(len(data)).encode() + b'\0' + data).hexdigest()
        self.tree = {'truncated': False, 'tree': [
            {'path': 'src/Weave.csproj', 'type': 'blob', 'mode': '100644', 'sha': 'b' * 40, 'size': 1},
            {'path': 'src/packages.lock.json', 'type': 'blob', 'mode': '100644', 'sha': blob, 'size': len(data)}]}
        self.blob = {'sha': blob, 'encoding': 'base64', 'content': base64.b64encode(data).decode()}
        self.calls = []

    def request(self, path):
        self.calls.append(path)
        if path.startswith('git/commits/'):
            return {'sha': SHA, 'tree': {'sha': TREE}}
        if path.startswith('git/trees/'):
            return self.tree
        return self.blob


class NugetSnapshotTests(unittest.TestCase):
    def snapshot(self, api):
        return snapshots.snapshot(api, SHA, 'refs/heads/main', 'owner/repo', '1', {})

    def test_direct_transitive_edges_are_resolved_versions_not_requested_ranges(self):
        graph = next(iter(manifests(lock()).values()))['resolved']
        self.assertEqual(len(graph), 2)
        top = graph['pkg:nuget/example.top@1.0.0']
        self.assertEqual(top['relationship'], 'direct')
        self.assertEqual(top['dependencies'], ['pkg:nuget/example.leaf@1.0.0'])
        self.assertEqual(graph['pkg:nuget/example.leaf@1.0.0']['relationship'], 'indirect')

    def test_project_edges_expand_to_real_packages_not_fictitious_nuget_projects(self):
        value = lock()
        value['dependencies']['net10.0']['Example.Top']['dependencies'] = {'WEAVE.LOCAL': '1.0.0'}
        graph = next(iter(manifests(value).values()))['resolved']
        self.assertEqual(graph['pkg:nuget/example.top@1.0.0']['dependencies'], ['pkg:nuget/example.leaf@1.0.0'])
        self.assertFalse(any('weave.local' in key for key in graph))

    def test_actual_version_change_changes_snapshot_identity(self):
        old = lock()
        changed = copy.deepcopy(old)
        changed['dependencies']['net10.0']['Example.Leaf']['resolved'] = '2.0.0'
        before = next(iter(manifests(old).values()))['resolved']
        after = next(iter(manifests(changed).values()))['resolved']
        self.assertEqual(set(after) - set(before), {'pkg:nuget/example.leaf@2.0.0'})
        self.assertEqual(after['pkg:nuget/example.top@1.0.0']['dependencies'], ['pkg:nuget/example.leaf@2.0.0'])

    def test_multiple_frameworks_and_central_transitive_pins_are_not_dropped(self):
        value = lock()
        value['dependencies']['net8.0'] = {'Other': package('CentralTransitive', '3.0.0')}
        self.assertEqual(len(manifests(value)), 2)
        self.assertIn('pkg:nuget/other@3.0.0', manifests(value)['src/packages.lock.json#net8.0']['resolved'])

    def test_unknown_types_missing_versions_hashes_and_edges_fail_closed(self):
        for change in ({'type': 'FutureType'}, {'resolved': ''}, {'contentHash': ''},
                       {'dependencies': {'Not.In.Lock': '1.0.0'}}):
            with self.subTest(change=change):
                value = lock()
                value['dependencies']['net10.0']['Example.Top'].update(change)
                with self.assertRaises(ValueError):
                    manifests(value)

    def test_cyclic_project_edges_and_case_collisions_are_rejected(self):
        value = lock()
        value['dependencies']['net10.0']['weave.local']['dependencies'] = {'weave.local': '1.0.0'}
        value['dependencies']['net10.0']['Example.Top']['dependencies'] = {'weave.local': '1.0.0'}
        with self.assertRaises(ValueError):
            manifests(value)
        value = lock()
        value['dependencies']['net10.0']['example.top'] = package()
        with self.assertRaises(ValueError):
            manifests(value)

    def test_exact_commit_and_lock_blob_are_preserved_in_submission(self):
        api = FakeApi()
        result = self.snapshot(api)
        self.assertEqual(result['sha'], SHA)
        self.assertEqual(result['metadata'], {'project_count': 1, 'lock_count': 1, 'package_nodes': 2})
        graph = next(iter(result['manifests'].values()))
        self.assertEqual(graph['metadata']['lock_blob'], api.blob['sha'])
        self.assertTrue(all(path.startswith('git/') for path in api.calls))

    def test_truncated_tree_missing_lock_and_symlink_never_create_empty_success(self):
        for condition in ('truncated', 'missing', 'symlink'):
            with self.subTest(condition=condition):
                api = FakeApi()
                if condition == 'truncated': api.tree['truncated'] = True
                if condition == 'missing': api.tree['tree'].pop()
                if condition == 'symlink': api.tree['tree'][1]['mode'] = '120000'
                with self.assertRaises(ValueError): self.snapshot(api)

    def test_tampered_blob_is_rejected(self):
        api = FakeApi()
        api.blob['content'] = base64.b64encode(b'{}').decode()
        with self.assertRaises(ValueError): self.snapshot(api)

    def test_empty_graph_duplicate_json_and_bad_identity_are_rejected(self):
        with self.assertRaises(ValueError): self.snapshot(FakeApi({'version': 2, 'dependencies': {'net10.0': {}}}))
        with self.assertRaises(ValueError): snapshots.load_json(b'{"version":1,"version":2}')
        with self.assertRaises(ValueError): snapshots.Api('../repo', 'token')
        with self.assertRaises(ValueError): snapshots.Api('owner/repo', 'bad\r\nheader')
        api = snapshots.Api('owner/repo', 'synthetic-token')
        with self.assertRaises(ValueError): api.request('https://evil.invalid/')
        self.assertIsNone(snapshots.NoRedirect().redirect_request(None, None, 302, '', {}, 'https://evil.invalid'))


if __name__ == '__main__':
    unittest.main()
