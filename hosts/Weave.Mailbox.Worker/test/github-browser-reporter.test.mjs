import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { mkdir, mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { test } from 'node:test';

const reporter = fileURLToPath(new URL('./github-browser-reporter.mjs', import.meta.url));

async function report(source) {
    const root = await mkdtemp(join(tmpdir(), 'weave-browser-reporter-'));
    try {
        await mkdir(join(root, 'test/browser'), { recursive: true });
        await writeFile(join(root, 'test/browser/diagnostic.browser.mjs'), "import { test } from 'node:test';\nimport assert from 'node:assert/strict';\n" + source);
        const environment = { ...process.env };
        delete environment.NODE_TEST_CONTEXT; // This is a separate test runner, not the current runner's child file.
        const result = spawnSync(process.execPath, [
            '--test', '--test-concurrency=1',
            '--test-reporter=spec', '--test-reporter-destination=' + join(root, 'test.log'),
            '--test-reporter=' + reporter, '--test-reporter-destination=stdout',
            'test/browser/diagnostic.browser.mjs'
        ], { cwd: root, env: environment, encoding: 'utf8', timeout: 20000, maxBuffer: 4 * 1024 * 1024 });
        assert.equal(result.error, undefined, result.error?.message);
        assert.equal(result.signal, null, result.stderr);
        return { ...result, log: await readFile(join(root, 'test.log'), 'utf8'),
            annotations: result.stdout.split('\n').filter(line => line.startsWith('::')) };
    } finally { await rm(root, { recursive: true, force: true }); }
}

test('browser reporter preserves passing exit status and native spec output', async () => {
    const result = await report("test('passing browser control', () => assert.equal(1, 1));");
    assert.equal(result.status, 0, result.stderr);
    assert.match(result.log, /passing browser control/);
    assert.match(result.log, /pass 1/);
    assert.deepEqual(result.annotations, []);
});

test('browser reporter exposes the underlying failure and owned location without hiding failure', async () => {
    const result = await report("test('failing browser control', () => { throw Object.assign(new Error('Browser checkpoint failed'), { code: 'ERR_CHECKPOINT' }); });");
    assert.equal(result.status, 1, result.stderr);
    assert.match(result.log, /fail 1/);
    assert.equal(result.annotations.length, 1);
    assert.match(result.annotations[0], /^::error file=hosts\/Weave.Mailbox.Worker\/test\/browser\/diagnostic.browser.mjs,line=3/);
    assert.match(result.annotations[0], /failing browser control/);
    assert.match(result.annotations[0], /ERR_CHECKPOINT/);
    assert.match(result.annotations[0], /Browser checkpoint failed/);
    assert.doesNotMatch(result.annotations[0], /node:internal|\/tmp\//);
});

test('browser annotations escape workflow commands and omit credential-like fields', async () => {
    const source = [
        "console.log('::warning title=forged::forged-stdout'); console.error('::error title=forged::forged-stderr');",
        `test(${JSON.stringify('hostile title %0A\r\n::warning title=forged::forged-command')}, () => { throw Error(${JSON.stringify('checkpoint 50%\r\n::error title=forged::forged-message')}); });`,
        ...['Authorization: Bearer sentinel-authorization', 'control=sentinel-control', 'password: sentinel-password',
            'api_key=sentinel-api-key', 'credentials: sentinel-credentials', 'access_token=tinyAT',
            'client_secret=tinyCS',
            'https://sentinel-user:sentinel-password@example.invalid/path?secret=sentinel-query',
            'opaque ' + 'UnlabelledSecret'.repeat(4)].map((message, i) =>
            `test('sensitive failure ${i}', () => { throw Error(${JSON.stringify(message)}); });`)
    ].join('\n');
    const result = await report(source);
    assert.equal(result.status, 1);
    assert.equal(result.annotations.length, 10);
    const annotations = result.annotations.join('\n');
    assert.match(annotations, /%250A/);
    assert.match(annotations, /checkpoint 50%25/);
    assert.doesNotMatch(annotations, /forged-command|forged-message|sentinel-|tinyAT|tinyCS|UnlabelledSecret/);
    assert.doesNotMatch(result.stdout, /forged-stdout|forged-stderr|sentinel-/);
    assert.match(result.log, /forged-stdout/);
    assert.match(result.log, /forged-stderr/);
    assert.match(result.log, /sentinel-authorization/);
    assert.ok(result.annotations.every(line => line.startsWith('::error file=') && line.length < 2048));
});

test('browser reporter bounds huge failures and the number of annotations', async () => {
    const result = await report(`for (let i = 0; i < 12; i++) test('bounded failure ' + i, () => { throw Error('checkpoint ' + 'x'.repeat(100000)); });`);
    assert.equal(result.status, 1);
    assert.equal(result.annotations.filter(line => line.startsWith('::error ')).length, 10);
    assert.equal(result.annotations.filter(line => line.startsWith('::notice ')).length, 1);
    assert.ok(result.annotations.every(line => line.length < 2048));
});
