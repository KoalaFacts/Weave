import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
import { echoPluginVersion } from '../dist/index.js';

const npmCli = process.env.npm_execpath;
if (!npmCli)
    throw new Error('Run this check through npm run pack:check.');

const packed = spawnSync(process.execPath, [npmCli, 'pack', '--dry-run', '--json'], {
    cwd: new URL('..', import.meta.url),
    encoding: 'utf8'
});
if (packed.status !== 0)
    throw new Error(packed.stderr || packed.stdout || 'npm pack failed.');

const report = JSON.parse(packed.stdout);
const entry = Array.isArray(report) ? report[0] : report['@koalafacts/weave-plugin-echo'];
assert.equal(entry.name, '@koalafacts/weave-plugin-echo');
assert.equal(echoPluginVersion, entry.version);
assert.equal(JSON.parse(readFileSync(new URL('../package.json', import.meta.url), 'utf8')).version, entry.version);
const files = new Set(entry.files.map(file => file.path));
assert.deepEqual(files, new Set([
    'LICENSE-AGPL',
    'LICENSE-MIT',
    'README.md',
    'dist/cli.d.ts',
    'dist/cli.js',
    'dist/index.d.ts',
    'dist/index.js',
    'dist/stdio.d.ts',
    'dist/stdio.js',
    'package.json'
]));
process.stdout.write(`Checked ${entry.name}@${entry.version}: ${files.size} release files.\n`);
