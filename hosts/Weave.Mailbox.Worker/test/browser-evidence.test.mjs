import assert from 'node:assert/strict';
import { test } from 'node:test';
import { mkdtemp, readFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { BrowserFixture } from './browser/playwright.mjs';

test('browser cleanup retains a bounded network history even without a successful launch', async () => {
    const root = await mkdtemp(join(tmpdir(), 'weave-browser-evidence-'));
    try {
        const browser = new BrowserFixture(root, 'http://127.0.0.1:1234');
        for (let index = 0; index < 1000; index++) {
            browser.trace.push({ page: 'fixture', method: 'GET', path: '/entry/' + index, status: 200 });
        }
        await browser.close();
        const entries = JSON.parse(await readFile(join(root, 'network.json'), 'utf8'));
        assert.equal(entries.length, 256, 'Evidence retains only the most recent 256 requests.');
        assert.equal(entries.at(-1).path, '/entry/999');
        assert.deepEqual(JSON.parse(await readFile(join(root, 'browser-errors.json'), 'utf8')), []);
    } finally {
        await rm(root, { recursive: true, force: true });
    }
});
