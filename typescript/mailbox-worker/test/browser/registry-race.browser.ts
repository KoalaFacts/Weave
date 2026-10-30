import assert from 'node:assert/strict';
import { test } from 'node:test';
import { mkdir, writeFile } from 'node:fs/promises';
import { join, resolve } from 'node:path';
import { start } from '../harness.ts';
import { BrowserFixture } from './playwright.ts';

const guidance = 'Enter a public address ID, or use Browse all.';
const card = label => ({ label, addressId: label, accountId: 'synthetic-public-owner' });
const list = (label, nextCursor = null) => ({ items: label ? [card(label)] : [], nextCursor });

// Delay only synthetic directory responses at the fixture's own local origin.
// All other requests retain BrowserFixture's network restrictions.
async function directoryResponses(page, origin) {
    let waiting;
    const calls = [], errors = [], held = new Set();
    await page.route(origin + '/v1/directory**', async route => {
        const request = route.request(), url = new URL(request.url());
        const release = Promise.withResolvers();
        const delivered = Promise.withResolvers();
        const entry = { path: url.pathname + url.search, release, delivered };
        held.add(entry); calls.push(entry);
        try {
            assert.equal(url.origin, origin);
            assert.equal(request.method(), 'GET');
            assert.ok(['/v1/directory', '/v1/directory/address'].includes(url.pathname));
            assert.ok(waiting, 'Unexpected automatic directory request');
            waiting.resolve(entry); waiting = undefined;
            await route.fulfill({ contentType: 'application/json', ...await release.promise });
        } catch (error) {
            errors.push(error);
            try { await route.abort('failed'); } catch (abortError) { errors.push(abortError); }
        } finally { held.delete(entry); delivered.resolve(); }
    });
    return {
        calls,
        async request(action) {
            assert.equal(waiting, undefined);
            const next = Promise.withResolvers(); waiting = next;
            const timeout = setTimeout(() => next.reject(new Error('Directory request did not start within 10 seconds')), 10000);
            try {
                const [, entry] = await Promise.all([action(), next.promise]);
                return entry;
            } finally { clearTimeout(timeout); if (waiting === next) waiting = undefined; }
        },
        async respond(entry, data, status = 200, stale = false) {
            const received = page.waitForResponse(response => response.url() === origin + entry.path);
            entry.release.resolve({ status, body: JSON.stringify(data) });
            await entry.delivered.promise;
            await (await received).finished();
            if (!stale) {
                const expected = status === 503 ? 'Registry unavailable (503).' : data.items?.length === 0 ? 'No current public addresses.' : 'Public addresses shown below.';
                await page.locator('#status').filter({ hasText: expected }).waitFor();
            }
            // Let the completed fetch/body continuations and the resulting render run.
            await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
        },
        async close() {
            for (const entry of held) entry.release.resolve({ status: 503, body: '{}' });
            await Promise.allSettled([...held].map(entry => entry.delivered.promise));
            if (errors.length) throw new AggregateError(errors, 'Directory response fixture failed');
        }
    };
}

async function visiblePage(page) {
    return { rows: await page.locator('#results').innerText(), number: await page.locator('#pageNumber').innerText() };
}

async function capture(fixture, page, name) {
    assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1), true);
    for (const id of ['lookup', 'previous', 'next', 'retry']) assert.equal(await page.locator('#' + id).isVisible(), true);
    await fixture.screenshot(page, name);
}

test('registry empty lookup fences pending pages and restores desktop/touch recovery controls', { timeout: 180000 }, async () => {
    const evidence = join(resolve(process.env.WEAVE_BROWSER_EVIDENCE ?? 'test-results/browser'), 'registry-race');
    await mkdir(evidence, { recursive: true });
    const checkpoints = [];
    let h, fixture;
    try {
        try {
            h = await start();
            const origin = (await h.mf.ready).origin;
            assert.equal(new URL(origin).hostname, '127.0.0.1');
            fixture = new BrowserFixture(evidence, origin); await fixture.start();
            for (const mobile of [false, true]) {
                const view = mobile ? 'mobile' : 'desktop';
                const page = await fixture.page('race-' + view, '/registry', mobile ? { viewport: { width: 390, height: 844 }, isMobile: true } : {});
                await page.locator('#status').filter({ hasText: 'No current public addresses.' }).waitFor();
                const responses = await directoryResponses(page, origin);
                const click = id => page.locator('#' + id)[mobile ? 'tap' : 'click']();
                try {
                    for (const direction of ['initial', 'next', 'previous']) for (const status of [200, 503]) {
                        let old = await responses.request(() => page.reload({ waitUntil: 'domcontentloaded' }));
                        if (direction !== 'initial') {
                            await responses.respond(old, list('page one', 'second'));
                            old = await responses.request(() => click('next'));
                            if (direction === 'previous') {
                                await responses.respond(old, list('page two', 'third'));
                                old = await responses.request(() => click('previous'));
                            }
                        }
                        const before = await visiblePage(page), count = responses.calls.length;
                        await page.locator('#address').fill('   '); await click('lookup');
                        assert.equal(responses.calls.length, count);
                        assert.deepEqual(await visiblePage(page), before);
                        assert.equal(await page.locator('#status').innerText(), guidance);
                        assert.equal(await page.locator('#previous').isDisabled(), direction !== 'previous');
                        assert.equal(await page.locator('#next').isDisabled(), direction === 'initial');
                        assert.equal(await page.locator('#retry').isDisabled(), false);
                        await responses.respond(old, list('stale response', 'stale cursor'), status, true);
                        assert.equal(responses.calls.length, count);
                        assert.deepEqual(await visiblePage(page), before);
                        assert.equal(await page.locator('#status').innerText(), guidance);
                        if (direction === 'next' && status === 200) await capture(fixture, page, view + '-input-guidance');

                        // Use restored navigation, then explicitly retry a real rendered error.
                        const recovery = await responses.request(() => click(direction === 'initial' ? 'retry' : direction));
                        assert.equal(recovery.path, old.path);
                        await responses.respond(recovery, {}, 503);
                        assert.equal(await page.locator('#status').innerText(), 'Registry unavailable (503).');
                        assert.equal(await page.locator('#retry').isDisabled(), false);
                        assert.equal(await page.locator('#results article').count(), 0);
                        if (direction === 'next' && status === 200) await capture(fixture, page, view + '-error');
                        const retry = await responses.request(() => click('retry'));
                        assert.equal(retry.path, recovery.path);
                        await responses.respond(retry, list(null));
                        assert.equal(await page.locator('#status').innerText(), 'No current public addresses.');
                        assert.equal(await page.locator('#retry').isDisabled(), true);
                        if (direction === 'next' && status === 200) await capture(fixture, page, view + '-empty');

                        await page.locator('#address').fill('12345678-1234-1234-1234-123456789abc');
                        const lookup = await responses.request(() => click('lookup'));
                        assert.equal(lookup.path, '/v1/directory/address?addressId=12345678-1234-1234-1234-123456789abc');
                        await responses.respond(lookup, card('exact result'));
                        assert.equal(await page.locator('#pageNumber').innerText(), 'Exact lookup');
                        const browse = await responses.request(() => click('browse'));
                        assert.equal(browse.path, '/v1/directory?limit=10&q=');
                        await responses.respond(browse, list('fresh page', 'next'));
                        assert.equal(await page.locator('#pageNumber').innerText(), 'Page 1');
                        assert.equal(await page.locator('#previous').isDisabled(), true);
                        assert.equal(await page.locator('#next').isDisabled(), false);
                        checkpoints.push(`${view}: ${direction} / old HTTP ${status}; retained rows, local validation, navigation, retry, exact lookup and browse`);
                    }
                } finally { await responses.close(); }
            }
            assert.deepEqual(fixture.errors, []);
        } finally { try { await fixture?.close(); } finally { await h?.close(); } }
        await writeFile(join(evidence, 'result.json'), JSON.stringify({ status: 'passed', checkpoints }, null, 2));
    } catch (error) {
        await writeFile(join(evidence, 'result.json'), JSON.stringify({ status: 'failed', checkpoints, error: String(error.stack).slice(-32768) }, null, 2));
        throw error;
    }
});
