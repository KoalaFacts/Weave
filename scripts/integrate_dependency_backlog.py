"""One-time, pinned dependency-PR integration. Never updates main or force-pushes."""
from pathlib import Path
import json
import subprocess
import xml.etree.ElementTree as ET

MAIN = 'e8931f0d8e81c2a19f331b463e325c29c05932b6'
PULLS = [
    (72, 'aa04d1b7dccd02336a98b4b9982fee2e5ad36b91', {'Aspire.Hosting.AppHost': '13.3.0'}),
    (73, '6f5093c6a39cac7f742d56f1b93fb98473c2fb97', {'Aspire.Hosting.Orleans': '13.3.0'}),
    (74, 'b228e3f0dc0975f9efc79386305c7dce02b18cc4', {'Aspire.Hosting.Redis': '13.3.0'}),
    (75, 'e89db3272b0001d0dc13f67ca7b00e5fbfe2cabb', {'Microsoft.Extensions.AI': '10.5.2', 'Microsoft.Extensions.AI.Abstractions': '10.5.2'}),
    (77, 'a90d2592396546ec23285f6661f4d36e3aa9cf40', {'Microsoft.Extensions.AI.Abstractions': '10.5.2', 'Microsoft.Extensions.AI.OpenAI': '10.5.2'}),
    (82, '0ede0072abbf1ac1843140487e8e9a805a1d169f', {}),
    (86, '489932c21161d8b324e548a2133a97c05f5607db', {}),
    (87, '3142057deac0abe0fd0dfb6ac5fb16ea68aa12bd', {}),
]
SECURITY = {'Microsoft.OpenApi': '2.7.5', 'MessagePack': '2.5.302', 'SSH.NET': '2026.0.0',
            'SQLitePCLRaw.bundle_e_sqlite3': '2.1.12', 'SQLitePCLRaw.lib.e_sqlite3': '3.53.3'}


def git(*args):
    return subprocess.check_output(['git', *args], text=True).strip()


def versions(text):
    entries = ET.fromstring(text).findall('.//PackageVersion')
    result = {x.attrib['Include']: x.attrib['Version'] for x in entries}
    if len(result) != len(entries):
        raise RuntimeError('Duplicate central package identities')
    return result


def integrate():
    if git('branch', '--show-current') != 'integration/pr-backlog-2026-09-19':
        raise RuntimeError('This script only operates on the isolated integration branch')
    if git('status', '--porcelain', '--untracked-files=no'):
        raise RuntimeError('Tracked working tree must be clean')
    subprocess.run(['git', 'merge-base', '--is-ancestor', MAIN, 'HEAD'], check=True)
    canonical = git('show', MAIN + ':Directory.Packages.props') + '\n'
    if Path('Directory.Packages.props').read_text() != canonical:
        raise RuntimeError('Integration branch dependency file changed since inventory')
    current = versions(canonical)
    for key, expected in SECURITY.items():
        if current.get(key) != expected:
            raise RuntimeError('Reviewed security baseline changed: ' + key)
    evidence = []
    for number, sha, updates in PULLS:
        ancestor = subprocess.run(['git', 'merge-base', '--is-ancestor', sha, 'HEAD'], check=False)
        if ancestor.returncode == 0:
            raise RuntimeError('This is a one-time integration; PR already in branch: ' + str(number))
        base = git('merge-base', MAIN, sha)
        upstream = git('diff', '--name-only', base, sha).splitlines()
        if not upstream or any(p != 'Directory.Packages.props' and not p.endswith('/packages.lock.json') for p in upstream):
            raise RuntimeError('Unreviewed non-dependency changes in PR ' + str(number))
        previous = git('rev-parse', 'HEAD')
        previous_locks = set(p for p in git('ls-files').splitlines() if p.endswith('/packages.lock.json'))
        result = subprocess.run(['git', 'merge', '--no-ff', '--no-commit', sha], check=False, capture_output=True, text=True)
        print(result.stdout, result.stderr, flush=True)
        conflicts = git('diff', '--name-only', '--diff-filter=U').splitlines()
        changed = git('diff', '--name-only', previous).splitlines()
        if result.returncode not in (0, 1) or any(p != 'Directory.Packages.props' and not p.endswith('/packages.lock.json') for p in changed):
            raise RuntimeError('Unexpected merge result for PR ' + str(number))
        all_locks = set(p for p in git('ls-files').splitlines() if p.endswith('/packages.lock.json'))
        if previous_locks:
            subprocess.run(['git', 'restore', '--source=' + previous, '--staged', '--worktree', '--', *sorted(previous_locks)], check=True)
        new_locks = all_locks - previous_locks
        if new_locks:
            subprocess.run(['git', 'rm', '-f', '--ignore-unmatch', '--', *sorted(new_locks)], check=True)
        for key, value in updates.items():
            old = f'Include="{key}" Version="{current[key]}"'
            if canonical.count(old) != 1:
                raise RuntimeError('Non-unique package entry: ' + key)
            canonical = canonical.replace(old, f'Include="{key}" Version="{value}"', 1)
            current[key] = value
        Path('Directory.Packages.props').write_text(canonical)
        subprocess.run(['git', 'add', 'Directory.Packages.props'], check=True)
        if git('diff', '--name-only', '--diff-filter=U'):
            raise RuntimeError('Unresolved conflict')
        if any(versions(canonical).get(key) != value for key, value in SECURITY.items()):
            raise RuntimeError('Security dependency downgrade')
        subprocess.run(['git', 'diff', '--cached', '--check'], check=True)
        note = 'retain equal or stronger current security pin' if not updates else 'resolve against flat source layout'
        subprocess.run(['git', 'commit', '-m', f'merge: integrate dependency PR #{number}; {note}'], check=True)
        evidence.append({'pr': number, 'head': sha, 'merge_commit': git('rev-parse', 'HEAD'), 'conflicts': conflicts,
                         'versions': updates, 'resolution': note, 'locks': 'regenerate for the active project graph before validation'})
    Path('/tmp/weave-backlog-merges.json').write_text(json.dumps(evidence, indent=2) + '\n')
    print(json.dumps(evidence, indent=2))


if __name__ == '__main__':
    integrate()
