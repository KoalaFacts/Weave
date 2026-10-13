import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { mkdir } from 'node:fs/promises';
import { resolve } from 'node:path';
import { test } from 'node:test';
import { BrowserFixture } from './playwright.mjs';

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
