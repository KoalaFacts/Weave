import assert from 'node:assert/strict';
import { test } from 'node:test';
import { mkdtemp, readFile, readdir, rm } from 'node:fs/promises';
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

function mobilePage({ height = 2200, failCapture = 0, growAfterCapture = false, bytes = 3 } = {}) {
    const captures = [];
    const mask = {};
    const state = { width: 390, height, x: 0, y: 100 };
    return {
        captures, state, mask,
        viewportSize: () => ({ width: 390, height: 844 }),
        locator: () => mask,
        async evaluate(_callback, position) {
            if (position) Object.assign(state, position);
            return { ...state };
        },
        async screenshot(options) {
            captures.push({ ...options, y: state.y });
            if (captures.length === failCapture) throw Error('capture interrupted');
            if (growAfterCapture) state.height++;
            return Buffer.alloc(bytes);
        }
    };
}

test('mobile evidence covers the document with bounded viewport tiles and restores scroll', async t => {
    const root = await mkdtemp(join(tmpdir(), 'weave-browser-tiles-'));
    t.after(() => rm(root, { recursive: true, force: true }));
    const fixture = new BrowserFixture(root, 'http://127.0.0.1:1234');
    const page = mobilePage();
    fixture.mobilePages.add(page);
    await fixture.screenshot(page, 'mobile');
    assert.deepEqual(page.captures.map(capture => capture.y), [0, 844, 1356]);
    assert.ok(page.captures.every(capture => capture.fullPage === false && capture.mask[0] === page.mask));
    assert.equal(page.state.y, 100);
    assert.equal(fixture.screenshots, 3);
    const index = JSON.parse(await readFile(join(root, 'mobile-tiles.json'), 'utf8'));
    assert.deepEqual(index.viewport, { width: 390, height: 844 });
    assert.deepEqual(index.document, { width: 390, height: 2200 });
    assert.deepEqual(index.tiles.map(tile => [tile.file, tile.y]), [
        ['mobile-tile-01.png', 0], ['mobile-tile-02.png', 844], ['mobile-tile-03.png', 1356]
    ]);
    assert.equal(index.tiles.at(-1).y + index.viewport.height, index.document.height);
});

test('mobile evidence rejects incomplete or excessive capture and restores scroll after failure', async t => {
    for (const [name, options, priorScreenshots, expected] of [
        ['interruption', { failCapture: 2 }, 0, /capture interrupted/],
        ['growth', { growAfterCapture: true }, 0, /changed during screenshot/],
        ['count', {}, 30, /count exceeded/],
        ['dimensions', { height: 20001 }, 0, /dimension budget/],
        ['bytes', { bytes: 8 * 1024 * 1024 + 1 }, 0, /byte budget/]
    ]) await t.test(name, async t => {
        const root = await mkdtemp(join(tmpdir(), 'weave-browser-tiles-'));
        t.after(() => rm(root, { recursive: true, force: true }));
        const fixture = new BrowserFixture(root, 'http://127.0.0.1:1234');
        const page = mobilePage(options);
        fixture.mobilePages.add(page);
        fixture.screenshots = priorScreenshots;
        await assert.rejects(fixture.screenshot(page, name), expected);
        assert.equal(page.state.y, 100);
        assert.ok(!(await readdir(root)).includes(name + '-tiles.json'), 'Incomplete evidence must not publish a coverage index.');
    });
});

test('failed mobile recapture removes the previous coverage index', async t => {
    for (const failure of ['capture', 'count', 'dimensions']) await t.test(failure, async t => {
        const root = await mkdtemp(join(tmpdir(), 'weave-browser-tiles-'));
        t.after(() => rm(root, { recursive: true, force: true }));
        const fixture = new BrowserFixture(root, 'http://127.0.0.1:1234');
        const page = mobilePage();
        fixture.mobilePages.add(page);
        await fixture.screenshot(page, 'repeated');
        assert.ok((await readdir(root)).includes('repeated-tiles.json'));
        if (failure === 'capture') page.screenshot = async () => { throw Error('capture interrupted'); };
        if (failure === 'count') fixture.screenshots = 31;
        if (failure === 'dimensions') page.state.height = 20001;
        await assert.rejects(fixture.screenshot(page, 'repeated'));
        assert.ok(!(await readdir(root)).includes('repeated-tiles.json'), 'A failed recapture must not retain the prior coverage index.');
    });
});

test('mobile evidence retains capture and restoration errors together', async t => {
    const root = await mkdtemp(join(tmpdir(), 'weave-browser-tiles-'));
    t.after(() => rm(root, { recursive: true, force: true }));
    const fixture = new BrowserFixture(root, 'http://127.0.0.1:1234');
    const page = mobilePage();
    fixture.mobilePages.add(page);
    const captureError = Error('capture interrupted');
    const restoreError = Error('restoration interrupted');
    page.screenshot = async () => { throw captureError; };
    const evaluate = page.evaluate;
    page.evaluate = async (callback, position) => {
        if (position?.y === 100) throw restoreError;
        return evaluate(callback, position);
    };
    await assert.rejects(fixture.screenshot(page, 'dual-failure'), error => {
        assert.ok(error instanceof AggregateError);
        assert.equal(error.cause, captureError);
        assert.deepEqual(error.errors, [captureError, restoreError]);
        return true;
    });
    assert.ok(!(await readdir(root)).includes('dual-failure-tiles.json'));
});
