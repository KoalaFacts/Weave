import assert from 'node:assert/strict';
import { test } from 'node:test';
import { mkdir,writeFile } from 'node:fs/promises';
import { resolve,join } from 'node:path';
import { start,payload } from '../harness.mjs';
import { BrowserFixture } from './playwright.mjs';

test('real account UI retains revoked addresses and request history after owner-only contact removal',{timeout:90000},async()=>{
    const evidence=resolve(process.env.WEAVE_BROWSER_EVIDENCE??'test-results/browser','account-state');await mkdir(evidence,{recursive:true});
    const h=await start(),fixture=new BrowserFixture(evidence,(await h.mf.ready).origin);
    try{
        const a=await h.participant(),b=await h.participant(),intro=payload('reviewed introduction');
        const r=await h.ok('/v1/contacts/requests',a.control,'POST',{requestVersion:2,requestId:intro.messageId,requesterAccountId:a.accountId,requesterAddressId:a.cardId,recipientAccountId:b.accountId,recipientAddressId:b.cardId,payload:intro});
        const decision={requesterAccountId:a.accountId,expectedGeneration:r.generation,status:'accepted',reply:null};await h.ok('/v1/contacts/requests/'+intro.messageId+'/decision',b.control,'POST',decision);
        await fixture.start();const page=await fixture.page('owner');
        await page.locator('#recovery summary').click();await page.locator('#control').fill(a.control);await page.locator('#register').click();
        await page.waitForFunction(()=>document.getElementById('status').textContent.includes('Account connected.'));
        await page.locator('#contactScenario > summary').click();await page.locator('#loadOwnedContacts').click();
        await page.locator('#ownedContacts').getByRole('button',{name:'Remove contact',exact:true}).click();
        await page.waitForFunction(()=>document.getElementById('ownedContacts').textContent.includes('No saved contacts yet.'));
        assert.equal((await h.ok('/v1/contacts',b.control)).items.length,1);assert.equal((await h.ok('/v1/contacts',b.control)).items[0].canSend,false);
        await page.locator('#loadContacts').click();await page.waitForFunction(()=>document.getElementById('contacts').textContent.includes('accepted'));
        assert.equal((await h.request('/v1/contacts/requests/'+intro.messageId+'/decision',b.control,'POST',decision)).status,409);
        await fixture.screenshot(page,'removed-contact-retained-history');
        await page.locator('#addressScenario > summary').click();await page.locator('#addresses').getByRole('button',{name:'Revoke address',exact:true}).click();
        await page.waitForFunction(()=>document.getElementById('addresses').textContent.includes('This ID remains reserved.'));
        assert.equal(await page.locator('#addresses').getByRole('button',{name:'Edit',exact:true}).count(),0);
        assert.equal(await page.locator('#requesterAddressId option').count(),1);
        assert.equal((await h.ok('/v1/addresses?includeRevoked=true',a.control)).items[0].addressId,a.cardId);
        await fixture.screenshot(page,'revoked-address-desktop');
        await page.locator('#discoverScenario > summary').click();await page.locator('#recipientAccountId').fill(b.accountId);await page.locator('#addressId').fill(b.cardId);await page.locator('#requestContact').click();
        await page.waitForFunction(()=>document.getElementById('status').textContent.includes('Create an address for this account'));
        assert.equal((await h.ok('/v1/contacts/requests',a.control)).items.length,1);
        const mobile=await fixture.page('mobile','/',{viewport:{width:390,height:844},isMobile:true});await mobile.locator('#recovery summary').tap();await mobile.locator('#control').fill(a.control);await mobile.locator('#register').tap();
        await mobile.waitForFunction(()=>document.getElementById('status').textContent.includes('Account connected.'));await mobile.locator('#addressScenario > summary').tap();
        const layout=await mobile.evaluate(()=>({width:innerWidth,documentWidth:document.documentElement.scrollWidth,touch:navigator.maxTouchPoints}));assert.equal(layout.width,390);assert.equal(layout.documentWidth,390);assert.ok(layout.touch>0);
        await fixture.screenshot(mobile,'revoked-address-mobile');
        assert.deepEqual(fixture.errors,[]);await writeFile(join(evidence,'result.json'),JSON.stringify({status:'passed',retainedRequests:1,retainedOtherContact:1,revokedAddressReserved:true,missingSourceDispatches:0},null,2));
    }finally{try{await fixture.close();}finally{await h.close();}}
});
