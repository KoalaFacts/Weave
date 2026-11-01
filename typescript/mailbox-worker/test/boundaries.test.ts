import assert from 'node:assert/strict';
import { test } from 'node:test';
import { build } from 'esbuild';
import { start, payload } from './harness.ts';
async function module(path) {
    const built=await build({entryPoints:[path],bundle:true,write:false,format:'esm',platform:'node'});
    return import('data:text/javascript;base64,'+Buffer.from(built.outputFiles[0].text).toString('base64'));
}
test('contact expiry is a read-only 100ns projection and does not expire accepted or rejected decisions',async()=>{
    const {effectiveRequest,ticks}=await module('src/protocol.ts');
    const expiresAt='2026-10-12T12:00:00.0000001Z',now=ticks(expiresAt);
    for(const status of ['pending','needsAction','accepted','rejected']){
        const summary={status,expiresAt};
        assert.equal(effectiveRequest(summary,now-1n).status,status);
        assert.equal(effectiveRequest(summary,now).status,['pending','needsAction'].includes(status)?'expired':status);
        assert.equal(summary.status,status);
    }
});
test('activation predicate accepts exact start, rejects exact end and preserves 100ns timestamp comparisons',async()=>{
    const {eligible}=await module('src/cards.ts'),{ticks}=await module('src/protocol.ts');
    const start=ticks('2026-10-12T12:00:00.0000001Z'),end=start+1n;
    const card={revokedAt:null,expiresAt:null,_effectiveFromTicks:String(start),_effectiveToTicks:String(end)};
    assert.equal(eligible(card,start-1n),false); assert.equal(eligible(card,start),true); assert.equal(eligible(card,end),false);
    assert.equal(ticks('2026-10-12T12:00:00.0000002Z')-start,1n);
    assert.throws(()=>ticks('2026-02-30T00:00:00Z'));
});
test('replacement of a rejected contact frees its pending slot atomically at the 64-message boundary',async()=>{
    const h=await start();
    try {
        const a=await h.participant(),b=await h.participant(),x=await h.participant(),p=payload();
        // 62 sends in one hour exceed the unclaimed-agent limit of 30; the per-recipient pending boundary is under test.
        await h.claim(a);
        await h.ok('/v1/contacts/requests',a,'POST',{requestId:p.messageId,cardId:b.cardId,methodId:'relay',payload:p});
        await h.ok('/v1/contacts/requests/'+p.messageId+'/decision',b,'POST',{requesterMailboxId:a.accountId,expectedGeneration:1,status:'accepted',reply:null});
        for(let i=0;i<62;i++) await h.ok('/v1/messages',a,'POST',{version:1,recipientMailboxId:b.accountId,contactGeneration:1,payload:payload('message '+i)});
        const first=payload();
        await h.ok('/v1/contacts/requests',x,'POST',{requestId:first.messageId,cardId:b.cardId,methodId:'relay',payload:first});
        await h.ok('/v1/contacts/requests/'+first.messageId+'/decision',b,'POST',{requesterMailboxId:x.accountId,expectedGeneration:1,status:'rejected',reply:null});
        const overflow={version:1,recipientMailboxId:b.accountId,contactGeneration:1,payload:payload('overflow')};
        assert.equal((await h.request('/v1/messages',a,'POST',overflow)).status,429);
        const second=payload();
        const replaced=await h.ok('/v1/contacts/requests',x,'POST',{requestId:second.messageId,cardId:b.cardId,methodId:'relay',payload:second});
        assert.equal(replaced.generation,2);
        assert.equal((await h.ok('/v1/outbox/'+first.messageId,x)).state,'blocked');
        assert.equal((await h.ok('/v1/outbox/'+second.messageId,x)).state,'pending');
        assert.equal((await h.request('/v1/outbox/'+overflow.payload.messageId,a)).status,404);
    } finally {await h.close();}
});
test('request replay after block reports channel transition time, while card ownership errors remain uniform',async()=>{
    const h=await start();
    try {
        const a=await h.participant(),b=await h.participant(),p=payload();
        const submission={requestId:p.messageId,cardId:b.cardId,methodId:'relay',payload:p};
        const pending=await h.ok('/v1/contacts/requests',a,'POST',submission);
        await h.ok('/v1/contacts/blocks',b,'PUT',{peerMailboxId:a.accountId,expectedGeneration:1});
        const replay=await h.ok('/v1/contacts/requests',a,'POST',submission);
        assert.equal(replay.generation,2); assert.ok(Date.parse(replay.updatedAt)>=Date.parse(pending.updatedAt));
        const c=await h.ok('/v1/contacts/card?cardId='+b.cardId,b); const {ownerMailboxId,ownerAccountId,...input}=c;
        assert.equal((await h.request('/v1/contacts/cards',a,'PUT',input)).status,404);
        assert.equal((await h.request('/v1/contacts/cards',b,'PUT',input)).status,409);
    } finally {await h.close();}
});
