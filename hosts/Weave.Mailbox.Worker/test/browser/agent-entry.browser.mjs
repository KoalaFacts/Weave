import assert from 'node:assert/strict';
import { test } from 'node:test';
import { mkdir } from 'node:fs/promises';
import { resolve } from 'node:path';
import { start } from '../harness.mjs';
import { BrowserFixture } from './playwright.mjs';

test('real browser creates an inert nonsecret invitation and preserves the run across copies', {timeout:60000},async()=>{
    const h=await start(),evidence=resolve(process.env.WEAVE_BROWSER_EVIDENCE??'test-results/browser','agent-entry');
    await mkdir(evidence,{recursive:true});
    const origin=(await h.mf.ready).origin,fixture=new BrowserFixture(evidence,origin);
    try{
        const account=await h.register(),address=await h.address(account,{label:'Recipient <img src=x onerror=alert(1)>',isPublic:false});
        const other=await h.address(account,{label:'Second recipient address',isPublic:false});
        await fixture.start();const page=await fixture.page('agent-entry');
        assert.equal(await page.locator('#agentStart a[href="/agent-start.md"]').count(),1);
        for(const role of ['A','B'])assert.match(await page.locator('#agentPrompt'+role).inputValue(),/agent-start\.md/);
        await page.locator('#recovery summary').click();await page.locator('#control').fill(account.control);await page.locator('#register').click();
        await page.waitForFunction(()=>document.getElementById('status').textContent.includes('Account connected.'));
        await page.locator('#invitationBuilder summary').click();await page.locator('#loadInvitationAddresses').click();
        await page.locator('#invitationAddress option[value="'+address.addressId+'"]').waitFor({state:'attached'});
        await page.locator('#invitationAddress').selectOption(address.addressId);await page.locator('#buildInvitation').click();
        const original=await page.locator('#invitationCard').inputValue(),card=JSON.parse(original);
        assert.equal(card.peerAccountId,account.agentId);assert.equal(card.peerAddressId,address.addressId);assert.equal(card.origin,origin);assert.ok(!original.includes(account.control));
        assert.equal(await page.locator('#invitationBuilder img').count(),0);
        await page.locator('#buildInvitation').click();assert.equal(await page.locator('#invitationCard').inputValue(),original);
        await page.context().grantPermissions(['clipboard-read','clipboard-write'],{origin});await page.locator('#copyInvitation').click();
        await page.waitForFunction(()=>document.getElementById('invitationStatus').textContent.includes('Contact card copied.'));
        assert.equal(await page.evaluate(()=>navigator.clipboard.readText()),original);
        await page.locator('#invitationAddress').selectOption(other.addressId);
        assert.equal(await page.locator('#invitationCard').inputValue(),'');assert.equal(await page.locator('#copyInvitation').isDisabled(),true);
        await page.locator('#buildInvitation').click();const second=JSON.parse(await page.locator('#invitationCard').inputValue());
        assert.equal(second.peerAddressId,other.addressId);assert.notEqual(second.runId,card.runId);
        await page.locator('#newInvitationRun').click();assert.notEqual(JSON.parse(await page.locator('#invitationCard').inputValue()).runId,second.runId);
        assert.equal((await h.ok('/v1/contacts/requests',account.control)).items.length,0);assert.equal((await h.ok('/v1/outbox',account.control)).items.length,0);
        await fixture.screenshot(page,'contact-card');
        await page.locator('#disconnect').click();assert.equal(await page.locator('#invitationCard').inputValue(),'');assert.equal(await page.locator('#copyInvitation').isDisabled(),true);
        assert.deepEqual(fixture.errors,[]);
    }finally{await fixture.close();await h.close();}
});
