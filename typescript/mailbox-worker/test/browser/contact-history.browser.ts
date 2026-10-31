import assert from 'node:assert/strict';
import { test } from 'node:test';
import { mkdir, writeFile } from 'node:fs/promises';
import { join, resolve } from 'node:path';
import { start, payload, keyFile } from '../harness.ts';
import { seedRequest } from '../storage-fixtures.ts';
import { BrowserFixture } from './playwright.ts';

test('contact history remains visible on desktop and native mobile after introduction expiry', { timeout:60000 }, async () => {
    const evidence = resolve(process.env.WEAVE_BROWSER_EVIDENCE ?? 'test-results/browser', 'contact-history');
    await mkdir(evidence, { recursive:true });
    let h, fixture;
    try {
        h = await start({ bindings:{ CLEANUP_MAX_MESSAGES:'0', CLEANUP_TIME_BUDGET_MS:'0' } });
        fixture = new BrowserFixture(evidence, (await h.mf.ready).origin);
        const a = await h.register(), b = await h.register();
        for (const [index,status] of ['pending','accepted','rejected'].entries()) {
            const p = payload('synthetic history',Date.now() - 9 * 86400000 + index);
            const summary = { requestId:p.messageId,cardId:'synthetic-history-card',requesterMailboxId:a.accountId,
                recipientMailboxId:b.accountId,methodId:'relay',generation:1,status,createdAt:p.createdAt,
                expiresAt:p.expiresAt,requestMessageId:p.messageId,replyMessageId:null };
            await seedRequest(h.db,{summary});
        }
        await fixture.start();
        for (const mobile of [false,true]) {
            const label = mobile ? 'mobile' : 'desktop', viewport = mobile ? {width:390,height:844} : {width:1440,height:1000};
            const page = await fixture.page(label,'/',{viewport,isMobile:mobile});
            await page.locator('#recovery summary').click();
            await page.locator('#agentKey').fill(keyFile(b));
            await page.locator('#register').click();
            await page.waitForFunction(() => document.getElementById('identity').textContent.startsWith('Account ID:'));
            await page.locator('#contactScenario summary').click();
            if (mobile) await page.locator('#loadContacts').tap(); else await page.locator('#loadContacts').click();
            await page.waitForFunction(() => document.querySelectorAll('#contacts .row').length === 3
                && !document.getElementById('contacts').textContent.includes('Loading introduction'));
            assert.deepEqual(await page.locator('#contacts .row > strong:first-child').allTextContents(),
                ['expired · generation 1','accepted · generation 1','rejected · generation 1']);
            assert.match(await page.locator('#contacts').textContent(),/This request stays in your history/);
            assert.equal(await page.getByRole('button',{name:'Accept contact',exact:true}).count(),0);
            assert.equal(await page.getByRole('button',{name:'Reject contact',exact:true}).count(),0);
            assert.equal(await page.locator('#contactsNext').isEnabled(),false);
            const layout = await page.evaluate(() => ({width:innerWidth,documentWidth:document.documentElement.scrollWidth,touchPoints:navigator.maxTouchPoints}));
            assert.equal(layout.width,viewport.width);
            assert.ok(layout.documentWidth <= viewport.width);
            if (mobile) assert.ok(layout.touchPoints > 0);
            await writeFile(join(evidence,label+'-layout.json'),JSON.stringify(layout,null,2));
            await fixture.screenshot(page,'contact-history-'+label);
        }
        assert.deepEqual(fixture.errors,[]);
        assert.equal(fixture.trace.some(r=>r.method !== 'GET' && !['/v1/accounts','/v1/token'].includes(r.path)),false);
        assert.equal(await h.db.prepare("SELECT count(*) n FROM contact_requests").first('n'),3);
        await writeFile(join(evidence,'result.json'),JSON.stringify({status:'passed',statuses:['expired','accepted','rejected'],requestRows:3,decisions:0,acknowledgments:0},null,2));
    } catch (error) {
        await writeFile(join(evidence,'result.json'),JSON.stringify({status:'failed',error:String(error.stack).slice(-32768)},null,2));
        throw error;
    } finally { try { await fixture?.close(); } finally { await h?.close(); } }
});
