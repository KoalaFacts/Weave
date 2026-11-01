import assert from 'node:assert/strict';
import { test } from 'node:test';
import { mkdir, writeFile } from 'node:fs/promises';
import { join, resolve } from 'node:path';
import { start } from '../harness.ts';
import { BrowserFixture } from './playwright.ts';

async function rows(page, expected) {
    await page.waitForFunction(ids => {
        const actual = [...document.querySelectorAll('#results article p:first-of-type')].map(p => p.textContent);
        return JSON.stringify(actual) === JSON.stringify(ids.map(id => 'Address ID: ' + id));
    }, expected);
    assert.deepEqual(await page.locator('#results article p:first-of-type').allTextContents(), expected.map(id => 'Address ID: ' + id));
}

async function capture(fixture, page, name) {
    const layout = await page.evaluate(() => ({
        width: innerWidth, documentWidth: document.documentElement.scrollWidth,
        clipped: [...document.querySelectorAll('input,button,article')].filter(element => {
            const bounds = element.getBoundingClientRect(); return bounds.width && (bounds.left < 0 || bounds.right > innerWidth + 1);
        }).map(element => element.id || element.tagName)
    }));
    assert.ok(layout.documentWidth <= layout.width + 1, JSON.stringify(layout));
    assert.deepEqual(layout.clipped, []);
    await fixture.screenshot(page, name);
}

test('public registry browser: desktop and touch browse ten-item pages, rate denial and recovery', { timeout: 180000 }, async () => {
    const evidence = join(resolve(process.env.WEAVE_BROWSER_EVIDENCE ?? 'test-results/browser'), 'registry');
    await mkdir(evidence, { recursive: true });
    const report = join(evidence, 'registry-pagination-result.json'), checkpoints = [];
    await writeFile(report, JSON.stringify({ status: 'running', checkpoints }));
    let h, fixture;
    try {
        try {
            h = await start();
            const origin = (await h.mf.ready).origin;
            assert.equal(new URL(origin).hostname, '127.0.0.1');
            fixture = new BrowserFixture(evidence, origin); await fixture.start();
            const views = [
                { name: 'desktop', page: await fixture.page('registry-pagination-desktop', '/registry'), mobile: false },
                { name: 'mobile', page: await fixture.page('registry-pagination-mobile', '/registry', { viewport: { width: 390, height: 844 }, isMobile: true }), mobile: true }
            ];
            for (const view of views) {
                await view.page.locator('#status').filter({ hasText: 'No current public addresses.' }).waitFor();
                assert.equal(await view.page.locator('#next').isDisabled(), true);
                await capture(fixture, view.page, 'registry-pagination-empty-' + view.name);
            }
            const owner = await h.register({ displayName: 'PRIVATE-REGISTRY-PROFILE' }), visible = [];
            for (let index = 0; index < 23; index++) visible.push(await h.address(owner, { label: 'Browse address ' + String(index + 1).padStart(2, '0'), isPublic: true }));
            for (let index=0;index<visible.length;index++) await h.db.prepare("UPDATE account_addresses SET created_at=? WHERE address_id=?").bind((BigInt(Date.UTC(2025,0,1,0,0,index))*10000n).toString(),visible[index].addressId).run();
            for (const profile of [{ label: 'PRIVATE-ADDRESS' }, { label: 'FUTURE-ADDRESS', isPublic: true, effectiveFrom: '2099-01-01T00:00:00Z' }, { label: 'EXPIRED-ADDRESS', isPublic: true, effectiveTo: '2020-01-01T00:00:00Z' }]) await h.address(owner, profile);
            const expected = visible.map(item => item.addressId).reverse();
            for (const view of views) {
                await view.page.locator('#find')[view.mobile ? 'tap' : 'click']();
                await rows(view.page, expected.slice(0, 10));
                const requests = fixture.trace.length;
                await view.page.locator('#address').fill('not-an-address');
                await view.page.locator('#lookup')[view.mobile ? 'tap' : 'click']();
                await view.page.locator('#status').filter({ hasText: 'Enter a valid public address ID (lowercase UUID), or use Browse all.' }).waitFor();
                assert.equal(fixture.trace.length, requests, 'Invalid input must not request the registry');
                await rows(view.page, expected.slice(0, 10));
                assert.equal(await view.page.locator('#pageNumber').textContent(), 'Page 1');
                assert.equal(await view.page.locator('#retry').isDisabled(), true);
                await capture(fixture, view.page, 'registry-invalid-address-' + view.name);
                await view.page.locator('#address').fill('00000000-0000-0000-0000-000000000000');
                await view.page.locator('#lookup')[view.mobile ? 'tap' : 'click']();
                await view.page.locator('#status').filter({ hasText: 'Public address unavailable.' }).waitFor();
                assert.equal(await view.page.locator('#results article').count(), 0);
                assert.equal(await view.page.locator('#pageNumber').textContent(), 'Exact lookup');
                assert.equal(await view.page.locator('#retry').isDisabled(), true);
                await view.page.locator('#address').fill(expected[0]);
                await view.page.locator('#lookup')[view.mobile ? 'tap' : 'click']();
                await rows(view.page, [expected[0]]);
                assert.equal(await view.page.locator('#pageNumber').textContent(), 'Exact lookup');
                await view.page.locator('#browse')[view.mobile ? 'tap' : 'click']();
                await rows(view.page, expected.slice(0, 10));
                assert.equal(await view.page.locator('#address').inputValue(), '');
                checkpoints.push(view.name + ': invalid input makes no request or retry; valid unavailable address is distinct; corrected lookup and Browse all recover');
                for (let index = 0; index < 3; index++) {
                    await rows(view.page, expected.slice(index * 10, (index + 1) * 10));
                    const text = await view.page.locator('body').innerText();
                    for (const hidden of ['PRIVATE-', 'FUTURE-ADDRESS', 'EXPIRED-ADDRESS', await h.token(owner), owner.key.privateJwk.d!]) assert.ok(!text.includes(hidden));
                    assert.ok(text.includes('10 addresses per page.'));
                    assert.equal(await view.page.locator('#next').isDisabled(), index === 2);
                    await capture(fixture, view.page, 'registry-pagination-' + view.name + '-page-' + (index + 1));
                    if (index < 2) await view.page.locator('#next')[view.mobile ? 'tap' : 'click']();
                }
                await view.page.locator('#previous')[view.mobile ? 'tap' : 'click']();
                await rows(view.page, expected.slice(10,20));
                assert.equal(await view.page.locator('#pageNumber').textContent(),'Page 2');
                await view.page.locator('#browse')[view.mobile ? 'tap' : 'click']();
                await rows(view.page, expected.slice(0,10));
                assert.equal(await view.page.locator('#pageNumber').textContent(),'Page 1');
                assert.equal(await view.page.locator('#previous').isDisabled(),true);
                checkpoints.push(view.name + ': anonymous 10/10/3 creation-ordered pages; previous and browse-all return to the expected pages; no private/inactive rows');
            }
            await h.db.prepare("UPDATE rate_limits SET count=2000,window_start=CAST(? AS INTEGER)*10000 WHERE policy='public-read' AND bucket_key='global'").bind(Date.now()).run();
            for (const view of views) {
                await view.page.locator('#find')[view.mobile ? 'tap' : 'click']();
                await view.page.locator('#status').filter({ hasText: /^Too many registry requests\. Try again in \d+ seconds\.$/ }).waitFor();
                assert.equal(await view.page.locator('#results article').count(), 0);
                assert.equal(await view.page.locator('#next').isDisabled(), true);
                await capture(fixture, view.page, 'registry-pagination-rate-' + view.name);
            }
            await h.db.prepare("UPDATE rate_limits SET window_start=CAST(? AS INTEGER)*10000 WHERE policy='public-read'").bind(Date.now() - 61000).run();
            for (const view of views) {
                await view.page.locator('#find')[view.mobile ? 'tap' : 'click'](); await rows(view.page, expected.slice(0, 10));
                assert.equal(await view.page.locator('#next').isDisabled(), false);
                await view.page.getByRole('link', { name: 'Account & mailbox demo', exact: true })[view.mobile ? 'tap' : 'click']();
                await view.page.waitForURL(origin + '/'); assert.equal(await view.page.locator('#agentKey').inputValue(), '');
            }
            checkpoints.push('Actual 429 Retry-After shown on desktop/mobile, stale rows cleared, later request recovers, account navigation stays disconnected');
            assert.deepEqual(fixture.errors, []);
        } finally { try { await fixture?.close(); } finally { await h?.close(); } }
        await writeFile(report, JSON.stringify({ status: 'passed', checkpoints }, null, 2));
    } catch (error) {
        await writeFile(report, JSON.stringify({ status: 'failed', checkpoints, error: String(error.stack).slice(-32768) }, null, 2));
        throw error;
    }
});
