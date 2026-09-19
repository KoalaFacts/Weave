"""Apply the reviewed UTF-8 edit set after verifying every original and result hash."""
import hashlib
import json
from pathlib import Path, PurePosixPath
import shutil
import subprocess

root = Path.cwd().resolve()
changes = []
for file in sorted((root / '.operation-changes').glob('[0-9].json')):
    changes.extend(json.loads(file.read_text()))
if len(changes) != 33:
    raise SystemExit('Expected exactly 33 reviewed files.')
prepared = {}
for item in changes:
    name = item['path']
    parts = PurePosixPath(name).parts
    if name in prepared or not parts or parts[0] not in {'src', 'hosts', 'extensions', 'tests'} or '..' in parts or '\\' in name:
        raise SystemExit('Unexpected review path: ' + name)
    path = root / name
    if not path.resolve().is_relative_to(root) or any(p.is_symlink() for p in [path, *path.parents]):
        raise SystemExit('Linked review path: ' + name)
    data = path.read_bytes() if path.exists() else b''
    before = hashlib.sha256(data).hexdigest() if path.exists() else None
    if before != item['before']:
        raise SystemExit('Source changed: ' + name)
    text = data.decode('utf-8')
    previous = 0
    for edit in item['edits']:
        if not (type(edit['start']) is int and type(edit['end']) is int and previous <= edit['start'] <= edit['end'] <= len(text)):
            raise SystemExit('Invalid edit offsets: ' + name)
        previous = edit['end']
    for edit in reversed(item['edits']):
        text = text[:edit['start']] + edit['text'] + text[edit['end']:]
    result = text.encode('utf-8')
    if hashlib.sha256(result).hexdigest() != item['after']:
        raise SystemExit('Result digest mismatch: ' + name)
    prepared[name] = result
for name, result in prepared.items():
    path = root / name
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(result)
# This job has contents permission only. Workflow cleanup is a separate authorized connector write.
shutil.rmtree(root / '.operation-changes')
subprocess.run(['git', 'add', '--', *prepared, '.operation-changes'], check=True)
subprocess.run(['git', 'diff', '--cached', '--check'], check=True)
print('Applied all 33 verified files without changing workflows.')
