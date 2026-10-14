import assert from 'node:assert/strict';
import { test } from 'node:test';
import { readFile } from 'node:fs/promises';
import { build } from 'esbuild';
import { Miniflare, convertV4MiniflareOptions } from 'miniflare';
import { start, payload } from './harness.mjs';
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
test('node crypto scheme matches C# test vectors in real workerd and unknown schemes fail closed',async()=>{
    const h=await start();
    try {
        const vectors=JSON.parse(await readFile('test/control-vectors.json','utf8'));
        for(const {control,digest} of vectors.filter(v=>/^[\x20-\x7e]+$/.test(v.control))) {
            const a=await h.register({},control);
            const row=await h.db.prepare("SELECT data FROM mailbox_records WHERE kind='account' AND id=?").bind(a.agentId).first();
            const record=JSON.parse(row.data);
            assert.equal(record.controlHashScheme,'sha256-utf8-v1'); assert.equal(record.controlHash,digest);
            assert.deepEqual(record.controlHashParameters,{algorithm:'SHA-256',inputEncoding:'utf-8',digestEncoding:'hex-lower',normalization:'none',salt:null,rounds:1});
            assert.equal((await h.request('/v1/inbox',control)).status,200);
            const wrongParameters={...record,controlHashParameters:{...record.controlHashParameters,rounds:2}};
            await h.db.prepare("UPDATE mailbox_records SET data=? WHERE kind='account' AND id=?").bind(JSON.stringify(wrongParameters),a.agentId).run();
            assert.equal((await h.request('/v1/inbox',control)).status,401);
            assert.equal((await h.request('/v1/accounts',control,'POST',{})).status,401);
            record.controlHashScheme='unknown';
            await h.db.prepare("UPDATE mailbox_records SET data=? WHERE kind='account' AND id=?").bind(JSON.stringify(record),a.agentId).run();
            assert.equal((await h.request('/v1/inbox',control)).status,401);
            assert.equal((await h.request('/v1/accounts',control,'POST',{})).status,401);
            assert.equal(await h.db.prepare("SELECT count(*) n FROM mailbox_records WHERE kind='account' AND id=?").bind(a.agentId).first('n'),1);
        }
        const bundled=await build({stdin:{contents:"import {controlDigest} from './src/credentials.ts'; export default {async fetch(r) {return Response.json(await controlDigest(await r.json()));}}",resolveDir:process.cwd()},bundle:true,write:false,format:'esm',platform:'browser',external:['node:crypto']});
        const worker=new Miniflare(convertV4MiniflareOptions({modules:true,script:bundled.outputFiles[0].text,compatibilityDate:'2026-07-01',compatibilityFlags:['nodejs_compat']}));
        try {for(const {control,digest} of vectors) assert.equal(await (await worker.dispatchFetch('http://localhost/',{method:'POST',body:JSON.stringify(control)})).json(),digest);} finally {await worker.dispose();}
        const {digestMatches}=await module('src/credentials.ts');
        assert.equal(digestMatches('00'.repeat(32),'00'.repeat(32)),true);
        for(const malformed of ['','ab','g'.repeat(64),null,{}]) assert.equal(digestMatches('00'.repeat(32),malformed),false);
        assert.equal(digestMatches('00'.repeat(32),'01'.repeat(32)),false);
    } finally {await h.close();}
});
test('replacement of a rejected contact frees its pending slot atomically at the 64-message boundary',async()=>{
    const h=await start();
    try {
        const a=await h.participant(),b=await h.participant(),x=await h.participant(),p=payload();
        await h.ok('/v1/contacts/requests',a.control,'POST',{requestId:p.messageId,cardId:b.cardId,methodId:'relay',payload:p});
        await h.ok('/v1/contacts/requests/'+p.messageId+'/decision',b.control,'POST',{requesterMailboxId:a.accountId,expectedGeneration:1,status:'accepted',reply:null});
        for(let i=0;i<62;i++) await h.ok('/v1/messages',a.control,'POST',{version:1,recipientMailboxId:b.accountId,contactGeneration:1,payload:payload('message '+i)});
        const first=payload();
        await h.ok('/v1/contacts/requests',x.control,'POST',{requestId:first.messageId,cardId:b.cardId,methodId:'relay',payload:first});
        await h.ok('/v1/contacts/requests/'+first.messageId+'/decision',b.control,'POST',{requesterMailboxId:x.accountId,expectedGeneration:1,status:'rejected',reply:null});
        const overflow={version:1,recipientMailboxId:b.accountId,contactGeneration:1,payload:payload('overflow')};
        assert.equal((await h.request('/v1/messages',a.control,'POST',overflow)).status,429);
        const second=payload();
        const replaced=await h.ok('/v1/contacts/requests',x.control,'POST',{requestId:second.messageId,cardId:b.cardId,methodId:'relay',payload:second});
        assert.equal(replaced.generation,2);
        assert.equal((await h.ok('/v1/outbox/'+first.messageId,x.control)).state,'blocked');
        assert.equal((await h.ok('/v1/outbox/'+second.messageId,x.control)).state,'pending');
        assert.equal((await h.request('/v1/outbox/'+overflow.payload.messageId,a.control)).status,404);
    } finally {await h.close();}
});
test('request replay after block reports channel transition time, while card ownership errors remain uniform',async()=>{
    const h=await start();
    try {
        const a=await h.participant(),b=await h.participant(),p=payload();
        const submission={requestId:p.messageId,cardId:b.cardId,methodId:'relay',payload:p};
        const pending=await h.ok('/v1/contacts/requests',a.control,'POST',submission);
        await h.ok('/v1/contacts/blocks',b.control,'PUT',{peerMailboxId:a.accountId,expectedGeneration:1});
        const replay=await h.ok('/v1/contacts/requests',a.control,'POST',submission);
        assert.equal(replay.generation,2); assert.ok(Date.parse(replay.updatedAt)>=Date.parse(pending.updatedAt));
        const c=await h.ok('/v1/contacts/card?cardId='+b.cardId,b.control); const {ownerMailboxId,...input}=c;
        assert.equal((await h.request('/v1/contacts/cards',a.control,'PUT',input)).status,404);
        assert.equal((await h.request('/v1/contacts/cards',b.control,'PUT',input)).status,409);
    } finally {await h.close();}
});
