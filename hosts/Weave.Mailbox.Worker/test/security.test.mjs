import assert from 'node:assert/strict';
import { test } from 'node:test';
import { start, payload, secret } from './harness.mjs';
async function firstEvent(response){
    const reader=response.body.getReader();let text='';
    try{for(let i=0;i<12;i++){const part=await reader.read();if(part.done)break;text+=new TextDecoder().decode(part.value);const match=/data: (.+)\n\n/.exec(text);if(match)return JSON.parse(match[1]);}assert.fail('Expected an owned SSE message');}
    finally{await reader.cancel();}
}
test('bidirectional HTTP IDOR matrix binds addresses, aliases, receipts, ACK and SSE to authenticated owner',async()=>{
    const h=await start();
    try{
        const a=await h.participant(),b=await h.participant(),intro=payload('private-introduction');
        await h.ok('/v1/contacts/requests',a.control,'POST',{requestId:intro.messageId,cardId:b.cardId,methodId:'relay',payload:intro});
        await h.ok('/v1/contacts/requests/'+intro.messageId+'/decision',b.control,'POST',{requesterMailboxId:a.agentId,expectedGeneration:1,status:'accepted',reply:null});
        await h.ok('/v1/inbox/'+intro.messageId+'/ack',b.control,'POST',{senderMailboxId:a.agentId});
        const messages=[];for(const [sender,recipient] of [[a,b],[b,a]]){const p=payload('private-'+sender.agentId);await h.ok('/v1/messages',sender.control,'POST',{version:1,recipientMailboxId:recipient.agentId,contactGeneration:1,payload:p});messages.push(p);}
        for(const [owner,other,ownSent,received] of [[a,b,messages[0],messages[1]],[b,a,messages[1],messages[0]]]){
            const protectedRoutes=[['GET','/v1/addresses'],['GET','/v1/addresses/'+other.cardId],['PUT','/v1/addresses/'+other.cardId,{}],['DELETE','/v1/addresses/'+other.cardId],['GET','/inbox/'+other.agentId],['GET','/outbox/'+other.agentId],['GET','/v1/outbox/'+received.messageId],['POST','/v1/inbox/'+ownSent.messageId+'/ack',{senderMailboxId:owner.agentId}],['GET','/v1/inbox/events']];
            for(const [method,path,body] of protectedRoutes)for(const credential of [undefined,secret(),'short'])assert.equal((await h.request(path,credential,method,body)).status,401,method+' '+path);
            for(const [method,path,body] of protectedRoutes.slice(1,-1))assert.equal((await h.request(path,owner.control,method,body)).status,404,'foreign object '+path);
            assert.equal((await h.ok('/v1/addresses/'+owner.cardId,owner.control)).accountId,owner.agentId);
            assert.equal((await h.ok('/inbox/'+owner.agentId,owner.control)).items[0].envelope.payload.messageId,received.messageId);
            assert.equal((await h.ok('/v1/outbox/'+ownSent.messageId,owner.control)).senderMailboxId,owner.agentId);
            const event=await firstEvent(await h.request('/v1/inbox/events?accountId='+other.agentId,owner.control));assert.equal(event.envelope.payload.messageId,received.messageId);
            const foreignCursor='inbox:'+btoa(other.agentId)+':0';assert.equal((await h.request('/v1/inbox?afterCursor='+encodeURIComponent(foreignCursor),owner.control)).status,400);
            assert.equal((await h.request('/v1/inbox?control='+encodeURIComponent(owner.control))).status,401);
        }
        // Only after both negative-direction assertions, perform authorized ACK/update/delete.
        for(const [owner,other,received] of [[a,b,messages[1]],[b,a,messages[0]]]){
            assert.equal((await h.ok('/v1/inbox/'+received.messageId+'/ack',owner.control,'POST',{senderMailboxId:other.agentId})).state,'acknowledged');
            assert.equal((await h.ok('/v1/addresses/'+owner.cardId,owner.control,'PUT',{label:'owner update'})).label,'owner update');
            await h.ok('/v1/addresses/'+owner.cardId,owner.control,'DELETE');assert.equal((await h.request('/v1/addresses/'+owner.cardId,owner.control)).status,404);
        }
    }finally{await h.close();}
});
test('HTTP input, SQL, error, cache and cross-origin checks fail closed without leaking credentials or internals',async()=>{
    const h=await start();
    try{
        const a=await h.register();await h.address(a,{label:'Visible',isPublic:true});
        assert.deepEqual((await h.ok('/v1/directory?q='+encodeURIComponent("' OR 1=1 --"))).items,[]);
        for(const path of ['/v1/directory?limit=11','/v1/directory?limit=1&limit=2','/v1/directory?q='+'x'.repeat(129)])assert.equal((await h.request(path)).status,400);
        const cases=[['not-json',400],['{"label":'+('['.repeat(18))+'0'+(']'.repeat(18))+'}',400],[JSON.stringify({label:'x'.repeat(131073)}),413],[JSON.stringify({directUri:'javascript:alert(1)'}),400],[JSON.stringify({label:'bad\u0000label'}),400]];
        for(const [body,status] of cases){const r=await h.request('/v1/addresses',a.control,'POST',body);assert.equal(r.status,status);const text=await r.text();assert.ok(!text.includes(a.control));assert.ok(!/stack|SELECT|mailbox_records|node_modules/.test(text));assert.equal(r.headers.get('cache-control'),'no-store');}
        for(const method of ['OPTIONS','POST']){
            const response=await h.mf.dispatchFetch('http://localhost/v1/addresses',{method,headers:{Origin:'https://attacker.test','Content-Type':'text/plain',Cookie:'control='+a.control,'Access-Control-Request-Headers':'X-Weave-Mailbox-Control'},...(method==='POST'?{body:'{}'}:{})});
            assert.equal(response.status,401);assert.equal(response.headers.get('access-control-allow-origin'),null);assert.equal(response.headers.get('set-cookie'),null);
        }
        const page=await h.request('/registry');assert.ok(page.headers.get('content-security-policy').includes("frame-ancestors 'none'"));assert.equal(page.headers.get('referrer-policy'),'no-referrer');assert.equal(page.headers.get('x-content-type-options'),'nosniff');
    }finally{await h.close();}
});
test('public registry enforces shared source/global request rates and does not store raw IP addresses',async()=>{
    const h=await start();
    try{
        const request=ip=>h.mf.dispatchFetch('http://localhost/v1/directory',{headers:{'CF-Connecting-IP':ip}});
        for(let i=0;i<120;i++)assert.equal((await request('192.0.2.10')).status,200);
        assert.equal((await request('192.0.2.10')).status,429);
        assert.equal((await request('192.0.2.11')).status,200);
        const rows=await h.db.prepare("SELECT data,id FROM mailbox_records WHERE kind='public-rate'").all();assert.ok(!JSON.stringify(rows.results).includes('192.0.2.'));
        await h.db.prepare("UPDATE mailbox_records SET data=json_set(data,'$.count',2000) WHERE kind='public-rate' AND id='global'").run();
        assert.equal((await request('192.0.2.12')).status,429);
        assert.equal((await h.request('/v1/contacts/cards')).status,429);
    }finally{await h.close();}
});
