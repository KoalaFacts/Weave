import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { copyFile, mkdir, mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { test } from 'node:test';

const reporter = fileURLToPath(new URL('./github-browser-reporter.ts', import.meta.url));
const runner = new URL('./fixtures/browser-reporter-runner.ts', import.meta.url);

async function report(tests, { stdout = [], stderr = [] } = {}) {
    const root = await mkdtemp(join(tmpdir(), 'weave-browser-reporter-'));
    try {
        await mkdir(join(root, 'test/browser'), { recursive: true });
        await copyFile(runner, join(root, 'test/browser/diagnostic.browser.ts'));
        await writeFile(join(root, 'test/browser/diagnostic.json'), JSON.stringify({ tests, stdout, stderr }));
        const environment = { ...process.env };
        delete environment.NODE_TEST_CONTEXT; // This is a separate test runner, not the current runner's child file.
        const result = spawnSync(process.execPath, [
            '--test', '--test-concurrency=1',
            '--test-reporter=spec', '--test-reporter-destination=' + join(root, 'test.log'),
            '--test-reporter=' + reporter, '--test-reporter-destination=stdout',
            'test/browser/diagnostic.browser.ts'
        ], { cwd: root, env: environment, encoding: 'utf8', timeout: 20000, maxBuffer: 4 * 1024 * 1024 });
        assert.equal(result.error, undefined, result.error?.message);
        assert.equal(result.signal, null, result.stderr);
        return { ...result, log: await readFile(join(root, 'test.log'), 'utf8'),
            annotations: result.stdout.split('\n').filter(line => line.startsWith('::')) };
    } finally { await rm(root, { recursive: true, force: true }); }
}

test('browser reporter preserves passing exit status and native spec output', async () => {
    const result = await report([{ name: 'passing browser control' }]);
    assert.equal(result.status, 0, result.stderr);
    assert.match(result.log, /passing browser control/);
    assert.match(result.log, /pass 1/);
    assert.deepEqual(result.annotations, []);
});

test('browser reporter exposes the underlying failure and owned location without hiding failure', async () => {
    const result = await report([{ name: 'failing browser control',
        error: { message: 'Browser checkpoint failed', code: 'ERR_CHECKPOINT' } }]);
    assert.equal(result.status, 1, result.stderr);
    assert.match(result.log, /fail 1/);
    assert.equal(result.annotations.length, 1);
    assert.match(result.annotations[0], /^::error file=typescript\/mailbox-worker\/test\/browser\/diagnostic.browser.ts,line=11,/);
    assert.match(result.annotations[0], /failing browser control/);
    assert.match(result.annotations[0], /ERR_CHECKPOINT/);
    assert.match(result.annotations[0], /Browser checkpoint failed/);
    assert.doesNotMatch(result.annotations[0], /node:internal|\/tmp\//);
});

test('browser annotations escape workflow commands and omit credential-like fields', async () => {
    const tests = [
        { name: 'hostile title %0A\r\n::warning title=forged::forged-command',
            error: { message: 'checkpoint 50%\r\n::error title=forged::forged-message' } },
        ...['Authorization: Bearer sentinel-authorization', 'control=sentinel-control', 'password: sentinel-password',
            'api_key=sentinel-api-key', 'credentials: sentinel-credentials', 'access_token=tinyAT',
            'client_secret=tinyCS',
            'https://sentinel-user:sentinel-password@example.invalid/path?secret=sentinel-query',
            'opaque ' + 'UnlabelledSecret'.repeat(4)].map((message, i) =>
            ({ name: 'sensitive failure ' + i, error: { message } }))
    ];
    const result = await report(tests, {
        stdout: ['::warning title=forged::forged-stdout'],
        stderr: ['::error title=forged::forged-stderr']
    });
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
    const result = await report(Array.from({ length: 12 }, (_, i) => ({
        name: 'bounded failure ' + i, error: { message: 'checkpoint ' + 'x'.repeat(100000) }
    })));
    assert.equal(result.status, 1);
    assert.equal(result.annotations.filter(line => line.startsWith('::error ')).length, 10);
    assert.equal(result.annotations.filter(line => line.startsWith('::notice ')).length, 1);
    assert.ok(result.annotations.every(line => line.length < 2048));
});

test('browser annotations reject control and ANSI text before matching credentials', async () => {
    const messages = ['access_token\u0000=tinyAT', 'client_secret\u001b[2K=tinyCS',
        'access_to\u0000ken=tinyAT', 'password\t tinyPW', 'token\u200b=tinyTK',
        'client_secret\u001b]0;title\u0007=tinyCS'];
    const result = await report(messages.map((message, i) =>
        ({ name: 'nonprinting failure ' + i, error: { message } })));
    assert.equal(result.status, 1);
    assert.equal(result.annotations.length, messages.length);
    assert.doesNotMatch(result.stdout, /tinyAT|tinyCS|tinyPW|tinyTK|[\u0000\u001b\u0007\u200b]/);
    assert.ok(result.annotations.every(line => line.includes('test/browser/diagnostic.browser.ts')));
    assert.match(result.log, /tinyAT/);
});
