import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { mkdir, readFile } from 'node:fs/promises';
import { resolve } from 'node:path';
import { test } from 'node:test';
import { BrowserFixture } from './playwright.ts';

test('native Playwright pages preserve literal data, isolation, navigation and cleanup', { timeout: 60000 }, async t => {
    const evidence = resolve(process.env.WEAVE_BROWSER_EVIDENCE ?? 'test-results/browser', 'fixture');
    await mkdir(evidence, { recursive: true });
    let externalRequests = 0;
    const external = createServer((_request, response) => { externalRequests++; response.end('unexpected'); });
    await new Promise(resolve => external.listen(0, '127.0.0.1', resolve));
    const externalOrigin = 'http://127.0.0.1:' + external.address().port;
    const server = createServer((request, response) => {
        if (request.url === '/redirect') {
            response.writeHead(302, { location: externalOrigin + '/redirect-target' }).end();
        } else if (request.url === '/touch') {
            response.setHeader('Content-Type', 'text/html');
            response.end(`<!doctype html><html><head><meta name="viewport" content="width=device-width, initial-scale=1"></head>
                <body style="margin:0;height:2400px"><input id="control" value="synthetic masked control">
                <button id="touch-probe" style="position:fixed;bottom:10px;left:10px">Touch probe</button>
                <script>document.getElementById('touch-probe').addEventListener('touchstart', event => {
                    globalThis.touchProbe = { trusted: event.isTrusted, points: navigator.maxTouchPoints };
                });</script></body></html>`);
        } else {
            response.setHeader('Content-Type', 'text/html');
            response.end('<!doctype html><html><body><h1>Fixture ready</h1></body></html>');
        }
    });
    await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
    const fixture = new BrowserFixture(evidence, 'http://127.0.0.1:' + server.address().port);
    try {
        await fixture.start();
        const first = await fixture.page('first');
        const second = await fixture.page('second');
        await t.test('special IDs and values stay literal before and after document navigation', async () => {
            const id = 'id-";globalThis.injected=true;//\\\u2028\u2029';
            const value = '</script>\n` ${globalThis.injected=true} "quoted" \\\u2028\u2029';
            for (const path of ['/first', '/second']) {
                await first.goto(fixture.origin + path, { waitUntil: 'load' });
                assert.equal(await first.evaluate(() => document.readyState), 'complete');
                const handle = await first.evaluateHandle(id => {
                    const field = document.createElement('textarea');
                    field.id = id;
                    document.body.append(field);
                    return document.getElementById(id);
                }, id);
                try {
                    await handle.asElement().fill(value);
                    assert.equal(await first.evaluate(id => document.getElementById(id).value, id), value);
                    assert.equal(await first.evaluate(() => globalThis.injected), undefined);
                } finally { await handle.dispose(); }
            }
        });
        await t.test('page evaluation reports exceptions and permits subsequent work', async () => {
            await assert.rejects(first.evaluate(() => { throw Error('intentional browser exception'); }), /intentional browser exception/);
            assert.equal(await first.locator('h1').textContent(), 'Fixture ready');
            await first.goto(fixture.origin + '/after-exception');
            assert.equal(await first.locator('h1').textContent(), 'Fixture ready');
        });
        await t.test('separate contexts isolate storage and closing one leaves its peer usable', async () => {
            assert.notEqual(first.context(), second.context());
            await first.evaluate(() => { localStorage.setItem('fixture', 'isolated'); sessionStorage.setItem('fixture', 'isolated'); });
            assert.equal(await second.evaluate(() => localStorage.length + sessionStorage.length), 0);
            await first.context().close();
            assert.equal(first.isClosed(), true);
            assert.equal(await second.locator('h1').textContent(), 'Fixture ready');
        });
        await t.test('other origins and redirects never reach their target', async () => {
            assert.deepEqual(fixture.errors, []);
            const fetchResult = async url => second.evaluate(async url => {
                try { await fetch(url); return 'unexpected success'; } catch { return 'blocked'; }
            }, url);
            assert.equal(await fetchResult(externalOrigin + '/direct?private=query'), 'blocked');
            assert.equal(await fetchResult(fixture.origin + '/redirect'), 'blocked');
            assert.equal(externalRequests, 0);
            assert.deepEqual(fixture.errors.splice(0), ['second: blocked unexpected origin', 'second: blocked redirect']);
            assert.ok(fixture.trace.every(entry => !JSON.stringify(entry).includes('private=query')));
        });
        await t.test('mobile screenshots preserve touch, viewport and scroll across desktop captures', async () => {
            const viewport = { width: 390, height: 844 };
            const mobile = await fixture.page('touch', '/touch', { viewport, isMobile: true });
            const state = () => mobile.evaluate(() => ({
                width: innerWidth, height: innerHeight, x: scrollX, y: scrollY,
                points: navigator.maxTouchPoints, coarse: matchMedia('(pointer: coarse)').matches
            }));
            await mobile.evaluate(() => scrollTo({ left: 0, top: 123, behavior: 'instant' }));
            const before = await state();
            assert.equal(before.width, viewport.width);
            assert.equal(before.height, viewport.height);
            assert.ok(before.points > 0);
            assert.equal(before.coarse, true);
            assert.equal(before.y, 123);
            await second.evaluate(() => { document.body.style.height = '2400px'; });
            for (let round = 0; round < 2; round++) {
                const name = 'touch-round-' + round;
                await fixture.screenshot(mobile, name);
                assert.deepEqual(mobile.viewportSize(), viewport);
                assert.deepEqual(await state(), before, 'Mobile capture must preserve native touch and geometry.');
                const index = JSON.parse(await readFile(resolve(evidence, name + '-tiles.json'), 'utf8'));
                assert.deepEqual(index.tiles.map(tile => tile.y), [0, 844, 1556]);
                for (const tile of index.tiles) {
                    const png = await readFile(resolve(evidence, tile.file));
                    assert.equal(png.readUInt32BE(16), viewport.width);
                    assert.equal(png.readUInt32BE(20), viewport.height);
                }
                await fixture.screenshot(second, name + '-desktop');
                assert.deepEqual(await state(), before, 'Desktop capture must leave the mobile context untouched.');
                await mobile.evaluate(() => { globalThis.touchProbe = null; });
                await mobile.locator('#touch-probe').tap();
                assert.deepEqual(await mobile.evaluate(() => globalThis.touchProbe), { trusted: true, points: before.points });
            }
        });
        await t.test('browser close cancels pending operations and is safe to repeat', async () => {
            const browser = fixture.browser;
            const pending = assert.rejects(second.locator('#never-present').waitFor({ timeout: 0 }), /closed/i);
            await fixture.close();
            await pending;
            assert.equal(second.isClosed(), true);
            assert.equal(browser.isConnected(), false);
            assert.deepEqual(browser.contexts(), []);
            await fixture.close();
            assert.deepEqual(fixture.errors, []);
        });
    } finally {
        try { await fixture.close(); }
        finally {
            await Promise.all([server, external].map(server => new Promise((resolve, reject) => server.close(error => error ? reject(error) : resolve()))));
        }
    }
});
